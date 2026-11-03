using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Dbe;

sealed class Worker : IDisposable
{
    private readonly WorkerConfig config;
    private readonly Compiler compiler;
    private readonly X509Certificate2 certificate;
    private readonly string state;
    private readonly FileStream ownership;
    private readonly SemaphoreSlim slots;
    private readonly SemaphoreSlim connections = new(8);
    private readonly SemaphoreSlim headerReaders = new(2);
    private readonly ConcurrentDictionary<int, Task> active = new();
    private readonly TcpListener listener;
    private readonly CancellationTokenSource shutdown = new();
    private int sequence, poisoned;
    private int disposed;
    private readonly NativeInputCache inputCache;
    private readonly RuntimeInputs runtimeInputs;
    private readonly NativeCompilationCache nativeCache;
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    internal Capabilities Capabilities => new(Environment.MachineName, compiler.Version, compiler.Identity,
        config.Slots, config.MemoryMiB, config.TimeoutSeconds);

    internal Worker(WorkerConfig config, string root)
    {
        if (config.Slots is < 1 or > 2 || config.MemoryMiB is < 256 or > 4096 || config.TimeoutSeconds is < 1 or > 120 ||
            config.Port is < 0 or > 65535 || !Wire.ValidHash(config.AllowedClientPin)) throw new Rejected("Invalid Worker limits/config");
        this.config = config;
        state = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, config.StateDirectory));
        Wire.NoReparse(state);
        Directory.CreateDirectory(state);
        // A state folder has a single live owner; crash leftovers are retained, not guessed safe to delete.
        ownership = new FileStream(System.IO.Path.Combine(state, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (Directory.EnumerateDirectories(state, "attempt-*").Any())
                throw new Rejected("Retained attempt directories need inspection before restart: " + state);
            string existingContexts = System.IO.Path.Combine(state, "ctx");
            if (Directory.Exists(existingContexts) && Directory.EnumerateFileSystemEntries(existingContexts).Any())
                throw new Rejected("Retained native contexts need inspection before restart: " + existingContexts);
            compiler = new Compiler(System.IO.Path.Combine(root, config.Compiler));
            try { certificate = Identity.Load(System.IO.Path.Combine(root, config.Certificate)); }
            catch { compiler.Dispose(); throw; }
        }
        catch { ownership.Dispose(); throw; }
        slots = new SemaphoreSlim(config.Slots);
        inputCache = new NativeInputCache(System.IO.Path.Combine(state, "native-cache"));
        runtimeInputs = new RuntimeInputs(compiler.Path, System.IO.Path.Combine(state, "runtime-cache"));
        string contexts = System.IO.Path.Combine(state, "ctx");
        nativeCache = new NativeCompilationCache(contexts, inputCache, runtimeInputs, compiler);
        listener = new TcpListener(IPAddress.Parse(config.BindAddress), config.Port);
    }

    internal void Start() { listener.Start(8); Event("listening", "", $"{config.BindAddress}:{Port}"); }
    internal void Stop() { shutdown.Cancel(); listener.Stop(); }
    internal async Task Serve()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(shutdown.Token);
                tcp.NoDelay = true;
                if (!connections.Wait(0)) { tcp.Dispose(); continue; }
                int id = Interlocked.Increment(ref sequence);
                Task task = Handle(tcp);
                active[id] = task;
                _ = task.ContinueWith(_ => { active.TryRemove(id, out var ignored); }, TaskScheduler.Default);
            }
        }
        catch (Exception e) when (shutdown.IsCancellationRequested && e is OperationCanceledException or SocketException) { }
        finally { await Task.WhenAll(active.Values); }
    }

    private async Task Handle(TcpClient tcp)
    {
        bool acquired = false, headerSent = false;
        string? workspace = null;
        string attempt = "";
        using (tcp)
        using (var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => Identity.Matches(cert, config.AllowedClientPin)))
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
        {
            lifetime.CancelAfter(TimeSpan.FromSeconds(180));
            try
            {
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(10));
                    await tls.AuthenticateAsServerAsync(Identity.Server(certificate), handshake.Token);
                }
                Request request;
                NativeInvocation? invocation = null;
                bool reference = false;
                await headerReaders.WaitAsync(lifetime.Token);
                try
                {
                    request = await Wire.Read<Request>(tls, lifetime.Token);
                    if (request.Version == 6 && request.Operation == "compile")
                    {
                        reference = request.Files is null;
                        invocation = await nativeCache.Resolve(request, lifetime.Token);
                        if (invocation is not null) request = invocation.Request;
                    }
                    else Wire.Validate(request);
                }
                finally { headerReaders.Release(); }
                attempt = request.Operation == "compile" ? request.Attempt : "";
                if (request.Version == 6 && request.Operation == "compile" && invocation is null)
                { await Wire.Write(tls, new Reply("context-miss", attempt), lifetime.Token); return; }
                if (request.Operation == "info")
                {
                    await Wire.Write(tls, new Reply(Volatile.Read(ref poisoned) == 0 ? "ready" : "unhealthy",
                        Capabilities: request.Version >= 5 ? Capabilities with { MaxProtocol = request.Version == 6 ? 6 : 5, DeferredInputs = true,
                            ReusablePch = request.Version == 6 ? true : null } : Capabilities), lifetime.Token);
                    return;
                }
                if (Volatile.Read(ref poisoned) != 0) throw new Rejected("Worker cleanup failed; restart after inspection");
                if (request.Toolchain != compiler.Identity) throw new Rejected("Toolchain mismatch");
                if (request.Version == 6) lifetime.CancelAfter(TimeSpan.FromSeconds(Wire.NativeRequestSeconds(config.TimeoutSeconds)));
                acquired = slots.Wait(0);
                if (!acquired) { await Wire.Write(tls, new Reply("busy", attempt), lifetime.Token); return; }
                // Server-generated workspace identity: replayed attempts never share a directory or outputs.
                workspace = System.IO.Path.Combine(state, "attempt-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workspace);
                Event("receiving", attempt);
                if (reference) await Wire.Write(tls, new Reply("accepted", attempt, MissingInputs: []), lifetime.Token);
                else if (request.Version >= 5) await inputCache.Receive(tls, request, workspace, lifetime.Token);
                else
                {
                    await Wire.Write(tls, new Reply("accepted", attempt), lifetime.Token);
                    foreach (InputFile file in request.Files!)
                        await Wire.ReceiveFile(tls, System.IO.Path.Combine(workspace, "inputs", file.Path.Replace('/', '\\')), file, lifetime.Token);
                }

                using var execution = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                using var monitorStop = new CancellationTokenSource();
                Task monitor = Monitor(tls, execution, monitorStop.Token);
                Execution result;
                CachedCompilation? cached = null;
                string? pdbName = request.ReturnsPdb ? "dbe-" + attempt + ".pdb" : null;
                string attemptDirectory = workspace;
                try
                {
                    Event("running", attempt);
                    if (request.NativeInput is { } native)
                    {
                        execution.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                        if (request.Version == 6)
                        {
                            cached = await nativeCache.Run(invocation!, attemptDirectory, config.TimeoutSeconds, config.MemoryMiB, execution.Token, pdbName);
                            result = cached.Result;
                        }
                        else
                        {
                            runtimeInputs.Populate(System.IO.Path.Combine(attemptDirectory, "inputs"));
                            result = await Task.Run(() => native.Run(compiler.Path, attemptDirectory, request, config.TimeoutSeconds, config.MemoryMiB, execution.Token, pdbName));
                        }
                    }
                    else if (request.NativePch is { } pair)
                    {
                        execution.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                        result = await Task.Run(() => pair.Run(compiler.Path, attemptDirectory, request, config.TimeoutSeconds, config.MemoryMiB, execution.Token, pdbName));
                    }
                    else result = await compiler.Run(workspace, request.Source!, config.TimeoutSeconds, config.MemoryMiB, execution.Token, request.Options, pdbName);
                }
                finally { monitorStop.Cancel(); await monitor; }
                if (result.Kind == 5)
                {
                    Interlocked.Exchange(ref poisoned, 1);
                    Event("cleanup-unconfirmed", attempt, workspace);
                    workspace = null; // Retain the directory; do not claim its resources are reusable.
                }
                string status = result.Kind switch { 0 => result.ExitCode == 0 ? "completed" : "compile-error", 2 => "cancelled",
                    3 => "timeout", 4 => "output-limit", _ => "infrastructure-error" };
                int[] required = request.Version >= 5 ? MappedCompile.Required(request, result) : [];
                if (required.Length != 0) status = "input-required";
                InputFile? output = null;
                InputFile? pdb = null;
                InputFile? producer = null;
                InputFile? producerPdb = null;
                string? objectPath = workspace is null ? null : System.IO.Path.Combine(workspace, "result.obj");
                if (status == "completed")
                {
                    if (objectPath is null || !File.Exists(objectPath)) throw new Rejected("Compiler produced no object");
                    Wire.NoReparse(objectPath);
                    long length = new FileInfo(objectPath).Length;
                    int limit = request.Version is 3 or 5 or 6 ? Wire.PreparedObjectLimit : Wire.OutputLimit;
                    if (length <= 0 || length > limit) throw new Rejected($"Object size {length} exceeds supported range 1..{limit}");
                    output = new InputFile("result.obj", length, Wire.HashFile(objectPath));
                    if (request.NativePch is not null || request.NativeInput?.Producer is not null)
                    {
                        string path = System.IO.Path.Combine(attemptDirectory, "producer.obj");
                        Wire.NoReparse(path);
                        if (new FileInfo(path).Length is <= 0 or > Wire.OutputLimit) throw new Rejected("Producer object out of bounds");
                        producer = new InputFile("producer.obj", new FileInfo(path).Length, Wire.HashFile(path));
                    }
                    if (pdbName is not null)
                    {
                        string path = System.IO.Path.Combine(workspace!, pdbName);
                        Wire.NoReparse(path);
                        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > Wire.PdbLimit) throw new Rejected("PDB missing or out of bounds");
                        pdb = new InputFile(pdbName, new FileInfo(path).Length, Wire.HashFile(path));
                    }
                    if (cached?.ProducerPdb is { } seedName)
                    {
                        string path = System.IO.Path.Combine(workspace!, seedName);
                        producerPdb = new InputFile(seedName, new FileInfo(path).Length, Wire.HashFile(path));
                    }
                }
                Event(status, attempt, result.Kind == 1 || result.Kind == 5 ? "Win32=" + result.Error : "");
                await Wire.Write(tls, new Reply(status, attempt, "native-kind=" + result.Kind + "; win32=" + result.Error,
                    ExitCode: result.Kind == 0 ? result.ExitCode : null, StdoutBytes: result.Stdout.Length,
                    StderrBytes: result.Stderr.Length, Output: output, Pdb: pdb, ProducerObject: producer,
                    RequiredInputs: required.Length == 0 ? null : required, ProducerPdb: producerPdb, Cache: cached?.Metrics), lifetime.Token);
                headerSent = true;
                bool sendSeed = false;
                if (producerPdb is not null)
                {
                    byte[] selection = new byte[1]; await tls.ReadExactlyAsync(selection, lifetime.Token);
                    if (selection[0] > 1) throw new Rejected("Invalid producer symbol selection");
                    sendSeed = selection[0] == 1;
                }
                await tls.WriteAsync(result.Stdout, lifetime.Token);
                await tls.WriteAsync(result.Stderr, lifetime.Token);
                if (output is not null) await Wire.SendFile(tls, objectPath!, output, lifetime.Token);
                if (pdb is not null) await Wire.SendFile(tls, System.IO.Path.Combine(workspace!, pdb.Path), pdb, lifetime.Token);
                if (producer is not null) await Wire.SendFile(tls, System.IO.Path.Combine(attemptDirectory, producer.Path), producer, lifetime.Token);
                if (producerPdb is not null && sendSeed) await Wire.SendFile(tls, System.IO.Path.Combine(workspace!, producerPdb.Path), producerPdb, lifetime.Token);
            }
            catch (Exception e)
            {
                Event("request-failed", attempt, e.GetType().Name + ": " + e.Message);
                if (tls.IsAuthenticated && !headerSent)
                {
                    using var errorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await Wire.Write(tls, new Reply(e is Rejected or JsonException ? "rejected" : "infrastructure-error", attempt,
                        Message: e is Rejected ? e.Message : e.GetType().Name), errorTimeout.Token); }
                    catch (Exception) { /* peer may already have disconnected */ }
                }
            }
            finally
            {
                if (nativeCache.Unhealthy) Interlocked.Exchange(ref poisoned, 1);
                if (workspace is not null)
                {
                    try { Directory.Delete(workspace, true); }
                    catch (Exception e) { Interlocked.Exchange(ref poisoned, 1); Event("cleanup-failed", attempt, e.Message); }
                }
                if (acquired) slots.Release();
                connections.Release();
            }
        }
    }

    private static async Task Monitor(Stream stream, CancellationTokenSource execution, CancellationToken stop)
    {
        try
        {
            int received = await stream.ReadAsync(new byte[1], stop);
            // EOF or any control byte cancels this connection's single attempt.
            if (received is 0 or 1) execution.Cancel();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (IOException) { execution.Cancel(); }
    }

    private static void Event(string state, string attempt, string detail = "") =>
        Console.WriteLine(JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, state, attempt, detail }));
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Stop(); nativeCache.Dispose(); certificate.Dispose(); compiler.Dispose(); inputCache.Dispose(); runtimeInputs.Dispose(); ownership.Dispose();
        slots.Dispose(); connections.Dispose(); headerReaders.Dispose(); shutdown.Dispose();
    }
}

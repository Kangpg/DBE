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
    private readonly ConcurrentDictionary<int, Task> active = new();
    private readonly TcpListener listener;
    private readonly CancellationTokenSource shutdown = new();
    private int sequence, poisoned;
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
            compiler = new Compiler(System.IO.Path.Combine(root, config.Compiler));
            try { certificate = Identity.Load(System.IO.Path.Combine(root, config.Certificate)); }
            catch { compiler.Dispose(); throw; }
        }
        catch { ownership.Dispose(); throw; }
        slots = new SemaphoreSlim(config.Slots);
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
                Request request = await Wire.Read<Request>(tls, lifetime.Token);
                Wire.Validate(request);
                attempt = request.Operation == "compile" ? request.Attempt : "";
                if (request.Operation == "info")
                {
                    await Wire.Write(tls, new Reply(Volatile.Read(ref poisoned) == 0 ? "ready" : "unhealthy", Capabilities: Capabilities), lifetime.Token);
                    return;
                }
                if (Volatile.Read(ref poisoned) != 0) throw new Rejected("Worker cleanup failed; restart after inspection");
                if (request.Toolchain != compiler.Identity) throw new Rejected("Toolchain mismatch");
                acquired = slots.Wait(0);
                if (!acquired) { await Wire.Write(tls, new Reply("busy", attempt), lifetime.Token); return; }
                // Server-generated workspace identity: replayed attempts never share a directory or outputs.
                workspace = System.IO.Path.Combine(state, "attempt-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workspace);
                Event("receiving", attempt);
                await Wire.Write(tls, new Reply("accepted", attempt), lifetime.Token);
                foreach (InputFile file in request.Files!)
                    await Wire.ReceiveFile(tls, System.IO.Path.Combine(workspace, "inputs", file.Path.Replace('/', '\\')), file, lifetime.Token);

                using var execution = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                using var monitorStop = new CancellationTokenSource();
                Task monitor = Monitor(tls, execution, monitorStop.Token);
                Execution result;
                string? pdbName = request.Version == 3 && request.Options!.Contains("/Zi") ? "dbe-" + attempt + ".pdb" : null;
                try
                {
                    Event("running", attempt);
                    result = await compiler.Run(workspace, request.Source!, config.TimeoutSeconds, config.MemoryMiB, execution.Token, request.Options, pdbName);
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
                InputFile? output = null;
                InputFile? pdb = null;
                string? objectPath = workspace is null ? null : System.IO.Path.Combine(workspace, "result.obj");
                if (status == "completed")
                {
                    if (objectPath is null || !File.Exists(objectPath)) throw new Rejected("Compiler produced no object");
                    Wire.NoReparse(objectPath);
                    long length = new FileInfo(objectPath).Length;
                    int limit = request.Version == 3 ? Wire.PreparedObjectLimit : Wire.OutputLimit;
                    if (length <= 0 || length > limit) throw new Rejected($"Object size {length} exceeds supported range 1..{limit}");
                    output = new InputFile("result.obj", length, Wire.HashFile(objectPath));
                    if (pdbName is not null)
                    {
                        string path = System.IO.Path.Combine(workspace!, pdbName);
                        Wire.NoReparse(path);
                        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > Wire.PdbLimit) throw new Rejected("PDB missing or out of bounds");
                        pdb = new InputFile(pdbName, new FileInfo(path).Length, Wire.HashFile(path));
                    }
                }
                Event(status, attempt, result.Kind == 1 || result.Kind == 5 ? "Win32=" + result.Error : "");
                await Wire.Write(tls, new Reply(status, attempt, "native-kind=" + result.Kind + "; win32=" + result.Error,
                    ExitCode: result.Kind == 0 ? result.ExitCode : null, StdoutBytes: result.Stdout.Length,
                    StderrBytes: result.Stderr.Length, Output: output, Pdb: pdb), lifetime.Token);
                headerSent = true;
                await tls.WriteAsync(result.Stdout, lifetime.Token);
                await tls.WriteAsync(result.Stderr, lifetime.Token);
                if (output is not null) await Wire.SendFile(tls, objectPath!, output, lifetime.Token);
                if (pdb is not null) await Wire.SendFile(tls, System.IO.Path.Combine(workspace!, pdb.Path), pdb, lifetime.Token);
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
        Stop(); certificate.Dispose(); compiler.Dispose(); ownership.Dispose();
        slots.Dispose(); connections.Dispose(); shutdown.Dispose();
    }
}

using System.Text;
using System.Text.Json;

namespace Dbe;
sealed record AdapterConfig(string RealCompiler, string ClientConfig, string StateDirectory, string Mode, bool AllowCompilerMismatch = false, string? HostPolicy = null, string[]? AdditionalClients = null)
{
    internal IEnumerable<string> Clients => new[] { ClientConfig }.Concat(AdditionalClients ?? []);
}
sealed record ObjectRecord(string Key, string Hash, InputFile? Pdb = null);

static partial class MsBuildAdapter
{
    internal static async Task<int> RunBuildTool(string tool, CancellationToken token)
    {
        var (config, _) = Identity.ReadConfig<AdapterConfig>(Path.Combine(AppContext.BaseDirectory, "msbuild.json"));
        using var scheduler = new HostScheduler(config.HostPolicy ?? throw new Rejected("Build tool requires host policy"), config.Clients);
        int memory = tool == "link" && scheduler.Policy.LinkMemoryMiB > 0 ? scheduler.Policy.LinkMemoryMiB : scheduler.Policy.LocalMemoryMiB;
        int slots = (memory + scheduler.Policy.LocalMemoryMiB - 1) / scheduler.Policy.LocalMemoryMiB;
        using var lease = await scheduler.Acquire(true, tool + " " + Environment.CurrentDirectory, token, slotCost: slots, memoryMiB: memory);
        string executable = Path.Combine(Path.GetDirectoryName(config.RealCompiler)!, tool + ".exe");
        Console.WriteLine("DBE: local " + tool + " " + Environment.CurrentDirectory);
        lease.BeginExecution();
        Execution result = await Task.Factory.StartNew(() => Native.Run(executable, Environment.CurrentDirectory, ClInvocation.RawTail(),
            1200, memory, token, true), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (result.Kind != 5) lease.ConfirmCleanup();
        Emit(result);
        return result.Kind == 0 ? unchecked((int)result.ExitCode) : Fail(tool + " supervisor kind=" + result.Kind);
    }
    internal static async Task<int> Run(string[] args, CancellationToken token)
    {
        var (config, root) = Identity.ReadConfig<AdapterConfig>(Path.Combine(AppContext.BaseDirectory, "msbuild.json"));
        if (config.Mode is not ("Local" or "Auto" or "Distributed" or "Hybrid")) throw new Rejected("Invalid DBE mode");
        if (config.Mode != "Hybrid" && config.AdditionalClients is { Length: > 0 }) throw new Rejected("Multiple Workers require Hybrid mode");
        using var scheduler = config.Mode == "Hybrid" ? new HostScheduler(config.HostPolicy ?? throw new Rejected("Hybrid requires a shared host policy"), config.Clients) : null;
        string raw = ClInvocation.RawTail();
        if (config.Mode == "Local") return Local(config, raw, "Local mode", token);
        ClInvocation[] batch;
        try
        {
            if (ClInvocation.CreatesPch(args)) return await LocalBounded(config, raw, "required PCH producer", token, scheduler);
            batch = ClInvocation.ParseBatch(args);
        }
        catch (UnsupportedCompile e) { return await Fallback(config, raw, e.Message, token, scheduler); }
        int[] results = new int[batch.Length];
        var pending = new System.Collections.Concurrent.ConcurrentQueue<int>(Enumerable.Range(0, batch.Length));
        int concurrency = scheduler is null ? 2 : scheduler.Policy.LocalSlots + scheduler.PipelineCapacity;
        var compiler = new Lazy<Compiler>(() => new Compiler(config.RealCompiler));
        try {
        await Parallel.ForEachAsync(Enumerable.Range(0, Math.Min(concurrency, batch.Length)), new ParallelOptions
        { MaxDegreeOfParallelism = concurrency, CancellationToken = token }, async (_, ct) =>
        {
            while (pending.TryDequeue(out int index)) {
            var invocation = batch[index];
            using var placement = scheduler is null ? null : await scheduler.AcquireCompile(invocation.Source, ct, invocation.CompilerPdb,
                Math.Min(scheduler.Policy.LocalBatchSlots, pending.Count + 1));
            await Task.Yield(); // Let other placements start before a synchronous native compiler call.
            var indices = new List<int> { index };
            if (placement is { IsLocal: true, SlotCost: > 1 })
                // Keep each MP child alive for a few TUs to amortize compiler
                // startup/PCH loading. Bound the chunk so remote work and
                // cancellation are not hidden behind a whole-project batch.
                while (indices.Count < placement.SlotCost * 4 && pending.TryDequeue(out int next)) indices.Add(next);
            if (indices.Count == 1) results[index] = await RunOne(config, root, invocation, ct, scheduler, placement, compiler);
            else {
                int result = await CompileLocalBatch(config, root, indices.Select(i => batch[i]).ToArray(), placement!, scheduler!, ct);
                foreach (int i in indices) results[i] = result;
            }
            }
        });
        return results.FirstOrDefault(x => x != 0);
        } finally { if (compiler.IsValueCreated) compiler.Value.Dispose(); }
    }

    private static async Task<int> RunOne(AdapterConfig config, string root, ClInvocation invocation, CancellationToken token, HostScheduler? scheduler, HostScheduler.Lease? placement, Lazy<Compiler> compiler)
    {
        using var owned = CreateAttempt(config, root, invocation);
        string objectState = owned.ObjectState, attempt = owned.Id, attemptRoot = owned.Root;
        string inputs = Path.Combine(attemptRoot, "inputs");
        string preprocessed = Path.Combine(inputs, "translation.cpp");
        string response = Path.Combine(attemptRoot, "preprocess.rsp");
        string localResponse = Path.Combine(attemptRoot, "local.rsp");
        File.WriteAllText(localResponse, string.Join(' ', invocation.LocalOptions.Select(ClInvocation.Quote)), Encoding.Unicode);
        string raw = "@" + ClInvocation.Quote(localResponse);
        async Task<int> FallbackOne(string reason)
        {
            if (scheduler is not null)
            {
                Console.WriteLine("DBE: fallback " + invocation.Source + ": " + reason);
                using var local = await scheduler.AcquireLocalFallback(placement!, invocation.Source, token, invocation.CompilerPdb);
                return await CompileLocal(config, invocation, attemptRoot, objectState, attempt, local, scheduler, token, "fallback");
            }
            if (config.Mode == "Auto") DeleteObject(invocation.Object);
            return await Fallback(config, raw, reason, token, scheduler);
        }
        if (placement is { IsLocal: true }) return await CompileLocal(config, invocation, attemptRoot, objectState, attempt, placement, scheduler!, token);
        string preprocessSource;
        try { preprocessSource = PreparedInput.PrepareSource(invocation, attemptRoot); }
        catch (Exception e) when (e is UnsupportedCompile or DecoderFallbackException or EncoderFallbackException)
        { return await FallbackOne(e.Message); }
        string preprocessArgs = string.Join(' ', invocation.PreprocessOptions.Concat(new[] { "/nologo", "/P", "/Y-", "/TP", "/Fi" + preprocessed, preprocessSource }).Select(ClInvocation.Quote));
        File.WriteAllText(response, preprocessArgs, Encoding.Unicode);
        Console.WriteLine("DBE: preparing " + invocation.Source);
        // Inherit tracker/INCLUDE and other compiler environment only on the local
        // machine. Remote execution still uses the Worker's minimal environment.
        Execution pp;
        using (var preparation = scheduler is null ? null : await scheduler.Acquire(true, invocation.Source + " [preprocess]", token,
            memoryMiB: scheduler.Policy.PreprocessMemoryMiB))
            pp = await ExecuteAsync(config, "@" + ClInvocation.Quote(response), token, preparation, scheduler?.Policy.LocalMemoryMiB ?? 4096);
        Emit(pp);
        if (pp.Kind != 0 || pp.ExitCode != 0)
        {
            DeleteObject(invocation.Object);
            Record(attemptRoot, "preprocess-failed", invocation, null, null);
            return pp.Kind == 0 ? unchecked((int)pp.ExitCode) : Fail("Local preprocessor failed: kind=" + pp.Kind);
        }
        if (!File.Exists(preprocessed) || new FileInfo(preprocessed).Length > Wire.PreparedLimit)
            return await FallbackOne("Preprocessed input exceeds supported size");
        // /P preserves line directives. Resolve relative diagnostic source paths
        // while macros such as __FILE__ have already been expanded locally.
        try { PreparedInput.Normalize(preprocessed, invocation); }
        catch (Exception e) when (e is UnsupportedCompile or DecoderFallbackException or EncoderFallbackException)
        { return await FallbackOne("Preprocessed input cannot be preserved: " + e.Message); }
        if (new FileInfo(preprocessed).Length > Wire.PreparedLimit) return await FallbackOne("Expanded input exceeds supported size");
        var (clientConfig, clientRoot) = Identity.ReadConfig<ClientConfig>(placement?.ClientPath ?? config.ClientConfig);
        using var client = new Client(clientConfig, clientRoot);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(180));
        Capabilities remote;
        try { remote = await client.Info(deadline.Token, 3); }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException or Rejected)
        { return await FallbackOne("Worker unavailable: " + e.GetType().Name); }
        // The batch owns the pins, so concurrent TUs share one identity scan and
        // compiler files remain immutable until every task in this batch ends.
        var localCompiler = compiler.Value;
        if (localCompiler.Identity != remote.Toolchain && (!config.AllowCompilerMismatch || invocation.RemoteOptions.Contains("/GL")))
            return await FallbackOne("Compiler identity mismatch: local " + localCompiler.Version + ", Worker " + remote.CompilerVersion);
        string inputHash = Wire.HashFile(preprocessed);
        string key = Wire.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            profile = "msbuild-preprocessed-v3", inputHash, invocation.RemoteOptions, invocation.Source,
            worker = remote.Toolchain, local = localCompiler.Identity, config.AllowCompilerMismatch
        })));
        string recordPath = Path.Combine(objectState, "object.json");
        if (File.Exists(invocation.Object) && File.Exists(recordPath))
        {
            ObjectRecord? prior = JsonSerializer.Deserialize<ObjectRecord>(File.ReadAllText(recordPath), Wire.Json);
            if (prior is not null && prior.Key == key && prior.Hash == Wire.HashFile(invocation.Object) && PdbMatches(prior.Pdb, invocation.Object))
            {
                // Hybrid's MSBuild tracker already selected this TU because an
                // input/command/PCH became newer. Content reuse is valid, but
                // retaining an older object timestamp would select it forever.
                if (scheduler is not null) File.SetLastWriteTimeUtc(invocation.Object, DateTime.UtcNow);
                Console.WriteLine("DBE: unchanged object " + invocation.Object);
                Record(attemptRoot, "unchanged", invocation, remote, inputHash);
                return 0;
            }
        }
        var manifest = new Request(3, "compile", attempt, remote.Toolchain, "translation.cpp",
            [new InputFile("translation.cpp", new FileInfo(preprocessed).Length, inputHash)], invocation.RemoteOptions);
        File.WriteAllText(Path.Combine(attemptRoot, "manifest.json"), JsonSerializer.Serialize(manifest, Wire.Json));
        Reply result;
        string outputRoot = Path.Combine(attemptRoot, "result");
        Event(attemptRoot, "remote-ready", invocation, remote.Machine);
        deadline.CancelAfter(Timeout.InfiniteTimeSpan);
        try
        {
            using var request = scheduler is null ? null : await scheduler.AcquireRemoteRequest(invocation.Source, token, placement!.ClientPath);
            deadline.CancelAfter(TimeSpan.FromSeconds(180));
            Event(attemptRoot, "remote-requested", invocation, remote.Machine);
            result = await client.Compile(manifest, inputs, outputRoot, deadline.Token);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException or Rejected)
        { return await FallbackOne("Remote transport failed: " + e.GetType().Name); }
        foreach (string stream in new[] { "stdout", "stderr" })
        {
            string file = Path.Combine(outputRoot, stream + ".bin");
            if (File.Exists(file)) (stream == "stdout" ? Console.OpenStandardOutput() : Console.OpenStandardError()).Write(File.ReadAllBytes(file));
        }
        if (result.Status == "compile-error")
        {
            DeleteObject(invocation.Object);
            Record(attemptRoot, "remote-compile-error", invocation, remote, inputHash);
            return unchecked((int)(result.ExitCode ?? 1));
        }
        if (result.Status != "completed") return await FallbackOne("Worker returned " + result.Status + ": " + result.Message);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(invocation.Object)!);
        // Immutable, attempt-named symbols must be in place before the object
        // becomes visible. LINK searches the object's directory for its PDB.
        if (result.Pdb is { } pdb)
        {
            string symbols = Path.Combine(Path.GetDirectoryName(invocation.Object)!, pdb.Path);
            Wire.NoReparse(symbols);
            File.Copy(Path.Combine(outputRoot, pdb.Path), symbols, false);
            if (Wire.HashFile(symbols) != pdb.Sha256) throw new Rejected("PDB changed before publication");
        }
        string pending = invocation.Object + ".dbe-" + attempt;
        try
        {
            File.Copy(Path.Combine(outputRoot, "result.obj"), pending, false);
            if (Wire.HashFile(pending) != result.Output!.Sha256) throw new Rejected("Object changed before publication");
            File.Move(pending, invocation.Object, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
        File.WriteAllText(recordPath + ".new", JsonSerializer.Serialize(new ObjectRecord(key, result.Output!.Sha256, result.Pdb), Wire.Json));
        File.Move(recordPath + ".new", recordPath, true);
        Console.WriteLine("DBE: remote " + remote.Machine + " compiler=" + remote.CompilerVersion + " -> " + invocation.Object);
        Record(attemptRoot, "remote-completed", invocation, remote, inputHash);
        Event(attemptRoot, "remote-completed", invocation, remote.Machine);
        return 0;
    }

    private static async Task<int> CompileLocal(AdapterConfig config, ClInvocation invocation, string attemptRoot, string objectState, string attempt,
        HostScheduler.Lease placement, HostScheduler scheduler, CancellationToken token, string kind = "local")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(invocation.Object)!);
        string pending = invocation.Object + ".dbe-" + attempt;
        var options = LocalCommand(invocation, pending, 1, new[] { invocation.Source });
        string response = Path.Combine(attemptRoot, "scheduled-local.rsp");
        File.WriteAllText(response, string.Join(' ', options.Select(ClInvocation.Quote)), Encoding.Unicode);
        Event(attemptRoot, kind + "-running", invocation, Environment.MachineName);
        try
        {
            Execution result = await ExecuteAsync(config, "@" + ClInvocation.Quote(response), token, placement, scheduler.Policy.LocalMemoryMiB);
            Emit(result);
            if (result.Kind != 0 || result.ExitCode != 0)
            {
                DeleteObject(invocation.Object);
                Record(attemptRoot, kind + "-compile-error", invocation, null, null);
                Event(attemptRoot, kind + "-failed", invocation, Environment.MachineName);
                return result.Kind == 0 ? unchecked((int)result.ExitCode) : Fail("Local supervisor kind=" + result.Kind);
            }
            token.ThrowIfCancellationRequested();
            if (!File.Exists(pending)) throw new Rejected("Local compiler produced no object");
            File.Move(pending, invocation.Object, true);
            File.Delete(Path.Combine(objectState, "object.json")); // A previous remote reuse record is no longer authoritative.
            Record(attemptRoot, kind + "-completed", invocation, null, null);
            Event(attemptRoot, kind + "-completed", invocation, Environment.MachineName);
            return 0;
        }
        catch { DeleteObject(invocation.Object); throw; }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    private static void Event(string root, string status, ClInvocation invocation, string machine)
    {
        var value = new { status, source = invocation.Source, machine, time = DateTimeOffset.UtcNow, process = Environment.ProcessId };
        File.WriteAllText(Path.Combine(root, status + ".json"), JsonSerializer.Serialize(value, Wire.Json));
        Console.WriteLine($"DBE: {status} {machine} {invocation.Source}");
    }
    private static Execution Execute(AdapterConfig config, string raw, CancellationToken token, HostScheduler.Lease? lease, int memory)
    {
        token.ThrowIfCancellationRequested();
        lease?.BeginExecution();
        Execution result = Native.Run(config.RealCompiler, Environment.CurrentDirectory, raw, 120, lease?.MemoryMiB ?? memory, token, true);
        if (result.Kind != 5) lease?.ConfirmCleanup();
        return result;
    }
    private static Task<Execution> ExecuteAsync(AdapterConfig config, string raw, CancellationToken token, HostScheduler.Lease? lease, int memory) =>
        Task.Factory.StartNew(() => Execute(config, raw, token, lease, memory), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    private static async Task<int> LocalBounded(AdapterConfig config, string raw, string reason, CancellationToken token, HostScheduler? scheduler)
    {
        if (scheduler is null) return Local(config, raw, reason, token);
        using var lease = await scheduler.Acquire(true, reason, token, ClInvocation.PdbIdentity(ClInvocation.Expand(ClInvocation.Split(raw))));
        Console.WriteLine("DBE: local (" + reason + ")");
        Execution result = await ExecuteAsync(config, raw + " /MP1", token, lease, scheduler.Policy.LocalMemoryMiB);
        Emit(result);
        return result.Kind == 0 ? unchecked((int)result.ExitCode) : Fail("Local compiler supervisor kind=" + result.Kind);
    }

    private static void Record(string root, string status, ClInvocation invocation, Capabilities? worker, string? inputHash) =>
        File.WriteAllText(Path.Combine(root, "adapter-result.json"), JsonSerializer.Serialize(new
        { status, invocation.Source, invocation.Object, inputHash, worker, capturedAtUtc = DateTimeOffset.UtcNow }, Wire.Json));
    private static int Local(AdapterConfig config, string raw, string reason, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Console.WriteLine("DBE: local (" + reason + ")");
        Execution result = Native.Run(config.RealCompiler, Environment.CurrentDirectory, raw, 120, 4096, token, true);
        Emit(result);
        return result.Kind == 0 ? unchecked((int)result.ExitCode) : Fail("Local compiler supervisor kind=" + result.Kind);
    }
    private static Task<int> Fallback(AdapterConfig config, string raw, string reason, CancellationToken token, HostScheduler? scheduler) =>
        config.Mode is "Auto" or "Hybrid" ? LocalBounded(config, raw, "fallback: " + reason, token, scheduler) : Task.FromResult(Fail(reason));
    private static int Fail(string message) { Console.Error.WriteLine("DBE : error DBE0001: " + message); return 125; }
    private static void Emit(Execution result)
    { Console.OpenStandardOutput().Write(result.Stdout); Console.OpenStandardError().Write(result.Stderr); }
    private static void DeleteObject(string path) { if (File.Exists(path)) File.Delete(path); }
    private static void TrimHistory(string root, string objectId)
    {
        // Called only while holding the output owner's exclusive lock. Keep two
        // old attempts plus the new one; never infer ownership from a GUID alone.
        var owned = Directory.EnumerateDirectories(root).Where(path =>
        {
            string name = Path.GetFileName(path);
            string marker = Path.Combine(path, ".dbe-owned");
            return Guid.TryParseExact(name, "N", out _) && File.Exists(marker) &&
                new FileInfo(marker).Length < 200 && File.ReadAllText(marker) == objectId + "\n" + name;
        }).OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(2).ToArray();
        foreach (string path in owned) { Wire.NoReparse(path); Directory.Delete(path, true); }
    }
    private static bool PdbMatches(InputFile? pdb, string obj)
    {
        if (pdb is null) return true;
        if (!pdb.Path.StartsWith("dbe-", StringComparison.Ordinal) || !pdb.Path.EndsWith(".pdb", StringComparison.Ordinal) ||
            !Guid.TryParseExact(pdb.Path[4..^4], "N", out _)) return false;
        string path = Path.Combine(Path.GetDirectoryName(obj)!, pdb.Path);
        Wire.NoReparse(path);
        return File.Exists(path) && new FileInfo(path).Length == pdb.Length && Wire.HashFile(path) == pdb.Sha256;
    }
}

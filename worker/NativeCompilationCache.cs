using System.Diagnostics;

namespace Dbe;

sealed record CachedCompilation(Execution Result, string? ProducerPdb, NativeCacheMetrics Metrics);

// Owns immutable input namespaces and completed PCH generations. A borrower
// receives private outputs and a writable PDB clone; cached files never change.
sealed class NativeCompilationCache(string root, NativeInputCache inputs, RuntimeInputs runtime, Compiler compiler) : IDisposable
{
    private const int ContextLimit = 4;
    // Disk artifact capacity is independent of the receiving compiler's
    // unchanged Job memory, timeout and slot limits.
    private const long ByteLimit = 4L * 1024 * 1024 * 1024, PchLimit = 1536L * 1024 * 1024;
    private readonly SemaphoreSlim admission = new(1);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private static readonly List<NativeCompilationCache> retained = [];
    private long inputBuilds, pchBuilds;
    private int unhealthy;
    internal bool Unhealthy => Volatile.Read(ref unhealthy) != 0;
    private sealed class Entry(NativeContext context, string path)
    {
        internal readonly NativeContext Context = context;
        internal string Key => Context.Identity;
        internal readonly string Root = path;
        internal readonly SemaphoreSlim Initialization = new(1);
        internal readonly List<FileStream> Pins = [];
        internal bool InputsReady, PchReady, Retain, Invalid;
        internal string? Failure;
        internal MappedCompiler? Compiler;
        internal string? SeedName;
        internal long Bytes, LastUse;
        internal int Users;
        internal void Delete()
        {
            foreach (var pin in Pins) pin.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            Initialization.Dispose();
        }
    }
    internal async Task<NativeInvocation?> Resolve(Request request, CancellationToken token)
    {
        if (request.Files is not null) return new NativeInvocation(request, new NativeContext(request));
        NativeContext.ValidateReferenceHeader(request);
        await admission.WaitAsync(token);
        try
        {
            return entries.TryGetValue(request.ContextKey!, out var entry) && entry.InputsReady && !entry.Invalid && !entry.Retain
                ? new NativeInvocation(entry.Context.Expand(request), entry.Context) : null;
        }
        finally { admission.Release(); }
    }

    private async Task<Entry> Borrow(NativeInvocation invocation, CancellationToken token)
    {
        await admission.WaitAsync(token);
        try
        {
            if (Unhealthy) throw new Rejected("Native context cleanup needs inspection");
            string key = invocation.Context.Identity;
            if (entries.TryGetValue(key, out var existing)) { Interlocked.Increment(ref existing.Users); return existing; }
            const long estimate = 2L * 1024 * 1024 * 1024;
            while (entries.Count >= ContextLimit || entries.Values.Sum(x => x.Bytes) + estimate > ByteLimit)
            {
                var old = entries.Values.Where(x => Volatile.Read(ref x.Users) == 0 && !x.Retain).OrderBy(x => x.LastUse).FirstOrDefault();
                if (old is null) throw new Rejected("Reusable native contexts are at capacity");
                try { old.Delete(); entries.Remove(old.Key); }
                catch { old.Retain = true; Interlocked.Exchange(ref unhealthy, 1); throw; }
            }
            Directory.CreateDirectory(root); Wire.NoReparse(root);
            if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace < 2L * 1024 * 1024 * 1024 + estimate)
                throw new Rejected("Reusable native context disk headroom insufficient");
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N")[..16]);
            if (Directory.Exists(directory) || File.Exists(directory)) throw new Rejected("Native context path already exists");
            Directory.CreateDirectory(directory);
            using (var marker = new FileStream(Path.Combine(directory, ".dbe-owned"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                marker.Write(System.Text.Encoding.ASCII.GetBytes(key));
            var created = new Entry(invocation.Context, directory) { Bytes = estimate, Users = 1 };
            entries.Add(key, created);
            return created;
        }
        finally { admission.Release(); }
    }
    internal async Task<CachedCompilation> Run(NativeInvocation invocation, string workspace, int seconds, int memory, CancellationToken token, string? pdbName)
    {
        Request request = invocation.Request;
        Entry entry = await Borrow(invocation, token);
        bool inputsHit = false, pchHit = false;
        Execution? producerLog = null;
        double waitMs = 0, inputsMs = 0, pchMs = 0, compileMs = 0;
        CachedCompilation Result(Execution execution, string? seed = null) => new(execution, seed,
            new NativeCacheMetrics(entry.Key, inputsHit, pchHit, Interlocked.Read(ref inputBuilds), Interlocked.Read(ref pchBuilds),
                waitMs, inputsMs, pchMs, compileMs));
        try
        {
            var watch = Stopwatch.StartNew();
            await entry.Initialization.WaitAsync(token); waitMs = watch.Elapsed.TotalMilliseconds;
            try
            {
                if (entry.Invalid || entry.Retain) throw new Rejected(entry.Failure ?? "Native context is not reusable");
                inputsHit = entry.InputsReady;
                if (!entry.InputsReady)
                {
                    watch.Restart();
                    string tree = Path.Combine(entry.Root, "inputs");
                    if (Directory.Exists(tree)) Directory.Delete(tree, true); // Never held a native process before InputsReady.
                    inputs.Materialize(request, entry.Root, token);
                    runtime.Populate(tree);
                    entry.Compiler = new MappedCompiler(compiler.Path, tree, request.NativeInput!);
                    entry.InputsReady = true;
                    inputsMs = watch.Elapsed.TotalMilliseconds; Interlocked.Increment(ref inputBuilds);
                }
                if (request.NativeInput!.Producer is { } recipe)
                {
                    pchHit = entry.PchReady;
                    if (!entry.PchReady)
                    {
                        watch.Restart();
                        entry.SeedName ??= recipe.Options.Contains("/Zi") ? "dbe-" + Guid.NewGuid().ToString("N") + ".pdb" : null;
                        string? seed = entry.SeedName is null ? null : Path.Combine(entry.Root, entry.SeedName);
                        foreach (string file in new[] { entry.Compiler!.Resolve(recipe.PchFile), entry.Compiler.Resolve(recipe.ObjectFile!), seed }.OfType<string>())
                            if (File.Exists(file)) File.Delete(file);
                        Execution produced = await Task.Run(() => entry.Compiler.Produce(recipe, entry.Root, seed, seconds, memory, token));
                        pchMs = watch.Elapsed.TotalMilliseconds; Interlocked.Increment(ref pchBuilds);
                        if (produced.Kind == 5) { entry.Retain = true; Interlocked.Exchange(ref unhealthy, 1); }
                        if (produced.Kind != 0 || produced.ExitCode != 0) return Result(produced);
                        producerLog = produced;
                        entry.Bytes = Directory.EnumerateFiles(entry.Root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
                        if (entry.Bytes > 2L * 1024 * 1024 * 1024) {
                            entry.Invalid = true; entry.Failure = "Native context exceeds its byte budget";
                            throw new Rejected(entry.Failure);
                        }
                        var artifacts = new List<(string Path, long Limit, string Kind)> {
                            (entry.Compiler.Resolve(recipe.PchFile), PchLimit, "PCH"),
                            (entry.Compiler.Resolve(recipe.ObjectFile!), Wire.OutputLimit, "producer object") };
                        if (seed is not null) artifacts.Add((seed, Wire.PdbLimit, "producer PDB"));
                        Pin(entry, artifacts);
                        entry.PchReady = true;
                    }
                }
            }
            finally { entry.Initialization.Release(); }
            string? pdb = pdbName is null ? null : Path.Combine(workspace, pdbName);
            if (entry.SeedName is { } seedName && pdb is not null) File.Copy(Path.Combine(entry.Root, seedName), pdb);
            watch.Restart();
            Execution result = await Task.Run(() => entry.Compiler!.Consume(request, workspace, pdb, seconds, memory, token));
            compileMs = watch.Elapsed.TotalMilliseconds;
            if (result.Kind == 5) { entry.Retain = true; Interlocked.Exchange(ref unhealthy, 1); }
            if (result.Kind == 0 && result.ExitCode == 0 && request.NativeInput!.Producer is { } producer)
            {
                File.Copy(entry.Compiler!.Resolve(producer.ObjectFile!), Path.Combine(workspace, "producer.obj"));
                if (entry.SeedName is { } name) File.Copy(Path.Combine(entry.Root, name), Path.Combine(workspace, name));
            }
            if (producerLog is not null && result.Kind != 5)
                result = result.Stdout.Length + producerLog.Stdout.Length > Wire.DiagnosticLimit || result.Stderr.Length + producerLog.Stderr.Length > Wire.DiagnosticLimit
                    ? result with { Kind = 4 }
                    : result with { Stdout = [..producerLog.Stdout, ..result.Stdout], Stderr = [..producerLog.Stderr, ..result.Stderr] };
            return Result(result, result.Kind == 0 && result.ExitCode == 0 ? entry.SeedName : null);
        }
        finally
        {
            entry.LastUse = Stopwatch.GetTimestamp(); Interlocked.Decrement(ref entry.Users);
        }
    }
    private static void Pin(Entry entry, IEnumerable<(string Path, long Limit, string Kind)> artifacts)
    {
        List<FileStream> acquired = [];
        try
        {
            foreach (var artifact in artifacts)
            {
                Wire.NoReparse(artifact.Path);
                var file = new FileStream(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read); acquired.Add(file);
                if (file.Length is <= 0 || file.Length > artifact.Limit) {
                    entry.Invalid = true;
                    entry.Failure = $"Cached {artifact.Kind} size={file.Length} exceeds limit={artifact.Limit}";
                    throw new Rejected(entry.Failure);
                }
            }
            entry.Pins.AddRange(acquired); acquired.Clear();
        }
        finally { foreach (var file in acquired) file.Dispose(); }
    }
    public void Dispose()
    {
        foreach (var entry in entries.Values)
        {
            if (entry.Retain) { Interlocked.Exchange(ref unhealthy, 1); continue; }
            try { entry.Delete(); }
            catch { entry.Retain = true; Interlocked.Exchange(ref unhealthy, 1); }
        }
        if (!Unhealthy)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root); }
            catch { Interlocked.Exchange(ref unhealthy, 1); }
        }
        if (Unhealthy) { lock (retained) retained.Add(this); }
        else admission.Dispose();
    }
}

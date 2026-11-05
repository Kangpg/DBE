using System.Text.Json;

namespace Dbe;

sealed record ProducerRecord(string Compiler, string Directory, NativeRecipe Recipe, string SourceHash);

// One CL Task's stable input description, shared by its local/remote placements.
sealed class NativeBuild : IDisposable
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".inl", ".inc", ".ipp", ".tpp", ".def" };
    private static readonly HashSet<string> CppSources = new(StringComparer.OrdinalIgnoreCase) { ".c", ".cc", ".cpp", ".cxx" };
    // Bounded so input hashing cannot compete with the build it serves.
    private const int HashConcurrency = 4;
    // Compiler and linker outputs. DBE supplies PCH and symbol inputs itself, so
    // listing these under a source tree only inflates the manifest.
    private static readonly HashSet<string> BuildOutputs = new(StringComparer.OrdinalIgnoreCase)
        { ".obj", ".pch", ".pdb", ".ilk", ".idb", ".iobj", ".ipdb", ".exp", ".tlog", ".lastbuildstate", ".ipch" };
    // A CL Task stops offering work to Workers once they keep failing, or its own
    // inventory cannot be described: small fallback Jobs would otherwise take turns
    // on the shared PDB and cost far more than the original native fan-out.
    private const int RemoteFailureLimit = 3;
    private int remoteFailures;
    internal bool RemoteViable => Volatile.Read(ref remoteFailures) < RemoteFailureLimit && Volatile.Read(ref unavailable) is null;
    internal void NoteRemoteFallback() => Interlocked.Increment(ref remoteFailures);
    internal void NoteRemoteCompleted() => Volatile.Write(ref remoteFailures, 0);
    private readonly string cwd = Environment.CurrentDirectory;
    private readonly string include = Environment.GetEnvironmentVariable("INCLUDE") ?? "";
    private readonly AdapterConfig config;
    private readonly ClInvocation[] batch;
    private readonly string[] inputRoots, inventoryRoots, artifactRoots;
    private InputFile[]? files;
    private NativeRecipe? producer;
    private string? unavailable;
    private string? contextKey;
    private NativeContext? preparedContext;
    internal NativeClientCache ClientCache { get; }
    private readonly object gate = new();
    internal NativeBuild(AdapterConfig config, ClInvocation[] batch)
    {
        this.config = config; this.batch = batch;
        ClientCache = new NativeClientCache(config.StateDirectory);
        // MSBuild declares the project's own scope for this CL Task; see DBE.Remote.targets.
        string[] declared = config.NativeAutoScope
            ? (Environment.GetEnvironmentVariable("DBE_INPUT_ROOTS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        inventoryRoots = (config.NativeInputRoots ?? []).Concat(declared).Select(Path.GetFullPath).Select(x => x.TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        inputRoots = inventoryRoots.Concat((config.NativeToolchainRoots ?? []).Select(Path.GetFullPath).Select(x => x.TrimEnd('\\')))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        artifactRoots = (config.NativeArtifactRoots ?? []).Select(Path.GetFullPath).Select(x => x.TrimEnd('\\') + "\\")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static string[] Profile(ClInvocation invocation)
    {
        var options = invocation.PreprocessOptions.ToList();
        if (!options.Contains("/utf-8")) options.AddRange(["/source-charset:." + invocation.CodePage, "/execution-charset:." + invocation.CodePage]);
        MappedCompile.ValidateOptions(options.ToArray());
        return options.ToArray();
    }
    private static string RecipePath(AdapterConfig config, ClInvocation invocation)
    {
        if (invocation.PchFile is null) throw new UnsupportedCompile("Native PCH requires an explicit Fp");
        string directory = Path.Combine(Path.GetFullPath(config.StateDirectory), "native-producers");
        Directory.CreateDirectory(directory);
        string key = Wire.Hash(System.Text.Encoding.UTF8.GetBytes(config.RealCompiler.ToUpperInvariant() + "\n" + invocation.PchFile.ToUpperInvariant()));
        return Path.Combine(directory, key + ".json");
    }
    internal static void Remember(AdapterConfig config, ClInvocation invocation)
    {
        if (invocation.PchHeader is null || invocation.PchFile is null || !invocation.PchProducer) return;
        var recipe = new NativeRecipe(invocation.SourceArgument ?? invocation.Source, Profile(invocation), invocation.PchHeader,
            invocation.PchFile, invocation.Object, Guid.NewGuid().ToString("N"));
        var record = new ProducerRecord(config.RealCompiler, Environment.CurrentDirectory, recipe, Wire.HashFile(invocation.Source));
        string destination = RecipePath(config, invocation), pending = destination + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(pending, JsonSerializer.Serialize(record, Wire.Json));
        File.Move(pending, destination, true);
    }
    internal Request Manifest(ClInvocation invocation, string attempt, string toolchain)
    {
        lock (gate)
        {
            if (unavailable is not null) throw new UnsupportedCompile(unavailable);
            try { files ??= Prepare(invocation); }
            catch (Exception e) when (e is UnsupportedCompile or Rejected or IOException or UnauthorizedAccessException)
            { unavailable = e.Message; throw new UnsupportedCompile(e.Message); }
            if (files.Any(x => x.Deferred) && invocation.PreprocessOptions.Contains("/std:c++latest"))
                throw new UnsupportedCompile("Deferred inputs currently require a fixed C++ language standard");
            var request = new Request(6, "compile", attempt, toolchain, MappedCompile.InputPath(invocation.Source), files,
                Profile(invocation), NativeInput: new MappedCompile(cwd, invocation.SourceArgument ?? invocation.Source, include, producer));
            contextKey ??= NativeContext.Key(request);
            request = request with { ContextKey = contextKey };
            if (preparedContext is null) preparedContext = new NativeContext(request);
            else _ = preparedContext.Expand(request with { Files = null });
            return request;
        }
    }
    internal bool Unchanged()
    {
        lock (gate)
        {
            if (files is null) return true;
            // Unrequested deferred entries describe possible inputs, not bytes
            // consumed by this compile. Resolve validates their metadata before
            // admitting bytes; unused output artifacts may change independently.
            return files.Where(file => !file.Deferred).AsParallel().WithDegreeOfParallelism(HashConcurrency).All(file =>
            {
                string path = Original(file.Path);
                var metadata = new FileInfo(path);
                return metadata.Exists && metadata.Length == file.Length && metadata.LastWriteTimeUtc.Ticks == file.ModifiedUtcTicks &&
                    Wire.HashFile(path) == file.Sha256;
            });
        }
    }
    internal Request BatchManifest(ClInvocation[] sources, string attempt, string toolchain)
    {
        Request first = Manifest(sources[0], attempt, toolchain);
        var request = first with { Version = 7, BatchSources = sources.Select(x =>
            new NativeSource(MappedCompile.InputPath(x.Source), x.SourceArgument ?? x.Source)).ToArray() };
        lock (gate) _ = preparedContext!.Expand(request with { Files = null });
        return request;
    }
    internal void Resolve(Request request, int[] required)
    {
        lock (gate)
        {
            var updated = files!.ToArray();
            foreach (int index in required)
            {
                InputFile wanted = request.Files![index];
                int position = Array.FindIndex(files!, file => file.Path == wanted.Path);
                if (position < 0 || !wanted.Deferred) throw new Rejected("Unrecognized deferred input");
                if (!files![position].Deferred) continue;
                string source = Original(wanted.Path);
                Wire.NoReparse(source);
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != wanted.Length || File.GetLastWriteTimeUtc(source).Ticks != wanted.ModifiedUtcTicks)
                    throw new UnsupportedCompile("Deferred input changed");
                if (input.Length > Wire.FileLimit) throw new UnsupportedCompile("Requested native input exceeds single-file budget");
                string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(input));
                updated[position] = wanted with { Sha256 = hash, Deferred = false };
                contextKey = null;
                preparedContext = null;
            }
            files = updated;
        }
    }
    internal static string Original(string path) => path[0] + @":\" + path[2..].Replace('/', '\\');
    private InputFile[] Prepare(ClInvocation invocation)
    {
        if (invocation.PchHeader is not null && invocation.PreprocessOptions.Contains("/GL"))
            throw new UnsupportedCompile("LTCG consumers require the initiating compiler's original PCH");
        if (invocation.PchHeader is not null)
        {
            string path = RecipePath(config, invocation);
            if (!File.Exists(path) || new FileInfo(path).Length > Wire.HeaderLimit) throw new UnsupportedCompile("Native PCH producer has not been recorded");
            var record = JsonSerializer.Deserialize<ProducerRecord>(File.ReadAllText(path), Wire.Json) ?? throw new UnsupportedCompile("Invalid native PCH producer");
            if (record.Compiler != config.RealCompiler || !record.Directory.Equals(cwd, StringComparison.OrdinalIgnoreCase) ||
                record.Recipe.Header != invocation.PchHeader) throw new UnsupportedCompile("Native PCH producer context differs");
            if (Wire.HashFile(Path.GetFullPath(record.Recipe.Source, cwd)) != record.SourceHash) throw new UnsupportedCompile("PCH producer source changed");
            producer = record.Recipe;
        }
        // The declared inventory must include sibling/parent-relative headers,
        // even when their directories are absent from /I and INCLUDE.
        var roots = inventoryRoots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in batch) roots.Add(Path.GetDirectoryName(item.Source)!);
        if (producer is not null) roots.Add(Path.GetDirectoryName(Path.GetFullPath(producer.Source, cwd))!);
        foreach (string option in invocation.PreprocessOptions)
        {
            string? value = option.StartsWith("/external:I", StringComparison.Ordinal) ? option[11..] :
                option.StartsWith("/I", StringComparison.Ordinal) ? option[2..] : null;
            if (value is not null) roots.Add(Path.GetFullPath(value, cwd));
            if (option.StartsWith("/FI", StringComparison.Ordinal) && Path.IsPathRooted(option[3..]))
                roots.Add(Path.GetDirectoryName(Path.GetFullPath(option[3..]))!);
        }
        if (!invocation.PreprocessOptions.Contains("/X"))
            foreach (string value in include.Split(';', StringSplitOptions.RemoveEmptyEntries)) roots.Add(Path.GetFullPath(value, cwd));
        roots = roots.Select(path => Path.TrimEndingDirectorySeparator(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string state = Path.GetFullPath(config.StateDirectory).TrimEnd('\\') + "\\";
        using (BuildTiming.Measure("native.snapshot.scan"))
        foreach (string root in roots.Order(StringComparer.OrdinalIgnoreCase))
        {
            // Automatic scope trusts what this project declares (/I and its MSBuild
            // IncludePath), which CL would read anyway. Only explicit configuration
            // restricts them. The drive and budget limits still apply.
            if (!config.NativeAutoScope && !inputRoots.Any(path => root.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)))
                throw new UnsupportedCompile("Native input root is outside configured project/SDK scope: " + root);
            if (root.TrimEnd('\\').Equals(Path.GetPathRoot(root)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new UnsupportedCompile("A whole drive cannot be a native snapshot root");
            MappedCompile.InputPath(root);
            if (!Directory.Exists(root)) continue;
            Wire.NoReparse(root);
            var directories = new Stack<string>(); directories.Push(root);
            while (directories.TryPop(out string? directory))
            {
                if (!visited.Add(directory)) continue;
                if (visited.Count > 65536) throw new UnsupportedCompile("Native directory budget exceeded");
                if (directory.StartsWith(state, StringComparison.OrdinalIgnoreCase)) continue;
                // Enumeration already returned each entry's attributes, length and
                // timestamp. The root and its ancestors were checked above, so a
                // per-entry ancestor walk would only repeat those system calls.
                foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    string child = entry.FullName;
                    if (entry is DirectoryInfo)
                    {
                        if (entry.Name is ".git" or ".svn" || child.StartsWith(state, StringComparison.OrdinalIgnoreCase)) continue;
                        RejectReparse(entry);
                        directories.Push(child);
                        continue;
                    }
                    if (BuildOutputs.Contains(entry.Extension)) continue;
                    bool artifact = artifactRoots.Any(path => child.StartsWith(path, StringComparison.OrdinalIgnoreCase));
                    bool header = SourceExtensions.Contains(entry.Extension) && !CppSources.Contains(entry.Extension);
                    if (artifact && !header && !batch.Any(x => child.Equals(x.Source, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (new[] { invocation.Object, invocation.PchFile, invocation.CompilerPdb, producer?.ObjectFile }
                        .Concat(batch.Select(x => x.Object)).Any(x => child.Equals(x, StringComparison.OrdinalIgnoreCase))) continue;
                    RejectReparse(entry);
                    selected[child] = (FileInfo)entry;
                    if (selected.Count > 65536) throw new UnsupportedCompile("Native input file-count budget exceeded at " + root);
                }
            }
        }
        string standardHeaders = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(config.RealCompiler)!, "../../..", "include")).TrimEnd('\\') + "\\";
        var compileSources = batch.Select(x => x.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (producer is not null) compileSources.Add(Path.GetFullPath(producer.Source, cwd));
        using var hashing = BuildTiming.Measure("native.snapshot.hash");
        string[] ordered = selected.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var inputs = new InputFile[ordered.Length];
        var index = NativeHashIndex.Load(Path.GetFullPath(config.StateDirectory));
        long total = 0;
        var contents = new HashSet<string>(StringComparer.Ordinal);
        bool overBudget = false;
        try
        {
            Parallel.For(0, ordered.Length, new ParallelOptions { MaxDegreeOfParallelism = HashConcurrency }, (i, loop) =>
            {
                string path = ordered[i];
                FileInfo metadata = selected[path];
                bool cpp = compileSources.Contains(path) || SourceExtensions.Contains(metadata.Extension) ||
                    metadata.Extension.Length == 0 && path.StartsWith(standardHeaders, StringComparison.OrdinalIgnoreCase);
                if (!cpp || metadata.Length > Wire.FileLimit)
                {
                    inputs[i] = new InputFile(MappedCompile.InputPath(path), metadata.Length, "", metadata.LastWriteTimeUtc.Ticks, Deferred: true);
                    return;
                }
                long length = metadata.Length, time = metadata.LastWriteTimeUtc.Ticks;
                if (!index.TryGet(path, length, time, out string hash))
                {
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(input));
                    length = input.Length;
                    long observed = File.GetLastWriteTimeUtc(path).Ticks;
                    if (observed == time && length == metadata.Length) index.Add(path, length, time, hash);
                    time = observed;
                }
                lock (contents)
                {
                    if (contents.Add(hash + "-" + time)) total += length;
                    if (total > Wire.NativeTotalLimit) { overBudget = true; loop.Stop(); }
                }
                inputs[i] = new InputFile(MappedCompile.InputPath(path), length, hash, time);
            });
        }
        catch (AggregateException e) when (e.InnerExceptions.Count > 0)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw(); }
        if (overBudget) throw new UnsupportedCompile("Native input byte budget exceeded");
        index.Save();
        return inputs;
    }
    private static void RejectReparse(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new Rejected("Reparse points are not allowed: " + entry.FullName);
    }
    public void Dispose() => ClientCache.Dispose();
}

namespace Dbe;

// One CL Task owns this cache. It never scans or modifies another build's data.
sealed class NativeClientCache : IDisposable
{
    private readonly string root;
    private readonly string evidence;
    private readonly Dictionary<string, string?> recorded = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly Dictionary<string, FileStream> pins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileStream> published = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> contexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeContext> definitions = new(StringComparer.Ordinal);
    private static readonly List<NativeClientCache> retained = [];
    private long bytes;
    internal NativeClientCache(string parent)
    {
        root = Path.Combine(Path.GetFullPath(parent), "native-client-cache", Guid.NewGuid().ToString("N"));
        evidence = Path.Combine(Path.GetFullPath(parent), "native-context-evidence");
    }
    internal bool Knows(string worker, string key) => contexts.ContainsKey(worker + ":" + key);
    internal void Remember(string worker, string key) => contexts.TryAdd(worker + ":" + key, 0);
    internal void Forget(string worker, string key) => contexts.TryRemove(worker + ":" + key, out _);
    internal NativeContext Validate(Request request)
    {
        lock (gate)
        {
            if (definitions.TryGetValue(request.ContextKey!, out var known) && request.Files is not null && known.Definition.Files!.SequenceEqual(request.Files))
            { _ = known.Expand(request with { Files = null }); return known; }
            var context = new NativeContext(request);
            if (definitions.Count < 64) definitions[context.Identity] = context;
            return context;
        }
    }
    internal void Record(Request request, string destination)
    {
        NativeContext context = Validate(request);
        lock (gate)
        {
            if (!recorded.TryGetValue(context.Identity, out string? file))
            {
                Directory.CreateDirectory(evidence); Wire.NoReparse(evidence);
                string candidate = Path.Combine(evidence, context.Identity + ".json");
                if (!File.Exists(candidate))
                {
                    var existing = Directory.EnumerateFiles(evidence, "*.json").Select(path => new FileInfo(path)).ToArray();
                    byte[] content = context.Encode();
                    if (existing.Length < 64 && existing.Sum(x => x.Length) + content.Length <= 1024L * 1024 * 1024)
                    {
                        string pending = candidate + "." + Guid.NewGuid().ToString("N");
                        try
                        {
                            File.WriteAllBytes(pending, content);
                            try { File.Move(pending, candidate); } catch (IOException) when (File.Exists(candidate)) { }
                        }
                        finally { if (File.Exists(pending)) File.Delete(pending); }
                    }
                }
                if (File.Exists(candidate))
                {
                    Wire.NoReparse(candidate);
                    if (Wire.HashFile(candidate) != context.Identity) throw new Rejected("Native context evidence changed");
                    file = candidate;
                }
                recorded.Add(context.Identity, file);
            }
            File.WriteAllText(destination, System.Text.Json.JsonSerializer.Serialize(new {
                kind = "native-invocation-evidence-v1", request.Version, request.Attempt, request.Toolchain, request.Source,
                request.Options, request.NativeInput, request.ContextKey, definitionFile = file, definitionRetained = file is not null,
                inputCount = request.Files!.Length
            }, Wire.Json));
        }
    }
    internal bool Restore(InputFile file, string destination)
    {
        lock (gate)
        {
            if (!pins.TryGetValue(file.Sha256, out var pin) || pin.Length != file.Length) return false;
            File.Copy(Path.Combine(root, file.Sha256 + ".pdb"), destination);
            return true;
        }
    }
    internal void Store(InputFile file, string source)
    {
        lock (gate)
        {
            if (pins.ContainsKey(file.Sha256) || pins.Count >= 64 || bytes + file.Length > 256L * 1024 * 1024) return;
            Directory.CreateDirectory(root); Wire.NoReparse(root);
            string destination = Path.Combine(root, file.Sha256 + ".pdb");
            File.Copy(source, destination);
            var pin = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (pin.Length != file.Length || Wire.HashFile(destination) != file.Sha256) throw new Rejected("Producer symbols changed before cache publication");
                pins.Add(file.Sha256, pin); bytes += file.Length;
            }
            catch { pin.Dispose(); throw; }
        }
    }
    internal void Publish(InputFile file, string source, string destination)
    {
        lock (gate)
        {
            if (published.ContainsKey(destination)) return;
            Wire.NoReparse(destination);
            if (!File.Exists(destination))
            {
                string pending = destination + "." + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(source, pending);
                    try { File.Move(pending, destination); }
                    catch (IOException) when (File.Exists(destination)) { }
                }
                finally { if (File.Exists(pending)) File.Delete(pending); }
            }
            var pin = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (pin.Length != file.Length || Wire.HashFile(destination) != file.Sha256) throw new Rejected("Published producer symbols do not match");
                published.Add(destination, pin);
            }
            catch { pin.Dispose(); throw; }
        }
    }
    public void Dispose()
    {
        try
        {
            foreach (var pin in pins.Values) pin.Dispose();
            foreach (var pin in published.Values) pin.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        catch { lock (retained) retained.Add(this); throw; }
    }
}

using System.Text;

namespace Dbe;

static partial class MsBuildAdapter
{
    // Compiler outputs must not inherit the per-object hash/attempt nesting.
    // MSVC's MP output path fails beyond MAX_PATH even with a \\?\ prefix.
    // Keep metadata in the attempt, but own one shallow output directory for
    // the complete native job. An unconfirmed job retains its marker/output.
    private sealed class LocalOutput : IDisposable
    {
        internal string Root { get; }
        private readonly string id = Guid.NewGuid().ToString("N");
        private readonly FileStream marker;
        private bool cleanupConfirmed = true;
        internal LocalOutput(AdapterConfig config, string root, string attemptRoot, IEnumerable<string> names)
        {
            Root = Path.GetFullPath(Path.Combine(root, config.StateDirectory, "local", id));
            foreach (string name in names)
                if (Path.Combine(Root, name).Length >= 260)
                    throw new Rejected("Local compiler output exceeds the MSVC path limit; select a shorter stateDirectory");
            Wire.NoReparse(Root);
            if (Directory.Exists(Root)) throw new Rejected("Local output directory already exists");
            Directory.CreateDirectory(Root);
            marker = new FileStream(Path.Combine(Root, ".owner"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            try
            {
                marker.Write(Encoding.UTF8.GetBytes(id + "\n" + attemptRoot)); marker.Flush(true);
                File.WriteAllText(Path.Combine(attemptRoot, "local-output.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { id, path = Root }, Wire.Json));
            }
            catch { marker.Dispose(); throw; }
        }
        internal void BeginExecution() => cleanupConfirmed = false;
        internal void CompleteExecution(Execution result) => cleanupConfirmed = result.Kind != 5;
        internal void Publish(string name, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Copy across volumes if necessary, then rename on the destination
            // volume. A fixed temporary basename avoids extending long names.
            string pending = Path.Combine(Path.GetDirectoryName(destination)!, ".dbe-" + id + ".obj");
            Wire.NoReparse(pending); Wire.NoReparse(destination);
            bool copied = false;
            try
            {
                File.Copy(Path.Combine(Root, name), pending, false); copied = true;
                File.Move(pending, destination, true);
            }
            finally { if (copied && File.Exists(pending)) File.Delete(pending); }
        }
        public void Dispose()
        {
            marker.Dispose();
            if (cleanupConfirmed) { Wire.NoReparse(Root); Directory.Delete(Root, true); }
        }
    }
    private sealed record OwnedAttempt(string ObjectState, string Root, string Id, FileStream Ownership) : IDisposable
    {
        public void Dispose() => Ownership.Dispose();
    }
    private static OwnedAttempt CreateAttempt(AdapterConfig config, string root, ClInvocation invocation)
    {
        string state = Path.GetFullPath(Path.Combine(root, config.StateDirectory));
        Wire.NoReparse(state); Wire.NoReparse(invocation.Object); Directory.CreateDirectory(state);
        string objectId = Wire.Hash(Encoding.UTF8.GetBytes(invocation.Object.ToUpperInvariant()));
        string objectState = Path.Combine(state, objectId); Directory.CreateDirectory(objectState);
        var ownership = new FileStream(Path.Combine(objectState, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            TrimHistory(objectState, objectId);
            string id = Guid.NewGuid().ToString("N"), attemptRoot = Path.Combine(objectState, id);
            Directory.CreateDirectory(Path.Combine(attemptRoot, "inputs"));
            File.WriteAllText(Path.Combine(attemptRoot, ".dbe-owned"), objectId + "\n" + id);
            return new OwnedAttempt(objectState, attemptRoot, id, ownership);
        }
        catch { ownership.Dispose(); throw; }
    }
    private static List<string> LocalCommand(ClInvocation invocation, string output, int slots, IEnumerable<string> sources)
    {
        var options = new List<string>();
        // ParseBatch owns this representation: common options followed by one
        // absolute source. Preserve /Fd and /Yu; only the driver fan-out changes.
        for (int i = 0; i < invocation.LocalOptions.Length - 1; i++)
        {
            string original = invocation.LocalOptions[i], arg = original.StartsWith('-') ? "/" + original[1..] : original;
            if (arg.StartsWith("/Fo", StringComparison.Ordinal)) { if (arg.Length == 3) i++; continue; }
            if (arg.StartsWith("/MP", StringComparison.Ordinal)) continue;
            options.Add(original);
        }
        options.AddRange(["/MP" + slots, "/Fo" + output]); options.AddRange(sources); return options;
    }
    private static async Task<int> CompileLocalBatch(AdapterConfig config, string root, ClInvocation[] sources,
        HostScheduler.Lease placement, CancellationToken token)
    {
        // One native job and one exclusive compiler-PDB lease own the whole MP
        // group. Independent jobs must not kill a shared MSPDBSRV underneath
        // each other; simply removing the PDB lock would violate that lifetime.
        var attempts = new List<OwnedAttempt>();
        try
        {
            foreach (var source in sources) attempts.Add(CreateAttempt(config, root, source));
            using var output = new LocalOutput(config, root, attempts[0].Root, sources.Select(x => Path.GetFileName(x.Object)));
            string staging = output.Root;
            string response = Path.Combine(attempts[0].Root, "scheduled-batch.rsp");
            File.WriteAllText(response, string.Join(' ', LocalCommand(sources[0], staging + Path.DirectorySeparatorChar,
                placement.SlotCost, sources.Select(x => x.Source)).Select(ClInvocation.Quote)), Encoding.Unicode);
            for (int i = 0; i < sources.Length; i++) Event(attempts[i].Root, "local-batch-running", sources[i], Environment.MachineName);
            var result = await ExecuteAsync(config, "@" + ClInvocation.Quote(response), token, placement,
                placement.MemoryMiB, output);
            Emit(result);
            if (result.Kind != 0 || result.ExitCode != 0)
            {
                for (int i = 0; i < sources.Length; i++)
                {
                    DeleteObject(sources[i].Object);
                    Record(attempts[i].Root, "local-compile-error", sources[i], null, null);
                    Event(attempts[i].Root, "local-failed", sources[i], Environment.MachineName);
                }
                return result.Kind == 0 ? unchecked((int)result.ExitCode) : Fail("Local batch supervisor kind=" + result.Kind);
            }
            token.ThrowIfCancellationRequested();
            foreach (var source in sources)
                if (!File.Exists(Path.Combine(staging, Path.GetFileName(source.Object)))) throw new Rejected("Local batch produced no object: " + source.Source);
            for (int i = 0; i < sources.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                output.Publish(Path.GetFileName(sources[i].Object), sources[i].Object);
                File.Delete(Path.Combine(attempts[i].ObjectState, "object.json"));
                Record(attempts[i].Root, "local-completed", sources[i], null, null);
                Event(attempts[i].Root, "local-completed", sources[i], Environment.MachineName);
            }
            return 0;
        }
        catch { foreach (var source in sources) DeleteObject(source.Object); throw; }
        finally { foreach (var attempt in attempts) attempt.Dispose(); }
    }
}

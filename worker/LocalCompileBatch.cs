using System.Text;

namespace Dbe;

static partial class MsBuildAdapter
{
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
        HostScheduler.Lease placement, HostScheduler scheduler, CancellationToken token)
    {
        // One native job and one exclusive compiler-PDB lease own the whole MP
        // group. Independent jobs must not kill a shared MSPDBSRV underneath
        // each other; simply removing the PDB lock would violate that lifetime.
        var attempts = new List<OwnedAttempt>();
        try
        {
            foreach (var source in sources) attempts.Add(CreateAttempt(config, root, source));
            string staging = Path.Combine(attempts[0].Root, "batch-output"); Directory.CreateDirectory(staging);
            string response = Path.Combine(attempts[0].Root, "scheduled-batch.rsp");
            File.WriteAllText(response, string.Join(' ', LocalCommand(sources[0], staging + Path.DirectorySeparatorChar,
                placement.SlotCost, sources.Select(x => x.Source)).Select(ClInvocation.Quote)), Encoding.Unicode);
            for (int i = 0; i < sources.Length; i++) Event(attempts[i].Root, "local-batch-running", sources[i], Environment.MachineName);
            var result = await ExecuteAsync(config, "@" + ClInvocation.Quote(response), token, placement,
                placement.MemoryMiB);
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
                Directory.CreateDirectory(Path.GetDirectoryName(sources[i].Object)!);
                string pending = sources[i].Object + ".dbe-" + attempts[i].Id;
                try
                {
                    // State and project may be on different volumes. Stage
                    // beside the object before its atomic publication.
                    File.Move(Path.Combine(staging, Path.GetFileName(sources[i].Object)), pending);
                    File.Move(pending, sources[i].Object, true);
                }
                finally { if (File.Exists(pending)) File.Delete(pending); }
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

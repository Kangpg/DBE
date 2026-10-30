using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Dbe;

sealed record HostPolicy(int LocalSlots = 4, int RemoteSlots = 2, int LocalMemoryMiB = 4096,
    int ReserveMemoryMiB = 8192, int WaitSeconds = 1200, int CpuBusyPercent = 85, int LinkMemoryMiB = 0, int LocalBatchSlots = 1,
    int PreprocessMemoryMiB = 0, int LocalBatchMemoryMiB = 0);

// A shared policy directory is the ownership scope, across adapter processes.
// Occupied files survive owner death. A released OS lock is NOT cleanup proof.
sealed class HostScheduler : IDisposable
{
    private readonly FileStream policyPin;
    private readonly string root;
    private readonly RemoteTarget[] workers;
    private int nextWorker;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> localPdbWaiters = new(StringComparer.Ordinal);
    internal sealed record RemoteTarget(string Key, string ClientPath);
    internal int PipelineCapacity => Policy.RemoteSlots * 2 * workers.Length;
    private ulong lastIdle, lastTotal;
    private long lastSample;
    private double? cpuBusy;
    internal HostPolicy Policy { get; }
    internal HostScheduler(string policyPath, string clientPath) : this(policyPath, new[] { clientPath }) { }
    internal HostScheduler(string policyPath, IEnumerable<string> clientPaths)
    {
        policyPath = Path.GetFullPath(policyPath); Wire.NoReparse(policyPath);
        policyPin = new FileStream(policyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (policyPin.Length > Wire.HeaderLimit) throw new Rejected("Host policy too large");
            Policy = JsonSerializer.Deserialize<HostPolicy>(policyPin, Wire.Json) ?? throw new Rejected("Missing host policy");
            if (Policy.LocalSlots < 1 || Policy.LocalSlots >= Environment.ProcessorCount || Policy.RemoteSlots is < 1 or > 2 ||
                Policy.LocalMemoryMiB is < 256 or > 8192 || Policy.ReserveMemoryMiB < 1024 || Policy.WaitSeconds is < 1 or > 3600 || Policy.CpuBusyPercent is < 1 or > 100 ||
                Policy.LinkMemoryMiB < 0 || Policy.LinkMemoryMiB > Policy.LocalSlots * Policy.LocalMemoryMiB)
                throw new Rejected("Invalid host policy; leave CPU and memory for the owner");
            // Native supervisor permits eight processes; leave space for the CL
            // driver and compiler service around at most four /MP workers.
            if (Policy.LocalBatchSlots < 1 || Policy.LocalBatchSlots > Math.Min(4, Policy.LocalSlots)) throw new Rejected("Invalid local compile batch size");
            if (Policy.PreprocessMemoryMiB != 0 && Policy.PreprocessMemoryMiB < 256 || Policy.PreprocessMemoryMiB > Policy.LocalMemoryMiB ||
                Policy.LocalBatchMemoryMiB < 0 || Policy.LocalBatchMemoryMiB > Policy.LocalSlots * Policy.LocalMemoryMiB)
                throw new Rejected("Invalid stage memory budget");
            root = Path.Combine(Path.GetDirectoryName(policyPath)!, "leases");
            Wire.NoReparse(root); Directory.CreateDirectory(root);
            workers = clientPaths.Select(path => {
                var (client, _) = Identity.ReadConfig<ClientConfig>(path);
                if (!Wire.ValidHash(client.WorkerPin)) throw new Rejected("Invalid Worker identity");
                return new RemoteTarget("remote-" + client.WorkerPin.ToUpperInvariant(), Path.GetFullPath(path));
            }).ToArray();
            if (workers.Length == 0 || workers.Select(x => x.Key).Distinct().Count() != workers.Length)
                throw new Rejected("Workers must have distinct certificate identities");
            ReadCpu(out lastIdle, out lastTotal); lastSample = Stopwatch.GetTimestamp();
        }
        catch { policyPin.Dispose(); throw; }
    }
    internal async Task<Lease> Acquire(bool localOnly, string source, CancellationToken token, string? pdb = null, int slotCost = 1, int memoryMiB = 0)
    {
        // A batch's required local fallbacks must drain before this same
        // scheduler sends another MP group through their shared PDB. This is
        // local batch starvation prevention, not cross-user fairness.
        bool waiting = localOnly && pdb is not null;
        if (waiting) localPdbWaiters.AddOrUpdate(pdb!, 1, (_, count) => count + 1);
        try { return await AcquireCore(true, localOnly, workers, false, source, token, pdb, slotCost, memoryMiB: memoryMiB); }
        finally { if (waiting) localPdbWaiters.AddOrUpdate(pdb!, 0, (_, count) => count - 1); }
    }

    // Two bounded stages: at most one prepared successor per remote execution
    // slot. Preparation tickets include requests already in flight, so retained
    // prepared inputs cannot grow with the number of translation units.
    internal Task<Lease> AcquireCompile(string source, CancellationToken token, string? pdb, int localSlots = 1) =>
        AcquireCore(true, false, workers, true, source, token, pdb, localSlots, true);

    internal Task<Lease> AcquireRemoteRequest(string source, CancellationToken token, string? clientPath = null) =>
        AcquireCore(false, false, new[] { clientPath is null ? workers[0] : workers.Single(x => x.ClientPath == clientPath) }, false, source, token, null, 1);

    private async Task<Lease> AcquireCore(bool allowLocal, bool localOnly, RemoteTarget[] candidates, bool pipeline,
        string source, CancellationToken token, string? pdb, int slotCost, bool flexibleLocal = false, int memoryMiB = 0)
    {
        if (slotCost < 1 || slotCost > Policy.LocalSlots || (!localOnly && slotCost != 1 && !flexibleLocal)) throw new Rejected("Invalid local reservation cost");
        if (memoryMiB < 0 || memoryMiB > Policy.LocalSlots * Policy.LocalMemoryMiB) throw new Rejected("Invalid memory reservation");
        var watch = Stopwatch.StartNew();
        long reported = 0;
        int start = (Interlocked.Increment(ref nextWorker) & int.MaxValue) % candidates.Length;
        if (cpuBusy is null) await Task.Delay(Math.Max(1, 100 - (int)Stopwatch.GetElapsedTime(lastSample).TotalMilliseconds), token);
        for (;;)
        {
            token.ThrowIfCancellationRequested();
            Lease? lease = null;
            string reason = "remote capacity";
            bool yieldPdb = flexibleLocal && pdb is not null && localPdbWaiters.TryGetValue(pdb, out int waiters) && waiters > 0;
            if (yieldPdb) reason = "queued local PDB consumer";
            if (allowLocal && !yieldPdb)
            {
                // A fast sequence of local MP groups must not repeatedly take
                // the last slot away from already queued remote preprocessing.
                int eligible = flexibleLocal && Policy.LocalBatchSlots > 1 ? Math.Max(1, Policy.LocalSlots - 1) : Policy.LocalSlots;
                for (int cost = Math.Min(slotCost, eligible); cost >= (flexibleLocal ? 1 : slotCost); cost--) {
                    int memory = memoryMiB > 0 ? memoryMiB : flexibleLocal && cost > 1 && Policy.LocalBatchMemoryMiB > 0
                        ? Policy.LocalBatchMemoryMiB : cost * Policy.LocalMemoryMiB;
                    if ((lease = TryTake("local", eligible, true, source, pdb, cost, memory, out reason)) is not null) break;
                }
            }
            if (lease is not null) return lease;
            if (!localOnly)
                for (int i = 0; i < candidates.Length; i++)
                {
                    var worker = candidates[(start + i) % candidates.Length];
                    lease = TryTake(worker.Key + (pipeline ? "-pipeline" : ""), Policy.RemoteSlots * (pipeline ? 2 : 1), false, source, null, 1, 0, out _, worker);
                    if (lease is not null) return lease;
                }
            if (watch.Elapsed.TotalSeconds >= Policy.WaitSeconds) throw new Rejected("Host admission timed out; inspect memory and retained leases in " + root);
            if (watch.ElapsedMilliseconds - reported >= 5000) {
                Console.WriteLine("DBE: admission-wait " + reason + " source=" + source);
                reported = watch.ElapsedMilliseconds;
            }
            await Task.Delay(100, token);
        }
    }
    internal Task<Lease> AcquireLocalFallback(Lease remote, string source, CancellationToken token, string? pdb)
    {
        if (remote.IsLocal) throw new InvalidOperationException("Fallback requires a remote reservation");
        // The remote attempt has ended (or never started). Waiting for a local
        // PDB must not reserve capacity on an otherwise idle Worker.
        remote.Dispose();
        return Acquire(true, source, token, pdb);
    }
    internal int ReservedMemoryMiB()
    {
        int reserved = 0;
        for (int i = 0; i < Policy.LocalSlots; i++) {
            string path = Path.Combine(root, $"local-{i}.lease");
            Wire.NoReparse(path);
            try {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (file.Length == 0) continue;
                if (file.Length > Wire.HeaderLimit) throw new Rejected("Oversized retained memory reservation");
                try {
                    using var record = JsonDocument.Parse(file);
                    // Older runtimes reserve one LocalMemoryMiB per slot.
                    int memory = record.RootElement.TryGetProperty("memoryPerSlotMiB", out var value) ? value.GetInt32() : Policy.LocalMemoryMiB;
                    if (memory < 1 || memory > Policy.LocalSlots * Policy.LocalMemoryMiB) throw new Rejected("Invalid retained memory reservation");
                    reserved = checked(reserved + memory);
                }
                catch (JsonException) when (file.Length == 0) { /* confirmed cleanup truncated the marker */ }
                catch (JsonException) { throw new Rejected("Malformed retained memory reservation: " + path); }
            }
            catch (FileNotFoundException) { }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { reserved += Policy.LocalMemoryMiB; }
        }
        return reserved;
    }
    private Lease? TryTake(string pool, int count, bool local, string source, string? pdb, int slotCost, int memoryMiB, out string reason, RemoteTarget? worker = null)
    {
        reason = "slot capacity";
        using var gate = TryOpen(Path.Combine(root, "admission.lock"));
        if (gate is null) return null;
        if (local)
        {
            if (Stopwatch.GetElapsedTime(lastSample).TotalMilliseconds >= 100)
            {
                ReadCpu(out ulong idle, out ulong total);
                cpuBusy = total > lastTotal ? 100d * (1d - Math.Min(idle - lastIdle, total - lastTotal) / (double)(total - lastTotal)) : null;
                lastIdle = idle; lastTotal = total; lastSample = Stopwatch.GetTimestamp();
            }
            if (cpuBusy is null || cpuBusy >= Policy.CpuBusyPercent) { reason = "CPU owner headroom"; return null; }
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory)) throw new Rejected("Cannot determine available host memory");
            int reserved = ReservedMemoryMiB();
            ulong required = (ulong)(Policy.ReserveMemoryMiB + reserved + memoryMiB) * 1024 * 1024;
            ulong available = Math.Min(memory.AvailablePhysical, memory.AvailablePageFile);
            if (available < required) { reason = $"memory available={available / 1048576}MiB required={required / 1048576}MiB (headroom={Policy.ReserveMemoryMiB}, reserved={reserved}, next={memoryMiB})"; return null; }
        }
        FileStream? resource = null;
        if (pdb is not null)
        {
            string path = Path.Combine(root, "pdb-" + Wire.Hash(Encoding.UTF8.GetBytes(pdb)) + ".lease");
            resource = TryOpen(path);
            if (resource is null) { reason = "compiler PDB in use"; return null; }
            if (resource.Length != 0) { resource.Dispose(); throw new Rejected("Unconfirmed PDB owner cleanup: " + path); }
        }
        var files = new List<FileStream>();
        int firstIndex = -1;
        try { for (int i = 0; i < count; i++)
        {
            string path = Path.Combine(root, $"{pool}-{i}.lease");
            FileStream? file = TryOpen(path);
            if (file is null) continue;
            if (file.Length != 0)
            {
                file.Dispose();
                throw new Rejected("Unconfirmed previous owner cleanup; retained lease: " + path);
            }
            files.Add(file);
            if (firstIndex < 0) firstIndex = i;
            if (files.Count == slotCost) break;
        }
        if (files.Count != slotCost) return null;
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(new { source, local, slotCost, memoryPerSlotMiB = (memoryMiB + slotCost - 1) / slotCost, process = Environment.ProcessId, started = DateTimeOffset.UtcNow }, Wire.Json);
        foreach (var file in files) { file.Write(data); file.Flush(true); }
        if (resource is not null) { resource.Write(data); resource.Flush(true); }
        var result = new Lease(files.ToArray(), local, firstIndex, resource, worker, memoryMiB);
        files.Clear(); resource = null; return result;
        } finally { foreach (var file in files) file.Dispose(); resource?.Dispose(); }
    }
    private static FileStream? TryOpen(string path)
    {
        Wire.NoReparse(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return null; }
    }
    internal sealed class Lease(FileStream[] files, bool local, int index, FileStream? resource, RemoteTarget? worker, int memoryMiB) : IDisposable
    {
        private bool confirmed = true;
        private bool disposed;
        internal bool IsLocal => local;
        internal string? ClientPath => worker?.ClientPath;
        internal int Index => index;
        internal int SlotCost => files.Length;
        internal int MemoryMiB => memoryMiB;
        internal void BeginExecution() => confirmed = false;
        internal void ConfirmCleanup() => confirmed = true;
        public void Dispose()
        {
            if (disposed) return;
            if (confirmed) { foreach (var file in files) {file.SetLength(0); file.Flush(true);} if (resource is not null) { resource.SetLength(0); resource.Flush(true); } }
            resource?.Dispose();
            foreach (var file in files) file.Dispose();
            disposed = true;
        }
    }
    public void Dispose() => policyPin.Dispose();
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    private static void ReadCpu(out ulong idle, out ulong total)
    {
        if (!GetSystemTimes(out idle, out ulong kernel, out ulong user)) throw new Rejected("Cannot read host CPU load");
        total = kernel + user;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}

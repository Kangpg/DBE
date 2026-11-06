using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Dbe;

// Legacy local memory fields reserve admission capacity. Opt-in native admission
// bounds owned Jobs independently of /MP fan-out; preparation reserves spare
// commit. WorkerConfig alone owns incoming hard Job memory limits.
sealed record HostPolicy(int LocalSlots = 4, int RemoteSlots = 2, int LocalMemoryMiB = 4096,
    int ReserveMemoryMiB = 8192, int WaitSeconds = 1200, int CpuBusyPercent = 85, int LinkMemoryMiB = 0, int LocalBatchSlots = 1,
    int PreprocessMemoryMiB = 0, int LocalBatchMemoryMiB = 0, bool PreserveLocalParallelism = false, int NativeLocalJobs = 4, int LocalFirstFactor = 4, int AheadMinimumFreeMiB = 8192);

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
    // A native MP Job cannot give back sources it has claimed. Its size decides
    // how much work the Workers can still reach while that Job runs, so divide
    // pending work by capacity. Logical processors overstate SMT throughput.
    internal double RemoteShare
    {
        get
        {
            int remote = Policy.RemoteSlots * workers.Length;
            return remote / (double)(remote + Math.Max(1, Environment.ProcessorCount / 2));
        }
    }
    // Repeating this as Jobs finish converges on the remaining work instead of
    // trusting one estimate of Worker speed: an idle Worker leaves more for the
    // next Job, and a Job that finishes early takes its share of what remains.
    internal static int LocalGroupSize(int pending, int parallelism, double remoteShare) =>
        Math.Clamp(pending - (int)Math.Ceiling(pending * remoteShare), parallelism, parallelism * 16);
    // Compile-ahead adds concurrent compilers on top of the user's work and is only an
    // optimization, so it gives way under memory pressure. Native compilation is unaffected.
    internal static bool AheadMustYield(ulong availableBytes, int minimumFreeMiB) => availableBytes < (ulong)minimumFreeMiB * 1024 * 1024;
    internal bool AheadMustYieldNow()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return !GlobalMemoryStatusEx(ref memory) || AheadMustYield(memory.AvailablePhysical, Policy.AheadMinimumFreeMiB);
    }
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
            if (Policy.LocalSlots < 1 || Policy.LocalSlots > Environment.ProcessorCount - (Policy.PreserveLocalParallelism ? 0 : 1) || Policy.RemoteSlots is < 1 or > 2 ||
                Policy.LocalMemoryMiB is < 256 or > 8192 || Policy.ReserveMemoryMiB < 1024 || Policy.WaitSeconds is < 1 or > 3600 || Policy.CpuBusyPercent is < 1 or > 100 ||
                Policy.LinkMemoryMiB < 0 || Policy.LinkMemoryMiB > Policy.LocalSlots * Policy.LocalMemoryMiB)
                throw new Rejected("Invalid host policy; leave CPU and memory for the owner");
            // Legacy policy remains conservative. Native initiator execution
            // has a separate owner and can admit the caller's full fan-out.
            if (Policy.PreserveLocalParallelism && (Policy.NativeLocalJobs < 1 || Policy.NativeLocalJobs > Environment.ProcessorCount))
                throw new Rejected("Invalid native initiator Job budget");
            if (Policy.LocalBatchSlots < 1 || Policy.LocalBatchSlots > (Policy.PreserveLocalParallelism ? Policy.LocalSlots : Math.Min(4, Policy.LocalSlots))) throw new Rejected("Invalid local compile batch size");
            if (Policy.LocalFirstFactor is < 1 or > 64) throw new Rejected("Invalid local-first budget");
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
        AcquireCore(true, false, workers, true, source, token, pdb, localSlots, true, stage: "placement");

    internal Task<Lease> AcquirePreprocess(string source, CancellationToken token) =>
        AcquireCore(true, true, workers, false, source + " [preprocess]", token, memoryMiB: Policy.PreprocessMemoryMiB, stage: "prep");

    internal Task<Lease> AcquireRemoteRequest(string source, CancellationToken token, string? clientPath = null) =>
        AcquireCore(false, false, new[] { clientPath is null ? workers[0] : workers.Single(x => x.ClientPath == clientPath) }, false, source, token, stage: "request");

    private async Task<Lease> AcquireCore(bool allowLocal, bool localOnly, RemoteTarget[] candidates, bool pipeline,
        string source, CancellationToken token, string? pdb = null, int slotCost = 1, bool flexibleLocal = false, int memoryMiB = 0, string stage = "local")
    {
        if (slotCost < 1 || slotCost > Policy.LocalSlots || (!localOnly && slotCost != 1 && !flexibleLocal)) throw new Rejected("Invalid local reservation cost");
        if (memoryMiB < 0 || memoryMiB > Policy.LocalSlots * Policy.LocalMemoryMiB) throw new Rejected("Invalid memory reservation");
        var watch = Stopwatch.StartNew();
        long reported = 0;
        using var timing = BuildTiming.Enabled ? new BuildTiming.Wait() : null;
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
                if (Policy.PreserveLocalParallelism)
                {
                    // Native /MP is internal to one Job, not an atomic request
                    // for every machine core. PCH work in another project must
                    // not leave the remaining cores idle waiting for that Job.
                    bool preparation = stage == "prep";
                    int memory = memoryMiB > 0 ? memoryMiB : slotCost > 1 && Policy.LocalBatchMemoryMiB > 0
                        ? Policy.LocalBatchMemoryMiB : Policy.LocalMemoryMiB;
                    lease = TryTake(preparation ? "prepare" : "native", preparation ? 2 : Policy.NativeLocalJobs,
                        true, source, pdb, 1, memory, out reason, nativeAdmission: true, parallelism: slotCost);
                }
                else
                {
                    // Leave a legacy local slot for queued preprocessing.
                    int eligible = flexibleLocal && Policy.LocalBatchSlots > 1 ? Math.Max(1, Policy.LocalSlots - 1) : Policy.LocalSlots;
                    for (int cost = Math.Min(slotCost, eligible); cost >= (flexibleLocal ? 1 : slotCost); cost--) {
                        int memory = memoryMiB > 0 ? memoryMiB : flexibleLocal && cost > 1 && Policy.LocalBatchMemoryMiB > 0
                            ? Policy.LocalBatchMemoryMiB : cost * Policy.LocalMemoryMiB;
                        if ((lease = TryTake("local", eligible, true, source, pdb, cost, memory, out reason)) is not null) break;
                        if (reason != "slot capacity" && !reason.StartsWith("memory available=", StringComparison.Ordinal)) break;
                    }
                }
            }
            if (lease is not null) return lease;
            string localReason = reason;
            if (!localOnly)
                for (int i = 0; i < candidates.Length; i++)
                {
                    var worker = candidates[(start + i) % candidates.Length];
                    lease = TryTake(worker.Key + (pipeline ? "-pipeline" : ""), Policy.RemoteSlots * (pipeline ? 2 : 1), false, source, null, 1, 0, out _, worker);
                    if (lease is not null) return lease;
                }
            if (watch.Elapsed.TotalSeconds >= Policy.WaitSeconds) throw new Rejected("Host admission timed out; inspect memory and retained leases in " + root);
            if (timing is not null)
            {
                string localBlock = localReason.StartsWith("memory available=", StringComparison.Ordinal) ? "memory" : localReason;
                timing.Observe("wait." + stage + "." + (allowLocal ? localBlock : "remote capacity")
                    + (pipeline ? " + worker unavailable" : ""));
            }
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
    internal int ReservedMemoryMiB(bool includeNative = true)
    {
        int reserved = 0;
        foreach (string name in Enumerable.Range(0, Policy.LocalSlots).Select(i => $"local-{i}.lease")
            .Concat(Policy.PreserveLocalParallelism ? Enumerable.Range(0, 2).Select(i => $"prepare-{i}.lease") : [])
            .Concat(Policy.PreserveLocalParallelism && includeNative ? Enumerable.Range(0, Policy.NativeLocalJobs).Select(i => $"native-{i}.lease") : [])) {
            string path = Path.Combine(root, name);
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
    private Lease? TryTake(string pool, int count, bool local, string source, string? pdb, int slotCost, int memoryMiB, out string reason, RemoteTarget? worker = null, bool nativeAdmission = false, int parallelism = 0)
    {
        reason = "slot capacity";
        // Remote pools reserve exactly one exclusive file. They do not read
        // or change the local memory total, so they can progress independently
        // while another process checks local CPU/memory and multi-slot leases.
        using var gate = local ? TryOpen(Path.Combine(root, "admission.lock")) : null;
        if (local && gate is null) { reason = "admission gate"; return null; }
        FileStream? resource = null;
        var files = new List<FileStream>();
        try
        {
            if (pdb is not null)
            {
                string path = Path.Combine(root, "pdb-" + Wire.Hash(Encoding.UTF8.GetBytes(pdb)) + ".lease");
                resource = TryOpen(path);
                if (resource is null) { reason = "compiler PDB in use"; return null; }
                if (resource.Length != 0) throw new Rejected("Unconfirmed PDB owner cleanup: " + path);
            }
            if (local && (!nativeAdmission || pool == "prepare"))
            {
                if (!nativeAdmission && Stopwatch.GetElapsedTime(lastSample).TotalMilliseconds >= 100)
                {
                    ReadCpu(out ulong idle, out ulong total);
                    cpuBusy = total > lastTotal ? 100d * (1d - Math.Min(idle - lastIdle, total - lastTotal) / (double)(total - lastTotal)) : null;
                    lastIdle = idle; lastTotal = total; lastSample = Stopwatch.GetTimestamp();
                }
                if (!nativeAdmission && (cpuBusy is null || cpuBusy >= Policy.CpuBusyPercent)) { reason = "CPU owner headroom"; return null; }
                var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (!GlobalMemoryStatusEx(ref memory)) throw new Rejected("Cannot determine available host memory");
                int reserved = ReservedMemoryMiB(includeNative: !nativeAdmission);
                ulong required = (ulong)(Policy.ReserveMemoryMiB + reserved + memoryMiB) * 1024 * 1024;
                // The user's native build already delegates its own allocations to
                // Windows. Bound preparation separately against spare commit;
                // incoming Worker Jobs still enforce their fixed hard limits.
                ulong available = nativeAdmission ? memory.AvailablePageFile : Math.Min(memory.AvailablePhysical, memory.AvailablePageFile);
                if (available < required) { reason = $"memory available={available / 1048576}MiB required={required / 1048576}MiB (headroom={Policy.ReserveMemoryMiB}, reserved={reserved}, next={memoryMiB})"; return null; }
            }
            int firstIndex = -1;
            for (int i = 0; i < count; i++)
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
            var result = new Lease(files.ToArray(), local, firstIndex, resource, worker, memoryMiB, parallelism > 0 ? parallelism : slotCost);
            files.Clear(); resource = null; return result;
        } finally { foreach (var file in files) file.Dispose(); resource?.Dispose(); }
    }
    private static FileStream? TryOpen(string path)
    {
        Wire.NoReparse(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return null; }
    }
    internal sealed class Lease(FileStream[] files, bool local, int index, FileStream? resource, RemoteTarget? worker, int memoryMiB, int parallelism) : IDisposable
    {
        private bool confirmed = true;
        private bool disposed;
        internal bool IsLocal => local;
        internal string? ClientPath => worker?.ClientPath;
        internal int Index => index;
        internal int SlotCost => files.Length;
        internal int Parallelism => parallelism;
        internal int ReservationMiB => memoryMiB;
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

using System.Diagnostics;
using System.Text.Json;

namespace Dbe;

static class SchedulerSelfTest
{
    internal static async Task Probe(string policy, string client, string marker, int milliseconds, CancellationToken token)
    {
        using var scheduler = new HostScheduler(policy, client);
        using var lease = await scheduler.Acquire(true, "reservation probe", token);
        lease.BeginExecution();
        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("o"));
        await Task.Delay(milliseconds, token);
        lease.ConfirmCleanup();
        File.WriteAllText(marker + ".released", DateTimeOffset.UtcNow.ToString("o"));
    }
    internal static async Task Run(string client, string directory, string? secondClient = null)
    {
        if (Directory.Exists(directory)) throw new Rejected("Scheduler test directory must be new");
        Directory.CreateDirectory(directory);
        string policy = Path.Combine(directory, "host.json");
        File.WriteAllText(policy, JsonSerializer.Serialize(new HostPolicy(2, 2, 256, 1024, 2, CpuBusyPercent: 100), Wire.Json));
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks.Add(name); }
        using (var scheduler = new HostScheduler(policy, client))
        {
            using var first = await scheduler.Acquire(false, "first", default, "shared-pdb");
            using var second = await scheduler.Acquire(false, "second", default, "shared-pdb");
            using var third = await scheduler.Acquire(false, "third", default, "other-pdb");
            using var fourth = await scheduler.Acquire(false, "fourth", default);
            Check(first.IsLocal && !second.IsLocal && third.IsLocal && !fourth.IsLocal, "local-and-remote-placement-with-PDB-exclusion");
            using var cancelled = new CancellationTokenSource(150);
            try { using var extra = await scheduler.Acquire(false, "overflow", cancelled.Token); throw new Exception("Admission exceeded budget"); }
            catch (OperationCanceledException) { checks.Add("full-pools-wait-is-cancellable"); }
        }
        Check(Directory.GetFiles(Path.Combine(directory, "leases"), "*.lease").All(x => new FileInfo(x).Length == 0), "normal-reservations-release-exactly-once");
        using (var scheduler = new HostScheduler(policy, client))
        {
            using var pdbOwner = await scheduler.Acquire(true, "active local PCH consumer", default, "shared-pdb");
            using var fallbackA = await scheduler.Acquire(false, "unsupported relative include A", default, "shared-pdb");
            using var fallbackB = await scheduler.Acquire(false, "unsupported relative include B", default, "shared-pdb");
            Check(!fallbackA.IsLocal && !fallbackB.IsLocal, "fallback-fixture-reserves-both-remote-slots");
            using var cancel = new CancellationTokenSource();
            var waitingA = scheduler.AcquireLocalFallback(fallbackA, "fallback A", cancel.Token, "shared-pdb");
            var waitingB = scheduler.AcquireLocalFallback(fallbackB, "fallback B", cancel.Token, "shared-pdb");
            try
            {
                using var nextA = await scheduler.Acquire(false, "next remote A", default, "shared-pdb");
                using var nextB = await scheduler.Acquire(false, "next remote B", default, "shared-pdb");
                Check(!nextA.IsLocal && !nextB.IsLocal && !waitingA.IsCompleted && !waitingB.IsCompleted,
                    "local-PDB-wait-releases-both-remote-slots-before-fallback-runs");
            }
            finally
            {
                cancel.Cancel();
                foreach (var waiting in new[] { waitingA, waitingB })
                {
                    try { using var unexpected = await waiting; throw new Exception("Fallback bypassed active PDB owner"); }
                    catch (OperationCanceledException) { }
                }
            }
        }
        Check(Directory.GetFiles(Path.Combine(directory, "leases"), "*.lease").All(x => new FileInfo(x).Length == 0),
            "cancelled-fallback-and-outer-disposal-release-without-stale-markers");
        using (var scheduler = new HostScheduler(policy, client))
        {
            using var local = await scheduler.Acquire(true, "PDB owner", default, "pipeline-pdb");
            var tickets = new List<HostScheduler.Lease>();
            try
            {
                for (int i = 0; i < 4; i++) tickets.Add(await scheduler.AcquireCompile("pipeline " + i, default, "pipeline-pdb"));
                Check(tickets.All(x => !x.IsLocal), "two-running-plus-two-prepared-inputs-fit-bounded-pipeline");
                using var first = await scheduler.AcquireRemoteRequest("request A", default);
                using var second = await scheduler.AcquireRemoteRequest("request B", default);
                using (var cancel = new CancellationTokenSource(150))
                {
                    try { using var overflow = await scheduler.AcquireRemoteRequest("request C", cancel.Token); throw new Exception("Remote execution budget exceeded"); }
                    catch (OperationCanceledException) { checks.Add("preparation-does-not-expand-remote-execution-budget"); }
                }
                using (var cancel = new CancellationTokenSource(150))
                {
                    try { using var overflow = await scheduler.AcquireCompile("pipeline overflow", cancel.Token, "pipeline-pdb"); throw new Exception("Preparation budget exceeded"); }
                    catch (OperationCanceledException) { checks.Add("prepared-input-backlog-is-bounded-and-cancellable"); }
                }
                first.Dispose();
                using var successor = await scheduler.AcquireRemoteRequest("prepared successor", default);
                Check(!successor.IsLocal, "prepared-successor-dispatches-while-other-request-remains-active");
            }
            finally { foreach (var ticket in tickets) ticket.Dispose(); }
        }
        using (var scheduler = new HostScheduler(policy, client))
        {
            using var link = await scheduler.Acquire(true, "weighted link", default, slotCost: 2);
            Check(Directory.GetFiles(Path.Combine(directory, "leases"), "local-*.lease").Count(x => new FileInfo(x).Length > 0) == 2, "link-atomically-reserves-two-local-slots");
            using var cancelled = new CancellationTokenSource(150);
            try {using var extra = await scheduler.Acquire(true, "compile during link", cancelled.Token);throw new Exception("Weighted budget exceeded");}
            catch (OperationCanceledException) {checks.Add("weighted-link-excludes-overbudget-compile");}
        }
        if (secondClient is not null)
        {
            using (var scheduler = new HostScheduler(policy, new[] { client, secondClient }))
            {
                using var locals = await scheduler.Acquire(true, "local budget", default, slotCost: 2);
                var tickets = new List<HostScheduler.Lease>(); var requests = new List<HostScheduler.Lease>();
                try
                {
                    for (int i = 0; i < 8; i++) tickets.Add(await scheduler.AcquireCompile("two-Worker pipeline", default, null));
                    Check(tickets.GroupBy(x => x.ClientPath).Count() == 2 && tickets.GroupBy(x => x.ClientPath).All(x => x.Count() == 4), "two-workers-have-independent-bounded-pipelines");
                    foreach (string path in new[] { client, secondClient })
                        for (int i = 0; i < 2; i++) requests.Add(await scheduler.AcquireRemoteRequest("two-Worker request", default, Path.GetFullPath(path)));
                    Check(requests.GroupBy(x => x.ClientPath).All(x => x.Count() == 2), "four-requests-reserve-two-slots-on-each-worker");
                }
                finally { foreach (var request in requests) request.Dispose(); foreach (var ticket in tickets) ticket.Dispose(); }
            }
            try { using var duplicate = new HostScheduler(policy, new[] { client, client }); throw new Exception("Worker alias doubled capacity"); }
            catch (Rejected) { checks.Add("duplicate-worker-identity-cannot-multiply-slots"); }
        }
        File.WriteAllText(policy, JsonSerializer.Serialize(new HostPolicy(3, 2, 512, 1024, 2, CpuBusyPercent: 100, LocalBatchSlots: 3, PreprocessMemoryMiB: 256, LocalBatchMemoryMiB: 768), Wire.Json));
        using (var scheduler = new HostScheduler(policy, client))
        {
            using var group = await scheduler.AcquireCompile("native MP group", default, "shared MP PDB", 3);
            using var preparation = await scheduler.Acquire(true, "remote preprocessing", default, memoryMiB: scheduler.Policy.PreprocessMemoryMiB);
            using var other = await scheduler.AcquireCompile("next TU", default, "shared MP PDB", 3);
            Check(group.IsLocal && group.SlotCost == 2 && preparation.IsLocal && !other.IsLocal,
                "MP-group-reserves-its-cores-and-leaves-a-preprocessing-slot");
            Check(group.MemoryMiB == 768 && preparation.MemoryMiB == 256 && scheduler.ReservedMemoryMiB() == 1024,
                "CPU-slots-and-stage-memory-are-accounted-independently");
        }
        string malformed = Path.Combine(directory, "leases", "local-0.lease");
        using (var scheduler = new HostScheduler(policy, client)) {
            using var owner = await scheduler.AcquireCompile("MP PDB owner", default, "queued-pdb", 2);
            var waiting = scheduler.Acquire(true, "required local fallback", default, "queued-pdb");
            Check(!waiting.IsCompleted, "required-local-work-waits-for-current-PDB-owner");
            owner.Dispose();
            using var newcomer = await scheduler.AcquireCompile("new MP group", default, "queued-pdb", 2);
            using var admitted = await waiting;
            Check(!newcomer.IsLocal && admitted.IsLocal, "new-MP-groups-yield-to-already-waiting-local-PDB-work");
        }
        File.WriteAllText(malformed, "{invalid");
        using (var scheduler = new HostScheduler(policy, client)) {
            try { using var admission = await scheduler.Acquire(true, "malformed owner", default); throw new Exception("Malformed budget was ignored"); }
            catch (Rejected) { Check(File.ReadAllText(malformed) == "{invalid", "malformed-reservation-blocks-admission-and-is-retained"); }
        }
        File.WriteAllText(malformed, ""); // exact marker created by this test, no descendant execution
        File.WriteAllText(policy, JsonSerializer.Serialize(new HostPolicy(1, 1, 256, 1024, 3, CpuBusyPercent: 100), Wire.Json));
        Process Start(string marker, int duration)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "admission-probe", policy, client, marker, duration.ToString() }) start.ArgumentList.Add(arg);
            return Process.Start(start) ?? throw new Exception("Could not start reservation probe");
        }
        async Task WaitMarker(string path, Process process)
        {
            for (int i = 0; i < 100; i++) { if (File.Exists(path)) return; if (process.HasExited) break; await Task.Delay(50); }
            throw new Exception("Probe failed before marker: " + path);
        }
        string a = Path.Combine(directory, "a"), b = Path.Combine(directory, "b");
        using (var first = Start(a, 700))
        using (var second = Start(b, 150))
        {
            try
            {
                await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync());
                Check(first.ExitCode == 0 && second.ExitCode == 0, "independent-adapter-processes-complete");
                var intervals = new[] { a, b }.Select(x => (Start: DateTimeOffset.Parse(File.ReadAllText(x)), End: DateTimeOffset.Parse(File.ReadAllText(x + ".released")))).OrderBy(x => x.Start).ToArray();
                Check(intervals[0].End <= intervals[1].Start, "cross-process-local-budget-does-not-overlap");
            }
            finally { if (!first.HasExited) { first.Kill(); await first.WaitForExitAsync(); } if (!second.HasExited) { second.Kill(); await second.WaitForExitAsync(); } }
        }
        string crash = Path.Combine(directory, "crash");
        using (var owner = Start(crash, 30000))
        {
            try { await WaitMarker(crash, owner); }
            finally { if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); } }
        }
        using (var scheduler = new HostScheduler(policy, client))
        {
            try { using var lease = await scheduler.Acquire(true, "after-owner-death", default); throw new Exception("Reused unconfirmed resources"); }
            catch (Rejected e) when (e.Message.Contains("Unconfirmed")) { checks.Add("owner-death-retains-lease-and-blocks-reuse"); }
        }
        File.WriteAllText(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(new { checks, scope = "Real cross-process reservation tests; native descendant cleanup is tested separately by Worker self-test." }, Wire.Json));
        Console.WriteLine("Scheduler checks: " + checks.Count + " " + directory);
    }
}

using System.Text.Json;

namespace Dbe;

static partial class MsBuildAdapter
{
    private static async Task<int> RunNative(AdapterConfig config, string root, ClInvocation[] sources, CancellationToken token,
        HostScheduler scheduler, HostScheduler.Lease placement, Lazy<Compiler> compiler, NativeBuild inputs)
    {
        var attempts = new List<OwnedAttempt>();
        try
        {
            foreach (var source in sources) attempts.Add(CreateAttempt(config, root, source));
            return await RunNativeOwned(config, root, sources, attempts, token, scheduler, placement, compiler, inputs);
        }
        catch { for (int i = 0; i < attempts.Count; i++) DeleteObject(sources[i].Object); throw; }
        finally { foreach (var attempt in attempts) attempt.Dispose(); }
    }
    private static async Task<int> RunNativeOwned(AdapterConfig config, string root, ClInvocation[] sources,
        IReadOnlyList<OwnedAttempt> attempts, CancellationToken token, HostScheduler scheduler, HostScheduler.Lease placement,
        Lazy<Compiler> compiler, NativeBuild inputs)
    {
        var invocation = sources[0]; var owned = attempts[0];
        bool batch = sources.Length > 1;
        Request Manifest(string toolchain) => batch ? inputs.BatchManifest(sources, owned.Id, toolchain) : inputs.Manifest(invocation, owned.Id, toolchain);
        void Events(string kind, string machine) { for (int i = 0; i < sources.Length; i++) Event(attempts[i].Root, kind, sources[i], machine); }
        async Task<int> FallbackNative(string reason)
        {
            foreach (var source in sources) Console.WriteLine("DBE: native-local-fallback " + source.Source + ": " + reason);
            placement.Dispose();
            int parallelism = Math.Min(sources.Length, scheduler.Policy.LocalBatchSlots);
            if (scheduler.Policy.PreserveLocalParallelism) parallelism = Math.Min(parallelism, invocation.NativeParallelism());
            int memory = parallelism > 1 && scheduler.Policy.LocalBatchMemoryMiB > 0 ? scheduler.Policy.LocalBatchMemoryMiB : scheduler.Policy.LocalMemoryMiB;
            using var local = await scheduler.Acquire(true, invocation.Source, token, invocation.CompilerPdb, parallelism, memory);
            return batch ? await ExecuteLocalBatch(config, root, sources, attempts, local, token, "fallback")
                : await CompileLocal(config, root, invocation, owned.Root, owned.ObjectState, local, scheduler, token, "fallback");
        }
        var (configuration, clientRoot) = Identity.ReadConfig<ClientConfig>(placement.ClientPath ?? config.ClientConfig);
        using var client = new Client(configuration, clientRoot);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(180));
        Capabilities remote;
        Request manifest;
        try
        {
            remote = await client.Info(deadline.Token, batch ? 7 : 6);
            if (remote.MaxProtocol != (batch ? 7 : 6) || remote.DeferredInputs != true || remote.ReusablePch != true) return await FallbackNative("Worker reusable-input negotiation unavailable; update runtime");
            if (remote.Toolchain != compiler.Value.Identity) return await FallbackNative("Compiler identity mismatch");
            if (!compiler.Value.Version.StartsWith("19.44.", StringComparison.Ordinal)) return await FallbackNative("Native projection currently supports verified MSVC 19.44");
            using (var preparation = await scheduler.AcquirePreprocess(invocation.Source + " [native snapshot]", token))
            using (BuildTiming.Measure("native.snapshot"))
                manifest = Manifest(remote.Toolchain);
            if (JsonSerializer.SerializeToUtf8Bytes(manifest, Wire.Json).Length > Wire.HeaderLimit)
                return await FallbackNative("Native input manifest exceeds frame budget");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return await FallbackNative("Native preparation deadline expired"); }
        catch (Exception e) when (e is UnsupportedCompile or Rejected or IOException or System.Net.Sockets.SocketException or
            System.Security.Authentication.AuthenticationException)
        { return await FallbackNative("Native preparation unavailable: " + e.Message); }
        string output = Path.Combine(owned.Root, "result");
        inputs.ClientCache.Record(manifest, Path.Combine(owned.Root, "manifest.json"));
        Events("native-ready", remote.Machine);
        Reply reply;
        for (int round = 0; ; round++)
        {
            output = Path.Combine(owned.Root, "result-" + round);
            try
            {
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                using var request = await scheduler.AcquireRemoteRequest(invocation.Source, token, placement.ClientPath);
                deadline.CancelAfter(TimeSpan.FromSeconds(Wire.NativeRequestSeconds(remote.TimeoutSeconds)));
                Events("native-requested", remote.Machine);
                using var timing = BuildTiming.Measure("native.rpc");
                reply = await client.Compile(manifest, "", output, deadline.Token, nativePaths: true, cache: inputs.ClientCache);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return await FallbackNative("Native result deadline expired"); }
            catch (Exception e) when (e is Rejected or IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException)
            { return await FallbackNative("Native transport unavailable: " + e.Message); }
            if (reply.Status != "input-required") break;
            if (round >= 32) return await FallbackNative("Native additional-input round budget exceeded");
            try
            {
                inputs.Resolve(manifest, reply.RequiredInputs!);
                manifest = Manifest(remote.Toolchain);
            }
            catch (Exception e) when (e is UnsupportedCompile or Rejected or IOException or UnauthorizedAccessException)
            { return await FallbackNative("Native requested input unavailable: " + e.Message); }
            Console.WriteLine("DBE: native additional compile inputs " + reply.RequiredInputs!.Length);
        }
        foreach (string name in new[] { "stdout", "stderr" })
        {
            string path = Path.Combine(output, name + ".bin");
            if (File.Exists(path)) (name == "stdout" ? Console.OpenStandardOutput() : Console.OpenStandardError()).Write(File.ReadAllBytes(path));
        }
        if (reply.Status == "compile-error")
        {
            foreach (var source in sources) DeleteObject(source.Object);
            Events("native-compile-error", remote.Machine);
            return unchecked((int)(reply.ExitCode ?? 1));
        }
        if (reply.Status != "completed") return await FallbackNative("Native Worker returned " + reply.Status + ": " + reply.Message);
        string key = Wire.Hash(JsonSerializer.SerializeToUtf8Bytes(manifest with { Attempt = "" }, Wire.Json));
        for (int i = 0; i < sources.Length; i++)
        {
            Reply item = batch ? reply with { Output = reply.Outputs![i], Outputs = null } : reply;
            PublishRemote(sources[i], output, item, attempts[i].Id, attempts[i].ObjectState, key, token, inputs.ClientCache);
            Record(attempts[i].Root, "remote-completed", sources[i], remote, key);
            Event(attempts[i].Root, "remote-completed", sources[i], remote.Machine);
            Console.WriteLine("DBE: native-remote " + remote.Machine + " compiler=" + remote.CompilerVersion + " -> " + sources[i].Object);
        }
        return 0;
    }
}

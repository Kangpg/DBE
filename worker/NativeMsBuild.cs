using System.Text.Json;

namespace Dbe;

static partial class MsBuildAdapter
{
    private static async Task<int> RunNative(AdapterConfig config, string root, ClInvocation invocation, CancellationToken token,
        HostScheduler scheduler, HostScheduler.Lease placement, Lazy<Compiler> compiler, NativeBuild inputs)
    {
        using var owned = CreateAttempt(config, root, invocation);
        async Task<int> FallbackNative(string reason)
        {
            Console.WriteLine("DBE: native-local-fallback " + invocation.Source + ": " + reason);
            using var local = await scheduler.AcquireLocalFallback(placement, invocation.Source, token, invocation.CompilerPdb);
            return await CompileLocal(config, root, invocation, owned.Root, owned.ObjectState, local, scheduler, token, "fallback");
        }
        var (configuration, clientRoot) = Identity.ReadConfig<ClientConfig>(placement.ClientPath ?? config.ClientConfig);
        using var client = new Client(configuration, clientRoot);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(180));
        Capabilities remote;
        Request manifest;
        try
        {
            remote = await client.Info(deadline.Token, 6);
            if (remote.MaxProtocol != 6 || remote.DeferredInputs != true || remote.ReusablePch != true) return await FallbackNative("Worker reusable-input negotiation unavailable; update runtime");
            if (remote.Toolchain != compiler.Value.Identity) return await FallbackNative("Compiler identity mismatch");
            if (!compiler.Value.Version.StartsWith("19.44.", StringComparison.Ordinal)) return await FallbackNative("Native projection currently supports verified MSVC 19.44");
            using (var preparation = await scheduler.AcquirePreprocess(invocation.Source + " [native snapshot]", token))
            using (BuildTiming.Measure("native.snapshot"))
                manifest = inputs.Manifest(invocation, owned.Id, remote.Toolchain);
            if (JsonSerializer.SerializeToUtf8Bytes(manifest, Wire.Json).Length > Wire.HeaderLimit)
                return await FallbackNative("Native input manifest exceeds frame budget");
        }
        catch (Exception e) when (e is UnsupportedCompile or Rejected or IOException or System.Net.Sockets.SocketException or
            System.Security.Authentication.AuthenticationException)
        { return await FallbackNative("Native preparation unavailable: " + e.Message); }
        string output = Path.Combine(owned.Root, "result");
        inputs.ClientCache.Record(manifest, Path.Combine(owned.Root, "manifest.json"));
        Event(owned.Root, "native-ready", invocation, remote.Machine);
        Reply reply;
        for (int round = 0; ; round++)
        {
            output = Path.Combine(owned.Root, "result-" + round);
            try
            {
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                using var request = await scheduler.AcquireRemoteRequest(invocation.Source, token, placement.ClientPath);
                deadline.CancelAfter(TimeSpan.FromSeconds(Wire.NativeRequestSeconds(remote.TimeoutSeconds)));
                Event(owned.Root, "native-requested", invocation, remote.Machine);
                using var timing = BuildTiming.Measure("native.rpc");
                reply = await client.Compile(manifest, "", output, deadline.Token, nativePaths: true, cache: inputs.ClientCache);
            }
            catch (Exception e) when (e is Rejected or IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException)
            { return await FallbackNative("Native transport unavailable: " + e.Message); }
            if (reply.Status != "input-required") break;
            if (round >= 32) return await FallbackNative("Native additional-input round budget exceeded");
            try
            {
                inputs.Resolve(manifest, reply.RequiredInputs!);
                manifest = inputs.Manifest(invocation, owned.Id, remote.Toolchain);
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
            DeleteObject(invocation.Object);
            Event(owned.Root, "native-compile-error", invocation, remote.Machine);
            return unchecked((int)(reply.ExitCode ?? 1));
        }
        if (reply.Status != "completed") return await FallbackNative("Native Worker returned " + reply.Status + ": " + reply.Message);
        string key = Wire.Hash(JsonSerializer.SerializeToUtf8Bytes(manifest with { Attempt = "" }, Wire.Json));
        PublishRemote(invocation, output, reply, owned.Id, owned.ObjectState, key, token, inputs.ClientCache);
        Record(owned.Root, "remote-completed", invocation, remote, key);
        Event(owned.Root, "remote-completed", invocation, remote.Machine);
        Console.WriteLine("DBE: native-remote " + remote.Machine + " compiler=" + remote.CompilerVersion + " -> " + invocation.Object);
        return 0;
    }
}

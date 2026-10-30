using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Dbe;

static class SelfTest
{
    private static readonly List<string> checks = [];
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        checks.Add(name); Console.WriteLine("PASS " + name);
    }

    internal static async Task Run(string compilerPath, string childProbe, string directory)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory)) throw new Rejected("Test output must be new");
        Directory.CreateDirectory(directory);
        string pair = Path.Combine(directory, "pair");
        Identity.CreatePair(pair, compilerPath, "127.0.0.1");
        var (workerConfig, workerRoot) = Identity.ReadConfig<WorkerConfig>(Path.Combine(pair, "worker", "worker.json"));
        using var worker = new Worker(workerConfig with { Port = 0 }, workerRoot);
        worker.Start();
        Task server = worker.Serve();
        var (clientConfig, clientRoot) = Identity.ReadConfig<ClientConfig>(Path.Combine(pair, "client", "client.json"));
        clientConfig = clientConfig with { Port = worker.Port };
        using var client = new Client(clientConfig, clientRoot);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        CancellationToken token = deadline.Token;
        try
        {
            Capabilities info = await client.Info(token);
            Check(Wire.ValidHash(info.Toolchain) && info.Slots == 2, "mutual-tls-capabilities");
            string input = Path.Combine(directory, "input 한글"); Directory.CreateDirectory(input);
            File.WriteAllText(Path.Combine(input, "main.cpp"), "#include \"value.h\"\nextern \"C\" int dbe_probe(){return probe_value;}\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(input, "value.h"), "inline constexpr int probe_value=42;\n", new UTF8Encoding(false));
            Request Manifest() => new(1, "compile", Guid.NewGuid().ToString("N"), info.Toolchain, "main.cpp",
                new[] { "main.cpp", "value.h" }.Select(n => new InputFile(n, new FileInfo(Path.Combine(input, n)).Length, Wire.HashFile(Path.Combine(input, n)))).ToArray());
            string resultDir = Path.Combine(directory, "success");
            Reply reply = await client.Compile(Manifest(), input, resultDir, token);
            Check(reply.Status == "completed" && File.Exists(Path.Combine(resultDir, "result.obj")), "real-msvc-transfer-hash-object");
            VerifyObject(compilerPath, resultDir, 42);
            Check(true, "link-and-execute-remote-object-42");
            string symbolsDir = Path.Combine(directory, "v3-pdb");
            var symbolsRequest = Manifest() with { Version = 3, Files = [new InputFile("main.cpp", 0, "")], Options = ["/utf-8", "/Zi", "/JMC", "/Od", "/MDd"] };
            string prepared = Path.Combine(directory, "v3-input"); Directory.CreateDirectory(prepared);
            File.WriteAllText(Path.Combine(prepared, "main.cpp"), "extern \"C\" int dbe_probe(){return 42;}\n");
            symbolsRequest = symbolsRequest with { Files = [new InputFile("main.cpp", new FileInfo(Path.Combine(prepared, "main.cpp")).Length, Wire.HashFile(Path.Combine(prepared, "main.cpp")))] };
            Reply symbolReply = await client.Compile(symbolsRequest, prepared, symbolsDir, token);
            Check(symbolReply.Status == "completed" && symbolReply.Pdb is not null && File.Exists(Path.Combine(symbolsDir, symbolReply.Pdb.Path)), "v3-Zi-transfers-verified-PDB");
            VerifyObject(compilerPath, symbolsDir, 42);
            Check(true, "v3-PDB-links-after-worker-workspace-removed");
            File.WriteAllText(Path.Combine(input, "value.h"), "inline constexpr int probe_value=43;\n", new UTF8Encoding(false));
            string changed = Path.Combine(directory, "header-change");
            reply = await client.Compile(Manifest(), input, changed, token);
            Check(reply.Status == "completed", "changed-header-recompiled");
            VerifyObject(compilerPath, changed, 43);
            Check(true, "link-and-execute-updated-object-43");
            File.WriteAllText(Path.Combine(input, "value.h"), "#error DBE_REMOTE_ERROR_CHECK\n", new UTF8Encoding(false));
            string errorDir = Path.Combine(directory, "compile-error");
            reply = await client.Compile(Manifest(), input, errorDir, token);
            string diagnostic = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(errorDir, "stdout.bin"))) + Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(errorDir, "stderr.bin")));
            Check(reply.Status == "compile-error" && reply.ExitCode != 0 && diagnostic.Contains("DBE_REMOTE_ERROR_CHECK") && !File.Exists(Path.Combine(errorDir, "result.obj")), "real-compiler-error-preserved-no-object");

            async Task<string> Raw(Request request)
            {
                var c = await client.Connect(token); using var tcp = c.Tcp; using var tls = c.Tls;
                await Wire.Write(tls, request, token);
                return (await Wire.Read<Reply>(tls, token)).Status;
            }
            Request valid = Manifest();
            Check(await Raw(valid with { Toolchain = new string('0', 64) }) == "rejected", "toolchain-mismatch-rejected");
            foreach (string invalid in new[] { "../escape.cpp", "C:/escape.cpp", "a:stream", "CON.cpp", "trailing./x", "a\\b.cpp" })
                Check(await Raw(valid with { Source = invalid }) == "rejected", "reject-path-" + invalid);
            Check(await Raw(valid with { Files = [valid.Files![0], valid.Files![0] with { Path = "MAIN.cpp" }] }) == "rejected", "case-collision-rejected");
            Check(await Raw(valid with { Files = [valid.Files![0] with { Length = long.MaxValue }] }) == "rejected", "input-size-rejected-before-allocation");
            Check(await Raw(valid with { Version = 99 }) == "rejected", "unsupported-version-rejected");
            Check(await Raw(valid with { Version = 2, Options = ["/utf-8", "/FoC:/outside.obj"] }) == "rejected", "remote-output-option-rejected");
            {
                var c = await client.Connect(token); using var tcp = c.Tcp; using var tls = c.Tls;
                byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, int.MaxValue);
                await tls.WriteAsync(prefix, token);
                Check((await Wire.Read<Reply>(tls, token)).Status == "rejected", "oversized-frame-rejected");
            }
            {
                var c = await client.Connect(token); using var tcp = c.Tcp; using var tls = c.Tls;
                var badHash = valid with { Files = [new InputFile("main.cpp", 1, new string('0', 64))] };
                await Wire.Write(tls, badHash, token);
                Check((await Wire.Read<Reply>(tls, token)).Status == "accepted", "hash-case-admitted");
                await tls.WriteAsync(new byte[] { 1 }, token);
                Check((await Wire.Read<Reply>(tls, token)).Status == "rejected", "corrupt-input-rejected");
            }
            // Hold both slots during upload, prove busy is bounded and info remains available.
            var first = await client.Connect(token); var second = await client.Connect(token);
            try
            {
                await Wire.Write(first.Tls, Manifest(), token); await Wire.Read<Reply>(first.Tls, token);
                await Wire.Write(second.Tls, Manifest(), token); await Wire.Read<Reply>(second.Tls, token);
                Check(await Raw(Manifest()) == "busy", "two-slot-admission-bound");
                Check((await client.Info(token)).Slots == 2, "info-available-while-busy");
            }
            finally { first.Tls.Dispose(); first.Tcp.Dispose(); second.Tls.Dispose(); second.Tcp.Dispose(); }
            await Eventually(() => !Directory.EnumerateDirectories(Path.Combine(workerRoot, "state"), "attempt-*").Any(), token);
            Check(true, "disconnect-during-upload-cleans-and-releases-slots");
            using (var badPin = new Client(clientConfig with { WorkerPin = new string('0', 64) }, clientRoot))
            {
                bool rejected = false; try { await badPin.Info(token); } catch (Exception) { rejected = true; }
                Check(rejected, "wrong-server-certificate-rejected");
            }
            string stranger = Path.Combine(directory, "stranger"); Identity.CreatePair(stranger, compilerPath);
            using (var wrongClient = new Client(clientConfig, Path.Combine(stranger, "client")))
            {
                bool rejected = false; try { await wrongClient.Info(token); } catch (Exception) { rejected = true; }
                Check(rejected, "unenrolled-client-rejected");
            }
            File.WriteAllText(Path.Combine(input, "value.h"), "inline constexpr int probe_value=42;\n", new UTF8Encoding(false));
            Check((await client.Compile(Manifest(), input, Path.Combine(directory, "recovery"), token)).Status == "completed", "worker-recovery-after-rejections");
            {
                // Cancellation is a control byte after the exact input bodies;
                // native tests below also exercise cancellation of a live process tree.
                var c = await client.Connect(token); using var tcp = c.Tcp; using var tls = c.Tls;
                Request cancelRequest = Manifest();
                await Wire.Write(tls, cancelRequest, token); await Wire.Read<Reply>(tls, token);
                foreach (InputFile file in cancelRequest.Files!) await Wire.SendFile(tls, Path.Combine(input, file.Path), file, token);
                await tls.WriteAsync(new byte[] { 1 }, token);
                Reply cancelled = await Wire.Read<Reply>(tls, token);
                Check(cancelled.Status == "cancelled" && cancelled.Output is null, "wire-cancellation-reaches-native-owner");
            }
            await NativeContracts(childProbe, directory, token);
        }
        finally { worker.Stop(); await server; }
        File.WriteAllText(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(new { checks, scope = "real local TLS loopback and real installed MSVC; two-machine run still required" }, Wire.Json));
    }

    private static void VerifyObject(string compiler, string directory, int expected)
    {
        File.WriteAllText(Path.Combine(directory, "verify.cpp"), $"extern \"C\" int dbe_probe(); extern \"C\" int entry(){{return dbe_probe()=={expected}?0:1;}}\n");
        Directory.CreateDirectory(Path.Combine(directory, "tmp"));
        Execution compile = Native.Run(compiler, directory, "/nologo /c /GS- /O2 /X /Foverify.obj verify.cpp", 10, 2048, CancellationToken.None);
        if (compile.Kind != 0 || compile.ExitCode != 0) throw new Exception("Object verifier compilation failed");
        Execution link = Native.Run(Path.Combine(Path.GetDirectoryName(compiler)!, "link.exe"), directory,
            "/nologo /nodefaultlib /debug /pdb:verify.pdb /entry:entry /subsystem:console /out:verify.exe verify.obj result.obj", 10, 2048, CancellationToken.None);
        if (link.Kind != 0 || link.ExitCode != 0) throw new Exception("Object verifier linking failed: " + Encoding.UTF8.GetString(link.Stdout));
        if (!File.Exists(Path.Combine(directory, "verify.pdb")) || Encoding.UTF8.GetString(link.Stdout).Contains("LNK4099") || Encoding.UTF8.GetString(link.Stderr).Contains("LNK4099"))
            throw new Exception("Object verifier lost debug symbols");
        Execution run = Native.Run(Path.Combine(directory, "verify.exe"), directory, "", 5, 256, CancellationToken.None);
        if (run.Kind != 0 || run.ExitCode != 0) throw new Exception("Returned object had incorrect behavior");
    }

    private static async Task NativeContracts(string child, string directory, CancellationToken token)
    {
        var priority = Native.Run(child, directory, "--priority", 5, 256, token);
        Check(priority.Kind == 0 && priority.ExitCode == 0 && Encoding.ASCII.GetString(priority.Stdout) == "16384", "native-below-normal-priority");
        string rawArgs = "one \"two words\" \"\" \"한글\" \"tail\\\\\" \"a\\\"b\" plain\\\\path";
        Execution argv = Native.Run(child, directory, "--args " + rawArgs, 5, 256, token);
        string expectedArgs = string.Join("\n", ClInvocation.Split(rawArgs).Select(a => string.Concat(a.Select(c => ((int)c).ToString("x4"))))) + "\n";
        Check(argv.Kind == 0 && argv.ExitCode == 0 && Encoding.ASCII.GetString(argv.Stdout).Replace("\r\n", "\n") == expectedArgs,
            "response-parser-agrees-with-native-CRT-quotes-and-backslashes");
        Execution exit = Native.Run(child, directory, "--exit", 5, 256, token);
        Check(exit.Kind == 0 && exit.ExitCode == 0xc000013a, "native-full-dword-exit");
        Execution flood = Native.Run(child, directory, "--flood", 5, 256, token);
        Check(flood.Kind == 4 && flood.Stdout.Length == Wire.DiagnosticLimit, "native-output-bound");
        foreach (bool cancel in new[] { false, true })
        {
            string marker = Path.Combine(directory, "pids-" + cancel + ".txt");
            using var cancellation = new CancellationTokenSource();
            Task<Execution> task = Task.Run(() => Native.Run(child, directory, "--linger \"" + marker + "\"", cancel ? 30 : 2, 256, cancellation.Token));
            await Eventually(() => File.Exists(marker) && new FileInfo(marker).Length > 0, token);
            int[] pids = File.ReadAllText(marker).Split(' ').Select(int.Parse).ToArray();
            using Process parent = Process.GetProcessById(pids[0]); using Process leaf = Process.GetProcessById(pids[1]);
            _ = parent.Handle; _ = leaf.Handle; // retain identities before exit/PID reuse
            if (cancel) cancellation.Cancel();
            Execution result = await task;
            Check(result.Kind == (cancel ? 2 : 3) && parent.WaitForExit(5000) && leaf.WaitForExit(5000), cancel ? "native-cancel-kills-process-family" : "native-timeout-kills-process-family");
        }
        string ownerDirectory = Path.Combine(directory, "owner-death"); Directory.CreateDirectory(ownerDirectory);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("process-test"); start.ArgumentList.Add(child); start.ArgumentList.Add(ownerDirectory);
        using Process owner = Process.Start(start)!;
        try
        {
            string marker = Path.Combine(ownerDirectory, "pids.txt");
            await Eventually(() => File.Exists(marker) && new FileInfo(marker).Length > 0, token);
            int[] pids = File.ReadAllText(marker).Split(' ').Select(int.Parse).ToArray();
            using Process parent = Process.GetProcessById(pids[0]); using Process leaf = Process.GetProcessById(pids[1]);
            _ = parent.Handle; _ = leaf.Handle;
            owner.Kill(); owner.WaitForExit(5000);
            Check(parent.WaitForExit(5000) && leaf.WaitForExit(5000), "forced-owner-death-kills-process-family");
        }
        finally { if (!owner.HasExited) { owner.Kill(); owner.WaitForExit(5000); } }
    }
    private static async Task Eventually(Func<bool> predicate, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Condition did not become true"); await Task.Delay(25, token); }
    }
}

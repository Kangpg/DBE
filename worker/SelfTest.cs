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
        var validation = new PreparedInput.ValidationCache();
        var legacy = PreparedInput.EncodingFor(1252);
        string header = Path.Combine(directory, "cache-input.h");
        File.WriteAllText(header, "A", Encoding.Unicode);
        validation.Check(header, legacy);
        var stamp = File.GetLastWriteTimeUtc(header);
        File.WriteAllText(header, "\u0100", Encoding.Unicode);
        File.SetLastWriteTimeUtc(header, stamp);
        validation.Check(header, PreparedInput.EncodingFor(65001));
        try { validation.Check(header, legacy); throw new Exception("Unexpected cached encoding acceptance"); }
        catch (UnsupportedCompile) { Check(true, "header-validation-distinguishes-bytes-and-codepage-with-identical-stat"); }
        NativeInventoryContracts(compilerPath, directory);
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
            NativePathContracts(compilerPath, directory);
            await NativePchContracts(client, info.Toolchain, compilerPath, directory, token);
            await MappedContracts(client, info.Toolchain, compilerPath, directory, token);
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
            await PdbIsolationContracts(compilerPath, childProbe, directory, token);
            MsvcPathContracts(compilerPath, directory, token);
            await PdbLeaseAliasContracts(compilerPath, directory, Path.Combine(pair, "client", "client.json"));
        }
        finally { worker.Stop(); await server; }
        File.WriteAllText(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(new { checks, scope = "real local TLS loopback and real installed MSVC; two-machine run still required" }, Wire.Json));
    }

    private static void NativePathContracts(string compiler, string directory)
    {
        string drive = Directory.CreateDirectory(Path.Combine(directory, "virtual-drive")).FullName;
        string project = Directory.CreateDirectory(Path.Combine(drive, "project")).FullName;
        Directory.CreateDirectory(Path.Combine(project, "out"));
        File.WriteAllText(Path.Combine(project, "value.h"), "#define VALUE 42\n");
        File.WriteAllText(Path.Combine(project, "main.cpp"), "#include \"Q:\\project\\value.h\"\nconstexpr bool same(const char*a,const char*b){return *a==*b&&(!*a||same(a+1,b+1));}\nstatic_assert(same(__FILE__,\"Q:\\\\project\\\\main.cpp\"));\nextern \"C\" int dbe_probe(){return VALUE;}\n");
        string mapping = "Q=" + char.ToUpperInvariant(drive[0]) + drive[1..];
        var compiled = Native.Run(compiler, project, "/nologo /c /std:c++20 /Z7 /GS- /X /FoQ:\\project\\out\\result.obj Q:\\project\\main.cpp",
            10, 2048, CancellationToken.None, tempDirectory: Path.Combine(directory, "success", "tmp"), mapping: mapping, logicalCwd: "Q:\\project");
        Check(compiled.Kind == 0 && compiled.ExitCode == 0, "native-process-map-preserves-absolute-include-and-file-macro:" + JsonSerializer.Serialize(compiled));
        VerifyObject(compiler, Path.Combine(project, "out"), 42);
        Check(true, "native-process-map-object-links-and-executes");
        File.WriteAllText(Path.Combine(project, "relative.cpp"), "#include \"value.h\"\nconstexpr bool same(const char*a,const char*b){return *a==*b&&(!*a||same(a+1,b+1));}\nstatic_assert(same(__FILE__,\"relative.cpp\"));\nextern \"C\" int relative_probe(){return VALUE;}\n");
        var relative = Native.Run(compiler, project, "/nologo /c /std:c++20 /Z7 /GS- /X /FoQ:\\project\\out\\relative.obj relative.cpp",
            10, 2048, CancellationToken.None, tempDirectory: Path.Combine(directory, "success", "tmp"), mapping: mapping, logicalCwd: "Q:\\project");
        Check(relative.Kind == 0 && relative.ExitCode == 0, "native-process-map-preserves-relative-cwd-and-file-macro:" + Encoding.UTF8.GetString(relative.Stdout));
    }

    internal static async Task MappedContracts(Client client, string identity, string compiler, string directory, CancellationToken token)
    {
        string input = Directory.CreateDirectory(Path.Combine(directory, "mapped-input", "Q", "project")).FullName;
        string sdk = Directory.CreateDirectory(Path.Combine(directory, "mapped-input", "C", "dbe-native-fixture")).FullName;
        File.WriteAllText(Path.Combine(sdk, "sdk.h"), "#define SDK_VALUE 10\n");
        File.WriteAllText(Path.Combine(input, "pch.h"), "#pragma once\n#include \"Q:\\project\\generated.h\"\n#include <sdk.h>\nstatic_assert(SDK_VALUE==10);\n");
        File.WriteAllText(Path.Combine(input, "generated.h"), "#define VALUE 42\n");
        File.WriteAllText(Path.Combine(input, "pch.cpp"), "#include \"pch.h\"\nextern \"C\" int producer_value(){return VALUE;}\n");
        File.WriteAllText(Path.Combine(input, "main.cpp"), "#include \"pch.h\"\nextern \"C\" int producer_value();\nextern \"C\" int dbe_probe(){return producer_value();}\n");
        var options = new[] { "/utf-8", "/std:c++20", "/Zi", "/GS-", "/Od" };
        var files = Directory.GetFiles(Path.Combine(directory, "mapped-input"), "*", SearchOption.AllDirectories).Select(p => new InputFile(
            Path.GetRelativePath(Path.Combine(directory, "mapped-input"), p).Replace('\\', '/'), new FileInfo(p).Length,
            Wire.HashFile(p), File.GetLastWriteTimeUtc(p).Ticks)).ToArray();
        var recipe = new NativeRecipe("Q:\\project\\pch.cpp", options, "pch.h", "Q:\\project\\shared.pch", "Q:\\project\\out\\pch.obj");
        var request = new Request(5, "compile", Guid.NewGuid().ToString("N"), identity, "Q/project/main.cpp", files, options,
            NativeInput: new MappedCompile("Q:\\project\\", "Q:\\project\\main.cpp", "C:\\dbe-native-fixture\\", recipe));
        string local = Directory.CreateDirectory(Path.Combine(directory, "local-native-producer")).FullName;
        using var runtime = new RuntimeInputs(compiler, Path.Combine(directory, "local-runtime"));
        runtime.Populate(Path.Combine(directory, "mapped-input"));
        Directory.CreateDirectory(Path.Combine(input, "out"));
        var mapped = new MappedCompiler(compiler, Path.Combine(directory, "mapped-input"), request.NativeInput!);
        var localProducer = mapped.Produce(recipe, local, Path.Combine(local, "local.pdb"), 10, 2048, token);
        Check(localProducer.Kind == 0 && localProducer.ExitCode == 0, "mapped-native-local-PCH-producer:" + JsonSerializer.Serialize(localProducer) + Encoding.UTF8.GetString(localProducer.Stdout) + Encoding.UTF8.GetString(localProducer.Stderr));
        File.Copy(Path.Combine(input, "out", "pch.obj"), Path.Combine(local, "producer.obj"));
        int deferredIndex = Array.FindIndex(files, file => file.Path.EndsWith("/sdk.h", StringComparison.Ordinal));
        var deferredFiles = files.ToArray();
        deferredFiles[deferredIndex] = files[deferredIndex] with { Sha256 = "", Deferred = true };
        using (var blocked = new FileStream(Path.Combine(sdk, "sdk.h"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Reply needed = await client.Compile(request with { Files = deferredFiles }, Path.Combine(directory, "mapped-input"),
                Path.Combine(directory, "deferred-input"), token);
            Check(needed.Status == "input-required" && needed.RequiredInputs!.SequenceEqual([deferredIndex]) &&
                needed.Output is null && needed.Pdb is null && needed.ProducerObject is null, "deferred-input-not-uploaded-and-requested-by-compiler");
        }
        async Task<(Reply Reply, string Output)> Submit(int i)
        {
            // The preceding deferred-input response can arrive before its
            // workspace cleanup releases the slot. Busy is admission, not a
            // compiler result; retry only that explicit protocol response.
            for (int retry = 0; retry < 100; retry++)
            {
                string output = Path.Combine(directory, "mapped-native-" + i + "-" + retry);
                Reply reply = await client.Compile(request with { Attempt = Guid.NewGuid().ToString("N") },
                    Path.Combine(directory, "mapped-input"), output, token);
                if (reply.Status != "busy") return (reply, output);
                await Task.Delay(25, token);
            }
            throw new TimeoutException("Mapped native fixture admission remained busy");
        }
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(Submit));
        for (int i = 0; i < concurrent.Length; i++)
        {
            var (reply, output) = concurrent[i];
            Check(reply.Status == "completed" && reply.Pdb is not null && reply.ProducerObject is not null,
                "mapped-native-PCH-Zi-artifacts-" + i + ":" + (File.Exists(Path.Combine(output, "stdout.bin")) ? Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(output, "stdout.bin"))) : reply.Message));
            VerifyObject(compiler, output, 42, true);
            Check(true, "mapped-native-PCH-Zi-link-execute-" + i);
            File.Copy(Path.Combine(local, "producer.obj"), Path.Combine(output, "producer.obj"), true);
            File.Copy(Path.Combine(local, "local.pdb"), Path.Combine(output, "local.pdb"), true);
            VerifyObject(compiler, output, 42, true);
            Check(true, "mapped-native-remote-consumer-links-original-local-PCH-object-" + i);
        }
    }

    internal static async Task NativePchContracts(Client client, string identity, string compiler, string directory, CancellationToken token)
    {
        string input = Directory.CreateDirectory(Path.Combine(directory, "original-input")).FullName;
        foreach (string name in new[] { "src", "first", "second", "generated" }) Directory.CreateDirectory(Path.Combine(input, name));
        File.WriteAllText(Path.Combine(input, "src", "pch.h"), "#pragma once\n#include <selected.h>\n#include <value.h>\ninline constexpr int answer=SELECTED+GENERATED+DEFINED+FORCED;\n");
        File.WriteAllText(Path.Combine(input, "src", "pch.cpp"), "#include \"pch.h\"\nextern \"C\" int producer_value(){return answer;}\n");
        File.WriteAllText(Path.Combine(input, "src", "main.cpp"), "#include \"pch.h\"\nextern \"C\" int producer_value();\nextern \"C\" int dbe_probe(){return producer_value();}\n");
        File.WriteAllText(Path.Combine(input, "first", "selected.h"), "#define SELECTED 10\n");
        File.WriteAllText(Path.Combine(input, "second", "selected.h"), "#error INCORRECT_INCLUDE_ORDER\n");
        File.WriteAllText(Path.Combine(input, "first", "forced.h"), "#define FORCED 4\n");
        Request Manifest() => new(4, "compile", Guid.NewGuid().ToString("N"), identity, "src/main.cpp",
            Directory.GetFiles(input, "*", SearchOption.AllDirectories).Select(p => new InputFile(
                Path.GetRelativePath(input, p).Replace('\\', '/'), new FileInfo(p).Length, Wire.HashFile(p))).ToArray(),
            ["/utf-8", "/std:c++20", "/Zi", "/Od", "/GS-", "/DDEFINED=8", "/Ifirst", "/Isecond", "/Igenerated", "/FIforced.h"],
            new PchPair("src/pch.cpp", "pch.h"));
        foreach (var test in new[] { ("initial", 20, 42), ("changed", 21, 43), ("error", 0, 0), ("recovery", 22, 44) })
        {
            File.WriteAllText(Path.Combine(input, "generated", "value.h"), test.Item1 == "error" ?
                "#error NATIVE_PCH_GENERATED_ERROR\n" : "#define GENERATED " + test.Item2 + "\n");
            string output = Path.Combine(directory, "native-pch-" + test.Item1);
            Reply reply = await client.Compile(Manifest(), input, output, token);
            if (test.Item1 == "error")
            {
                string diagnostics = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(output, "stdout.bin")));
                Check(reply.Status == "compile-error" && diagnostics.Contains("NATIVE_PCH_GENERATED_ERROR") &&
                    reply.Output is null && reply.ProducerObject is null && reply.Pdb is null, "native-pch-producer-error-no-partial-results");
            }
            else
            {
                Check(reply.Status == "completed" && reply.ProducerObject is not null && reply.Pdb is not null, "native-pch-artifacts-" + test.Item1);
                VerifyObject(compiler, output, test.Item3, true);
                Check(true, "native-pch-ordered-includes-macros-forced-header-execution-" + test.Item1);
            }
        }
        Request valid = Manifest();
        File.AppendAllText(Path.Combine(input, "src", "main.cpp"), "#error NATIVE_PCH_CONSUMER_ERROR\n");
        string failedConsumer = Path.Combine(directory, "native-pch-consumer-error");
        Reply failure = await client.Compile(Manifest(), input, failedConsumer, token);
        Check(failure.Status == "compile-error" && failure.Output is null && failure.ProducerObject is null && failure.Pdb is null &&
            Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(failedConsumer, "stdout.bin"))).Contains("NATIVE_PCH_CONSUMER_ERROR"),
            "native-pch-consumer-error-no-producer-published");
        foreach (var options in new[] { new[] { "/utf-8", "/IC:/outside" }, new[] { "/utf-8", "/P" }, new[] { "/utf-8", "/Y-" }, new[] { "/utf-8", "/FC" } })
        {
            bool rejected = false;
            try { Wire.Validate(valid with { Options = options }); } catch (Rejected) { rejected = true; }
            Check(rejected, "native-pch-reject-unsupported-" + options[1]);
        }
    }

    private static void VerifyObject(string compiler, string directory, int expected, bool producer = false)
    {
        File.WriteAllText(Path.Combine(directory, "verify.cpp"), $"extern \"C\" int dbe_probe(); extern \"C\" int entry(){{return dbe_probe()=={expected}?0:1;}}\n");
        Directory.CreateDirectory(Path.Combine(directory, "tmp"));
        Execution compile = Native.Run(compiler, directory, "/nologo /c /GS- /O2 /X /Foverify.obj verify.cpp", 10, 2048, CancellationToken.None);
        if (compile.Kind != 0 || compile.ExitCode != 0) throw new Exception("Object verifier compilation failed");
        Execution link = Native.Run(Path.Combine(Path.GetDirectoryName(compiler)!, "link.exe"), directory,
            "/nologo /nodefaultlib /debug /pdb:verify.pdb /entry:entry /subsystem:console /out:verify.exe verify.obj result.obj" + (producer ? " producer.obj" : ""), 10, 2048, CancellationToken.None);
        if (link.Kind != 0 || link.ExitCode != 0) throw new Exception("Object verifier linking failed: " + Encoding.UTF8.GetString(link.Stdout));
        if (!File.Exists(Path.Combine(directory, "verify.pdb")) || Encoding.UTF8.GetString(link.Stdout).Contains("LNK4099") || Encoding.UTF8.GetString(link.Stderr).Contains("LNK4099"))
            throw new Exception("Object verifier lost debug symbols");
        Execution run = Native.Run(Path.Combine(directory, "verify.exe"), directory, "", 5, 256, CancellationToken.None);
        if (run.Kind != 0 || run.ExitCode != 0) throw new Exception("Returned object had incorrect behavior");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "SetEnvironmentVariableW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool SetProcessVariable(string name, string? value);

    // Each owned Job has a private MSPDBSRV, so the exclusive local PDB lease is the only guard
    // against two Jobs writing one PDB, and /Fo and /Fp name the files DBE publishes and pins.
    // Every spelling must resolve to the file the real compiler writes, not to its text. Drive-
    // relative spellings use a second volume whose current directory comes from "=X:"; the
    // compiler inherits it through the same environment producer as production launches.
    private static void MsvcPathContracts(string compiler, string directory, CancellationToken token)
    {
        string root = Directory.CreateDirectory(Path.Combine(directory, "msvc-path")).FullName;
        char same = char.ToUpperInvariant(root[0]);
        string temp = Path.GetFullPath(Path.GetTempPath());
        char? other = temp.Length > 2 && temp[1] == ':' && char.ToUpperInvariant(temp[0]) != same ? char.ToUpperInvariant(temp[0]) : null;
        string? otherBase = other is null ? null : Directory.CreateDirectory(Path.Combine(temp, "dbe-msvc-path-" + Guid.NewGuid().ToString("N"))).FullName;
        string driveVariable = "=" + other + ":";
        string? previousDrive = other is null ? null : Environment.GetEnvironmentVariable(driveVariable);
        (string Kind, string Spelling)[] cases = [
            ("Fd", ""), ("Fd", "/Fd"), ("Fd", "/Fdsub\\"), ("Fd", "/Fdsub/"), ("Fd", "/Fdsub"), ("Fd", "/Fdname"), ("Fd", "/Fdname.x"),
            ("Fd", "/Fd:colon.pdb"), ("Fd", "-FdDash.pdb"), ("Fd", @"/Fdsub\vc140.pdb"), ("Fd", "/Fdfirst.pdb /Fdlast"),
            ("Fd", "/Fd{same}:same.pdb"), ("Fd", "/Fd{other}:other.pdb"), ("Fd", @"/Fd{other}:sub\"),
            ("Fo", ""), ("Fo", "/Fo"), ("Fo", "/Fo:colon.obj"), ("Fo", "/Fosub"), ("Fo", "/Fosub\\"), ("Fo", "/Foname"), ("Fo", "-FoDash.obj"),
            ("Fo", "/Fofirst.obj /Folast.obj"), ("Fo", "/Fo{same}:same.obj"), ("Fo", "/Fo{other}:odrive.obj"),
            ("Fp", "/Fp"), ("Fp", "/Fp:colon.pch"), ("Fp", "/Fpname"), ("Fp", "/Fpsub\\"), ("Fp", "/Fpsub"), ("Fp", "/Fpfirst.pch /Fplast.pch"),
            ("Fp", "/Fp{other}:odrive.pch")];
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        int skipped = 0;
        try
        {
            for (int i = 0; i < cases.Length; i++)
            {
                var (kind, spelling) = cases[i];
                if (spelling.Contains("{other}") && other is null) { skipped++; continue; }
                string dir = Directory.CreateDirectory(Path.Combine(root, i.ToString())).FullName;
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                File.WriteAllText(Path.Combine(dir, "p.h"), "inline int g(){return 2;}");
                File.WriteAllText(Path.Combine(dir, "a.cpp"), "#include \"p.h\"\nint f(){return g();}");
                string? otherDir = otherBase is null ? null : Directory.CreateDirectory(Path.Combine(otherBase, i.ToString(), "sub")).Parent!.FullName;
                if (otherDir is not null && !SetProcessVariable(driveVariable, otherDir)) throw new Exception("Cannot set the fixture drive directory");
                string text = spelling.Replace("{same}", same.ToString()).Replace("{other}", other?.ToString() ?? "");
                string arguments = kind switch { "Fd" => "/nologo /c /Zi /FS ", "Fo" => "/nologo /c ", _ => "/nologo /c /Ycp.h " } + text + " a.cpp";
                Execution run = Native.RunInitiator(compiler, dir, arguments, 60, token);
                if (run.Kind != 0 || run.ExitCode != 0) throw new Exception("MSVC path fixture failed for '" + spelling + "': " + Encoding.UTF8.GetString(run.Stdout));
                string[] written = new[] { dir, otherDir }.OfType<string>().SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                    .Where(file => Path.GetFileName(file) is not ("a.cpp" or "p.h"))
                    .Where(file => kind switch { "Fd" => !Path.GetFileName(file).Equals("a.obj", StringComparison.OrdinalIgnoreCase), "Fp" => Path.GetExtension(file) == ".pch", _ => true })
                    .ToArray();
                // The production lookup itself reads "=X:" from this process.
                var context = new MsvcPathContext(dir, drive => Environment.GetEnvironmentVariable("=" + drive + ":"));
                var parsedArgs = ClInvocation.Expand(ClInvocation.Split(arguments));
                string? resolved = kind switch
                {
                    "Fd" => ClInvocation.PdbIdentity(parsedArgs, context),
                    "Fo" => ClInvocation.ParseBatch(parsedArgs.ToArray(), false, context)[0].Object,
                    _ => ClInvocation.ParseBatch(parsedArgs.ToArray(), true, context)[0].PchFile
                };
                Check(written.Length == 1 && resolved is not null && Path.GetFullPath(written[0]).Equals(resolved, StringComparison.OrdinalIgnoreCase),
                    "msvc-" + kind + "-path-matches-real-compiler:" + (spelling.Length == 0 ? "default" : spelling));
                identities[kind + spelling] = resolved!.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ? resolved[dir.Length..].ToUpperInvariant() : resolved.ToUpperInvariant();
            }
        }
        finally
        {
            if (other is not null) SetProcessVariable(driveVariable, previousDrive);
            if (otherBase is not null) Directory.Delete(otherBase, true);
        }
        Check(identities["Fd"] == identities["Fd/Fd"] && identities["Fd/Fdsub\\"] == identities["Fd/Fdsub/"] && identities["Fd/Fdsub/"] == identities[@"Fd/Fdsub\vc140.pdb"] &&
            identities["Fd/Fdsub"] != identities["Fd/Fdsub\\"] && identities["Fd/Fdfirst.pdb /Fdlast"] == @"\LAST.PDB" &&
            identities["Fo/Fosub"] == @"\SUB.OBJ" && identities["Fp/Fpsub"] == @"\SUB.PCH", "aliased-spellings-share-one-identity-and-existing-directories-without-separator-are-files");
        Check(skipped == 0 || other is null, other is null ? "drive-relative-cases-unverified-temp-shares-fixture-volume" : "drive-relative-cases-use-working-directory-or-inherited-drive-directory");
        string detached = Directory.CreateDirectory(Path.Combine(root, "detached")).FullName;
        File.WriteAllText(Path.Combine(detached, "a.cpp"), "int f(){return 1;}");
        string sourceArg = ClInvocation.Quote(Path.Combine(detached, "a.cpp"));
        File.WriteAllText(Path.Combine(detached, "b.cpp"), "int h(){return 3;}");
        Directory.CreateDirectory(Path.Combine(detached, "existing"));
        // Absolute sources: the self-test Worker shares this process, so never change its cwd.
        foreach (var (arguments, expected) in new[] {
            ("/nologo /c /Zi /Fd other.pdb " + sourceArg, "Unsupported compiler argument: other.pdb"),
            ("/nologo /c /Fo out.obj " + sourceArg, "Unsupported compiler argument: out.obj"),
            ("/nologo /c /Ycp.h /Fp x.pch " + sourceArg, "Unsupported compiler argument: x.pch"),
            ("/nologo /c /Foname.x " + sourceArg, "Expected an object output"),
            ("/nologo /c " + ClInvocation.Quote("/Fo" + Path.Combine(detached, "existing")) + " " + sourceArg + " " + ClInvocation.Quote(Path.Combine(detached, "b.cpp")), "Multiple sources require an object directory") })
        {
            try { ClInvocation.ParseBatch(ClInvocation.Expand(ClInvocation.Split(arguments)).ToArray(), true); throw new Exception("Accepted " + arguments); }
            catch (UnsupportedCompile error) { Check(error.Message == expected, "detached-or-unsupported-output-spelling-stays-local:" + expected); }
        }
    }

    // Without a shared MSPDBSRV, two Jobs writing one PDB fail with C1041 (reproduced with real
    // MSVC: separate endpoints failed 3/3, a shared endpoint passed 3/3). Aliased /Fd spellings
    // of one PDB must therefore take one exclusive lease through the production scheduler path,
    // and cancelling either the running owner or the waiter must leave the other able to finish.
    private static async Task PdbLeaseAliasContracts(string compiler, string directory, string clientPath)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        CancellationToken token = deadline.Token;
        string root = Directory.CreateDirectory(Path.Combine(directory, "pdb-lease-alias")).FullName;
        string policy = Path.Combine(root, "policy", "host.json");
        Directory.CreateDirectory(Path.GetDirectoryName(policy)!);
        int cores = Environment.ProcessorCount;
        // Two native Jobs may run at once, so only the PDB lease can serialize these sessions.
        File.WriteAllText(policy, JsonSerializer.Serialize(new HostPolicy(cores, 2, 256, 1024, 120, CpuBusyPercent: 100, LocalBatchSlots: cores,
            PreprocessMemoryMiB: 256, LocalBatchMemoryMiB: 1024, PreserveLocalParallelism: true, NativeLocalJobs: 2), Wire.Json));
        using var scheduler = new HostScheduler(policy, clientPath);
        static string Sources(string dir, string prefix)
        {
            for (int k = 0; k < 4; k++)
            {
                var text = new StringBuilder();
                for (int i = 0; i < 5000; i++)
                    text.Append("struct S").Append(prefix).Append(k).Append('_').Append(i).Append("{int a;double b;int f()const{return a+").Append(i)
                        .Append(";}}; S").Append(prefix).Append(k).Append('_').Append(i).Append(" g").Append(prefix).Append(k).Append('_').Append(i).Append(";\n");
                File.WriteAllText(Path.Combine(dir, prefix + k + ".cpp"), text.ToString());
            }
            return string.Join(' ', Enumerable.Range(0, 4).Select(k => prefix + k + ".cpp"));
        }
        (string Dir, string ArgsA, string ArgsB) Fixture(string name)
        {
            string dir = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            string a = Sources(dir, "a"), b = Sources(dir, "b");
            return (dir, "/nologo /c /Zi /FS /GS- /MP4 /Fdsub\\ " + a, "/nologo /c /Zi /FS /GS- /MP4 " + ClInvocation.Quote(@"/Fdsub\vc140.pdb") + " " + b);
        }
        string Pdb(string dir, string arguments) =>
            ClInvocation.ParseBatch(ClInvocation.Expand(ClInvocation.Split(arguments)).ToArray(), false, new MsvcPathContext(dir, _ => null))[0].CompilerPdb!;
        // Mirrors the native Local owner: lease first, then an owned Job, cleanup confirmed only
        // when the supervisor did not report uncertain process cleanup.
        async Task<(Execution Run, long Start, long End)> Session(string dir, string arguments, CancellationToken execution, TaskCompletionSource? acquired = null)
        {
            using var lease = await scheduler.Acquire(true, "alias " + arguments[^10..], execution, Pdb(dir, arguments), 1, 1024);
            acquired?.TrySetResult();
            long start = Stopwatch.GetTimestamp();
            lease.BeginExecution();
            Execution run = await Task.Run(() => Native.RunInitiator(compiler, dir, arguments, 120, execution));
            if (run.Kind != 5) lease.ConfirmCleanup();
            return (run, start, Stopwatch.GetTimestamp());
        }
        string Output(Execution run) => Encoding.UTF8.GetString(run.Stdout) + Encoding.UTF8.GetString(run.Stderr);

        var both = Fixture("concurrent");
        Check(Pdb(both.Dir, both.ArgsA) == Pdb(both.Dir, both.ArgsB), "aliased-Fd-spellings-request-one-PDB-lease");
        var first = Session(both.Dir, both.ArgsA, token);
        var second = Session(both.Dir, both.ArgsB, token);
        var results = await Task.WhenAll(first, second);
        bool serialized = results[0].End <= results[1].Start || results[1].End <= results[0].Start;
        bool compiled = results.All(r => r.Run.Kind == 0 && r.Run.ExitCode == 0) && serialized && !results.Any(r => Output(r.Run).Contains("C1041"));
        Check(compiled, "concurrent-aliased-sessions-serialize-and-both-compile" + (compiled ? "" : ":" + string.Join(" | ", results.Select(r => r.Run.ExitCode + " " + Output(r.Run).Trim()))));
        string objects = string.Join(' ', Enumerable.Range(0, 4).SelectMany(k => new[] { "a" + k + ".obj", "b" + k + ".obj" }));
        Execution link = Native.RunInitiator(Path.Combine(Path.GetDirectoryName(compiler)!, "link.exe"), both.Dir,
            "/nologo /dll /noentry /nodefaultlib /debug /pdb:linked.pdb /out:linked.dll " + objects, 60, token);
        bool linked = link.Kind == 0 && link.ExitCode == 0 && !Output(link).Contains("LNK4099");
        Check(linked, "serialized-shared-PDB-links-objects-from-both-sessions" + (linked ? "" : ":" + Output(link).Trim()));

        var holderCancel = Fixture("cancel-holder");
        using (var cancelHolder = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var holderAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = Session(holderCancel.Dir, holderCancel.ArgsA, cancelHolder.Token, holderAcquired);
            await holderAcquired.Task.WaitAsync(token);
            var waiter = Session(holderCancel.Dir, holderCancel.ArgsB, token);
            await Task.Delay(300, token);
            bool waited = !waiter.IsCompleted;
            cancelHolder.Cancel();
            var cancelled = await holder;
            var finished = await waiter;
            bool recovered = waited && cancelled.Run.Kind == 2 && finished.Run.Kind == 0 && finished.Run.ExitCode == 0 && finished.Start >= cancelled.End;
            Check(recovered, "cancelled-PDB-owner-releases-lease-and-waiter-compiles" + (recovered ? "" : ":" + finished.Run.ExitCode + " " + Output(finished.Run).Trim()));
        }

        var waiterCancel = Fixture("cancel-waiter");
        var ownerAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = Session(waiterCancel.Dir, waiterCancel.ArgsA, token, ownerAcquired);
        await ownerAcquired.Task.WaitAsync(token);
        using (var cancelWaiter = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var queued = Session(waiterCancel.Dir, waiterCancel.ArgsB, cancelWaiter.Token);
            await Task.Delay(300, token);
            bool waiting = !queued.IsCompleted;
            cancelWaiter.Cancel();
            bool waiterCancelled = false;
            try { await queued; } catch (OperationCanceledException) { waiterCancelled = true; }
            var completed = await owner;
            using var next = await scheduler.Acquire(true, "after cancelled waiter", token, Pdb(waiterCancel.Dir, waiterCancel.ArgsB), 1, 1024).WaitAsync(TimeSpan.FromSeconds(10), token);
            Check(waiting && waiterCancelled && completed.Run.Kind == 0 && completed.Run.ExitCode == 0 && next.IsLocal,
                "cancelled-PDB-waiter-leaves-owner-and-lease-usable");
        }
    }

    private static async Task PdbIsolationContracts(string compiler, string child, string directory, CancellationToken token)
    {
        string root = Directory.CreateDirectory(Path.Combine(directory, "pdb-isolation")).FullName;
        string[] dirs = Enumerable.Range(0, 2).Select(i => Directory.CreateDirectory(Path.Combine(root, i.ToString())).FullName).ToArray();
        foreach (string dir in dirs)
        {
            File.WriteAllText(Path.Combine(dir, "first0.cpp"), "extern \"C\" int value(){return 41;}");
            File.WriteAllText(Path.Combine(dir, "first1.cpp"), "struct PdbFixture{int member;}; PdbFixture fixture{42};");
            File.WriteAllText(Path.Combine(dir, "second0.cpp"), "extern \"C\" int value(); extern \"C\" int entry(){return value()==41?0:1;}");
            File.WriteAllText(Path.Combine(dir, "second1.cpp"), "extern \"C\" int another(){return 43;}");
        }
        using var cancelA = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var cancelB = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<Execution> Start(int i, CancellationToken cancel) => Task.Run(() => Native.RunInitiator(child, dirs[i],
            "--pdb-session \"" + compiler + "\" \"" + Path.Combine(dirs[i], "ready") + "\" \"" + Path.Combine(dirs[i], "release") + "\"", 45, cancel));
        // A test-owned common endpoint makes the old shared-service failure
        // reproducible without connecting to a user's ambient PDB broker.
        string? previous = Environment.GetEnvironmentVariable("_MSPDBSRV_ENDPOINT_");
        Task<Execution>? a = null, b = null;
        try
        {
            Environment.SetEnvironmentVariable("_MSPDBSRV_ENDPOINT_", "DBE.Test." + Guid.NewGuid().ToString("N"));
            a = Start(0, cancelA.Token);
            await Eventually(() => File.Exists(Path.Combine(dirs[0], "ready")), token);
            b = Start(1, cancelB.Token);
            await Eventually(() => File.Exists(Path.Combine(dirs[1], "ready")), token);
            string[][] markers = dirs.Select(dir => File.ReadAllLines(Path.Combine(dir, "ready"))).ToArray();
            Check(markers[0][1] != markers[1][1] && markers.All(m => m[1].Length > 0), "owned-Jobs-have-distinct-PDB-service-endpoints");
            using Process brokerA = Process.GetProcessById(int.Parse(markers[0][0].Split(' ')[0]));
            using Process brokerB = Process.GetProcessById(int.Parse(markers[1][0].Split(' ')[0]));
            _ = brokerA.Handle; _ = brokerB.Handle;
            Check(brokerA.StartTime.ToUniversalTime().ToFileTimeUtc() == long.Parse(markers[0][0].Split(' ')[1]) &&
                brokerB.StartTime.ToUniversalTime().ToFileTimeUtc() == long.Parse(markers[1][0].Split(' ')[1]) && brokerA.Id != brokerB.Id,
                "real-Zi-MP-compilers-create-separate-owned-PDB-services");
            cancelA.Cancel();
            Execution cancelled = await a;
            Check(cancelled.Kind == 2 && brokerA.WaitForExit(5000) && !brokerB.HasExited, "cancel-one-Job-joins-its-broker-and-preserves-other-broker");
            File.WriteAllText(Path.Combine(dirs[1], "release"), "go");
            Execution completed = await b;
            Check(completed.Kind == 0 && completed.ExitCode == 0 && brokerB.WaitForExit(5000),
                "other-Job-continues-Zi-MP-and-joins-its-own-broker:" + Encoding.UTF8.GetString(completed.Stdout));
            Execution link = Native.RunInitiator(Path.Combine(Path.GetDirectoryName(compiler)!, "link.exe"), dirs[1],
                "/nologo /nodefaultlib /debug /pdb:verify.pdb /entry:entry /subsystem:console /out:verify.exe first0.obj first1.obj second0.obj second1.obj", 10, token);
            Execution run = Native.RunInitiator(Path.Combine(dirs[1], "verify.exe"), dirs[1], "", 5, token);
            Check(link.Kind == 0 && link.ExitCode == 0 && !Encoding.UTF8.GetString(link.Stdout).Contains("LNK4099") &&
                File.Exists(Path.Combine(dirs[1], "verify.pdb")) && run.Kind == 0 && run.ExitCode == 0, "surviving-Job-PDB-objects-link-and-execute");
        }
        finally
        {
            Environment.SetEnvironmentVariable("_MSPDBSRV_ENDPOINT_", previous);
            cancelA.Cancel(); cancelB.Cancel();
            if (a is not null) await a;
            if (b is not null) await b;
        }
    }

    private static async Task NativeContracts(string child, string directory, CancellationToken token)
    {
        var priority = Native.Run(child, directory, "--priority", 5, 256, token);
        Check(priority.Kind == 0 && priority.ExitCode == 0 && Encoding.ASCII.GetString(priority.Stdout) == "16384", "native-below-normal-priority");
        var ownPriority = Native.RunInitiator(child, directory, "--priority", 5, token);
        var incomingFanout = Native.Run(child, directory, "--many", 5, 256, token);
        var ownFanout = Native.RunInitiator(child, directory, "--many", 5, token);
        Check(ownPriority.Kind == 0 && ownPriority.ExitCode == 0 && Encoding.ASCII.GetString(ownPriority.Stdout) == "32" &&
            incomingFanout.Kind == 0 && incomingFanout.ExitCode == 8 && ownFanout.Kind == 0 && ownFanout.ExitCode == 0,
            "initiator-native-priority-and-fanout-preserve-incoming-limits-and-owned-cleanup");
        var limited = Native.Run(child, directory, "--commit", 5, 256, token);
        var initiating = Native.Run(child, directory, "--commit", 5, 0, token);
        Check(limited.Kind == 0 && limited.ExitCode == 8 && initiating.Kind == 0 && initiating.ExitCode == 0,
            "initiator-native-heap-and-incoming-job-memory-limit-are-distinct");
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
    private static void NativeInventoryContracts(string compiler, string directory)
    {
        string scope = Directory.CreateDirectory(Path.Combine(directory, "inventory")).FullName;
        string project = Directory.CreateDirectory(Path.Combine(scope, "project")).FullName;
        string sibling = Directory.CreateDirectory(Path.Combine(scope, "sibling")).FullName;
        string source = Path.Combine(project, "main.cpp"), header = Path.Combine(sibling, "value.h");
        string opaque = Path.Combine(sibling, "extensionless"), unused = Path.Combine(project, "unused.payload");
        File.WriteAllText(source, "#include \"../sibling/value.h\"\n#include \"../sibling/extensionless\"\n");
        File.WriteAllText(header, "#define VALUE 42\n"); File.WriteAllText(opaque, "#define OTHER 7\n"); File.WriteAllText(unused, "A");
        string toolchain = Directory.CreateDirectory(Path.Combine(directory, "inventory-toolchain")).FullName;
        string includes = Directory.CreateDirectory(Path.Combine(toolchain, "include")).FullName;
        string usedTool = Path.Combine(includes, "used.h"), unusedTool = Path.Combine(toolchain, "unused.h");
        File.WriteAllText(usedTool, "#define SDK_VALUE 7\n"); File.WriteAllText(unusedTool, "#define UNSELECTED_SDK_VALUE 0\n");
        var invocation = new ClInvocation(source, Path.Combine(project, "main.obj"), ["/utf-8", "/X", "/std:c++20", "/I" + includes + Path.DirectorySeparatorChar], [], [], 65001, null, null);
        var config = new AdapterConfig(compiler, "", Path.Combine(directory, "inventory-state"), "Hybrid", NativeInputs: true,
            NativeInputRoots: [scope], NativeToolchainRoots: [toolchain]);
        var snapshot = new NativeBuild(config, [invocation]);
        Request request = snapshot.Manifest(invocation, Guid.NewGuid().ToString("N"), new string('a', 64));
        Check(request.Files!.Any(file => file.Path == MappedCompile.InputPath(header) && !file.Deferred), "native-inventory-includes-parent-relative-sibling");
        Check(request.Files!.Any(file => file.Path == MappedCompile.InputPath(usedTool)) &&
            !request.Files!.Any(file => file.Path == MappedCompile.InputPath(unusedTool)), "toolchain-scope-inventories-only-selected-include-tree");
        try
        {
            var outside = invocation with { PreprocessOptions = ["/utf-8", "/X", "/I" + directory] };
            _ = new NativeBuild(config, [outside]).Manifest(outside, Guid.NewGuid().ToString("N"), new string('a', 64));
            throw new Exception("Unexpected out-of-scope input acceptance");
        }
        catch (UnsupportedCompile) { Check(true, "native-inventory-rejects-unrelated-include-root"); }
        File.WriteAllText(unused, "Unused metadata changed independently");
        Check(snapshot.Unchanged(), "unused-deferred-metadata-is-not-consumed-input");
        int index = Array.FindIndex(request.Files!, file => file.Path == MappedCompile.InputPath(opaque));
        snapshot.Resolve(request, [index]);
        var stamp = File.GetLastWriteTimeUtc(opaque);
        File.WriteAllText(opaque, "#define OTHER 8\n"); File.SetLastWriteTimeUtc(opaque, stamp);
        Check(!snapshot.Unchanged(), "resolved-deferred-bytes-are-validated-after-compile");
        snapshot = new NativeBuild(config, [invocation]);
        _ = snapshot.Manifest(invocation, Guid.NewGuid().ToString("N"), new string('a', 64));
        File.SetLastWriteTimeUtc(header, File.GetLastWriteTimeUtc(header).AddSeconds(2));
        Check(!snapshot.Unchanged(), "native-input-timestamp-change-is-semantic");
        NativeHashIndexContracts(Directory.CreateDirectory(Path.Combine(directory, "hash-index")).FullName);
        // Automatic scope accepts what the project itself declares, keeps the
        // whole-drive refusal, and does not list compiler outputs as inputs.
        string declaredRoot = Directory.CreateDirectory(Path.Combine(directory, "declared-elsewhere")).FullName;
        File.WriteAllText(Path.Combine(declaredRoot, "declared.h"), "#define DECLARED 1\n");
        File.WriteAllText(Path.Combine(declaredRoot, "stale.obj"), "x");
        var auto = config with { NativeAutoScope = true, NativeInputRoots = [], NativeToolchainRoots = [], StateDirectory = Path.Combine(directory, "auto-state") };
        var autoInvocation = invocation with { PreprocessOptions = ["/utf-8", "/X", "/I" + declaredRoot] };
        InputFile[] autoFiles = new NativeBuild(auto, [autoInvocation]).Manifest(autoInvocation, Guid.NewGuid().ToString("N"), new string('a', 64)).Files!;
        Check(autoFiles.Any(file => file.Path == MappedCompile.InputPath(Path.Combine(declaredRoot, "declared.h"))), "automatic-scope-accepts-project-declared-include-root");
        Check(!autoFiles.Any(file => file.Path.EndsWith("stale.obj", StringComparison.OrdinalIgnoreCase)), "native-inventory-omits-compiler-outputs");
        var wholeDrive = autoInvocation with { PreprocessOptions = ["/utf-8", "/X", "/I" + Path.GetPathRoot(directory)] };
        try { _ = new NativeBuild(auto, [wholeDrive]).Manifest(wholeDrive, Guid.NewGuid().ToString("N"), new string('a', 64)); throw new Exception("Whole drive accepted"); }
        catch (UnsupportedCompile) { Check(true, "automatic-scope-still-refuses-a-whole-drive"); }
        // Repeated Worker failures, or an inventory that cannot be described, end
        // remote placement for this CL Task; a completion resets the count.
        var circuit = new NativeBuild(config, [invocation]);
        circuit.NoteRemoteFallback(); circuit.NoteRemoteFallback(); circuit.NoteRemoteCompleted(); circuit.NoteRemoteFallback(); circuit.NoteRemoteFallback();
        Check(circuit.RemoteViable, "remote-completion-resets-the-failure-count");
        circuit.NoteRemoteFallback();
        Check(!circuit.RemoteViable, "repeated-remote-failures-end-remote-placement");
        var unscoped = invocation with { PreprocessOptions = ["/utf-8", "/X", "/I" + declaredRoot] };
        var refused = new NativeBuild(config, [unscoped]);
        try { _ = refused.Manifest(unscoped, Guid.NewGuid().ToString("N"), new string('a', 64)); throw new Exception("Explicit scope accepted an outside root"); }
        catch (UnsupportedCompile) { Check(!refused.RemoteViable, "unavailable-inventory-ends-remote-placement"); }
        byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);
        Check(MsBuildAdapter.IsWorkerEnvironmentFailure(Bytes("a.cpp(1): fatal error C1083: cannot open")) && MsBuildAdapter.IsWorkerEnvironmentFailure(Bytes("fatal error C1060: 힙 공간")), "worker-environment-failures-go-to-the-local-compiler");
        Check(!MsBuildAdapter.IsWorkerEnvironmentFailure(Bytes("a.cpp(1): error C2065: undeclared")) && !MsBuildAdapter.IsWorkerEnvironmentFailure(Bytes("a.cpp(1): fatal error C1189: #error")), "source-errors-remain-remote-diagnostics");
        // A remembered hash must not outlive the metadata it was recorded under.
        string reused = Path.Combine(project, "reused.h");
        File.WriteAllText(reused, "#define REUSED 1\n");
        File.SetLastWriteTimeUtc(reused, DateTime.UtcNow.AddMinutes(-5));
        string Hashed() => new NativeBuild(config, [invocation]).Manifest(invocation, Guid.NewGuid().ToString("N"), new string('a', 64))
            .Files!.Single(file => file.Path == MappedCompile.InputPath(reused)).Sha256;
        string original = Hashed();
        Check(original == Hashed(), "native-snapshot-reuses-the-durable-hash-index");
        DateTime recorded = File.GetLastWriteTimeUtc(reused);
        File.WriteAllText(reused, "#define REUSED 2\n");
        File.SetLastWriteTimeUtc(reused, recorded);
        string sameMetadata = Hashed();
        Check(sameMetadata == original, "native-hash-index-is-keyed-by-length-and-timestamp");
        File.SetLastWriteTimeUtc(reused, recorded.AddSeconds(2));
        Check(Hashed() == Wire.HashFile(reused) && Hashed() != original, "native-hash-index-rehashes-after-timestamp-change");
        File.SetLastWriteTimeUtc(reused, recorded);
        File.WriteAllText(reused, "#define REUSED 22\n");
        File.SetLastWriteTimeUtc(reused, recorded);
        Check(Hashed() == Wire.HashFile(reused), "native-hash-index-rehashes-after-length-change");
    }
    private static void NativeHashIndexContracts(string state)
    {
        string file = Path.Combine(state, "native-hash-index.txt");
        string input = Path.GetFullPath(Path.Combine(state, "input.h"));
        string hash = new string('b', 64);
        long old = DateTime.UtcNow.AddMinutes(-5).Ticks;
        var index = NativeHashIndex.Load(state);
        index.Add(input, 7, old, hash);
        index.Add(input + ".racy", 7, DateTime.UtcNow.Ticks, hash);
        index.Save();
        var reloaded = NativeHashIndex.Load(state);
        Check(reloaded.TryGet(input, 7, old, out string found) && found == hash, "hash-index-survives-process-boundary");
        Check(!reloaded.TryGet(input + ".racy", 7, DateTime.UtcNow.Ticks, out _), "hash-index-skips-just-modified-files");
        Check(!reloaded.TryGet(input, 8, old, out _) && !reloaded.TryGet(input, 7, old + 1, out _), "hash-index-misses-on-any-metadata-change");
        var concurrent = NativeHashIndex.Load(state);
        var other = NativeHashIndex.Load(state);
        concurrent.Add(input + ".a", 1, old, hash); other.Add(input + ".b", 2, old, hash);
        concurrent.Save(); other.Save();
        reloaded = NativeHashIndex.Load(state);
        Check(reloaded.TryGet(input + ".a", 1, old, out _) && reloaded.TryGet(input + ".b", 2, old, out _), "hash-index-merges-other-process-entries");
        File.WriteAllText(file, "not\ta\tvalid\tentry\n" + old + "\t7\t" + hash + "\trelative.h\n" + "\0\0\0");
        Check(!NativeHashIndex.Load(state).TryGet("relative.h", 7, old, out _), "malformed-hash-index-means-hash-again");
    }

    private static async Task Eventually(Func<bool> predicate, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Condition did not become true"); await Task.Delay(25, token); }
    }
}

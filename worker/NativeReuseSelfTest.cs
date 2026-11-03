using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Dbe;

static class NativeReuseSelfTest
{
    internal static async Task Run(string compilerPath, string directory, string typeVerifier, string diaLibrary, string? clientConfig = null)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory)) throw new Rejected("Reuse test directory must be new");
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); checks.Add(name); Console.WriteLine("PASS " + name); }
        Worker? worker = null; Task server = Task.CompletedTask; string? contexts = null;
        ClientConfig cc; string cr;
        if (clientConfig is null)
        {
            string pair = Path.Combine(directory, "pair"); Identity.CreatePair(pair, compilerPath, "127.0.0.1");
            var (wc, wr) = Identity.ReadConfig<WorkerConfig>(Path.Combine(pair, "worker", "worker.json"));
            worker = new Worker(wc with { Port = 0 }, wr); worker.Start(); server = worker.Serve();
            (cc, cr) = Identity.ReadConfig<ClientConfig>(Path.Combine(pair, "client", "client.json"));
            cc = cc with { Port = worker.Port }; contexts = Path.Combine(wr, wc.StateDirectory, "ctx");
        }
        else (cc, cr) = Identity.ReadConfig<ClientConfig>(clientConfig);
        using var ownedWorker = worker;
        using var client = new Client(cc, cr);
        using var symbols = new NativeClientCache(Path.Combine(directory, "client-state"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180)); var token = deadline.Token;
        string inputs = Directory.CreateDirectory(Path.Combine(directory, "inputs")).FullName;
        string source = Directory.CreateDirectory(Path.Combine(inputs, "Q", "cache")).FullName;
        string sdk = Directory.CreateDirectory(Path.Combine(inputs, "C", "dbe-cache-sdk")).FullName;
        File.WriteAllText(Path.Combine(sdk, "sdk.h"), "#define SDK_VALUE 10\n");
        string header = Path.Combine(source, "pch.h");
        string headerText = "#pragma once\n#include \"value.h\"\n#include <sdk.h>\nstatic_assert(SDK_VALUE==10);\n" + string.Join('\n', Enumerable.Range(0, 3000).Select(i => "struct DbePchType" + i + "{int value;};"));
        File.WriteAllText(header, headerText); File.WriteAllText(Path.Combine(source, "value.h"), "#define VALUE 42\n");
        File.WriteAllText(Path.Combine(source, "pch.cpp"), "#include \"pch.h\"\nextern \"C\" int producer_value(){return VALUE;}\n");
        File.WriteAllText(Path.Combine(source, "alpha.cpp"), "#include \"pch.h\"\nstruct DbeReuseAlpha{int first;int second;};extern \"C\" int alpha(DbeReuseAlpha*p){return p->first+p->second+VALUE;}\n");
        File.WriteAllText(Path.Combine(source, "beta.cpp"), "#include \"pch.h\"\nstruct DbeReuseBeta{double payload;char label[32];};extern \"C\" int beta(DbeReuseBeta*p){return int(p->payload)+p->label[0]+VALUE;}\n");
        File.WriteAllText(Path.Combine(source, "bad.cpp"), "#include \"pch.h\"\n#error DBE_REUSE_CONSUMER_ERROR\n");
        File.WriteAllText(Path.Combine(source, "slow.cpp"), "#include \"pch.h\"\n" + string.Join('\n', Enumerable.Range(0, 60000).Select(i => "int slow_" + i + "(){return " + i + ";}")));
        var names = new List<string> { "pch.h", "value.h", "pch.cpp", "alpha.cpp", "beta.cpp", "bad.cpp", "slow.cpp" };
        foreach (int i in Enumerable.Range(0, 512))
        {
            string name = "unused-" + i + ".h"; names.Add(name); File.WriteAllText(Path.Combine(source, name), "// immutable fixture header " + i + "\n");
        }
        string[] options = ["/utf-8", "/std:c++20", "/Zi", "/GS-", "/Od", "/X", "/IC:\\dbe-cache-sdk\\"];
        using var localCompiler = new Compiler(compilerPath);
        string generation = Guid.NewGuid().ToString("N");
        NativeRecipe Recipe(string epoch) => new("Q:\\cache\\pch.cpp", options, "pch.h", "Q:\\cache\\shared.pch", "Q:\\cache\\out\\pch.obj", epoch);
        Request Manifest(string file, string? epoch = null)
        {
            var files = names.Select(name => new InputFile("Q/cache/" + name, new FileInfo(Path.Combine(source, name)).Length,
                Wire.HashFile(Path.Combine(source, name)), File.GetLastWriteTimeUtc(Path.Combine(source, name)).Ticks))
                .Append(new InputFile("C/dbe-cache-sdk/sdk.h", new FileInfo(Path.Combine(sdk, "sdk.h")).Length,
                    Wire.HashFile(Path.Combine(sdk, "sdk.h")), File.GetLastWriteTimeUtc(Path.Combine(sdk, "sdk.h")).Ticks)).ToArray();
            var request = new Request(6, "compile", Guid.NewGuid().ToString("N"), localCompiler.Identity, "Q/cache/" + file, files, options,
                NativeInput: new MappedCompile("Q:\\cache\\", file, "", Recipe(epoch ?? generation)));
            return request with { ContextKey = NativeContext.Key(request) };
        }
        async Task<(Reply Reply, string Output, double Ms)> Submit(Request request, string label, NativeClientCache? cache = null)
        {
            var watch = Stopwatch.StartNew();
            for (int retry = 0; retry < 100; retry++)
            {
                string output = Path.Combine(directory, label + "-" + retry);
                var reply = await client.Compile(request with { Attempt = Guid.NewGuid().ToString("N") }, inputs, output, token, cache: cache);
                if (reply.Status != "busy") return (reply, output, watch.Elapsed.TotalMilliseconds);
                await Task.Delay(25, token);
            }
            throw new TimeoutException("Reuse fixture admission remained busy");
        }
        (Reply Reply, string Output, double Ms)[] first = [];
        var timings = new List<object>();
        string original = Directory.CreateDirectory(Path.Combine(directory, "original-producer")).FullName;
        try
        {
            var capabilities = await client.Info(token, 6);
            Check(capabilities.ReusablePch == true && capabilities.MaxProtocol == 6, "reusable-profile-negotiated");
            Check(capabilities.Toolchain == localCompiler.Identity, "selected-compiler-matches-worker");
            Request initial = Manifest("alpha.cpp");
            using var runtime = new RuntimeInputs(compilerPath, Path.Combine(directory, "runtime")); runtime.Populate(inputs);
            var environment = new MappedCompiler(compilerPath, inputs, initial.NativeInput!);
            var produced = environment.Produce(Recipe(generation), original, Path.Combine(original, "original.pdb"), 30, 2048, token);
            Check(produced.Kind == 0 && produced.ExitCode == 0, "original-local-producer-built-with-same-options");
            File.Copy(environment.Resolve(Recipe(generation).ObjectFile!), Path.Combine(original, "producer.obj"));
            first = await Task.WhenAll(Submit(initial, "alpha", symbols), Submit(Manifest("beta.cpp"), "beta", symbols));
            Check(first.All(x => x.Reply.Status == "completed" && x.Reply.ProducerPdb is not null), "parallel-consumers-return-independent-PDBs");
            long pchBuilds = first[0].Reply.Cache!.PchBuilds, inputBuilds = first[0].Reply.Cache!.InputBuilds;
            Check(first.All(x => x.Reply.Cache!.InputBuilds == inputBuilds && x.Reply.Cache.PchBuilds == pchBuilds) &&
                (worker is null || (pchBuilds == 1 && inputBuilds == 1)), "parallel-consumers-build-input-tree-and-PCH-once");
            Check(first.Count(x => x.Reply.Cache!.PchHit) == 1, "one-concurrent-consumer-reuses-PCH");
            var warm = await Submit(Manifest("alpha.cpp"), "warm", symbols);
            Check(warm.Reply.Status == "completed" && warm.Reply.Cache!.InputsHit && warm.Reply.Cache.PchHit && warm.Reply.Cache.PchBuilds == pchBuilds, "warm-request-reuses-inputs-and-PCH");
            using (var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(warm.Output, "result.json"))))
            {
                Check(receipt.RootElement.GetProperty("inputBytesSent").GetInt64() == 0 && receipt.RootElement.GetProperty("producerPdbBytesReceived").GetInt64() == 0, "warm-request-transfers-no-input-or-producer-PDB-bytes");
                Check(receipt.RootElement.GetProperty("contextReferenceUsed").GetBoolean() && receipt.RootElement.GetProperty("headerBytesSent").GetInt64() < 4096,
                    "warm-request-references-context-without-repeating-file-manifest");
            }
            async Task<string> Raw(Request request)
            {
                var connection = await client.Connect(token); using var tcp = connection.Tcp; using var tls = connection.Tls;
                await Wire.Write(tls, request, token); return (await Wire.Read<Reply>(tls, token)).Status;
            }
            var reference = initial with { Files = null, Attempt = Guid.NewGuid().ToString("N") };
            Check(await Raw(reference with { Options = ["/FoC:/outside.obj"] }) == "rejected", "context-reference-rejects-output-overrides");
            Check(await Raw(reference with { Source = "Q/cache/absent.cpp", NativeInput = reference.NativeInput! with { SourceArgument = "absent.cpp" } }) == "rejected", "context-reference-rejects-undeclared-source");
            Check(await Raw(reference with { NativeInput = reference.NativeInput! with { Include = "C:\\other" } }) == "rejected", "context-reference-rejects-changed-include-environment");
            var alteredFiles = initial.Files!.ToArray(); alteredFiles[0] = alteredFiles[0] with { Sha256 = new string('0', 64) };
            try { symbols.Validate(initial with { Files = alteredFiles }); throw new Exception("Altered file definition was silently reused"); }
            catch (Rejected) { Check(true, "client-does-not-drop-changed-file-definition"); }
            InputFile originalFile = initial.Files![0];
            try
            {
                initial.Files[0] = originalFile with { Sha256 = new string('0', 64) };
                try { symbols.Validate(initial); throw new Exception("Mutated array was silently reused"); }
                catch (Rejected) { Check(true, "context-owns-file-array-against-caller-mutation"); }
            }
            finally { initial.Files[0] = originalFile; }
            for (int i = 0; i < 3; i++)
            {
                var baseline = Manifest("alpha.cpp") with { Version = 5, ContextKey = null };
                var old = await Submit(baseline, "baseline-" + i);
                var reused = await Submit(Manifest("alpha.cpp"), "reused-" + i, symbols);
                Check(old.Reply.Status == "completed" && reused.Reply.Status == "completed", "completed-equivalent-warm-pair-" + i);
                timings.Add(new { baselineMs = old.Ms, reusedMs = reused.Ms, cache = reused.Reply.Cache });
            }
            var bad = await Submit(Manifest("bad.cpp"), "consumer-error", symbols);
            Check(bad.Reply.Status == "compile-error" && bad.Reply.Cache!.PchHit && !File.Exists(Path.Combine(bad.Output, "result.obj")), "cached-PCH-preserves-consumer-errors");
            var recovered = await Submit(Manifest("alpha.cpp"), "consumer-recovery", symbols);
            Check(recovered.Reply.Status == "completed" && recovered.Reply.Cache!.PchHit, "consumer-error-does-not-corrupt-readonly-PCH");
            using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cancelled.CancelAfter(150);
                string output = Path.Combine(directory, "cancelled");
                try { await client.Compile(Manifest("slow.cpp"), inputs, output, cancelled.Token, cache: symbols); throw new Exception("Slow compile was not cancelled"); }
                catch (OperationCanceledException) { Check(!File.Exists(Path.Combine(output, "result.obj")), "cancelled-reuse-request-publishes-no-object"); }
            }
            recovered = await Submit(Manifest("alpha.cpp"), "cancel-recovery", symbols);
            Check(recovered.Reply.Status == "completed" && recovered.Reply.Cache!.PchHit, "PCH-reusable-after-consumer-cancellation");
            DateTime valueTime = File.GetLastWriteTimeUtc(Path.Combine(source, "value.h"));
            File.WriteAllText(Path.Combine(source, "value.h"), "#define VALUE 43\n"); File.SetLastWriteTimeUtc(Path.Combine(source, "value.h"), valueTime);
            var changed = await Submit(Manifest("alpha.cpp"), "header-change", symbols);
            Check(changed.Reply.Status == "completed" && !changed.Reply.Cache!.PchHit && changed.Reply.Cache.PchBuilds == pchBuilds + 1, "header-content-invalidates-PCH");
            Request flags = Manifest("alpha.cpp"); string[] changedOptions = [.. options, "/DDBE_PCH_FLAG=1"];
            flags = flags with { Options = changedOptions, NativeInput = flags.NativeInput! with { Producer = flags.NativeInput.Producer! with { Options = changedOptions } } };
            flags = flags with { ContextKey = NativeContext.Key(flags) };
            var flagResult = await Submit(flags, "flag-change", symbols);
            Check(flagResult.Reply.Status == "completed" && !flagResult.Reply.Cache!.PchHit && flagResult.Reply.Cache.PchBuilds == pchBuilds + 2, "producer-options-invalidate-PCH");
            var epoch = await Submit(Manifest("alpha.cpp", Guid.NewGuid().ToString("N")), "generation-change", symbols);
            Check(epoch.Reply.Status == "completed" && !epoch.Reply.Cache!.PchHit && epoch.Reply.Cache.PchBuilds == pchBuilds + 3, "original-Yc-generation-invalidates-PCH");
            File.AppendAllText(header, "\n#error DBE_REUSE_PRODUCER_ERROR\n");
            var producerError = await Submit(Manifest("alpha.cpp"), "producer-error", symbols);
            Check(producerError.Reply.Status == "compile-error" && producerError.Reply.Output is null && producerError.Reply.ProducerPdb is null, "producer-error-not-cached-as-success");
            File.WriteAllText(header, headerText);
            Request missing = Manifest("alpha.cpp", Guid.NewGuid().ToString("N")); symbols.Remember(cc.WorkerPin, missing.ContextKey!);
            var missed = await Submit(missing, "missing-context", symbols);
            Check(missed.Reply.Status == "completed" && !missed.Reply.Cache!.PchHit, "missing-context-resends-validated-definition");
        }
        finally { if (worker is not null) { worker.Stop(); await server; worker.Dispose(); } }
        if (contexts is not null)
            Check(!Directory.Exists(contexts) || !Directory.EnumerateFiles(contexts, ".dbe-owned", SearchOption.AllDirectories).Any(), "worker-stopped-and-cached-seed-paths-removed-before-link");
        foreach (string producerKind in new[] { "remote", "original" })
        {
            string linked = Directory.CreateDirectory(Path.Combine(directory, "linked-" + producerKind)).FullName;
            for (int i = 0; i < first.Length; i++)
            {
                File.Copy(Path.Combine(first[i].Output, "result.obj"), Path.Combine(linked, i == 0 ? "alpha.obj" : "beta.obj"));
                foreach (var symbol in new[] { first[i].Reply.Pdb!, first[i].Reply.ProducerPdb! })
                    if (!File.Exists(Path.Combine(linked, symbol.Path))) File.Copy(Path.Combine(first[i].Output, symbol.Path), Path.Combine(linked, symbol.Path));
            }
            File.Copy(Path.Combine(producerKind == "remote" ? first[0].Output : original, "producer.obj"), Path.Combine(linked, "producer.obj"));
            if (producerKind == "original") File.Copy(Path.Combine(original, "original.pdb"), Path.Combine(linked, "original.pdb"));
            File.WriteAllText(Path.Combine(linked, "verify.cpp"), "extern \"C\" {int _fltused=0;} struct DbeReuseAlpha{int first;int second;};struct DbeReuseBeta{double payload;char label[32];};extern \"C\" int alpha(DbeReuseAlpha*);extern \"C\" int beta(DbeReuseBeta*);extern \"C\" int entry(){DbeReuseAlpha a{10,11};DbeReuseBeta b{20,{1}};return alpha(&a)+beta(&b)==126?0:1;}\n");
            Directory.CreateDirectory(Path.Combine(linked, "tmp"));
            void Execute(string exe, string args)
            {
                var result = Native.Run(exe, linked, args, 30, 2048, token);
                if (result.Kind != 0 || result.ExitCode != 0 || Encoding.UTF8.GetString(result.Stdout).Contains("LNK4099"))
                    throw new Exception("Reuse artifact verification failed: " + Encoding.UTF8.GetString(result.Stdout) + Encoding.UTF8.GetString(result.Stderr));
            }
            Execute(compilerPath, "/nologo /c /GS- /O2 /X /Foverify.obj verify.cpp");
            Execute(Path.Combine(Path.GetDirectoryName(compilerPath)!, "link.exe"), "/nologo /nodefaultlib /debug /pdb:verify.pdb /entry:entry /subsystem:console /out:verify.exe verify.obj alpha.obj beta.obj producer.obj");
            Execute(Path.Combine(linked, "verify.exe"), "");
            Check(true, producerKind + "-producer-and-two-consumers-link-run" + (worker is not null ? "-after-worker-stops" : "-on-requester"));
            var types = Native.Run(typeVerifier, linked, string.Join(' ', new[] { diaLibrary, Path.Combine(linked, "verify.pdb"), "DbeReuseAlpha", "DbeReuseBeta" }.Select(ClInvocation.Quote)), 10, 256, token);
            if (types.Kind != 0 || types.ExitCode != 0) throw new Exception("DIA type verification failed");
            using var data = JsonDocument.Parse(types.Stdout);
            var udts = data.RootElement.GetProperty("types").EnumerateArray().ToArray();
            Check(udts.Any(x => x.GetProperty("name").GetString() == "DbeReuseAlpha" && x.GetProperty("length").GetInt32() == 8) &&
                udts.Any(x => x.GetProperty("name").GetString() == "DbeReuseBeta" && x.GetProperty("length").GetInt32() == 40 &&
                    x.GetProperty("members").EnumerateArray().Any(m => m.GetProperty("name").GetString() == "label" && m.GetProperty("offset").GetInt32() == 8)), producerKind + "-producer-PDB-types-and-members-correct");
            File.WriteAllBytes(Path.Combine(linked, "types.json"), types.Stdout);
        }
        File.WriteAllText(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(new { checks, timings,
            scope = "real MSVC, " + (worker is not null ? "TLS loopback" : "configured TLS worker") +
                ", PCH/PDB reuse and DIA; synthetic inputs only; actual-project throughput unverified" }, Wire.Json));
    }
}

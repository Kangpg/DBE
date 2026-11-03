using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Dbe;

static class NativeReuseSelfTest
{
    internal static async Task Run(string compilerPath, string directory, string typeVerifier, string diaLibrary, string? clientConfig = null, bool requireBatch = false)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory)) throw new Rejected("Reuse test directory must be new");
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); checks.Add(name); Console.WriteLine("PASS " + name); }
        Worker? worker = null; Task server = Task.CompletedTask; string? contexts = null;
        string? inheritedRuntime = null, hostRuntime = null; FileAttributes hostAttributes = default;
        ClientConfig cc; string cr;
        if (clientConfig is null)
        {
            string pair = Path.Combine(directory, "pair"); Identity.CreatePair(pair, compilerPath, "127.0.0.1");
            var (wc, wr) = Identity.ReadConfig<WorkerConfig>(Path.Combine(pair, "worker", "worker.json"));
            hostRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Globalization", "Sorting", "SortDefault.nls");
            hostAttributes = File.GetAttributes(hostRuntime);
            string runtimeCache = Directory.CreateDirectory(Path.Combine(wr, wc.StateDirectory, "runtime-cache")).FullName;
            inheritedRuntime = Path.Combine(runtimeCache, "sorting-SortDefault.nls");
            File.Copy(hostRuntime, inheritedRuntime); File.SetAttributes(inheritedRuntime, File.GetAttributes(inheritedRuntime) | FileAttributes.ReadOnly);
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
        File.WriteAllText(Path.Combine(source, "tail-a.cpp"), "#include \"pch.h\"\nextern \"C\" int tail_a(){return 7;}\n");
        File.WriteAllText(Path.Combine(source, "tail-b.cpp"), "#include \"pch.h\"\nextern \"C\" int tail_b(){return 9;}\n");
        File.WriteAllText(Path.Combine(source, "@input.cpp"), "// Must never become a response-file argument\n");
        var names = new List<string> { "pch.h", "value.h", "pch.cpp", "alpha.cpp", "beta.cpp", "bad.cpp", "slow.cpp", "tail-a.cpp", "tail-b.cpp", "@input.cpp" };
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
        (Reply Reply, string Output, double Ms)[] batchArtifacts = [];
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
            if (inheritedRuntime is not null)
                Check((File.GetAttributes(inheritedRuntime) & FileAttributes.ReadOnly) == 0 && File.GetAttributes(hostRuntime!) == hostAttributes,
                    "owned-runtime-copy-normalizes-readonly-without-host-mutation");
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
            if (worker is not null || requireBatch)
            {
                Check((await client.Info(token, 7)).MaxProtocol == 7, "native-batch-protocol-negotiated-with-v6-compatibility");
                Request batch = Manifest("alpha.cpp") with { Version = 7, BatchSources = [
                    new NativeSource("Q/cache/alpha.cpp", "alpha.cpp"), new NativeSource("Q/cache/beta.cpp", "beta.cpp"), new NativeSource("Q/cache/tail-a.cpp", "tail-a.cpp"), new NativeSource("Q/cache/tail-b.cpp", "tail-b.cpp") ] };
                var grouped = await Submit(batch, "batch", symbols);
                Check(grouped.Reply.Status == "completed" && grouped.Reply.Output is null && grouped.Reply.Outputs is { Length: 4 } &&
                    grouped.Reply.Pdb is not null && grouped.Reply.Cache!.PchHit, "batch-four-objects-share-one-consumer-PDB-and-cached-PCH");
                batchArtifacts = Enumerable.Range(0, 4).Select(i => (grouped.Reply with { Output = grouped.Reply.Outputs![i], Outputs = null }, grouped.Output, grouped.Ms)).ToArray();
                string published = Path.Combine(directory, "published", grouped.Reply.Pdb!.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(published)!);
                symbols.Publish(grouped.Reply.Pdb, Path.Combine(grouped.Output, grouped.Reply.Pdb.Path), published);
                symbols.Publish(grouped.Reply.Pdb, Path.Combine(grouped.Output, grouped.Reply.Pdb.Path), published);
                try { symbols.Publish(grouped.Reply.Pdb with { Sha256 = new string('0', 64) }, published, published); throw new Exception("Published PDB identity changed"); }
                catch (Rejected) { Check(true, "batch-PDB-publication-reuses-immutable-identity-and-rejects-conflicts"); }
                var warmBatch = await Submit(batch, "batch-warm", symbols);
                using (var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(warmBatch.Output, "result.json"))))
                    Check(warmBatch.Reply.Status == "completed" && receipt.RootElement.GetProperty("contextReferenceUsed").GetBoolean() &&
                        receipt.RootElement.GetProperty("inputBytesSent").GetInt64() == 0 && receipt.RootElement.GetProperty("producerPdbBytesReceived").GetInt64() == 0,
                        "batch-warm-request-reuses-context-inputs-and-producer-symbols");
                var badBatch = batch with { BatchSources = [batch.BatchSources![0], new NativeSource("Q/cache/bad.cpp", "bad.cpp")] };
                var failedBatch = await Submit(badBatch, "batch-error", symbols);
                Check(failedBatch.Reply.Status == "compile-error" && failedBatch.Reply.Outputs is null && failedBatch.Reply.Pdb is null &&
                    !Directory.EnumerateFiles(failedBatch.Output, "*.obj").Any(), "batch-compiler-error-never-publishes-partial-objects-or-PDB");
                Check(await Raw(batch with { Files = null, BatchSources = [batch.BatchSources[0], new NativeSource("Q/cache/absent.cpp", "absent.cpp")] }) == "rejected",
                    "batch-reference-rejects-undeclared-source");
                Check(await Raw(batch with { Files = null, BatchSources = [batch.BatchSources[0], new NativeSource("Q/cache/beta.cpp", "alpha.cpp")] }) == "rejected",
                    "batch-reference-rejects-source-argument-redirection");
                Check(await Raw(batch with { Files = null, BatchSources = [batch.BatchSources[0], batch.BatchSources[0]] }) == "rejected",
                    "batch-reference-rejects-colliding-object-names");
                Check(await Raw(batch with { Files = null, BatchSources = [batch.BatchSources[0], new NativeSource("Q/cache/@input.cpp", "@input.cpp")] }) == "rejected",
                    "batch-reference-rejects-response-file-source-argument");
                using (var cancelledBatch = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    cancelledBatch.CancelAfter(200);
                    Request slowBatch = batch with { Attempt = Guid.NewGuid().ToString("N"), BatchSources = [
                        batch.BatchSources[0], new NativeSource("Q/cache/slow.cpp", "slow.cpp") ] };
                    try { await client.Compile(slowBatch, inputs, Path.Combine(directory, "batch-cancel"), cancelledBatch.Token, cache: symbols); throw new Exception("Batch ignored cancellation"); }
                    catch (OperationCanceledException) when (cancelledBatch.IsCancellationRequested) { Check(true, "batch-cancellation-reaches-owned-compiler"); }
                }
                var afterCancel = await Submit(batch, "batch-after-cancel", symbols);
                Check(afterCancel.Reply.Status == "completed" && afterCancel.Reply.Cache!.PchHit, "batch-cancellation-releases-slot-and-preserves-immutable-PCH");
                var ownedBatch = new NativeContext(batch); var originalSource = batch.BatchSources[1];
                batch.BatchSources[1] = new NativeSource("Q/cache/bad.cpp", "bad.cpp");
                Check(ownedBatch.Definition.BatchSources![1] == originalSource, "batch-context-owns-source-array");
                batch.BatchSources[1] = originalSource;
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
            string producerSource = Path.Combine(source, "pch.cpp");
            string originalProducerText = File.ReadAllText(producerSource);
            DateTime producerTime = File.GetLastWriteTimeUtc(producerSource);
            long beforeLimit = missed.Reply.Cache!.PchBuilds;
            try
            {
                File.WriteAllText(producerSource, "#include \"pch.h\"\nchar dbe_oversized_producer[" + (Wire.OutputLimit + 1) + "]={1};\n");
                Request oversized = Manifest("alpha.cpp");
                var limited = await Submit(oversized, "producer-size-limit", symbols);
                Check(limited.Reply.Status == "rejected" && limited.Reply.Message!.Contains("producer object") &&
                    limited.Reply.Output is null && !File.Exists(Path.Combine(limited.Output, "result.obj")), "producer-artifact-limit-rejects-without-publication");
                var repeated = await Submit(oversized, "producer-size-limit-repeated", symbols);
                Check(repeated.Reply.Status == "rejected" && repeated.Reply.Message == limited.Reply.Message,
                    "repeated-context-preserves-artifact-limit-reason");
            }
            finally
            {
                File.WriteAllText(producerSource, originalProducerText); File.SetLastWriteTimeUtc(producerSource, producerTime);
            }
            var afterLimit = await Submit(Manifest("alpha.cpp", missing.NativeInput!.Producer!.Generation), "after-size-limit", symbols);
            Check(afterLimit.Reply.Status == "completed" && afterLimit.Reply.Cache!.PchHit && afterLimit.Reply.Cache.PchBuilds == beforeLimit + 1,
                "artifact-policy-failure-builds-once-and-preserves-valid-contexts");
        }
        finally { if (worker is not null) { worker.Stop(); await server; worker.Dispose(); } }
        if (contexts is not null)
            Check(!Directory.Exists(contexts) || !Directory.EnumerateFiles(contexts, ".dbe-owned", SearchOption.AllDirectories).Any(), "worker-stopped-and-cached-seed-paths-removed-before-link");
        if (worker is not null)
        {
            string cleanupPair = Path.Combine(directory, "cleanup-failure-pair"); Identity.CreatePair(cleanupPair, compilerPath);
            var (cleanupConfig, cleanupRoot) = Identity.ReadConfig<WorkerConfig>(Path.Combine(cleanupPair, "worker", "worker.json"));
            using var cleanupWorker = new Worker(cleanupConfig with { Port = 0 }, cleanupRoot);
            cleanupWorker.Start(); Task cleanupServer = cleanupWorker.Serve();
            string state = Path.Combine(cleanupRoot, cleanupConfig.StateDirectory);
            string unknown = Directory.CreateDirectory(Path.Combine(state, "ctx", "unexpected-context")).FullName;
            string evidence = Path.Combine(unknown, "evidence.txt"); File.WriteAllText(evidence, "retain unknown data");
            cleanupWorker.Stop(); await cleanupServer; cleanupWorker.Dispose();
            bool held = false;
            try { using var probe = new FileStream(Path.Combine(state, "worker.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { held = true; }
            Check(held && File.ReadAllText(evidence) == "retain unknown data", "shutdown-failure-retains-state-ownership-and-evidence");
        }
        foreach (var dataset in new[] { (Name: "individual", Items: first), (Name: "batch", Items: batchArtifacts) }.Where(x => x.Items.Length > 0))
        foreach (string producerKind in new[] { "remote", "original" })
        {
            var artifacts = dataset.Items;
            string linked = Directory.CreateDirectory(Path.Combine(directory, "linked-" + dataset.Name + "-" + producerKind)).FullName;
            for (int i = 0; i < artifacts.Length; i++)
            {
                File.Copy(Path.Combine(artifacts[i].Output, artifacts[i].Reply.Output!.Path), Path.Combine(linked, new[] { "alpha.obj", "beta.obj", "tail-a.obj", "tail-b.obj" }[i]));
                foreach (var symbol in new[] { artifacts[i].Reply.Pdb!, artifacts[i].Reply.ProducerPdb! })
                    if (!File.Exists(Path.Combine(linked, symbol.Path))) File.Copy(Path.Combine(artifacts[i].Output, symbol.Path), Path.Combine(linked, symbol.Path));
            }
            File.Copy(Path.Combine(producerKind == "remote" ? artifacts[0].Output : original, "producer.obj"), Path.Combine(linked, "producer.obj"));
            if (producerKind == "original") File.Copy(Path.Combine(original, "original.pdb"), Path.Combine(linked, "original.pdb"));
            File.WriteAllText(Path.Combine(linked, "verify.cpp"), "extern \"C\" {int _fltused=0;} struct DbeReuseAlpha{int first;int second;};struct DbeReuseBeta{double payload;char label[32];};extern \"C\" int alpha(DbeReuseAlpha*);extern \"C\" int beta(DbeReuseBeta*);extern \"C\" int entry(){DbeReuseAlpha a{10,11};DbeReuseBeta b{20,{1}};return alpha(&a)+beta(&b)==126?0:1;}\n");
            if (artifacts.Length == 4) {
                string verify = Path.Combine(linked, "verify.cpp");
                File.WriteAllText(verify, File.ReadAllText(verify).Replace("int entry(){", "int tail_a();extern \"C\" int tail_b();extern \"C\" int entry(){")
                    .Replace("==126?", "+tail_a()+tail_b()==142?"));
            }
            Directory.CreateDirectory(Path.Combine(linked, "tmp"));
            void Execute(string exe, string args)
            {
                var result = Native.Run(exe, linked, args, 30, 2048, token);
                if (result.Kind != 0 || result.ExitCode != 0 || Encoding.UTF8.GetString(result.Stdout).Contains("LNK4099"))
                    throw new Exception("Reuse artifact verification failed: " + Encoding.UTF8.GetString(result.Stdout) + Encoding.UTF8.GetString(result.Stderr));
            }
            Execute(compilerPath, "/nologo /c /GS- /O2 /X /Foverify.obj verify.cpp");
            Execute(Path.Combine(Path.GetDirectoryName(compilerPath)!, "link.exe"), "/nologo /nodefaultlib /debug /pdb:verify.pdb /entry:entry /subsystem:console /out:verify.exe verify.obj alpha.obj beta.obj producer.obj" +
                (artifacts.Length == 4 ? " tail-a.obj tail-b.obj" : ""));
            Execute(Path.Combine(linked, "verify.exe"), "");
            Check(true, dataset.Name + "-" + producerKind + "-producer-and-" + artifacts.Length + "-consumers-link-run" + (worker is not null ? "-after-worker-stops" : "-on-requester"));
            var types = Native.Run(typeVerifier, linked, string.Join(' ', new[] { diaLibrary, Path.Combine(linked, "verify.pdb"), "DbeReuseAlpha", "DbeReuseBeta" }.Select(ClInvocation.Quote)), 10, 256, token);
            if (types.Kind != 0 || types.ExitCode != 0) throw new Exception("DIA type verification failed");
            using var data = JsonDocument.Parse(types.Stdout);
            var udts = data.RootElement.GetProperty("types").EnumerateArray().ToArray();
            Check(udts.Any(x => x.GetProperty("name").GetString() == "DbeReuseAlpha" && x.GetProperty("length").GetInt32() == 8) &&
                udts.Any(x => x.GetProperty("name").GetString() == "DbeReuseBeta" && x.GetProperty("length").GetInt32() == 40 &&
                    x.GetProperty("members").EnumerateArray().Any(m => m.GetProperty("name").GetString() == "label" && m.GetProperty("offset").GetInt32() == 8)), dataset.Name + "-" + producerKind + "-producer-PDB-types-and-members-correct");
            File.WriteAllBytes(Path.Combine(linked, "types.json"), types.Stdout);
        }
        File.WriteAllText(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(new { checks, timings,
            scope = "real MSVC, " + (worker is not null ? "TLS loopback" : "configured TLS worker") +
                ", PCH/PDB reuse and DIA; synthetic inputs only; actual-project throughput unverified" }, Wire.Json));
    }
}

using System.Text;
using System.Text.Json;

namespace Dbe;
static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            // Only the explicitly launched build family receives this event name.
            using var buildCancellation = BuildCancellation.Observe(stop);
            string name = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "";
            if (name is "dbe-link" or "dbe-lib") return await MsBuildAdapter.RunBuildTool(name[4..], stop.Token);
            if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dbe-remote-cl", StringComparison.OrdinalIgnoreCase))
                return await MsBuildAdapter.Run(args, stop.Token);
            if (args.Length == 0) throw new Rejected("Commands: serve config | info config | probe config output-directory | compile config manifest input-directory output-directory | pair new-directory compiler-path | self-test compiler-path child-probe-path output-directory");
            if (args[0] == "pair" && args.Length == 3) { Identity.CreatePair(args[1], args[2]); return 0; }
            if (args[0] == "scheduler-self-test" && args.Length is 3 or 4) { await SchedulerSelfTest.Run(args[1], args[2], args.Length == 4 ? args[3] : null); return 0; }
            if (args[0] == "admission-probe" && args.Length == 5)
            { await SchedulerSelfTest.Probe(args[1], args[2], args[3], int.Parse(args[4]), stop.Token); return 0; }
            // Local-only process ownership fixture; never accepted by the wire protocol.
            if (args[0] == "process-test" && args.Length == 3)
            {
                Native.Run(args[1], args[2], "--linger \"" + System.IO.Path.Combine(args[2], "pids.txt") + "\"", 60, 256, stop.Token);
                return 0;
            }
            if (args[0] == "self-test" && args.Length == 4) { await SelfTest.Run(args[1], args[2], args[3]); return 0; }
            if (args[0] == "serve" && args.Length == 2)
            {
                var (config, root) = Identity.ReadConfig<WorkerConfig>(args[1]);
                using var worker = new Worker(config, root);
                using var registration = stop.Token.Register(worker.Stop);
                worker.Start();
                await worker.Serve();
                return 0;
            }
            if (args.Length >= 2 && args[0] is "info" or "probe" or "compile")
            {
                var (config, root) = Identity.ReadConfig<ClientConfig>(args[1]);
                using var client = new Client(config, root);
                stop.CancelAfter(TimeSpan.FromSeconds(180));
                if (args[0] == "info" && args.Length == 2)
                {
                    Console.WriteLine(JsonSerializer.Serialize(await client.Info(stop.Token), Wire.Json)); return 0;
                }
                if (args[0] == "probe" && args.Length == 3)
                {
                    // Deliberate remote-toolchain probe, not equivalence with the initiating compiler.
                    Capabilities remote = await client.Info(stop.Token);
                    string input = System.IO.Path.GetFullPath(args[2]) + ".inputs";
                    if (Directory.Exists(input)) throw new Rejected("Probe inputs already exist");
                    Directory.CreateDirectory(input);
                    File.WriteAllText(System.IO.Path.Combine(input, "main.cpp"), "#include \"value.h\"\nextern \"C\" int dbe_probe() { return probe_value; }\n", new UTF8Encoding(false));
                    File.WriteAllText(System.IO.Path.Combine(input, "value.h"), "#pragma once\ninline constexpr int probe_value = 42;\n", new UTF8Encoding(false));
                    var files = new[] { "main.cpp", "value.h" }.Select(name =>
                        new InputFile(name, new FileInfo(System.IO.Path.Combine(input, name)).Length, Wire.HashFile(System.IO.Path.Combine(input, name)))).ToArray();
                    var request = new Request(1, "compile", Guid.NewGuid().ToString("N"), remote.Toolchain, "main.cpp", files);
                    await File.WriteAllTextAsync(System.IO.Path.Combine(input, "manifest.json"), JsonSerializer.Serialize(request, Wire.Json), stop.Token);
                    Console.WriteLine("Using Worker compiler " + remote.CompilerVersion + " / " + remote.Toolchain);
                    Reply result = await client.Compile(request, input, args[2], stop.Token);
                    Console.WriteLine(JsonSerializer.Serialize(result, Wire.Json));
                    return result.Status == "completed" ? 0 : 1;
                }
                if (args[0] == "compile" && args.Length == 5)
                {
                    if (new FileInfo(args[2]).Length > Wire.HeaderLimit) throw new Rejected("Manifest too large");
                    Request request = JsonSerializer.Deserialize<Request>(File.ReadAllText(args[2]), Wire.Json) ?? throw new Rejected("Null manifest");
                    Reply result = await client.Compile(request, System.IO.Path.GetFullPath(args[3]), args[4], stop.Token);
                    Console.WriteLine(JsonSerializer.Serialize(result, Wire.Json));
                    return result.Status == "completed" ? 0 : 1;
                }
            }
            throw new Rejected("Invalid command or arguments");
        }
        catch (Exception e) { Console.Error.WriteLine("DBE: " + e.GetType().Name + ": " + e.Message); return 2; }
    }
}

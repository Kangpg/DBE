namespace Dbe;

// An explicitly supplied, relative input tree and a shared compiler profile.
// The producer and consumer run on the same Worker under one admission lease.
sealed record PchPair(string Producer, string Header)
{
    internal void Validate(Request request, HashSet<string> paths)
    {
        Wire.ValidatePath(Producer);
        Wire.ValidatePath(Header);
        if (!paths.Contains(Producer) || !Producer.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Producer, request.Source, StringComparison.OrdinalIgnoreCase))
            throw new Rejected("Expected a distinct PCH producer input");
        if (request.Options is not { Length: <= 128 } options || !options.Contains("/utf-8"))
            throw new Rejected("Native pair requires an explicit UTF-8 profile");
        foreach (string arg in options)
        {
            if (arg is null || arg.Length > 512) throw new Rejected("Invalid native option");
            if (arg.StartsWith("/FI", StringComparison.Ordinal) || arg.StartsWith("/I", StringComparison.Ordinal))
            {
                Wire.ValidatePath(arg[(arg.StartsWith("/FI", StringComparison.Ordinal) ? 3 : 2)..]);
                continue;
            }
            if (arg.StartsWith("/D", StringComparison.Ordinal) || arg.StartsWith("/U", StringComparison.Ordinal))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(arg, @"\A/[DU][a-zA-Z_][a-zA-Z_0-9]*(=[a-zA-Z_0-9]+)?\z") ||
                    (arg.StartsWith("/U", StringComparison.Ordinal) && arg.Contains('='))) throw new Rejected("Unsupported macro option");
                continue;
            }
            if (arg == "/FC" || !CompileOptions.Supports(arg)) throw new Rejected("Unsupported native pair option");
        }
    }

    internal Execution Run(string compiler, string workspace, Request request, int seconds, int memoryMiB, CancellationToken token, string? pdb)
    {
        string cwd = Path.Combine(workspace, "inputs");
        string temp = Directory.CreateDirectory(Path.Combine(workspace, "tmp")).FullName;
        var options = new List<string>(request.Options!) { "/nologo", "/c", "/X", "/Fp" + Path.Combine(workspace, "shared.pch") };
        if (pdb is not null) options.Add("/Fd" + Path.Combine(workspace, pdb));
        Execution Compile(string mode, string source, string obj) => Native.Run(compiler, cwd,
            string.Join(' ', options.Concat([mode + Header, "/Tp" + source, "/Fo" + Path.Combine(workspace, obj)]).Select(ClInvocation.Quote)),
            seconds, memoryMiB, token, tempDirectory: temp);
        var first = Compile("/Yc", Producer, "producer.obj");
        if (first.Kind != 0 || first.ExitCode != 0) return first;
        if (!File.Exists(Path.Combine(workspace, "shared.pch"))) throw new Rejected("Producer did not create a PCH");
        var second = Compile("/Yu", request.Source!, "result.obj");
        if (second.Kind == 5) return second;
        if (first.Stdout.Length + second.Stdout.Length > Wire.DiagnosticLimit || first.Stderr.Length + second.Stderr.Length > Wire.DiagnosticLimit)
            return second with { Kind = 4 };
        return second with { Stdout = [.. first.Stdout, .. second.Stdout], Stderr = [.. first.Stderr, .. second.Stderr] };
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace Dbe;

sealed record NativeRecipe(string Source, string[] Options, string Header, string PchFile, string? ObjectFile = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Generation = null);
sealed record MappedCompile(string WorkingDirectory, string SourceArgument, string Include, NativeRecipe? Producer)
{
    internal static string Marker(string path) => "__DBE_NATIVE_INPUT_" + Wire.Hash(Encoding.UTF8.GetBytes(path)) + "__";
    internal static int[] Required(Request request, Execution execution)
    {
        if (execution.Kind != 0 || execution.ExitCode == 0) return [];
        string diagnostics = Encoding.UTF8.GetString(execution.Stdout) + Encoding.UTF8.GetString(execution.Stderr);
        return request.Files!.Select((file, index) => (file, index)).Where(x => x.file.Deferred &&
            diagnostics.Contains(Marker(x.file.Path), StringComparison.Ordinal)).Select(x => x.index).ToArray();
    }
    internal static string InputPath(string absolute)
    {
        if (absolute.Length < 4 || !char.IsAsciiLetter(absolute[0]) || absolute[1] != ':' || absolute[2] != '\\')
            throw new UnsupportedCompile("Native inputs require drive-absolute paths");
        string relative = char.ToUpperInvariant(absolute[0]) + "/" + absolute[3..].Replace('\\', '/');
        Wire.ValidatePath(relative, 512);
        return relative;
    }
    internal static string InputDirectory(string absolute) => InputPath(Path.TrimEndingDirectorySeparator(absolute));
    internal static string SourcePath(string argument, string directory)
    {
        if (string.IsNullOrEmpty(argument) || argument[0] is '/' or '-' or '@' || argument.Length > 32760 ||
            argument.Contains('\0') || argument.Contains('\r') || argument.Contains('\n'))
            throw new Rejected("Expected a source argument, not an option or response file");
        return InputPath(Path.GetFullPath(argument, directory));
    }
    internal void Validate(Request request, HashSet<string> paths)
    {
        InputDirectory(WorkingDirectory);
        if (SourcePath(SourceArgument, WorkingDirectory) != request.Source) throw new Rejected("Native source identity mismatch");
        ValidateOptions(request.Options);
        if (Include.Length > 32760 || Include.Contains('\0')) throw new Rejected("Invalid native include environment");
        foreach (string root in Include.Split(';', StringSplitOptions.RemoveEmptyEntries)) InputDirectory(Path.GetFullPath(root, WorkingDirectory));
        if (Producer is { } producer)
        {
            Wire.ValidatePath(producer.Header);
            InputPath(producer.PchFile);
            if (producer.ObjectFile is not null) InputPath(producer.ObjectFile);
            if (!paths.Contains(SourcePath(producer.Source, WorkingDirectory))) throw new Rejected("Missing PCH producer");
            ValidateOptions(producer.Options);
            if (request.Version >= 6 && (producer.ObjectFile is null || !Guid.TryParseExact(producer.Generation, "N", out _)))
                throw new Rejected("Reusable PCH requires producer object and generation");
            if (request.Version >= 6 && producer.Options.Contains("/Zi") != request.ReturnsPdb)
                throw new Rejected("Reusable PCH requires matching debug profiles");
            if (request.Version >= 6 && request.Options!.Contains("/GL")) throw new Rejected("Reusable LTCG PCH is not supported");
            if (paths.Contains(InputPath(producer.PchFile)) || producer.ObjectFile is not null && paths.Contains(InputPath(producer.ObjectFile)))
                throw new Rejected("Native outputs overlap immutable inputs");
        }
    }
    internal static void ValidateOptions(string[]? options)
    {
        if (options is not { Length: <= 256 }) throw new Rejected("Invalid native compiler profile");
        foreach (string option in options)
        {
            if (option is null || option.Length > 4096 || option.Contains('\0') || option.Contains('\r') || option.Contains('\n'))
                throw new Rejected("Invalid native compiler option");
            if (CompileOptions.Supports(option) || option is "/X" or "/showIncludes" or "/external:anglebrackets" or "/external:env:INCLUDE") continue;
            if (option.StartsWith("/I", StringComparison.Ordinal) || option.StartsWith("/FI", StringComparison.Ordinal) ||
                option.StartsWith("/external:I", StringComparison.Ordinal))
            {
                if (option.Length <= 2) throw new Rejected("Missing native input option value");
                continue;
            }
            if (option.StartsWith("/D", StringComparison.Ordinal) || option.StartsWith("/U", StringComparison.Ordinal))
            {
                if (option.Length <= 2) throw new Rejected("Missing macro");
                continue;
            }
            throw new Rejected("Unsupported native compiler option");
        }
    }

    internal Execution Run(string compiler, string workspace, Request request, int seconds, int memoryMiB, CancellationToken token, string? pdbName)
    {
        var environment = new MappedCompiler(compiler, Path.Combine(workspace, "inputs"), this);
        string? pdb = pdbName is null ? null : Path.Combine(workspace, pdbName);
        Execution? first = Producer is { } p ? environment.Produce(p, workspace, pdb, seconds, memoryMiB, token) : null;
        if (first is not null && (first.Kind != 0 || first.ExitCode != 0)) return first;
        if (Producer?.ObjectFile is { } original)
            File.Copy(environment.Resolve(original), Path.Combine(workspace, "producer.obj"));
        var result = environment.Consume(request, workspace, pdb, seconds, memoryMiB, token);
        if (first is null || result.Kind == 5) return result;
        if (result.Stdout.Length + first.Stdout.Length > Wire.DiagnosticLimit || result.Stderr.Length + first.Stderr.Length > Wire.DiagnosticLimit)
            return result with { Kind = 4 };
        return result with { Stdout = [..first.Stdout, ..result.Stdout], Stderr = [..first.Stderr, ..result.Stderr] };
    }
    internal static string PhysicalPath(string path)
    {
        path = Path.GetFullPath(path);
        var buffer = new StringBuilder(32768);
        if (QueryDosDevice(path[..2], buffer, buffer.Capacity) == 0) throw new Rejected("Unable to resolve physical volume");
        return @"\\?\GLOBALROOT" + buffer + path[2..];
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, StringBuilder target, int capacity);
}

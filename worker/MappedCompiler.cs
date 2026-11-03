using System.Text;

namespace Dbe;

// One private input namespace; output, temporary files and PDBs remain per job.
sealed class MappedCompiler
{
    private readonly string inputs, executable, mapping;
    private readonly MappedCompile context;
    internal MappedCompiler(string compiler, string inputs, MappedCompile context)
    {
        this.inputs = inputs; this.context = context;
        Directory.CreateDirectory(ResolveDirectory(context.WorkingDirectory));
        var roots = Directory.GetDirectories(inputs).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Any(x => Path.GetFileName(x).Length != 1)) throw new Rejected("Invalid mapped drive root");
        char drive = Enumerable.Range('A', 26).Select(x => (char)x).Reverse()
            .First(x => !roots.Any(p => Path.GetFileName(p).Equals(x.ToString(), StringComparison.OrdinalIgnoreCase)));
        string binary = Path.GetDirectoryName(compiler)!;
        mapping = string.Join('\n', roots.Select(x => Path.GetFileName(x).ToUpperInvariant() + "=" + char.ToUpperInvariant(x[0]) + x[1..])
            .Append(drive + "=" + char.ToUpperInvariant(binary[0]) + binary[1..]));
        executable = drive + @":\cl.exe";
    }
    internal string Resolve(string path) => Path.Combine(inputs, MappedCompile.InputPath(path).Replace('/', '\\'));
    private string ResolveDirectory(string path) => Path.Combine(inputs, MappedCompile.InputDirectory(path).Replace('/', '\\'));
    internal Execution Produce(NativeRecipe recipe, string workspace, string? pdb, int seconds, int memory, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Resolve(recipe.PchFile))!);
        string destination = recipe.ObjectFile ?? MappedCompile.PhysicalPath(Path.Combine(workspace, "producer.obj"));
        if (recipe.ObjectFile is not null) Directory.CreateDirectory(Path.GetDirectoryName(Resolve(recipe.ObjectFile))!);
        return Compile([recipe.Source], recipe.Options, recipe, true, destination, workspace, pdb, seconds, memory, token);
    }
    internal Execution Consume(Request request, string workspace, string? pdb, int seconds, int memory, CancellationToken token) =>
        Compile([request.NativeInput!.SourceArgument], request.Options!, context.Producer, false,
            MappedCompile.PhysicalPath(Path.Combine(workspace, "result.obj")), workspace, pdb, seconds, memory, token);
    internal Execution ConsumeBatch(Request request, string workspace, string? pdb, int seconds, int memory, CancellationToken token)
    {
        var sources = request.BatchSources ?? throw new Rejected("Missing batch sources");
        string objects = Directory.CreateDirectory(Path.Combine(workspace, "batch-objects")).FullName;
        Execution result = Compile(sources.Select(x => x.Argument), request.Options!, context.Producer, false,
            MappedCompile.PhysicalPath(objects) + "\\", workspace, pdb, seconds, memory, token);
        if (result.Kind == 0 && result.ExitCode == 0)
            for (int i = 0; i < sources.Length; i++)
                File.Move(Path.Combine(objects, Path.GetFileNameWithoutExtension(sources[i].Path) + ".obj"),
                    Path.Combine(workspace, NativeBatch.ObjectName(i)));
        return result;
    }
    private Execution Compile(IEnumerable<string> sources, string[] options, NativeRecipe? recipe, bool producer, string output,
        string workspace, string? pdb, int seconds, int memory, CancellationToken token)
    {
        var arguments = options.Concat(["/nologo", "/c", "/TP", "/Fo" + output]).ToList();
        if (recipe is not null) arguments.AddRange([(producer ? "/Yc" : "/Yu") + recipe.Header, "/Fp" + recipe.PchFile]);
        if (pdb is not null) arguments.Add("/Fd" + MappedCompile.PhysicalPath(pdb));
        arguments.AddRange(sources);
        string response = Path.Combine(workspace, producer ? "producer.rsp" : "consumer.rsp");
        File.WriteAllText(response, string.Join(' ', arguments.Select(ClInvocation.Quote)), Encoding.Unicode);
        string temporary = MappedCompile.PhysicalPath(Directory.CreateDirectory(Path.Combine(workspace, "tmp")).FullName);
        return Native.Run(executable, workspace, "@" + ClInvocation.Quote(MappedCompile.PhysicalPath(response)), seconds, memory, token,
            tempDirectory: temporary, mapping: mapping, logicalCwd: context.WorkingDirectory, include: context.Include);
    }
}

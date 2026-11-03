namespace Dbe;

sealed record NativeSource(string Path, string Argument);

// A bounded native CL invocation owns one writable PDB and all of its objects.
// One lease still runs one compiler, with the existing incoming memory limit.
static class NativeBatch
{
    // Start conservatively: at most four full-size objects plus symbols fit
    // below 1 GiB per incoming lease, leaving disk headroom for both slots.
    internal const int MaxSources = 4;
    internal static string ObjectName(int index) => "result-" + index + ".obj";
    internal static Request Single(Request batch, NativeSource source) => batch with {
        Version = 6, Source = source.Path, BatchSources = null,
        NativeInput = batch.NativeInput! with { SourceArgument = source.Argument }
    };
    internal static void Validate(Request request, HashSet<string>? paths = null)
    {
        if (request.Version != 7 || request.Operation != "compile" || request.NativeInput is null ||
            request.BatchSources is not { Length: >= 2 and <= MaxSources } sources)
            throw new Rejected("Invalid native batch");
        if (sources[0] is null || sources[0].Path != request.Source || sources[0].Argument != request.NativeInput.SourceArgument)
            throw new Rejected("Batch primary source mismatch");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (source is null || string.IsNullOrEmpty(source.Argument) || source.Argument.Length > 32760 ||
                source.Argument.Contains('\0') || source.Argument.Contains('\r') || source.Argument.Contains('\n'))
                throw new Rejected("Invalid batch source argument");
            Wire.ValidatePath(source.Path, 512);
            if (!new[] { ".cpp", ".cc", ".cxx" }.Contains(Path.GetExtension(source.Path), StringComparer.OrdinalIgnoreCase) ||
                !identities.Add(source.Path) || !objects.Add(Path.GetFileNameWithoutExtension(source.Path)))
                throw new Rejected("Batch source or object collision");
            if (MappedCompile.SourcePath(source.Argument, request.NativeInput.WorkingDirectory) != source.Path ||
                paths is not null && !paths.Contains(source.Path)) throw new Rejected("Batch source is outside its context");
        }
    }
    internal static InputFile[] ValidateOutputs(Request request, Reply reply)
    {
        if (reply.Output is not null) throw new Rejected("Batch returned a scalar object");
        if (reply.Status != "completed")
        {
            if (reply.Outputs is not null) throw new Rejected("Failed batch returned objects");
            return [];
        }
        if (reply.ExitCode != 0 || reply.Outputs is null || reply.Outputs.Length != request.BatchSources!.Length)
            throw new Rejected("Incomplete native batch result");
        for (int i = 0; i < reply.Outputs.Length; i++)
        {
            InputFile file = reply.Outputs[i];
            if (file is null || file.Path != ObjectName(i) || file.Length is <= 0 or > Wire.PreparedObjectLimit ||
                !Wire.ValidHash(file.Sha256)) throw new Rejected("Invalid batch object manifest");
        }
        return reply.Outputs;
    }
}

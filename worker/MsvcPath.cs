namespace Dbe;

// Single owner of how MSVC turns /Fo, /Fd and /Fp values into the file it writes. Consumers must
// use these resolved paths rather than reinterpret option text.
//
// Verified with MSVC 19.44 (cl.exe launched with an explicit cwd and per-drive "=X:" directory):
// - "/Fo:" "/Fd:" "/Fp:" colon forms are accepted; the last occurrence wins.
// - A bare switch keeps its default and never consumes the next argument (that argument is
//   treated as an input file).
// - An empty value or one ending in '\' or '/' names a directory that receives the default name.
// - Without a trailing separator the value is a file name even when a directory of that name
//   exists; a missing extension gets the option's extension, any other extension is kept.
// - A drive-relative value ("C:name") uses the working directory when it is on that drive,
//   otherwise the drive's current directory from the inherited "=C:" variable, otherwise the root.
sealed record MsvcPathContext(string WorkingDirectory, Func<char, string?> DriveDirectory)
{
    // The compiler inherits the adapter's working directory and environment block (including the
    // hidden per-drive entries preserved by the native launcher), so the process state is the
    // compiler's state.
    internal static MsvcPathContext Process() =>
        new(Environment.CurrentDirectory, drive => Environment.GetEnvironmentVariable("=" + drive + ":"));
}

static class MsvcPath
{
    internal const string DefaultPdbName = "vc140.pdb";
    internal const string DefaultPchName = "vc140.pch";

    // Value of an already '/'-normalized switch such as "/Fo:x.obj" or "/Fox.obj".
    internal static string OptionValue(string arg, string prefix)
    {
        string value = arg[prefix.Length..];
        return value.StartsWith(':') ? value[1..] : value;
    }

    internal static bool NamesDirectory(string value) => value.Length == 0 || value.EndsWith('\\') || value.EndsWith('/');

    internal static string ResolveOutput(string value, string defaultName, string extension, MsvcPathContext context)
    {
        if (NamesDirectory(value)) value += defaultName;
        else if (!Path.HasExtension(value)) value += extension;
        return Absolute(value, context);
    }

    internal static string Absolute(string value, MsvcPathContext context)
    {
        bool driveRelative = value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':' &&
            (value.Length == 2 || value[2] is not ('\\' or '/'));
        if (!driveRelative) return Path.GetFullPath(value, Path.GetFullPath(context.WorkingDirectory));
        char drive = char.ToUpperInvariant(value[0]);
        string cwd = Path.GetFullPath(context.WorkingDirectory);
        string baseDirectory = cwd.Length >= 2 && cwd[1] == ':' && char.ToUpperInvariant(cwd[0]) == drive
            ? cwd
            : context.DriveDirectory(drive) is { Length: > 0 } current ? current : drive + ":\\";
        return Path.GetFullPath(Path.Combine(baseDirectory, value[2..]));
    }
}

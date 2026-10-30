using System.Runtime.InteropServices;
using System.Text;

namespace Dbe;
sealed class UnsupportedCompile(string reason) : Exception(reason);
sealed record ClInvocation(string Source, string Object, string[] PreprocessOptions, string[] RemoteOptions, string[] LocalOptions, int CodePage, string? PchHeader, string? CompilerPdb)
{
    internal static ClInvocation[] ParseBatch(string[] args)
    {
        var all = Expand(args);
        List<(int Index, string Path)> sources = []; string? output = null;
        List<string> preprocess = [], remote = [];
        bool compile = false;
        int codePage = (int)GetACP(); string? pchHeader = null;
        for (int i = 0; i < all.Count; i++)
        {
            string arg = all[i];
            // CL accepts '-' and '/' switch prefixes. Keep the original list for fallback.
            if (arg.StartsWith('-')) arg = "/" + arg[1..];
            string Value(string prefix) => arg.Length == prefix.Length
                ? ++i < all.Count ? all[i] : throw new UnsupportedCompile("Missing value for " + prefix)
                : arg[prefix.Length..];
            if (arg == "/c") { compile = true; continue; }
            if (arg.StartsWith("/MP", StringComparison.Ordinal) && (arg.Length == 3 || int.TryParse(arg[3..], out int n) && n > 0)) continue;
            if (arg == "/Y-") { pchHeader = null; continue; }
            if (arg.StartsWith("/Yu", StringComparison.Ordinal)) { pchHeader = Value("/Yu"); continue; }
            if (arg is "/nologo" or "/TP" or "/Gm-" or "/errorReport:prompt" or "/errorReport:none" or "/errorReport:queue") continue;
            if (arg == "/external:anglebrackets" || arg.StartsWith("/external:env:", StringComparison.Ordinal)) { preprocess.Add(arg); continue; }
            if (arg.StartsWith("/external:I", StringComparison.Ordinal)) { preprocess.Add("/external:I" + Value("/external:I")); continue; }
            if (arg.StartsWith("/Fo", StringComparison.Ordinal)) { output = Value("/Fo"); continue; }
            if (arg.StartsWith("/Fd", StringComparison.Ordinal) || arg.StartsWith("/Fp", StringComparison.Ordinal)) { _ = Value(arg[..3]); continue; }
            string? inputOption = new[] { "/FI", "/I", "/D", "/U" }.FirstOrDefault(p => arg.StartsWith(p, StringComparison.Ordinal));
            if (inputOption is not null) { preprocess.Add(inputOption + Value(inputOption)); continue; }
            if (arg is "/X" or "/showIncludes") { preprocess.Add(arg); continue; }
            if (arg == "/utf-8") codePage = 65001;
            if (arg.StartsWith("/source-charset:", StringComparison.Ordinal) || arg.StartsWith("/execution-charset:", StringComparison.Ordinal))
                throw new UnsupportedCompile("Explicit non-/utf-8 charset overrides need separate validation");
            if (CompileOptions.Supports(arg)) { preprocess.Add(arg); remote.Add(arg); continue; }
            if (!arg.StartsWith('/') && !arg.StartsWith('-') && new[] { ".cpp", ".cc", ".cxx" }.Contains(Path.GetExtension(arg), StringComparer.OrdinalIgnoreCase))
            {
                sources.Add((i, Path.GetFullPath(arg))); continue;
            }
            throw new UnsupportedCompile("Unsupported compiler argument: " + arg);
        }
        if (!compile || sources.Count == 0 || sources.Any(x => !File.Exists(x.Path))) throw new UnsupportedCompile("Expected /c and existing cpp sources");
        if (!remote.Contains("/utf-8")) { remote.Add("/source-charset:." + codePage); remote.Add("/execution-charset:." + codePage); }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CL")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("_CL_")))
            throw new UnsupportedCompile("CL/_CL_ environment options require local execution");
        CompileOptions.Validate(remote.ToArray(), 3);
        var sourceIndices = sources.Select(x => x.Index).ToHashSet();
        var common = all.Where((_, index) => !sourceIndices.Contains(index)).ToArray();
        List<ClInvocation> batch = [];
        foreach (var source in sources)
        {
            string destination = string.IsNullOrEmpty(output) ? Path.GetFileNameWithoutExtension(source.Path) + ".obj" : output;
            bool directory = destination.EndsWith('\\') || destination.EndsWith('/') || Directory.Exists(destination);
            if (sources.Count > 1 && !string.IsNullOrEmpty(output) && !directory) throw new UnsupportedCompile("Multiple sources require an object directory");
            if (directory) destination = Path.Combine(destination, Path.GetFileNameWithoutExtension(source.Path) + ".obj");
            destination = Path.GetFullPath(destination);
            if (!Path.GetExtension(destination).Equals(".obj", StringComparison.OrdinalIgnoreCase)) throw new UnsupportedCompile("Expected an object output");
            batch.Add(new(source.Path, destination, preprocess.ToArray(), remote.ToArray(), [..common, source.Path], codePage, pchHeader, PdbIdentity(all)));
        }
        if (batch.Select(x => x.Object).Distinct(StringComparer.OrdinalIgnoreCase).Count() != batch.Count) throw new UnsupportedCompile("Object filename collision");
        return batch.ToArray();
    }

    internal static bool CreatesPch(string[] args) => Expand(args).Any(x => x.StartsWith("/Yc", StringComparison.Ordinal) || x.StartsWith("-Yc", StringComparison.Ordinal));
    internal static string? PdbIdentity(IReadOnlyList<string> args)
    {
        if (!args.Any(x => x is "/Zi" or "/ZI" or "-Zi" or "-ZI")) return null;
        string pdb = Path.Combine(Environment.CurrentDirectory, "default-compiler-pdb");
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i].StartsWith('-') ? "/" + args[i][1..] : args[i];
            if (!arg.StartsWith("/Fd", StringComparison.Ordinal)) continue;
            pdb = arg.Length > 3 ? arg[3..] : ++i < args.Count ? args[i] : throw new UnsupportedCompile("Missing /Fd");
        }
        return Path.GetFullPath(pdb).ToUpperInvariant();
    }

    internal static List<string> Expand(string[] args)
    {
        List<string> result = [];
        foreach (string arg in args)
        {
            if (!arg.StartsWith('@')) { result.Add(arg); continue; }
            string path = Path.GetFullPath(arg[1..]);
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) throw new UnsupportedCompile("Invalid or oversized response file");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var reader = new StreamReader(path, Encoding.GetEncoding((int)GetACP()), true);
            string[] expanded = Split(reader.ReadToEnd());
            if (expanded.Any(x => x.StartsWith('@'))) throw new UnsupportedCompile("Nested response files require local execution");
            result.AddRange(expanded);
        }
        return result;
    }

    // MSVC/CRT quote and backslash rules, with response-file newlines as whitespace.
    internal static string[] Split(string text)
    {
        List<string> output = [];
        for (int i = 0; i < text.Length;)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i == text.Length) break;
            bool quoted = false; StringBuilder word = new();
            while (i < text.Length && (quoted || !char.IsWhiteSpace(text[i])))
            {
                int slashes = 0;
                while (i < text.Length && text[i] == '\\') { slashes++; i++; }
                if (i < text.Length && text[i] == '"')
                {
                    word.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) { word.Append('"'); i++; }
                    else if (quoted && i + 1 < text.Length && text[i + 1] == '"') { word.Append('"'); i += 2; }
                    else { quoted = !quoted; i++; }
                }
                else { word.Append('\\', slashes); if (i < text.Length && (quoted || !char.IsWhiteSpace(text[i]))) word.Append(text[i++]); }
            }
            if (quoted) throw new UnsupportedCompile("Unbalanced response-file quotes");
            output.Add(word.ToString());
        }
        return output.ToArray();
    }
    internal static string Quote(string arg)
    {
        StringBuilder result = new("\""); int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); slashes = 0;
            result.Append(c);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    internal static string RawTail()
    {
        string command = Environment.CommandLine; bool quoted = false; int i = 0;
        for (; i < command.Length; i++)
        {
            if (command[i] == '"') quoted = !quoted;
            else if (!quoted && char.IsWhiteSpace(command[i])) break;
        }
        return command[i..];
    }
    [DllImport("kernel32.dll")] private static extern uint GetACP();
}

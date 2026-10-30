using System.Text;
using System.Text.RegularExpressions;

namespace Dbe;

// Owns the encoding/line-path interpretation of MSVC /P output. /P uses the
// selected source code page and can lose characters from BOM-marked inputs;
// never turn that loss into a successful remote build.
static partial class PreparedInput
{
    internal static Encoding EncodingFor(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static string ReadSource(string path, Encoding fallback)
    {
        if (new FileInfo(path).Length > Wire.PreparedLimit) throw new UnsupportedCompile("Source exceeds validation budget");
        // MSVC accepts legacy headers with undecodable bytes in comments. Decode
        // with a sentinel, then validate code tokens; do not reject a build only
        // because an SDK/vendor comment uses another encoding.
        var sourceEncoding = (Encoding)fallback.Clone();
        sourceEncoding.DecoderFallback = new DecoderReplacementFallback("\uFFFD");
        using var reader = new StreamReader(path, sourceEncoding, true);
        string text = reader.ReadToEnd();
        // Throw on characters /P would replace with '?' in its output code page.
        try { _ = fallback.GetByteCount(WithoutComments(text)); }
        catch (EncoderFallbackException) { throw new UnsupportedCompile("Source code cannot round-trip through preprocessor code page: " + path); }
        return text;
    }

    private static string WithoutComments(string text)
    {
        StringBuilder code = new();
        for (int i = 0; i < text.Length;)
        {
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
            { i += 2; while (i < text.Length && text[i] != '\n') i++; continue; }
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            { int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? text.Length : end + 2; continue; }
            if (i + 1 < text.Length && text[i] == 'R' && text[i + 1] == '"')
            {
                int open = text.IndexOf('(', i + 2);
                if (open < 0 || open - i > 18) throw new UnsupportedCompile("Cannot validate raw string delimiter");
                string close = ")" + text[(i + 2)..open] + "\"";
                int end = text.IndexOf(close, open + 1, StringComparison.Ordinal);
                if (end < 0) throw new UnsupportedCompile("Cannot validate unterminated raw string");
                code.Append(text, i, end + close.Length - i); i = end + close.Length; continue;
            }
            char c = text[i++]; code.Append(c);
            bool separator = c == '\'' && i >= 2 && char.IsLetterOrDigit(text[i - 2]) && i < text.Length && char.IsLetterOrDigit(text[i]);
            if ((c == '"' || c == '\'') && !separator)
                while (i < text.Length)
                {
                    char next = text[i++]; code.Append(next);
                    if (next == '\\' && i < text.Length) code.Append(text[i++]);
                    else if (next == c) break;
                }
        }
        return code.ToString();
    }

    internal static string PrepareSource(ClInvocation invocation, string attemptRoot)
    {
        if (invocation.PchHeader is null) return invocation.Source;
        string text = ReadSource(invocation.Source, EncodingFor(invocation.CodePage));
        string trimmed = LeadingComments().Replace(text, "");
        Match include = FirstInclude().Match(trimmed);
        if (!include.Success || !string.Equals(include.Groups[1].Value.Replace('\\', '/'), invocation.PchHeader.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new UnsupportedCompile("Remote PCH use requires the PCH header as the first source directive");
        if (File.Exists(Path.Combine(Path.GetDirectoryName(invocation.Source)!, invocation.PchHeader))) return invocation.Source;
        // Moving an ordinary subdirectory source would change quoted-include
        // lookup. Only an entry whose remaining includes are absolute (as in
        // MSBuild's generated unity entry) can be relocated without that change.
        string remaining = WithoutComments(trimmed[include.Length..]);
        foreach (string line in remaining.Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            Match absolute = AbsoluteInclude().Match(line.Trim());
            if (!absolute.Success || !Path.IsPathFullyQualified(absolute.Groups[1].Value))
                throw new UnsupportedCompile("PCH expansion would relocate a relative or macro include; preserve original source locally: " + invocation.Source);
        }
        // MSBuild unity sources live under IntDir. /Yu can consume their initial
        // header from the PCH without opening it. Our expanded path must resolve
        // that one include explicitly; adding a global /I could change unrelated
        // include resolution. Preserve __FILE__/line locations in the snapshot.
        string header = Path.GetFullPath(invocation.PchHeader);
        if (!File.Exists(header)) throw new UnsupportedCompile("Cannot resolve the PCH header for expanded compilation");
        int at = text.Length - trimmed.Length + include.Groups[1].Index;
        text = text[..at] + header.Replace('\\', '/') + text[(at + include.Groups[1].Length)..];
        string entry = Path.Combine(attemptRoot, "entry.cpp");
        File.WriteAllText(entry, "#line 1 \"" + invocation.Source.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"\n" + text, EncodingFor(invocation.CodePage));
        return entry;
    }

    internal static void Normalize(string path, ClInvocation invocation)
    {
        Encoding encoding = EncodingFor(invocation.CodePage);
        string text = File.ReadAllText(path, encoding);
        if (ModuleDeclaration().IsMatch(text)) throw new UnsupportedCompile("Module/import declarations require local execution");
        HashSet<string> checkedFiles = new(StringComparer.OrdinalIgnoreCase);
        checkedFiles.Add(invocation.Source);
        _ = ReadSource(invocation.Source, encoding);
        text = LineDirective().Replace(text, match =>
        {
            string source = match.Groups[2].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
            if (source.StartsWith('<') || !File.Exists(source)) return match.Value;
            source = Path.GetFullPath(source);
            if (checkedFiles.Add(source)) _ = ReadSource(source, encoding);
            return match.Groups[1].Value + source.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        });
        File.WriteAllText(path, text, encoding);
    }

    [GeneratedRegex(@"\A(?:\s+|/\*[\s\S]*?\*/|//[^\r\n]*(?:\r?\n|$))*", RegexOptions.CultureInvariant)] private static partial Regex LeadingComments();
    [GeneratedRegex("^#\\s*include\\s*[\"<]([^\">]+)[\">]", RegexOptions.CultureInvariant)] private static partial Regex FirstInclude();
    [GeneratedRegex("^\\s*#\\s*include\\s*\"([^\"]+)\"\\s*$", RegexOptions.CultureInvariant)] private static partial Regex AbsoluteInclude();
    [GeneratedRegex(@"^\s*(?:export\s+)?(?:module|import)\s*(?:[<\"";:]|[A-Za-z_]\w*\s*[.;:])", RegexOptions.Multiline | RegexOptions.CultureInvariant)] private static partial Regex ModuleDeclaration();
    [GeneratedRegex("^(#(?:line)?\\s+[0-9]+\\s+\")((?:\\\\.|[^\"\\r\\n])*)\"", RegexOptions.Multiline | RegexOptions.CultureInvariant)] private static partial Regex LineDirective();
}

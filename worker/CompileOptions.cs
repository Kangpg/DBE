using System.Text.RegularExpressions;
namespace Dbe;

// Sole owner of the versioned code-generation vocabulary. Paths, plugins, response
// files, PCH, modules and output switches are deliberately absent.
static partial class CompileOptions
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "/Od", "/O1", "/O2", "/Ox", "/Ob0", "/Ob1", "/Ob2", "/Ob3", "/Oi", "/Oi-", "/Os", "/Ot", "/Oy", "/Oy-",
        "/Z7", "/Zi", "/FS", "/GL", "/GL-", "/MD", "/MDd", "/MT", "/MTd", "/EHsc", "/EHs", "/EHa", "/EHsc-",
        "/GR", "/GR-", "/GS", "/GS-", "/Gy", "/Gy-", "/Gw", "/Gw-", "/GF", "/GF-", "/Gd", "/Gz", "/Gv", "/Gr",
        "/std:c++14", "/std:c++17", "/std:c++20", "/std:c++latest", "/permissive-", "/permissive",
        "/Zc:wchar_t", "/Zc:wchar_t-", "/Zc:forScope", "/Zc:forScope-", "/Zc:inline", "/Zc:inline-",
        "/Zc:__cplusplus", "/Zc:preprocessor", "/Zc:preprocessor-", "/Zc:strictStrings", "/Zc:strictStrings-",
        "/Zc:twoPhase-", "/Zc:threadSafeInit", "/Zc:threadSafeInit-", "/Zc:sizedDealloc", "/Zc:sizedDealloc-",
        "/fp:precise", "/fp:strict", "/fp:fast", "/fp:except", "/fp:except-",
        "/W0", "/W1", "/W2", "/W3", "/W4", "/Wall", "/WX", "/WX-", "/w", "/sdl", "/sdl-",
        "/diagnostics:classic", "/diagnostics:column", "/diagnostics:caret", "/FC", "/utf-8", "/J", "/JMC", "/JMC-",
        "/RTC1", "/RTCs", "/RTCu", "/RTCc", "/Zl", "/bigobj", "/volatile:iso", "/volatile:ms",
        "/external:W0", "/external:W1", "/external:W2", "/external:W3", "/external:W4", "/external:templates-"
    };
    [GeneratedRegex("^/(wd|we|wo)[0-9]{4,5}$|^/Zp(1|2|4|8|16)$|^/Zm[0-9]{1,4}$|^/(source|execution)-charset:\\.[0-9]{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericOption();
    internal static bool Supports(string option) => Allowed.Contains(option) || NumericOption().IsMatch(option);
    internal static void Validate(string[]? options, int version = 2)
    {
        if (options is null || options.Length > 128 || options.Any(x => x is null || !Supports(x)))
            throw new Rejected("Unsupported compiler options");
        if (version < 3 && options.Any(x => x == "/Zi" || x.StartsWith("/source-charset:") || x.StartsWith("/execution-charset:")))
            throw new Rejected("PDB and explicit code pages require protocol v3");
        if (!options.Contains("/utf-8") && !(version == 3 && options.Any(x => x.StartsWith("/source-charset:.")) && options.Any(x => x.StartsWith("/execution-charset:."))))
            throw new Rejected("Preprocessed input requires explicit source and execution encoding");
    }
}

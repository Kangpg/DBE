using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

// MSBuild still owns content/command/PCH tracking. This guard additionally
// invalidates when an include search directory gains or loses a file, including
// a previously absent __has_include input. No source contents are modified.
public sealed class DbeInputInventory : Task
{
    // A solution directory usually also holds other projects' outputs, which appear
    // during the build under many names. Counting them would make the next unchanged
    // build look like an include membership change, so only files that could newly
    // satisfy an #include count. MSBuild's tracking still covers every file actually read.
    private static readonly HashSet<string> IncludeCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "", ".h", ".hh", ".hpp", ".hxx", ".h++", ".inl", ".inc", ".ipp", ".tpp", ".tcc", ".def", ".c", ".cc", ".cpp", ".cxx" };
    private static readonly HashSet<string> BuildDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".svn", ".vs", "ipch" };
    [Required] public string ProjectDirectory { get; set; }
    [Required] public string StateFile { get; set; }
    public ITaskItem[] Sources { get; set; }
    public ITaskItem[] ProjectReferences { get; set; }
    public string[] SearchRoots { get; set; }
    public string[] ExcludedRoots { get; set; }
    public string[] RuntimeInputs { get; set; }
    public string TrackerDirectory { get; set; }
    public string[] ObservedInputExclusions { get; set; }
    public string[] TrackedRuntimeRoots { get; set; }
    public bool NativeLocal { get; set; }
    [Output] public string[] InvalidatedObjects { get; set; } = new string[0];
    [Output] public string[] TrackedExclusions { get; set; }
    [Output] public bool Changed { get; set; }
    [Output] public string Fingerprint { get; set; }
    private string Full(string path)
    {
        try { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectDirectory, path)); }
        catch (Exception error) { throw new IOException("Invalid include inventory path '" + path + "': " + error.Message); }
    }
    private static IEnumerable<string> SplitPaths(string value)
    {
        // A transformed MSBuild item's metadata can arrive as one semicolon
        // list inside the task array. Quotes delimit an individual path.
        var path = new StringBuilder(); bool quoted = false;
        foreach (char c in value)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ';' && !quoted) { yield return path.ToString(); path.Clear(); }
            else path.Append(c);
        }
        if (quoted) throw new IOException("Unbalanced include path quotes");
        yield return path.ToString();
    }
    // Only invalidate objects observed as compiler outputs for the same
    // declared sources. MP writes may be recorded under a composite root.
    private string[] LocalObjects()
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Sources ?? new ITaskItem[0]) {
            if (String.Equals(item.GetMetadata("ExcludedFromBuild"), "true", StringComparison.OrdinalIgnoreCase)) continue;
            var name = item.GetMetadata("ObjectFileName");
            if (String.IsNullOrWhiteSpace(name)) continue;
            if (name.EndsWith("\\") || name.EndsWith("/"))
                name = Path.Combine(name, Path.GetFileNameWithoutExtension(item.ItemSpec) + ".obj");
            expected[Full(item.ItemSpec)] = Full(name);
        }
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = Full(TrackerDirectory);
        if (!Directory.Exists(directory)) return new string[0];
        foreach (var log in Directory.EnumerateFiles(directory, "dbe-remote-cl*.write.*.tlog")) {
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(log, Encoding.Unicode)) {
                if (line.StartsWith("^", StringComparison.Ordinal)) {
                    active.Clear();
                    foreach (var source in line.Substring(1).Split('|')) {
                        string output;
                        if (expected.TryGetValue(source, out output)) active.Add(output);
                    }
                } else if (active.Contains(line) && File.Exists(line)) result.Add(line);
            }
        }
        return result.ToArray();
    }
    public override bool Execute()
    {
        Changed = true;
        try
        {
            var exclusions = (ExcludedRoots ?? new string[0]).Where(x => !String.IsNullOrWhiteSpace(x))
                .Select(x => Full(x).TrimEnd('\\', '/') + "\\").ToArray();
            Func<string, bool> excluded = path => exclusions.Any(x => (path.TrimEnd('\\') + "\\").StartsWith(x, StringComparison.OrdinalIgnoreCase));
            var observedExclusions = (ObservedInputExclusions ?? new string[0]).Where(x => !String.IsNullOrWhiteSpace(x))
                .Select(x => Full(x).TrimEnd('\\', '/') + "\\").ToArray();
            var runtimeRoots = (TrackedRuntimeRoots ?? new string[0]).Where(x => !String.IsNullOrWhiteSpace(x))
                .Select(x => Full(x).TrimEnd('\\', '/') + "\\").ToArray();
            var trackedExclusions = new HashSet<string>(runtimeRoots.Select(x => x.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            roots.Add(Full(ProjectDirectory));
            foreach (var source in Sources ?? new ITaskItem[0]) roots.Add(Path.GetDirectoryName(Full(source.ItemSpec)));
            // References are caller-declared source scopes, even when their
            // headers are reached through relative includes rather than /I.
            // Include them before the first compile; learning them only from
            // read tlogs makes the next unchanged build appear to add inputs.
            foreach (var reference in ProjectReferences ?? new ITaskItem[0]) roots.Add(Path.GetDirectoryName(Full(reference.ItemSpec)));
            foreach (var root in (SearchRoots ?? new string[0]).SelectMany(SplitPaths))
                if (!String.IsNullOrWhiteSpace(root)) roots.Add(Full(root.Trim().Trim('"')));
            // An observed absolute include may live outside declared search
            // roots. Its siblings can affect subsequent quoted includes too.
            var observedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!String.IsNullOrEmpty(TrackerDirectory) && Directory.Exists(Full(TrackerDirectory)))
                foreach (var log in Directory.GetFiles(Full(TrackerDirectory), "dbe-remote-cl.read.*.tlog"))
                    foreach (var line in File.ReadLines(log, Encoding.Unicode)) {
                        if (line.StartsWith("^") || !Path.IsPathRooted(line)) continue;
                        // CanonicalTrackedInputFiles excludes exact parent
                        // directories, not a subtree. Include even removed
                        // attempt directories observed in retained tlogs.
                        if (runtimeRoots.Any(x => line.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                            trackedExclusions.Add(Path.GetDirectoryName(line));
                        else if (File.Exists(line) && !excluded(line)
                            && !observedExclusions.Any(x => line.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                            observedRoots.Add(Path.GetDirectoryName(line));
                    }
            TrackedExclusions = trackedExclusions.ToArray();
            var entries = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> visit = null;
            visit = directory =>
            {
                directory = Full(directory);
                if (excluded(directory) || !visited.Add(directory)) return;
                // Only a file can satisfy an #include. A directory appearing empty
                // during the build changes nothing; a file inside it is still counted.
                if (!Directory.Exists(directory)) return;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Include directory is a reparse point: " + directory);
                foreach (var file in Directory.EnumerateFiles(directory))
                    if (IncludeCandidates.Contains(Path.GetExtension(file)))
                        entries.Add("file:" + file.ToUpperInvariant());
                foreach (var child in Directory.EnumerateDirectories(directory))
                    if (!BuildDirectories.Contains(Path.GetFileName(child)) && !child.EndsWith(".tlog", StringComparison.OrdinalIgnoreCase)) visit(child);
            };
            foreach (var root in roots.OrderBy(x => x.Length)) visit(root);
            using (var sha = SHA256.Create())
            {
                foreach (var file in RuntimeInputs ?? new string[0])
                    using (var stream = File.OpenRead(Full(file))) entries.Add("runtime:" + Full(file) + ":" + BitConverter.ToString(sha.ComputeHash(stream)));
                Func<IEnumerable<string>, string> digest = lines => BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(String.Join("\n", lines))));
                var state = new List<string> { digest(entries) };
                // '|' cannot occur in a Windows path, and unlike ';' it stays one MSBuild value.
                var previous = File.Exists(StateFile) ? File.ReadAllText(StateFile).Trim().Split('|') : new string[0];
                Changed = previous.Length == 0 || previous[0] != state[0];
                if (Changed && previous.Length != 0) Log.LogMessage(MessageImportance.High, "DBE: include inventory changed for declared roots; recompiling this project");
                // A directory learned from tracker logs is recorded on first sight, not
                // treated as a change: the compile that read it already saw its contents.
                // Once recorded, any later membership change in it is detected.
                var recorded = previous.Skip(1).Select(x => x.Split('\t')).Where(x => x.Length == 2)
                    .ToDictionary(x => x[1], x => x[0], StringComparer.OrdinalIgnoreCase);
                foreach (var root in observedRoots.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    // Sibling files only, independent of which other roots were seen.
                    var siblings = Directory.Exists(root)
                        ? Directory.EnumerateFiles(root).Where(x => IncludeCandidates.Contains(Path.GetExtension(x))).Select(x => x.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal)
                        : Enumerable.Empty<string>();
                    string value = digest(siblings), known;
                    if (recorded.TryGetValue(root, out known) && known != value)
                    {
                        Changed = true;
                        Log.LogMessage(MessageImportance.High, "DBE: include directory membership changed: " + root);
                    }
                    state.Add(value + "\t" + root);
                }
                Fingerprint = String.Join("|", state);
            }
            if (Changed && NativeLocal && !String.IsNullOrEmpty(TrackerDirectory)) InvalidatedObjects = LocalObjects();
        }
        catch (Exception error)
        {
            // Never turn incomplete coverage into an up-to-date decision.
            Fingerprint = "";
            Log.LogMessage(MessageImportance.High, "DBE: input inventory unavailable; compile revalidation required: " + error.Message);
        }
        return true;
    }
}

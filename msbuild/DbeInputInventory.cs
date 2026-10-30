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
    [Required] public string ProjectDirectory { get; set; }
    [Required] public string StateFile { get; set; }
    public ITaskItem[] Sources { get; set; }
    public string[] SearchRoots { get; set; }
    public string[] ExcludedRoots { get; set; }
    public string[] RuntimeInputs { get; set; }
    public string TrackerDirectory { get; set; }
    public string[] ObservedInputExclusions { get; set; }
    public string[] TrackedRuntimeRoots { get; set; }
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
            foreach (var root in (SearchRoots ?? new string[0]).SelectMany(SplitPaths))
                if (!String.IsNullOrWhiteSpace(root)) roots.Add(Full(root.Trim().Trim('"')));
            // An observed absolute include may live outside declared search
            // roots. Its siblings can affect subsequent quoted includes too.
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
                            roots.Add(Path.GetDirectoryName(line));
                    }
            TrackedExclusions = trackedExclusions.ToArray();
            var entries = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> visit = null;
            visit = directory =>
            {
                directory = Full(directory);
                if (excluded(directory) || !visited.Add(directory)) return;
                if (!Directory.Exists(directory)) { entries.Add("missing:" + directory); return; }
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Include directory is a reparse point: " + directory);
                foreach (var file in Directory.EnumerateFiles(directory)) entries.Add("file:" + file.ToUpperInvariant());
                foreach (var child in Directory.EnumerateDirectories(directory)) visit(child);
            };
            foreach (var root in roots.OrderBy(x => x.Length)) visit(root);
            using (var sha = SHA256.Create())
            {
                foreach (var file in RuntimeInputs ?? new string[0])
                    using (var stream = File.OpenRead(Full(file))) entries.Add("runtime:" + Full(file) + ":" + BitConverter.ToString(sha.ComputeHash(stream)));
                Fingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(String.Join("\n", entries))));
            }
            Changed = !File.Exists(StateFile) || File.ReadAllText(StateFile).Trim() != Fingerprint;
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

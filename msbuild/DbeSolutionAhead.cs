using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

// VS asks each project to build in dependency order and never says whether this is a
// whole-solution build. The DBE extension records that fact in a marker. The first
// project to arrive compiles the solution ahead; the others wait with their nodes
// released, then every project continues its ordinary Build in VS's order.
public sealed class DbeSolutionAhead : Task, ICancelableTask
{
    [Required] public string Solution { get; set; }
    public string Configurations { get; set; }
    public string MarkerDirectory { get; set; }
    public bool Complete { get; set; }
    [Output] public ITaskItem[] Projects { get; set; } = new ITaskItem[0];
    [Output] public bool Owner { get; set; }
    // A waiter must always proceed: the ordinary Build is correct without compile-ahead,
    // and a stuck wait would hang the user's IDE.
    private static readonly TimeSpan WaitLimit = TimeSpan.FromHours(1);
    private readonly ManualResetEventSlim cancel = new ManualResetEventSlim(false);
    public void Cancel() => cancel.Set();
    private static string Key(string solution)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(solution).ToUpperInvariant()))).Replace("-", "");
    }
    public override bool Execute()
    {
        // Compile-ahead is an optimization: no failure here may affect the build.
        try
        {
            string marker = Path.Combine(MarkerDirectory, Key(Solution));
            string[] parts = File.Exists(marker) ? File.ReadAllText(marker).Split(' ') : new string[0];
            if (parts.Length != 2 || !OwnerAlive(parts[1])) return true;
            string prefix = marker + "." + parts[0];
            if (Complete) { File.WriteAllText(prefix + ".done", ""); return true; }
            try { using (new FileStream(prefix + ".claim", FileMode.CreateNew, FileAccess.Write)) { } }
            catch (IOException) { Wait(prefix + ".done", marker); return true; }
            Owner = true;
            // VS omits BuildProjectInSolution for projects it builds; only "False" excludes one.
            Projects = (XDocument.Parse(Configurations ?? "<SolutionConfiguration />").Root?.Elements("ProjectConfiguration") ?? Enumerable.Empty<XElement>())
                .Where(x => !String.Equals((string)x.Attribute("BuildProjectInSolution"), "False", StringComparison.OrdinalIgnoreCase))
                .Where(x => ((string)x.Attribute("AbsolutePath") ?? "").EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase) && File.Exists((string)x.Attribute("AbsolutePath")))
                .Select(x =>
                {
                    string[] mapping = x.Value.Split('|');
                    var item = new TaskItem((string)x.Attribute("AbsolutePath"));
                    item.SetMetadata("Properties", "Configuration=" + mapping[0] + ";Platform=" + (mapping.Length > 1 ? mapping[1] : "") + ";DBECompileAheadInstance=true;BuildProjectReferences=false");
                    return (ITaskItem)item;
                }).ToArray();
            Log.LogMessage(MessageImportance.High, "DBE: compiling " + Projects.Length + " solution projects ahead");
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Xml.XmlException)
        {
            Log.LogMessage(MessageImportance.High, "DBE: solution compile-ahead skipped: " + e.Message);
        }
        return true;
    }
    private static bool OwnerAlive(string process)
    {
        int id;
        if (!int.TryParse(process, out id)) return false;
        try { using (var owner = System.Diagnostics.Process.GetProcessById(id)) return !owner.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private void Wait(string done, string marker)
    {
        // Release this node: compile-ahead needs the nodes the waiting projects hold.
        var engine = (IBuildEngine3)BuildEngine;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        engine.Yield();
        try { while (!File.Exists(done) && File.Exists(marker) && watch.Elapsed < WaitLimit && !cancel.Wait(200)) { } }
        finally { engine.Reacquire(); }
    }
}

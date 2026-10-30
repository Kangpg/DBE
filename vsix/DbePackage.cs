using System;
using System.ComponentModel.Design;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Dbe.VisualStudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid("f55fa185-f0aa-4913-9cfb-9a64b547b9f1")]
    public sealed class DbePackage : AsyncPackage, IVsUpdateSolutionEvents
    {
        private static readonly Guid Commands = new Guid("a4b34f17-39f5-4cfa-993b-ae0acec8ebc7");
        private DTE2 dte;
        private IVsOutputWindowPane pane;
        private IVsSolutionBuildManager manager;
        private uint subscription;
        private System.Diagnostics.Process running;
        private bool starting;
        private readonly object gate = new object();

        protected override async Task InitializeAsync(CancellationToken token, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(token);
            dte = (DTE2)await GetServiceAsync(typeof(DTE));
            var output = (IVsOutputWindow)await GetServiceAsync(typeof(SVsOutputWindow));
            Guid id = Commands;ErrorHandler.ThrowOnFailure(output.CreatePane(ref id, "DBE Build", 1, 0));
            ErrorHandler.ThrowOnFailure(output.GetPane(ref id, out pane));
            manager = (IVsSolutionBuildManager)await GetServiceAsync(typeof(SVsSolutionBuildManager));
            ErrorHandler.ThrowOnFailure(manager.AdviseUpdateSolutionEvents(this, out subscription));
            var service = (OleMenuCommandService)await GetServiceAsync(typeof(IMenuCommandService));
            Add(service, 0x100, () => Begin(false), false);
            Add(service, 0x101, () => Begin(true), false);
            Add(service, 0x102, Cancel, true);
            Add(service, 0x103, () => Begin(false, true), false, true);
            Add(service, 0x104, () => Begin(true, true), false, true);
        }
        private void Add(OleMenuCommandService service, int id, Action action, bool cancel, bool projectOnly = false)
        {
            var command = new OleMenuCommand((_, __) => action(), new CommandID(Commands, id));
            command.BeforeQueryStatus += (_, __) => {
                ThreadHelper.ThrowIfNotOnUIThread();
                bool active;lock(gate) active = starting || running != null;
                command.Enabled = cancel ? active : !active && dte.Solution.IsOpen && dte.Solution.SolutionBuild.BuildState != vsBuildState.vsBuildStateInProgress && (!projectOnly || SelectedProject() != null);
            };
            service.AddCommand(command);
        }
        private Project SelectedProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try {
                var selected = dte.ToolWindows.SolutionExplorer.SelectedItems as Array;
                if (selected == null || selected.Length != 1) return null;
                var item = selected.GetValue(0) as UIHierarchyItem;
                var project = item?.Object as Project ?? (item?.Object as ProjectItem)?.ContainingProject;
                string path = project?.FullName;
                return !string.IsNullOrWhiteSpace(path) && path.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase) ? project : null;
            } catch (COMException) {return null;}
        }
        private static string SelectedTarget(Project project, bool rebuild)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var names = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (Project current = project; current != null; current = current.ParentProjectItem?.ContainingProject) {
                if (!visited.Add(current.UniqueName)) throw new InvalidOperationException("Invalid solution project hierarchy.");
                names.Insert(0, current.Name);
            }
            return ProjectTarget.Create(names, rebuild);
        }
        private void Begin(bool rebuild, bool projectOnly = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            lock(gate) {if(starting || running != null) return;starting=true;}
            JoinableTaskFactory.RunAsync(async () => {
                try {await Build(rebuild, projectOnly);}
                catch(Exception error) {pane.OutputStringThreadSafe("DBE: " + error.Message + "\n");}
                finally {lock(gate) starting=false;}
            });
        }
        private async Task Build(bool rebuild, bool projectOnly)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if(dte.Solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateInProgress) throw new InvalidOperationException("Wait for the current Visual Studio build.");
            string solution = dte.Solution.FullName;
            if(!File.Exists(solution) || !solution.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Open a saved C++ solution.");
            Project project = projectOnly ? SelectedProject() : null;
            if (projectOnly && project == null) throw new InvalidOperationException("Select one loaded C++ project in Solution Explorer.");
            string buildTarget = projectOnly ? SelectedTarget(project, rebuild) : rebuild ? "Rebuild" : "Build";
            string scope = projectOnly ? "Project: " + project.FullName : "Solution: " + solution;
            string settingsPath = Path.ChangeExtension(solution, ".dbe.json");
            Settings settings;
            using(var input = File.OpenRead(settingsPath)) settings = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(input);
            string adapter = Path.GetFullPath(settings.AdapterDirectory);
            if(!File.Exists(Path.Combine(adapter,"msbuild.json"))) throw new InvalidOperationException("Configured DBE adapter is missing.");
            var configuration = (SolutionConfiguration2)dte.Solution.SolutionBuild.ActiveConfiguration;
            string selectedConfiguration = configuration.Name, platform = configuration.PlatformName;
            if(platform != "x64") throw new InvalidOperationException("DBE currently requires the x64 configuration.");
            dte.ExecuteCommand("File.SaveAll");
            BuildTools tools;
            using(var input=File.OpenRead(Path.Combine(adapter,"build-tools.json"))) tools=(BuildTools)new DataContractJsonSerializer(typeof(BuildTools)).ReadObject(input);
            string msbuild = Path.GetFullPath(tools.MSBuild);
            string host = Path.Combine(Path.GetDirectoryName(typeof(DbePackage).Assembly.Location), "dbe-build-host.exe");
            if(!File.Exists(msbuild) || !File.Exists(host)) throw new FileNotFoundException("DBE build host or MSBuild is missing.");
            string run = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBE", "builds", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(run);
            string targets = string.IsNullOrEmpty(settings.ValidationTargets) ? Path.Combine(adapter,"DBE.Remote.targets") : Path.GetFullPath(settings.ValidationTargets);
            string response = Path.Combine(run, "build.rsp");
            var arguments = new StringBuilder();
            foreach(string argument in new[]{solution,"/nologo","/nr:false","/m:4","/t:"+buildTarget,"/v:normal",
                "/p:Configuration="+selectedConfiguration,"/p:Platform="+platform,"/p:DBEEnabled=true","/p:DBEAdapterDirectory="+adapter,"/p:ForceImportAfterCppTargets="+targets})
                arguments.AppendLine(Quote(argument));
            if(!string.IsNullOrEmpty(settings.ValidationTargets)) {arguments.AppendLine(Quote("/p:DBEValidationRoot="+Path.GetDirectoryName(solution)+"\\"));arguments.AppendLine("/p:VcpkgManifestInstall=false");}
            File.WriteAllText(response, arguments.ToString(), Encoding.Unicode);
            pane.Activate();dte.ExecuteCommand("View.Output");
            pane.OutputStringThreadSafe("DBE: " + selectedConfiguration + "|" + platform + " " + scope + "\nTarget: " + buildTarget + "\nLog: " + Path.Combine(run,"build.log") + "\n");
            var process = new System.Diagnostics.Process {StartInfo=new ProcessStartInfo(host, Quote(msbuild)+" "+Quote(response)+" "+Quote(@"Local\DBE.Build."+Guid.NewGuid().ToString("N"))) {
                UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(solution),RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,
                StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)
            },EnableRaisingEvents=true};
            var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, __) => finished.TrySetResult(process.ExitCode);
            var clock=Stopwatch.StartNew();
            try {
                lock(gate) {if(!process.Start()) throw new InvalidOperationException("Unable to start DBE host.");running=process;starting=false;}
                using(var log = new StreamWriter(Path.Combine(run,"build.log"), false, new UTF8Encoding(false))) {
                    var logGate=new object();
                    Task stdout=Pump(process.StandardOutput,log,logGate), stderr=Pump(process.StandardError,log,logGate);
                    int exit=await finished.Task.ConfigureAwait(false);
                    await Task.WhenAll(stdout,stderr).ConfigureAwait(false);
                    pane.OutputStringThreadSafe("DBE: " + (exit==0?"Succeeded":exit==130?"Cancelled":"Failed ("+exit+")") + " in " + clock.Elapsed.TotalSeconds.ToString("F1") + " s\n");
                }
            } finally {lock(gate) {if(running==process) running=null;}process.Dispose();}
        }
        private async Task Pump(StreamReader reader, StreamWriter log, object logGate)
        {
            string line;
            while((line=await reader.ReadLineAsync().ConfigureAwait(false))!=null) {
                lock(logGate) {log.WriteLine(line);}
                pane.OutputStringThreadSafe((line.Length>2000?line.Substring(0,2000)+" … [full line in log]":line)+"\n");
            }
        }
        private void Cancel() {lock(gate) {if(running!=null) {try {running.StandardInput.Close();} catch(InvalidOperationException) {} }}}
        private static string Quote(string value)
        {
            if(value.IndexOfAny(new[]{'"','\r','\n'})>=0) throw new InvalidOperationException("Unsupported quote/newline in build parameter.");
            // CRT quoting: double trailing backslashes before a closing quote.
            int trailing=0;for(int i=value.Length-1;i>=0 && value[i]=='\\';i--) trailing++;
            return "\""+value+new string('\\',trailing)+"\"";
        }
        protected override void Dispose(bool disposing) {if(disposing) {Cancel();if(manager!=null) manager.UnadviseUpdateSolutionEvents(subscription);}base.Dispose(disposing);}
        public int UpdateSolution_Begin(ref int cancel) {lock(gate) {if(starting || running!=null) cancel=1;}return VSConstants.S_OK;}
        public int UpdateSolution_StartUpdate(ref int cancel) => UpdateSolution_Begin(ref cancel);
        public int UpdateSolution_Done(int succeeded,int modified,int cancelled) => VSConstants.S_OK;
        public int UpdateSolution_Cancel() => VSConstants.S_OK;
        public int OnActiveProjectCfgChange(IVsHierarchy hierarchy) => VSConstants.S_OK;
        [DataContract] private sealed class Settings {
            [DataMember(Name="adapterDirectory",IsRequired=true)] public string AdapterDirectory {get;set;}
            [DataMember(Name="validationTargets")] public string ValidationTargets {get;set;}
        }
        [DataContract] private sealed class BuildTools {[DataMember(Name="msbuild",IsRequired=true)] public string MSBuild {get;set;}}
    }
}

[CmdletBinding()]
param([Parameter(Mandatory)][string]$AdapterDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$tools=Get-DbeToolchain '2022' (Join-Path $root 'native/dbe-cl.vcxproj')
$run=Join-Path $root ('out/build-host-test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run|Out-Null
$hostPath=Join-Path $root 'out/vsix-build/dbe-build-host.exe'
$worker=Join-Path $AdapterDirectory 'dbe-worker.exe'
$child=Join-Path $root 'out/worker-test-build/worker-child.exe'
$checks=[Collections.Generic.List[string]]::new()
function Start-Host([string]$Name,[string[]]$BuildArguments){
    $response=Join-Path $run ($Name+'.rsp')
    [IO.File]::WriteAllText($response,($BuildArguments -join [Environment]::NewLine),[Text.Encoding]::Unicode)
    $start=[Diagnostics.ProcessStartInfo]::new($hostPath)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false);$start.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
    foreach($arg in @($tools.MSBuild,$response,('Local\DBE.Build.'+[Guid]::NewGuid().ToString('N')))){$start.ArgumentList.Add($arg)}
    $p=[Diagnostics.Process]::Start($start)
    [pscustomobject]@{Process=$p;Out=$p.StandardOutput.ReadToEndAsync();Err=$p.StandardError.ReadToEndAsync();Name=$Name}
}
function Finish-Host($Job,[int]$Expected){
    if(!$Job.Process.WaitForExit(120000)){$Job.Process.StandardInput.Close();if(!$Job.Process.WaitForExit(45000)){$Job.Process.Kill();$Job.Process.WaitForExit()};throw 'Build host timed out'}
    [IO.File]::WriteAllText((Join-Path $run ($Job.Name+'.log')),$Job.Out.GetAwaiter().GetResult()+$Job.Err.GetAwaiter().GetResult())
    $code=$Job.Process.ExitCode;$Job.Process.Dispose()
    if($code -ne $Expected){throw "Unexpected build host exit $code, expected $Expected"}
}
$projectRoot=Join-Path $run 'project';New-Item -ItemType Directory -Path $projectRoot|Out-Null
Copy-Item (Join-Path $root 'fixture-remote/*') $projectRoot -Recurse
$settings=[xml]'<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><DBEEnabled>true</DBEEnabled><DBEAdapterDirectory /></PropertyGroup></Project>'
$settings.Project.PropertyGroup.DBEAdapterDirectory=$AdapterDirectory;$settings.Save((Join-Path $projectRoot 'DBE.settings.props'))
@{adapterDirectory=$AdapterDirectory}|ConvertTo-Json|Set-Content (Join-Path $projectRoot 'remote-probe.dbe.json')
$build=Start-Host 'build' @(('"'+(Join-Path $projectRoot 'remote-probe.sln')+'"'),'/nologo','/nr:false','/p:Configuration=Release','/p:Platform=x64','/v:normal')
Finish-Host $build 0
& (Join-Path $projectRoot 'out/Release/bin/remote-probe.exe')|Out-Null
if($LASTEXITCODE -ne 42){throw 'Built fixture failed'}
$checks.Add('native-host-real-MSBuild-hybrid-compile-link-run')
$unicodeProject=Join-Path $run '한글-진단.proj'
$unicodeXml=[xml]'<Project><Target Name="Build"><Message Text="DBE 한글 출력: 빌드 시작" Importance="High"/><Warning Text="DBE 한글 경고: 경로 확인" Code="DBETEST1"/><Error Text="DBE 한글 오류: 의도한 실패" Code="DBETEST2"/></Target></Project>'
$unicodeXml.Save($unicodeProject)
$unicodeJob=Start-Host 'unicode-diagnostics' @(('"'+$unicodeProject+'"'),'/nologo','/nr:false','/t:Build','/v:normal')
Finish-Host $unicodeJob 1
$unicodeLog=[IO.File]::ReadAllText((Join-Path $run 'unicode-diagnostics.log'))
foreach($expected in 'DBE 한글 출력: 빌드 시작','DBE 한글 경고: 경로 확인','DBE 한글 오류: 의도한 실패','한글-진단.proj'){
    if(!$unicodeLog.Contains($expected)){throw "UTF-8 MSBuild output lost text: $expected"}
}
$checks.Add('native-host-UTF8-Korean-message-warning-error-and-path')
foreach($mode in 'cancel','owner-death'){
    $attempt=Join-Path $run $mode;New-Item -ItemType Directory -Path $attempt|Out-Null
    $project=Join-Path $attempt 'test.proj'
    $xml=[xml]'<Project><Target Name="Build"><Exec Command="" /></Target></Project>'
    $xml.Project.Target.Exec.Command='"'+$worker+'" process-test "'+$child+'" "'+$attempt+'"';$xml.Save($project)
    $job=Start-Host $mode @(('"'+$project+'"'),'/nologo','/nr:false','/t:Build')
    $handles=@()
    try {
        $marker=Join-Path $attempt 'pids.txt';$watch=[Diagnostics.Stopwatch]::StartNew()
        while(!(Test-Path $marker)){if($job.Process.HasExited -or $watch.Elapsed.TotalSeconds -gt 20){throw 'Missing child marker'};Start-Sleep -Milliseconds 50}
        $handles=@((Get-Content -Raw $marker).Split(' ',[StringSplitOptions]::RemoveEmptyEntries)|ForEach-Object {$p=[Diagnostics.Process]::GetProcessById([int]$_);$null=$p.Handle;$p})
        if($mode -eq 'cancel'){$job.Process.StandardInput.Close();Finish-Host $job 130}
        else{$job.Process.Kill();$job.Process.WaitForExit();$job.Process.Dispose()}
        foreach($p in $handles){if(!$p.WaitForExit(10000)){throw 'Build host left a descendant alive'}}
        $checks.Add('native-host-'+$mode+'-stops-owned-descendants')
    } finally {
        foreach($p in $handles){$p.Dispose()}
        try{if(!$job.Process.HasExited){$job.Process.Kill();$job.Process.WaitForExit()}}catch [InvalidOperationException]{}
    }
}
@{Checks=$checks.ToArray();Run=$run;Fixture=(Join-Path $projectRoot 'remote-probe.sln');Scope='Real MSBuild, MSVC and native process handles; extension UI tested separately.'}|ConvertTo-Json|Tee-Object -FilePath (Join-Path $run 'checks.json')

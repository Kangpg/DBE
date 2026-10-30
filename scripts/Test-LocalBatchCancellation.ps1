[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixtureProject,[Parameter(Mandatory)][string]$AdapterDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$tools=Get-DbeToolchain '2022' (Join-Path $root 'native/dbe-cl.vcxproj')
$project=(Resolve-Path -LiteralPath $FixtureProject).Path
# Only the generated profile fixture belongs to this test, never an external project.
if($project -notmatch ('^'+[regex]::Escape((Join-Path $root 'out\compiler-profiles-'))+'[0-9a-f]{32}\\project\\remote-probe\.vcxproj$')){throw 'Expected the owned compiler-profiles fixture'}
$config=Get-Content (Join-Path $AdapterDirectory 'msbuild.json') -Raw|ConvertFrom-Json
$policy=Get-Content $config.hostPolicy -Raw|ConvertFrom-Json
if($policy.localBatchSlots -lt 2){throw 'This test requires local MP grouping'}
$run=Join-Path $root ('out/local-batch-cancel-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $run|Out-Null
$files=@(Get-ChildItem (Split-Path $project -Parent) -Filter 'local-batch-*.cpp')
if($files.Count -lt 12){throw 'Missing local batch fixture inputs'}
$original=@{};$handles=[Collections.Generic.List[Diagnostics.Process]]::new();$process=$null
try {
    foreach($file in $files){
        $original[$file.FullName]=[IO.File]::ReadAllBytes($file.FullName)
        $body=[Text.StringBuilder]::new('#include "stdafx.h"'+"`n")
        $id=$file.BaseName.Replace('-','_')
        foreach($i in 1..60000){[void]$body.AppendLine("int cancel_${id}_$i(){return $i;}")}
        [IO.File]::WriteAllText($file.FullName,$body.ToString())
    }
    $response=Join-Path $run 'build.rsp'
    [IO.File]::WriteAllLines($response,@(('"'+$project+'"'),'/nologo','/nr:false','/m:1','/p:Configuration=Debug','/p:Platform=x64','/v:normal'),[Text.Encoding]::Unicode)
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'out/vsix-build/dbe-build-host.exe'))
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($arg in @($tools.MSBuild,$response,('Local\DBE.Build.'+[Guid]::NewGuid().ToString('N')))){$start.ArgumentList.Add($arg)}
    $process=[Diagnostics.Process]::Start($start);$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    $watch=[Diagnostics.Stopwatch]::StartNew();$observed=@()
    while(!$process.HasExited -and $watch.Elapsed.TotalSeconds -lt 60){
        $snapshot=@(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,CreationDate)
        $owned=[Collections.Generic.HashSet[uint32]]::new();[void]$owned.Add([uint32]$process.Id)
        do {$changed=$false;foreach($item in $snapshot){if($owned.Contains([uint32]$item.ParentProcessId) -and !$owned.Contains([uint32]$item.ProcessId) -and $item.CreationDate -ge $process.StartTime){[void]$owned.Add([uint32]$item.ProcessId);$changed=$true}}}while($changed)
        $compilers=@($snapshot|Where-Object {$_.Name -eq 'cl.exe' -and $owned.Contains([uint32]$_.ProcessId)})
        $drivers=@($compilers|ForEach-Object ProcessId)
        $mpChildren=@($compilers|Where-Object {$_.ParentProcessId -in $drivers})
        if($mpChildren.Count -ge 2){
            foreach($item in $compilers){$handle=[Diagnostics.Process]::GetProcessById([int]$item.ProcessId);$null=$handle.Handle;if([Math]::Abs(($handle.StartTime-$item.CreationDate).TotalMilliseconds) -gt 10){$handle.Dispose();throw 'Compiler process identity changed'};$handles.Add($handle)}
            $observed=$compilers;break
        }
        Start-Sleep -Milliseconds 100
    }
    if($handles.Count -lt 3){throw 'Did not observe a CL driver with two live MP compiler children'}
    $process.StandardInput.Close()
    if(!$process.WaitForExit(45000)){throw 'Cancellation did not finish'}
    [IO.File]::WriteAllText((Join-Path $run 'build.log'),$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult())
    if($process.ExitCode -ne 130){throw "Expected cancellation exit 130, got $($process.ExitCode)"}
    foreach($handle in $handles){if(!$handle.WaitForExit(10000)){throw 'A captured MP compiler survived cancellation'}}
    $leases=Join-Path (Split-Path $config.hostPolicy -Parent) 'leases'
    if(@(Get-ChildItem $leases -Filter '*.lease'|Where-Object Length -gt 0).Count){throw 'Graceful cancellation retained unconfirmed reservations'}
    @{Checks=@('real-MSVC-MP-driver-and-two-live-children','build-host-cancel-exit-130','captured-compiler-handles-exited','graceful-cancel-clears-reservations');Observed=$observed;Run=$run}|ConvertTo-Json -Depth 5|Tee-Object -FilePath (Join-Path $run 'checks.json')
} finally {
    if($process){if(!$process.HasExited){$process.StandardInput.Close();if(!$process.WaitForExit(45000)){$process.Kill();$process.WaitForExit()}};$process.Dispose()}
    foreach($handle in $handles){$handle.Dispose()}
    foreach($file in $original.Keys){[IO.File]::WriteAllBytes($file,$original[$file])}
}

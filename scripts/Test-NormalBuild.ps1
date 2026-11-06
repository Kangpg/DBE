[CmdletBinding()]
param([Parameter(Mandatory)][string]$LocalAdapter,[string]$HybridAdapter,
      [ValidateSet('2022','2026')][string]$VisualStudio='2026')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
$run=Join-Path $root ('out/normal-'+[guid]::NewGuid().ToString('N').Substring(0,12))
Copy-Item (Join-Path $root 'fixture-remote') $run -Recurse
$solution=Join-Path $run 'remote-probe.sln'
$project=Join-Path $run 'remote-probe.vcxproj'
$register=Join-Path $PSScriptRoot 'Set-NormalBuild.ps1'
# Prepare a plain C++ project with PCH and /MP. Freeze it before registration.
$x=[xml](Get-Content $project -Raw)
$ns=$x.DocumentElement.NamespaceURI
foreach($n in @($x.Project.Import|Where-Object {$_.Project -match 'DBE'})){[void]$x.Project.RemoveChild($n)}
$cl=$x.Project.ItemDefinitionGroup.ClCompile
foreach($entry in @{MultiProcessorCompilation='true';PrecompiledHeader='Use';PrecompiledHeaderFile='stdafx.h'}.GetEnumerator()){
    $n=$x.CreateElement($entry.Key,$ns);$n.InnerText=$entry.Value;[void]$cl.AppendChild($n)
}
$group=$x.Project.ItemGroup|Where-Object {$_.SelectSingleNode("*[local-name()='ClCompile']")}
$include='#include "stdafx.h"'+[Environment]::NewLine+'#pragma message("DBE_NORMAL_COMPILE_PROBE")'+[Environment]::NewLine
Set-Content (Join-Path $run 'stdafx.h') "#pragma once`n#include <vector>"
foreach($name in @('stdafx')+@(1..18|ForEach-Object {"work$_"})){
    $n=$x.CreateElement('ClCompile',$ns);$n.SetAttribute('Include',$name+'.cpp');[void]$group.AppendChild($n)
    $body=''
    if($name -eq 'stdafx'){
        $pch=$x.CreateElement('PrecompiledHeader',$ns);$pch.InnerText='Create';[void]$n.AppendChild($pch)
    }else{$body=(1..3500|ForEach-Object {"int ${name}_$_(){return $_;}"}) -join [Environment]::NewLine}
    [IO.File]::WriteAllText((Join-Path $run ($name+'.cpp')),$include+$body)
}
foreach($name in 'main','value'){
    $path=Join-Path $run ($name+'.cpp')
    [IO.File]::WriteAllText($path,$include+[IO.File]::ReadAllText($path))
}
$x.Save($project)
$before=(Get-FileHash $project).Hash
$checks=[Collections.Generic.List[string]]::new()
function Open-Ide {
    $version=if($VisualStudio -eq '2022'){'17.0'}else{'18.0'}
    $instance=[Activator]::CreateInstance([type]::GetTypeFromProgID("VisualStudio.DTE.$version",$true))
    try {
        $instance.SuppressUI=$true
        $instance.Solution.Open($solution)
        $instance.Solution.SolutionBuild.SolutionConfigurations.Item('Debug').Activate()
        return $instance
    } catch {$instance.Quit();throw}
}
function Build([string]$Name,[switch]$ExpectFailure){
    $ide.Solution.SolutionBuild.Build($true)
    $log=[IO.File]::ReadAllText((Join-Path $run 'out/Debug/obj/remote-probe.log'))
    [IO.File]::WriteAllText((Join-Path $run ('out/Debug/obj/'+$Name+'.log')),$log)
    if(($ide.Solution.SolutionBuild.LastBuildInfo -ne 0) -ne $ExpectFailure.IsPresent){throw "Unexpected build result: $Name ($run)"}
    if(!$ExpectFailure){
        & (Join-Path $run 'out/Debug/bin/remote-probe.exe')
        if($LASTEXITCODE -ne 42){throw 'Incorrect linked executable'}
    }
    return $log
}
function Objects {
    Get-ChildItem (Join-Path $run 'out/Debug/obj') -Filter *.obj | ForEach-Object {$_.Name+'|'+$_.LastWriteTimeUtc.Ticks}
}
$ide=$null
try {
    & $register -Solution $solution -AdapterDirectory $LocalAdapter
    $ide=Open-Ide
    $first=Build first
    $tlogs=@(Get-ChildItem $run -Recurse -File -Filter 'dbe-remote-cl.read.*.tlog')
    if(!$tlogs.Count -or $first -notmatch 'DBE_NORMAL_COMPILE_PROBE'){throw 'The native CL tracker did not observe a DBE compile'}
    $checks.Add('standard-IDE-Build-Local-PCH-MP-link-run')
    $stamps=@(Objects)
    $buildLog=Join-Path $run 'out/Debug/obj/remote-probe.log'
    $previousLogStamp=(Get-Item $buildLog).LastWriteTimeUtc.Ticks
    $noop=Build noop
    if((Get-Item $buildLog).LastWriteTimeUtc.Ticks -ne $previousLogStamp -and $noop -match 'DBE_NORMAL_COMPILE_PROBE'){throw 'No-op compiled a translation unit'}
    if(!$stamps.Count -or (Compare-Object $stamps @(Objects))){throw 'No-op rewrote objects'}
    $checks.Add('repeat-build-preserves-objects')
    $source=Join-Path $run 'value.cpp'
    $original=[IO.File]::ReadAllText($source)
    # Exercise an invalidated MP command with an existing output. A valid
    # command and a one-source incremental failure alone can mask stale objects.
    [IO.File]::WriteAllText((Join-Path $run 'headers/membership.h'),'// fixture')
    [IO.File]::AppendAllText($source,[Environment]::NewLine+'#error DBE_NORMAL_ERROR')
    if((Build error -ExpectFailure) -notmatch 'DBE_NORMAL_ERROR'){throw 'Missing compiler diagnostic'}
    if(Test-Path (Join-Path $run 'out/Debug/obj/value.obj')){throw 'Failed compile kept stale object'}
    $checks.Add('standard-error-diagnostic-and-stale-object-cleanup')
    # Keep the compile active for the standard VS Cancel command.
    $slow=[Text.StringBuilder]::new($original)
    foreach($i in 1..100000){[void]$slow.AppendLine("int slow_$i(){return $i;}")}
    [IO.File]::WriteAllText($source,$slow.ToString())
    $start=Get-Date
    $ide.Solution.SolutionBuild.Build($false)
    $child=$null
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while(!$child -and $watch.Elapsed.TotalSeconds -lt 40){
        $wrappers=@(Get-CimInstance Win32_Process -Filter "Name='dbe-remote-cl.exe'"|Where-Object {
            $_.ExecutablePath -eq (Join-Path $LocalAdapter 'dbe-remote-cl.exe') -and $_.CreationDate -ge $start
        })
        foreach($w in $wrappers){
            $item=Get-CimInstance Win32_Process -Filter ("Name='cl.exe' and ParentProcessId="+$w.ProcessId)|Select-Object -First 1
            if($item){
                # A short-lived compiler may exit between the listing and this capture.
                try { $p=[Diagnostics.Process]::GetProcessById([int]$item.ProcessId);$null=$p.Handle } catch [ArgumentException] { continue }
                if([math]::Abs(($p.StartTime-$item.CreationDate).TotalMilliseconds) -gt 10){$p.Dispose();throw 'Compiler identity changed'}
                $child=$p;break
            }
        }
        if(!$child){Start-Sleep -Milliseconds 100}
    }
    if(!$child){throw 'No live native compiler observed for cancellation'}
    try {
        $ide.ExecuteCommand('Build.Cancel')
        if(!$child.WaitForExit(15000)){throw 'Captured compiler survived IDE cancel'}
    } finally {$child.Dispose()}
    $watch.Restart()
    while($ide.Solution.SolutionBuild.BuildState -eq 2 -and $watch.Elapsed.TotalSeconds -lt 30){Start-Sleep -Milliseconds 100}
    if($ide.Solution.SolutionBuild.BuildState -eq 2 -or (Test-Path (Join-Path $run 'out/Debug/obj/value.obj'))){throw 'Cancellation did not settle cleanly'}
    [IO.File]::WriteAllText($source,$original)
    $null=Build recovery
    $checks.Add('standard-IDE-cancel-live-compiler-and-recovery')
    if($HybridAdapter){
        $ide.Quit();$ide=$null
        & $register -Solution $solution -AdapterDirectory $HybridAdapter
        $ide=Open-Ide
        $log=Build hybrid
        if($log -notmatch 'DBE: remote-completed ' -or $log -notmatch 'DBE: local(-batch)?-completed '){throw "Missing Hybrid placement: $run"}
        $checks.Add('same-IDE-Build-Hybrid-local-and-remote-link-run')
    }
    if((Get-FileHash $project).Hash -ne $before){throw 'Registration changed project bytes'}
    $checks|ConvertTo-Json|Set-Content (Join-Path $run 'checks.json')
    [pscustomobject]@{Run=$run;Checks=$checks}
} finally {
    try {if($ide){$ide.Quit()}} finally {& $register -Solution $solution -Disable}
}

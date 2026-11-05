[CmdletBinding()]
param([Parameter(Mandatory)][string]$AdapterDirectory,[ValidateSet('2022','2026')][string]$VisualStudio='2022',[int]$MinimumRemoteCompletions=0)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent;. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$tools=Get-DbeToolchain $VisualStudio (Join-Path $root 'native\dbe-cl.vcxproj') 'v143'
$mode=(Get-Content (Join-Path $AdapterDirectory 'msbuild.json') -Raw|ConvertFrom-Json).Mode
$run=Join-Path $root ('out\hybrid-incremental-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $run|Out-Null
$projectRoot=Join-Path $run 'project';New-Item -ItemType Directory $projectRoot|Out-Null;Copy-Item (Join-Path $root 'fixture-remote\*') $projectRoot -Recurse
$settings=[xml]'<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><DBEEnabled>true</DBEEnabled><DBEAdapterDirectory /></PropertyGroup></Project>'
$settings.Project.PropertyGroup.DBEAdapterDirectory=$AdapterDirectory;$settings.Save((Join-Path $projectRoot 'DBE.settings.props'))
$project=Join-Path $projectRoot 'remote-probe.vcxproj';$checks=[Collections.Generic.List[string]]::new()
# A referenced header scope is outside the project and /I roots. It must be
# present before initial CL tracking, including a previously absent include.
$reference=Join-Path $run 'referenced';New-Item -ItemType Directory $reference|Out-Null
$referenceProject=Join-Path $reference 'headers.vcxproj'
[IO.File]::WriteAllText($referenceProject,@'
<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup Label="ProjectConfigurations"><ProjectConfiguration Include="Debug|x64"><Configuration>Debug</Configuration><Platform>x64</Platform></ProjectConfiguration></ItemGroup>
  <PropertyGroup Label="Globals"><ProjectGuid>{61973446-AC14-44DE-BCFD-53F60C7C0BF8}</ProjectGuid></PropertyGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.Default.props" />
  <PropertyGroup Label="Configuration"><ConfigurationType>Utility</ConfigurationType><PlatformToolset>v143</PlatformToolset></PropertyGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.props" />
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.targets" />
</Project>
'@)
[IO.File]::WriteAllText((Join-Path $reference 'shared.h'),"#pragma once`n#if __has_include(`"reference-extra.h`")`n#include `"reference-extra.h`"`n#else`n#define DBE_REFERENCE_DELTA 0`n#endif`n")
# The reference generates this header in its pre-build step, so compile-ahead can
# reach the consumer before the header exists. The ordinary Build must still succeed.
$generator=[xml][IO.File]::ReadAllText($referenceProject);$generatorNs=$generator.DocumentElement.NamespaceURI
$definitions=$generator.CreateElement('ItemDefinitionGroup',$generatorNs);$event=$generator.CreateElement('PreBuildEvent',$generatorNs);$command=$generator.CreateElement('Command',$generatorNs)
$command.InnerText='if not exist generated.h (ping -n 4 127.0.0.1 >nul & echo #define DBE_GENERATED 0 > generated.h)'
[void]$event.AppendChild($command);[void]$definitions.AppendChild($event);[void]$generator.DocumentElement.AppendChild($definitions);$generator.Save($referenceProject)
[IO.File]::WriteAllText((Join-Path $projectRoot 'headers/value.h'),"#pragma once`n#include `"../../referenced/shared.h`"`n#include `"../../referenced/generated.h`"`n#define DBE_VALUE (40 + DBE_REFERENCE_DELTA + DBE_GENERATED)`n")
$definition=[xml][IO.File]::ReadAllText($project);$ns=$definition.DocumentElement.NamespaceURI
$group=$definition.CreateElement('ItemGroup',$ns);$item=$definition.CreateElement('ProjectReference',$ns);$item.SetAttribute('Include','..\referenced\headers.vcxproj')
foreach($entry in @{Project='{61973446-AC14-44DE-BCFD-53F60C7C0BF8}';ReferenceOutputAssembly='false';LinkLibraryDependencies='false'}.GetEnumerator()){
    $metadata=$definition.CreateElement($entry.Key,$ns);$metadata.InnerText=$entry.Value;[void]$item.AppendChild($metadata)
}
[void]$group.AppendChild($item);[void]$definition.DocumentElement.AppendChild($group);$definition.Save($project)
function Build([string]$Name,[int]$Expected,[bool]$NoCompile=$false,[bool]$Failure=$false,[string]$Target='Build'){
    # Native Local emits no DBE execution events. Check output timestamps too,
    # so a silent recompilation cannot pass the no-op contract.
    $objects=Join-Path $projectRoot 'out\Debug\obj'
    $before=if($NoCompile){@(Get-ChildItem $objects -Filter *.obj|ForEach-Object {$_.Name+'|'+$_.LastWriteTimeUtc.Ticks})}else{@()}
    $log=Join-Path $run ($Name+'.log');& $tools.MSBuild $project /nologo /nr:false /m:4 /p:Configuration=Debug /p:Platform=x64 "/t:$Target" /v:normal *> $log
    if(($LASTEXITCODE -ne 0) -ne $Failure){Get-Content $log -Tail 15;throw "Unexpected result: $Name"}
    if(!$Failure){& (Join-Path $projectRoot 'out\Debug\bin\remote-probe.exe');if($LASTEXITCODE -ne $Expected){throw "Wrong runtime result: $Name / $LASTEXITCODE"}}
    if($NoCompile -and (Select-String -Path $log -Pattern 'DBE: (local(-batch)?-running|preparing|remote-requested|fallback-running)' -Quiet)){throw "No-op compiled: $Name"}
    if($NoCompile){
        $after=@(Get-ChildItem $objects -Filter *.obj|ForEach-Object {$_.Name+'|'+$_.LastWriteTimeUtc.Ticks})
        if(!$before.Count -or (Compare-Object $before $after)){throw "No-op changed object outputs: $Name"}
    }
    $checks.Add($Name)
}
Build initial 42
# Report how an early compile against the not-yet-generated header appears to the caller.
if(Select-String -Path (Join-Path $run 'initial.log') -Pattern 'C1083' -Quiet){throw 'A deferred compile-ahead failure reached the caller'}
$checks.Add('generated-header-through-compile-ahead-without-false-errors')
Build noop 42 $true
[IO.File]::WriteAllText((Join-Path $reference 'reference-extra.h'),"#pragma once`n#define DBE_REFERENCE_DELTA 5`n")
Build added-reference-sibling-include 47
Build noop-after-reference-add 47 $true
Remove-Item -LiteralPath (Join-Path $reference 'reference-extra.h')
Build removed-reference-sibling-include 42
Build noop-after-reference-remove 42 $true
[IO.File]::WriteAllText((Join-Path $projectRoot 'headers\optional.h'),"#pragma once`n#define DBE_OPTIONAL 7`n")
Build added-previously-absent-include 49
Remove-Item -LiteralPath (Join-Path $projectRoot 'headers\optional.h')
Build removed-optional-include 42
[IO.File]::WriteAllText((Join-Path $projectRoot 'headers\value.h'),"#pragma once`n#define DBE_VALUE 41`n")
Build changed-header 43
Build noop-after-header 43 $true
[IO.File]::WriteAllText((Join-Path $projectRoot 'forced.h'),"#pragma once`n#define DBE_DELTA 3`n")
Build changed-forced-header 44
Add-Content (Join-Path $projectRoot 'value.cpp') 'int broken(){return DBE_MISSING_TEST;}'
Build compiler-error 0 $false $true
if(Test-Path (Join-Path $projectRoot 'out\Debug\obj\value.obj')){throw 'Failed compilation retained stale object'}
Copy-Item (Join-Path $root 'fixture-remote\value.cpp') (Join-Path $projectRoot 'value.cpp') -Force
Build error-recovery 44
Build explicit-rebuild 44 $false $false Rebuild
Build noop-after-rebuild 44 $true
$remote=@(Get-ChildItem $run -Filter *.log | Select-String -Pattern 'DBE: remote-completed ').Count
if($remote -lt $MinimumRemoteCompletions){throw "Expected at least $MinimumRemoteCompletions remote completions, observed $remote"}
@{Run=$run;RemoteCompletions=$remote;Checks=$checks.ToArray();Scope="Actual $mode MSBuild compile/link/runtime and no-op, include membership/content, forced header, error/recovery and Rebuild"}|ConvertTo-Json|Tee-Object -FilePath (Join-Path $run 'checks.json')

[CmdletBinding()]
param([Parameter(Mandatory)][string]$AdapterDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent;. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$tools=Get-DbeToolchain '2022' (Join-Path $root 'native\dbe-cl.vcxproj')
$run=Join-Path $root ('out\hybrid-incremental-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $run|Out-Null
$projectRoot=Join-Path $run 'project';New-Item -ItemType Directory $projectRoot|Out-Null;Copy-Item (Join-Path $root 'fixture-remote\*') $projectRoot -Recurse
$settings=[xml]'<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><DBEEnabled>true</DBEEnabled><DBEAdapterDirectory /></PropertyGroup></Project>'
$settings.Project.PropertyGroup.DBEAdapterDirectory=$AdapterDirectory;$settings.Save((Join-Path $projectRoot 'DBE.settings.props'))
$project=Join-Path $projectRoot 'remote-probe.vcxproj';$checks=[Collections.Generic.List[string]]::new()
function Build([string]$Name,[int]$Expected,[bool]$NoCompile=$false,[bool]$Failure=$false,[string]$Target='Build'){
    $log=Join-Path $run ($Name+'.log');& $tools.MSBuild $project /nologo /nr:false /m:1 /p:Configuration=Debug /p:Platform=x64 "/t:$Target" /v:normal *> $log
    if(($LASTEXITCODE -ne 0) -ne $Failure){Get-Content $log -Tail 15;throw "Unexpected result: $Name"}
    if(!$Failure){& (Join-Path $projectRoot 'out\Debug\bin\remote-probe.exe');if($LASTEXITCODE -ne $Expected){throw "Wrong runtime result: $Name / $LASTEXITCODE"}}
    if($NoCompile -and (Select-String -Path $log -Pattern 'DBE: (local(-batch)?-running|preparing|remote-requested|fallback-running)' -Quiet)){throw "No-op compiled: $Name"}
    $checks.Add($Name)
}
Build initial 42
Build noop 42 $true
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
@{Run=$run;Checks=$checks.ToArray();Scope='Actual Hybrid MSBuild compile/link/runtime and no-op, include membership/content, forced header, error/recovery and Rebuild'}|ConvertTo-Json|Tee-Object -FilePath (Join-Path $run 'checks.json')

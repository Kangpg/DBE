[CmdletBinding()]
param([Parameter(Mandatory)][string]$DeploymentFile,[Parameter(Mandatory)][string]$PublishDirectory,[switch]$AllowCompilerMismatch)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$deployment=Get-Content -Raw -LiteralPath $DeploymentFile | ConvertFrom-Json
$run=Join-Path $root ('out\vs-remote-demo-'+[Guid]::NewGuid().ToString('N'))
$projectRoot=Join-Path $run 'project'
New-Item -ItemType Directory -Path $projectRoot | Out-Null
Copy-Item -Path (Join-Path $root 'fixture-remote\*') -Destination $projectRoot -Recurse
$adapter=& (Join-Path $PSScriptRoot 'New-MSBuildAdapter.ps1') -Destination (Join-Path $run 'adapter') -ClientConfig $deployment.ClientConfig -PublishDirectory $PublishDirectory -Mode Distributed -AllowCompilerMismatch:$AllowCompilerMismatch
$settings=[xml]'<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><DBEEnabled>true</DBEEnabled><DBEAdapterDirectory /></PropertyGroup></Project>'
$settings.Project.PropertyGroup.DBEAdapterDirectory=$adapter.AdapterDirectory
$settings.Save((Join-Path $projectRoot 'DBE.settings.props'))
[IO.File]::WriteAllText((Join-Path $projectRoot 'main.cpp'), "#include <cstdio>`nint remote_value();`nint main() { std::printf(`"DBE remote result: %d\n`", remote_value()); return 0; }`n")
$project=Join-Path $projectRoot 'remote-probe.vcxproj'
$solution=Join-Path $projectRoot 'remote-probe.sln'
$tools=Get-DbeToolchain '2022' $project
$devenv=Join-Path $tools.Root 'Common7\IDE\devenv.com'
$checks=[Collections.Generic.List[string]]::new()
function Build([string]$Name,[string]$Action='Build') {
    $log=Join-Path $run ($Name+'.log')
    & $devenv $solution "/$Action" 'Release|x64' /Out $log | Out-Null
    if($LASTEXITCODE -ne 0) {Get-Content -LiteralPath $log -Tail 20; throw "Visual Studio failed: $Name"}
    return $log
}
function Verify-Run([int]$Expected) {
    $output=& (Join-Path $projectRoot 'out\Release\bin\remote-probe.exe')
    if($LASTEXITCODE -ne 0 -or $output -ne "DBE remote result: $Expected") {throw 'Unexpected linked program result.'}
}
function Stamp { (Get-Item -LiteralPath (Join-Path $projectRoot 'out\Release\obj\value.obj')).LastWriteTimeUtc.Ticks }
$first=Build '01-rebuild' 'Rebuild'
if(@(Select-String -LiteralPath $first -SimpleMatch 'DBE: remote ').Count -ne 2) {throw 'Visual Studio did not compile both sources remotely.'}
Verify-Run 42; $checks.Add('devenv-rebuild-two-remote-sources-and-run-42')
$stamp=Stamp
$noop=Build '02-noop'
if((Stamp) -ne $stamp -or !(Select-String -LiteralPath $noop -SimpleMatch 'DBE: unchanged object ' -Quiet)) {throw 'Visual Studio skipped input validation or rewrote unchanged object.'}
$checks.Add('devenv-build-preprocesses-and-keeps-object')
$optional=Join-Path $projectRoot 'headers\optional.h'
[IO.File]::WriteAllText($optional,"#pragma once`n#define DBE_OPTIONAL 7`n")
try {
    $null=Build '03-new-header'; Verify-Run 49
    $checks.Add('devenv-build-detects-previously-absent-header')
} finally {Remove-Item -LiteralPath $optional}
$null=Build '04-restored'; Verify-Run 42
$checks.Add('devenv-build-detects-removed-header-and-restores-demo')
[pscustomobject]@{Run=$run;Solution=$solution;Adapter=$adapter.AdapterDirectory;Checks=$checks.ToArray();AllowCompilerMismatch=$AllowCompilerMismatch.IsPresent;Scope='Actual Visual Studio devenv solution build; interactive IDE buttons, error-list navigation and Stop Build not exercised.'} |
    ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $run 'verification.json')

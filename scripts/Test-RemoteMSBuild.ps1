[CmdletBinding()]
param([Parameter(Mandatory)][string]$DeploymentFile,[string]$PublishDirectory,[switch]$FirstBuildOnly)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$deployment=Get-Content -Raw -LiteralPath $DeploymentFile | ConvertFrom-Json
if(!$PublishDirectory) {$PublishDirectory=(& (Join-Path $PSScriptRoot 'Build-Worker.ps1') -SelfContained).Directory}
$run=Join-Path $root ('out\msbuild-remote 한글 '+[Guid]::NewGuid().ToString('N'))
$projectRoot=Join-Path $run 'project'; New-Item -ItemType Directory -Path $projectRoot | Out-Null
Copy-Item -Path (Join-Path $root 'fixture-remote\*') -Destination $projectRoot -Recurse
$adapter=& (Join-Path $PSScriptRoot 'New-MSBuildAdapter.ps1') -Destination (Join-Path $run 'adapter') -ClientConfig $deployment.ClientConfig -PublishDirectory $PublishDirectory -Mode Distributed -AllowCompilerMismatch
$settings=[xml]'<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><DBEEnabled>true</DBEEnabled><DBEAdapterDirectory /></PropertyGroup></Project>'
$settings.Project.PropertyGroup.DBEAdapterDirectory=$adapter.AdapterDirectory
$settings.Save((Join-Path $projectRoot 'DBE.settings.props'))
$project=Join-Path $projectRoot 'remote-probe.vcxproj'
$tools=Get-DbeToolchain '2022' $project
$checks=[Collections.Generic.List[string]]::new(); $measurements=[Collections.Generic.List[object]]::new()
function Build([string]$Step,[string]$Target='Build',[string]$Configuration='Release',[Nullable[bool]]$Failure=$false) {
    $log=Join-Path $run ($Step+'.log'); $watch=[Diagnostics.Stopwatch]::StartNew()
    & $tools.MSBuild $project /nologo /nr:false /m:1 "/t:$Target" "/p:Configuration=$Configuration" /p:Platform=x64 /v:normal *> $log
    $exit=$LASTEXITCODE; $watch.Stop(); $measurements.Add([pscustomobject]@{Step=$Step;ElapsedMs=$watch.ElapsedMilliseconds;Exit=$exit;Log=$log})
    if($null -ne $Failure -and (($exit -ne 0) -ne $Failure)) { Get-Content -LiteralPath $log -Tail 28; throw "Unexpected build result: $log" }
    return $log
}
function Stamp([string]$File='value.obj') { return (Get-Item -LiteralPath (Join-Path $projectRoot ('out\Release\obj\'+$File))).LastWriteTimeUtc.Ticks }
function Run-Result([int]$Expected,[string]$Configuration='Release') {
    & (Join-Path $projectRoot "out\$Configuration\bin\remote-probe.exe")
    if($LASTEXITCODE -ne $Expected) {throw "Executable returned $LASTEXITCODE; expected $Expected"}
}
$first=Build '01-full'
if(!(Select-String -LiteralPath $first -SimpleMatch 'DBE: remote ' -Quiet)) {throw 'Build succeeded without remote execution.'}
Run-Result 42; $checks.Add('real-msbuild-two-tu-remote-build-and-run-42')
if($FirstBuildOnly) { [pscustomobject]@{Run=$run;Project=$project;Log=$first} | ConvertTo-Json; return }
$initial=Stamp; $initialMain=Stamp 'main.obj'
$noop=Build '02-noop'
if((Stamp) -ne $initial -or (Stamp 'main.obj') -ne $initialMain -or !(Select-String -LiteralPath $noop -SimpleMatch 'DBE: unchanged object ' -Quiet)) {throw 'No-op changed object or missed input revalidation.'}
$checks.Add('no-op-repreprocess-keeps-object-timestamps')
[IO.File]::WriteAllText((Join-Path $projectRoot 'headers\value.h'),"#pragma once`n#define DBE_VALUE 41`n")
$null=Build '03-header'; Run-Result 43
if((Stamp) -eq $initial -or (Stamp 'main.obj') -ne $initialMain) {throw 'Header change did not selectively update the object.'}
$checks.Add('header-change-selective-object-and-run-43')
[IO.File]::WriteAllText((Join-Path $projectRoot 'headers\optional.h'),"#pragma once`n#define DBE_OPTIONAL 7`n")
$null=Build '04-new-include'; Run-Result 50; $checks.Add('previously-absent-include-added-and-run-50')
[IO.File]::WriteAllText((Join-Path $projectRoot 'forced.h'),"#pragma once`n#define DBE_DELTA 3`n")
$null=Build '05-forced-include'; Run-Result 51; $checks.Add('forced-include-change-and-run-51')
Add-Content -LiteralPath (Join-Path $projectRoot 'value.cpp') -Value 'int invalid_remote_function() { return DBE_UNDECLARED_TEST; }'
$errorLog=Build '06-remote-error' 'Build' 'Release' $true
if(!(Select-String -LiteralPath $errorLog -SimpleMatch 'DBE_UNDECLARED_TEST' -Quiet) -or (Test-Path -LiteralPath (Join-Path $projectRoot 'out\Release\obj\value.obj'))) {throw 'Remote compiler error was lost or stale object survived.'}
$checks.Add('remote-semantic-error-path-and-no-stale-object')
Copy-Item -LiteralPath (Join-Path $root 'fixture-remote\value.cpp') -Destination (Join-Path $projectRoot 'value.cpp') -Force
$null=Build '07-recovery'; Run-Result 51; $checks.Add('error-recovery')
$null=Build '08-debug' 'Build' 'Debug'; Run-Result 51 'Debug'; $checks.Add('debug-MDd-Od-RTC-Z7-profile')
$null=Build '09-clean' 'Clean'
if(Test-Path -LiteralPath (Join-Path $projectRoot 'out\Release\obj\value.obj')) {throw 'Clean left object behind.'}
$null=Build '10-rebuild' 'Rebuild'; Run-Result 51; $checks.Add('clean-and-rebuild')
$readLogs=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'out\Release\obj') -Recurse -Filter '*.read.*.tlog')
if(!$readLogs -or !(Select-String -LiteralPath $readLogs.FullName -SimpleMatch 'VALUE.H' -Quiet)) {throw 'Local preprocessing header was not recorded in tlogs.'}
$checks.Add('tracker-observes-local-header-access')
$configFile=Join-Path $adapter.AdapterDirectory 'msbuild.json'
$adapterConfig=Get-Content -Raw -LiteralPath $configFile | ConvertFrom-Json
$adapterConfig.allowCompilerMismatch=$false
$adapterConfig | ConvertTo-Json | Set-Content -LiteralPath $configFile -Encoding UTF8
$strict=Build '11-strict-identity' 'Build' 'Release' $null
$hasMismatch=Select-String -LiteralPath $strict -SimpleMatch 'Compiler identity mismatch' -Quiet
if($hasMismatch){if($measurements[$measurements.Count-1].Exit -eq 0){throw 'Mismatch was accepted'};$checks.Add('strict-compiler-identity-rejects-patch-difference')}
else {if($measurements[$measurements.Count-1].Exit -ne 0){throw 'Strict identity failed for an unexpected reason'};$checks.Add('strict-compiler-identity-accepts-matching-binaries')}
$adapterConfig.mode='Auto'
$adapterConfig | ConvertTo-Json | Set-Content -LiteralPath $configFile -Encoding UTF8
$auto=Build '12-auto-local'
if($hasMismatch -and !(Select-String -LiteralPath $auto -SimpleMatch 'DBE: local (' -Quiet)) {throw 'Auto did not choose local on mismatch.'}
Run-Result 51; $checks.Add('auto-respects-compiler-identity-and-preserves-build')
$adapterConfig.allowCompilerMismatch=$true
$adapterConfig.mode='Distributed'
$adapterConfig | ConvertTo-Json | Set-Content -LiteralPath $configFile -Encoding UTF8
$null=Build '13-remote-restored'; Run-Result 51
$checks.Add('remote-mode-restored-after-local')
$projectText=[IO.File]::ReadAllText($project)
$adapterConfig.mode='Auto'
$adapterConfig | ConvertTo-Json | Set-Content -LiteralPath $configFile -Encoding UTF8
try {
    [IO.File]::WriteAllText($project,$projectText.Replace('<DebugInformationFormat>OldStyle</DebugInformationFormat>','<DebugInformationFormat>EditAndContinue</DebugInformationFormat>'))
    $unsupported=Build '14-ZI-auto-local' 'Build' 'Debug'
    if(!(Select-String -LiteralPath $unsupported -SimpleMatch 'Unsupported compiler argument: /ZI' -Quiet)) {throw 'Unsupported /ZI was not sent to local compiler.'}
    Run-Result 51 'Debug'; $checks.Add('unsupported-ZI-auto-local-raw-command')
} finally { [IO.File]::WriteAllText($project,$projectText) }
$originalClient=$adapterConfig.clientConfig
$offline=Get-Content -Raw -LiteralPath $originalClient | ConvertFrom-Json
$offline.host='127.0.0.1'
$socket=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$socket.Start();$offline.port=$socket.LocalEndpoint.Port;$socket.Stop()
$offline.certificate=Join-Path (Split-Path $originalClient -Parent) $offline.certificate
$offlinePath=Join-Path $run 'offline-client.json'
$offline | ConvertTo-Json | Set-Content -LiteralPath $offlinePath -Encoding UTF8
$adapterConfig.clientConfig=$offlinePath
$adapterConfig | ConvertTo-Json | Set-Content -LiteralPath $configFile -Encoding UTF8
$offlineLog=Build '15-offline-auto'
if(!(Select-String -LiteralPath $offlineLog -SimpleMatch 'Worker unavailable:' -Quiet)) {throw 'Offline Auto fallback reason missing.'}
Run-Result 51;$checks.Add('unavailable-worker-auto-local')
$adapterConfig.mode='Distributed'
$adapterConfig | ConvertTo-Json | Set-Content -LiteralPath $configFile -Encoding UTF8
$null=Build '16-offline-distributed' 'Build' 'Release' $true
$checks.Add('unavailable-worker-distributed-fails')
$adapterConfig.clientConfig=$originalClient
$adapterConfig | ConvertTo-Json | Set-Content -LiteralPath $configFile -Encoding UTF8
$null=Build '17-final-remote'; Run-Result 51
$checks.Add('remote-restored-after-offline')
$result=[pscustomobject]@{Run=$run;Project=$project;Adapter=$adapter;Checks=$checks.ToArray();Measurements=$measurements.ToArray();Scope='Real MSBuild and remote Worker; Visual Studio UI build requires separate verification.'}
$result | ConvertTo-Json -Depth 7 | Tee-Object -FilePath (Join-Path $run 'verification.json')

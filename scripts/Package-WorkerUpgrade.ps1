[CmdletBinding()]
param([Parameter(Mandatory)][string]$DeploymentFile,[Parameter(Mandatory)][string]$PublishDirectory,[ValidateSet(2,3)][int]$ProtocolVersion=3,[string]$Compiler)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$old=Get-Content -Raw -LiteralPath $DeploymentFile | ConvertFrom-Json
$upgrade=Join-Path $root ('out\worker-upgrade-'+[Guid]::NewGuid().ToString('N'))
$worker=Join-Path $upgrade 'worker'; $client=Join-Path $upgrade 'client'
New-Item -ItemType Directory -Path $worker,$client | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $worker -Recurse
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $client -Recurse
$oldWorker=Split-Path $old.WorkerConfig -Parent; $oldClient=Split-Path $old.ClientConfig -Parent
foreach($name in @('identity.pfx','worker.json','start-worker.cmd','Allow-WorkerFirewall.ps1')) { Copy-Item -LiteralPath (Join-Path $oldWorker $name) -Destination $worker }
foreach($name in @('identity.pfx','client.json')) { Copy-Item -LiteralPath (Join-Path $oldClient $name) -Destination $client }
if($Compiler) {
    if($ProtocolVersion -ne 3){throw 'Bundled compiler requires the v3 package'}
    $compilerPath=(Resolve-Path -LiteralPath $Compiler).Path
    if([IO.Path]::GetFileName($compilerPath) -ne 'cl.exe'){throw 'Expected MSVC cl.exe'}
    $toolchain=Join-Path $worker 'toolchain';New-Item -ItemType Directory -Path $toolchain|Out-Null
    Copy-Item -Path (Join-Path (Split-Path $compilerPath -Parent) '*') -Destination $toolchain -Recurse
    $config=Get-Content -Raw -LiteralPath (Join-Path $worker 'worker.json')|ConvertFrom-Json
    $config.compiler='toolchain\cl.exe'
    $config|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $worker 'worker.json') -Encoding UTF8
}
Copy-Item -LiteralPath (Join-Path $root 'docs\worker-install.md') -Destination (Join-Path $worker 'README.md')
$zip=Join-Path $upgrade "dbe-worker-v$ProtocolVersion-win-x64.zip"
Compress-Archive -Path (Join-Path $worker '*') -DestinationPath $zip
$result=[pscustomobject]@{WorkerZip=$zip;ClientConfig=(Join-Path $client 'client.json');ClientExecutable=(Join-Path $client 'dbe-worker.exe');WorkerConfig=(Join-Path $worker 'worker.json')}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root "out\worker-deployment-v$ProtocolVersion.json") -Encoding UTF8
$result

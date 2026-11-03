[CmdletBinding()]
param([Parameter(Mandatory)][string]$DeploymentFile,[Parameter(Mandatory)][string]$PublishDirectory,[ValidateSet(2,3,4)][int]$ProtocolVersion=3,[string]$Compiler,
    [string]$ConnectionDirectory,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ConnectionFiles.ps1')
$old=Get-Content -Raw -LiteralPath $DeploymentFile | ConvertFrom-Json
$pair=Get-DbePersistentConnectionDirectory $ConnectionDirectory
if(Test-Path -LiteralPath $pair){throw 'Connection destination must be new'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('out/worker-upgrade-'+[Guid]::NewGuid().ToString('N'))}
$upgrade=[IO.Path]::GetFullPath($OutputDirectory);Assert-DbePlainConnectionPath $upgrade
Assert-DbeSeparateConnectionOutput $pair $upgrade
if(Test-Path -LiteralPath $upgrade){throw 'Upgrade output must be new'}
$worker=Join-Path $upgrade 'worker';$template=Join-Path $pair 'worker';$client=Join-Path $pair 'client'
New-Item -ItemType Directory -Path $worker,$client,$template | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $worker -Recurse
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $client -Recurse
$oldWorker=Split-Path $old.WorkerConfig -Parent; $oldClient=Split-Path $old.ClientConfig -Parent
foreach($name in @('identity.pfx','worker.json','start-worker.cmd','Allow-WorkerFirewall.ps1')) { Copy-Item -LiteralPath (Join-Path $oldWorker $name) -Destination $template }
foreach($name in @('identity.pfx','client.json')) { Copy-Item -LiteralPath (Join-Path $oldClient $name) -Destination $client }
if($Compiler) {
    if($ProtocolVersion -lt 3){throw 'Bundled compiler requires protocol v3 or later'}
    $compilerPath=(Resolve-Path -LiteralPath $Compiler).Path
    if([IO.Path]::GetFileName($compilerPath) -ne 'cl.exe'){throw 'Expected MSVC cl.exe'}
    $toolchain=Join-Path $worker 'toolchain';New-Item -ItemType Directory -Path $toolchain|Out-Null
    Copy-Item -Path (Join-Path (Split-Path $compilerPath -Parent) '*') -Destination $toolchain -Recurse
    $config=Get-Content -Raw -LiteralPath (Join-Path $template 'worker.json')|ConvertFrom-Json
    $config.compiler='toolchain\cl.exe'
    $config|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $template 'worker.json') -Encoding UTF8
}
Copy-Item -Path (Join-Path $template '*') -Destination $worker -Recurse
Copy-Item -LiteralPath (Join-Path $root 'docs\worker-install.md') -Destination (Join-Path $worker 'README.md')
$zip=Join-Path $upgrade "dbe-worker-v$ProtocolVersion-win-x64.zip"
Compress-Archive -Path (Join-Path $worker '*') -DestinationPath $zip
$result=[pscustomobject]@{WorkerZip=$zip;ClientConfig=(Join-Path $client 'client.json');ClientExecutable=(Join-Path $client 'dbe-worker.exe');WorkerConfig=(Join-Path $template 'worker.json');ConnectionDirectory=$pair;DeploymentFile=(Join-Path $pair 'deployment.json')}
$result | ConvertTo-Json | Set-Content -LiteralPath $result.DeploymentFile -Encoding UTF8
$result

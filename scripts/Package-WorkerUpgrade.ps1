[CmdletBinding(DefaultParameterSetName='Deployment')]
param([Parameter(Mandatory,ParameterSetName='Deployment')][string]$DeploymentFile,
    [Parameter(Mandatory,ParameterSetName='Recovered')][string]$ClientConfig,
    [Parameter(Mandatory)][string]$PublishDirectory,[ValidateSet(2,3,4)][int]$ProtocolVersion=3,[string]$Compiler,
    [string]$ConnectionDirectory,[string]$OutputDirectory)
# Runtime-only upgrade. The Worker PC owns its server identity, worker.json (allowed requester pin,
# compiler, state and receiving limits) and toolchain; Register-Requester may have changed the pin
# after packaging. The ZIP therefore never carries identity.pfx or worker.json and is extracted over
# the existing Worker folder.
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ConnectionFiles.ps1')
$installedConfig=$null;$oldClient=$null
if($PSCmdlet.ParameterSetName -eq 'Deployment'){
    $old=Read-DbeConnectionJson ([IO.Path]::GetFullPath($DeploymentFile))
    $clientSource=[IO.Path]::GetFullPath([string]$old.ClientConfig)
    # A deployment produced from a recovered connection has no Worker config record and refers to
    # the requester-owned client config in place.
    if($old.WorkerConfig){$installedConfig=[IO.Path]::GetFullPath([string]$old.WorkerConfig);$oldClient=Split-Path $clientSource -Parent}
} else {
    $clientSource=(Resolve-Path -LiteralPath $ClientConfig).ProviderPath
}
$client=Read-DbeConnectionJson $clientSource
if($client.workerPin -cnotmatch '^[0-9a-f]{64}$' -or [string]::IsNullOrWhiteSpace($client.certificate)){throw 'Invalid client configuration'}
$clientIdentity=[IO.Path]::GetFullPath([IO.Path]::Combine((Split-Path $clientSource -Parent),[string]$client.certificate))
if(!(Test-Path -LiteralPath $clientIdentity -PathType Leaf)){throw 'Client identity is missing; use the connection recovery workflow'}
if($Compiler){
    if($ProtocolVersion -lt 3){throw 'Bundled compiler requires protocol v3 or later'}
    # A runtime-only upgrade cannot change which compiler worker.json selects. Replacing the bundled
    # toolchain is allowed only when the installation record shows the Worker already uses it.
    if(!$installedConfig){throw 'Compiler replacement requires a deployment record; the recovered Worker config is not known on this PC'}
    $record=Read-DbeConnectionJson $installedConfig
    if([string]$record.compiler -cne 'toolchain\cl.exe'){throw 'The installed Worker does not use a bundled toolchain; compiler selection is a Worker config change outside runtime-only upgrade'}
    $compilerPath=(Resolve-Path -LiteralPath $Compiler).Path
    if([IO.Path]::GetFileName($compilerPath) -ne 'cl.exe'){throw 'Expected MSVC cl.exe'}
}
$pair=Get-DbePersistentConnectionDirectory $ConnectionDirectory
if(Test-Path -LiteralPath $pair){throw 'Connection destination must be new'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('out/worker-upgrade-'+[Guid]::NewGuid().ToString('N'))}
$upgrade=[IO.Path]::GetFullPath($OutputDirectory);Assert-DbePlainConnectionPath $upgrade
Assert-DbeSeparateConnectionOutput $pair $upgrade
if(Test-Path -LiteralPath $upgrade){throw 'Upgrade output must be new'}
$worker=Join-Path $upgrade 'worker';$clientRuntime=Join-Path $pair 'client'
New-Item -ItemType Directory -Path $worker,$clientRuntime | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $worker -Recurse
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $clientRuntime -Recurse
foreach($owned in @('identity.pfx','worker.json')){if(Test-Path -LiteralPath (Join-Path $worker $owned)){throw "Publish directory must not contain Worker-owned $owned"}}
if($Compiler){
    $toolchain=Join-Path $worker 'toolchain';New-Item -ItemType Directory -Path $toolchain|Out-Null
    Copy-Item -Path (Join-Path (Split-Path $compilerPath -Parent) '*') -Destination $toolchain -Recurse
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Allow-WorkerFirewall.ps1') -Destination $worker
Copy-Item -LiteralPath (Join-Path $root 'docs\worker-install.md') -Destination (Join-Path $worker 'README.md')
if($oldClient){
    # Deployment-owned client pair: carry the same key and config into the new record.
    foreach($name in @('identity.pfx','client.json')){Copy-Item -LiteralPath (Join-Path $oldClient $name) -Destination $clientRuntime}
    $newClientConfig=Join-Path $clientRuntime 'client.json'
    $template=Join-Path $pair 'worker';New-Item -ItemType Directory $template|Out-Null
    Copy-Item -LiteralPath $installedConfig -Destination $template
    $recordConfig=Join-Path $template 'worker.json'
} else {
    # Recovered requester: its client config stays with the durable requester identity.
    $newClientConfig=$clientSource;$recordConfig=$null
}
$zip=Join-Path $upgrade "dbe-worker-v$ProtocolVersion-win-x64.zip"
Compress-Archive -Path (Join-Path $worker '*') -DestinationPath $zip
$result=[pscustomobject]@{WorkerZip=$zip;Kind='runtime-only';ClientConfig=$newClientConfig;ClientExecutable=(Join-Path $clientRuntime 'dbe-worker.exe');
    WorkerConfig=$recordConfig;WorkerPin=$client.workerPin;ConnectionDirectory=$pair;DeploymentFile=(Join-Path $pair 'deployment.json')}
$result | ConvertTo-Json | Set-Content -LiteralPath $result.DeploymentFile -Encoding UTF8
$result

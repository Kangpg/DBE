[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConnectionDirectory,[Parameter(Mandatory)][string]$WorkerConnectionFile,
    [Parameter(Mandatory)][string]$HostName)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ConnectionFiles.ps1')
$root=(Resolve-Path -LiteralPath $ConnectionDirectory).ProviderPath
$profile=Read-DbeRequester $root -RequirePrivateKey
$reply=Read-DbeConnectionJson $WorkerConnectionFile
if($reply.version -ne 1 -or $reply.requesterId -cne $profile.requesterId -or $reply.requesterPin -cne $profile.requesterPin -or
    $reply.workerPin -cnotmatch '^[0-9a-f]{64}$' -or $reply.port -lt 1 -or $reply.port -gt 65535 -or
    [Uri]::CheckHostName($HostName) -eq [UriHostNameType]::Unknown){throw 'Invalid Worker connection export or requester identity'}
$directory=Join-Path $root ('workers/'+$reply.workerPin);Assert-DbePlainConnectionPath $directory
New-Item -ItemType Directory $directory -Force|Out-Null
$path=Join-Path $directory 'client.json'
Write-DbeConnectionJson $path ([ordered]@{host=$HostName;port=$reply.port;certificate='../../identity.pfx';workerPin=$reply.workerPin})
[pscustomobject]@{ClientConfig=$path;WorkerPin=$reply.workerPin;HostName=$HostName}

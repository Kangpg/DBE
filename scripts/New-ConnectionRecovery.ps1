[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory,[string]$ConnectionDirectory,[string]$BundleDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ConnectionFiles.ps1')
$root=Split-Path $PSScriptRoot -Parent
$data=Get-DbePersistentConnectionDirectory $ConnectionDirectory
$exe=Join-Path (Resolve-Path -LiteralPath $PublishDirectory).ProviderPath 'dbe-worker.exe'
if(!(Test-Path -LiteralPath $data)){
    & $exe requester $data
    if($LASTEXITCODE){throw 'Requester creation failed; preserve any partial directory for inspection'}
}
$profile=Read-DbeRequester $data -RequirePrivateKey
if(!$BundleDirectory){$BundleDirectory=Join-Path $root ('out/reconnect-'+$profile.requesterId)}
$bundle=[IO.Path]::GetFullPath($BundleDirectory);Assert-DbePlainConnectionPath $bundle
Assert-DbeSeparateConnectionOutput $data $bundle
if(Test-Path -LiteralPath $bundle){throw 'Bundle destination must be new; persistent requester data is reusable'}
New-Item -ItemType Directory $bundle|Out-Null
foreach($name in @('requester.cer','requester.json')){Copy-Item -LiteralPath (Join-Path $data $name) -Destination $bundle}
foreach($name in @('ConnectionFiles.ps1','Register-Requester.ps1','Register-Requester.cmd')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $bundle}
$zip=$bundle+'.zip'
if(Test-Path -LiteralPath $zip){throw 'Bundle ZIP already exists'}
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $zip
[pscustomobject]@{ConnectionDirectory=$data;RequesterId=$profile.requesterId;BundleDirectory=$bundle;WorkerRecoveryZip=$zip;PrivateKeyIncluded=$false}

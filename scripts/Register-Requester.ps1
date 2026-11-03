[CmdletBinding()]
param([string]$WorkerDirectory=$PSScriptRoot,[string]$RequesterDirectory=$PSScriptRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ConnectionFiles.ps1')
$root=(Resolve-Path -LiteralPath $WorkerDirectory).ProviderPath;Assert-DbePlainConnectionPath $root
$profile=Read-DbeRequester ((Resolve-Path -LiteralPath $RequesterDirectory).ProviderPath)
$configPath=Join-Path $root 'worker.json';$exe=Join-Path $root 'dbe-worker.exe'
Assert-DbePlainConnectionPath $exe
if(!(Test-Path -LiteralPath $exe -PathType Leaf)){throw 'Expected the existing Worker runtime'}
foreach($p in @(Get-CimInstance Win32_Process -Filter "Name='dbe-worker.exe'" -ErrorAction Stop)){
    if(!$p.ExecutablePath){throw 'Cannot resolve a running Worker identity; stop Workers normally and retry'}
    if([IO.Path]::GetFullPath($p.ExecutablePath).Equals([IO.Path]::GetFullPath($exe),[StringComparison]::OrdinalIgnoreCase)){throw 'Stop this Worker normally with Ctrl+C before registration'}
}
$config=Read-DbeConnectionJson $configPath
if([string]::IsNullOrWhiteSpace($config.stateDirectory) -or [string]::IsNullOrWhiteSpace($config.certificate) -or
    $config.allowedClientPin -notmatch '^[0-9a-fA-F]{64}$' -or $config.port -lt 1 -or $config.port -gt 65535){throw 'Invalid existing Worker configuration'}
$original=[IO.File]::ReadAllBytes($configPath)
$state=[IO.Path]::GetFullPath([IO.Path]::Combine($root,[string]$config.stateDirectory));Assert-DbePlainConnectionPath $state
$gatePath=Join-Path $root 'connection-registration.lock';Assert-DbePlainConnectionPath $gatePath
$gate=$null;$stateLock=$null;$pending=$null
try {
    $gate=[IO.File]::Open($gatePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    if(Test-Path -LiteralPath $state -PathType Container){
        $lockPath=Join-Path $state 'worker.lock';Assert-DbePlainConnectionPath $lockPath
        if(!(Test-Path -LiteralPath $lockPath -PathType Leaf)){throw 'Existing state has no Worker ownership lock; inspect before registration'}
        $stateLock=[IO.File]::Open($lockPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    }
    $certificate=[IO.Path]::GetFullPath([IO.Path]::Combine($root,[string]$config.certificate));Assert-DbePlainConnectionPath $certificate
    $cert=[Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate,'',[Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet)
    try {
        if(!$cert.HasPrivateKey -or $cert.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow -or $cert.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow){throw 'Existing Worker identity is unavailable or expired'}
        $workerPin=Get-DbeConnectionPin $cert
    } finally {$cert.Dispose()}
    if([Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath)) -cne [Convert]::ToBase64String($original)){throw 'Worker config changed during registration'}
    $changed=$config.allowedClientPin -cne $profile.requesterPin;$backup=$null
    if($changed){
        $config.allowedClientPin=$profile.requesterPin
        $pending=$configPath+'.pending-'+[Guid]::NewGuid().ToString('N');$backup=$configPath+'.backup-'+[Guid]::NewGuid().ToString('N')
        [IO.File]::WriteAllText($pending,($config|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
        [IO.File]::Replace($pending,$configPath,$backup);$pending=$null
    }
    $export=Join-Path $root ('connection-'+$profile.requesterId+'.json')
    $connection=[ordered]@{version=1;requesterId=$profile.requesterId;requesterPin=$profile.requesterPin;workerPin=$workerPin;port=$config.port}
    Write-DbeConnectionJson $export $connection
    [pscustomobject]@{Changed=$changed;ConfigBackup=$backup;ConnectionFile=$export;Connection=$connection}|ConvertTo-Json -Depth 6
} finally {
    if($stateLock){$stateLock.Dispose()};if($gate){$gate.Dispose()}
    if($pending -and (Test-Path -LiteralPath $pending)){Remove-Item -LiteralPath $pending}
}

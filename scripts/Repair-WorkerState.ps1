[CmdletBinding()]
param([string]$WorkerDirectory=$PSScriptRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=(Resolve-Path -LiteralPath $WorkerDirectory).ProviderPath
function Assert-PlainPath([string]$Path){
    for($part=[IO.Path]::GetFullPath($Path);$part;$part=[IO.Path]::GetDirectoryName($part)){
        if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse paths are not supported for Worker state repair'}
    }
}
Assert-PlainPath $root
$configPath=Join-Path $root 'worker.json'
$executable=Join-Path $root 'dbe-worker.exe'
if(!(Test-Path -LiteralPath $configPath -PathType Leaf) -or !(Test-Path -LiteralPath $executable -PathType Leaf)){throw 'Expected worker.json and dbe-worker.exe in WorkerDirectory'}
Assert-PlainPath $configPath
$original=[IO.File]::ReadAllBytes($configPath)
$config=[IO.File]::ReadAllText($configPath)|ConvertFrom-Json
if(!$config.stateDirectory -or [string]::IsNullOrWhiteSpace([string]$config.stateDirectory)){throw 'Worker stateDirectory is missing'}
foreach($process in @(Get-CimInstance Win32_Process -Filter "Name='dbe-worker.exe'" -ErrorAction Stop)){
    if(!$process.ExecutablePath){throw 'Cannot resolve a running Worker identity; close Worker processes and retry'}
    if([IO.Path]::GetFullPath($process.ExecutablePath).Equals([IO.Path]::GetFullPath($executable),[StringComparison]::OrdinalIgnoreCase)){throw 'Worker is still running; stop it normally before repairing state'}
}
$oldState=[IO.Path]::GetFullPath([IO.Path]::Combine($root,[string]$config.stateDirectory))
Assert-PlainPath $oldState
$contexts=Join-Path $oldState 'ctx'
Assert-PlainPath $contexts
$hasContexts=(Test-Path -LiteralPath $contexts -PathType Container) -and @([IO.Directory]::EnumerateFileSystemEntries($contexts)).Count -gt 0
$hasAttempts=(Test-Path -LiteralPath $oldState -PathType Container) -and @(Get-ChildItem -LiteralPath $oldState -Directory -Filter 'attempt-*').Count -gt 0
if(!$hasContexts -and !$hasAttempts){[pscustomobject]@{Changed=$false;StateDirectory=$oldState;Reason='No retained contexts or attempts'};return}
$lockPath=Join-Path $oldState 'worker.lock'
Assert-PlainPath $lockPath
if(!(Test-Path -LiteralPath $lockPath -PathType Leaf)){throw 'Retained state has no ownership lock; automatic repair is unavailable'}
$ownership=$null;$pending=$null
try{
    $ownership=[IO.File]::Open($lockPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $name='state-'+[Guid]::NewGuid().ToString('N')
    $newState=[IO.Path]::GetFullPath((Join-Path $root $name))
    if(!$newState.StartsWith($root.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'New state escaped WorkerDirectory'}
    if(Test-Path -LiteralPath $newState){throw 'New state directory already exists'}
    $config.stateDirectory=$name
    $json=$config|ConvertTo-Json -Depth 16
    if([Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath)) -ne [Convert]::ToBase64String($original)){throw 'Worker config changed during repair'}
    $pending=$configPath+'.pending-'+[Guid]::NewGuid().ToString('N')
    $backup=$configPath+'.backup-'+[Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($pending,$json,[Text.UTF8Encoding]::new($false))
    # Atomic replacement creates the backup; old state is never renamed/deleted.
    [IO.File]::Replace($pending,$configPath,$backup)
    $pending=$null
    [pscustomobject]@{Changed=$true;PreservedState=$oldState;StateDirectory=$newState;ConfigBackup=$backup}
}finally{
    if($ownership){$ownership.Dispose()}
    if($pending -and (Test-Path -LiteralPath $pending)){Remove-Item -LiteralPath $pending}
}

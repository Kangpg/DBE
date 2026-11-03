[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory,[Parameter(Mandatory)][string]$ChildProbe,[string]$Compiler,[string]$OutputRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
if(!$Compiler){$Compiler=(Get-DbeToolchain '2022' (Join-Path $root 'native/dbe-cl.vcxproj')).Compiler}
$publish=(Resolve-Path -LiteralPath $PublishDirectory).Path
$child=(Resolve-Path -LiteralPath $ChildProbe).Path
$base=if($OutputRoot){[IO.Path]::GetFullPath($OutputRoot)}else{Join-Path $root 'out'}
$run=Join-Path $base ('state-recovery-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $run -Force|Out-Null
$checks=[Collections.Generic.List[string]]::new()
function Check([bool]$Value,[string]$Name){if(!$Value){throw "FAIL $Name"};$checks.Add($Name)}
function Fixture([string]$Name){
    $dir=Join-Path $run $Name;New-Item -ItemType Directory (Join-Path $dir 'state/ctx/retained') -Force|Out-Null
    Copy-Item -LiteralPath $child -Destination (Join-Path $dir 'dbe-worker.exe')
    [IO.File]::WriteAllText((Join-Path $dir 'state/worker.lock'),'')
    [IO.File]::WriteAllText((Join-Path $dir 'state/ctx/retained/evidence.txt'),'preserve this evidence')
    [ordered]@{stateDirectory='state';compiler='toolchain\cl.exe';bindAddress='127.0.0.1';port=43127;certificate='identity.pfx';allowedClientPin=('a'*64)}|ConvertTo-Json|Set-Content (Join-Path $dir 'worker.json') -Encoding utf8
    return $dir
}
function Repair([string]$Directory,[bool]$Failure=$false){
    $log=Join-Path $run ([guid]::NewGuid().ToString('N')+'.log')
    & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Repair-WorkerState.ps1') -WorkerDirectory $Directory *> $log
    if(($LASTEXITCODE -ne 0) -ne $Failure){Get-Content $log;throw 'Unexpected state repair result'}
}
$dir=Fixture 'preservation';$config=Join-Path $dir 'worker.json';$before=(Get-FileHash $config).Hash
Repair $dir
$after=Get-Content $config -Raw|ConvertFrom-Json;$saved=@(Get-ChildItem $dir -Filter 'worker.json.backup-*')
Check ($after.stateDirectory -ne 'state' -and $saved.Count -eq 1 -and (Get-FileHash $saved[0].FullName).Hash -eq $before -and [IO.File]::ReadAllText((Join-Path $dir 'state/ctx/retained/evidence.txt')) -eq 'preserve this evidence') 'repair-preserves-original-config-and-retained-evidence'
$original=Get-Content $saved[0].FullName -Raw|ConvertFrom-Json;$original.stateDirectory=$after.stateDirectory
Check (($original|ConvertTo-Json -Compress) -eq ($after|ConvertTo-Json -Compress)) 'repair-changes-only-state-directory'
$stamp=(Get-FileHash $config).Hash;Repair $dir
Check ((Get-FileHash $config).Hash -eq $stamp -and @(Get-ChildItem $dir -Filter 'worker.json.backup-*').Count -eq 1) 'repair-without-leftovers-is-idempotent'
$dir=Fixture 'locked';$config=Join-Path $dir worker.json;$before=(Get-FileHash $config).Hash
$lock=[IO.File]::Open((Join-Path $dir 'state/worker.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{Repair $dir $true}finally{$lock.Dispose()}
Check ((Get-FileHash $config).Hash -eq $before) 'locked-state-refuses-config-mutation'
$dir=Fixture 'malformed';$config=Join-Path $dir worker.json;[IO.File]::WriteAllText($config,'{bad-json');$before=(Get-FileHash $config).Hash
Repair $dir $true;Check ((Get-FileHash $config).Hash -eq $before) 'malformed-config-preserved'
$dir=Fixture 'active';$config=Join-Path $dir worker.json;$before=(Get-FileHash $config).Hash
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $publish 'dbe-worker.exe'));$start.UseShellExecute=$false;$start.CreateNoWindow=$true
foreach($arg in @('process-test',(Join-Path $dir dbe-worker.exe),$dir)){$start.ArgumentList.Add($arg)}
$owner=[Diagnostics.Process]::Start($start);$handles=[Collections.Generic.List[Diagnostics.Process]]::new()
try{
    $marker=Join-Path $dir pids.txt;$watch=[Diagnostics.Stopwatch]::StartNew()
    while(!(Test-Path $marker) -and !$owner.HasExited -and $watch.Elapsed.TotalSeconds -lt 10){Start-Sleep -Milliseconds 50}
    if(!(Test-Path $marker)){throw 'Owned active Worker fixture did not start'}
    foreach($id in ([IO.File]::ReadAllText($marker).Split(' '))){$handle=[Diagnostics.Process]::GetProcessById([int]$id);$null=$handle.Handle;$handles.Add($handle)}
    Repair $dir $true
    Check ((Get-FileHash $config).Hash -eq $before) 'active-worker-refuses-config-mutation'
}finally{if(!$owner.HasExited){$owner.Kill();$owner.WaitForExit(10000)|Out-Null};foreach($handle in $handles){if(!$handle.WaitForExit(10000)){throw 'Owned probe child survived owner exit'};$handle.Dispose()};$owner.Dispose()}
$pair=Join-Path $run 'live-pair'; & (Join-Path $publish dbe-worker.exe) pair $pair $Compiler
if($LASTEXITCODE){throw 'Live recovery pair failed'}
$dir=Join-Path $pair worker;Copy-Item -Path (Join-Path $publish '*') -Destination $dir -Recurse
New-Item -ItemType Directory (Join-Path $dir 'state/ctx/retained') -Force|Out-Null
[IO.File]::WriteAllText((Join-Path $dir 'state/worker.lock'),'');[IO.File]::WriteAllText((Join-Path $dir 'state/ctx/retained/evidence.txt'),'keep')
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
foreach($file in @((Join-Path $dir worker.json),(Join-Path $pair 'client/client.json'))){$cfg=Get-Content $file -Raw|ConvertFrom-Json;$cfg.port=$port;$cfg|ConvertTo-Json|Set-Content $file -Encoding utf8}
Repair $dir
$server=Start-Process -FilePath (Join-Path $dir dbe-worker.exe) -ArgumentList @('serve',('"'+(Join-Path $dir worker.json)+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run server.log) -RedirectStandardError (Join-Path $run server-error.log)
try{
    $ready=$false
    for($i=0;$i -lt 40;$i++){& (Join-Path $publish dbe-worker.exe) info (Join-Path $pair 'client/client.json') 6 *> (Join-Path $run info.json);if($LASTEXITCODE -eq 0){$ready=$true;break};if($server.HasExited){break};Start-Sleep -Milliseconds 100}
    Check ($ready -and [IO.File]::ReadAllText((Join-Path $dir 'state/ctx/retained/evidence.txt')) -eq 'keep') 'repaired-worker-starts-with-existing-pair-and-preserved-old-state'
}finally{if(!$server.HasExited){$server.Kill();$server.WaitForExit(10000)|Out-Null};$server.Dispose()}
[pscustomobject]@{Run=$run;Checks=$checks.ToArray()}|ConvertTo-Json|Tee-Object -FilePath (Join-Path $run checks.json)

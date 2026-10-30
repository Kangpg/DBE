[CmdletBinding()]
param([Parameter(Mandatory)][string]$DeploymentFile)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$deployment = Get-Content -Raw -LiteralPath $DeploymentFile | ConvertFrom-Json
$root = Split-Path $PSScriptRoot -Parent
$run = Join-Path $root ('out\package-test-' + [Guid]::NewGuid().ToString('N'))
$extracted = Join-Path $run 'extracted 한글'
Expand-Archive -LiteralPath $deployment.WorkerZip -DestinationPath $extracted
if (Test-Path -LiteralPath (Join-Path $extracted 'client.json')) { throw 'Client configuration leaked into Worker ZIP.' }
foreach ($required in @('dbe-worker.exe','dbe-process.dll','coreclr.dll','identity.pfx','worker.json','start-worker.cmd','Allow-WorkerFirewall.ps1')) {
    if (!(Test-Path -LiteralPath (Join-Path $extracted $required))) { throw "Package missing $required" }
}
$config = Get-Content -Raw -LiteralPath (Join-Path $extracted 'worker.json') | ConvertFrom-Json
$config.BindAddress = '127.0.0.1'; $config.Port = 0
$workerConfig = Join-Path $extracted 'loopback.json'
$config | ConvertTo-Json | Set-Content -LiteralPath $workerConfig -Encoding UTF8
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = Join-Path $extracted 'dbe-worker.exe'
$info.Arguments = 'serve "' + $workerConfig + '"'
$info.UseShellExecute = $false; $info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
$owner = [Diagnostics.Process]::Start($info)
try {
    $lineTask = $owner.StandardOutput.ReadLineAsync()
    if (!$lineTask.Wait(15000)) { throw 'Packaged server did not start.' }
    if (!$lineTask.Result) { throw ('Server exited: ' + $owner.StandardError.ReadToEnd()) }
    $startup = $lineTask.Result | ConvertFrom-Json
    if ($startup.state -ne 'listening') { throw 'Unexpected startup state.' }
    $port = [int]($startup.detail.Split(':')[-1])
    $client = Get-Content -Raw -LiteralPath $deployment.ClientConfig | ConvertFrom-Json
    $client.Host = '127.0.0.1'; $client.Port = $port
    $client.Certificate = Join-Path (Split-Path $deployment.ClientConfig -Parent) $client.Certificate
    $clientConfig = Join-Path $run 'client-loopback.json'
    $client | ConvertTo-Json | Set-Content -LiteralPath $clientConfig -Encoding UTF8
    & $deployment.ClientExecutable info $clientConfig *> (Join-Path $run 'info.json')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged TLS handshake failed.' }
    & $deployment.ClientExecutable probe $clientConfig (Join-Path $run 'result') *> (Join-Path $run 'probe.log')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged probe failed.' }
    $result = Get-Content -Raw -LiteralPath (Join-Path $run 'result\result.json') | ConvertFrom-Json
    if ($result.result.status -ne 'completed') { throw 'Packaged result was not completed.' }
    $objectHash = (Get-FileHash -LiteralPath (Join-Path $run 'result\result.obj')).Hash.ToLowerInvariant()
    if ($objectHash -ne $result.result.output.sha256) { throw 'Packaged object hash mismatch.' }
    $runtimePath = @($owner.Modules | Where-Object ModuleName -eq 'coreclr.dll' | ForEach-Object {$_.FileName})
    if ($runtimePath.Count -ne 1 -or !([IO.Path]::GetFullPath($runtimePath[0])).StartsWith([IO.Path]::GetFullPath($extracted) + '\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Worker did not load its bundled runtime.'
    }
    [pscustomobject]@{Status='passed';Run=$run;Port=$port;BundledRuntime=$runtimePath[0];ObjectSHA256=$objectHash;WorkerOnlyArchive=$true} |
        ConvertTo-Json | Tee-Object -FilePath (Join-Path $run 'verification.json')
} finally {
    if (!$owner.HasExited) { $owner.Kill(); $owner.WaitForExit(10000) | Out-Null }
    $owner.Dispose()
}

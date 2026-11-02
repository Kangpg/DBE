[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string[]]$ClientConfigs
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$run = Join-Path $root ('out/native-pch-remote-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$worker = Join-Path $PublishDirectory 'dbe-worker.exe'
$index = 0
foreach ($config in $ClientConfigs) {
    $destination = Join-Path $run ([string]$index)
    $log = Join-Path $run ($index.ToString() + '.log')
    & $worker native-pch-test $config $Compiler $destination *> $log
    if ($LASTEXITCODE -ne 0) { throw "Native PCH verification failed. See $log" }
    $index++
}
[pscustomobject]@{
    Run=$run
    WorkersVerified=$index
    Scope='Explicit relative inputs, native Worker Yc/Yu, ordered includes, macros, forced include, generated-header change, producer error, recovery, returned objects and PDB, local link and executable result.'
}

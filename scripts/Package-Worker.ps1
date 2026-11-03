[CmdletBinding()]
param([string]$PublishDirectory,[Parameter(Mandatory)][string]$WorkerAddress,[string]$Compiler,[switch]$BundleCompiler,
    [string]$ConnectionDirectory,[string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ConnectionFiles.ps1')
$pair=Get-DbePersistentConnectionDirectory $ConnectionDirectory
if(Test-Path -LiteralPath $pair){throw 'Connection destination must be new'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('out/worker-package-'+[Guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory);Assert-DbePlainConnectionPath $output
Assert-DbeSeparateConnectionOutput $pair $output
if(Test-Path -LiteralPath $output){throw 'Package output must be new'}
if (!$PublishDirectory) { $PublishDirectory = (& (Join-Path $PSScriptRoot 'Build-Worker.ps1') -SelfContained).Directory }
if (!(Test-Path -LiteralPath (Join-Path $PublishDirectory 'coreclr.dll'))) { throw 'Package requires a self-contained publish.' }
# Resolve the caller's installed toolchain when no compiler is selected.
if(!$Compiler){
    . (Join-Path $PSScriptRoot 'Toolchain.ps1')
    $Compiler=(Get-DbeToolchain '2022' (Join-Path $root 'native\dbe-cl.vcxproj')).Compiler
}
$address=[Net.IPAddress]::Parse($WorkerAddress)
if($address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw 'Expected an IPv4 Worker address'}
$compiler=(Resolve-Path -LiteralPath $Compiler).Path
& (Join-Path $PublishDirectory 'dbe-worker.exe') pair $pair $compiler
if ($LASTEXITCODE -ne 0) { throw 'Pair creation failed.' }
$template = Join-Path $pair 'worker'
$worker = Join-Path $output 'worker'
New-Item -ItemType Directory $worker|Out-Null
$client = Join-Path $pair 'client'
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $worker -Recurse
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $client -Recurse
$workerConfig=Join-Path $template 'worker.json';$clientConfig=Join-Path $client 'client.json'
$wc=Get-Content -Raw $workerConfig|ConvertFrom-Json;$wc.bindAddress=$address.ToString()
$cc=Get-Content -Raw $clientConfig|ConvertFrom-Json;$cc.host=$address.ToString()
if($BundleCompiler){
    $toolchain=Join-Path $worker 'toolchain';New-Item -ItemType Directory $toolchain|Out-Null
    Copy-Item -Path (Join-Path (Split-Path $compiler -Parent) '*') -Destination $toolchain -Recurse
    $wc.compiler='toolchain\cl.exe'
}
$wc|ConvertTo-Json|Set-Content $workerConfig -Encoding UTF8
$cc|ConvertTo-Json|Set-Content $clientConfig -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $root 'docs\worker-install.md') -Destination (Join-Path $template 'README.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Allow-WorkerFirewall.ps1') -Destination $template
@'
@echo off
cd /d "%~dp0"
"%~dp0dbe-worker.exe" serve "%~dp0worker.json"
pause
'@ | Set-Content -LiteralPath (Join-Path $template 'start-worker.cmd') -Encoding ASCII
Copy-Item -Path (Join-Path $template '*') -Destination $worker -Recurse
$zip = Join-Path $output 'dbe-worker-win-x64.zip'
Compress-Archive -Path (Join-Path $worker '*') -DestinationPath $zip
$result=[pscustomobject]@{WorkerZip=$zip;ClientConfig=$clientConfig;ClientExecutable=(Join-Path $client 'dbe-worker.exe');WorkerConfig=$workerConfig;ConnectionDirectory=$pair;DeploymentFile=(Join-Path $pair 'deployment.json')}
$result|ConvertTo-Json|Set-Content $result.DeploymentFile -Encoding UTF8
$result

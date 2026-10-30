[CmdletBinding()]
param([string]$PublishDirectory,[string]$Compiler)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$project = Join-Path $root 'native\dbe-cl.vcxproj'
$tools = Get-DbeToolchain '2022' $project
if(!$Compiler){$Compiler=$tools.Compiler}
if (!$PublishDirectory) { $PublishDirectory = (& (Join-Path $PSScriptRoot 'Build-Worker.ps1')).Directory }
$build = Join-Path $root 'out\worker-test-build'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$log = Join-Path $build 'child-build.log'
& $tools.MSBuild $project /nologo /nr:false /m:1 /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v143 /p:PreferredToolArchitecture=x64 "/p:DBESource=$root\tests\worker-child.cpp" /p:TargetName=worker-child "/p:OutDir=$build\" "/p:IntDir=$build\obj\" /v:normal *> $log
if ($LASTEXITCODE -ne 0) { throw "Worker child fixture failed to build: $log" }
$run = Join-Path $root ('out\worker-test-' + [Guid]::NewGuid().ToString('N'))
$testLog = $run + '.log'
& (Join-Path $PublishDirectory 'dbe-worker.exe') self-test $Compiler (Join-Path $build 'worker-child.exe') $run *> $testLog
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $testLog -Tail 20; throw "Worker tests failed: $testLog" }
[pscustomobject]@{Run=$run;Log=$testLog;Checks=(Join-Path $run 'checks.json')}

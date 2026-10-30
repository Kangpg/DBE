[CmdletBinding()]
param([ValidateSet('2022', '2026')][string]$VisualStudio = '2022')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'native\dbe-cl.vcxproj'
$tools = Get-DbeToolchain $VisualStudio $project
$output = Join-Path $root "out\wrapper-$VisualStudio"
New-Item -ItemType Directory -Path $output -Force | Out-Null
$log = Join-Path $output 'build.log'
& $tools.MSBuild $project /nologo /nr:false /m:1 /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$($tools.Toolset)" /p:PreferredToolArchitecture=x64 "/p:OutDir=$output\bin\" "/p:IntDir=$output\obj\" /v:normal *> $log
if ($LASTEXITCODE -ne 0) { throw "Wrapper build failed. See $log" }
[pscustomobject]@{ Wrapper=(Join-Path $output 'bin\dbe-cl.exe'); Compiler=$tools.Compiler; Log=$log }

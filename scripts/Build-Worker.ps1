[CmdletBinding()]
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'native\dbe-cl.vcxproj'
$tools = Get-DbeToolchain '2022' $project
$output = Join-Path $root 'out\worker-build'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$nativeLog = Join-Path $output 'native.log'
& $tools.MSBuild $project /nologo /nr:false /m:1 /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v143 /p:PreferredToolArchitecture=x64 /p:ConfigurationType=DynamicLibrary "/p:DBESource=$root\native\worker-process.cpp" /p:TargetName=dbe-process "/p:OutDir=$output\native\" "/p:IntDir=$output\obj\" /v:normal *> $nativeLog
if ($LASTEXITCODE -ne 0) { throw "Native Worker build failed: $nativeLog" }
$pathsLog = Join-Path $output 'paths.log'
& $tools.MSBuild $project /nologo /nr:false /m:1 /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v143 /p:PreferredToolArchitecture=x64 "/p:DBESource=$root\native\dbe-paths.cpp" /p:TargetName=dbe-paths "/p:OutDir=$output\paths\" "/p:IntDir=$output\paths-obj\" /v:normal *> $pathsLog
if ($LASTEXITCODE -ne 0) { throw "Native path launcher build failed: $pathsLog" }
$publish = Join-Path $output $(if ($SelfContained) { 'portable' } else { 'framework' })
$managedLog = Join-Path $output 'managed.log'
Push-Location (Join-Path $root 'worker')
try {
    & 'C:\Program Files\dotnet\dotnet.exe' publish Dbe.Worker.csproj -c Release -r win-x64 --self-contained $SelfContained.IsPresent.ToString().ToLowerInvariant() -o $publish --nologo *> $managedLog
    if ($LASTEXITCODE -ne 0) { throw "Managed Worker build failed: $managedLog" }
} finally { Pop-Location }
Copy-Item -LiteralPath (Join-Path $output 'native\dbe-process.dll') -Destination $publish -Force
Copy-Item -LiteralPath (Join-Path $output 'paths\dbe-paths.exe') -Destination $publish -Force
[pscustomobject]@{Executable=(Join-Path $publish 'dbe-worker.exe');Directory=$publish;Compiler=$tools.Compiler;NativeLog=$nativeLog;ManagedLog=$managedLog}

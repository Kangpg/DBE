[CmdletBinding()]
param([ValidateSet('2022', '2026')][string]$VisualStudio = '2022', [string]$Wrapper)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$tools = Get-DbeToolchain $VisualStudio (Join-Path $root 'fixture\probe.vcxproj')
$msbuild = $tools.MSBuild
$toolset = $tools.Toolset
$mode = if ($Wrapper) { 'wrapper' } else { 'baseline' }
$run = Join-Path $root ('out\' + $mode + '-' + $VisualStudio + ' 한글 경로 ' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
Copy-Item -Path (Join-Path $root 'fixture\*') -Destination $run
$project = Join-Path $run 'probe.vcxproj'
$extra = @()
if ($Wrapper) {
    $wrapperDirectory = Join-Path $run 'wrapper 도구'
    New-Item -ItemType Directory -Path $wrapperDirectory | Out-Null
    Copy-Item -LiteralPath $Wrapper -Destination (Join-Path $wrapperDirectory 'dbe-cl.exe')
    $extra = @('/p:DBEEnabled=true', "/p:DBEWrapperDirectory=$wrapperDirectory", "/p:DBETargetsFile=$root\msbuild\DBE.Local.targets")
}
$measurements = [System.Collections.Generic.List[object]]::new()
function Invoke-ProbeBuild([string]$Name, [string]$Target = 'Build', [bool]$ExpectFailure = $false) {
    $log = Join-Path $run ($Name + '.log')
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & $msbuild $project /nologo /nr:false /m:1 "/t:$Target" /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$toolset" /p:PreferredToolArchitecture=x64 @extra /v:normal *> $log
    $code = $LASTEXITCODE
    $watch.Stop()
    $measurements.Add([pscustomobject]@{ step=$Name; elapsedMs=$watch.ElapsedMilliseconds; exitCode=$code; log=$log })
    if (($code -ne 0) -ne $ExpectFailure) { throw "Unexpected build result ($Name, exit $code). See $log" }
}
function Get-ObjectStamp {
    $obj = Join-Path $run 'out\obj\main.obj'
    return (Get-Item -LiteralPath $obj).LastWriteTimeUtc.Ticks
}
$previousCompiler = [Environment]::GetEnvironmentVariable('DBE_REAL_CL', 'Process')
try {
    if ($Wrapper) { $env:DBE_REAL_CL = $tools.Compiler }
    Invoke-ProbeBuild '01-full'
    if ($Wrapper -and !(Select-String -LiteralPath (Join-Path $run '01-full.log') -SimpleMatch 'dbe-cl.exe' -Quiet)) { throw 'Build did not invoke wrapper.' }
    $readLogs = @(Get-ChildItem -LiteralPath (Join-Path $run 'out\obj') -Recurse -Filter '*.read.*.tlog')
    if (!$readLogs -or !(Select-String -LiteralPath $readLogs.FullName -SimpleMatch 'VALUE.H' -Quiet)) { throw 'Tracker did not record included header.' }
    $initial = Get-ObjectStamp
    & (Join-Path $run 'out\bin\probe.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Fixture executable returned failure.' }
    Invoke-ProbeBuild '02-noop'
    if ((Get-ObjectStamp) -ne $initial) { throw 'No-op build recompiled main.obj.' }
    Start-Sleep -Milliseconds 1100
    Add-Content -LiteralPath (Join-Path $run 'main.cpp') -Value '// source-change probe'
    Invoke-ProbeBuild '03-source-change'
    $sourceChanged = Get-ObjectStamp
    if ($sourceChanged -eq $initial) { throw 'Source change did not recompile main.obj.' }
    Start-Sleep -Milliseconds 1100
    Add-Content -LiteralPath (Join-Path $run 'value.h') -Value '// header-change probe'
    Invoke-ProbeBuild '04-header-change'
    if ((Get-ObjectStamp) -eq $sourceChanged) { throw 'Header change did not recompile main.obj.' }
    Invoke-ProbeBuild '05-clean' 'Clean'
    if (Test-Path -LiteralPath (Join-Path $run 'out\obj\main.obj')) { throw 'Clean left main.obj behind.' }
    Invoke-ProbeBuild '06-rebuild' 'Rebuild'
    & (Join-Path $run 'out\bin\probe.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Rebuilt fixture executable returned failure.' }
    Add-Content -LiteralPath (Join-Path $run 'main.cpp') -Value '#error DBE_EXPECTED_COMPILER_ERROR'
    Invoke-ProbeBuild '07-compiler-error' 'Build' $true
    if (!(Select-String -LiteralPath (Join-Path $run '07-compiler-error.log') -SimpleMatch 'DBE_EXPECTED_COMPILER_ERROR' -Quiet)) { throw 'Compiler diagnostic did not reach MSBuild.' }
    Copy-Item -LiteralPath (Join-Path $root 'fixture\main.cpp') -Destination (Join-Path $run 'main.cpp') -Force
    # Copy can restore an older timestamp; explicitly touch the input for this recovery check.
    (Get-Item -LiteralPath (Join-Path $run 'main.cpp')).LastWriteTimeUtc = [DateTime]::UtcNow
    Invoke-ProbeBuild '08-error-recovery'
    $result = [pscustomobject]@{
        visualStudio=$VisualStudio; installationPath=$tools.Root; toolset=$toolset; mode=$mode; compiler=$tools.Compiler
        checks=@('full-build','no-op-preserves-object','source-invalidates-object','header-invalidates-object','clean-removes-object','rebuild','executable-exit-zero','header-in-read-tlog','compiler-error-propagation','error-recovery','unicode-space-paths')
        measurements=$measurements.ToArray()
        limitations=@('Local execution only; no remote execution.', 'Elapsed wall time only; CPU, peak process-family memory and agent token usage are not measured.', 'Tiny fixture timings do not establish acceleration.')
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8
    $result | ConvertTo-Json -Depth 6
} finally {
    [Environment]::SetEnvironmentVariable('DBE_REAL_CL', $previousCompiler, 'Process')
}

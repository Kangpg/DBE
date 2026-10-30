[CmdletBinding()]
param([ValidateSet('2022', '2026')][string]$VisualStudio = '2022', [ValidateRange(2, 20)][int]$Pairs = 5)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$root = Split-Path $PSScriptRoot -Parent
$built = & (Join-Path $PSScriptRoot 'Build-Wrapper.ps1') -VisualStudio $VisualStudio
$project = Join-Path $root 'native\dbe-cl.vcxproj'
$tools = Get-DbeToolchain $VisualStudio $project
$run = Join-Path $root ('out\measurement-' + $VisualStudio + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$log = Join-Path $run 'measure-build.log'
& $tools.MSBuild $project /nologo /nr:false /m:1 /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$($tools.Toolset)" /p:PreferredToolArchitecture=x64 "/p:DBESource=$root\tests\measure-process.cpp" /p:TargetName=measure-process "/p:OutDir=$run\" "/p:IntDir=$run\obj\" /v:normal *> $log
if ($LASTEXITCODE -ne 0) { throw "Measurement helper build failed: $log" }
$source = (1..500 | ForEach-Object { "int function_$_(int x) { return x * $_ + $($_ + 1); }" }) -join "`n"
[IO.File]::WriteAllText((Join-Path $run 'sample.cpp'), $source)
$rows = [System.Collections.Generic.List[object]]::new()
for ($pair = 0; $pair -le $Pairs; ++$pair) {
    # Alternate ordering to reduce systematic warm-cache/order bias.
    $order = if ($pair % 2 -eq 0) { @('direct', 'wrapper') } else { @('wrapper', 'direct') }
    foreach ($mode in $order) {
        $report = Join-Path $run "$pair-$mode.json"
        $info = [Diagnostics.ProcessStartInfo]::new()
        $info.FileName = Join-Path $run 'measure-process.exe'
        $info.WorkingDirectory = $run
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.Environment['DBE_REAL_CL'] = $tools.Compiler
        $info.Environment['DBE_MEASURE_EXE'] = if ($mode -eq 'direct') { $tools.Compiler } else { $built.Wrapper }
        $info.Environment['DBE_MEASURE_ARGUMENTS'] = '/nologo /c /O2 /Z7 /Fooutput.obj sample.cpp'
        $info.Environment['DBE_MEASURE_REPORT'] = $report
        $process = [Diagnostics.Process]::Start($info)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit(40000)) { throw 'Measurement timed out.' }
            [Threading.Tasks.Task]::WaitAll(@($stdout, $stderr))
            if ($process.ExitCode -ne 0) { throw "Measurement failed: $($stderr.Result) (exit $($process.ExitCode))" }
        } finally {
            if (!$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
            $process.Dispose()
        }
        $sample = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if ($sample.cpuMs -le 0 -or $sample.peakFamilyCommitBytes -le 0) { throw 'Missing process-family accounting.' }
        $rows.Add([pscustomobject]@{ mode=$mode; pair=$pair; phase=$(if ($pair -eq 0) { 'first-pair-cache-uncontrolled' } else { 'warm-repeat' }); metrics=$sample })
    }
}
$result = [pscustomobject]@{ visualStudio=$VisualStudio; compiler=$tools.Compiler; workload='One real MSVC compile of 500 integer functions, /O2 /Z7'; measurements=$rows.ToArray(); limitations=@('Cold OS cache not measured; first pair is not a cold-cache claim.', 'Peak process-family committed bytes, not peak resident/working-set memory.', 'Agent tokens unavailable; not a distributed-build or end-to-end acceleration benchmark.') }
$result | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8
Write-Output (Join-Path $run 'result.json')

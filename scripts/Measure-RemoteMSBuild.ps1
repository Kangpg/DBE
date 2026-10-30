[CmdletBinding()]
param([Parameter(Mandatory)][string]$Project,[ValidateRange(2,5)][int]$Pairs=2)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$native=Join-Path $root 'native\dbe-cl.vcxproj'
$tools=Get-DbeToolchain '2022' $native
$run=Join-Path $root ('out\msbuild-measure-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
& $tools.MSBuild $native /nologo /nr:false /m:1 /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v143 "/p:DBESource=$root\tests\measure-process.cpp" /p:TargetName=measure-process "/p:OutDir=$run\" "/p:IntDir=$run\obj\" /v:normal *> (Join-Path $run 'build-helper.log')
if($LASTEXITCODE -ne 0) {throw 'Could not build measurement helper.'}
$rows=[Collections.Generic.List[object]]::new()
for($pair=0;$pair -lt $Pairs;$pair++) {
    $modes=if($pair % 2 -eq 0){@('native','remote')}else{@('remote','native')}
    foreach($mode in $modes) {
        foreach($target in @('Rebuild','Build')) {
            $name="$pair-$mode-$target"; $report=Join-Path $run ($name+'.json')
            $info=[Diagnostics.ProcessStartInfo]::new()
            $info.FileName=Join-Path $run 'measure-process.exe';$info.UseShellExecute=$false;$info.CreateNoWindow=$true
            $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
            $info.Environment['DBE_MEASURE_EXE']=$tools.MSBuild
            $enabled=if($mode -eq 'remote'){'true'}else{'false'}
            $info.Environment['DBE_MEASURE_ARGUMENTS']='"'+$Project+'" /nologo /nr:false /m:1 /t:'+ $target +' /p:Configuration=Release /p:Platform=x64 /p:DBEEnabled='+$enabled+' /v:normal'
            $info.Environment['DBE_MEASURE_REPORT']=$report
            $process=[Diagnostics.Process]::Start($info)
            try {
                $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
                if(!$process.WaitForExit(40000)) {throw 'Measured build timed out.'}
                [Threading.Tasks.Task]::WaitAll(@($stdout,$stderr))
                [IO.File]::WriteAllText((Join-Path $run ($name+'.log')),$stdout.Result+$stderr.Result)
                if($process.ExitCode -ne 0) {throw "Measurement failed: $name"}
            } finally {if(!$process.HasExited){$process.Kill();$process.WaitForExit(5000)|Out-Null};$process.Dispose()}
            $metrics=Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
            $rows.Add([pscustomobject]@{Pair=$pair;Mode=$mode;Target=$target;Phase=$(if($pair -eq 0){'first-pair-cache-uncontrolled'}else{'warm-repeat'});Metrics=$metrics})
        }
    }
}
[pscustomobject]@{Project=$Project;Rows=$rows.ToArray();Limitations=@('Initiator process-family CPU and peak commit only; remote Worker CPU/memory excluded.','Native and remote compiler patch versions differ.','Cold cache, resident-memory peak and agent tokens not measured.','Tiny fixture; not a production speedup claim.')} |
    ConvertTo-Json -Depth 8 | Tee-Object -FilePath (Join-Path $run 'measurement.json')

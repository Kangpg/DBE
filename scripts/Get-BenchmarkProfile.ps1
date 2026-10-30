[CmdletBinding()]
param([Parameter(Mandatory)][string]$Solution)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$solutionPath=(Resolve-Path -LiteralPath $Solution).Path
$solutionRoot=Split-Path $solutionPath -Parent
$solutionText=[IO.File]::ReadAllText($solutionPath)
$projects=[regex]::Matches($solutionText,'(?m)^Project\("[^"\r\n]+"\)\s*=\s*"(?<name>[^"\r\n]+)",\s*"(?<path>[^"\r\n]+\.vcxproj)"')
if(!$projects.Count){throw 'Expected a solution containing C++ projects.'}
$tools=Get-DbeToolchain '2022' (Join-Path $root 'native\dbe-cl.vcxproj')
$rows=[Collections.Generic.List[object]]::new()
$fields=@('PrecompiledHeader','PrecompiledHeaderFile','DebugInformationFormat','MultiProcessorCompilation','AdditionalOptions','WarningLevel','ExternalWarningLevel','LanguageStandard','CompileAs','ExcludedFromBuild')
foreach($project in $projects) {
    $path=[IO.Path]::GetFullPath((Join-Path $solutionRoot $project.Groups['path'].Value))
    foreach($configuration in @('Debug','Release')) {
        # Evaluation only: no target, code generation, build event, clean or compiler runs.
        $raw=& $tools.MSBuild $path /nologo /nr:false "/p:Configuration=$configuration" /p:Platform=x64 "/p:SolutionDir=$solutionRoot\" /getProperty:PlatformToolset,VCToolsInstallDir,OutDir,IntDir /getItem:ClCompile
        if($LASTEXITCODE -ne 0){throw "Evaluation failed for $path ($configuration): $($raw -join [Environment]::NewLine)"}
        $value=($raw -join [Environment]::NewLine)|ConvertFrom-Json
        $items=@($value.Items.ClCompile)
        $metadata=[ordered]@{}
        foreach($field in $fields){$metadata[$field]=@($items | ForEach-Object {$property=$_.PSObject.Properties[$field];if($null -ne $property){$property.Value}} | Sort-Object -Unique)}
        $rows.Add([pscustomobject]@{Name=$project.Groups['name'].Value;Project=$path;ProjectSha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;Configuration=$configuration;Properties=$value.Properties;CompileItemCount=$items.Count;Metadata=$metadata})
    }
}
$run=Join-Path $root ('out\benchmark-profile-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$report=Join-Path $run 'profile.json'
[pscustomobject]@{Solution=$solutionPath;SolutionSha256=(Get-FileHash -LiteralPath $solutionPath -Algorithm SHA256).Hash;CapturedAtUtc=[DateTime]::UtcNow.ToString('o');Projects=$rows.ToArray();Scope='MSBuild evaluation only; no timed builds or source edits. Compile item counts include items excluded by metadata; this is not remote eligibility proof.'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding UTF8
[pscustomobject]@{Report=$report;Projects=$projects.Count;Configurations=$rows.Count}

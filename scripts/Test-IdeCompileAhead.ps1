# Verifies whole-solution compile-ahead through the real IDE Build command. The script
# opens its own IDE, writes the marker the DBE extension would write, and registers only
# its own solution, so open IDEs and other solutions are not affected.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AdapterDirectory,[ValidateSet('2022','2026')][string]$VisualStudio='2026')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
$run=Join-Path $root ('out\ide-ahead-'+[guid]::NewGuid().ToString('N').Substring(0,10));New-Item -ItemType Directory $run|Out-Null
$solution=Join-Path $run 'ahead.sln'
$register=Join-Path $PSScriptRoot 'Set-NormalBuild.ps1'
$checks=[Collections.Generic.List[string]]::new()
function Project([string]$Name,[string]$Guid,[string]$Type,[string[]]$Sources,[string]$Reference){
    $directory=Join-Path $run $Name;New-Item -ItemType Directory $directory|Out-Null
    $items=($Sources|ForEach-Object {"<ClCompile Include=`"$_.cpp`" />"}) -join ''
    $refs=if($Reference){"<ItemGroup><ProjectReference Include=`"..\$Reference\$Reference.vcxproj`"><Project>{$script:libGuid}</Project></ProjectReference></ItemGroup>"}else{''}
    [IO.File]::WriteAllText((Join-Path $directory "$Name.vcxproj"),@"
<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup Label="ProjectConfigurations"><ProjectConfiguration Include="Debug|x64"><Configuration>Debug</Configuration><Platform>x64</Platform></ProjectConfiguration></ItemGroup>
  <PropertyGroup Label="Globals"><ProjectGuid>{$Guid}</ProjectGuid></PropertyGroup>
  <Import Project="`$(VCTargetsPath)\Microsoft.Cpp.Default.props" />
  <PropertyGroup Label="Configuration"><ConfigurationType>$Type</ConfigurationType><PlatformToolset>v143</PlatformToolset></PropertyGroup>
  <Import Project="`$(VCTargetsPath)\Microsoft.Cpp.props" />
  <PropertyGroup><OutDir>`$(SolutionDir)out\</OutDir><IntDir>`$(SolutionDir)obj\`$(ProjectName)\</IntDir></PropertyGroup>
  <ItemDefinitionGroup><ClCompile><DebugInformationFormat>ProgramDatabase</DebugInformationFormat><MultiProcessorCompilation>true</MultiProcessorCompilation><Optimization>Disabled</Optimization></ClCompile></ItemDefinitionGroup>
  <ItemGroup>$items</ItemGroup>
  $refs
  <Import Project="`$(VCTargetsPath)\Microsoft.Cpp.targets" />
</Project>
"@)
}
$script:libGuid='7A0E0F2C-6B7A-4B21-9C51-2F4B1D3E9A01';$appGuid='7A0E0F2C-6B7A-4B21-9C51-2F4B1D3E9A02'
Project 'lib' $script:libGuid 'StaticLibrary' @('slow') $null
$appSources=@('main')+@(1..8|ForEach-Object {"work$_"})
Project 'app' $appGuid 'Application' $appSources 'lib'
$ballast=(1..90000|ForEach-Object {"int slow_$_(){return $_;}"}) -join "`n"
[IO.File]::WriteAllText((Join-Path $run 'lib\slow.cpp'),"int lib_value(){return 40;}`n"+$ballast)
foreach($i in 1..8){[IO.File]::WriteAllText((Join-Path $run "app\work$i.cpp"),"int work$i(){return $i;}`n")}
[IO.File]::WriteAllText((Join-Path $run 'app\main.cpp'),"int lib_value();`nint main(){return lib_value()+2;}`n")
$cpp='8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942'
[IO.File]::WriteAllText($solution,@"
Microsoft Visual Studio Solution File, Format Version 12.00
Project("{$cpp}") = "lib", "lib\lib.vcxproj", "{$script:libGuid}"
EndProject
Project("{$cpp}") = "app", "app\app.vcxproj", "{$appGuid}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|x64 = Debug|x64
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{$script:libGuid}.Debug|x64.ActiveCfg = Debug|x64
		{$script:libGuid}.Debug|x64.Build.0 = Debug|x64
		{$appGuid}.Debug|x64.ActiveCfg = Debug|x64
		{$appGuid}.Debug|x64.Build.0 = Debug|x64
	EndGlobalSection
EndGlobal
"@)
$markers=Join-Path $env:LOCALAPPDATA 'DBE\ide-builds';New-Item -ItemType Directory $markers -Force|Out-Null
$sha=[Security.Cryptography.SHA256]::Create()
$key=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(([IO.Path]::GetFullPath($solution)).ToUpperInvariant()))) -replace '-','');$sha.Dispose()
$marker=Join-Path $markers $key
function Clear-Marker { Get-ChildItem $markers -Filter "$key*" -ErrorAction SilentlyContinue|Remove-Item -Force }
function Clean { foreach($n in 'out','obj'){$p=Join-Path $run $n;if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Recurse -Force}} }
function Stamp([string]$Name){(Get-Item -LiteralPath (Join-Path $run $Name)).LastWriteTimeUtc}
function Objects {@(Get-ChildItem (Join-Path $run 'obj') -Recurse -Filter *.obj|ForEach-Object {$_.FullName+'|'+$_.LastWriteTimeUtc.Ticks})}
$ide=$null
function Build([switch]$Whole,[switch]$ExpectFailure){
    Clear-Marker
    if($Whole){[IO.File]::WriteAllText($marker,([guid]::NewGuid().ToString('N')+' '+$ideProcess))}
    $watch=[Diagnostics.Stopwatch]::StartNew()
    # Poll rather than block, so a hang fails this test instead of stalling it forever.
    $ide.Solution.SolutionBuild.Build($false)
    $settled=0
    while($settled -lt 3){
        if($watch.Elapsed.TotalSeconds -gt 300){throw "The IDE build did not finish within 300 seconds ($run)"}
        Start-Sleep -Milliseconds 200
        try { if($ide.Solution.SolutionBuild.BuildState -eq 3){$settled++}else{$settled=0} } catch [Runtime.InteropServices.COMException] { $settled=0 }
    }
    $watch.Stop()
    Clear-Marker
    if(($ide.Solution.SolutionBuild.LastBuildInfo -ne 0) -ne $ExpectFailure.IsPresent){throw "Unexpected IDE build result ($run)"}
    if(!$ExpectFailure){& (Join-Path $run 'out\app.exe');if($LASTEXITCODE -ne 42){throw 'Wrong linked result'}}
    $watch.Elapsed.TotalSeconds
}
try {
    & $register -Solution $solution -AdapterDirectory $AdapterDirectory
    $version=if($VisualStudio -eq '2022'){'17.0'}else{'18.0'}
    $before=@(Get-Process devenv -ErrorAction SilentlyContinue|ForEach-Object Id)
    $ide=[Activator]::CreateInstance([type]::GetTypeFromProgID("VisualStudio.DTE.$version",$true))
    $ideProcess=@(Get-Process devenv|Where-Object {$_.Id -notin $before}|ForEach-Object Id)[0]
    if(!$ideProcess){throw 'The test IDE process was not identified'}
    $ide.SuppressUI=$true;$ide.Solution.Open($solution);$ide.Solution.SolutionBuild.SolutionConfigurations.Item('Debug').Activate()

    # No marker means VS's plain order: the application compiles after the library.
    $plain=Build
    $appFirst=@(Get-ChildItem (Join-Path $run 'obj\app') -Filter *.obj|Sort-Object LastWriteTimeUtc)[0].LastWriteTimeUtc
    if($appFirst -lt (Stamp 'obj\lib\slow.obj')){throw 'Compile-ahead ran without the whole-solution marker'}
    $checks.Add("no-marker-keeps-the-plain-order (${plain}s)")

    Clean
    $ahead=Build -Whole
    $appFirst=@(Get-ChildItem (Join-Path $run 'obj\app') -Filter *.obj|Sort-Object LastWriteTimeUtc)[0].LastWriteTimeUtc
    if($appFirst -ge (Stamp 'obj\lib\slow.obj')){throw "The application did not compile while the library compiled ($run)"}
    $checks.Add("whole-solution-marker-overlaps-project-compiles-link-and-run (${ahead}s)")

    $stamps=@(Objects)
    $null=Build -Whole
    if(Compare-Object $stamps @(Objects)){throw 'A repeated whole-solution Build rewrote objects'}
    $checks.Add('repeated-whole-solution-build-compiles-nothing')

    $source=Join-Path $run 'app\work1.cpp';$original=[IO.File]::ReadAllText($source)
    [IO.File]::AppendAllText($source,"`n#error DBE_AHEAD_ERROR`nint undeclared_use(){return dbe_undeclared_name;}`n")
    $null=Build -Whole -ExpectFailure
    $log=[IO.File]::ReadAllText((Join-Path $run 'obj\app\app.log'))
    if($log -notmatch 'DBE_AHEAD_ERROR' -or $log -notmatch 'C1189'){throw 'The real compile error was not reported'}
    if(@([regex]::Matches($log,'C1189')).Count -ne 1){throw 'The deferred attempt repeated the compile error'}
    if(Test-Path -LiteralPath (Join-Path $run 'obj\app\work1.obj')){throw 'The failed compile kept an object'}
    $checks.Add('real-error-is-reported-by-the-ordinary-build')
    [IO.File]::WriteAllText($source,$original)
    $null=Build -Whole
    $checks.Add('recovery-after-error')
    $result=[pscustomobject]@{Run=$run;Checks=$checks.ToArray()}
    $result|ConvertTo-Json|Set-Content (Join-Path $run 'checks.json');$result
} finally {
    # Only the IDE process this script identified at creation may be stopped.
    try { if($ide){$ide.Quit()} } catch [Runtime.InteropServices.COMException] { if($ideProcess){Stop-Process -Id $ideProcess -Force -ErrorAction SilentlyContinue} }
    finally { & $register -Solution $solution -Disable; Clear-Marker }
}

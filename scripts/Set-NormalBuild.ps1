# Registers one trusted solution in the current user's MSBuild import directory.
# Run with PowerShell 7. No edits to the solution, project, VS install or PATH.
[CmdletBinding(DefaultParameterSetName='Enable')]
param(
    [Parameter(Mandatory)][string]$Solution,
    [Parameter(Mandatory,ParameterSetName='Enable')][string]$AdapterDirectory,
    [Parameter(Mandatory,ParameterSetName='Disable')][switch]$Disable,
    [string]$UserExtensionsPath = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft\MSBuild')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Digest([string]$Value) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))) -replace '-','').ToLowerInvariant() } finally { $sha.Dispose() }
}
function Literal([string]$Value) {
    # Values enter both an MSBuild expression and an XML attribute. Reject
    # expression syntax instead of ever evaluating it as user-supplied code.
    if ($Value -match '''|[%;*?]|[$@]\(') { throw 'Path contains unsupported MSBuild expression characters.' }
    [Security.SecurityElement]::Escape($Value)
}
$solutionPath=[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($Solution)){$Solution}else{Join-Path $PWD.Path $Solution}))
if ([IO.Path]::GetExtension($solutionPath) -ne '.sln') { throw 'Expected a .sln file.' }
$id=Digest $solutionPath.ToUpperInvariant()
$parent=Join-Path $UserExtensionsPath 'Current\Microsoft.Common.targets'
$imports=Join-Path $parent 'ImportAfter'
$file=Join-Path $imports ("DBE.NormalBuild.$id.targets")
$marker="<!-- DBE.NormalBuild.v1 $id "
if (!$Disable) {
    if (!(Test-Path -LiteralPath $solutionPath -PathType Leaf)) { throw 'Solution does not exist.' }
    $adapter=(Resolve-Path -LiteralPath $AdapterDirectory).Path
    $config=Get-Content (Join-Path $adapter 'msbuild.json') -Raw | ConvertFrom-Json
    if ($config.mode -notin 'Hybrid','Local') { throw 'Select a Local or Hybrid adapter.' }
    foreach ($name in 'DBE.Remote.targets','dbe-remote-cl.exe','compiler.props','DbeInputInventory.cs') {
        if (!(Test-Path -LiteralPath (Join-Path $adapter $name) -PathType Leaf)) { throw "Incomplete adapter: $name" }
    }
    if (!(Test-Path -LiteralPath $config.realCompiler -PathType Leaf)) { throw 'Configured compiler is missing.' }
    if ($config.mode -eq 'Local' -and [IO.File]::ReadAllText((Join-Path $adapter 'dbe-remote-cl.compiler')) -ne $config.realCompiler) {
        throw 'Native launcher and configured compiler differ.'
    }
    $template=@'
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup Condition="'$(MSBuildProjectExtension)' == '.vcxproj' and '$(BuildingInsideVisualStudio)' == 'true' and '$(DesignTimeBuild)' != 'true' and '$(SolutionPath)' == 'SOLUTION' and '$(Platform)' == 'x64' and '$(PlatformToolset)' == 'v143' and '$(DBEEnabled)' == '' and '$(ForceImportAfterCppTargets)' == '' and '$(CLToolPath)' == '' and ('$(CLToolExe)' == '' or '$(CLToolExe)' == 'cl.exe') and $(ExecutablePath.StartsWith('COMPILERDIR;',StringComparison.OrdinalIgnoreCase)) and Exists('ADAPTER\DBE.Remote.targets')">
    <DBEEnabled>true</DBEEnabled>
    <DBEAdapterDirectory>ADAPTER</DBEAdapterDirectory>
    <ForceImportAfterCppTargets>ADAPTER\DBE.Remote.targets</ForceImportAfterCppTargets>
  </PropertyGroup>
</Project>
'@
    # Single pass substitution: inserted paths may themselves contain a token.
    $tokens=@{SOLUTION=(Literal $solutionPath);ADAPTER=(Literal $adapter);COMPILERDIR=(Literal (Split-Path $config.realCompiler -Parent))}
    $payload=[regex]::Replace($template,'SOLUTION|ADAPTER|COMPILERDIR',{param($m) $tokens[$m.Value]})
    $contents=$marker+(Digest $payload)+" -->"+[char]10+$payload
}
$mutex=[Threading.Mutex]::new($false,"Local\DBE.NormalBuild.$id")
$held=$false
try {
    try { $held=$mutex.WaitOne(10000) } catch [Threading.AbandonedMutexException] { $held=$true }
    if (!$held) { throw 'Another registration is in progress.' }
    $exists=[IO.File]::Exists($file)
    if ($exists) {
        $old=[IO.File]::ReadAllText($file)
        $parts=$old.Split([char[]]@([char]10),2)
        if ($parts.Count -ne 2 -or $parts[0] -cne ($marker+(Digest $parts[1])+' -->')) {
            throw 'Existing registration is foreign or modified. Inspect it before updating.'
        }
        if (!$Disable -and $contents -ceq $old) { return }
        $backup=Join-Path $parent ('DBE.backup.'+[guid]::NewGuid().ToString('N'))
    }
    if ($Disable) {
        if ($exists) { [IO.File]::Move($file,$backup) }
    } else {
        [void][IO.Directory]::CreateDirectory($imports)
        # ImportAfter loads every file, so create temporary files and backups outside it.
        $temp=Join-Path $parent ('DBE.pending.'+[guid]::NewGuid().ToString('N'))
        try {
            [IO.File]::WriteAllText($temp,$contents,[Text.UTF8Encoding]::new($false))
            if ($exists) { [IO.File]::Replace($temp,$file,$backup) } else { [IO.File]::Move($temp,$file) }
        } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
    }
} finally { if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }

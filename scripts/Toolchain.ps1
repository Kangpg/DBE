function Get-DbeToolchain([string]$VisualStudio, [string]$Project) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $range = if ($VisualStudio -eq '2022') { '[17.0,18.0)' } else { '[18.0,19.0)' }
    $vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -version $range -property installationPath
    if ($LASTEXITCODE -ne 0 -or !$vsRoot) { throw "Visual Studio $VisualStudio with C++ tools was not found." }
    $msbuild = Join-Path $vsRoot 'MSBuild\Current\Bin\MSBuild.exe'
    $toolset = if ($VisualStudio -eq '2022') { 'v143' } else { 'v145' }
    $toolDirectory = & $msbuild $Project /nologo /nr:false /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$toolset" /p:PreferredToolArchitecture=x64 /getProperty:VCToolsInstallDir
    if ($LASTEXITCODE -ne 0 -or !$toolDirectory) { throw 'Could not evaluate selected MSVC toolchain.' }
    $compiler = Join-Path ([string]$toolDirectory).Trim() 'bin\Hostx64\x64\cl.exe'
    if (!(Test-Path -LiteralPath $compiler)) { throw "Selected compiler does not exist: $compiler" }
    [pscustomobject]@{ Root=$vsRoot; MSBuild=$msbuild; Toolset=$toolset; Compiler=$compiler }
}

[CmdletBinding()]
param()
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$root=Split-Path $PSScriptRoot -Parent
$tools=Get-DbeToolchain '2022' (Join-Path $root 'native/dbe-cl.vcxproj')
$output=Join-Path $root 'out/vsix-build'
New-Item -ItemType Directory -Path $output -Force|Out-Null
$nativeLog=Join-Path $output 'host-build.log'
& $tools.MSBuild (Join-Path $root 'native/dbe-cl.vcxproj') /nologo /nr:false /p:Configuration=Release /p:Platform=x64 /p:PreferredToolArchitecture=x64 "/p:DBESource=$root\native\build-host.cpp" /p:TargetName=dbe-build-host "/p:OutDir=$output\" "/p:IntDir=$output\host-obj\" /v:normal *> $nativeLog
if($LASTEXITCODE -ne 0){Get-Content $nativeLog -Tail 20;throw 'Build host failed'}
$log=Join-Path $output 'extension-build.log'
& $tools.MSBuild (Join-Path $root 'vsix/Dbe.VisualStudio.csproj') /nologo /nr:false /p:Configuration=Release "/p:VSInstallDir=$($tools.Root)\" "/p:DBEBuildHostPath=$output\dbe-build-host.exe" /v:normal *> $log
if($LASTEXITCODE -ne 0){Get-Content $log -Tail 30;throw 'VSIX build failed'}
$sourceManifest=[xml](Get-Content -Raw (Join-Path $root 'vsix/source.extension.vsixmanifest'))
$archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $output 'Dbe.VisualStudio.vsix'))
try {
    $entry=$archive.GetEntry('extension.vsixmanifest')
    if(!$entry){throw 'VSIX is missing its manifest'}
    $reader=[IO.StreamReader]::new($entry.Open())
    try {$packagedManifest=[xml]$reader.ReadToEnd()} finally {$reader.Dispose()}
    if($packagedManifest.PackageManifest.Metadata.Identity.Version -ne $sourceManifest.PackageManifest.Metadata.Identity.Version){throw 'Packaged VSIX version differs from source'}
    if($packagedManifest.PackageManifest.Installation.OuterXml -ne $sourceManifest.PackageManifest.Installation.OuterXml){throw 'Packaged VSIX compatibility differs from source'}
    if(!$archive.GetEntry('Dbe.VisualStudio.dll') -or !$archive.GetEntry('dbe-build-host.exe')){throw 'VSIX is missing required runtime files'}
} finally {$archive.Dispose()}
$assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $output 'Dbe.VisualStudio.dll')))
$resource=$assembly.GetManifestResourceStream('VSPackage.resources')
if(!$resource){throw 'VSIX assembly is missing VSPackage.resources'}
$resourceReader=[Resources.ResourceReader]::new($resource)
try {
    $resourceType=$null;$resourceData=$null
    $resourceReader.GetResourceData('Menus.ctmenu',[ref]$resourceType,[ref]$resourceData)
    if(!$resourceData -or $resourceData.Length -eq 0){throw 'VSIX command table is empty'}
} finally {$resourceReader.Dispose()}
[pscustomobject]@{Package=(Join-Path $output 'Dbe.VisualStudio.vsix');Directory=$output;Log=$log}

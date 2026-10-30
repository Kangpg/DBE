[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$ClientConfig,
    [string]$PublishDirectory,
    [ValidateSet('Local','Auto','Distributed','Hybrid')][string]$Mode='Auto',
    [string]$HostPolicy,
    [string]$StateDirectory='state',
    [string[]]$AdditionalClients=@(),
    [switch]$AllowCompilerMismatch
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$tools=Get-DbeToolchain '2022' (Join-Path $root 'native\dbe-cl.vcxproj')
if(Test-Path -LiteralPath $Destination) { throw 'Adapter destination must be new.' }
if($Mode -eq 'Hybrid' -and !$HostPolicy){throw 'Hybrid mode requires the shared HostPolicy file.'}
if(!$PublishDirectory) { $PublishDirectory=(& (Join-Path $PSScriptRoot 'Build-Worker.ps1') -SelfContained).Directory }
New-Item -ItemType Directory -Path $Destination | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $Destination -Recurse
Copy-Item -LiteralPath (Join-Path $Destination 'dbe-worker.exe') -Destination (Join-Path $Destination 'dbe-remote-cl.exe')
if($Mode -eq 'Local'){
    $wrapper=& (Join-Path $PSScriptRoot 'Build-Wrapper.ps1') -VisualStudio 2022
    Copy-Item -LiteralPath $wrapper.Wrapper -Destination (Join-Path $Destination 'dbe-remote-cl.exe') -Force
    [IO.File]::WriteAllText((Join-Path $Destination 'dbe-remote-cl.compiler'),$tools.Compiler,[Text.Encoding]::Unicode)
}
if($Mode -eq 'Hybrid'){
    foreach($tool in 'link','lib'){Copy-Item -LiteralPath (Join-Path $Destination 'dbe-worker.exe') -Destination (Join-Path $Destination "dbe-$tool.exe")}
}
$state=if([IO.Path]::IsPathRooted($StateDirectory)){[IO.Path]::GetFullPath($StateDirectory)}else{[IO.Path]::GetFullPath((Join-Path $Destination $StateDirectory))}
$config=[ordered]@{realCompiler=$tools.Compiler;clientConfig=(Resolve-Path -LiteralPath $ClientConfig).Path;stateDirectory=$state;mode=$Mode;allowCompilerMismatch=$AllowCompilerMismatch.IsPresent}
if($HostPolicy){$config.hostPolicy=(Resolve-Path -LiteralPath $HostPolicy).Path}
if($AdditionalClients.Count){
    if($Mode -ne 'Hybrid'){throw 'Multiple Workers require Hybrid mode'}
    $config.additionalClients=@($AdditionalClients|ForEach-Object {(Resolve-Path -LiteralPath $_).Path})
}
$config |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'msbuild.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $root 'msbuild\DBE.Remote.targets') -Destination $Destination
Copy-Item -LiteralPath (Join-Path $root 'msbuild\DbeInputInventory.cs') -Destination $Destination
@{msbuild=$tools.MSBuild;compiler=$tools.Compiler}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Destination 'build-tools.json') -Encoding UTF8
$compilerProps=[xml]'<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><DBEConfiguredCompiler /></PropertyGroup></Project>'
$compilerProps.Project.PropertyGroup.DBEConfiguredCompiler=$tools.Compiler
foreach($entry in @{DBEStateDirectory=$state;DBELeaseDirectory=$(if($HostPolicy){Join-Path (Split-Path (Resolve-Path $HostPolicy).Path -Parent) 'leases'}else{Join-Path (Resolve-Path $Destination).Path 'unused-leases'})}.GetEnumerator()){
    $element=$compilerProps.CreateElement($entry.Key,$compilerProps.DocumentElement.NamespaceURI);$element.InnerText=$entry.Value;[void]$compilerProps.Project.PropertyGroup.AppendChild($element)
}
$runtimeDirs=@(@($ClientConfig)+$AdditionalClients|ForEach-Object {Split-Path (Resolve-Path -LiteralPath $_).Path -Parent})
if($HostPolicy){$runtimeDirs+=Split-Path (Resolve-Path -LiteralPath $HostPolicy).Path -Parent}
$element=$compilerProps.CreateElement('DBERuntimeDirectories',$compilerProps.DocumentElement.NamespaceURI);$element.InnerText=($runtimeDirs|Sort-Object -Unique)-join ';';[void]$compilerProps.Project.PropertyGroup.AppendChild($element)
if($Mode -eq 'Hybrid'){$element=$compilerProps.CreateElement('DBEHybrid',$compilerProps.DocumentElement.NamespaceURI);$element.InnerText='true';$compilerProps.Project.PropertyGroup.AppendChild($element)|Out-Null}
if($Mode -eq 'Local'){$element=$compilerProps.CreateElement('DBELocal',$compilerProps.DocumentElement.NamespaceURI);$element.InnerText='true';$compilerProps.Project.PropertyGroup.AppendChild($element)|Out-Null}
$compilerProps.Save((Join-Path $Destination 'compiler.props'))
[pscustomobject]@{AdapterDirectory=(Resolve-Path -LiteralPath $Destination).Path;Targets=(Join-Path (Resolve-Path -LiteralPath $Destination).Path 'DBE.Remote.targets');Compiler=$tools.Compiler}

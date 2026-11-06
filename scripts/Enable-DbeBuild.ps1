# Connects every matching MSBuild C++ project of this Windows user to DBE, with no
# per-solution step. Without -Apply it only prints the plan and writes nothing.
# Works in Windows PowerShell 5.1 and PowerShell 7. No edits to projects, VS or PATH.
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$Disable,
    [switch]$IdeOnly,
    [string]$PublishDirectory,
    [string[]]$ClientConfig=@(),
    [string]$ImportConnection,
    [string]$DataRoot,
    [string]$UserExtensionsPath
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
if(!$DataRoot){$DataRoot=Join-Path $env:LOCALAPPDATA 'DBE'}
if(!$UserExtensionsPath){$UserExtensionsPath=Join-Path $env:LOCALAPPDATA 'Microsoft\MSBuild'}
$parent=Join-Path $UserExtensionsPath 'Current\Microsoft.Common.targets'
$imports=Join-Path $parent 'ImportAfter'
$marker='<!-- DBE.AllProjects.v1 '
function Digest([string]$Value){
    $sha=[Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))) -replace '-','').ToLowerInvariant() } finally { $sha.Dispose() }
}
function Literal([string]$Value){
    # Values enter both an MSBuild expression and an XML attribute. Reject expression
    # syntax instead of ever evaluating it as user-supplied code.
    if($Value -match '''|[%;*?]|[$@]\('){throw "Path contains unsupported MSBuild expression characters: $Value"}
    [Security.SecurityElement]::Escape($Value)
}
function Existing { if(Test-Path -LiteralPath $imports){@(Get-ChildItem -LiteralPath $imports -Filter 'DBE.AllProjects.*.targets')}else{@()} }
function Backup([string]$Path){ Join-Path $parent ('DBE.backup.'+[guid]::NewGuid().ToString('N')+'.'+[IO.Path]::GetFileName($Path)) }
function Assert-Owned([string]$Path){
    $text=[IO.File]::ReadAllText($Path)
    $parts=$text.Split([char[]]@([char]10),2)
    if($parts.Count -ne 2 -or $parts[0] -cne ($marker+(Digest $parts[1])+' -->')){throw "Existing registration is foreign or modified; inspect it before updating: $Path"}
    $text
}

if($Disable){
    $files=@(Existing)
    if(!$Apply){return [pscustomobject]@{Applied=$false;WouldDisable=@($files|ForEach-Object FullName)}}
    foreach($file in $files){ $null=Assert-Owned $file.FullName; [IO.File]::Move($file.FullName,(Backup $file.FullName)) }
    return [pscustomobject]@{Applied=$true;Disabled=@($files|ForEach-Object FullName)}
}

if(!$PublishDirectory){$PublishDirectory=Join-Path $root 'out\worker-build\portable'}
$runtime=(Resolve-Path -LiteralPath $PublishDirectory).Path
. (Join-Path $PSScriptRoot 'WorkerRuntime.ps1')
$exe=Join-Path $runtime 'dbe-worker.exe'
if(!(Test-Path -LiteralPath $exe)){throw "Runtime is missing: $exe"}
$version=[string](& $exe --version)

# Each Worker reports its own compiler identity. A Worker that cannot answer now is
# left out and reported, so running this script again adds it after a restart.
$connections=Join-Path $DataRoot 'connections'
# A connection may live where an IDE cannot see it, such as a packaged app's private
# profile. Copy only what a connection needs, keep the source, and never overwrite.
$imported=$null
if($ImportConnection){
    $source=(Resolve-Path -LiteralPath $ImportConnection).Path
    if(!(Test-Path -LiteralPath (Join-Path $source 'identity.pfx')) -or !(Test-Path -LiteralPath (Join-Path $source 'workers'))){throw "Not a connection directory: $source"}
    $imported=Join-Path $connections (Split-Path $source -Leaf)
    $parts=@(Get-ChildItem -LiteralPath $source -File -Filter 'identity.pfx')+@(Get-ChildItem -LiteralPath $source -File -Filter 'requester.*')+@(Get-ChildItem -LiteralPath (Join-Path $source 'workers') -Recurse -File -Filter 'client.json')
    if($Apply){
        foreach($part in $parts){
            $target=Join-Path $imported $part.FullName.Substring($source.Length+1)
            if(Test-Path -LiteralPath $target){
                if((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $part.FullName).Hash){throw "A different connection file already exists: $target"}
                continue
            }
            [void][IO.Directory]::CreateDirectory((Split-Path $target -Parent))
            Copy-Item -LiteralPath $part.FullName -Destination $target
        }
    }
}
$candidates=if($ClientConfig.Count){$ClientConfig}elseif($ImportConnection -and !$Apply){
    @(Get-ChildItem -LiteralPath (Join-Path $source 'workers') -Recurse -File -Filter client.json|ForEach-Object FullName)
}elseif(Test-Path -LiteralPath $connections){@(Get-ChildItem -LiteralPath $connections -Recurse -Filter client.json|ForEach-Object FullName)}else{@()}
function Query([string]$Program,[string[]]$Arguments){
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=$Program;$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.Arguments=($Arguments|ForEach-Object {'"'+$_+'"'}) -join ' '
    $process=[Diagnostics.Process]::Start($start)
    try {
        $error_=$process.StandardError.ReadToEndAsync();$output=$process.StandardOutput.ReadToEndAsync()
        if(!$process.WaitForExit(30000)){$process.Kill();return [pscustomobject]@{Ok=$false;Text='timed out'}}
        [pscustomobject]@{Ok=($process.ExitCode -eq 0);Text=$(if($process.ExitCode -eq 0){$output.Result}else{$error_.Result.Trim()})}
    } finally {$process.Dispose()}
}
$workers=@(foreach($path in $candidates){
    $full=(Resolve-Path -LiteralPath $path).Path
    $reply=Query $exe @('info',$full,'7')
    if($reply.Ok){$info=$reply.Text|ConvertFrom-Json;[pscustomobject]@{Client=$full;Machine=$info.machine;Toolchain=$info.toolchain;Unavailable=$null}}
    else{[pscustomobject]@{Client=$full;Machine=$null;Toolchain=$null;Unavailable=$reply.Text}}
})
$ready=@($workers|Where-Object {!$_.Unavailable})

# Every installed Hostx64/x64 compiler is considered. A project selecting any other
# compiler or platform never matches the registration and stays a plain native build.
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installations=if(Test-Path -LiteralPath $vswhere){@(& $vswhere -all -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)}else{@()}
$compilers=@(foreach($installation in $installations){
    $tools=Join-Path $installation 'VC\Tools\MSVC'
    if(!(Test-Path -LiteralPath $tools)){continue}
    foreach($directory in Get-ChildItem -LiteralPath $tools -Directory){
        $compiler=Join-Path $directory.FullName 'bin\Hostx64\x64\cl.exe'
        if(!(Test-Path -LiteralPath $compiler)){continue}
        $reply=Query $exe @('compiler-identity',$compiler)
        if(!$reply.Ok){[pscustomobject]@{Compiler=$compiler;Identity=$null;Version=$null;Workers=@()};continue}
        $identity=$reply.Text|ConvertFrom-Json
        [pscustomobject]@{Compiler=$compiler;Identity=$identity.identity;Version=$identity.version;Workers=@($ready|Where-Object {$_.Toolchain -eq $identity.identity})}
    }
})
$matched=@($compilers|Where-Object {@($_.Workers).Count})

function Registration([string]$Adapter,[string]$Compiler){
    $ide=if($IdeOnly){" and '`$(BuildingInsideVisualStudio)' == 'true'"}else{''}
    $template=@'
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup Condition="'$(MSBuildProjectExtension)' == '.vcxproj' and '$(DesignTimeBuild)' != 'true' and '$(DBE_DISABLE)' != '1' and '$(Platform)' == 'x64' and '$(DBEEnabled)' == '' and '$(ForceImportAfterCppTargets)' == '' and '$(CLToolPath)' == '' and ('$(CLToolExe)' == '' or '$(CLToolExe)' == 'cl.exe') and $(ExecutablePath.StartsWith('COMPILERDIR;',StringComparison.OrdinalIgnoreCase)) and Exists('ADAPTER\DBE.Remote.targets')IDEONLY">
    <DBEEnabled>true</DBEEnabled>
    <DBEAdapterDirectory>ADAPTER</DBEAdapterDirectory>
    <ForceImportAfterCppTargets>ADAPTER\DBE.Remote.targets</ForceImportAfterCppTargets>
  </PropertyGroup>
</Project>
'@
    # Single pass substitution: inserted paths may themselves contain a token.
    $tokens=@{ADAPTER=(Literal $Adapter);COMPILERDIR=(Literal (Split-Path $Compiler -Parent));IDEONLY=$ide}
    $payload=[regex]::Replace($template,'ADAPTER|COMPILERDIR|IDEONLY',{param($m) $tokens[$m.Value]})
    $marker+(Digest $payload)+' -->'+[char]10+$payload
}
$planned=@(foreach($entry in $matched){
    $clients=@($entry.Workers|ForEach-Object Client|Sort-Object)
    $id=(Digest (($entry.Compiler.ToUpperInvariant())+"`n"+$version+"`n"+($clients -join "`n"))).Substring(0,12)
    $compilerId=(Digest $entry.Compiler.ToUpperInvariant()).Substring(0,12)
    [pscustomobject]@{Compiler=$entry.Compiler;Version=$entry.Version;Workers=@($entry.Workers|ForEach-Object Machine);Clients=$clients;
        Adapter=(Join-Path $DataRoot "adapters\$version-$id");Registration=(Join-Path $imports "DBE.AllProjects.$compilerId.targets");State=(Join-Path $DataRoot "s\$id")}
})
$summary=[pscustomobject]@{
    Applied=[bool]$Apply;Runtime=$version;IdeOnly=[bool]$IdeOnly
    ImportedConnection=$imported
    Connected=$planned
    NativeCompilers=@($compilers|Where-Object {!@($_.Workers).Count}|ForEach-Object Compiler)
    UnavailableWorkers=@($workers|Where-Object {$_.Unavailable}|ForEach-Object {$_.Client+': '+$_.Unavailable})
    Note='Matching x64 MSBuild C++ projects use DBE; every other project stays a native build. Set DBE_DISABLE=1 to opt a build out.'
}
if(!$Apply){return $summary}
if(!$planned.Count){throw 'No installed compiler matches a reachable Worker; nothing was changed.'}

$policyDirectory=Join-Path $DataRoot 'policies\default'
$policy=Join-Path $policyDirectory 'host.json'
if(!(Test-Path -LiteralPath $policy)){
    New-Item -ItemType Directory $policyDirectory -Force|Out-Null
    $cores=[Environment]::ProcessorCount
    [IO.File]::WriteAllText($policy,(@{localSlots=$cores;remoteSlots=2;localMemoryMiB=3072;reserveMemoryMiB=2048;waitSeconds=1200;cpuBusyPercent=100;linkMemoryMiB=5120;localBatchSlots=$cores;preprocessMemoryMiB=512;localBatchMemoryMiB=4096;preserveLocalParallelism=$true;nativeLocalJobs=4}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
}
foreach($item in $planned){
    # A new adapter path never overwrites a runtime an active build may be using.
    if(!(Test-Path -LiteralPath $item.Adapter)){
        $additional=@($item.Clients|Select-Object -Skip 1)
        $null=& (Join-Path $PSScriptRoot 'New-MSBuildAdapter.ps1') -Compiler $item.Compiler -Mode Hybrid -NativeInputs -NativeAutoScope -NativeBatchFiles 4 -PublishDirectory $runtime -Destination $item.Adapter -ClientConfig $item.Clients[0] -AdditionalClients $additional -HostPolicy $policy -StateDirectory $item.State
    }
    $contents=Registration $item.Adapter $item.Compiler
    [void][IO.Directory]::CreateDirectory($imports)
    if([IO.File]::Exists($item.Registration)){
        if((Assert-Owned $item.Registration) -ceq $contents){continue}
        $backup=Backup $item.Registration
    } else {$backup=$null}
    # ImportAfter loads every file, so temporary files and backups stay outside it.
    $temp=Join-Path $parent ('DBE.pending.'+[guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($temp,$contents,[Text.UTF8Encoding]::new($false))
        if($backup){[IO.File]::Replace($temp,$item.Registration,$backup)}else{[IO.File]::Move($temp,$item.Registration)}
    } finally { if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)} }
}
$summary

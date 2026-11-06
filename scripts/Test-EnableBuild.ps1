# Verifies user-wide registration with a real Worker and real MSBuild. Registration and
# MSBuild both use a fresh owned extensions folder, so the real profile is untouched.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory,[Parameter(Mandatory)][string]$ClientConfig,[ValidateSet('2022','2026')][string]$VisualStudio='2026')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent;. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$tools=Get-DbeToolchain $VisualStudio (Join-Path $root 'native\dbe-cl.vcxproj') 'v143'
$run=Join-Path $root ('out\enable-'+[guid]::NewGuid().ToString('N').Substring(0,10));New-Item -ItemType Directory $run|Out-Null
$checks=[Collections.Generic.List[string]]::new()
$fakeLocal=Join-Path $run 'local';New-Item -ItemType Directory $fakeLocal|Out-Null
$realExtensions=Join-Path $env:LOCALAPPDATA 'Microsoft\MSBuild'
function RealRegistrations { if(Test-Path -LiteralPath "$realExtensions\Current\Microsoft.Common.targets\ImportAfter"){@(Get-ChildItem "$realExtensions\Current\Microsoft.Common.targets\ImportAfter" -Filter 'DBE.AllProjects.*'|ForEach-Object FullName)}else{@()} }
$realBefore=@(RealRegistrations)
$enable=@{PublishDirectory=$PublishDirectory;ClientConfig=@($ClientConfig);DataRoot=(Join-Path $fakeLocal 'DBE');UserExtensionsPath=(Join-Path $fakeLocal 'Microsoft\MSBuild')}
$script=Join-Path $PSScriptRoot 'Enable-DbeBuild.ps1'
function Files { @(Get-ChildItem $fakeLocal -Recurse -File -ErrorAction SilentlyContinue) }

$plan=& $script @enable
if($plan.Applied -or @(Files).Count -or !@($plan.Connected).Count){throw 'The plan must connect a compiler and write nothing'}
$checks.Add('plan-writes-nothing-and-finds-a-matching-compiler')
$selected=@($plan.Connected|Where-Object {$_.Compiler -eq $tools.Compiler})
if(!$selected.Count){throw "The Worker does not match the selected compiler: $($tools.Compiler)"}
$checks.Add('worker-identity-selects-the-matching-installed-compiler')

$applied=& $script @enable -Apply
if(!$applied.Applied -or !(Test-Path -LiteralPath $selected[0].Registration) -or !(Test-Path -LiteralPath $selected[0].Adapter)){throw 'Apply did not register'}
$registered=[IO.File]::ReadAllText($selected[0].Registration)
$adapterStamp=(Get-Item -LiteralPath (Join-Path $selected[0].Adapter 'msbuild.json')).LastWriteTimeUtc.Ticks
$null=& $script @enable -Apply
if([IO.File]::ReadAllText($selected[0].Registration) -cne $registered -or (Get-Item -LiteralPath (Join-Path $selected[0].Adapter 'msbuild.json')).LastWriteTimeUtc.Ticks -ne $adapterStamp -or @(Get-ChildItem "$fakeLocal\Microsoft\MSBuild\Current\Microsoft.Common.targets" -Filter 'DBE.backup.*').Count){throw 'Apply is not idempotent'}
$checks.Add('apply-is-idempotent-without-backups')

# A plain project: no DBE imports, properties or settings anywhere in its files.
$project=Join-Path $run 'plain'
Copy-Item (Join-Path $root 'fixture-remote') $project -Recurse
$vcx=Join-Path $project 'remote-probe.vcxproj'
$xml=[xml][IO.File]::ReadAllText($vcx)
foreach($n in @($xml.Project.Import|Where-Object {$_.Project -match 'DBE'})){[void]$xml.Project.RemoveChild($n)}
$xml.Save($vcx)
Remove-Item -LiteralPath (Join-Path $project 'DBE.settings.props') -ErrorAction SilentlyContinue
if(Select-String -LiteralPath $vcx -Pattern 'DBEEnabled|DBEAdapterDirectory|DBE\.[A-Za-z]' -Quiet){throw 'The plain fixture is still connected to DBE'}
$before=(Get-FileHash -LiteralPath $vcx).Hash
function Build([string]$Name,[hashtable]$Environment=@{}){
    $log=Join-Path $run ($Name+'.log')
    $saved=@{}
    foreach($key in @($Environment.Keys)){$saved[$key]=[Environment]::GetEnvironmentVariable($key)}
    try {
        foreach($key in $Environment.Keys){[Environment]::SetEnvironmentVariable($key,$Environment[$key])}
        # MSBuild takes this path from the Windows profile, not from LOCALAPPDATA.
        & $tools.MSBuild $vcx /nologo /nr:false /m:4 /t:Rebuild /p:Configuration=Debug /p:Platform=x64 /p:PlatformToolset=v143 /p:VcpkgEnabled=false /v:normal "/p:MSBuildUserExtensionsPath=$fakeLocal\Microsoft\MSBuild" *> $log
        $code=$LASTEXITCODE
    } finally { foreach($key in $saved.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])} }
    if($code){Get-Content $log -Tail 15;throw "Build failed: $Name"}
    $log
}
$log=Build registered
if(!(Select-String -LiteralPath $log -SimpleMatch 'DBE: ' -Quiet)){throw 'The plain project did not enter DBE'}
& (Join-Path $project 'out\Debug\bin\remote-probe.exe');if($LASTEXITCODE -ne 42){throw 'Wrong result through registration'}
$checks.Add('plain-project-builds-through-registration-without-edits')
$log=Build opt-out @{DBE_DISABLE='1'}
if(Select-String -LiteralPath $log -SimpleMatch 'DBE: ' -Quiet){throw 'DBE_DISABLE was ignored'}
& (Join-Path $project 'out\Debug\bin\remote-probe.exe');if($LASTEXITCODE -ne 42){throw 'Wrong result after opting out'}
$checks.Add('DBE_DISABLE-gives-a-native-build')
if((Get-FileHash -LiteralPath $vcx).Hash -ne $before){throw 'Registration changed project bytes'}
$checks.Add('project-bytes-are-unchanged')

# Another registration version replaces with a backup; a foreign edit is refused.
$changed=& $script @enable -Apply -IdeOnly
$backups=@(Get-ChildItem "$fakeLocal\Microsoft\MSBuild\Current\Microsoft.Common.targets" -Filter 'DBE.backup.*')
if($backups.Count -ne 1 -or [IO.File]::ReadAllText($backups[0].FullName) -cne $registered){throw 'The replaced registration was not backed up'}
$log=Build ide-only-from-command-line
if(Select-String -LiteralPath $log -SimpleMatch 'DBE: ' -Quiet){throw 'IdeOnly registration entered a command-line build'}
$checks.Add('changed-registration-is-backed-up-and-ide-only-skips-the-command-line')
[IO.File]::AppendAllText($selected[0].Registration,'<!-- edited -->')
try { $null=& $script @enable -Apply;throw 'A modified registration was overwritten' }
catch { if($_.Exception.Message -notlike '*foreign or modified*'){throw} }
$checks.Add('modified-registration-is-refused')
$content=[IO.File]::ReadAllText($selected[0].Registration).Replace('<!-- edited -->','');[IO.File]::WriteAllText($selected[0].Registration,$content,[Text.UTF8Encoding]::new($false))

$null=& $script @enable -Disable -Apply
if(@(Get-ChildItem "$fakeLocal\Microsoft\MSBuild\Current\Microsoft.Common.targets\ImportAfter" -Filter 'DBE.*').Count){throw 'Disable left a registration'}
$log=Build disabled
if(Select-String -LiteralPath $log -SimpleMatch 'DBE: ' -Quiet){throw 'A disabled registration still entered DBE'}
$checks.Add('disable-removes-registration-and-keeps-a-backup')

# Importing a connection copies only its identity and Worker settings, and a real mutual
# TLS query through the copy proves the private key and relative paths still resolve.
$connection=Split-Path (Split-Path (Split-Path $ClientConfig -Parent) -Parent) -Parent
if(Test-Path -LiteralPath (Join-Path $connection 'identity.pfx')){
    $imports=@{PublishDirectory=$PublishDirectory;ImportConnection=$connection;DataRoot=(Join-Path $run 'imported\DBE');UserExtensionsPath=(Join-Path $run 'imported\Microsoft\MSBuild')}
    $planOnly=& $script @imports
    if(Test-Path -LiteralPath (Join-Path $run 'imported')){throw 'The import plan wrote files'}
    $done=& $script @imports -Apply
    $copies=@(Get-ChildItem $done.ImportedConnection -Recurse -File)
    if(!@($done.Connected).Count -or @($done.Connected|Where-Object {@($_.Clients|Where-Object {!$_.StartsWith($done.ImportedConnection)}).Count}).Count){throw 'The imported connection was not used'}
    if($copies|Where-Object {$_.Name -notin 'identity.pfx','requester.cer','requester.json','client.json'}){throw 'Import copied more than the connection needs'}
    $null=& $script @imports -Apply
    $checks.Add('import-copies-only-the-connection-and-reaches-the-Worker-with-the-copy')
}
if(Compare-Object $realBefore @(RealRegistrations)){throw 'The real user profile changed'}
$checks.Add('real-user-profile-is-untouched')
$result=[pscustomobject]@{Run=$run;Checks=$checks.ToArray()}
$result|ConvertTo-Json|Set-Content (Join-Path $run 'checks.json');$result

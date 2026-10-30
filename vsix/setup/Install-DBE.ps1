[CmdletBinding()]
param([string]$InstanceId,[ValidatePattern('^[A-Za-z0-9]*$')][string]$RootSuffix='',[switch]$CheckOnly)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
try {
    $package=Join-Path $PSScriptRoot 'Dbe.VisualStudio.vsix'
    if(!(Test-Path -LiteralPath $package -PathType Leaf)){throw 'Dbe.VisualStudio.vsix must be beside this installer.'}
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if(!(Test-Path -LiteralPath $vswhere)){throw 'Visual Studio Installer was not found. Install Visual Studio 2022 or 2026 first.'}
    $discovery=[Diagnostics.ProcessStartInfo]::new($vswhere,'-products * -requires Microsoft.VisualStudio.Component.CoreEditor -version "[17.0,19.0)" -format json -utf8')
    $discovery.UseShellExecute=$false;$discovery.CreateNoWindow=$true;$discovery.RedirectStandardOutput=$true
    $discovery.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
    $query=[Diagnostics.Process]::Start($discovery)
    try {$json=$query.StandardOutput.ReadToEnd();$query.WaitForExit();if($query.ExitCode -ne 0){throw 'Visual Studio discovery failed.'}}
    finally {$query.Dispose()}
    $instances=ConvertFrom-Json -InputObject $json
    if($InstanceId){$instances=@($instances | Where-Object {$_.instanceId -eq $InstanceId})}
    $selected=$instances | Sort-Object {[version]$_.installationVersion} -Descending | Select-Object -First 1
    if(!$selected){throw 'No matching Visual Studio IDE was found.'}
    $ide=Join-Path $selected.installationPath 'Common7\IDE\devenv.exe'
    $installer=Join-Path $selected.installationPath 'Common7\IDE\VSIXInstaller.exe'
    if(!(Test-Path -LiteralPath $ide) -or !(Test-Path -LiteralPath $installer)){throw 'Selected Visual Studio is missing its IDE or VSIX installer.'}
    # Require every window of the selected installation to close. Never terminate one.
    $open=@(Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" | Where-Object {
        !$_.ExecutablePath -or [string]::Equals($_.ExecutablePath,$ide,[StringComparison]::OrdinalIgnoreCase)
    })
    $plan=[pscustomobject]@{VisualStudio=$selected.displayName;InstanceId=$selected.instanceId;RootSuffix=$RootSuffix;Package=$package;OpenWindows=$open.Count}
    $plan | Format-List | Out-Host
    if($CheckOnly){exit 0}
    if($open.Count){throw 'Save your work and close Visual Studio windows for this installation, then run Install-DBE.cmd again.'}
    $argsList=@('/quiet',('/instanceIds:'+$selected.instanceId))
    if($RootSuffix){$argsList+=('/rootSuffix:'+$RootSuffix)}
    $argsList+=('"'+$package+'"')
    $installed=Start-Process -FilePath $installer -ArgumentList $argsList -WindowStyle Hidden -PassThru -Wait
    if($installed.ExitCode -ne 0){throw "VSIX Installer returned $($installed.ExitCode). Installation was not confirmed."}
    $log=Join-Path $env:TEMP ('DBE-VS-configuration-'+[Guid]::NewGuid().ToString('N')+'.xml')
    $refreshArgs=@('/UpdateConfiguration','/Log',('"'+$log+'"'))
    if($RootSuffix){$refreshArgs+=@('/RootSuffix',$RootSuffix)}
    $refreshed=Start-Process -FilePath $ide -ArgumentList $refreshArgs -WindowStyle Hidden -PassThru -Wait
    if($refreshed.ExitCode -ne 0){throw "Visual Studio configuration refresh failed. Log: $log"}
    $activity=[xml][IO.File]::ReadAllText($log)
    if(@($activity.activity.entry | Where-Object {$_.description -match 'Going volatile|Configuration is Volatile'}).Count){throw "The configuration cache was still in use. Close Visual Studio and rerun setup. Log: $log"}
    if(!@($activity.activity.entry | Where-Object {$pathNode=$_.SelectSingleNode('path');$pathNode -and $pathNode.InnerText -match '[\\/]dbe\.visualstudio\.pkgdef$'}).Count){throw "DBE package registration was not observed. Log: $log"}
    Write-Host 'DBE extension installed. Open Visual Studio, then Extensions > DBE.'
    Write-Host 'Keep the existing solution.dbe.json and Worker pairing. This installer does not change normal Build.'
    Write-Host "Configuration log: $log"
    exit 0
} catch {Write-Host ('ERROR: '+$_.Exception.Message) -ForegroundColor Red;exit 1}

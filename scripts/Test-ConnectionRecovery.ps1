[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory,[string]$Compiler,[string]$PackagePublishDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$root=Split-Path $PSScriptRoot -Parent
$publish=(Resolve-Path -LiteralPath $PublishDirectory).ProviderPath;$exe=Join-Path $publish 'dbe-worker.exe'
if(!$Compiler){$Compiler=(Get-DbeToolchain '2022' (Join-Path $root 'native/dbe-cl.vcxproj')).Compiler}
$id=[Guid]::NewGuid().ToString('N');$run=Join-Path $root ('out/connection-test-'+$id)
$data=Join-Path ([IO.Path]::GetTempPath()) ('dbe-connection-'+$id)
New-Item -ItemType Directory $run|Out-Null
$checks=[Collections.Generic.List[string]]::new()
function Check([bool]$Value,[string]$Name){if(!$Value){throw "FAIL $Name"};$checks.Add($Name)}
# Expected native failures write stderr; Windows PowerShell 5.1 turns redirected stderr into a
# terminating error under Stop. Keep the exit code as the result on both hosts.
function Native([scriptblock]$Command,[string]$Output){$ErrorActionPreference='Continue';& $Command *> $Output;return $LASTEXITCODE}
function Register([string]$Worker,[bool]$Failure=$false,[string]$Requester=$data){
    $log=Join-Path $run ('register-'+[Guid]::NewGuid().ToString('N')+'.log')
    $code=Native {& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Register-Requester.ps1') -WorkerDirectory $Worker -RequesterDirectory $Requester} $log
    if(($code -ne 0) -ne $Failure){Get-Content $log -Tail 8;throw 'Unexpected registration result'}
    if(!$Failure){return (Get-Content $log -Raw|ConvertFrom-Json)}
}
try {& (Join-Path $PSScriptRoot 'New-ConnectionRecovery.ps1') -PublishDirectory $publish -ConnectionDirectory (Join-Path $run 'unsafe') -BundleDirectory (Join-Path $run 'unsafe-bundle')|Out-Null;throw 'Accepted persistent out data'}
catch {if($_.Exception.Message -notlike 'Persistent connection data*'){throw};Check $true 'persistent-requester-data-rejects-out-directory'}
try {& (Join-Path $PSScriptRoot 'Package-Worker.ps1') -PublishDirectory $publish -WorkerAddress '127.0.0.1' -Compiler $Compiler -ConnectionDirectory $data -OutputDirectory (Join-Path $data 'nested-output')|Out-Null;throw 'Accepted nested persistent/output trees'}
catch {if($_.Exception.Message -notlike 'Connection data and removable package output*'){throw};Check $true 'persistent-connection-and-removable-output-trees-cannot-overlap'}
$bundle=& (Join-Path $PSScriptRoot 'New-ConnectionRecovery.ps1') -PublishDirectory $publish -ConnectionDirectory $data -BundleDirectory (Join-Path $run 'bundle')
$privateHash=(Get-FileHash (Join-Path $data 'identity.pfx')).Hash
$profile=Get-Content (Join-Path $data 'requester.json') -Raw|ConvertFrom-Json
$code=Native {& $exe requester $data} (Join-Path $run 'existing-requester.log')
Check ($code -ne 0 -and (Get-FileHash (Join-Path $data 'identity.pfx')).Hash -ceq $privateHash) 'existing-requester-command-refuses-identity-overwrite'
$zip=[IO.Compression.ZipFile]::OpenRead($bundle.WorkerRecoveryZip)
try {Check ($zip.Entries.Count -eq 5 -and !@($zip.Entries|Where-Object {$_.FullName -match '(?i)\.pfx$|identity|client\.json|worker\.json'}).Count) 'worker-transport-has-public-registration-files-only'}
finally {$zip.Dispose()}
$again=& (Join-Path $PSScriptRoot 'New-ConnectionRecovery.ps1') -PublishDirectory $publish -ConnectionDirectory $data -BundleDirectory (Join-Path $run 'bundle-again')
Check ($again.RequesterId -ceq $bundle.RequesterId -and (Get-FileHash (Join-Path $data 'identity.pfx')).Hash -ceq $privateHash) 'new-transport-bundle-reuses-durable-private-identity'
$pins=[Collections.Generic.List[string]]::new()
foreach($index in 1..2){
    $pair=Join-Path $run ('pair-'+$index);& $exe pair $pair $Compiler;if($LASTEXITCODE){throw 'Fixture pair failed'}
    $worker=Join-Path $pair 'worker';Copy-Item -Path (Join-Path $publish '*') -Destination $worker -Recurse
    $configPath=Join-Path $worker 'worker.json';$config=Get-Content $configPath -Raw|ConvertFrom-Json
    $socket=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$socket.Start();$port=$socket.LocalEndpoint.Port;$socket.Stop()
    $config.port=$port;$config|ConvertTo-Json|Set-Content $configPath
    Set-Content -LiteralPath (Join-Path $worker 'start-worker.cmd') -Value '@echo off' -Encoding ASCII
    $oldClient=Join-Path $pair 'client/client.json';$old=Get-Content $oldClient -Raw|ConvertFrom-Json;$old.port=$port;$old|ConvertTo-Json|Set-Content $oldClient
    $oldConfigHash=(Get-FileHash $configPath).Hash;$certHash=(Get-FileHash (Join-Path $worker 'identity.pfx')).Hash
    $state=Join-Path $worker 'state';New-Item -ItemType Directory $state|Out-Null
    $lock=Join-Path $state 'worker.lock';[IO.File]::WriteAllText($lock,'')
    $evidence=Join-Path $state 'preserved-note.txt';[IO.File]::WriteAllText($evidence,'retain this existing state')
    $guard=[IO.File]::Open($lock,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {Register $worker $true;Check ((Get-FileHash $configPath).Hash -ceq $oldConfigHash) "worker-$index-locked-state-refuses-mutation"}
    finally {$guard.Dispose()}
    $registration=Register $worker
    $after=Get-Content $configPath -Raw|ConvertFrom-Json
    Check ($registration.Changed -and (Get-FileHash $registration.ConfigBackup).Hash -ceq $oldConfigHash -and
        $after.allowedClientPin -ceq $profile.requesterPin -and $after.compiler -ceq $config.compiler -and $after.stateDirectory -ceq $config.stateDirectory -and
        $after.slots -eq $config.slots -and $after.memoryMiB -eq $config.memoryMiB -and $after.timeoutSeconds -eq $config.timeoutSeconds -and
        $after.bindAddress -ceq $config.bindAddress -and $after.port -eq $config.port -and $after.certificate -ceq $config.certificate -and
        (Get-FileHash (Join-Path $worker 'identity.pfx')).Hash -ceq $certHash -and
        [IO.File]::ReadAllText($evidence) -ceq 'retain this existing state') "worker-$index-registration-backs-up-config-and-preserves-identity-state-budgets"
    $unchangedHash=(Get-FileHash $configPath).Hash
    $repeat=Register $worker
    Check (!$repeat.Changed -and (Get-FileHash $configPath).Hash -ceq $unchangedHash -and @(Get-ChildItem $worker -Filter 'worker.json.backup-*').Count -eq 1 -and !$repeat.ConnectionBackup) "worker-$index-registration-is-idempotent"
    if($index -eq 1){
        # Exercise the delivered entry points with omitted paths, from another cwd.
        Copy-Item -Path (Join-Path $bundle.BundleDirectory '*') -Destination $worker
        $inputFile=Join-Path $run 'cmd-input.txt';[IO.File]::WriteAllText($inputFile,"`r`n",[Text.ASCIIEncoding]::new())
        $cmdLine='""'+(Join-Path $worker 'Register-Requester.cmd')+'" < "'+$inputFile+'""'
        $cmdCode=Native {& $env:ComSpec /d /s /c $cmdLine} (Join-Path $run 'delivered-cmd.log')
        $scriptCode=Native {& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $worker 'Register-Requester.ps1')} (Join-Path $run 'default-script.log')
        Check ($cmdCode -eq 0 -and $scriptCode -eq 0 -and (Get-FileHash $configPath).Hash -ceq $unchangedHash) 'delivered-registration-CMD-and-script-resolve-omitted-paths-on-Windows-PowerShell'
    }
    # A stale derived export must not leave registration permanently failing after the config commit.
    $exportBytes=[IO.File]::ReadAllBytes($registration.ConnectionFile)
    $stale=Get-Content $registration.ConnectionFile -Raw|ConvertFrom-Json;$stale.port=1;$stale|ConvertTo-Json|Set-Content $registration.ConnectionFile
    $staleHash=(Get-FileHash $registration.ConnectionFile).Hash
    $regenerated=Register $worker
    Check (!$regenerated.Changed -and (Get-FileHash $configPath).Hash -ceq $unchangedHash -and $regenerated.ConnectionBackup -and
        (Get-FileHash $regenerated.ConnectionBackup).Hash -ceq $staleHash -and
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($registration.ConnectionFile)) -ceq [Convert]::ToBase64String($exportBytes)) "worker-$index-stale-export-is-backed-up-and-regenerated"
    $import=& (Join-Path $PSScriptRoot 'Import-WorkerConnection.ps1') -ConnectionDirectory $data -WorkerConnectionFile $registration.ConnectionFile -HostName '127.0.0.1'
    if($index -eq 1){$recoveredClient=$import.ClientConfig}
    $pins.Add($import.WorkerPin)
    $repeatImport=& (Join-Path $PSScriptRoot 'Import-WorkerConnection.ps1') -ConnectionDirectory $data -WorkerConnectionFile $registration.ConnectionFile -HostName '127.0.0.1'
    Check ($repeatImport.ClientConfig -ceq $import.ClientConfig -and (Test-Path $import.ClientConfig)) "worker-$index-import-is-idempotent-and-outside-artifacts"
    $server=Start-Process -FilePath (Join-Path $worker 'dbe-worker.exe') -ArgumentList @('serve',('"'+$configPath+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run "server-$index.log") -RedirectStandardError (Join-Path $run "server-$index-error.log")
    try {
        $ready=$false
        for($attempt=0;$attempt -lt 40;$attempt++){if(!(Native {& $exe info $import.ClientConfig 7} (Join-Path $run "info-$index.json"))){$ready=$true;break};if($server.HasExited){break};Start-Sleep -Milliseconds 100}
        Check $ready "worker-$index-new-requester-authenticates-to-original-worker-identity"
        Register $worker $true
        Check ((Get-FileHash $configPath).Hash -ceq $unchangedHash) "worker-$index-running-worker-refuses-registration"
        $code=Native {& $exe info $oldClient 7} (Join-Path $run "old-client-$index.log")
        Check ($code -ne 0) "worker-$index-old-requester-is-not-authorized-after-rotation"
        $code=Native {& $exe probe $import.ClientConfig (Join-Path $run "compile-$index")} (Join-Path $run "compile-$index.log")
        Check ($code -eq 0 -and (Test-Path (Join-Path $run "compile-$index/result.obj"))) "worker-$index-real-TLS-MSVC-compile-with-recovered-connection"
    } finally {if(!$server.HasExited){$server.Kill();$server.WaitForExit(10000)|Out-Null};$server.Dispose()}
}
Check ($pins[0] -cne $pins[1]) 'two-distinct-worker-identities-share-only-requester-identity'
$export=Get-Content (Join-Path $run ('pair-1/worker/connection-'+$profile.requesterId+'.json')) -Raw|ConvertFrom-Json
$export.requesterId='0'*32;$badExport=Join-Path $run 'wrong-requester-export.json';$export|ConvertTo-Json|Set-Content $badExport
try {& (Join-Path $PSScriptRoot 'Import-WorkerConnection.ps1') -ConnectionDirectory $data -WorkerConnectionFile $badExport -HostName '127.0.0.1'|Out-Null;throw 'Accepted wrong requester export'}
catch {if($_.Exception.Message -notlike 'Invalid Worker connection export*'){throw};Check $true 'connection-import-refuses-export-for-another-requester'}
$bad=Join-Path $run 'bad-requester';New-Item -ItemType Directory $bad|Out-Null
Copy-Item (Join-Path $data 'requester.cer') $bad
$invalid=Get-Content (Join-Path $data 'requester.json') -Raw|ConvertFrom-Json;$invalid.requesterPin='0'*64;$invalid|ConvertTo-Json|Set-Content (Join-Path $bad 'requester.json')
$pairOneConfig=Join-Path $run 'pair-1/worker/worker.json';$pairOneHash=(Get-FileHash $pairOneConfig).Hash
$badLog=Join-Path $run 'mismatched-requester.log'
$code=Native {& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Register-Requester.ps1') -WorkerDirectory (Join-Path $run 'pair-1/worker') -RequesterDirectory $bad} $badLog
Check ($code -ne 0 -and (Get-Content $badLog -Raw) -match 'Requester certificate identity or validity mismatch' -and
    (Get-FileHash $pairOneConfig).Hash -ceq $pairOneHash) 'mismatched-public-requester-certificate-refused'
if($PackagePublishDirectory){
    $package=& (Join-Path $PSScriptRoot 'Package-Worker.ps1') -PublishDirectory $PackagePublishDirectory -WorkerAddress '127.0.0.1' -Compiler $Compiler -ConnectionDirectory (Join-Path $data 'install-pair') -OutputDirectory (Join-Path $run 'package-output')
    & (Join-Path $PSScriptRoot 'Test-WorkerPackage.ps1') -DeploymentFile $package.DeploymentFile *> (Join-Path $run 'package-verification.log')
    if(!$?){throw 'Initial package verification failed'}
    $pairKey=(Get-FileHash (Join-Path (Split-Path $package.ClientConfig -Parent) 'identity.pfx')).Hash
    # Move only this test's known output directory; retain all evidence.
    $oldOutput=[IO.Path]::GetFullPath((Join-Path $run 'package-output'));$retainedOutput=[IO.Path]::GetFullPath((Join-Path $run 'retained-package-output'))
    foreach($path in @($oldOutput,$retainedOutput)){if(!$path.StartsWith([IO.Path]::GetFullPath($run)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture move escaped run'}}
    Move-Item -LiteralPath $oldOutput -Destination $retainedOutput
    $version=& $package.ClientExecutable --version
    Check (!(Test-Path $oldOutput) -and $version -and (Test-Path $package.ClientConfig) -and (Test-Path $package.WorkerConfig) -and (Test-Path $package.DeploymentFile)) 'installed-client-and-deployment-survive-artifact-directory-removal'
    # A framework-dependent runtime cannot start on a Worker without .NET: both producers must refuse it before writing.
    $frameworkRuntime=$publish
    if((Get-Content (Join-Path $publish 'dbe-worker.runtimeconfig.json') -Raw|ConvertFrom-Json).runtimeOptions.PSObject.Properties.Name -contains 'includedFrameworks'){
        throw 'PublishDirectory must be the framework-dependent build so the self-contained refusal is tested against a real runtime'
    }
    foreach($producer in @('upgrade','package')){
        $refusedData=Join-Path $data "refused-framework-$producer";$refusedOutput=Join-Path $run "refused-framework-$producer"
        try {
            if($producer -eq 'upgrade'){& (Join-Path $PSScriptRoot 'Package-WorkerUpgrade.ps1') -DeploymentFile $package.DeploymentFile -PublishDirectory $frameworkRuntime -ConnectionDirectory $refusedData -OutputDirectory $refusedOutput|Out-Null}
            else {& (Join-Path $PSScriptRoot 'Package-Worker.ps1') -PublishDirectory $frameworkRuntime -WorkerAddress '127.0.0.1' -Compiler $Compiler -ConnectionDirectory $refusedData -OutputDirectory $refusedOutput|Out-Null}
            throw "Accepted framework-dependent runtime for $producer"
        } catch {
            if($_.Exception.Message -notlike '*separately installed .NET*'){throw}
            Check (!(Test-Path $refusedData) -and !(Test-Path $refusedOutput)) "$producer-refuses-framework-dependent-runtime-before-writing"
        }
    }
    $installed=Join-Path $run 'installed-worker';Expand-Archive -LiteralPath (Join-Path $retainedOutput 'dbe-worker-win-x64.zip') -DestinationPath $installed
    try {& (Join-Path $PSScriptRoot 'Package-WorkerUpgrade.ps1') -DeploymentFile $package.DeploymentFile -PublishDirectory $PackagePublishDirectory -Compiler $Compiler -ConnectionDirectory (Join-Path $data 'refused-compiler') -OutputDirectory (Join-Path $run 'refused-compiler')|Out-Null;throw 'Accepted compiler selection change'}
    catch {if($_.Exception.Message -notlike '*does not use a bundled toolchain*'){throw}
        Check (!(Test-Path (Join-Path $data 'refused-compiler')) -and !(Test-Path (Join-Path $run 'refused-compiler'))) 'runtime-upgrade-refuses-compiler-selection-change-before-writing'}
    $upgrade=& (Join-Path $PSScriptRoot 'Package-WorkerUpgrade.ps1') -DeploymentFile $package.DeploymentFile -PublishDirectory $PackagePublishDirectory -ConnectionDirectory (Join-Path $data 'upgrade-pair') -OutputDirectory (Join-Path $run 'upgrade-output')
    & (Join-Path $PSScriptRoot 'Test-WorkerPackage.ps1') -DeploymentFile $upgrade.DeploymentFile -InstalledWorkerDirectory $installed *> (Join-Path $run 'upgrade-verification.log')
    if(!$?){throw 'Upgrade package verification failed'}
    Check ((Get-FileHash (Join-Path (Split-Path $upgrade.ClientConfig -Parent) 'identity.pfx')).Hash -ceq $pairKey -and
        (Get-Content $upgrade.ClientConfig -Raw|ConvertFrom-Json).workerPin -ceq (Get-Content $package.ClientConfig -Raw|ConvertFrom-Json).workerPin) 'upgrade-preserves-authentication-from-durable-data-after-output-removal'
    # A Worker re-registered to the recovered requester is upgraded without reverting its allowed pin.
    $recoveredWorker=Join-Path $run 'pair-1/worker';$recoveredConfigHash=(Get-FileHash (Join-Path $recoveredWorker 'worker.json')).Hash
    try {& (Join-Path $PSScriptRoot 'Package-WorkerUpgrade.ps1') -ClientConfig $recoveredClient -PublishDirectory $PackagePublishDirectory -Compiler $Compiler -ConnectionDirectory (Join-Path $data 'refused-recovered-compiler') -OutputDirectory (Join-Path $run 'refused-recovered-compiler')|Out-Null;throw 'Accepted compiler change for unknown config'}
    catch {if($_.Exception.Message -notlike 'Compiler replacement requires a deployment record*'){throw};Check $true 'recovered-upgrade-refuses-compiler-change-for-unknown-worker-config'}
    $recoveredUpgrade=& (Join-Path $PSScriptRoot 'Package-WorkerUpgrade.ps1') -ClientConfig $recoveredClient -PublishDirectory $PackagePublishDirectory -ConnectionDirectory (Join-Path $data 'recovered-upgrade') -OutputDirectory (Join-Path $run 'recovered-upgrade-output')
    $originalClientHash=(Get-FileHash $recoveredClient).Hash
    $absoluteClient=Get-Content $recoveredClient -Raw|ConvertFrom-Json
    $absoluteClient.certificate=[IO.Path]::GetFullPath([IO.Path]::Combine((Split-Path $recoveredClient -Parent),[string]$absoluteClient.certificate))
    $absoluteClientPath=Join-Path $run 'absolute-client.json';$absoluteClient|ConvertTo-Json|Set-Content $absoluteClientPath
    $verifyDeployment=Join-Path $run 'absolute-client-deployment.json'
    [ordered]@{WorkerZip=$recoveredUpgrade.WorkerZip;ClientConfig=$absoluteClientPath;ClientExecutable=$recoveredUpgrade.ClientExecutable}|ConvertTo-Json|Set-Content $verifyDeployment
    & (Join-Path $PSScriptRoot 'Test-WorkerPackage.ps1') -DeploymentFile $verifyDeployment -InstalledWorkerDirectory $recoveredWorker *> (Join-Path $run 'recovered-upgrade-verification.log')
    if(!$?){throw 'Recovered upgrade verification failed'}
    $verification=Get-Content (Join-Path $run 'recovered-upgrade-verification.log') -Raw|ConvertFrom-Json
    Check ($verification.Status -ceq 'passed' -and (Get-FileHash $recoveredClient).Hash -ceq $originalClientHash) 'recovered-upgrade-verification-preserves-absolute-certificate-paths-and-original-config'
    $second=& (Join-Path $PSScriptRoot 'Package-WorkerUpgrade.ps1') -DeploymentFile $recoveredUpgrade.DeploymentFile -PublishDirectory $PackagePublishDirectory -ConnectionDirectory (Join-Path $data 'recovered-upgrade-2') -OutputDirectory (Join-Path $run 'recovered-upgrade-output-2')
    $recoveredFull=[IO.Path]::GetFullPath($recoveredClient)
    Check ([IO.Path]::GetFullPath($recoveredUpgrade.ClientConfig) -ieq $recoveredFull -and [IO.Path]::GetFullPath($second.ClientConfig) -ieq $recoveredFull -and !$recoveredUpgrade.WorkerConfig -and
        (Get-FileHash (Join-Path $recoveredWorker 'worker.json')).Hash -ceq $recoveredConfigHash) 'recovered-worker-runtime-upgrade-keeps-registered-requester'
}
$report=[pscustomobject]@{Run=$run;Checks=$checks.ToArray();ConnectionDirectory=$data;Scope='Synthetic loopback Workers; Windows PowerShell registration; real mutual TLS and MSVC. Physical Worker recovery remains separate.'}
$report|ConvertTo-Json -Depth 5|Set-Content (Join-Path $run 'checks.json')
$report|ConvertTo-Json -Depth 5

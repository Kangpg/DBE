[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory,[string]$RemoteClientConfig,[string]$HybridPolicy,[string[]]$AdditionalClients=@(),[switch]$LongOutputNames)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$tools=Get-DbeToolchain '2022' (Join-Path $root 'native\dbe-cl.vcxproj')
$publish=(Resolve-Path -LiteralPath $PublishDirectory).Path
$run=Join-Path $root ('out\compiler-profiles-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run|Out-Null
$exe=Join-Path $publish 'dbe-worker.exe';$pair=Join-Path $run 'pair'
$worker=$null
if($RemoteClientConfig){$clientConfig=(Resolve-Path -LiteralPath $RemoteClientConfig).Path}
else {
& $exe pair $pair $tools.Compiler
if($LASTEXITCODE -ne 0){throw 'Pair setup failed'}
$workerConfig=Join-Path $pair 'worker\worker.json';$clientConfig=Join-Path $pair 'client\client.json'
$socket=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$socket.Start();$port=$socket.LocalEndpoint.Port;$socket.Stop()
$wc=Get-Content -Raw $workerConfig|ConvertFrom-Json;$wc.bindAddress='127.0.0.1';$wc.port=$port;$wc|ConvertTo-Json|Set-Content $workerConfig
$cc=Get-Content -Raw $clientConfig|ConvertFrom-Json;$cc.host='127.0.0.1';$cc.port=$port;$cc|ConvertTo-Json|Set-Content $clientConfig
$worker=Start-Process -FilePath $exe -ArgumentList @('serve',('"'+$workerConfig+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'worker.log') -RedirectStandardError (Join-Path $run 'worker-error.log')
}
$checks=[Collections.Generic.List[string]]::new()
try {
    $ready=$false
    for($i=0;$i -lt 30;$i++){ & $exe info $clientConfig *> (Join-Path $run 'info.json');if($LASTEXITCODE -eq 0){$ready=$true;break};if(!$worker -or $worker.HasExited){throw 'Worker unavailable'};Start-Sleep -Milliseconds 100 }
    if(!$ready){throw 'Worker did not become ready'}
    $mode=if($HybridPolicy){'Hybrid'}else{'Distributed'}
    $state=Join-Path $root ('out\s-'+[Guid]::NewGuid().ToString('N'))
    $adapter=& (Join-Path $PSScriptRoot 'New-MSBuildAdapter.ps1') -Destination (Join-Path $run 'adapter') -ClientConfig $clientConfig -PublishDirectory $publish -Mode $mode -HostPolicy $HybridPolicy -AdditionalClients $AdditionalClients -StateDirectory $state
    $projectRoot=Join-Path $run 'project';New-Item -ItemType Directory -Path $projectRoot|Out-Null
    Copy-Item -Path (Join-Path $root 'fixture-remote\*') -Destination $projectRoot -Recurse
    $settings=[xml]'<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><DBEEnabled>true</DBEEnabled><DBEAdapterDirectory /></PropertyGroup></Project>'
    $settings.Project.PropertyGroup.DBEAdapterDirectory=$adapter.AdapterDirectory;$settings.Save((Join-Path $projectRoot 'DBE.settings.props'))
    $project=Join-Path $projectRoot 'remote-probe.vcxproj'
    $xml=[xml](Get-Content -Raw $project);$ns=[Xml.XmlNamespaceManager]::new($xml.NameTable);$ns.AddNamespace('m',$xml.DocumentElement.NamespaceURI)
    $cl=$xml.SelectSingleNode('//m:ItemDefinitionGroup/m:ClCompile',$ns)
    $cl.DebugInformationFormat='ProgramDatabase';$cl.PrecompiledHeader='Use';$cl.AdditionalOptions='/diagnostics:column -Zm1200 %(AdditionalOptions)'
    $optimization=$xml.CreateElement('WholeProgramOptimization',$xml.DocumentElement.NamespaceURI);$optimization.SetAttribute('Condition',"'`$(Configuration)' == 'Release'");$optimization.InnerText='true';$xml.SelectSingleNode('//m:PropertyGroup[@Label="Configuration"]',$ns).AppendChild($optimization)|Out-Null
    foreach($entry in @{PrecompiledHeaderFile='stdafx.h';MultiProcessorCompilation='true';SupportJustMyCode='true';TreatAngleIncludeAsExternal='true';ExternalWarningLevel='TurnOffAllWarnings'}.GetEnumerator()){$n=$xml.CreateElement($entry.Key,$xml.DocumentElement.NamespaceURI);$n.InnerText=$entry.Value;$cl.AppendChild($n)|Out-Null}
    $group=$xml.SelectSingleNode('//m:ItemGroup[m:ClCompile]',$ns)
    foreach($name in @('stdafx.cpp','cp949.cpp','utf16.cpp')){$n=$xml.CreateElement('ClCompile',$xml.DocumentElement.NamespaceURI);$n.SetAttribute('Include',$name);if($name -eq 'stdafx.cpp'){$pch=$xml.CreateElement('PrecompiledHeader',$xml.DocumentElement.NamespaceURI);$pch.InnerText='Create';$n.AppendChild($pch)|Out-Null};$group.AppendChild($n)|Out-Null};$xml.Save($project)
    [Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
    [IO.File]::WriteAllText((Join-Path $projectRoot 'stdafx.h'),"#pragma once`n#include <vector>`n#include <external-warning.h>`n")
    [IO.File]::WriteAllText((Join-Path $projectRoot 'headers\external-warning.h'),"#pragma once`ninline int external_warning(double x) { return x; }`n")
    [IO.File]::WriteAllText((Join-Path $projectRoot 'stdafx.cpp'),"#include `"stdafx.h`"`n")
    [IO.File]::WriteAllText((Join-Path $projectRoot 'cp949.cpp'),"#include `"stdafx.h`"`nint cp949_value(){return L`"한글`"[0];}`n",[Text.Encoding]::GetEncoding(949))
    [IO.File]::WriteAllText((Join-Path $projectRoot 'utf16.cpp'),"#include `"stdafx.h`"`nint utf16_value(){return L`"한글`"[0];}`n",[Text.Encoding]::Unicode)
    [IO.File]::WriteAllText((Join-Path $projectRoot 'main.cpp'),"#include `"stdafx.h`"`nint remote_value(); int cp949_value(); int utf16_value();`nint main(){return cp949_value()==0xd55c && utf16_value()==0xd55c ? remote_value() : 1;}`n",[Text.UTF8Encoding]::new($true))
    $valuePath=Join-Path $projectRoot 'value.cpp';[IO.File]::WriteAllText($valuePath,"#include `"stdafx.h`"`n"+[IO.File]::ReadAllText($valuePath))
    if($HybridPolicy){
        $generated=Join-Path $projectRoot 'Generated';New-Item -ItemType Directory -Path $generated|Out-Null
        [IO.File]::WriteAllText((Join-Path $generated 'relative.h'),"#pragma once`n#define RELATIVE_VALUE 7`n")
        [IO.File]::WriteAllText((Join-Path $generated 'relative.cpp'),"#include `"stdafx.h`"`n#include `"relative.h`"`nint relative_value(){return RELATIVE_VALUE;}`n")
        $n=$xml.CreateElement('ClCompile',$xml.DocumentElement.NamespaceURI);$n.SetAttribute('Include','Generated\relative.cpp');$group.AppendChild($n)|Out-Null;$xml.Save($project)
        # Keep real local compilation alive long enough to exercise concurrent placement,
        # rather than requiring network dispatch for a sub-100ms trivial TU.
        foreach($name in 'main','value','cp949','utf16'){
            $ballast=(1..5000|ForEach-Object {"int hybrid_$($name)_$_(){return $_;}"}) -join "`n"
            $encoding=switch($name){'cp949'{[Text.Encoding]::GetEncoding(949)} 'utf16'{[Text.Encoding]::Unicode} default{[Text.UTF8Encoding]::new($true)}}
            [IO.File]::AppendAllText((Join-Path $projectRoot ($name+'.cpp')),"`n"+$ballast,$encoding)
        }
        $policy=Get-Content $HybridPolicy -Raw|ConvertFrom-Json
        if($policy.PSObject.Properties.Name -contains 'localBatchSlots' -and $policy.localBatchSlots -gt 1){
            foreach($i in 1..16){
                $suffix=if($LongOutputNames){'_'+('s'*55)}else{''}
                $name="local-batch-$i$suffix.cpp";[IO.File]::WriteAllText((Join-Path $projectRoot $name),"#include `"stdafx.h`"`nint local_batch_$i(){return $i;}`n")
                $n=$xml.CreateElement('ClCompile',$xml.DocumentElement.NamespaceURI);$n.SetAttribute('Include',$name);[void]$group.AppendChild($n)
            }
            $xml.Save($project)
        }
    }
    function Build([string]$Name,[string]$Configuration='Debug',[bool]$Failure=$false){
        $log=Join-Path $run ($Name+'.log')
        & $tools.MSBuild $project /nologo /nr:false /m:1 /p:Platform=x64 "/p:Configuration=$Configuration" /v:normal *> $log
        if(($LASTEXITCODE -ne 0) -ne $Failure){Get-Content $log -Tail 25;throw "Unexpected result: $log"};return $log
    }
    $full=Build '01-debug'
    $remoteCount=@(Select-String -LiteralPath $full -SimpleMatch 'DBE: remote ').Count
    if((!$HybridPolicy -and $remoteCount -ne 4) -or $remoteCount -eq 0 -or !(Select-String -LiteralPath $full -SimpleMatch 'required PCH producer' -Quiet)){throw 'PCH/remote routing missing'}
    if($HybridPolicy -and !(Select-String -LiteralPath $full -Pattern 'DBE: local(-batch)?-running ' -Quiet)){throw 'Planned local compilation missing'}
    if($HybridPolicy -and $policy.PSObject.Properties.Name -contains 'localBatchSlots' -and $policy.localBatchSlots -gt 1){
        if(!(Select-String -Path $full -SimpleMatch 'DBE: local-batch-running ' -Quiet)){throw 'Local MP group did not execute'}
        $checks.Add('native-MP-group-preserves-shared-PCH-PDB-and-links')
        if($LongOutputNames){
            $objects=@(Get-ChildItem (Join-Path $projectRoot 'out\Debug\obj') -Filter 'local-batch-*_*.obj')
            if($objects.Count -ne 16){throw 'Long-named objects missing'}
            $localEvents=@(Get-ChildItem $state -Filter 'local-batch-running.json' -Recurse|ForEach-Object {Get-Content $_.FullName -Raw|ConvertFrom-Json}|Where-Object {$_.source -match 'local-batch-.*_s{55}\.cpp$'})
            if(!$localEvents.Count){throw 'Long-named files never exercised local MP'}
            if(@(Get-ChildItem (Join-Path $state 'local') -Directory).Count){throw 'Confirmed local outputs were not cleaned'}
            $checks.Add('long-object-names-local-MP-publication-and-cleanup')
        }
    }
    & (Join-Path $projectRoot 'out\Debug\bin\remote-probe.exe');if($LASTEXITCODE -ne 42){throw 'Mixed encoding executable failed'}
    if(Select-String -LiteralPath $full -Pattern 'LNK4099|warning C4244' -Quiet){throw 'PDB or external warning profile lost'}
    $symbols=& (Join-Path (Split-Path $tools.Compiler -Parent) 'dumpbin.exe') /symbols (Join-Path $projectRoot 'out\Debug\obj\value.obj')
    if($LASTEXITCODE -ne 0 -or !($symbols -match '__CheckForDebuggerJustMyCode')){throw 'Remote object lost /JMC instrumentation'}
    $checks.Add('PCH-Zi-MP-external-warnings-mixed-encoding-Debug-link-run')
    $checks.Add('JMC-helper-preserved-in-remote-object')
    if($HybridPolicy){
        $configFile=Join-Path $adapter.AdapterDirectory 'msbuild.json';$configuration=Get-Content -Raw $configFile|ConvertFrom-Json
        $savedClients=@(if($configuration.PSObject.Properties.Name -contains 'additionalClients'){$configuration.additionalClients})
        try {
            if($savedClients.Count){$configuration.additionalClients=@()}
            $configuration.mode='Distributed';$configuration|ConvertTo-Json|Set-Content $configFile
            $relativeLog=Build '01-relative-include-rejected' 'Debug' $true
            if(!(Select-String -LiteralPath $relativeLog -SimpleMatch 'PCH expansion would relocate' -Quiet)){throw 'Relocated include was not rejected'}
        } finally {$configuration.mode='Hybrid';if($savedClients.Count){$configuration.additionalClients=$savedClients};$configuration|ConvertTo-Json|Set-Content $configFile}
        $null=Build '01-relative-include-recovered'
        $checks.Add('relative-include-rejected-remotely-and-preserved-locally')
    }
    $stamp=(Get-Item (Join-Path $projectRoot 'out\Debug\obj\value.obj')).LastWriteTimeUtc.Ticks
    $noop=Build '02-noop'
    if(!$HybridPolicy){if((Get-Item (Join-Path $projectRoot 'out\Debug\obj\value.obj')).LastWriteTimeUtc.Ticks -ne $stamp){throw 'No-op replaced object'};$checks.Add('PDB-aware-object-reuse')}
    else {
        if(Select-String -LiteralPath $noop -Pattern 'DBE: (local(-batch)?-running|remote-requested|preparing|fallback-running)' -Quiet){throw 'No-op Hybrid build started compilation'}
        if((Get-Item (Join-Path $projectRoot 'out\Debug\obj\value.obj')).LastWriteTimeUtc.Ticks -ne $stamp){throw 'Hybrid no-op replaced object'}
        & (Join-Path $projectRoot 'out\Debug\bin\remote-probe.exe');if($LASTEXITCODE -ne 42){throw 'Hybrid repeated build changed runtime result'};$checks.Add('hybrid-no-op-skips-compile-and-preserves-object')
    }
    $release=Build '03-release' 'Release'; & (Join-Path $projectRoot 'out\Release\bin\remote-probe.exe');if($LASTEXITCODE -ne 42){throw 'Release executable failed'};$checks.Add('PCH-Zi-MP-Release-link-run')
    Add-Content -LiteralPath $valuePath -Value 'int user_warning(double x){return x;}'
    $errorLog=Build '04-warning-error' 'Debug' $true
    if(!(Select-String -LiteralPath $errorLog -Pattern 'C4244|C2220' -Quiet)){throw 'User warning was suppressed'}
    $checks.Add('external-warning-suppression-preserves-user-WX')
    [IO.File]::WriteAllText($valuePath,"#include `"stdafx.h`"`n"+[IO.File]::ReadAllText((Join-Path $root 'fixture-remote\value.cpp')))
    [IO.File]::WriteAllText((Join-Path $projectRoot 'utf16.cpp'),"#include `"stdafx.h`"`nint utf16_value(){return L`"한글`"[0];} const wchar_t* lost=L`"😀`";`n",[Text.Encoding]::Unicode)
    $loss=Build '05-loss-rejected' 'Debug' (!$HybridPolicy)
    if(!$HybridPolicy){if(!(Select-String -LiteralPath $loss -SimpleMatch 'cannot round-trip' -Quiet)){throw 'Preprocessor Unicode loss was not diagnosed'};$checks.Add('nonrepresentable-code-rejected-before-remote-publication')}
    else {& (Join-Path $projectRoot 'out\Debug\bin\remote-probe.exe');if($LASTEXITCODE -ne 42){throw 'Hybrid encoding fallback changed result'};$checks.Add('hybrid-preserves-unrepresentable-input-via-local-compiler')}
    [pscustomobject]@{Run=$run;Checks=$checks.ToArray();RemoteClientUsed=[bool]$RemoteClientConfig;Scope='Real MSVC and mutually authenticated Worker, linked profile fixtures. Synthetic fixtures only; no timing comparison or external-project claim.'}|ConvertTo-Json|Tee-Object -FilePath (Join-Path $run 'checks.json')
} finally {if($worker){if(!$worker.HasExited){$worker.Kill();$worker.WaitForExit(10000)|Out-Null};$worker.Dispose()}}

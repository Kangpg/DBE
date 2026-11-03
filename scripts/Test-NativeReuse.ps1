[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory,[string]$Compiler,[ValidateSet('2022','2026')][string]$VisualStudio='2022',[string]$RemoteClientConfig)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$tools=Get-DbeToolchain $VisualStudio (Join-Path $root 'native/dbe-cl.vcxproj') 'v143'
if(!$Compiler){$Compiler=$tools.Compiler}
$dia=Join-Path $tools.Root 'DIA SDK'
if(!(Test-Path (Join-Path $dia 'include/dia2.h'))){throw 'This verification requires the selected Visual Studio DIA SDK.'}
$build=Join-Path $root ('out/reuse-verifier-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $build|Out-Null
$props=[xml]'<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><ItemDefinitionGroup><ClCompile><AdditionalIncludeDirectories /></ClCompile></ItemDefinitionGroup></Project>'
$props.Project.ItemDefinitionGroup.ClCompile.AdditionalIncludeDirectories=(Join-Path $dia 'include')+';%(AdditionalIncludeDirectories)'
$props.Save((Join-Path $build 'dia.props'))
& $tools.MSBuild (Join-Path $root 'native/dbe-cl.vcxproj') /nologo /nr:false /m:1 /p:VcpkgEnabled=false /p:PreferredToolArchitecture=x64 /p:Configuration=Release /p:Platform=x64 /p:TargetName=pdb-types "/p:DBESource=$root\tests\pdb-types.cpp" "/p:OutDir=$build\" "/p:IntDir=$build\obj\" "/p:ForceImportBeforeCppTargets=$build\dia.props" /v:normal *> (Join-Path $build 'build.log')
if($LASTEXITCODE){throw "DIA verifier build failed: $build"}
$run=Join-Path $root ('out/native-reuse-'+[guid]::NewGuid().ToString('N'))
$log=$run+'.log'
$arguments=@('reuse-self-test')
if($RemoteClientConfig){$arguments=@('reuse-remote-test',(Resolve-Path -LiteralPath $RemoteClientConfig).Path)}
$arguments+=@($Compiler,$run,(Join-Path $build 'pdb-types.exe'),(Join-Path $dia 'bin/amd64/msdia140.dll'))
& (Join-Path $PublishDirectory 'dbe-worker.exe') @arguments *> $log
if($LASTEXITCODE){Get-Content $log -Tail 25;throw "Native reuse verification failed: $log"}
[pscustomobject]@{Run=$run;Log=$log;Checks=(Join-Path $run 'checks.json')}

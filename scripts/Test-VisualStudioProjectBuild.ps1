[CmdletBinding()]
param([Parameter(Mandatory)][string]$AdapterDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$tools=Get-DbeToolchain '2022' (Join-Path $root 'native/dbe-cl.vcxproj')
Add-Type -Path (Join-Path $root 'vsix/ProjectTarget.cs')
$run=Join-Path $root ('out/vsix-project-test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run|Out-Null
$app=Join-Path $run 'app';New-Item -ItemType Directory -Path $app|Out-Null
Copy-Item (Join-Path $root 'fixture-remote/*') $app -Recurse
$project=Join-Path $app 'App.Project.vcxproj'
$xml=[xml][IO.File]::ReadAllText((Join-Path $app 'remote-probe.vcxproj'))
foreach($import in @($xml.Project.Import)) {if($import.Project -match 'DBE'){[void]$xml.Project.RemoveChild($import)}}
$xml.Save($project)
$depGuid='{9B1A16BE-5CF2-4BD9-9572-0D312658EE01}'
$otherGuid='{9B1A16BE-5CF2-4BD9-9572-0D312658EE02}'
$folderGuid='{9B1A16BE-5CF2-4BD9-9572-0D312658EE03}'
$appGuid='{B98B0BD6-9ED8-4E05-9D3B-244E6B7D39E7}'
$record=Join-Path $run 'dependency.txt'
foreach($name in 'dependency','unrelated') {
    $utility=[xml][IO.File]::ReadAllText($project)
    $utility.Project.PropertyGroup | Where-Object {$_.GetAttribute('Label') -eq 'Globals'} | ForEach-Object {$_.ProjectGuid=if($name -eq 'dependency'){$depGuid}else{$otherGuid}}
    $utility.Project.PropertyGroup | Where-Object {$_.GetAttribute('Label') -eq 'Configuration'} | ForEach-Object {$_.ConfigurationType='Utility'}
    foreach($group in @($utility.Project.ItemGroup)){if($group.GetElementsByTagName('ClCompile').Count){[void]$utility.Project.RemoveChild($group)}}
    $target=$utility.CreateElement('Target',$utility.DocumentElement.NamespaceURI);$target.SetAttribute('Name','VerifyProjectSelection');$target.SetAttribute('BeforeTargets','PrepareForBuild')
    if($name -eq 'dependency') {
        $task=$utility.CreateElement('WriteLinesToFile',$utility.DocumentElement.NamespaceURI)
        $task.SetAttribute('File',$record);$task.SetAttribute('Lines','$(Configuration)');$task.SetAttribute('Overwrite','false')
    } else {$task=$utility.CreateElement('Error',$utility.DocumentElement.NamespaceURI);$task.SetAttribute('Text','Unrelated project must not be built')}
    [void]$target.AppendChild($task);[void]$utility.DocumentElement.AppendChild($target)
    $utility.Save((Join-Path $run ($name+'.vcxproj')))
}
$solution=Join-Path $run 'project-selection.sln'
$sln=@"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "front.ends", "front.ends", "$folderGuid"
EndProject
Project("{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}") = "App.Project", "app\App.Project.vcxproj", "$appGuid"
    ProjectSection(ProjectDependencies) = postProject
        $depGuid = $depGuid
    EndProjectSection
EndProject
Project("{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}") = "dependency", "dependency.vcxproj", "$depGuid"
EndProject
Project("{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}") = "unrelated", "unrelated.vcxproj", "$otherGuid"
EndProject
Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|x64 = Debug|x64
    EndGlobalSection
    GlobalSection(ProjectConfigurationPlatforms) = postSolution
        $appGuid.Debug|x64.ActiveCfg = Release|x64
        $appGuid.Debug|x64.Build.0 = Release|x64
        $depGuid.Debug|x64.ActiveCfg = Release|x64
        $depGuid.Debug|x64.Build.0 = Release|x64
        $otherGuid.Debug|x64.ActiveCfg = Debug|x64
        $otherGuid.Debug|x64.Build.0 = Debug|x64
    EndGlobalSection
    GlobalSection(NestedProjects) = preSolution
        $appGuid = $folderGuid
    EndGlobalSection
EndGlobal
"@
[IO.File]::WriteAllText($solution,$sln,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $run 'tested-solution.txt'),$sln,[Text.UTF8Encoding]::new($false))
@{adapterDirectory=$AdapterDirectory}|ConvertTo-Json|Set-Content (Join-Path $run 'project-selection.dbe.json')
$checks=[Collections.Generic.List[string]]::new()
foreach($rebuild in $false,$true) {
    $target=[Dbe.VisualStudio.ProjectTarget]::Create([string[]]@('front.ends','App.Project'),$rebuild)
    $log=Join-Path $run ($(if($rebuild){'rebuild'}else{'build'})+'.log')
    & $tools.MSBuild $solution /nologo /nr:false /m:2 "/t:$target" /p:Configuration=Debug /p:Platform=x64 /p:DBEEnabled=true "/p:DBEAdapterDirectory=$AdapterDirectory" "/p:ForceImportAfterCppTargets=$AdapterDirectory\DBE.Remote.targets" /p:VcpkgManifestInstall=false /v:normal *> $log
    if($LASTEXITCODE -ne 0){Get-Content $log -Tail 15;throw "Project target failed: $target"}
    & (Join-Path $app 'out/Release/bin/App.Project.exe')|Out-Null
    if($LASTEXITCODE -ne 42){throw 'Selected project did not produce the mapped Release executable'}
    $checks.Add($target)
}
$records=@(Get-Content $record)
if($records.Count -ne 2 -or @($records|Where-Object {$_ -ne 'Release'}).Count){throw 'Solution-only dependency or configuration mapping was lost'}
$checks.Add('solution-only-dependency-and-project-configuration-map-preserved')
$checks.Add('unrelated-project-not-built')
# A selected root project named Build must never select the whole solution target.
if([Dbe.VisualStudio.ProjectTarget]::Create([string[]]@('Build'),$false) -ne 'Solution:Build'){throw 'Reserved target collision'}
$checks.Add('reserved-project-name-disambiguated')
@{Run=$run;Solution=$solution;Checks=$checks.ToArray();Scope='Real MSBuild/MSVC project Build/Rebuild with nested names, solution-only dependency, remapped configuration and unrelated-project failure sentinel; UI selection separately verified.'}|ConvertTo-Json|Tee-Object -FilePath (Join-Path $run 'checks.json')

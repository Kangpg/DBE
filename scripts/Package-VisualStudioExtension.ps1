[CmdletBinding()]
param()
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
$built=& (Join-Path $PSScriptRoot 'Build-VisualStudioExtension.ps1')
$manifest=[xml][IO.File]::ReadAllText((Join-Path $root 'vsix/source.extension.vsixmanifest'))
$version=[string]$manifest.PackageManifest.Metadata.Identity.Version
$destination=Join-Path $root ('out/vsix-setup/'+$version)
New-Item -ItemType Directory -Path $destination -Force|Out-Null
Copy-Item -LiteralPath $built.Package -Destination (Join-Path $destination 'Dbe.VisualStudio.vsix')
foreach($file in 'Install-DBE.cmd','Install-DBE.ps1','README.txt'){
    $text=[IO.File]::ReadAllText((Join-Path $root ('vsix/setup/'+$file)))
    $encoding=if($file -eq 'Install-DBE.cmd'){[Text.ASCIIEncoding]::new()}else{[Text.UTF8Encoding]::new($true)}
    [IO.File]::WriteAllText((Join-Path $destination $file),$text.Replace("`r`n","`n").Replace("`n","`r`n"),$encoding)
}
$zip=Join-Path (Split-Path $destination -Parent) ('DBE-VisualStudio-Setup-'+$version+'.zip')
Compress-Archive -LiteralPath @((Join-Path $destination 'Dbe.VisualStudio.vsix'),(Join-Path $destination 'Install-DBE.cmd'),(Join-Path $destination 'Install-DBE.ps1'),(Join-Path $destination 'README.txt')) -DestinationPath $zip -Force
[pscustomobject]@{Version=$version;Directory=$destination;Zip=$zip;Vsix=(Join-Path $destination 'Dbe.VisualStudio.vsix')}

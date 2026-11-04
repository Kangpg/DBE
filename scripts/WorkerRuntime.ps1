# Single owner of the "deliverable Worker runtime" rule shared by initial and upgrade packaging.
# Worker PCs are not required to have .NET installed, so a delivered runtime must be self-contained.
Set-StrictMode -Version Latest
function Assert-DbeSelfContainedPublish([string]$Directory){
    $dir=(Resolve-Path -LiteralPath $Directory).ProviderPath
    $configPath=Join-Path $dir 'dbe-worker.runtimeconfig.json'
    if(!(Test-Path -LiteralPath $configPath -PathType Leaf) -or (Get-Item -LiteralPath $configPath).Length -gt 65536){throw 'Package requires a self-contained publish; Worker runtimeconfig is missing'}
    $options=([IO.File]::ReadAllText($configPath)|ConvertFrom-Json).runtimeOptions
    $names=@($options.PSObject.Properties|ForEach-Object Name)
    # Framework-dependent hosts declare framework/frameworks; self-contained ones declare includedFrameworks.
    if('framework' -in $names -or 'frameworks' -in $names -or 'includedFrameworks' -notin $names -or !@($options.includedFrameworks).Count){
        throw 'Package requires a self-contained publish; this runtime needs a separately installed .NET'
    }
    foreach($name in @('dbe-worker.exe','dbe-process.dll','coreclr.dll')){
        if(!(Test-Path -LiteralPath (Join-Path $dir $name) -PathType Leaf)){throw "Package requires a self-contained publish; missing $name"}
    }
    return $dir
}

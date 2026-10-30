[CmdletBinding()]
param([Parameter(Mandatory)][string]$DeploymentFile)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$root = Split-Path $PSScriptRoot -Parent
$deployment = Get-Content -Raw -LiteralPath $DeploymentFile | ConvertFrom-Json
$tools = Get-DbeToolchain '2022' (Join-Path $root 'native\dbe-cl.vcxproj')
$run = Join-Path $root ('out\remote-verification-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$utf8 = [Text.UTF8Encoding]::new($false)

function Invoke-Client([string[]]$Arguments, [string]$Log, [bool]$ExpectSuccess = $true) {
    & $deployment.ClientExecutable @Arguments *> $Log
    if ($ExpectSuccess -and $LASTEXITCODE -ne 0) { throw "Client failed: $Log" }
    if (!$ExpectSuccess -and $LASTEXITCODE -ne 1) { throw "Expected a reported compile failure: $Log" }
}
function Verify-Object([string]$Directory, [int]$Expected) {
    $source = Join-Path $Directory 'verify.cpp'
    [IO.File]::WriteAllText($source, ('extern "C" int dbe_probe(); extern "C" int entry(){return dbe_probe()=={0}?0:1;}' -replace '\{0\}', [string]$Expected), $utf8)
    Push-Location $Directory
    try {
        & $tools.Compiler /nologo /c /GS- /O2 /X /Foverify.obj verify.cpp *> compile-verifier.log
        if ($LASTEXITCODE -ne 0) { throw "Verifier compile failed: $Directory" }
        & (Join-Path (Split-Path $tools.Compiler -Parent) 'link.exe') /nologo /nodefaultlib /entry:entry /subsystem:console /out:verify.exe verify.obj result.obj *> link-verifier.log
        if ($LASTEXITCODE -ne 0) { throw "Returned object could not be linked: $Directory" }
        & (Join-Path $Directory 'verify.exe')
        if ($LASTEXITCODE -ne 0) { throw "Returned object had incorrect value; expected $Expected" }
    } finally { Pop-Location }
}

# Explicitly suppress only compiler/linker option injection for this fixed fixture.
# Restore process settings on exit, and never dump the ambient environment.
$saved = @{}
foreach ($name in @('CL','_CL_','LINK','_LINK_')) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $null, 'Process')
}
try {
    Invoke-Client @('info', $deployment.ClientConfig) (Join-Path $run 'worker-before.json')
    $worker = Get-Content -Raw -LiteralPath (Join-Path $run 'worker-before.json') | ConvertFrom-Json
    $checks.Add('actual-worker-mutual-tls-capabilities')
    $first = Join-Path $run 'first'
    Invoke-Client @('probe', $deployment.ClientConfig, $first) (Join-Path $run 'probe.log')
    $firstResult = Get-Content -Raw -LiteralPath (Join-Path $first 'result.json') | ConvertFrom-Json
    if ($firstResult.result.status -ne 'completed' -or $firstResult.toolchain -ne $worker.toolchain) { throw 'Unexpected Worker result identity.' }
    $hash = (Get-FileHash -LiteralPath (Join-Path $first 'result.obj')).Hash.ToLowerInvariant()
    if ($hash -ne $firstResult.result.output.sha256) { throw 'Returned object hash mismatch.' }
    $checks.Add('remote-compile-and-object-sha256')
    Verify-Object $first 42
    $checks.Add('link-and-execute-remote-object-42')

    $inputDirectory = $first + '.inputs'
    $manifestPath = Join-Path $inputDirectory 'manifest.json'
    foreach ($case in @('header-change','compile-error','recovery')) {
        # Preserve each attempt's exact input bytes and manifest for reproduction.
        $caseInputs = Join-Path $run ($case + '.inputs')
        New-Item -ItemType Directory -Path $caseInputs | Out-Null
        Copy-Item -LiteralPath (Join-Path $inputDirectory 'main.cpp') -Destination $caseInputs
        $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
        $header = if ($case -eq 'compile-error') { '#error DBE_TWO_PC_ERROR_CHECK' } else { 'inline constexpr int probe_value=43;' }
        [IO.File]::WriteAllText((Join-Path $caseInputs 'value.h'), $header, $utf8)
        $manifest.attempt = [Guid]::NewGuid().ToString('N')
        foreach ($file in $manifest.files) {
            $path = Join-Path $caseInputs $file.path
            $file.length = (Get-Item -LiteralPath $path).Length
            $file.sha256 = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
        }
        $caseManifest = Join-Path $caseInputs 'manifest.json'
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $caseManifest -Encoding UTF8
        $destination = Join-Path $run $case
        Invoke-Client @('compile', $deployment.ClientConfig, $caseManifest, $caseInputs, $destination) (Join-Path $run ($case + '.log')) ($case -ne 'compile-error')
        $result = Get-Content -Raw -LiteralPath (Join-Path $destination 'result.json') | ConvertFrom-Json
        if ($case -eq 'compile-error') {
            $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Join-Path $destination 'stdout.bin'))) + [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Join-Path $destination 'stderr.bin')))
            if ($result.result.status -ne 'compile-error' -or !$text.Contains('DBE_TWO_PC_ERROR_CHECK') -or (Test-Path -LiteralPath (Join-Path $destination 'result.obj'))) { throw 'Compile-error semantics not preserved.' }
            $checks.Add('remote-error-diagnostic-and-no-object')
        } else {
            if ($result.result.status -ne 'completed') { throw "Unexpected status: $case" }
            Verify-Object $destination 43
            $checks.Add($case + '-object-executed-43')
        }
    }
    Invoke-Client @('info', $deployment.ClientConfig) (Join-Path $run 'worker-after.json')
    $checks.Add('worker-ready-after-error-and-recovery')
    [pscustomobject]@{
        CapturedAtUtc=[DateTime]::UtcNow.ToString('o'); Worker=$worker; Checks=$checks.ToArray(); Run=$run
        Scope='Two-PC explicit-input compile, returned-object link/execution, header modification, error and recovery. Not automatic MSBuild distribution or performance benchmarking.'
        Limitations=@('This fixture explicitly targets the reported Worker toolchain; it does not compare the local compiler binary-directory identity.','Remote overload/cancellation/forced termination remain locally verified only.','CPU, peak process-family memory and agent tokens were not measured.')
    } | ConvertTo-Json -Depth 8 | Tee-Object -FilePath (Join-Path $run 'verification.json')
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}

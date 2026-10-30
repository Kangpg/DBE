[CmdletBinding()]
param([ValidateSet('2022', '2026')][string]$VisualStudio = '2022')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$root = Split-Path $PSScriptRoot -Parent
$built = & (Join-Path $PSScriptRoot 'Build-Wrapper.ps1') -VisualStudio $VisualStudio
$project = Join-Path $root 'native\dbe-cl.vcxproj'
$tools = Get-DbeToolchain $VisualStudio $project
$run = Join-Path $root ('out\contract-' + $VisualStudio + ' 한글 경로 ' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$wrapper = Join-Path $run 'dbe-cl.exe'
Copy-Item -LiteralPath $built.Wrapper -Destination $wrapper
$log = Join-Path $run 'probe-build.log'
& $tools.MSBuild $project /nologo /nr:false /m:1 /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$($tools.Toolset)" /p:PreferredToolArchitecture=x64 "/p:DBESource=$root\tests\child-probe.cpp" /p:TargetName=child-probe "/p:OutDir=$run\" "/p:IntDir=$run\obj\" /v:normal *> $log
if ($LASTEXITCODE -ne 0) { throw "Probe build failed: $log" }
$probe = Join-Path $run 'child-probe.exe'
$checks = [System.Collections.Generic.List[string]]::new()

function Start-Child([string]$Exe, [string]$RawArguments, [string]$Compiler) {
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Exe
    $info.Arguments = $RawArguments
    $info.WorkingDirectory = $run
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Environment.Remove('DBE_REAL_CL') | Out-Null
    if ($Compiler) { $info.Environment['DBE_REAL_CL'] = $Compiler }
    $info.Environment['DBE_TEST_SENTINEL'] = 'selected value 한글'
    return [System.Diagnostics.Process]::Start($info)
}
function Invoke-Child([string]$Exe, [string]$RawArguments, [string]$Compiler) {
    $process = Start-Child $Exe $RawArguments $Compiler
    $out = [IO.MemoryStream]::new()
    $err = [IO.MemoryStream]::new()
    try {
        $outTask = $process.StandardOutput.BaseStream.CopyToAsync($out)
        $errTask = $process.StandardError.BaseStream.CopyToAsync($err)
        [byte[]]$inputBytes = 0, 1, 13, 10, 255, 128, 65
        try { $process.StandardInput.BaseStream.Write($inputBytes, 0, $inputBytes.Length) } catch [IO.IOException] { }
        $process.StandardInput.Close()
        if (!$process.WaitForExit(15000)) { throw "Process timed out: $Exe" }
        if (!$outTask.Wait(5000) -or !$errTask.Wait(5000)) { throw 'Output pipe did not close.' }
        return [pscustomobject]@{ Code=$process.ExitCode; Out=[Convert]::ToBase64String($out.ToArray()); Err=[Convert]::ToBase64String($err.ToArray()); Text=[Text.Encoding]::UTF8.GetString($err.ToArray()) }
    } finally {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose(); $out.Dispose(); $err.Dispose()
    }
}
function Assert-Equivalent($Direct, $Wrapped, [string]$Name) {
    if ($Direct.Code -ne $Wrapped.Code -or $Direct.Out -cne $Wrapped.Out -or $Direct.Err -cne $Wrapped.Err) { throw "Direct/wrapper mismatch: $Name" }
    $checks.Add($Name)
}

$raw = '  "two words" "" "한글" "trailing\\" "a\"b" a\\\b /DNAME="value with space" @"unopened file.rsp"'
Assert-Equivalent (Invoke-Child $probe $raw '') (Invoke-Child $wrapper $raw $probe) 'raw-arguments-cwd-environment-binary-stdio-exit-37'
Assert-Equivalent (Invoke-Child $probe '--exit' '') (Invoke-Child $wrapper '--exit' $probe) 'full-dword-exit-status'

foreach ($configuration in @('', 'relative-cl.exe', (Join-Path $run 'missing.exe'), $wrapper)) {
    $failure = Invoke-Child $wrapper '' $configuration
    if ($failure.Code -ne 125 -or !$failure.Text.Contains('DBE wrapper:')) { throw "Invalid compiler config was not rejected: $configuration" }
}
$checks.Add('missing-relative-absent-self-compiler-rejected')
$alias = Join-Path $run 'wrapper-hardlink.exe'
New-Item -ItemType HardLink -Path $alias -Target $wrapper | Out-Null
if ((Invoke-Child $wrapper '' $alias).Code -ne 125) { throw 'Hardlink recursion was not rejected.' }
$checks.Add('hardlink-recursion-rejected')

# Packaged Local adapters select their compiler without modifying the caller's
# environment. A present but invalid sidecar must never fall back to that env.
$sidecar = [IO.Path]::ChangeExtension($wrapper, '.compiler')
try {
    [IO.File]::WriteAllText($sidecar, $probe, [Text.Encoding]::Unicode)
    Assert-Equivalent (Invoke-Child $probe $raw $wrapper) (Invoke-Child $wrapper $raw $wrapper) 'sidecar-preserves-arguments-and-overrides-environment'
    foreach ($invalid in @('relative.exe', $wrapper, $alias, ($probe + [char]0))) {
        [IO.File]::WriteAllText($sidecar, $invalid, [Text.Encoding]::Unicode)
        if ((Invoke-Child $wrapper '' $probe).Code -ne 125) { throw 'Invalid sidecar fell back to environment.' }
    }
    [IO.File]::WriteAllBytes($sidecar, [byte[]]@(65, 0, 66))
    if ((Invoke-Child $wrapper '' $probe).Code -ne 125) { throw 'Malformed sidecar was accepted.' }
    $checks.Add('sidecar-invalid-path-identity-and-encoding-rejected')
} finally { Remove-Item -LiteralPath $sidecar -ErrorAction SilentlyContinue }

# Compile UTF-16 response file with relative Unicode source/output paths. This
# requires no SDK includes, so no shell environment setup is needed.
$source = Join-Path $run '한글 소스.cpp'
[IO.File]::WriteAllText($source, "int square(int x) { return x * x; }`n")
$response = Join-Path $run 'compile args.rsp'
[IO.File]::WriteAllText($response, '/nologo /c /Z7 /utf-8 "한글 소스.cpp" /Fo"한글 결과.obj"', [Text.Encoding]::Unicode)
$digest = (Get-FileHash -LiteralPath $response -Algorithm SHA256).Hash
$direct = Invoke-Child $tools.Compiler '@"compile args.rsp"' ''
if ($direct.Code -ne 0 -or !(Test-Path -LiteralPath (Join-Path $run '한글 결과.obj'))) { throw 'Direct response-file compile failed.' }
$wrapped = Invoke-Child $wrapper '@"compile args.rsp"' $tools.Compiler
Assert-Equivalent $direct $wrapped 'real-msvc-utf16-response-file'
if ((Get-FileHash -LiteralPath $response -Algorithm SHA256).Hash -ne $digest) { throw 'Wrapper changed response file.' }
[IO.File]::WriteAllText($source, "#error DBE_RESPONSE_FAILURE`n")
$directError = Invoke-Child $tools.Compiler '@"compile args.rsp"' ''
if ($directError.Code -eq 0) { throw 'Invalid source unexpectedly compiled successfully.' }
Assert-Equivalent $directError (Invoke-Child $wrapper '@"compile args.rsp"' $tools.Compiler) 'real-msvc-error-diagnostics-and-exit'

# Hold process handles while children are alive: post-cancellation observations
# use these handles, never a potentially reused PID. Kill only our wrapper.
[IO.File]::WriteAllText($sidecar, $probe, [Text.Encoding]::Unicode)
$owner = Start-Child $wrapper '--linger' ''
$child = $null; $grandchild = $null
try {
    $ready = $owner.StandardOutput.ReadLineAsync()
    if (!$ready.Wait(5000) -or $ready.Result -notmatch '^(\d+) (\d+)$') { throw 'Descendant readiness failed.' }
    $child = [Diagnostics.Process]::GetProcessById([int]$Matches[1])
    $grandchild = [Diagnostics.Process]::GetProcessById([int]$Matches[2])
    $null = $child.Handle; $null = $grandchild.Handle
    $replace = $null
    try { $replace = [IO.File]::Open($sidecar, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite) }
    catch [IO.IOException] { }
    if ($replace) { $replace.Dispose(); throw 'Active compiler sidecar was not pinned.' }
    $checks.Add('sidecar-pinned-through-child-lifetime')
    $owner.Kill()
    if (!$owner.WaitForExit(5000) -or !$child.WaitForExit(5000) -or !$grandchild.WaitForExit(5000)) { throw 'Wrapper cancellation left descendants alive.' }
    $checks.Add('forced-wrapper-exit-cleans-child-and-grandchild')
} finally {
    if (!$owner.HasExited) { $owner.Kill(); $owner.WaitForExit(5000) | Out-Null }
    $owner.Dispose()
    if ($child) { $child.Dispose() }
    if ($grandchild) { $grandchild.Dispose() }
    Remove-Item -LiteralPath $sidecar
}
$result = [pscustomobject]@{ visualStudio=$VisualStudio; compiler=$tools.Compiler; checks=$checks.ToArray(); limitations=@('Forced termination tested; interactive Visual Studio Stop Build and console Ctrl+C are not covered here.'); output=$run }
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8
$result | ConvertTo-Json -Depth 4

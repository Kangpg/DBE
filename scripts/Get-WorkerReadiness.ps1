[CmdletBinding()]
param(
    [ValidateSet('2022', '2026')][string]$VisualStudio = '2022',
    [Parameter(Mandatory)][string]$InitiatorAddress,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'out\readiness' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$warnings = New-Object 'System.Collections.Generic.List[string]'

# This report contains selected machine/toolchain facts, never the environment.
$hardware = $null
try {
    $system = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $processors = @(Get-CimInstance Win32_Processor -ErrorAction Stop)
    $hardware = [ordered]@{
        cpu = @($processors | ForEach-Object { $_.Name.Trim() })
        logicalProcessors = [int]$system.NumberOfLogicalProcessors
        physicalMemoryBytes = [long]$system.TotalPhysicalMemory
    }
} catch { $warnings.Add("Hardware inspection unavailable: $($_.Exception.Message)") }

$network = @()
foreach ($adapter in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
    if ($adapter.OperationalStatus -ne 'Up') { continue }
    $properties = $adapter.GetIPProperties()
    foreach ($address in $properties.UnicastAddresses) {
        if ($address.Address.AddressFamily -ne 'InterNetwork' -or
            [System.Net.IPAddress]::IsLoopback($address.Address)) { continue }
        $network += [ordered]@{
            interface = $adapter.Name
            address = $address.Address.ToString()
            mask = $address.IPv4Mask.ToString()
            gateways = @($properties.GatewayAddresses | ForEach-Object { $_.Address.ToString() })
        }
    }
}

$compiler = $null
try {
    $tools = Get-DbeToolchain $VisualStudio (Join-Path $root 'fixture\probe.vcxproj')
    $binary = Get-Item -LiteralPath $tools.Compiler
    $compiler = [ordered]@{
        toolset = $tools.Toolset
        path = $binary.FullName
        fileVersion = $binary.VersionInfo.FileVersion
        sha256 = (Get-FileHash -LiteralPath $binary.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        limitation = 'cl.exe identity only; not a complete toolchain fingerprint or compile verification'
    }
} catch { $warnings.Add("Compiler inspection unavailable: $($_.Exception.Message)") }

$ping = New-Object System.Net.NetworkInformation.Ping
try {
    $reply = $ping.Send($InitiatorAddress, 1500)
    $reachability = [ordered]@{
        address = $InitiatorAddress
        method = 'ICMP'
        status = $reply.Status.ToString()
        roundtripMilliseconds = $(if ($reply.Status -eq 'Success') { $reply.RoundtripTime } else { $null })
    }
} catch {
    $reachability = [ordered]@{ address=$InitiatorAddress; method='ICMP'; status='Unknown'; error=$_.Exception.Message }
} finally { $ping.Dispose() }

$report = [ordered]@{
    schemaVersion = 1
    capturedAtUtc = [DateTime]::UtcNow.ToString('o')
    machine = [System.Net.Dns]::GetHostName()
    hardware = $hardware
    network = $network
    compiler = $compiler
    initiatorReachability = $reachability
    warnings = @($warnings.ToArray())
    scope = 'Readiness inventory only. No Worker listener, compile, firewall change, or installation. ICMP failure does not prove TCP is blocked.'
}
$path = Join-Path $OutputDirectory ("worker-readiness-{0}.json" -f [Guid]::NewGuid().ToString('N'))
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
Write-Output "Report: $path"
if (!$compiler) { Write-Warning 'MSVC was not resolved; see the report.' }
Write-Output ("Initiator ICMP: {0} (not a Worker connection test)" -f $reachability.status)

[CmdletBinding()]
param([Parameter(Mandatory)][string]$InitiatorAddress)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'dbe-worker.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Run the copy next to the packaged dbe-worker.exe.' }
$address = [System.Net.IPAddress]::Parse($InitiatorAddress)
if ($address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { throw 'Expected one IPv4 initiator address.' }
# Unique to this installation path; reruns reuse a matching rule, not unrelated settings.
$hash = [System.Security.Cryptography.SHA256]::Create()
try { $suffix = ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($exe)))).Replace('-', '').Substring(0, 12) }
finally { $hash.Dispose() }
$name = 'DBE-Worker-' + $suffix
$rule = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
if ($rule) {
    $ports = $rule | Get-NetFirewallPortFilter
    $addresses = $rule | Get-NetFirewallAddressFilter
    $application = $rule | Get-NetFirewallApplicationFilter
    if ($ports.LocalPort -ne '43127' -or $ports.Protocol -ne 'TCP' -or $addresses.RemoteAddress -ne $InitiatorAddress -or $application.Program -ne $exe -or $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow' -or $rule.Enabled -ne 'True') {
        throw "An existing rule differs; inspect it before changing: $name"
    }
    Write-Output "Existing rule: $name"
} else {
    New-NetFirewallRule -Name $name -DisplayName 'DBE Worker: paired initiator' -Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol TCP -LocalPort 43127 -RemoteAddress $InitiatorAddress -Program $exe | Out-Null
    Write-Output "Created rule: $name"
}
Write-Output "To remove: Remove-NetFirewallRule -Name '$name'"

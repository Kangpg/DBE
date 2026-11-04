# Shared ownership and format checks for the connection lifecycle scripts.
Set-StrictMode -Version Latest
function Assert-DbePlainConnectionPath([string]$Path){
    for($part=[IO.Path]::GetFullPath($Path);$part;$part=[IO.Path]::GetDirectoryName($part)){
        if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){
            throw 'Connection paths must not contain reparse points'
        }
    }
}
function Get-DbeConnectionPin([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate){
    $hash=[Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($Certificate.RawData))).Replace('-','').ToLowerInvariant() }
    finally {$hash.Dispose()}
}
function Get-DbePersistentConnectionDirectory([string]$Directory){
    if(!$Directory){$Directory=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('DBE/connections/'+[Guid]::NewGuid().ToString('N'))}
    $path=[IO.Path]::GetFullPath($Directory);Assert-DbePlainConnectionPath $path
    if(@($path.Split([char[]]'\/')|Where-Object {$_ -ieq 'out'}).Count){throw 'Persistent connection data must be outside out directories'}
    return $path
}
function Assert-DbeSeparateConnectionOutput([string]$Data,[string]$Output){
    $a=[IO.Path]::GetFullPath($Data).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $b=[IO.Path]::GetFullPath($Output).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if($a.StartsWith($b,[StringComparison]::OrdinalIgnoreCase) -or $b.StartsWith($a,[StringComparison]::OrdinalIgnoreCase)){
        throw 'Connection data and removable package output must have separate directory trees'
    }
}
function Read-DbeConnectionJson([string]$Path){
    Assert-DbePlainConnectionPath $Path
    if(!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -gt 65536){throw 'Expected a small connection JSON file'}
    return ([IO.File]::ReadAllText($Path)|ConvertFrom-Json)
}
function Read-DbeRequester([string]$Directory,[switch]$RequirePrivateKey){
    $profile=Read-DbeConnectionJson (Join-Path $Directory 'requester.json')
    if($profile.version -ne 1 -or $profile.requesterId -cnotmatch '^[0-9a-f]{32}$' -or $profile.requesterPin -cnotmatch '^[0-9a-f]{64}$'){
        throw 'Invalid requester profile'
    }
    $path=Join-Path $Directory 'requester.cer';Assert-DbePlainConnectionPath $path
    if((Get-Item -LiteralPath $path).Length -gt 16384){throw 'Oversized requester certificate'}
    $cert=[Security.Cryptography.X509Certificates.X509Certificate2]::new($path)
    try {
        if((Get-DbeConnectionPin $cert) -cne $profile.requesterPin -or $cert.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or $cert.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow){throw 'Requester certificate identity or validity mismatch'}
        $eku=@($cert.Extensions|Where-Object {$_.Oid.Value -eq '2.5.29.37'})
        if($eku.Count -ne 1 -or !@($eku[0].EnhancedKeyUsages|Where-Object Value -eq '1.3.6.1.5.5.7.3.2').Count){throw 'Expected a client-authentication certificate'}
    } finally {$cert.Dispose()}
    if($RequirePrivateKey){
        $path=Join-Path $Directory 'identity.pfx';Assert-DbePlainConnectionPath $path
        if((Get-Item -LiteralPath $path).Length -gt 65536){throw 'Oversized requester private identity'}
        $cert=[Security.Cryptography.X509Certificates.X509Certificate2]::new($path,'',[Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet)
        try {if(!$cert.HasPrivateKey -or (Get-DbeConnectionPin $cert) -cne $profile.requesterPin){throw 'Requester private identity mismatch'}}
        finally {$cert.Dispose()}
    }
    return $profile
}
# Caller-owned files are never replaced. -Derived marks a file regenerated from another owner's
# facts: a differing copy is backed up and replaced so a retry converges. Returns the backup path.
function Write-DbeConnectionJson([string]$Path,$Value,[switch]$Derived){
    Assert-DbePlainConnectionPath $Path
    $text=($Value|ConvertTo-Json -Depth 16)
    $exists=Test-Path -LiteralPath $Path
    if($exists){
        if([IO.File]::ReadAllText($Path) -ceq $text){return $null}
        if(!$Derived){throw 'Existing connection file differs; preserve it and use a new destination'}
    }
    $pending=$Path+'.pending-'+[Guid]::NewGuid().ToString('N');$backup=$null
    try {
        [IO.File]::WriteAllText($pending,$text,[Text.UTF8Encoding]::new($false))
        if($exists){$backup=$Path+'.backup-'+[Guid]::NewGuid().ToString('N');[IO.File]::Replace($pending,$Path,$backup)}
        else {[IO.File]::Move($pending,$Path)}
    } finally {if(Test-Path -LiteralPath $pending){Remove-Item -LiteralPath $pending}}
    return $backup
}

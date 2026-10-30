# MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
# Interactive Windows OpenSSH profile helper. Never reads or displays a private key.
[CmdletBinding()]
param(
    [ValidateSet('Profile', 'GenerateKey', 'Fingerprint')][string]$Action = 'Profile',
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$')][string]$Alias,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9.:-]*$')][string]$Hostname,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_.-]*$')][string]$Username,
    [ValidateRange(1,65535)][int]$Port = 22,
    [string]$IdentityFile,
    [string]$CertificateFile,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_.-]*$')][string]$JumpHost,
    [ValidateSet('ed25519','ed25519-sk')][string]$KeyType = 'ed25519',
    [string]$SshDirectory = (Join-Path $env:USERPROFILE '.ssh')
)
$ErrorActionPreference = 'Stop'
$sshDirectory = [IO.Path]::GetFullPath($SshDirectory)
$sshTools = Join-Path ([Environment]::GetFolderPath('System')) 'OpenSSH'
$keygen = Join-Path $sshTools 'ssh-keygen.exe'
if ($Action -ne 'Profile' -and -not (Test-Path -LiteralPath $keygen)) { throw 'Install the Windows OpenSSH Client optional feature first.' }
function Get-ConfigPath([string]$Path) {
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path -match '["`\r\n%$]') {
        throw 'Use an absolute path without quotes, line breaks or expansion characters.'
    }
    return $Path.Replace('\','/')
}
Get-ConfigPath $sshDirectory | Out-Null
if ($Action -eq 'GenerateKey') {
    if (-not $IdentityFile) { throw 'Supply -IdentityFile with a NEW absolute key path.' }
    Get-ConfigPath $IdentityFile | Out-Null
    if ((Test-Path -LiteralPath $IdentityFile) -or (Test-Path -LiteralPath ($IdentityFile + '.pub'))) {
        throw 'The key path already exists. Existing keys are never overwritten.'
    }
    $parent = Split-Path $IdentityFile -Parent
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'Create the destination folder first.' }
    if ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Use a directory without redirection.' }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $permitted = @($sid.Value, 'S-1-5-18', 'S-1-5-32-544')
    $parentRules = (Get-Acl -LiteralPath $parent).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])
    if (@($parentRules | Where-Object { $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -notin $permitted }).Count -gt 0) {
        throw 'The destination directory grants access to other accounts. Use a private directory before generating a key.'
    }
    Write-Host 'Enter a strong passphrase when ssh-keygen prompts. For ed25519-sk, connect your FIDO2 token.'
    & $keygen -t $KeyType -a 100 -f $IdentityFile
    if ($LASTEXITCODE -ne 0) { throw 'Key generation failed or was cancelled. FIDO2 support depends on the installed OpenSSH build and token.' }
    $security = New-Object System.Security.AccessControl.FileSecurity
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $security.SetOwner($sid)
    $security.SetAccessRuleProtection($true,$false)
    $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','Allow')))
    Set-Acl -LiteralPath $IdentityFile -AclObject $security
    Write-Host "Key created. Only share the PUBLIC file: $IdentityFile.pub"
    exit
}
if ($Action -eq 'Fingerprint') {
    if (-not $IdentityFile -or -not $IdentityFile.EndsWith('.pub') -or -not (Test-Path -LiteralPath $IdentityFile -PathType Leaf)) {
        throw 'Supply -IdentityFile pointing to an existing PUBLIC .pub file.'
    }
    & $keygen -lf $IdentityFile -E sha256
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read public-key fingerprint.' }
    exit
}
if (-not $Alias -or -not $Hostname -or -not $Username) { throw 'Profile requires -Alias, -Hostname and -Username.' }
$profileDirectory = Join-Path $sshDirectory 'mgremote'
New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null
$profilePath = Join-Path $profileDirectory ($Alias + '.conf')
if (Test-Path -LiteralPath $profilePath) { throw 'This profile already exists. Review it manually before changing it.' }
$lines = @("Host $Alias", "    HostName $Hostname", "    User $Username", "    Port $Port",
    '    StrictHostKeyChecking ask', '    ForwardAgent no', '    Compression no', '    PermitLocalCommand no')
if ($IdentityFile) {
    $keyPath = Get-ConfigPath $IdentityFile
    if (-not (Test-Path -LiteralPath $IdentityFile -PathType Leaf)) { throw 'Identity file does not exist.' }
    $lines += ('    IdentityFile "' + $keyPath + '"')
    $lines += '    IdentitiesOnly yes'
}
if ($CertificateFile) {
    $certPath = Get-ConfigPath $CertificateFile
    if (-not (Test-Path -LiteralPath $CertificateFile -PathType Leaf)) { throw 'SSH certificate file does not exist.' }
    $lines += '    CertificateFile "' + $certPath + '"'
}
if ($JumpHost) { $lines += "    ProxyJump $JumpHost" }
# Include an exact profile path, never a wildcard over unrelated files.
$includeLine = 'Include "' + $profilePath.Replace('\','/') + '"'
$configPath = Join-Path $sshDirectory 'config'
$existing = if (Test-Path -LiteralPath $configPath) { [IO.File]::ReadAllText($configPath) } else { '' }
if ($existing -and -not $existing.EndsWith("`n")) { $existing += "`r`n" }
$encoding = New-Object Text.UTF8Encoding($false)
# CreateNew is deliberate: refuse to replace a concurrently created profile.
$stream = [IO.File]::Open($profilePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $writer = New-Object IO.StreamWriter($stream,$encoding)
    $writer.Write(($lines -join "`r`n") + "`r`n")
    $writer.Flush()
} finally { $stream.Dispose() }
if ($existing) {
    $backup = $configPath + '.mgremote-' + [Guid]::NewGuid().ToString('N') + '.bak'
    [IO.File]::Copy($configPath,$backup,$false)
}
[IO.File]::WriteAllText($configPath, $includeLine + "`r`n" + $existing, $encoding)
Write-Host "Profile created: $profilePath"
Write-Host "In MGRemote use SSH2, WindowsTerminal, hostname '$Alias', empty username, and port $Port."
Write-Host 'Verify the server fingerprint through a trusted channel at first connection. Configure the jump-host alias separately when used.'

# MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
# Run in PowerShell 7 on Windows. No SDK, AD, network or real credentials required.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $root 'mRemoteNG/Security/WindowsSecurityPolicy.cs'), (Join-Path $root 'mRemoteNG/Security/TemporarySshKey.cs'), (Join-Path $root 'mRemoteNG/Security/SshProfileStore.cs'), (Join-Path $root 'mRemoteNG/Security/UserDataCleanup.cs'), (Join-Path $root 'mRemoteNG/Security/AnyDeskLauncher.cs'), (Join-Path $root 'mRemoteNG/Security/SshFileTransfer.cs')
$script:checks = 0
function Assert-True($Condition, [string]$Name) {
    if (-not $Condition) { throw "FAILED: $Name" }
    $script:checks++
}
function Assert-Rejected([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Assert-True $rejected $Name
}
[mRemoteNG.Security.WindowsSecurityPolicy]::ValidateRdp(1, $true)
Assert-True $true 'RDP valid policy accepted'
foreach ($level in @(0, 2, 99)) {
    Assert-Rejected { [mRemoteNG.Security.WindowsSecurityPolicy]::ValidateRdp($level, $true) } "RDP authentication $level refused"
}
Assert-Rejected { [mRemoteNG.Security.WindowsSecurityPolicy]::ValidateRdp(1, $false) } 'RDP without NLA refused'
foreach ($hostValue in @('-oProxyCommand=calc', 'host name', 'host"name', 'host&calc', "host`nname", 'user@host', 'host%name')) {
    Assert-Rejected { [mRemoteNG.Security.WindowsSecurityPolicy]::BuildSshArguments($hostValue, 'admin', 22) } 'SSH hostile hostname refused'
}
Assert-Rejected { [mRemoteNG.Security.WindowsSecurityPolicy]::BuildSshArguments('server', '-oProxyCommand=x', 22) } 'SSH hostile username refused'
foreach ($port in @(0, -1, 65536)) {
    Assert-Rejected { [mRemoteNG.Security.WindowsSecurityPolicy]::BuildSshArguments('server', 'admin', $port) } 'SSH invalid port refused'
}
$sshArguments = [mRemoteNG.Security.WindowsSecurityPolicy]::BuildSshArguments('2001:db8::1', 'admin', 2222)
Assert-True ($sshArguments -match 'StrictHostKeyChecking=ask' -and $sshArguments -match 'ForwardAgent=no' -and $sshArguments.EndsWith('-p 2222 -l admin 2001:db8::1')) 'SSH identity checks, no forwarding, IPv6 and alternate port'
Assert-True (-not [mRemoteNG.Security.WindowsSecurityPolicy]::AllowAutomaticUpdates) 'Upstream installer channel disabled'
foreach ($options in @('-1', '-A', '-hostkey *', '-proxycmd calc.exe', '-pw secret', '-load unsafe', '-i', '-L -proxycmd', '-sessionlog output.log', '"unterminated')) {
    Assert-Rejected { [mRemoteNG.Security.WindowsSecurityPolicy]::GetSafePuttyOptions($options) } 'Unsafe PuTTY options refused'
}
$safeOptions = [mRemoteNG.Security.WindowsSecurityPolicy]::GetSafePuttyOptions('-L 127.0.0.1:3390:server:3389 -D 1080 -N -agent')
Assert-True ($safeOptions.Length -eq 6) 'PuTTY tunnels and agent use accepted'
$key = $null
$abandoned = $null
try {
    $key = [mRemoteNG.Security.TemporarySshKey]::Create('SYNTHETIC TEST KEY - NOT A CREDENTIAL')
    $keyPath = $key.FilePath
    $keyDirectory = Split-Path $keyPath -Parent
    $keyRoot = Split-Path $keyDirectory -Parent
    Assert-True ((Get-Content -LiteralPath $keyPath -Raw) -eq 'SYNTHETIC TEST KEY - NOT A CREDENTIAL') 'Key can be read by the client'
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $acl = Get-Acl -LiteralPath $keyPath
    $rules = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
    Assert-True ($rules.Count -gt 0 -and @($rules | Where-Object { $_.IdentityReference.Value -ne $sid -or $_.AccessControlType -ne 'Allow' }).Count -eq 0) 'Key ACL grants access only to current Windows user'
    [IO.Directory]::SetLastWriteTimeUtc($keyDirectory, [DateTime]::UtcNow.AddDays(-2))
    [mRemoteNG.Security.TemporarySshKey]::CleanupAbandoned($keyRoot, [TimeSpan]::FromDays(1))
    Assert-True (Test-Path -LiteralPath $keyPath) 'Cleanup preserves a live old session'
    $abandoned = Join-Path $keyRoot ('key-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($abandoned) | Out-Null
    [IO.File]::WriteAllText((Join-Path $abandoned 'key.ppk'), 'SYNTHETIC ABANDONED KEY')
    [IO.File]::WriteAllText((Join-Path $abandoned 'lease'), '')
    [IO.Directory]::SetLastWriteTimeUtc($abandoned, [DateTime]::UtcNow.AddDays(-2))
    [mRemoteNG.Security.TemporarySshKey]::CleanupAbandoned($keyRoot, [TimeSpan]::FromDays(1))
    Assert-True (-not (Test-Path -LiteralPath $abandoned)) 'Abandoned session key removed'
    $dispose = [Delegate]::CreateDelegate([Action], $key, 'Dispose')
    [Threading.Tasks.Parallel]::Invoke([Action[]]@($dispose, $dispose, $dispose))
    $key = $null
    Assert-True (-not (Test-Path -LiteralPath $keyDirectory)) 'Key and session directory removed on close'
} finally {
    if ($null -ne $key) { $key.Dispose() }
}
[xml]$settings = Get-Content (Join-Path $root 'mRemoteNG/Properties/Settings.settings') -Raw
$auth = $settings.SettingsFile.Settings.Setting | Where-Object Name -eq 'ConDefaultRDPAuthenticationLevel'
Assert-True ($auth.Value.InnerText -eq 'AuthRequired') 'New connections require RDP server authentication'
$export = Get-Content (Join-Path $root 'mRemoteNG/UI/Forms/FrmExport.cs') -Raw
Assert-True ($export.Contains('chkPassword.Checked = false;') -and $export.Contains('chkAssignedCredential.Checked = false;')) 'Export credentials opt-in'
$factory = Get-Content (Join-Path $root 'mRemoteNG/Connection/Protocol/ProtocolFactory.cs') -Raw
Assert-True (-not $factory.Contains('new ProtocolSSH1()') -and -not $factory.Contains('new ProtocolTelnet()') -and -not $factory.Contains('new ProtocolRlogin()')) 'Legacy protocol construction disabled'
$rdp = Get-Content (Join-Path $root 'mRemoteNG/Connection/Protocol/RDP/RdpProtocol.cs') -Raw
Assert-True ($rdp.Contains('WindowsSecurityPolicy.ValidateRdp(') -and -not $rdp.Contains('AuthenticationLevel = 0;')) 'RDP policy connected to initialization'
$putty = Get-Content (Join-Path $root 'mRemoteNG/Connection/Protocol/PuttyBase.cs') -Raw
Assert-True ($putty.Contains('arguments.Add("-batch")') -and $putty.Contains('WindowsSecurityPolicy.GetSafePuttyOptions(') -and $putty.Contains('TemporarySshKey.Create(privateKey)')) 'PuTTY policy and protected key lifecycle integrated'
foreach ($tool in @('Configure-MGRemoteSsh.ps1','Build-MGRemote.ps1','Sign-MGRemoteRelease.ps1','Build-MGRemotePackage.ps1')) {
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $tool), [ref]$null, [ref]$parseErrors) | Out-Null
    Assert-True ($parseErrors.Count -eq 0) "$tool parses correctly"
}
$fixture = Join-Path $root ('artifacts/test-' + [Guid]::NewGuid().ToString('N'))
$sshFixture = Join-Path $fixture '.ssh'
try {
    New-Item -ItemType Directory -Path $sshFixture -Force | Out-Null
    $syntheticIdentity = Join-Path $fixture 'test key.ppk'
    [IO.File]::WriteAllText($syntheticIdentity, 'SYNTHETIC KEY FOR CONFIG TEST ONLY')
    [IO.File]::WriteAllText((Join-Path $sshFixture 'config'), "Host existing`r`n    HostName existing.example.com`r`n")
    & (Join-Path $PSScriptRoot 'Configure-MGRemoteSsh.ps1') -SshDirectory $sshFixture -Alias test-linux -Hostname server.example.com -Username admin -Port 2222 -IdentityFile $syntheticIdentity -JumpHost bastion
    $config = [IO.File]::ReadAllText((Join-Path $sshFixture 'config'))
    $profile = [IO.File]::ReadAllText((Join-Path $sshFixture 'mgremote/test-linux.conf'))
    Assert-True ($config.StartsWith('Include "') -and $config.Contains('Host existing')) 'SSH config preserves existing profiles and adds exact Include'
    Assert-True (@(Get-ChildItem -LiteralPath $sshFixture -Filter 'config.mgremote-*.bak').Count -eq 1) 'SSH config backup created'
    Assert-True ($profile.Contains('ProxyJump bastion') -and $profile.Contains('Port 2222') -and $profile.Contains('StrictHostKeyChecking ask')) 'SSH bastion, host verification and alternate port profile'
    Assert-True ($profile -match 'IdentityFile "[^"\r\n]+"\r?\n    IdentitiesOnly yes') 'Identity path with spaces is quoted on a separate config line'
    Assert-Rejected { & (Join-Path $PSScriptRoot 'Configure-MGRemoteSsh.ps1') -SshDirectory $sshFixture -Alias test-linux -Hostname server.example.com -Username admin } 'SSH profile overwrite refused'
    Assert-Rejected { & (Join-Path $PSScriptRoot 'Configure-MGRemoteSsh.ps1') -SshDirectory $sshFixture -Alias bad -Hostname "host`nProxyCommand calc" -Username admin } 'SSH config directive injection refused'
    $safeIdentity = [mRemoteNG.Security.WindowsSecurityPolicy]::GetSafePuttyOptions('-i "' + $syntheticIdentity + '" -N')
    Assert-True ($safeIdentity[1] -eq $syntheticIdentity) 'PuTTY identity path with spaces preserved'
    $nativeDirectory = Join-Path $fixture 'native-ssh'
    New-Item -ItemType Directory -Path $nativeDirectory -Force | Out-Null
    $nativeConfigPath = Join-Path $nativeDirectory 'config'
    $original = [Text.Encoding]::UTF8.GetBytes("Host preserved`r`n    HostName preserved.example.com`r`n")
    [IO.File]::WriteAllBytes($nativeConfigPath, $original)
    $nativeIdentity = Join-Path $fixture 'OpenSSH test key'
    [IO.File]::WriteAllText($nativeIdentity, "-----BEGIN OPENSSH PRIVATE KEY-----`nSYNTHETIC CONFIG TEST ONLY`n-----END OPENSSH PRIVATE KEY-----`n")
    $keyAcl = [Security.AccessControl.FileSecurity]::new()
    $keyOwner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $keyAcl.SetOwner($keyOwner)
    $keyAcl.SetAccessRuleProtection($true, $false)
    $keyAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($keyOwner, 'FullControl', 'Allow'))
    Set-Acl -LiteralPath $nativeIdentity -AclObject $keyAcl
    $nativeProfile = [mRemoteNG.Security.SshProfileStore]::CreateProfile('native-test', '2001:db8::1', 'admin', 2222, $nativeIdentity, '', 'bastion', $nativeDirectory)
    $nativeConfig = [IO.File]::ReadAllText($nativeConfigPath)
    $nativeText = [IO.File]::ReadAllText($nativeProfile)
    $scpPath = Join-Path ([Environment]::GetFolderPath('System')) 'OpenSSH/scp.exe'
    if ((Test-Path -LiteralPath $scpPath) -and [Diagnostics.FileVersionInfo]::GetVersionInfo($scpPath).FileMajorPart -ge 9) {
        $upload = [mRemoteNG.Security.SshFileTransfer]::BuildStartInfo('server.example.invalid', 'alice', 2222, ('-i "' + $nativeIdentity + '"'), $nativeIdentity, '/home/alice/Test folder', $true, $false, 0, $null)
        Assert-True (-not $upload.UseShellExecute -and -not $upload.CreateNoWindow -and $upload.ArgumentList.Contains('-i') -and $upload.ArgumentList.Contains('2222') -and $upload.ArgumentList[$upload.ArgumentList.Count - 1] -eq 'alice@server.example.invalid:/home/alice/Test folder') 'SFTP upload uses one quoted path argument, optional private key and native authentication'
        Assert-True (-not $upload.ArgumentList.Contains('-O') -and -not $upload.ArgumentList.Contains('-T')) 'SFTP does not enable legacy SCP or disable filename checks'
        $download = [mRemoteNG.Security.SshFileTransfer]::BuildStartInfo('server.example.invalid', '', 22, '', $nativeDirectory, '/srv/documents', $false, $true, 45678, 'target.example.invalid')
        Assert-True ($download.ArgumentList.Contains('-r') -and $download.ArgumentList.Contains('HostName=127.0.0.1') -and $download.ArgumentList.Contains('HostKeyAlias=target.example.invalid') -and $download.ArgumentList[$download.ArgumentList.Count - 2] -eq 'server.example.invalid:/srv/documents') 'Recursive SFTP download preserves target identity through a tunnel'
        foreach ($remotePath in @('relative/path', '/data/*', "/data/line`nnew")) {
            Assert-Rejected { [mRemoteNG.Security.SshFileTransfer]::BuildStartInfo('server', '', 22, '', $nativeIdentity, $remotePath, $true, $false, 0, $null) } 'Ambiguous, wildcard or control-character transfer paths refused'
        }
        Assert-Rejected { [mRemoteNG.Security.SshFileTransfer]::BuildStartInfo('server', '', 22, '', $nativeDirectory, '/tmp', $true, $false, 0, $null) } 'Uploading a directory requires explicit recursive mode'
        [mRemoteNG.Security.SshFileTransfer]::ValidateUploadTree($nativeDirectory, [Threading.CancellationToken]::None)
        Assert-True $true 'Local upload tree validated without reading file contents'
    } else { Write-Warning 'Windows OpenSSH 9+ is absent: real-client SFTP argument tests skipped.' }
    $directArguments = [mRemoteNG.Security.WindowsSecurityPolicy]::BuildOpenSshArguments('server.example.invalid', 'alice', 2222, ('-i "' + $nativeIdentity + '"'))
    $tunnelArguments = [mRemoteNG.Security.WindowsSecurityPolicy]::BuildSshTunnelArguments('bastion.example.invalid', 'alice', 22, '', '2001:db8::1', 3389, 45678)
    Assert-True ($tunnelArguments.Contains('-L 127.0.0.1:45678:[2001:db8::1]:3389') -and $tunnelArguments.Contains('ExitOnForwardFailure=yes') -and $tunnelArguments.Contains('GatewayPorts=no')) 'SSH tunnel binds only IPv4 loopback, accepts IPv6 destinations and fails on forwarding errors'
    foreach ($destination in @('-oProxyCommand=bad', 'server:22:other', "server`ncommand", 'server name')) {
        Assert-Rejected { [mRemoteNG.Security.WindowsSecurityPolicy]::BuildSshTunnelArguments('bastion', 'alice', 22, '', $destination, 3389, 45678) } 'Tunnel destination injection refused'
    }
    Assert-Rejected { [mRemoteNG.Security.WindowsSecurityPolicy]::BuildSshTunnelArguments('bastion', 'alice', 22, '', 'target', 0, 45678) } 'Invalid tunnel destination port refused'
    $routed = [mRemoteNG.Security.WindowsSecurityPolicy]::BuildTunneledSshArguments('target-profile', 'target-user', 2222, '', 45678, 'target.example.invalid')
    Assert-True ($routed.Contains('HostName=127.0.0.1') -and $routed.Contains('HostKeyAlias=[target.example.invalid]:2222') -and $routed.Contains('-p 45678') -and $routed.EndsWith('target-profile')) 'SSH through tunnel preserves target profile lookup and real host-key identity, independently of the bastion'
    Assert-True ($directArguments.Contains('-i "' + $nativeIdentity + '"') -and $directArguments.Contains('IdentitiesOnly=yes') -and $directArguments.EndsWith('-l alice server.example.invalid')) 'Native SSH honors an explicitly selected private key and quotes its path'
    Assert-Rejected { [mRemoteNG.Security.WindowsSecurityPolicy]::BuildOpenSshArguments('server', 'alice', 22, '-N') } 'Unsupported OpenSSH options produce an explicit error instead of being silently ignored'
    Assert-Rejected { [mRemoteNG.Security.WindowsSecurityPolicy]::BuildOpenSshArguments('server', 'alice', 22, ('-i "' + $syntheticIdentity + '"')) } 'Direct OpenSSH rejects a PuTTY PPK identity'
    $missingIdentity = Join-Path $fixture 'rotated missing private key'
    Assert-True ([mRemoteNG.Security.WindowsSecurityPolicy]::GetSafePuttyOptions(('-i "' + $missingIdentity + '"'), $false)[1] -eq $missingIdentity) 'Key editor can inspect a deleted absolute identity path for replacement/removal'
    Assert-Rejected { [mRemoteNG.Security.WindowsSecurityPolicy]::GetSafePuttyOptions('-i "' + $missingIdentity + '"') } 'Launching still refuses a missing identity file'
    $publicIdentity = Join-Path $fixture 'public-only.pub'
    [IO.File]::WriteAllText($publicIdentity, 'ssh-ed25519 AAAATEST fixture')
    Assert-Rejected { [mRemoteNG.Security.SshProfileStore]::ValidateExistingPrivateKey($publicIdentity) } 'A public key cannot be selected as a private identity'
    $looseIdentity = Join-Path $fixture 'loose-private-key'
    [IO.File]::Copy($nativeIdentity, $looseIdentity)
    $looseAcl = Get-Acl -LiteralPath $looseIdentity
    $looseAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-1-0'), 'Read', 'Allow'))
    Set-Acl -LiteralPath $looseIdentity -AclObject $looseAcl
    Assert-Rejected { [mRemoteNG.Security.SshProfileStore]::ValidateExistingPrivateKey($looseIdentity) } 'Private keys readable by other accounts are refused without modifying them'
    $sshClient = Join-Path ([Environment]::GetFolderPath('System')) 'OpenSSH/ssh.exe'
    $keygen = Join-Path ([Environment]::GetFolderPath('System')) 'OpenSSH/ssh-keygen.exe'
    if ((Test-Path -LiteralPath $sshClient) -and (Test-Path -LiteralPath $keygen)) {
        $encryptedIdentity = Join-Path $fixture 'encrypted fixture key'
        & $keygen -q -t ed25519 -a 4 -N 'mgremote-synthetic-passphrase' -f $encryptedIdentity
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic encrypted-key generation failed.' }
        [mRemoteNG.Security.SshProfileStore]::ValidateExistingPrivateKey($encryptedIdentity)
        $derived = & $keygen -y -P 'mgremote-synthetic-passphrase' -f $encryptedIdentity
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic private-key decryption failed.' }
        $publicParts = ([IO.File]::ReadAllText($encryptedIdentity + '.pub')).Trim().Split(' ')
        Assert-True (($derived -join '').StartsWith($publicParts[0] + ' ' + $publicParts[1])) 'Windows OpenSSH decrypts the synthetic passphrase-protected private key'
        $emptyConfig = Join-Path $fixture 'isolated ssh config'
        [IO.File]::WriteAllText($emptyConfig, '')
        $encryptedArguments = [mRemoteNG.Security.WindowsSecurityPolicy]::BuildOpenSshArguments('server.example.invalid', 'alice', 2222, ('-i "' + $encryptedIdentity + '"'))
        $interactive = [mRemoteNG.Security.WindowsSecurityPolicy]::BuildOpenSshStartInfo('server.example.invalid', 'alice', 2222, ('-i "' + $encryptedIdentity + '"'))
        Assert-True (-not $interactive.UseShellExecute -and -not $interactive.CreateNoWindow -and $interactive.FileName -eq $sshClient -and $interactive.Arguments -eq $encryptedArguments) 'Native-console SSH uses the trusted Windows client and exact validated arguments without capturing the passphrase'
        $start = [Diagnostics.ProcessStartInfo]::new($sshClient)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $start.RedirectStandardError = $true
        $start.Arguments = '-G -F "' + $emptyConfig + '" ' + $encryptedArguments
        $probe = [Diagnostics.Process]::Start($start)
        try {
            $parsedConfig = $probe.StandardOutput.ReadToEnd()
            $probe.StandardError.ReadToEnd() | Out-Null
            $probe.WaitForExit()
            Assert-True ($probe.ExitCode -eq 0 -and $parsedConfig.Contains('port 2222') -and $parsedConfig.Contains('identitiesonly yes') -and $parsedConfig.Contains('encrypted fixture key')) 'Real Windows ssh -G parses the exact backend arguments, encrypted key path with spaces and alternate port offline'
        } finally { $probe.Dispose() }
    } else { Write-Warning 'Windows OpenSSH is absent: real-client offline key tests were skipped.' }
    foreach ($target in @('123456789', 'test-client@ad', 'server@company')) {
        Assert-True ([mRemoteNG.Security.AnyDeskLauncher]::IsValidTarget($target)) 'AnyDesk ID or alias accepted'
    }
    foreach ($target in @('--install', '123 --with-password', 'alias&calc@ad', "123`n456", 'host.example.invalid')) {
        Assert-True (-not [mRemoteNG.Security.AnyDeskLauncher]::IsValidTarget($target)) 'AnyDesk options, injection and ambiguous hostnames refused'
    }
    $fakeAnyDesk = Join-Path $fixture 'AnyDesk-fixture.exe'
    [IO.File]::WriteAllText($fakeAnyDesk, 'SYNTHETIC NEVER EXECUTED')
    $anyDeskStart = [mRemoteNG.Security.AnyDeskLauncher]::BuildStartInfo($fakeAnyDesk, '123456789')
    Assert-True (-not $anyDeskStart.UseShellExecute -and $anyDeskStart.ArgumentList.Count -eq 1 -and $anyDeskStart.ArgumentList[0] -eq '123456789') 'AnyDesk launches directly with one destination argument, no shell and no password'
    Assert-Rejected { [mRemoteNG.Security.AnyDeskLauncher]::BuildStartInfo('AnyDesk.exe', '123456789') } 'AnyDesk relative executable path refused'
    Assert-True ($nativeText.Contains('ProxyJump bastion') -and $nativeText.Contains('HostName 2001:db8::1') -and $nativeText.Contains('IdentitiesOnly yes')) 'Native app profile supports keys, bastions and IPv6'
    Assert-True ($nativeConfig.Contains('Host preserved') -and $nativeConfig.StartsWith('Include "')) 'Native app preserves existing SSH config'
    $backup = Get-ChildItem -LiteralPath $nativeDirectory -Filter 'config.mgremote-*.bak' | Select-Object -First 1
    Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($backup.FullName)) -eq [Convert]::ToBase64String($original)) 'Native backup preserves exact original bytes'
    Assert-Rejected { [mRemoteNG.Security.SshProfileStore]::CreateProfile('native-test', 'server', 'admin', 22, '', '', '', $nativeDirectory) } 'Native profile overwrite refused'
    Assert-Rejected { [mRemoteNG.Security.SshProfileStore]::CreateProfile('../escape', 'server', 'admin', 22, '', '', '', $nativeDirectory) } 'Native profile path traversal refused'
    Assert-Rejected { [mRemoteNG.Security.SshProfileStore]::CreateProfile('inject', "server`nProxyCommand calc", 'admin', 22, '', '', '', $nativeDirectory) } 'Native profile directive injection refused'
    Assert-Rejected { [mRemoteNG.Security.SshProfileStore]::CreateProfile('putty-key', 'server', 'admin', 22, $syntheticIdentity, '', '', $nativeDirectory) } 'Native OpenSSH rejects incompatible PuTTY key'
    [IO.File]::WriteAllBytes($nativeConfigPath, [Text.Encoding]::Unicode.GetPreamble() + [Text.Encoding]::Unicode.GetBytes('Host unicode'))
    $beforeFailure = [IO.File]::ReadAllBytes($nativeConfigPath)
    Assert-Rejected { [mRemoteNG.Security.SshProfileStore]::CreateProfile('utf16', 'server', 'admin', 22, '', '', '', $nativeDirectory) } 'Incompatible existing config encoding refused'
    Assert-True ((-not (Test-Path -LiteralPath (Join-Path $nativeDirectory 'mgremote/utf16.conf'))) -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($nativeConfigPath)) -eq [Convert]::ToBase64String($beforeFailure)) 'Failed native profile creation preserves config and removes its new profile'
    Assert-Rejected { [mRemoteNG.Security.SshProfileStore]::ValidateNewKeyPath($nativeIdentity) } 'Existing private key overwrite refused'
    Assert-Rejected { [mRemoteNG.Security.SshProfileStore]::ValidateNewKeyPath((Join-Path $fixture 'missing/key')) } 'Nonexistent key directory refused'
    $profileFixture = Join-Path $fixture 'profile'
    foreach ($directory in @('AppData/Roaming/MGRemote','AppData/Local/MGRemote/TemporaryKeys','AppData/Local/MGRemote contributors/MGRemote.exe_Url_test','AppData/Local/MGRemote contributors/AnotherApp','AppData/Roaming/mRemoteNG','.ssh/mgremote','.ssh/other')) {
        $created = Join-Path $profileFixture $directory
        New-Item -ItemType Directory -Path $created -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $created 'synthetic.txt'), 'SYNTHETIC CLEANUP TEST DATA')
    }
    $sharedKey = Join-Path $profileFixture '.ssh/id_ed25519'
    [IO.File]::WriteAllText($sharedKey, 'SYNTHETIC PERSONAL KEY - MUST BE PRESERVED')
    $ownInclude = (Join-Path $profileFixture '.ssh/mgremote/own.conf').Replace('\','/')
    $otherInclude = (Join-Path $profileFixture '.ssh/other/other.conf').Replace('\','/')
    $sshOriginal = 'Include "' + $ownInclude + '"' + "`r`n" + 'Include "' + $otherInclude + '"' + "`r`nHost other`r`n    HostName preserved.example.com`r`n"
    $cleanupConfig = Join-Path $profileFixture '.ssh/config'
    [IO.File]::WriteAllText($cleanupConfig, $sshOriginal)
    $ownBackup = Join-Path $profileFixture ('.ssh/config.mgremote-' + [Guid]::NewGuid().ToString('N') + '.bak')
    [IO.File]::WriteAllText($ownBackup, 'SYNTHETIC MGREMOTE BACKUP')
    $manualBackup = Join-Path $profileFixture '.ssh/config.manual.bak'
    [IO.File]::WriteAllText($manualBackup, 'SYNTHETIC PERSONAL BACKUP')
    [mRemoteNG.Security.UserDataCleanup]::RemoveProfileData($profileFixture)
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $profileFixture 'AppData/Roaming/MGRemote'))) 'Purge removes MGRemote saved connections/settings'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $profileFixture 'AppData/Local/MGRemote'))) 'Purge removes MGRemote temporary keys'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $profileFixture 'AppData/Local/MGRemote contributors/MGRemote.exe_Url_test'))) 'Purge removes MGRemote user.config directory'
    Assert-True ((Test-Path -LiteralPath (Join-Path $profileFixture 'AppData/Roaming/mRemoteNG/synthetic.txt')) -and (Test-Path -LiteralPath (Join-Path $profileFixture 'AppData/Local/MGRemote contributors/AnotherApp/synthetic.txt'))) 'Purge preserves other applications'
    Assert-True ((Test-Path -LiteralPath $sharedKey) -and (Test-Path -LiteralPath $manualBackup)) 'Purge preserves personal SSH keys and manual backups'
    $remainingConfig = [IO.File]::ReadAllText($cleanupConfig)
    Assert-True (-not $remainingConfig.Contains($ownInclude) -and $remainingConfig.Contains($otherInclude) -and $remainingConfig.Contains('Host other')) 'Purge removes only MGRemote Include lines'
    Assert-True ((-not (Test-Path -LiteralPath (Join-Path $profileFixture '.ssh/mgremote'))) -and (-not (Test-Path -LiteralPath $ownBackup))) 'Purge removes generated profiles and MGRemote backups'
    [mRemoteNG.Security.UserDataCleanup]::RemoveProfileData($profileFixture)
    Assert-True (Test-Path -LiteralPath $sharedKey) 'Repeated purge is safe'
    $outside = Join-Path $fixture 'outside'
    New-Item -ItemType Directory -Path $outside -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $outside 'preserve.txt'),'SYNTHETIC OUTSIDE DATA')
    $junction = Join-Path $profileFixture 'AppData/Roaming/MGRemote'
    New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null
    try {
        Assert-Rejected { [mRemoteNG.Security.UserDataCleanup]::RemoveProfileData($profileFixture) } 'Purge refuses a junction before deleting data'
        Assert-True (Test-Path -LiteralPath (Join-Path $outside 'preserve.txt')) 'Purge never follows a junction to outside data'
    } finally { Remove-Item -LiteralPath $junction -Force }
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $allowedParent = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedFixture.StartsWith($allowedParent, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolvedFixture -Leaf) -match '^test-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    } else { throw 'Refusing cleanup outside the test artifacts directory.' }
}
Write-Output "PASS: $script:checks targeted security checks (policy compiled; Windows key ACL and cleanup exercised)."

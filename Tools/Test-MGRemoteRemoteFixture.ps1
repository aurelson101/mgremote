# Disposable CI-only SSH/RDP lab. Never modifies a user's server.
[CmdletBinding()]
param([ValidateSet('Start','Stop')][string]$Action='Start')
$ErrorActionPreference='Stop'
if (!$IsWindows -or $env:GITHUB_ACTIONS -ne 'true') { throw 'Disposable GitHub Windows runner required.' }
$stateFile=Join-Path $env:RUNNER_TEMP 'mgremote-remote-fixture.json'
function Save-State {
    if (!(Test-Path $stateFile)) {
        New-Item -ItemType File $stateFile | Out-Null
        $a=[Security.AccessControl.FileSecurity]::new();$a.SetAccessRuleProtection($true,$false)
        foreach ($id in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18','S-1-5-32-544')) { $a.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($id),'FullControl','Allow')) }
        Set-Acl $stateFile $a
    }
    $script:state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $stateFile
}
function Add-FixtureGroup([string]$sid) {
    $name=([Security.Principal.SecurityIdentifier]::new($sid).Translate([Security.Principal.NTAccount]).Value -split '\\')[-1]
    $group=[ADSI]("WinNT://$env:COMPUTERNAME/$name,group")
    if (!$group.IsMember("WinNT://$env:COMPUTERNAME/$user,user")) { $group.Add("WinNT://$env:COMPUTERNAME/$user,user") }
}
function Free-Port { $l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$l.Start();$p=$l.LocalEndpoint.Port;$l.Stop();return $p }
if ($Action -eq 'Stop') {
    if (!(Test-Path $stateFile)) { return }
    $s=Get-Content $stateFile -Raw | ConvertFrom-Json -AsHashtable
    if ($s.User -notmatch '^mgfixture[a-f0-9]{8}$' -or !$s.Root.StartsWith((Join-Path $env:RUNNER_TEMP 'mgremote-lab-'))) { throw 'Unexpected fixture identity.' }
    if ($s.RdpChanged) {
        $tcp='HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp'
        Set-ItemProperty $tcp PortNumber $s.OldPort
        Set-ItemProperty $tcp UserAuthentication $s.OldUserAuthentication
        Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' fDenyTSConnections $s.OldDeny
        if ($s.OldCert) { Set-ItemProperty $tcp SSLCertificateSHA1Hash ([byte[]]$s.OldCert) } else { Remove-ItemProperty $tcp SSLCertificateSHA1Hash -ErrorAction SilentlyContinue }
        Restart-Service TermService -Force
        if ($s.OldServiceStatus -ne "Running") { Stop-Service TermService -Force }
        Set-Service TermService -StartupType $s.OldStartType
    }
    if ($s.Task) { Stop-ScheduledTask -TaskName $s.Task -ErrorAction SilentlyContinue;Unregister-ScheduledTask -TaskName $s.Task -Confirm:$false -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($s.Root,[StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($rule in @($s.Firewall)) { Remove-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue }
    if ($s.Cert) {
        Remove-Item ('Cert:\CurrentUser\Root\'+$s.Cert) -ErrorAction SilentlyContinue
        Remove-Item ('Cert:\LocalMachine\My\'+$s.Cert) -DeleteKey -ErrorAction SilentlyContinue
    }
    $sessions = try { & quser.exe $s.User 2>$null } catch { @() }
    foreach ($line in $sessions) { if ($line -match ('^\s*>?\s*'+[regex]::Escape($s.User)+'\s+(?:\S+\s+)?(?<id>\d+)\s+(?:Active|Disc)')) { & logoff.exe $Matches.id } }
    ([ADSI]("WinNT://$env:COMPUTERNAME")).Delete('user',$s.User)
    Get-CimInstance Win32_UserProfile | Where-Object { $_.SID -eq $s.Sid -and !$_.Loaded } | Remove-CimInstance -ErrorAction SilentlyContinue
    if (Test-Path $s.Root) { Remove-Item -LiteralPath $s.Root -Recurse -Force }
    Remove-Item -LiteralPath $stateFile
    return
}
if (Test-Path $stateFile) { throw 'Fixture state already exists.' }
$id=[guid]::NewGuid().ToString('N');$root=Join-Path $env:RUNNER_TEMP ('mgremote-lab-'+$id)
New-Item -ItemType Directory $root | Out-Null
$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18','S-1-5-32-544')) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
}
Set-Acl -LiteralPath $root -AclObject $acl
$password=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))+'Aa1!'
$user='mgfixture'+$id.Substring(0,8)
$account=([ADSI]("WinNT://$env:COMPUTERNAME")).Create('user',$user)
$account.SetPassword($password);$account.Put('UserFlags',0x10200);$account.SetInfo()
$userSid=[Security.Principal.NTAccount]::new($env:COMPUTERNAME,$user).Translate([Security.Principal.SecurityIdentifier]).Value
Add-FixtureGroup 'S-1-5-32-545'
$script:state=@{Root=$root;User=$user;Sid=$userSid;Task=('MGRemoteFixture-'+$id);SshPort=(Free-Port);RdpAvailable=$false;Firewall=@()}
Save-State
$arch=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
$zipName=if ($arch -eq 'Arm64') {'OpenSSH-ARM64.zip'} else {'OpenSSH-Win64.zip'}
$hash=if ($arch -eq 'Arm64') {'d2b1928225479fec9e0c938f527fdb5ed676446cca1bbcc5af84aca1f272842e'} else {'b67ff8becd203502190ec42ac7787c24c2cd3325ed7700485fe7a1c83d2a7957'}
$zip=Join-Path $root 'ssh.zip'
Invoke-WebRequest ('https://github.com/PowerShell/Win32-OpenSSH/releases/download/10.4.0.0p1-Preview/'+$zipName) -OutFile $zip
if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'OpenSSH fixture digest mismatch.' }
Expand-Archive -LiteralPath $zip -DestinationPath $root
$bin=(Get-ChildItem $root -Filter sshd.exe -Recurse | Select-Object -First 1).DirectoryName
$keygen=Join-Path $bin 'ssh-keygen.exe'
foreach ($name in @('host','client')) { & $keygen -q -t ed25519 -N '' -f (Join-Path $root $name);if ($LASTEXITCODE -ne 0) { throw 'Fixture key generation failed.' } }
$hostAcl=[Security.AccessControl.FileSecurity]::new();$hostAcl.SetAccessRuleProtection($true,$false)
$hostAcl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
foreach ($sid in @('S-1-5-18','S-1-5-32-544')) { $hostAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','Allow')) }
Set-Acl (Join-Path $root 'host') $hostAcl
$binAcl=Get-Acl $bin
$binAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($userSid),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'));Set-Acl $bin $binAcl
$remote=Join-Path $root 'remote';New-Item -ItemType Directory $remote | Out-Null
$remoteAcl=Get-Acl $remote;$remoteAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($userSid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'));Set-Acl $remote $remoteAcl
$state.ScpExecutable=Join-Path $bin 'scp.exe';$state.RemotePath=$remote.Replace('\','/');Save-State

$r=$root.Replace('\','/');$b=$bin.Replace('\','/')
@"
Port $($state.SshPort)
ListenAddress 127.0.0.1
HostKey "$r/host"
PidFile "$r/sshd.pid"
AuthorizedKeysFile "$r/client.pub"
StrictModes no
PasswordAuthentication no
PubkeyAuthentication yes
AllowUsers $user
Subsystem sftp "$b/sftp-server.exe"
"@ | Set-Content (Join-Path $root 'sshd.conf') -Encoding ascii
$parts=(Get-Content (Join-Path $root 'host.pub')).Split(' ')
"[127.0.0.1]:$($state.SshPort) $($parts[0]) $($parts[1])" | Set-Content (Join-Path $root 'known_hosts') -Encoding ascii
"Host *`n UserKnownHostsFile `"$r/known_hosts`"`n GlobalKnownHostsFile NUL`n BatchMode yes`n StrictHostKeyChecking yes`n ForwardAgent no`n PermitLocalCommand no`n" | Set-Content (Join-Path $root 'client.conf') -Encoding ascii
$state.Executable=Join-Path $bin 'ssh.exe'
$state.Arguments='-F "'+(Join-Path $root 'client.conf')+'" -i "'+(Join-Path $root 'client')+'" -p '+$state.SshPort+' '+$user+'@127.0.0.1'
Save-State
$taskAction=New-ScheduledTaskAction -Execute (Join-Path $bin 'sshd.exe') -Argument ('-D -f "'+(Join-Path $root 'sshd.conf')+'" -E "'+(Join-Path $root 'sshd.log')+'"') -WorkingDirectory $bin
Register-ScheduledTask -TaskName $state.Task -Action $taskAction -Principal (New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest) | Out-Null
Start-ScheduledTask -TaskName $state.Task
$deadline=[DateTime]::UtcNow.AddSeconds(20);$listening=$false
while (!$listening -and [DateTime]::UtcNow -lt $deadline) {
    try { $c=[Net.Sockets.TcpClient]::new();$c.Connect('127.0.0.1',$state.SshPort);$c.Dispose();$listening=$true } catch { Start-Sleep -Milliseconds 200 }
}
if (!$listening) { if (Test-Path (Join-Path $root 'sshd.log')) { Get-Content (Join-Path $root 'sshd.log') -Tail 15 | Write-Host }; throw 'Isolated SSH fixture failed to listen.' }
# Windows Server supports a separate administrative desktop; client Windows remains single-session.
if ((Get-CimInstance Win32_OperatingSystem).ProductType -ne 1) {
    if (@(Get-NetFirewallProfile | Where-Object { !$_.Enabled }).Count) { throw 'RDP fixture requires the Windows firewall.' }
    Add-FixtureGroup 'S-1-5-32-544'
    $tcp='HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp'
    $old=Get-ItemProperty $tcp
    $state.OldUserAuthentication=$old.UserAuthentication;$state.OldServiceStatus=(Get-Service TermService).Status.ToString();$state.OldStartType=(Get-Service TermService).StartType.ToString()
    $state.OldPort=$old.PortNumber;$state.OldCert=$old.SSLCertificateSHA1Hash
    $state.OldDeny=(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server').fDenyTSConnections
    $state.RdpPort=Free-Port
    $block='MGRemoteBlock-'+$id;$allow='MGRemoteLoopback-'+$id;$state.Firewall=@($block,$allow);Save-State
    New-NetFirewallRule -Name $block -DisplayName $block -Direction Inbound -Action Block -Protocol TCP -LocalPort $state.RdpPort -RemoteAddress @('0.0.0.0-126.255.255.255','128.0.0.0-255.255.255.255','::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff') | Out-Null
    New-NetFirewallRule -Name $allow -DisplayName $allow -Direction Inbound -Action Allow -Protocol TCP -LocalPort $state.RdpPort -RemoteAddress @('127.0.0.1','::1') | Out-Null
    $cert=New-SelfSignedCertificate -DnsName localhost -CertStoreLocation Cert:\LocalMachine\My -Provider 'Microsoft RSA SChannel Cryptographic Provider' -KeySpec KeyExchange -KeyLength 2048 -NotAfter (Get-Date).AddHours(2)
    $state.Cert=$cert.Thumbprint;Save-State
    $keyName=$cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
    if (!$keyName) { throw 'The fixture certificate has no CSP private-key container.' }
    $keyFile=Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\'+$keyName)
    if (!(Test-Path -LiteralPath $keyFile -PathType Leaf)) { throw 'The fixture private-key file is missing.' }
    $keyAcl=Get-Acl $keyFile;$keyAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-20'),'Read','Allow'));Set-Acl $keyFile $keyAcl
    $store=[Security.Cryptography.X509Certificates.X509Store]::new('Root','CurrentUser');$store.Open('ReadWrite');$store.Add([Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData));$store.Close()
    $state.RdpChanged=$true;Save-State
    Set-ItemProperty $tcp PortNumber $state.RdpPort
    Set-ItemProperty $tcp UserAuthentication 1
    Get-CimInstance -Namespace root/cimv2/terminalservices -ClassName Win32_TSGeneralSetting -Filter "TerminalName='RDP-tcp'" | Set-CimInstance -Property @{SSLCertificateSHA1Hash=$cert.Thumbprint} | Out-Null
    Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' fDenyTSConnections 0
    if ((Get-Service TermService).StartType -eq "Disabled") { Set-Service TermService -StartupType Manual }
    Restart-Service TermService -Force
    $state.RdpAvailable=$true;$state.RdpPassword=$password;$state.RdpDomain=$env:COMPUTERNAME;Save-State
}
"MGREMOTE_REMOTE_FIXTURE=$stateFile" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
Write-Host 'Isolated loopback SSH fixture ready; RDP is enabled only on the server image. No production endpoint was modified.'

# Build an isolated AppImage; does not install software or alter the Windows fork.
[CmdletBinding()]
param([string]$SquashfsTool, [string]$Version = '0.2.11')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
$root = Split-Path $PSScriptRoot -Parent
if (!$SquashfsTool) {
    $SquashfsTool = if ($IsWindows) { Join-Path $root 'artifacts/linux-tools/usr/bin/mksquashfs.exe' } else { '/usr/bin/mksquashfs' }
}
if (!(Test-Path -LiteralPath $SquashfsTool)) { throw 'Provide -SquashfsTool pointing to a trusted mksquashfs executable.' }
$release = Join-Path $root ('artifacts/linux/' + $Version + '/' + [guid]::NewGuid().ToString('N'))
$appdir = Join-Path $release 'MGRemote.AppDir'
$binary = Join-Path $appdir 'usr/bin'
New-Item -ItemType Directory -Force $binary | Out-Null
& dotnet publish (Join-Path $PSScriptRoot 'MGRemote.Linux.csproj') -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=false -o $binary
if ($LASTEXITCODE -ne 0) { throw 'Linux publish failed.' }
& python3 (Join-Path $PSScriptRoot 'Bundle-Terminal.py') $binary
if ($LASTEXITCODE -ne 0) { throw 'Terminal bundle failed.' }
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $binary 'mgremote-askpass'), (Get-Content (Join-Path $PSScriptRoot 'mgremote-askpass') -Raw).Replace("`r`n","`n"), $utf8)
$launcher = @'
#!/bin/sh
set -eu
APPDIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
export DOTNET_ROOT="$APPDIR/usr/bin"
export DOTNET_MULTILEVEL_LOOKUP=0
exec "$APPDIR/usr/bin/MGRemoteLinux" "$@"
'@
[IO.File]::WriteAllText((Join-Path $appdir 'AppRun'), $launcher.Replace("`r`n","`n") + "`n", $utf8)
[IO.File]::WriteAllText((Join-Path $appdir 'mgremote.desktop'), "[Desktop Entry]`nType=Application`nName=MGRemote Linux (Prototype)`nExec=MGRemoteLinux`nIcon=mgremote`nCategories=Network;RemoteAccess;`nTerminal=false`n", $utf8)
[IO.File]::WriteAllText((Join-Path $appdir 'mgremote.svg'), '<svg xmlns="http://www.w3.org/2000/svg" width="128" height="128"><rect width="128" height="128" rx="24" fill="#17283d"/><path d="M24 39h80v52H24z" fill="none" stroke="#69c9ff" stroke-width="7"/><path d="m40 55 12 10-12 10m25 0h20" fill="none" stroke="white" stroke-width="6"/></svg>', $utf8)
Copy-Item -LiteralPath (Join-Path $root 'COPYING.txt'), (Join-Path $PSScriptRoot 'README.md') -Destination $appdir
& dotnet list (Join-Path $PSScriptRoot 'MGRemote.Linux.csproj') package --include-transitive --format json | Set-Content -LiteralPath (Join-Path $appdir 'dependencies.json')
if ($LASTEXITCODE -ne 0) { throw 'Dependency inventory failed.' }
$notices = Join-Path $appdir 'licenses'
New-Item -ItemType Directory -Force $notices | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'Licenses/*') -Destination $notices
$assets = Get-Content (Join-Path $PSScriptRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json
$cache = @($assets.packageFolders.PSObject.Properties)[0].Name
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $package = Join-Path $cache $library.Value.path
    foreach ($notice in @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match 'license|copying|notice' })) {
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $notices ($library.Name.Replace('/','-') + '-' + $notice.Name))
    }
}
$runtime = Join-Path $release 'runtime-x86_64'
Invoke-WebRequest 'https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64' -OutFile $runtime
$runtimeHash = '617aa77314d57c8b8e4fae9c3f0b35af7b30d5529cb69e98f64f92510ed98a46'
if ((Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -ne $runtimeHash) { throw 'AppImage runtime changed: verify a new trusted upstream digest before updating the pin.' }
$squash = Join-Path $release 'filesystem.squashfs'
& $SquashfsTool $appdir $squash -noappend -no-xattrs -all-root -comp gzip -p 'AppRun m 755 0 0' -p 'usr/bin/MGRemoteLinux m 755 0 0' -p 'usr/bin/mgremote-askpass m 755 0 0'
if ($LASTEXITCODE -ne 0) { throw 'SquashFS creation failed.' }
$image = Join-Path $release "MGRemote-Linux-$Version-x86_64.AppImage"
$output = [IO.File]::Open($image, [IO.FileMode]::CreateNew)
try {
    foreach ($part in @($runtime,$squash)) { $stream = [IO.File]::OpenRead($part); try { $stream.CopyTo($output) } finally { $stream.Dispose() } }
} finally { $output.Dispose() }
if (!$IsWindows) { & chmod +x -- $image; if ($LASTEXITCODE -ne 0) { throw 'Cannot mark AppImage executable.' } }
$source = Join-Path $release "MGRemote-Linux-$Version-source.zip"
$sourceTree = Join-Path $release 'source-tree'
$linuxSource = Join-Path $sourceTree 'Linux'
New-Item -ItemType Directory -Force $linuxSource | Out-Null
foreach ($name in @('MGRemote.Linux.csproj','Connections.cs','CredentialBroker.cs','mgremote-askpass','XmlSecrets.cs','MainWindow.Secrets.cs','Program.cs','MainWindow.Sessions.cs','MainWindow.Tree.cs','MainWindow.TreeState.cs','MainWindow.Desktop.cs','MainWindow.Configuration.cs','MainWindow.Appearance.cs','DesktopAppearance.cs','MainWindow.Profile.cs','ProfileEncryption.cs','ProfileTransfer.cs','ProfileTransferTests.cs','MainWindow.ProfileTransfer.cs','MainWindow.ProfileTransfer.Tests.cs','ProfileEncryptionTests.cs','ConnectionIcons.cs','ConnectionSettingsTests.cs','EmbeddedSession.cs','FileTransfer.cs','FileTransferPane.cs','SelfTest.cs','Test-Serial.py','Bundle-Terminal.py','TerminalPackages.json','Build-AppImage.ps1','README.md','Licenses')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $linuxSource -Recurse
}
Copy-Item -LiteralPath (Join-Path $root 'COPYING.txt') -Destination $sourceTree
Compress-Archive -Path (Join-Path $sourceTree '*') -DestinationPath $source
@{ Product='MGRemote Linux'; Version=$Version; Architecture='x86_64'; Prototype=$true; Signed=$false; BuiltOn=[Runtime.InteropServices.RuntimeInformation]::OSDescription; LinuxExecutionTested=$false; LiveRemoteSessionsTested=$false; AppImage=(Split-Path $image -Leaf); SHA256=(Get-FileHash $image -Algorithm SHA256).Hash; RuntimeSHA256=$runtimeHash; Source=(Split-Path $source -Leaf); SourceSHA256=(Get-FileHash $source -Algorithm SHA256).Hash } | ConvertTo-Json | Set-Content (Join-Path $release 'manifest.json')
Write-Host "AppImage: $image"
Write-Host 'PROTOTYPE: Linux execution and real remote sessions still require validation.'

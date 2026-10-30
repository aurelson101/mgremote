# MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
# Creates an isolated, self-contained Windows MSI, source archive and SHA-256 manifest.
[CmdletBinding()]
param(
    [ValidateSet('x64','arm64')][string]$Platform = 'x64',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.13',
    [ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$versionNumber = [version]$Version
if ($versionNumber.Major -gt 255 -or $versionNumber.Minor -gt 255 -or $versionNumber.Build -gt 65535) {
    throw 'MSI version fields must be at most 255.255.65535.'
}
& (Join-Path $PSScriptRoot 'Test-MGRemoteSecurity.ps1')
& (Join-Path $PSScriptRoot 'Build-MGRemote.ps1') -Platform $Platform -Publish
$artifacts = Join-Path $root 'artifacts'
$build = Get-Content -LiteralPath (Join-Path $artifacts "build-$Platform.json") -Raw | ConvertFrom-Json
if (-not $build.SelfContained -or $build.Platform -ne $Platform) { throw 'A matching self-contained build is required.' }
$payload = [IO.Path]::GetFullPath($build.OutputDirectory)
if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant() -eq $Platform) {
    & dotnet run --project (Join-Path $root 'Tools/PanelSmoke/PanelSmoke.csproj') -- $payload
    if ($LASTEXITCODE -ne 0) { throw 'Connection panel startup regression test failed.' }
    & dotnet run --project (Join-Path $root 'Tools/ImportSmoke/ImportSmoke.csproj') -- $payload $artifacts
    if ($LASTEXITCODE -ne 0) { throw 'XML/RDP connection import regression test failed.' }
    & dotnet run --project (Join-Path $root 'Tools/GuiSmoke/GuiSmoke.csproj') "-p:MGRemotePayload=$payload" -- $payload $artifacts
    if ($LASTEXITCODE -ne 0) { throw 'GUI lifecycle/layout regression test failed.' }
} else { Write-Warning 'Connection-panel smoke test skipped for a different target architecture.' }
$allowed = [IO.Path]::GetFullPath((Join-Path $artifacts "publish/win-$Platform")) + [IO.Path]::DirectorySeparatorChar
if (-not $payload.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'The payload must be a fresh MGRemote publish directory.' }
foreach ($required in @('MGRemote.exe','MGRemote.dll','MGRemote.runtimeconfig.json','hostfxr.dll','coreclr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) { throw "Incomplete self-contained payload: $required" }
}
$files = Get-ChildItem -LiteralPath $payload -File -Recurse
if (@($files | Where-Object { $_.Extension -match '^\.(ps1|bat|cmd|ppk|pem|key|pfx|p12|db|sqlite|sqlite3)$' -or $_.Name -eq 'confCons.xml' -or $_.Name -eq 'user.config' }).Count -gt 0) {
    throw 'The payload contains a script or credential/data file. Packaging has been stopped.'
}
Copy-Item -LiteralPath (Join-Path $root 'COPYING.txt') -Destination (Join-Path $payload 'COPYING.txt')
Copy-Item -LiteralPath (Join-Path $root 'MGREMOTE-WINDOWS.md') -Destination (Join-Path $payload 'MGREMOTE-WINDOWS.md')
Copy-Item -LiteralPath (Join-Path $artifacts 'dependencies.json') -Destination (Join-Path $payload 'dependencies.json')
if ($CertificateThumbprint) {
    & (Join-Path $PSScriptRoot 'Sign-MGRemoteRelease.ps1') -Platform $Platform -BuiltDirectory $payload -CertificateThumbprint $CertificateThumbprint
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
$releaseDirectory = Join-Path $artifacts ("installers/$Version/" + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
& $msbuild (Join-Path $root 'Installer/MGRemote.wixproj') /restore /t:Rebuild /p:Configuration=Release "/p:Platform=$Platform" "/p:MGRemoteVersion=$Version" "/p:PublishDir=$payload" "/p:OutputPath=$releaseDirectory/" /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed. No release is approved.' }
$installer = Join-Path $releaseDirectory "MGRemote-$Version-win-$Platform-preview.msi"
if (-not (Test-Path -LiteralPath $installer)) { throw 'The expected MSI was not produced.' }
if ($CertificateThumbprint) {
    $certificate = Get-Item -LiteralPath ("Cert:\CurrentUser\My\" + $CertificateThumbprint)
    $signature = Set-AuthenticodeSignature -LiteralPath $installer -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
    if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) { throw 'MSI signature validation failed.' }
}
& (Join-Path $PSScriptRoot 'Test-MGRemoteInstaller.ps1') -PackagePath $installer
# Supply the exact working source, including local modifications, without .git/build outputs.
# Untracked files are explicitly allowlisted; a later .env/private key cannot silently enter the archive.
$sourceFiles = @(& git -C $root -c core.quotepath=false ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the corresponding source.' }
$sourceFiles += @('MGREMOTE-WINDOWS.md', 'Tools/Build-MGRemote.ps1', 'Tools/Build-MGRemotePackage.ps1',
    'Tools/Configure-MGRemoteSsh.ps1', 'Tools/Sign-MGRemoteRelease.ps1', 'Tools/Test-MGRemoteSecurity.ps1', 'Tools/Test-MGRemoteInstaller.ps1', 'Tools/PanelSmoke/PanelSmoke.csproj', 'Tools/PanelSmoke/Program.cs', 'Tools/ImportSmoke/ImportSmoke.csproj', 'Tools/ImportSmoke/Program.cs', 'Tools/GuiSmoke/GuiSmoke.csproj', 'Tools/GuiSmoke/Program.cs', 'mRemoteNG/UI/DesktopAppearance.cs',
    'mRemoteNG/Security/TemporarySshKey.cs', 'mRemoteNG/Security/WindowsSecurityPolicy.cs', 'mRemoteNG/Security/SshProfileStore.cs', 'mRemoteNG/Security/UserDataCleanup.cs',
    'mRemoteNG/UI/Forms/SshProfileForm.cs', 'mRemoteNG/UI/Forms/NewConnectionForm.cs', 'mRemoteNG/UI/Forms/AnyDeskForm.cs',
    'mRemoteNG/UI/WindowPlacement.cs', 'mRemoteNG/UI/Controls/SessionConsole.cs', 'mRemoteNG/UI/Controls/SshTerminal.cs', 'mRemoteNG/UI/Controls/PseudoTerminal.cs', 'mRemoteNG/Security/AnyDeskLauncher.cs',
    'mRemoteNG/Connection/SshIdentityPropertyDescriptor.cs', 'mRemoteNG/Connection/OpenSshTunnel.cs', 'mRemoteNG/Security/SshFileTransfer.cs',
    'mRemoteNG/UI/Forms/RdpSharingForm.cs', 'mRemoteNG/UI/Forms/SshFileTransferForm.cs', 'Installer/MGRemote.wixproj', 'Installer/MGRemote.wxs')
$sourceFiles += @(Get-ChildItem -LiteralPath (Join-Path $root 'mRemoteNG/UI/Controls/TerminalAssets') -File | ForEach-Object { 'mRemoteNG/UI/Controls/TerminalAssets/' + $_.Name })
$sourceArchive = Join-Path $releaseDirectory "MGRemote-$Version-source.zip"
Add-Type -AssemblyName System.IO.Compression
$zipStream = [IO.File]::Open($sourceArchive, [IO.FileMode]::CreateNew)
try {
    $zip = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($relative in ($sourceFiles | Sort-Object -Unique)) {
            $source = Join-Path $root $relative
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
            if ([IO.Path]::GetExtension($source) -match '^\.(pfx|p12|ppk|pem|key)$' -or [IO.Path]::GetFileName($source) -eq '.env') {
                throw 'A source path requires manual secret review. No source archive is approved.'
            }
            $entry = $zip.CreateEntry($relative.Replace('\','/'), [IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            $sourceStream = [IO.File]::OpenRead($source)
            try { $sourceStream.CopyTo($entryStream) } finally { $sourceStream.Dispose(); $entryStream.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $zipStream.Dispose() }
$manifest = [ordered]@{
    Product='MGRemote'; Version=$Version; Platform=$Platform; BaseCommit=$build.BaseCommit
    Preview=$true; InstallerSigned=[bool]$CertificateThumbprint; ExecutableSigned=[bool]$CertificateThumbprint; BuiltUtc=[DateTime]::UtcNow.ToString('o')
    Installer=(Split-Path $installer -Leaf); InstallerSHA256=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    Source=(Split-Path $sourceArchive -Leaf); SourceSHA256=(Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash
    LiveRdpSshValidated=$false
}
$manifestPath = Join-Path $releaseDirectory "MGRemote-$Version-win-$Platform-manifest.json"
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "MSI: $installer"
Write-Host "Corresponding source: $sourceArchive"
Write-Host "SHA-256 manifest: $manifestPath"
if (-not $CertificateThumbprint) { Write-Warning 'This is an unsigned preview. Real RDP/SSH and clean-machine install/uninstall tests are still required.' }

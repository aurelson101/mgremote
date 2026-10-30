# MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
# Uses an existing certificate in the Windows store; never exports private keys.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint,
    [ValidateSet('x64','arm64')][string]$Platform = 'x64',
    [string]$BuiltDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$outputDirectory = if ($BuiltDirectory) { [IO.Path]::GetFullPath($BuiltDirectory) } else { Join-Path $root "mRemoteNG/bin/$Platform/Release" }
$exe = Join-Path $outputDirectory 'MGRemote.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build the application first.' }
$certificate = Get-Item -LiteralPath ("Cert:\CurrentUser\My\" + $CertificateThumbprint)
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {
    throw 'A currently valid code-signing certificate with its private key is required.'
}
if (@($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -eq 0) {
    throw 'The certificate must explicitly allow Code Signing.'
}
if ($certificate.Subject -eq $certificate.Issuer) { throw 'A self-signed certificate is not accepted for release signing.' }
# Sign only this fork's compiled assemblies, never replace a third-party publisher signature.
$ownBinaries = @('MGRemote.exe','MGRemote.dll','ExternalConnectors.dll','ObjectListView.dll','Assemblies/mRp.Contracts.dll')
foreach ($relativePath in $ownBinaries) {
    $path = Join-Path $outputDirectory $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing application binary: $relativePath" }
}
foreach ($relativePath in $ownBinaries) {
    $path = Join-Path $outputDirectory $relativePath
    $signature = Set-AuthenticodeSignature -LiteralPath $path -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
    if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate) { throw "Signature or timestamp validation failed: $relativePath" }
    $verified = Get-AuthenticodeSignature -LiteralPath $path
    if ($verified.Status -ne 'Valid' -or $verified.SignerCertificate.Thumbprint -ne $CertificateThumbprint) { throw "Signer verification failed: $relativePath" }
}
Write-Host 'MGRemote executable and application assemblies signed and verified. Third-party dependencies are unchanged; MSI signing is handled by the package builder.'

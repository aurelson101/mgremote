# Real install/repair/uninstall, strictly limited to disposable GitHub Windows runners.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath, [switch]$AllowDisposableRunner)
$ErrorActionPreference = 'Stop'
if (!$AllowDisposableRunner -or $env:GITHUB_ACTIONS -ne 'true' -or !$IsWindows) { throw 'This test is restricted to explicitly authorized disposable Windows CI runners.' }
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($PackagePath, 0)
function Property([string]$name) {
    $view = $db.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''' + $name + '''')
    $null = $view.Execute(); $record = $view.Fetch()
    $value = if ($null -ne $record) { $record.StringData(1) } else { $null }
    $null = $view.Close(); return $value
}
if ((Property 'ProductName') -ne 'MGRemote (Preview)' -or (Property 'UpgradeCode') -ne '{B2E9F552-D411-4719-A829-164CE19B58A0}') { throw 'Unexpected package identity.' }
$productCode = Property 'ProductCode'
if ($productCode -notmatch '^\{[A-Fa-f0-9-]{36}\}$' -or $installer.ProductState($productCode) -ne -1 -or (Test-Path 'HKLM:\SOFTWARE\MGRemote')) { throw 'The runner already contains an MGRemote installation.' }
$root = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('mgremote-install-' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $testRoot 'Application'
New-Item -ItemType Directory $testRoot | Out-Null
$testUserData = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'MGRemote'
New-Item -ItemType Directory -Force $testUserData | Out-Null
$sentinel = Join-Path $testUserData ('installer-fixture-' + [guid]::NewGuid().ToString('N') + '.txt')
$nonce = [guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($sentinel, $nonce)
$success = $false; $rebootRequired = $false
function Run-Msi([string[]]$Arguments, [string]$Stage) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:WINDIR 'System32/msiexec.exe'))
    $start.UseShellExecute = $false
    foreach ($arg in $Arguments) { $start.ArgumentList.Add($arg) }
    foreach ($arg in @('/qn','/norestart','/l*v',(Join-Path $testRoot ($Stage + '.log')))) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(180000)) { $process.Kill(); throw "MSI $Stage timed out." }
        if ($process.ExitCode -notin @(0,3010)) { throw "MSI $Stage failed with code $($process.ExitCode). See $Stage.log." }
        if ($process.ExitCode -eq 3010) { $script:rebootRequired = $true }
    } finally { $process.Dispose() }
}
try {
    Run-Msi @('/i',$PackagePath,('INSTALLFOLDER=' + $install)) 'install'
    if (!(Test-Path (Join-Path $install 'MGRemote.exe')) -or $installer.ProductState($productCode) -ne 5) { throw 'The installed product is incomplete or unregistered.' }
    & dotnet run --project (Join-Path $root 'Tools/PanelSmoke/PanelSmoke.csproj') -- $install
    if ($LASTEXITCODE -ne 0) { throw 'The installed application panel smoke test failed.' }
    $license = Join-Path $install 'COPYING.txt'
    if (!(Test-Path -LiteralPath $license)) { throw 'The installed license is missing.' }
    Remove-Item -LiteralPath $license
    Run-Msi @('/fa',$productCode) 'repair'
    if (!(Test-Path -LiteralPath $license) -or [IO.File]::ReadAllText($sentinel) -ne $nonce) { throw 'Repair did not restore the installed file or changed user data.' }
    Run-Msi @('/x',$productCode,'PURGEUSERDATA=0') 'uninstall'
    if ((Test-Path (Join-Path $install 'MGRemote.exe')) -or $installer.ProductState($productCode) -ne -1) { throw 'Uninstall retained the product executable or registration.' }
    if ([IO.File]::ReadAllText($sentinel) -ne $nonce) { throw 'Uninstall removed or changed user data.' }
    $success = $true
} finally {
    if ($installer.ProductState($productCode) -eq 5) { Run-Msi @('/x',$productCode,'PURGEUSERDATA=0') 'cleanup-uninstall' }
    Remove-Item -LiteralPath $sentinel -ErrorAction SilentlyContinue
    $output = Split-Path $PackagePath -Parent
    foreach ($log in Get-ChildItem -LiteralPath $testRoot -Filter '*.log') { Copy-Item -LiteralPath $log.FullName -Destination (Join-Path $output ('install-cycle-' + $log.Name)) }
    @{ Result=$(if ($success) {'PASS'} else {'FAIL'}); ProductCode=$productCode
       Architecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
       FreshProductInstallTested=$success; InstalledPanelStartupTested=$success; RepairTested=$success
       UninstallTested=$success; UserDataPreserved=$success; PristineOsTested=$false; RebootRequired=$rebootRequired
       InstallerSHA256=(Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'install-cycle-verification.json') -Encoding UTF8
}
Write-Host 'PASS: actual fresh-product MSI install, installed panel startup, repair, uninstall and user-data preservation on a disposable runner.'

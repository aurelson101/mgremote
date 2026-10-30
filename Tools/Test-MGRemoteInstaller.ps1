# MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
# Read-only MSI session: AppSearch and condition evaluation only. Does not install the application.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$installer = New-Object -ComObject WindowsInstaller.Installer
$installer.UILevel = 2
$database = $installer.OpenDatabase($PackagePath, 0)
function Read-MsiProperty([string]$Name) {
    $view = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''' + $Name + '''')
    $null = $view.Execute()
    $record = $view.Fetch()
    $value = if ($null -ne $record) { $record.StringData(1) } else { $null }
    $null = $view.Close()
    return $value
}
$product = Read-MsiProperty 'ProductName'
if ($product -ne 'MGRemote (Preview)' -or (Read-MsiProperty 'UpgradeCode') -ne '{B2E9F552-D411-4719-A829-164CE19B58A0}') { throw 'Unexpected MSI identity.' }
$view = $database.OpenView('SELECT `Condition` FROM `LaunchCondition`')
$null = $view.Execute()
$condition = $null
while ($null -ne ($record = $view.Fetch())) {
    $value = $record.StringData(1)
    if ($value.Contains('MGREMOTE_WINDOWS_BUILD')) { $condition = $value }
}
$null = $view.Close()
if (-not $condition -or $condition.Contains('WindowsBuild')) { throw 'The actual OS registry build must determine compatibility.' }
$view = $database.OpenView('SELECT `FileName` FROM `File`')
$null = $view.Execute()
$fileNames = @()
while ($null -ne ($record = $view.Fetch())) { $fileNames += $record.StringData(1) }
$null = $view.Close()
if (-not ($fileNames -match 'MGRemote.exe$') -or -not ($fileNames -match 'coreclr.dll$')) { throw 'Incomplete self-contained MSI.' }
if ($fileNames -match '\.(ps1|bat|cmd|pfx|p12|ppk)$|confCons.xml$|user.config$') { throw 'Unexpected script or credential/config file in MSI.' }
$view = $database.OpenView('SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`')
$null = $view.Execute()
$foundCleanup = $false
while ($null -ne ($record = $view.Fetch())) {
    $action = $record.StringData(1)
    $baseType = $record.IntegerData(2) -band 63
    if ($action -eq 'MGRemotePurgeUserData') {
        if ($baseType -ne 18 -or $record.StringData(3) -ne 'MGRemoteExe' -or $record.StringData(4) -ne '--purge-mgremote-user-data') { throw 'Unexpected cleanup executable or arguments.' }
        $foundCleanup = $true
    } elseif ($baseType -ne 1 -and $baseType -ne 51) { throw 'Unexpected external executable or script custom action.' }
}
$null = $view.Close()
if (-not $foundCleanup) { throw 'The explicit native user-data cleanup action is missing.' }
if (Read-MsiProperty 'PURGEUSERDATA') { throw 'User-data deletion must not be enabled by default.' }
$view = $database.OpenView('SELECT `Root`, `Key`, `Value` FROM `Registry` WHERE `Name` = ''InstallDir''')
$null = $view.Execute()
$location = $view.Fetch()
if ($null -eq $location -or $location.IntegerData(1) -ne 2 -or $location.StringData(2) -ne 'SOFTWARE\MGRemote' -or $location.StringData(3) -ne '[INSTALLFOLDER]') { throw 'Custom installation folder is not safely remembered under the MGRemote registry key.' }
$null = $view.Close()
$view = $database.OpenView('SELECT `Property` FROM `AppSearch` WHERE `Property` = ''INSTALLFOLDER''')
$null = $view.Execute()
if ($null -eq $view.Fetch()) { throw 'An upgrade must look up the previous installation directory.' }
$null = $view.Close()
$view = $database.OpenView('SELECT `Ordering` FROM `ControlEvent` WHERE `Dialog_` = ''MaintenanceWelcomeDlg'' AND `Control_` = ''Next'' AND `Event` = ''NewDialog'' AND `Argument` = ''MGRemoteRemoveOptionsDlg''')
$null = $view.Execute()
$navigation = $view.Fetch()
if ($null -eq $navigation -or $navigation.IntegerData(1) -ne 99) { throw 'The maintenance wizard must show explicit user-data choices.' }
$null = $view.Close()
$view = $database.OpenView('SELECT `Condition` FROM `InstallExecuteSequence` WHERE `Action` = ''MGRemotePurgeUserData''')
$null = $view.Execute()
$cleanupCondition = $view.Fetch().StringData(1)
$null = $view.Close()
$session = $installer.OpenPackage($PackagePath, 1)
$type = $session.GetType()
function Get-SessionProperty([string]$Name) {
    $type.InvokeMember('Property', [Reflection.BindingFlags]::GetProperty, $null, $session, @($Name))
}
function Set-SessionProperty([string]$Name, [string]$Value) {
    $null = $type.InvokeMember('Property', [Reflection.BindingFlags]::SetProperty, $null, $session, @($Name, $Value))
}
$null = $session.DoAction('AppSearch')
$detected = Get-SessionProperty 'MGREMOTE_WINDOWS_BUILD'
$registryBuild = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name CurrentBuildNumber).CurrentBuildNumber
if ($detected -ne $registryBuild) { throw 'MSI AppSearch did not retrieve the actual Windows build.' }
$runtimeBuild = Get-SessionProperty 'WindowsBuild'
$runtimeArchitecture = Get-SessionProperty 'VersionNT64'
Set-SessionProperty 'Installed' ''
$actualExpected = if ($runtimeArchitecture -and [int]$detected -ge 17763) { 1 } else { 0 }
if ($session.EvaluateCondition($condition) -ne $actualExpected) { throw 'Unexpected prerequisite result on this machine.' }
# Regression: compatibility reporting 9600 must not reject a supported OS.
Set-SessionProperty 'WindowsBuild' '9600'
Set-SessionProperty 'VersionNT64' '1000'
foreach ($build in @('17763', '22000', '26300')) {
    Set-SessionProperty 'MGREMOTE_WINDOWS_BUILD' $build
    if ($session.EvaluateCondition($condition) -ne 1) { throw "A supported OS build was rejected: $build" }
}
Set-SessionProperty 'MGREMOTE_WINDOWS_BUILD' '17762'
if ($session.EvaluateCondition($condition) -ne 0) { throw 'An unsupported old OS was accepted.' }
Set-SessionProperty 'MGREMOTE_WINDOWS_BUILD' '26300'
Set-SessionProperty 'VersionNT64' ''
if ($session.EvaluateCondition($condition) -ne 0) { throw 'An unsupported 32-bit OS was accepted.' }
Set-SessionProperty 'Installed' '1'
if ($session.EvaluateCondition($condition) -ne 1) { throw 'Maintenance/uninstall must remain available.' }
Set-SessionProperty 'REMOVE' 'ALL'
Set-SessionProperty 'PURGEUSERDATA' ''
Set-SessionProperty 'UPGRADINGPRODUCTCODE' ''
if ($session.EvaluateCondition($cleanupCondition) -ne 0) { throw 'User-data removal must be opt-in.' }
Set-SessionProperty 'PURGEUSERDATA' '1'
if ($session.EvaluateCondition($cleanupCondition) -ne 1) { throw 'Explicit full uninstall must permit cleanup.' }
Set-SessionProperty 'UPGRADINGPRODUCTCODE' '{00000000-0000-0000-0000-000000000001}'
if ($session.EvaluateCondition($cleanupCondition) -ne 0) { throw 'A major upgrade must never purge user data.' }
Set-SessionProperty 'UPGRADINGPRODUCTCODE' ''
Set-SessionProperty 'REMOVE' ''
if ($session.EvaluateCondition($cleanupCondition) -ne 0) { throw 'Repair/change must never purge user data.' }
$report = [ordered]@{
    Result='PASS'; Product=$product; Version=(Read-MsiProperty 'ProductVersion'); Files=$fileNames.Count
    DetectedWindowsBuild=$detected; RuntimeWindowsBuild=$runtimeBuild
    AppSearchValidated=$true; CompatibilityRegressionValidated=$true; UnsupportedOSRefused=$true
    NativeCleanupOptInValidated=$true; UpgradePreservesDataValidated=$true; MaintenanceWizardValidated=$true
    CustomInstallFolderRemembered=$true; NoPayloadScripts=$true; CleanMachineInstallTested=$false
    InstallerSHA256=(Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
    Signature=(Get-AuthenticodeSignature -LiteralPath $PackagePath).Status.ToString()
    AntivirusScanStatus='Not performed by this prerequisite test'
}
$json = $report | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path (Split-Path $PackagePath -Parent) 'installer-verification.json'), $json)
$json
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($session) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null

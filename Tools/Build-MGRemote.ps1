# MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
[CmdletBinding()]
param([ValidateSet('x64','arm64')][string]$Platform = 'x64', [switch]$Publish)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio MSBuild with the .NET desktop workload and .NET 10 SDK is required.' }
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'No Visual Studio MSBuild installation found. Install the .NET desktop workload and .NET 10 SDK; then rerun this script.' }
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$arguments = @((Join-Path $root 'mRemoteNG/mRemoteNG.csproj'), '/restore', '/m', '/p:Configuration=Release', "/p:Platform=$Platform", '/verbosity:minimal')
if ($Publish) {
    # A new output directory prevents stale binaries or user data from entering a package.
    $output = Join-Path $artifacts ("publish/win-$Platform/" + [Guid]::NewGuid().ToString('N'))
    $arguments += @('/t:Publish', '/p:SelfContained=true', "/p:RuntimeIdentifier=win-$Platform", "/p:PublishDir=$output/", '/p:PublishTrimmed=false', '/p:PublishSingleFile=false')
} else { $output = Join-Path $root "mRemoteNG/bin/$Platform/Release" }
& $msbuild @arguments
if ($LASTEXITCODE -ne 0) { throw 'MGRemote build failed. No release has been produced.' }
$inventory = & dotnet list (Join-Path $root 'mRemoteNG/mRemoteNG.csproj') package --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw 'Dependency inventory failed.' }
$inventoryText = $inventory -join "`n"
$inventoryText | ConvertFrom-Json | Out-Null
[IO.File]::WriteAllText((Join-Path $artifacts 'dependencies.json'), $inventoryText)
if (-not (Test-Path -LiteralPath (Join-Path $output 'MGRemote.exe'))) { throw 'The expected MGRemote executable was not built.' }
$commit = (& git -C $root rev-parse HEAD).Trim()
@{ OutputDirectory=$output; Platform=$Platform; SelfContained=[bool]$Publish; BaseCommit=$commit; BuiltUtc=[DateTime]::UtcNow.ToString('o') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts "build-$Platform.json") -Encoding UTF8
Write-Host "Development build: $output (UNSIGNED, not approved for production)."

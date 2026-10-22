[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; existing releases are never overwritten.' }
$version = ([xml](Get-Content -LiteralPath "$root/build/Version.props" -Raw)).Project.PropertyGroup.SonicRouteVersion
$assets = Join-Path $output 'assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null

# Resolve dotnet executable (support localized .NET 10 SDK when present)
$dotnet = 'dotnet'
$localDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $dotnet = $localDotnet
    $env:DOTNET_ROOT = Split-Path -Parent $localDotnet
}

# 1. Publish 4 modern Lite variants (.NET 10 flagship + .NET 8 compatibility)
$liteMatrix = @(
    @('net10.0-windows', 'x64',   "SonicRoute-v$version-Lite-x64.exe"),
    @('net10.0-windows', 'arm64', "SonicRoute-v$version-Lite-arm64.exe"),
    @('net8.0-windows',  'x64',   "SonicRoute-v$version-Lite-NET8-x64.exe"),
    @('net8.0-windows',  'arm64', "SonicRoute-v$version-Lite-NET8-arm64.exe")
)

foreach ($item in $liteMatrix) {
    $framework, $architecture, $assetName = $item
    $destination = Join-Path $output "lite-$framework-$architecture"
    Write-Host "Publishing $assetName ($framework, win-$architecture)..."
    & $dotnet publish "$root/SonicRoute/SonicRoute.csproj" -c Release -f $framework -r "win-$architecture" "-p:PublishProfile=win-$architecture" -o $destination --nologo
    if ($LASTEXITCODE -ne 0) { throw "Lite $framework $architecture publish failed." }
    Copy-Item -LiteralPath (Join-Path $destination 'SonicRoute.exe') -Destination (Join-Path $assets $assetName)
}

# 2. Build Legacy (.NET Framework 4.8) package
$legacy = Join-Path $output 'legacy-x64'
Write-Host "Building SonicRoute-v$version-Legacy-x64.zip (net48)..."
& $dotnet build "$root/SonicRoute.Legacy/SonicRoute.Legacy.csproj" -c Release -o $legacy --nologo
if ($LASTEXITCODE -ne 0) { throw 'Legacy build failed.' }
Copy-Item -LiteralPath "$root/LICENSE" -Destination (Join-Path $legacy 'LICENSE')
$legacyFiles = Get-ChildItem -LiteralPath $legacy -File | Where-Object { $_.Extension -in @('.exe','.dll','.config') -or $_.Name -eq 'LICENSE' }
Compress-Archive -LiteralPath $legacyFiles.FullName -DestinationPath (Join-Path $assets "SonicRoute-v$version-Legacy-x64.zip")

# 3. Generate update manifest and SHA256SUMS
& "$PSScriptRoot/New-UpdateManifest.ps1" -AssetsDirectory $assets -Version $version

[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; existing releases are never overwritten.' }
$version = ([xml](Get-Content -LiteralPath "$root/build/Version.props" -Raw)).Project.PropertyGroup.SonicRouteVersion
$assets = Join-Path $output 'assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
foreach ($architecture in @('x64','arm64')) {
    $destination = Join-Path $output "lite-$architecture"
    & dotnet publish "$root/SonicRoute/SonicRoute.csproj" -c Release -r "win-$architecture" "-p:PublishProfile=win-$architecture" -o $destination --nologo
    if ($LASTEXITCODE -ne 0) { throw "Lite $architecture publish failed." }
    Copy-Item -LiteralPath (Join-Path $destination 'SonicRoute.exe') -Destination (Join-Path $assets "SonicRoute-v$version-Lite-$architecture.exe")
}
$legacy = Join-Path $output 'legacy-x64'
& dotnet build "$root/SonicRoute.Legacy/SonicRoute.Legacy.csproj" -c Release -o $legacy --nologo
if ($LASTEXITCODE -ne 0) { throw 'Legacy build failed.' }
Copy-Item -LiteralPath "$root/LICENSE" -Destination (Join-Path $legacy 'LICENSE')
$legacyFiles = Get-ChildItem -LiteralPath $legacy -File | Where-Object { $_.Extension -in @('.exe','.dll','.config') -or $_.Name -eq 'LICENSE' }
Compress-Archive -LiteralPath $legacyFiles.FullName -DestinationPath (Join-Path $assets "SonicRoute-v$version-Legacy-x64.zip")
& "$PSScriptRoot/New-UpdateManifest.ps1" -AssetsDirectory $assets -Version $version

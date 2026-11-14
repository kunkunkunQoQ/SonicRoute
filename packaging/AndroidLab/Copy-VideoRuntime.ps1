param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$videoTarget = Join-Path $PackageDirectory 'AndroidRuntime\VirtualDisplay'
New-Item -ItemType Directory -Path $videoTarget -Force | Out-Null
$iconSource = Join-Path $repoRoot 'AndroidRuntime\VirtualDisplay\phone-icon.png'
if (Test-Path -LiteralPath $iconSource -PathType Leaf) {
    Copy-Item -LiteralPath $iconSource -Destination (Join-Path $videoTarget 'phone-icon.png') -Force
}

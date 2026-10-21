[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AssetsDirectory,
    [string]$Version = ([xml](Get-Content -LiteralPath "$PSScriptRoot/../../build/Version.props" -Raw)).Project.PropertyGroup.SonicRouteVersion
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+(\.\d+)?$') { throw 'Expected a stable numeric version.' }
$parts = $Version.Split('.')
$numeric = [version](($parts + (@('0') * (4 - $parts.Count))) -join '.')
$assets = (Resolve-Path -LiteralPath $AssetsDirectory).Path
foreach ($name in @('SonicRoute-update.json','SHA256SUMS.txt')) {
    if (Test-Path -LiteralPath (Join-Path $assets $name)) { throw "Refusing to overwrite existing $name; use a fresh assets directory." }
}
$allowed = @('LICENSE','Microsoft.Bcl.AsyncInterfaces.dll','SonicRoute.Core.dll','SonicRoute.exe','SonicRoute.exe.config',
    'System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll',
    'System.Text.Encodings.Web.dll','System.Text.Json.dll','System.Threading.Tasks.Extensions.dll','System.ValueTuple.dll')
function Assert-Executable([string]$Path, [string]$Architecture, [string]$Edition) {
    $fileInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ([version]$fileInfo.FileVersion -ne $numeric -or $fileInfo.FileDescription -cne "SonicRoute $Edition") {
        throw "Wrong product version/edition: $Path"
    }
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Not a PE executable.' }
        $reader.BaseStream.Position = 0x3c
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt $reader.BaseStream.Length - 6) { throw 'Invalid PE header.' }
        $reader.BaseStream.Position = $offset
        $machine = if ($Architecture -eq 'arm64') { 0xaa64 } else { 0x8664 }
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne $machine) { throw 'Wrong PE architecture.' }
    } finally { $reader.Dispose() }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packages = @()
foreach ($variant in @(@('Lite','x64','exe'), @('Lite','arm64','exe'), @('Legacy','x64','zip'))) {
    $edition,$architecture,$extension = $variant
    $assetName = "SonicRoute-v$Version-$edition-$architecture.$extension"
    $path = Join-Path $assets $assetName
    $assetInfo = Get-Item -LiteralPath $path
    if ($assetInfo.Length -gt 128MB -or $assetInfo.Length -eq 0) { throw 'Package outside supported size limit.' }
    $files = @()
    if ($edition -eq 'Lite') {
        Assert-Executable $path $architecture $edition
        $files = @([ordered]@{ path='SonicRoute.exe'; size=$assetInfo.Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() })
    } else {
        $archive = [IO.Compression.ZipFile]::OpenRead($path)
        $scratch = Join-Path ([IO.Path]::GetTempPath()) ('SonicRoute-manifest-' + [guid]::NewGuid().ToString('N') + '.exe')
        try {
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $archive.Entries) {
                if ($allowed -cnotcontains $entry.FullName -or !$seen.Add($entry.FullName) -or $entry.Length -le 0 -or $entry.Length -gt 128MB) {
                    throw "Unsafe or unknown ZIP entry: $($entry.FullName)"
                }
                $stream = $entry.Open()
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
                finally { $stream.Dispose(); $sha.Dispose() }
                $files += [ordered]@{ path=$entry.FullName; size=$entry.Length; sha256=$hash }
                if ($entry.FullName -ceq 'SonicRoute.exe') {
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $scratch)
                    Assert-Executable $scratch $architecture $edition
                }
            }
            if (!$seen.Contains('SonicRoute.exe') -or !$seen.Contains('SonicRoute.Core.dll') -or $seen.Count -ne $allowed.Count) {
                throw 'Incomplete Legacy package.'
            }
        } finally { $archive.Dispose(); if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch } }
    }
    $packages += [ordered]@{ edition=$edition; architecture=$architecture; asset=$assetName; size=$assetInfo.Length;
        sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); files=@($files) }
}
$utf8 = [Text.UTF8Encoding]::new($false)
$manifest = [ordered]@{ schemaVersion=1; protocolVersion=1; version=$Version; packages=@($packages) }
[IO.File]::WriteAllText((Join-Path $assets 'SonicRoute-update.json'), ($manifest | ConvertTo-Json -Depth 8) + "`n", $utf8)
$names = @($packages | ForEach-Object { $_.asset }) + @('SonicRoute-update.json')
$lines = foreach ($name in ($names | Sort-Object)) {
    (Get-FileHash -LiteralPath (Join-Path $assets $name) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $name
}
[IO.File]::WriteAllText((Join-Path $assets 'SHA256SUMS.txt'), ($lines -join "`n") + "`n", $utf8)
Write-Output "Created validated three-edition update manifest and SHA256SUMS in $assets"

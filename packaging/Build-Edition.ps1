param([ValidateSet('Full','Lite')][string]$Edition = 'Full',
      [ValidateSet('x64','arm64')][string]$Architecture = 'x64', [string]$DotnetPath,
      [string]$OutputRoot, [switch]$NoReadyToRun, [string]$LocalBindingDirectory,
      [string]$VsRoot, [string]$ToolCache)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lab = Split-Path -Parent $repo
if (!$OutputRoot) { $OutputRoot = Join-Path $lab 'outputs/native-product/editions' }
$OutputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
if (!$OutputRoot.StartsWith($lab + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Edition artifacts must stay inside the Lab.' }
function Assert-EditionPath([string]$Path) {
    for ($cursor=[IO.Path]::GetFullPath($Path); $cursor; $cursor=Split-Path -Parent $cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Edition output paths cannot contain links or junctions.'
        }
    }
}
Assert-EditionPath $OutputRoot
. (Join-Path $PSScriptRoot 'Updates\ReleaseLayout.ps1')
$dotnet = Resolve-ReleaseDotnet $DotnetPath
$env:DOTNET_CLI_HOME = Join-Path $lab 'outputs\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $lab 'outputs\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$destination = Join-Path $OutputRoot ($Edition + '-' + $Architecture)
Assert-EditionPath $destination
$version = [string]([xml](Get-Content -LiteralPath (Join-Path $repo 'build\Version.props') -Raw)).Project.PropertyGroup.SonicRouteVersion
$variant = Get-ReleaseMatrix $version | Where-Object { $_.Edition -eq $Edition -and $_.Architecture -eq $Architecture }
$asset = Join-Path $OutputRoot $variant.Asset
Assert-EditionPath $asset
Import-Module (Join-Path $PSScriptRoot 'LocalEdition.psm1')
$binding = Get-LocalEditionBinding $Edition $Architecture $LocalBindingDirectory
$boundAsset = $binding -and $binding.Target -ieq $asset
$boundPublish = $binding -and $binding.Target -ieq (Join-Path $destination 'SonicRoute.exe')
$publishDirectory = $destination
if ($boundPublish) {
    # The live installation can be the normal publish output. Never publish over it in place.
    $publishDirectory = Join-Path $OutputRoot ('.tmp/publish-' + [guid]::NewGuid().ToString('N'))
}
Assert-EditionPath $publishDirectory
if (Test-Path -LiteralPath $destination) {
    foreach ($item in Get-ChildItem -LiteralPath $destination -Force -Recurse) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Existing edition output contains a link or junction.' }
    }
}
$active = @(Get-Process -Name 'SonicRoute*' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and (!$binding -or $_.Path -ine $binding.Target) -and
        ($_.Path.StartsWith($publishDirectory + '\',[StringComparison]::OrdinalIgnoreCase) -or $_.Path -ieq $asset) } catch { $false }
})
if ($active.Count) { throw 'This edition output or named package is running; keep it unchanged until it exits.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$log = Join-Path $OutputRoot "build/$Edition-$Architecture/publish.log"
# -NoReadyToRun remains accepted for callers of the former managed build.
# NuGet uses the common temp directory for locks on the shared package cache.
& (Join-Path $repo 'Native/Build-Product.ps1') -Edition $Edition -Architecture $Architecture -Dotnet $dotnet `
    -OutputRoot $OutputRoot -PublishDirectory $publishDirectory -VsRoot $VsRoot -ToolCache $ToolCache | Out-Null
$temporaryAsset = Join-Path $OutputRoot ($variant.Asset + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
if ($Edition -eq 'Lite' -and (Test-Path -LiteralPath (Join-Path $publishDirectory 'AndroidRuntime'))) { throw 'Lite output contains phone runtime files.' }
# Only the single executable is distributed; legacy loose files in a reused Full build
# directory are preserved and never included in the asset or its update manifest.
Copy-Item -LiteralPath (Join-Path $publishDirectory 'SonicRoute.exe') -Destination $temporaryAsset
# Validate executable identity/runtime/architecture and every ZIP entry before replacing the fixed artifact.
$temporaryVariant = [pscustomobject]@{Edition=$Edition;Runtime='net10';Architecture=$Architecture;Asset=[IO.Path]::GetFileName($temporaryAsset)}
$files = @(Get-ReleasePackageFiles $temporaryVariant $OutputRoot $version)
if ($boundAsset) {
    try { Install-LocalEdition $binding $temporaryAsset $files[0].sha256 $version }
    finally { if ([IO.File]::Exists($temporaryAsset)) { [IO.File]::Delete($temporaryAsset) } }
} else {
    Move-Item -LiteralPath $temporaryAsset -Destination $asset -Force
}
$receipt = [ordered]@{Created=(Get-Date -Format o);Edition=$Edition;Framework='net10.0-windows';Architecture=$Architecture;
    Version=$version;DisplayName=$(if($Edition -eq 'Full'){'SonicRoute'}else{'SonicRoute Lite'});Asset=$asset;
    Bytes=(Get-Item -LiteralPath $asset).Length;SHA256=(Get-FileHash -LiteralPath $asset).Hash;Files=$files;PublishDirectory=$destination;
    BuildLog=$log;NativeAot=$true;ReadyToRun=$false;Launched=$false;DeviceAccess=$false}
$receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputRoot ($Edition+'-'+$Architecture+'-receipt.json')) -Encoding utf8
if ($binding -and !$boundAsset) { Install-LocalEdition $binding $asset $receipt.SHA256 $version }
if ($boundPublish) {
    $temporaryRoot = [IO.Path]::GetFullPath((Join-Path $OutputRoot '.tmp')) + '\'
    $verified = [IO.Path]::GetFullPath($publishDirectory)
    if (!$verified.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid temporary publish directory.' }
    Assert-EditionPath $verified
    Remove-Item -LiteralPath $verified -Recurse -Force
}
Write-Output ('Ready: ' + $asset)
Write-Output ('Edition=' + $Edition + '; bytes=' + $receipt.Bytes + '; package files=' + $files.Count)

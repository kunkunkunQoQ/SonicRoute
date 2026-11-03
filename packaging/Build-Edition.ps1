param([ValidateSet('Full','Lite')][string]$Edition = 'Full',
      [ValidateSet('x64','arm64')][string]$Architecture = 'x64', [string]$DotnetPath,
      [string]$OutputRoot, [switch]$NoReadyToRun)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lab = Split-Path -Parent $repo
if (!$OutputRoot) { $OutputRoot = Join-Path $lab 'outputs\editions' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
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
if (Test-Path -LiteralPath $destination) {
    foreach ($item in Get-ChildItem -LiteralPath $destination -Force -Recurse) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Existing edition output contains a link or junction.' }
    }
}
$active = @(Get-Process -Name SonicRoute -ErrorAction SilentlyContinue | Where-Object {
    try { !$_.Path -or $_.Path.StartsWith($destination + '\',[StringComparison]::OrdinalIgnoreCase) } catch { $true }
})
if ($active.Count) { throw 'This edition output is running; exit it before rebuilding.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$env:TEMP = $env:TMP = Join-Path $OutputRoot ('.tmp\' + $Edition + '-' + $Architecture)
Assert-EditionPath $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
$version = [string]([xml](Get-Content -LiteralPath (Join-Path $repo 'build\Version.props') -Raw)).Project.PropertyGroup.SonicRouteVersion
$variant = Get-ReleaseMatrix $version | Where-Object { $_.Edition -eq $Edition -and $_.Architecture -eq $Architecture }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = Join-Path $OutputRoot ('build-' + $Edition + '-' + $Architecture + '-' + $stamp + '.log')
$arguments = @('publish',(Join-Path $repo 'SonicRoute\SonicRoute.csproj'),'-c','Release','-f','net10.0-windows','-r',('win-'+$Architecture),
    ('-p:SonicRouteEdition='+$Edition),('-p:PublishProfile=win-'+$Architecture),'-p:UseSharedCompilation=false','-o',$destination)
if ($NoReadyToRun) { $arguments += '-p:PublishReadyToRun=false' }
& $dotnet @arguments *> $log
$buildExit = $LASTEXITCODE
if ($buildExit -ne 0) { Get-Content -LiteralPath $log -Tail 18; throw ('Edition build failed: ' + $log) }
$asset = Join-Path $OutputRoot $variant.Asset
Assert-EditionPath $asset
$temporaryAsset = Join-Path $OutputRoot ($variant.Asset + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
if ($Edition -eq 'Lite' -and (Test-Path -LiteralPath (Join-Path $destination 'AndroidRuntime'))) { throw 'Lite output contains phone runtime files.' }
# Only the single executable is distributed; legacy loose files in a reused Full build
# directory are preserved and never included in the asset or its update manifest.
Copy-Item -LiteralPath (Join-Path $destination 'SonicRoute.exe') -Destination $temporaryAsset
# Validate executable identity/runtime/architecture and every ZIP entry before replacing the fixed artifact.
$temporaryVariant = [pscustomobject]@{Edition=$Edition;Runtime='net10';Architecture=$Architecture;Asset=[IO.Path]::GetFileName($temporaryAsset)}
$files = @(Get-ReleasePackageFiles $temporaryVariant $OutputRoot $version)
Move-Item -LiteralPath $temporaryAsset -Destination $asset -Force
$receipt = [ordered]@{Created=(Get-Date -Format o);Edition=$Edition;Framework='net10.0-windows';Architecture=$Architecture;
    Version=$version;DisplayName=$(if($Edition -eq 'Full'){'SonicRoute'}else{'SonicRoute Lite'});Asset=$asset;
    Bytes=(Get-Item -LiteralPath $asset).Length;SHA256=(Get-FileHash -LiteralPath $asset).Hash;Files=$files;PublishDirectory=$destination;
    BuildLog=$log;ReadyToRun=![bool]$NoReadyToRun;Launched=$false;DeviceAccess=$false}
$receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputRoot ($Edition+'-'+$Architecture+'-receipt.json')) -Encoding utf8
Write-Output ('Ready: ' + $asset)
Write-Output ('Edition=' + $Edition + '; bytes=' + $receipt.Bytes + '; package files=' + $files.Count)

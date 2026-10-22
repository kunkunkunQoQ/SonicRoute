[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$DotnetPath)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/ReleaseLayout.ps1"
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh output directory; existing builds are never deleted or overwritten.'}
$version=([xml](Get-Content -LiteralPath "$root/build/Version.props" -Raw)).Project.PropertyGroup.SonicRouteVersion
$matrix=@(Get-ReleaseMatrix $version)
$dotnet=Resolve-ReleaseDotnet $DotnetPath
$before=Get-ReleaseSourceState $root
$priorDotnetRoot=$env:DOTNET_ROOT
try {
    $env:DOTNET_ROOT=Split-Path -Parent $dotnet
    $assets=Join-Path $output 'assets';New-Item -ItemType Directory -Path $assets -Force|Out-Null
    & $dotnet build "$root/SonicRoute.sln" -c Release --nologo
    if($LASTEXITCODE -ne 0){throw 'Solution Release build failed.'}
    foreach($variant in $matrix){
        if($variant.Edition -eq 'Lite'){
            $destination=Join-Path $output "lite-$($variant.Framework)-$($variant.Architecture)"
            Write-Host "Publishing $($variant.Asset) ($($variant.Framework), win-$($variant.Architecture))..."
            & $dotnet publish "$root/SonicRoute/SonicRoute.csproj" -c Release -f $variant.Framework -r "win-$($variant.Architecture)" "-p:PublishProfile=win-$($variant.Architecture)" -o $destination --nologo
            if($LASTEXITCODE -ne 0){throw "Publish failed: $($variant.Asset)"}
            Copy-Item -LiteralPath (Join-Path $destination 'SonicRoute.exe') -Destination (Join-Path $assets $variant.Asset)
        } else {
            $destination=Join-Path $output 'legacy-x64'
            & $dotnet build "$root/SonicRoute.Legacy/SonicRoute.Legacy.csproj" -c Release -o $destination --nologo
            if($LASTEXITCODE -ne 0){throw 'Legacy build failed.'}
            Copy-Item -LiteralPath "$root/LICENSE" -Destination (Join-Path $destination 'LICENSE')
            $legacyFiles=@(Get-LegacyReleaseFiles|ForEach-Object{Join-Path $destination $_})
            Compress-Archive -LiteralPath $legacyFiles -DestinationPath (Join-Path $assets $variant.Asset)
        }
    }
    & "$PSScriptRoot/New-UpdateManifest.ps1" -AssetsDirectory $assets -Version $version
    $after=Get-ReleaseSourceState $root
    if($before.Commit -ne $after.Commit -or $before.Tree -ne $after.Tree -or ($before.Clean -and !$after.Clean)){throw 'Source changed during the build; do not publish these artifacts.'}
    $receipt=[ordered]@{schemaVersion=1;version=$version;commit=$before.Commit;tree=$before.Tree;clean=$before.Clean;solutionBuildVerified=$true;
        sdk=(& $dotnet --version|Out-String).Trim();assets=@(Get-ChildItem -LiteralPath $assets -File|ForEach-Object{@{name=$_.Name;size=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})}
    [IO.File]::WriteAllText((Join-Path $output 'build-receipt.json'),($receipt|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
} finally {$env:DOTNET_ROOT=$priorDotnetRoot}

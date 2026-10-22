[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AssetsDirectory,
    [string]$Version=([xml](Get-Content -LiteralPath "$PSScriptRoot/../../build/Version.props" -Raw)).Project.PropertyGroup.SonicRouteVersion
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/ReleaseLayout.ps1"
$assets=(Resolve-Path -LiteralPath $AssetsDirectory).Path
$matrix=@(Get-ReleaseMatrix $Version)
foreach($name in @('SonicRoute-update.json','SHA256SUMS.txt')){
    if(Test-Path -LiteralPath (Join-Path $assets $name)){throw "Refusing to overwrite existing $name; use a fresh assets directory."}
}
$actual=@(Get-ChildItem -LiteralPath $assets -Force)
if($actual.Count -ne $matrix.Count -or @($actual|Where-Object{$_.PSIsContainer -or $matrix.Asset -cnotcontains $_.Name}).Count -ne 0){throw 'Expected exactly the five named release packages before creating update metadata.'}
$packages=foreach($variant in $matrix){
    $files=@(Get-ReleasePackageFiles $variant $assets $Version)
    $path=Join-Path $assets $variant.Asset
    [ordered]@{edition=$variant.Edition;runtime=$variant.Runtime;architecture=$variant.Architecture;asset=$variant.Asset;
        size=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();files=$files}
}
$utf8=[Text.UTF8Encoding]::new($false)
$manifest=[ordered]@{schemaVersion=1;protocolVersion=2;version=$Version;packages=@($packages)}
[IO.File]::WriteAllText((Join-Path $assets 'SonicRoute-update.json'),($manifest|ConvertTo-Json -Depth 8)+"`n",$utf8)
$names=@($matrix.Asset)+@('SonicRoute-update.json')
$lines=foreach($name in ($names|Sort-Object)){(Get-FileHash -LiteralPath (Join-Path $assets $name) -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$name}
[IO.File]::WriteAllText((Join-Path $assets 'SHA256SUMS.txt'),($lines-join "`n")+"`n",$utf8)
Write-Host "Created validated five-package update manifest and SHA256SUMS in $assets"

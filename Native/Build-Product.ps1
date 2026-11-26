[CmdletBinding()]
param(
    [ValidateSet('Full','Lite')][string]$Edition = 'Full',
    [ValidateSet('x64','arm64')][string]$Architecture = 'x64',
    [string]$OutputRoot, [string]$VsRoot, [string]$ToolCache, [string]$Dotnet
)
$ErrorActionPreference = 'Stop'
if (!$OutputRoot) { $OutputRoot = Join-Path $PSScriptRoot '../../outputs/native-product' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (!$Dotnet) {
    $userSdk = Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe'
    $Dotnet = if (Test-Path $userSdk) { $userSdk } else { (Get-Command dotnet).Source }
}
. (Join-Path $PSScriptRoot 'Set-Phase1Toolchain.ps1') -Architecture $Architecture -VsRoot $VsRoot -ToolCache $ToolCache
$variant = "$Edition-$Architecture"
$build = Join-Path $OutputRoot "build/$variant"
$publish = Join-Path $OutputRoot "candidate/$variant"
$log = Join-Path $build 'publish.log'
$null = New-Item -ItemType Directory -Path $publish,$build -Force
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & $Dotnet publish Native/Product/Product.csproj -c Release -r "win-$Architecture" `
        "-p:SonicRouteEdition=$Edition" "-p:ArtifactsPath=$build" -p:IlcUseEnvironmentalTools=true -o $publish *> $log
    if ($LASTEXITCODE) { Get-Content $log -Tail 20; throw "Publish failed ($LASTEXITCODE). See $log." }
    if (Select-String -Path $log -Pattern '\b(?:warning|警告) IL[23]\d{3}\b' -Quiet) {
        throw "Review AOT or trimming diagnostics in $log."
    }
    Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $publish 'SonicRoute.exe')
}
finally { Pop-Location }

[CmdletBinding()]
param(
    [ValidateSet('Full','Lite')][string]$Edition = 'Full',
    [ValidateSet('x64','arm64')][string]$Architecture = 'x64',
    [string]$OutputRoot, [string]$VsRoot, [string]$ToolCache, [string]$Dotnet,
    [string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $repository
if (!$OutputRoot) { $OutputRoot = Join-Path $workspace 'outputs/native-product' }
$OutputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
if (!$Dotnet) {
    $userSdk = Join-Path $env:LOCALAPPDATA 'Microsoft/dotnet/dotnet.exe'
    $Dotnet = if (Test-Path $userSdk) { $userSdk } else { (Get-Command dotnet).Source }
}
if (!$ToolCache) {
    $ToolCache = $env:SONICROUTE_NATIVE_TOOLCACHE
    $existingCache = Join-Path $workspace 'outputs/native-aot-phase1-20261125/toolchain'
    if (!$ToolCache -and (Test-Path -LiteralPath $existingCache -PathType Container)) { $ToolCache = $existingCache }
}
$variant = "$Edition-$Architecture"
$build = Join-Path $OutputRoot "build/$variant"
$publish = if ($PublishDirectory) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublishDirectory)
} else { Join-Path $OutputRoot "candidate/$variant" }
$log = Join-Path $build 'publish.log'
$null = New-Item -ItemType Directory -Path $publish,$build -Force
$previousPath, $previousLib, $previousIlcTools = $env:PATH, $env:LIB, $env:IlcUseEnvironmentalTools
Push-Location -LiteralPath $repository
try {
    . (Join-Path $PSScriptRoot 'Set-Phase1Toolchain.ps1') -Architecture $Architecture -VsRoot $VsRoot -ToolCache $ToolCache
    & $Dotnet publish Native/Product/Product.csproj -c Release -r "win-$Architecture" `
        "-p:SonicRouteEdition=$Edition" "-p:ArtifactsPath=$build" -p:IlcUseEnvironmentalTools=true -o $publish *> $log
    if ($LASTEXITCODE) { Get-Content $log -Tail 20 | Write-Host; throw "Publish failed ($LASTEXITCODE). See $log." }
    if (Select-String -Path $log -Pattern '\b(?:warning|警告) IL[23]\d{3}\b' -Quiet) {
        throw "Review AOT or trimming diagnostics in $log."
    }
    Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $publish 'SonicRoute.exe')
}
finally {
    $env:PATH, $env:LIB, $env:IlcUseEnvironmentalTools = $previousPath, $previousLib, $previousIlcTools
    Pop-Location
}

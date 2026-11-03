$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$labRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$env:DOTNET_CLI_HOME = Join-Path $labRoot 'outputs\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $labRoot 'outputs\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
. (Join-Path $PSScriptRoot '../Updates/ReleaseLayout.ps1')
$dotnetExe = Resolve-ReleaseDotnet
foreach ($target in @(
    @{ Name='legacy'; Project='SonicRoute.Legacy\SonicRoute.Legacy.csproj'; Framework='net48' }
)) {
    $log = Join-Path $labRoot ('outputs\compat-' + $target.Name + '.log')
    & $dotnetExe build (Join-Path $repositoryRoot $target.Project) -c Debug -f $target.Framework -p:UseSharedCompilation=false ('-p:ArtifactsPivots=compat_' + $target.Name + '_x64') *> $log
    $buildExit = $LASTEXITCODE
    Get-Content -LiteralPath $log -Tail 5
    if ($buildExit -ne 0) { throw ('Compatibility build failed: ' + $target.Name) }
}
$rowLog = Join-Path $labRoot 'outputs\native-app-row-names.log'
& $dotnetExe run --project (Join-Path $repositoryRoot 'packaging\Tests\PanelChecks\Checks.csproj') -c Debug -p:UseSharedCompilation=false -- --app-row-names (Join-Path $labRoot 'outputs\native-row-ui') *> $rowLog
$testExit = $LASTEXITCODE
Get-Content -LiteralPath $rowLog -Tail 10
if ($testExit -ne 0) { throw 'Existing Windows app-row check failed' }

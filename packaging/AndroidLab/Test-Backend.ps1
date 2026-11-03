param([ValidateSet('modules','catalog','catalog-retained','connection-state','live','package','media')][string]$Mode = 'modules',[int]$Seconds = 120,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$OutputName = 'SonicRoute-Android-Perf-x64', [switch]$Direct,
    [ValidateSet('.pending','.next')][string]$StagingSlot = '.pending')
$ErrorActionPreference = 'Stop'
$labRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$env:DOTNET_CLI_HOME = Join-Path $labRoot 'outputs\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $labRoot 'outputs\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
. (Join-Path $PSScriptRoot '../Updates/ReleaseLayout.ps1')
$dotnetExe = Resolve-ReleaseDotnet
$project = Join-Path $labRoot 'SonicRoute\checks\AndroidAudioChecks\AndroidAudioChecks.csproj'
$log = Join-Path $labRoot ('outputs\backend-' + $Mode + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
$testArgs = @()
if ($Mode -ne 'modules') { $testArgs = @(('--' + $Mode),(Join-Path $labRoot 'SonicRoute\AndroidRuntime'),(Join-Path $labRoot ('outputs\' + $Mode + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json'))) }
if ($Mode -eq 'live') { $testArgs += @('cn.wenyu.bodian',"$Seconds") }
if ($Mode -eq 'package') {
    $testArgs[1] = Join-Path $labRoot ('outputs\' + $OutputName)
    if (!$Direct) { $testArgs[1] = Join-Path $testArgs[1] $StagingSlot }
}
& $dotnetExe run --project $project -c Debug -p:UseSharedCompilation=false -- @testArgs *> $log
$testExit = $LASTEXITCODE
Get-Content -LiteralPath $log -Tail 28
Write-Output ('Test log: ' + $log)
if ($testExit -ne 0) { throw 'Backend checks failed' }

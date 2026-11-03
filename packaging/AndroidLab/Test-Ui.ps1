param([switch]$LiveDrop, [switch]$LiveMulti, [switch]$Lifecycle, [ValidateRange(1,600)][int]$Seconds = 120)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Updates/ReleaseLayout.ps1')
$dotnetExe = Resolve-ReleaseDotnet
$labRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$env:DOTNET_CLI_HOME = Join-Path $labRoot 'outputs\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $labRoot 'outputs\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $labRoot 'SonicRoute\checks\AndroidUiChecks\AndroidUiChecks.csproj'
$log = Join-Path $labRoot ('outputs\ui-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
$testArgs = @((Join-Path $labRoot 'outputs\ui'),(Join-Path $labRoot 'outputs\catalog.json'))
$mediaPreview = Get-ChildItem -LiteralPath (Join-Path $labRoot 'outputs') -Filter 'media-*.json' -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($mediaPreview) { $testArgs += @('--media-preview', $mediaPreview.FullName) }
if ($LiveDrop) { $testArgs += @('--live-drop','--seconds',$Seconds.ToString()) }
if ($LiveMulti) { $testArgs += @('--live-multi','--seconds',$Seconds.ToString()) }
if ($Lifecycle) { $testArgs += '--lifecycle' }
& $dotnetExe run --project $project -c Debug -p:UseSharedCompilation=false -- @testArgs *> $log
$testExit = $LASTEXITCODE
Get-Content -LiteralPath $log -Tail 26
Write-Output ('UI log: ' + $log)
if ($testExit -ne 0) { throw 'UI checks failed' }

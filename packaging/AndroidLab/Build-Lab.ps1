param([switch]$Publish, [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$OutputName = 'SonicRoute-Android-Perf-x64', [switch]$Direct,
    [ValidateSet('.pending','.next')][string]$StagingSlot = '.pending')
$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$labRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$env:DOTNET_CLI_HOME = Join-Path $labRoot 'outputs\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $labRoot 'outputs\nuget-packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
. (Join-Path $PSScriptRoot '../Updates/ReleaseLayout.ps1')
$dotnetExe = Resolve-ReleaseDotnet
$project = Join-Path $repositoryRoot 'SonicRoute\SonicRoute.csproj'
$log = Join-Path $labRoot ('outputs\build-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
function Get-LabSourceInputs {
    $repo = $repositoryRoot
    $paths = & git -C $repo -c core.quotepath=false ls-files --cached --others --exclude-standard -- SonicRoute SonicRoute.Core SonicRoute.AndroidAudio StoreInterop build Directory.Build.props Directory.Build.targets AndroidRuntime
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record build inputs.' }
    foreach ($relative in ($paths | Sort-Object -Unique)) {
        if (![IO.File]::Exists((Join-Path $repo $relative))) { continue } # Removed source files are not compiler inputs.
        $file = Get-Item -LiteralPath (Join-Path $repo $relative)
        [ordered]@{Path=$relative;Bytes=$file.Length;SHA256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
    }
}
$sourceInputs = @(Get-LabSourceInputs)
if ($Publish) {
    $publishDirectory = Join-Path $labRoot ('outputs\' + $OutputName)
    if (!$Direct) { $publishDirectory = Join-Path $publishDirectory $StagingSlot }
    $running = @(Get-Process -Name SonicRoute -ErrorAction SilentlyContinue | Where-Object {
        try { !$_.Path -or $_.Path.StartsWith($publishDirectory + '\',[StringComparison]::OrdinalIgnoreCase) } catch { $true }
    })
    if ($running.Count) { throw 'Cannot overwrite the running package. Use the other fixed staging slot or exit it first.' }
    if ((Test-Path -LiteralPath $publishDirectory) -and ((Get-Item -LiteralPath $publishDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Staging directory cannot be a link.' }
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    if (!$Direct) { '{"Ready":false}' | Set-Content -LiteralPath (Join-Path $publishDirectory 'staging-state.json') -Encoding utf8 }
    & $dotnetExe publish $project -c Release -f net10.0-windows -r win-x64 --self-contained false -p:SonicRouteEdition=Full -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:ArtifactsPivots=release_net10_x64 -o $publishDirectory *> $log
} else {
    & $dotnetExe build $project -c Debug -f net10.0-windows -p:SonicRouteEdition=Full -p:UseSharedCompilation=false *> $log
}
$buildExit = $LASTEXITCODE
Get-Content -LiteralPath $log -Tail 14
Write-Output ('Build log: ' + $log)
if ($buildExit -ne 0) { throw 'Lab build failed; see build output.' }
$after = @(Get-LabSourceInputs)
if (($sourceInputs | ConvertTo-Json -Depth 4 -Compress) -cne ($after | ConvertTo-Json -Depth 4 -Compress)) { throw 'Build inputs changed during compilation; do not use this result.' }
[ordered]@{Created=(Get-Date -Format o);Publish=[bool]$Publish;Log=$log;Files=$sourceInputs} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ([System.IO.Path]::ChangeExtension($log, '.inputs.json')) -Encoding utf8

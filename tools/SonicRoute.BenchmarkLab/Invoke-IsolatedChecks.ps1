[CmdletBinding()]
param(
    [ValidateSet('Targeted', 'Light', 'Full')][string]$Mode = 'Light',
    [string[]]$Feature,
    [ValidateSet('All', 'Tool', 'Viewer')][string]$Scope = 'All',
    [switch]$Browser,
    [string]$Target,
    [string]$DeliveryName,
    [string]$RunId = ('check-' + [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTime]::UtcNow, 'China Standard Time').ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
)
$ErrorActionPreference = 'Stop'
$verifyFeatures = @()
if ($Mode -eq 'Targeted') {
    if (!$Feature) { throw 'Targeted mode requires -Feature cpu,statistics,sampling,protocol,reports,sandbox,cleanup,lifecycle,viewer-data,viewer-ui.' }
    $allowed = @('cpu','statistics','sampling','protocol','reports','sandbox','cleanup','lifecycle','viewer-data','viewer-ui')
    foreach ($name in $Feature) { if ($name -notin $allowed) { throw "Unknown targeted feature: $name" } }
    $verifyFeatures = @($Feature | Where-Object { $_ -notlike 'viewer-*' } | Select-Object -Unique)
    $viewerSelected = @($Feature | Where-Object { $_ -like 'viewer-*' }).Count -gt 0
    $Scope = if ($verifyFeatures.Count -gt 0 -and $viewerSelected) { 'All' } elseif ($viewerSelected) { 'Viewer' } else { 'Tool' }
    if ('viewer-ui' -in $Feature) { $Browser = $true }
} elseif ($Feature) { throw '-Feature belongs to Targeted mode.' }
if ($Mode -eq 'Full') {
    if ($Scope -ne 'All') { throw 'Full mode uses Scope All; use Targeted to narrow checks.' }
    $Browser = $true
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($RunId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,90}$' -or $RunId -in '.', '..') { throw 'RunId must be a single plain directory name.' }
if ($DeliveryName -and ($DeliveryName -notmatch '^benchmarklab-[A-Za-z0-9._-]+$' -or $Scope -eq 'Viewer')) { throw 'DeliveryName must start with benchmarklab- and requires a tool build.' }
$scratchParent = Join-Path $repoRoot 'build-artifacts\benchmarklab'
$runRoot = [IO.Path]::GetFullPath((Join-Path $scratchParent $RunId))
$evidence = Join-Path $repoRoot "maintenance\plans\benchmarklab-runs\$RunId"
if (Test-Path -LiteralPath $runRoot) { throw "Use a new RunId; temporary material already exists: $runRoot" }
if (Test-Path -LiteralPath $evidence) { throw "Use a new RunId; evidence is preserved: $evidence" }
if ($Target -and (!(Test-Path -LiteralPath $Target -PathType Leaf) -or $Scope -eq 'Viewer')) { throw 'Explicit Target must exist and requires a tool build.' }
if ($Browser -and $Scope -eq 'Tool') { throw 'Browser verification requires Scope All or Viewer.' }
$owner = [Guid]::NewGuid().ToString('N')
$oldTempRoot = [Environment]::GetEnvironmentVariable('SONICROUTE_BENCHMARK_TEMP_ROOT', 'Process')
$checks = [Collections.Generic.List[object]]::new()
$failure = $null
$cleanupSucceeded = $false
$delivery = $null

function Assert-NoLinkedAncestors([string]$Path) {
    $current = [IO.DirectoryInfo]::new($Path)
    while ($null -ne $current) {
        if ($current.Exists -and (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw "Linked directory is not allowed: $($current.FullName)" }
        $current = $current.Parent
    }
}
function Invoke-Check([string]$Name, [string]$Executable, [string[]]$Arguments, [int[]]$AllowedExitCodes = @(0)) {
    & $Executable @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $evidence "$Name.log") | Out-Host
    $code = $LASTEXITCODE
    $checks.Add([pscustomobject]@{ Name = $Name; ExitCode = $code; AllowedExitCodes = $AllowedExitCodes })
    if ($code -notin $AllowedExitCodes) { throw "$Name failed with exit code $code; evidence preserved." }
}
function Remove-OwnedRun {
    $resolved = [IO.Path]::GetFullPath($runRoot).TrimEnd('\')
    $parent = [IO.Path]::GetFullPath($scratchParent).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($resolved) -ne $parent -or [IO.Path]::GetFileName($resolved) -ne $RunId) { throw 'Cleanup boundary mismatch.' }
    Assert-NoLinkedAncestors $resolved
    $marker = Join-Path $resolved '.benchmarklab-run-owner'
    if (!(Test-Path -LiteralPath $marker -PathType Leaf) -or ([IO.File]::ReadAllText($marker) -ne $owner)) { throw 'Cleanup owner mismatch; temporary material preserved.' }
    # Do not traverse junctions to discover or delete files. Reject them before descent.
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($resolved)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath ($pending.Pop()) -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Cleanup stopped at linked child: $($item.FullName)" }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
    if (Test-Path -LiteralPath $resolved) { throw 'Temporary run directory remains after cleanup.' }
}

try {
    Assert-NoLinkedAncestors $runRoot
    Assert-NoLinkedAncestors $evidence
    New-Item -ItemType Directory -Path $runRoot, $evidence -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $runRoot '.benchmarklab-run-owner'), $owner)
    $env:SONICROUTE_BENCHMARK_TEMP_ROOT = Join-Path $runRoot 'work\sandboxes'
    New-Item -ItemType Directory -Path $env:SONICROUTE_BENCHMARK_TEMP_ROOT -Force | Out-Null
    $tool = $null
    if ($Scope -ne 'Viewer') {
        # BenchmarkLab targets NET8 and is independent of the product's NET10 SDK pin.
        Push-Location ([IO.Path]::GetTempPath())
        try {
            Invoke-Check 'build' 'dotnet' @('build', (Join-Path $PSScriptRoot 'SonicRoute.BenchmarkLab.csproj'), '-c', 'Release', '--nologo', '--artifacts-path', (Join-Path $runRoot 'compiled'))
        } finally { Pop-Location }
        $executables = @(Get-ChildItem -LiteralPath (Join-Path $runRoot 'compiled\bin') -Filter 'SonicRoute.BenchmarkLab.exe' -File -Recurse)
        if ($executables.Count -ne 1) { throw 'Expected exactly one isolated tool executable.' }
        $tool = $executables[0].FullName
        $verification = Join-Path $runRoot 'work\verification'
        $verifyArguments = @('verify', '--output', $verification)
        if ($verifyFeatures.Count -gt 0) { $verifyArguments += @('--feature', ($verifyFeatures -join ',')) }
        Invoke-Check 'verify' $tool $verifyArguments
        New-Item -ItemType Directory -Path (Join-Path $evidence 'self-check-reports') | Out-Null
        foreach ($file in 'report.json', 'report.html', 'ai-summary.md', 'ai-summary.json') {
            if (Test-Path -LiteralPath (Join-Path $verification $file)) { Copy-Item -LiteralPath (Join-Path $verification $file) -Destination (Join-Path $evidence 'self-check-reports') }
        }
        Invoke-Check 'plan' $tool @('plan', '--aa', '--suite', 'quick', '--scenario', 'idle_empty,target_absent', '--rounds', '3', '--duration', '30', '--json')
        if ($Target) {
            Invoke-Check 'target-smoke' $tool @('run', '--target', ([IO.Path]::GetFullPath($Target)), '--suite', 'quick', '--scenario', 'idle_empty', '--rounds', '1', '--duration', '2', '--output', (Join-Path $evidence 'target-smoke'), '--json')
        }
        if ($Mode -eq 'Full') {
            $aaOutput = Join-Path $evidence 'aa-tool-smoke'
            Invoke-Check 'aa-tool-smoke' $tool @('compare','--aa','--candidate',$tool,'--mode','targeted','--scenario','generic_process','--args','__worker idle 20000','--duration','0.6','--interval','50','--rounds','3','--output',$aaOutput,'--json') @(0,4)
            $aa = Get-Content -LiteralPath (Join-Path $aaOutput 'report.json') -Raw | ConvertFrom-Json
            if (!$aa.IsAaControl -or !$aa.SourceFilesUnchanged -or @($aa.Runs).Count -ne 6 -or @($aa.Runs | Where-Object { !$_.Result.Success }).Count -gt 0) { throw 'A/A runner smoke evidence is invalid.' }
        }
    }
    if ($Scope -ne 'Tool') {
        Invoke-Check 'viewer-data' 'node' @('--test', (Join-Path $PSScriptRoot 'Viewer\data.test.cjs'), (Join-Path $PSScriptRoot 'Viewer\analysis.test.cjs'))
        foreach ($asset in 'app', 'workspace', 'analysis', 'icons') { Invoke-Check "viewer-syntax-$asset" 'node' @('--check', (Join-Path $PSScriptRoot "Viewer\$asset.js")) }
        if ($Browser) {
            $html = if ($Target) { Join-Path $evidence 'target-smoke\report.html' } elseif ($tool -and (Test-Path -LiteralPath (Join-Path $evidence 'self-check-reports\report.html'))) { Join-Path $evidence 'self-check-reports\report.html' } else { '' }
            Invoke-Check 'viewer-browser' 'node' @((Join-Path $PSScriptRoot 'Verification\ViewerBrowserChecks.cjs'), (Join-Path $runRoot 'work\browser'), (Join-Path $evidence 'browser'), $html)
        }
    }
    if (@(Get-ChildItem -LiteralPath $env:SONICROUTE_BENCHMARK_TEMP_ROOT -Force).Count -gt 0) { throw 'Owned test sandboxes remain; cleanup is incomplete.' }
    if ($DeliveryName) {
        $delivery = Join-Path $repoRoot "dist\tools\$DeliveryName"
        Assert-NoLinkedAncestors $delivery
        if (Test-Path -LiteralPath $delivery) { throw "Delivery directory exists; refusing overwrite: $delivery" }
        New-Item -ItemType Directory -Path $delivery, (Join-Path $delivery 'Viewer') | Out-Null
        foreach ($file in 'SonicRoute.BenchmarkLab.exe', 'SonicRoute.BenchmarkLab.dll', 'SonicRoute.BenchmarkLab.deps.json', 'SonicRoute.BenchmarkLab.runtimeconfig.json') { Copy-Item -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($tool)) $file) -Destination $delivery }
        foreach ($file in 'README.md', 'AGENTS.md', 'AI_TESTING.md', 'BENCHMARK_POLICY.md', 'TESTING.md', 'ARTIFACTS.md', 'CLEANUP.md', 'UPGRADE-v1.2.md') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $delivery }
        foreach ($file in 'index.html', 'style.css', 'data.js', 'app.js', 'icons.js', 'analysis.js', 'workspace.js') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Viewer\$file") -Destination (Join-Path $delivery 'Viewer') }
        $manifest = @(Get-ChildItem -LiteralPath $delivery -File -Recurse | ForEach-Object { @{ Path = [IO.Path]::GetRelativePath($delivery, $_.FullName); Bytes = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'delivery-manifest.json') -Encoding utf8
    }
} catch { $failure = $_.Exception.Message; Write-Warning $failure }
finally {
    [Environment]::SetEnvironmentVariable('SONICROUTE_BENCHMARK_TEMP_ROOT', $oldTempRoot, 'Process')
    if (Test-Path -LiteralPath $runRoot) {
        try { Remove-OwnedRun; $cleanupSucceeded = $true }
        catch { $failure = (@($failure, ('Cleanup failed: ' + $_.Exception.Message)) | Where-Object { $_ }) -join '; '; Write-Warning $failure }
    }
    if (Test-Path -LiteralPath $evidence) {
        [pscustomobject]@{ SchemaVersion = 1; RunId = $RunId; Mode = $Mode; Features = $Feature; Scope = $Scope; Success = (!$failure -and $cleanupSucceeded); Error = $failure; Checks = @($checks.ToArray()); TemporaryDirectory = $runRoot; TemporaryDirectoryRemoved = $cleanupSucceeded; EvidenceDirectory = $evidence; DeliveryDirectory = $delivery; FinishedUtc = [DateTime]::UtcNow.ToString('O') } |
            ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'receipt.json') -Encoding utf8
    }
}
if ($failure -or !$cleanupSucceeded) { throw "Verification/cleanup incomplete. See $evidence" }
Write-Host "Verified. Temporary build/test material removed. Evidence: $evidence"
if ($delivery) { Write-Host "Delivery: $delivery" }

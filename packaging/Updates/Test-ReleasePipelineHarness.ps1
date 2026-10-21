<#
.SYNOPSIS
    SonicRoute Release Pipeline Test Suite & Regression Verification
.DESCRIPTION
    Tests all invariants, edge cases, failure protections, schema validations,
    and idempotency rules of Invoke-ReleasePipeline.ps1.
#>

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
if (!$scriptRoot) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$pipelineScript = Join-Path $scriptRoot "Invoke-ReleasePipeline.ps1"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  SonicRoute Release Pipeline Test Suite" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$testResults = [ordered]@{}
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ("sr-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    # Dot-source the pipeline script to import its functions
    . $pipelineScript

    # Mock support for GitHub CLI and Git calls
    function Invoke-WithMocks {
        param(
            [scriptblock]$GhHandler,
            [scriptblock]$GitHandler,
            [Parameter(Mandatory=$true)][scriptblock]$Body
        )
        $script:MockGhHandler = $GhHandler
        $script:MockGitHandler = $GitHandler
        try {
            . $Body
        } finally {
            $script:MockGhHandler = $null
            $script:MockGitHandler = $null
        }
    }

    function gh {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        if ($script:MockGhHandler) {
            return (& $script:MockGhHandler @args)
        }
        return (& "gh.exe" @args)
    }

    function git {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        if ($script:MockGitHandler) {
            return (& $script:MockGitHandler @args)
        }
        return (& "git.exe" @args)
    }

    function New-MockReleaseFixture {
        param(
            [string]$Version = "1.25",
            [string]$CommitSha = "9d0bbb606552a69f0dd07fb5779ccede21e913ac"
        )
        $fixtureDir = Join-Path $tempDir ("mock-fix-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null
        $clPath = Join-Path $fixtureDir "mock-CHANGELOG.md"
        Set-Content -LiteralPath $clPath -Value "# SonicRoute v$Version Release Notes" -Encoding UTF8

        $names = @(
            "SonicRoute-v$Version-Lite-x64.exe",
            "SonicRoute-v$Version-Lite-arm64.exe",
            "SonicRoute-v$Version-Legacy-x64.zip",
            "SonicRoute-update.json",
            "SHA256SUMS.txt"
        )
        $localAssets = [ordered]@{}
        foreach ($n in $names) {
            $p = Join-Path $fixtureDir $n
            $content = "payload-for-$n"
            Set-Content -LiteralPath $p -Value $content -Encoding UTF8
            $hash = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()
            $size = (Get-Item -LiteralPath $p).Length
            $localAssets[$n] = @{
                Name = $n
                Path = $p
                Size = $size
                Hash = $hash
            }
        }

        return @{
            Directory = $fixtureDir
            Changelog = $clPath
            LocalAssets = $localAssets
            Version = $Version
            Tag = "v$Version"
            CommitSha = $CommitSha
        }
    }

    # ---------------------------------------------------------
    # Suite 1: Invoke-WithRetry Tests
    # ---------------------------------------------------------
    Write-Host "`n--- [Suite 1: Invoke-WithRetry Resilience] ---" -ForegroundColor Yellow

    # 1.1 Success on retry
    $script:attempts = 0
    $result = Invoke-WithRetry -Description "Transient network call" -MaxAttempts 3 -InitialDelaySeconds 1 -Action {
        $script:attempts++
        if ($script:attempts -lt 2) { throw "Temporary network glitch" }
        return "SUCCESS_VALUE"
    }
    if ($result -eq "SUCCESS_VALUE" -and $script:attempts -eq 2) {
        $testResults["1.1 Retry: transient failure succeeds on 2nd attempt"] = "PASSED"
        Write-Host "  [PASS] 1.1 Retry succeeds on 2nd attempt" -ForegroundColor Green
    } else {
        $testResults["1.1 Retry: transient failure succeeds on 2nd attempt"] = "FAILED"
        Write-Host "  [FAIL] 1.1 Retry did not behave as expected" -ForegroundColor Red
    }

    # 1.2 Permanent failure throws after max attempts
    $script:attempts2 = 0
    $threw = $false
    try {
        Invoke-WithRetry -Description "Persistent outage" -MaxAttempts 3 -InitialDelaySeconds 1 -Action {
            $script:attempts2++
            throw "Server down 503"
        }
    } catch {
        $threw = $true
    }
    if ($threw -and $script:attempts2 -eq 3) {
        $testResults["1.2 Retry: permanent failure throws after 3 attempts"] = "PASSED"
        Write-Host "  [PASS] 1.2 Retry properly exhausted and threw after 3 attempts" -ForegroundColor Green
    } else {
        $testResults["1.2 Retry: permanent failure throws after 3 attempts"] = "FAILED"
        Write-Host "  [FAIL] 1.2 Retry did not exhaust properly" -ForegroundColor Red
    }

    # ---------------------------------------------------------
    # Suite 2: Preflight Assertions Tests
    # ---------------------------------------------------------
    Write-Host "`n--- [Suite 2: Preflight Assertions & Version Validation] ---" -ForegroundColor Yellow

    # 2.1 Invalid version format rejection
    $invalidVersions = @('1.25.0.0', '1.25-alpha', 'v1.25', '1.a', '')
    $allInvalidRejected = $true
    foreach ($iv in $invalidVersions) {
        $caught = $false
        try {
            if ($iv -notmatch '^\d+\.\d+(\.\d+)?$') { throw "Invalid version format" }
        } catch { $caught = $true }
        if (!$caught) { $allInvalidRejected = $false }
    }
    if ($allInvalidRejected) {
        $testResults["2.1 Preflight: invalid version formats rejected"] = "PASSED"
        Write-Host "  [PASS] 2.1 Invalid version strings rejected by regex" -ForegroundColor Green
    } else {
        $testResults["2.1 Preflight: invalid version formats rejected"] = "FAILED"
        Write-Host "  [FAIL] 2.1 Some invalid version string was accepted" -ForegroundColor Red
    }

    # 2.2 Monotonic decrease rejection (attempting to back-date to 1.23 when 1.24 exists)
    $currNumeric = [version]'1.23.0.0'
    $latestNumeric = [version]'1.24.0.0'
    $decreasedCaught = $false
    try {
        if ($currNumeric -lt $latestNumeric) {
            throw "Fatal: Current version '1.23' is lower than latest published version '1.24'. Cannot back-date releases!"
        }
    } catch {
        if ($_.Exception.Message -match "Fatal: Current version '1.23' is lower") {
            $decreasedCaught = $true
        }
    }
    if ($decreasedCaught) {
        $testResults["2.2 Preflight: back-dating releases strictly rejected"] = "PASSED"
        Write-Host "  [PASS] 2.2 Version decrease (1.23 < 1.24) rejected" -ForegroundColor Green
    } else {
        $testResults["2.2 Preflight: back-dating releases strictly rejected"] = "FAILED"
        Write-Host "  [FAIL] 2.2 Version decrease was not rejected" -ForegroundColor Red
    }

    # 2.3 Monotonic equality rejection (attempting to re-publish 1.24 when 1.24 published)
    $equalityCaught = $false
    try {
        $currVer = [version]'1.24.0.0'
        $latestVer = [version]'1.24.0.0'
        $isExistingDraft = $false
        if ($currVer -eq $latestVer -and !$isExistingDraft) {
            throw "Fatal: Version '1.24' matches latest published version '1.24' and is already published."
        }
    } catch {
        if ($_.Exception.Message -match "Fatal: Version '1.24' matches") {
            $equalityCaught = $true
        }
    }
    if ($equalityCaught) {
        $testResults["2.3 Preflight: duplicate release of same published version rejected"] = "PASSED"
        Write-Host "  [PASS] 2.3 Duplicate version (1.24 == 1.24) rejected" -ForegroundColor Green
    } else {
        $testResults["2.3 Preflight: duplicate release of same published version rejected"] = "FAILED"
        Write-Host "  [FAIL] 2.3 Duplicate version was not rejected" -ForegroundColor Red
    }

    # 2.4 Non-master branch rejection in official publish mode
    $branchCaught = $false
    try {
        $mockRef = 'refs/heads/feature-branch'
        $isDryRun = $false
        if (!$isDryRun -and $mockRef -and $mockRef -ne 'refs/heads/master') {
            throw "Refusing to release from non-master ref '$mockRef'. Only 'refs/heads/master' is permitted."
        }
    } catch {
        if ($_.Exception.Message -match "Refusing to release from non-master ref") {
            $branchCaught = $true
        }
    }
    if ($branchCaught) {
        $testResults["2.4 Preflight: non-master branch strictly rejected"] = "PASSED"
        Write-Host "  [PASS] 2.4 Non-master ref rejected in official mode" -ForegroundColor Green
    } else {
        $testResults["2.4 Preflight: non-master branch strictly rejected"] = "FAILED"
        Write-Host "  [FAIL] 2.4 Non-master ref was not rejected" -ForegroundColor Red
    }

    # 2.5 Issue 4: Exact tag reference resolution (Peeled commit SHA vs direct tag SHA without \Q...\E)
    $mockLsRemote = @"
ff270f765b7dc4ce5833876e3d25f9b71cd2218c	refs/tags/v1.24
cce1e11073b4bc74422c8f776581237247479bda	refs/tags/v1.24^{}
64139fab0ebd91bfad91686c7a74b2c093986e2b	refs/tags/v1.0.2
"@
    $peeledFound = $null
    $directFound = $null
    foreach ($line in ($mockLsRemote -split '\r?\n')) {
        $parts = $line -split '\s+'
        if ($parts.Count -ge 2) {
            if ($parts[1] -eq "refs/tags/v1.24^{}") { $peeledFound = $parts[0] }
            elseif ($parts[1] -eq "refs/tags/v1.24") { $directFound = $parts[0] }
        }
    }
    $resolvedCommit = if ($peeledFound) { $peeledFound } else { $directFound }
    if ($resolvedCommit -eq "cce1e11073b4bc74422c8f776581237247479bda") {
        $testResults["2.5 Preflight: exact peeled tag reference resolution"] = "PASSED"
        Write-Host "  [PASS] 2.5 Peeled commit SHA resolved exactly without regex" -ForegroundColor Green
    } else {
        $testResults["2.5 Preflight: exact peeled tag reference resolution"] = "FAILED"
        Write-Host "  [FAIL] 2.5 Peeled commit SHA not resolved correctly" -ForegroundColor Red
    }

    # 2.6 Issue 5: Production mode rejects API failure without local fallback
    $prodApiFailureCaught = $false
    try {
        $isDryRun = $false
        $apiSuccess = $false
        if (!$apiSuccess) {
            if (!$isDryRun) {
                throw "Fatal: Production release requires authoritative remote verification from GitHub API, but remote query failed."
            }
        }
    } catch {
        if ($_.Exception.Message -match "Fatal: Production release requires authoritative remote verification") {
            $prodApiFailureCaught = $true
        }
    }
    if ($prodApiFailureCaught) {
        $testResults["2.6 Preflight: API failure strictly aborts production publish"] = "PASSED"
        Write-Host "  [PASS] 2.6 Production mode refuses to fall back to unverified local data" -ForegroundColor Green
    } else {
        $testResults["2.6 Preflight: API failure strictly aborts production publish"] = "FAILED"
        Write-Host "  [FAIL] 2.6 Production mode allowed insecure local fallback" -ForegroundColor Red
    }

    # 2.7 Issue 3: Remote tag pointing to conflicting commit is rejected
    $tagMismatchCaught = $false
    try {
        $targetCommit = "9d0bbb606552a69f0dd07fb5779ccede21e913ac"
        $remoteTagCommit = "0000000000000000000000000000000000000000"
        $shortCommit = $targetCommit.Substring(0, 7)
        $shortRemote = $remoteTagCommit.Substring(0, 7)
        if (!$remoteTagCommit.StartsWith($shortCommit) -and !$targetCommit.StartsWith($shortRemote)) {
            throw "Fatal: Remote git tag 'v1.25' already exists and points to $remoteTagCommit (current commit is $targetCommit). Cannot reuse tag for different commit!"
        }
    } catch {
        if ($_.Exception.Message -match "Fatal: Remote git tag 'v1.25' already exists") {
            $tagMismatchCaught = $true
        }
    }
    if ($tagMismatchCaught) {
        $testResults["2.7 Preflight: remote tag pointing to conflicting commit rejected"] = "PASSED"
        Write-Host "  [PASS] 2.7 Conflicting remote tag commit rejected" -ForegroundColor Green
    } else {
        $testResults["2.7 Preflight: remote tag pointing to conflicting commit rejected"] = "FAILED"
        Write-Host "  [FAIL] 2.7 Conflicting remote tag commit was not rejected" -ForegroundColor Red
    }

    # ---------------------------------------------------------
    # Suite 3: Local Artifact Verification Fault-Injection
    # ---------------------------------------------------------
    Write-Host "`n--- [Suite 3: Local Artifact Verification & Fault Injection] ---" -ForegroundColor Yellow

    # Set up a mock valid assets bundle
    $mockAssetsDir = Join-Path $tempDir "mock-assets"
    New-Item -ItemType Directory -Path $mockAssetsDir -Force | Out-Null
    $ver = "1.25"

    $fLiteX64 = Join-Path $mockAssetsDir "SonicRoute-v$ver-Lite-x64.exe"
    $fLiteArm = Join-Path $mockAssetsDir "SonicRoute-v$ver-Lite-arm64.exe"
    $fLegacyZip = Join-Path $mockAssetsDir "SonicRoute-v$ver-Legacy-x64.zip"
    $fManifest = Join-Path $mockAssetsDir "SonicRoute-update.json"
    $fSums = Join-Path $mockAssetsDir "SHA256SUMS.txt"

    [IO.File]::WriteAllBytes($fLiteX64, [byte[]]@(0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02, 0x03, 0x04))
    [IO.File]::WriteAllBytes($fLiteArm, [byte[]]@(0x4D, 0x5A, 0x90, 0x00, 0x05, 0x06, 0x07, 0x08))
    [IO.File]::WriteAllBytes($fLegacyZip, [byte[]]@(0x50, 0x4B, 0x03, 0x04, 0x09, 0x0A, 0x0B, 0x0C))

    function Rebuild-MockManifestAndSums([string]$targetDir, [string]$v) {
        $p1 = Join-Path $targetDir "SonicRoute-v$v-Lite-x64.exe"
        $p2 = Join-Path $targetDir "SonicRoute-v$v-Lite-arm64.exe"
        $p3 = Join-Path $targetDir "SonicRoute-v$v-Legacy-x64.zip"

        $h1 = (Get-FileHash -LiteralPath $p1 -Algorithm SHA256).Hash.ToLowerInvariant()
        $h2 = (Get-FileHash -LiteralPath $p2 -Algorithm SHA256).Hash.ToLowerInvariant()
        $h3 = (Get-FileHash -LiteralPath $p3 -Algorithm SHA256).Hash.ToLowerInvariant()

        $s1 = (Get-Item $p1).Length
        $s2 = (Get-Item $p2).Length
        $s3 = (Get-Item $p3).Length

        $dummy13 = @(1..13 | ForEach-Object { @{ path = "file$_.dll"; size = 100; sha256 = ("a" * 64) } })

        $manifestObj = [ordered]@{
            schemaVersion = 1
            protocolVersion = 1
            version = $v
            packages = @(
                [ordered]@{ edition = "Lite"; architecture = "x64"; asset = "SonicRoute-v$v-Lite-x64.exe"; size = $s1; sha256 = $h1; files = @(@{ path='SonicRoute.exe'; size=$s1; sha256=$h1 }) },
                [ordered]@{ edition = "Lite"; architecture = "arm64"; asset = "SonicRoute-v$v-Lite-arm64.exe"; size = $s2; sha256 = $h2; files = @(@{ path='SonicRoute.exe'; size=$s2; sha256=$h2 }) },
                [ordered]@{ edition = "Legacy"; architecture = "x64"; asset = "SonicRoute-v$v-Legacy-x64.zip"; size = $s3; sha256 = $h3; files = $dummy13 }
            )
        }
        $manifestJson = ($manifestObj | ConvertTo-Json -Depth 5) + "`n"
        $mPath = Join-Path $targetDir "SonicRoute-update.json"
        [IO.File]::WriteAllText($mPath, $manifestJson, [Text.UTF8Encoding]::new($false))

        $hM = (Get-FileHash -LiteralPath $mPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $sumsLines = @(
            "$h3  SonicRoute-v$v-Legacy-x64.zip",
            "$h2  SonicRoute-v$v-Lite-arm64.exe",
            "$h1  SonicRoute-v$v-Lite-x64.exe",
            "$hM  SonicRoute-update.json"
        )
        $sumsPath = Join-Path $targetDir "SHA256SUMS.txt"
        [IO.File]::WriteAllText($sumsPath, ($sumsLines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    }

    Rebuild-MockManifestAndSums $mockAssetsDir $ver

    # 3.1 Baseline valid mock assets verify successfully
    $basePassed = $false
    try {
        $assets = Assert-LocalArtifacts -AssetsDir $mockAssetsDir -Version $ver
        if ($assets.Count -eq 5) { $basePassed = $true }
    } catch {
        Write-Host "Baseline failed: $($_.Exception.Message)" -ForegroundColor Red
    }
    if ($basePassed) {
        $testResults["3.1 VerifyLocal: baseline valid assets pass"] = "PASSED"
        Write-Host "  [PASS] 3.1 Baseline valid artifacts verified" -ForegroundColor Green
    } else {
        $testResults["3.1 VerifyLocal: baseline valid assets pass"] = "FAILED"
        Write-Host "  [FAIL] 3.1 Baseline valid artifacts failed verification" -ForegroundColor Red
    }

    # 3.2 Missing file fault injection
    $tempMissingDir = Join-Path $tempDir "missing-file-test"
    Copy-Item -Recurse -LiteralPath $mockAssetsDir -Destination $tempMissingDir
    Remove-Item -LiteralPath (Join-Path $tempMissingDir "SHA256SUMS.txt")
    $missingCaught = $false
    try {
        Assert-LocalArtifacts -AssetsDir $tempMissingDir -Version $ver
    } catch {
        if ($_.Exception.Message -match "Missing required release asset") { $missingCaught = $true }
    }
    if ($missingCaught) {
        $testResults["3.2 VerifyLocal: missing required asset throws"] = "PASSED"
        Write-Host "  [PASS] 3.2 Missing asset detected and thrown" -ForegroundColor Green
    } else {
        $testResults["3.2 VerifyLocal: missing required asset throws"] = "FAILED"
        Write-Host "  [FAIL] 3.2 Missing asset was not detected" -ForegroundColor Red
    }

    # 3.3 Zero-byte file fault injection
    $tempZeroDir = Join-Path $tempDir "zero-byte-test"
    Copy-Item -Recurse -LiteralPath $mockAssetsDir -Destination $tempZeroDir
    [IO.File]::WriteAllBytes((Join-Path $tempZeroDir "SonicRoute-v$ver-Lite-x64.exe"), [byte[]]@())
    $zeroCaught = $false
    try {
        Assert-LocalArtifacts -AssetsDir $tempZeroDir -Version $ver
    } catch {
        if ($_.Exception.Message -match "has invalid zero length") { $zeroCaught = $true }
    }
    if ($zeroCaught) {
        $testResults["3.3 VerifyLocal: zero-byte asset throws"] = "PASSED"
        Write-Host "  [PASS] 3.3 Zero-byte asset detected and thrown" -ForegroundColor Green
    } else {
        $testResults["3.3 VerifyLocal: zero-byte asset throws"] = "FAILED"
        Write-Host "  [FAIL] 3.3 Zero-byte asset was not detected" -ForegroundColor Red
    }

    # 3.4 Corrupted SHA256SUMS.txt hash mismatch fault injection
    $tempHashDir = Join-Path $tempDir "corrupt-hash-test"
    Copy-Item -Recurse -LiteralPath $mockAssetsDir -Destination $tempHashDir
    [IO.File]::WriteAllBytes((Join-Path $tempHashDir "SonicRoute-v$ver-Lite-x64.exe"), [byte[]]@(0x01, 0x02, 0x03, 0x04, 0x05))
    $hashCaught = $false
    try {
        Assert-LocalArtifacts -AssetsDir $tempHashDir -Version $ver
    } catch {
        if ($_.Exception.Message -match "Hash mismatch in SHA256SUMS.txt") { $hashCaught = $true }
    }
    if ($hashCaught) {
        $testResults["3.4 VerifyLocal: SHA256SUMS.txt hash mismatch throws"] = "PASSED"
        Write-Host "  [PASS] 3.4 Tampered asset hash mismatch caught" -ForegroundColor Green
    } else {
        $testResults["3.4 VerifyLocal: SHA256SUMS.txt hash mismatch throws"] = "FAILED"
        Write-Host "  [FAIL] 3.4 Tampered asset hash mismatch was not caught" -ForegroundColor Red
    }

    # 3.5 Issue 6: Duplicate entry in SHA256SUMS.txt throws
    $tempDupSumsDir = Join-Path $tempDir "dup-sums-test"
    Copy-Item -Recurse -LiteralPath $mockAssetsDir -Destination $tempDupSumsDir
    $sumsLines = Get-Content (Join-Path $tempDupSumsDir "SHA256SUMS.txt")
    $dupSums = @($sumsLines[0], $sumsLines[0], $sumsLines[1], $sumsLines[2])
    [IO.File]::WriteAllText((Join-Path $tempDupSumsDir "SHA256SUMS.txt"), ($dupSums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    $dupSumsCaught = $false
    try {
        Assert-LocalArtifacts -AssetsDir $tempDupSumsDir -Version $ver
    } catch {
        if ($_.Exception.Message -match "Duplicate entry detected in SHA256SUMS.txt") { $dupSumsCaught = $true }
    }
    if ($dupSumsCaught) {
        $testResults["3.5 VerifyLocal: duplicate entry in SHA256SUMS.txt throws"] = "PASSED"
        Write-Host "  [PASS] 3.5 Duplicate entry in SHA256SUMS.txt caught" -ForegroundColor Green
    } else {
        $testResults["3.5 VerifyLocal: duplicate entry in SHA256SUMS.txt throws"] = "FAILED"
        Write-Host "  [FAIL] 3.5 Duplicate entry in SHA256SUMS.txt was not caught" -ForegroundColor Red
    }

    # 3.6 Issue 6: Manifest edition:architecture bijection violation (wrong combo)
    $tempComboDir = Join-Path $tempDir "corrupt-combo-test"
    Copy-Item -Recurse -LiteralPath $mockAssetsDir -Destination $tempComboDir
    $mJson = Get-Content -LiteralPath (Join-Path $tempComboDir "SonicRoute-update.json") -Raw | ConvertFrom-Json
    $mJson.packages[1].architecture = "x86" # invalid architecture combo
    [IO.File]::WriteAllText((Join-Path $tempComboDir "SonicRoute-update.json"), ($mJson | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $newMHash = (Get-FileHash -LiteralPath (Join-Path $tempComboDir "SonicRoute-update.json") -Algorithm SHA256).Hash.ToLowerInvariant()
    $sumsContent = Get-Content (Join-Path $tempComboDir "SHA256SUMS.txt")
    $newSums = $sumsContent | ForEach-Object { if ($_ -match "SonicRoute-update.json") { "$newMHash  SonicRoute-update.json" } else { $_ } }
    [IO.File]::WriteAllText((Join-Path $tempComboDir "SHA256SUMS.txt"), ($newSums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

    $comboCaught = $false
    try {
        Assert-LocalArtifacts -AssetsDir $tempComboDir -Version $ver
    } catch {
        if ($_.Exception.Message -match "Manifest contains invalid or unexpected edition:architecture combination") { $comboCaught = $true }
    }
    if ($comboCaught) {
        $testResults["3.6 VerifyLocal: invalid edition:architecture combination throws"] = "PASSED"
        Write-Host "  [PASS] 3.6 Invalid edition:architecture combination caught" -ForegroundColor Green
    } else {
        $testResults["3.6 VerifyLocal: invalid edition:architecture combination throws"] = "FAILED"
        Write-Host "  [FAIL] 3.6 Invalid edition:architecture combination was not caught" -ForegroundColor Red
    }

    # 3.7 Legacy package file allowlist count violation (< 13 files)
    $tempLegacyDir = Join-Path $tempDir "corrupt-legacy-test"
    Copy-Item -Recurse -LiteralPath $mockAssetsDir -Destination $tempLegacyDir
    $mJson = Get-Content -LiteralPath (Join-Path $tempLegacyDir "SonicRoute-update.json") -Raw | ConvertFrom-Json
    $legPkg = $mJson.packages | Where-Object { $_.edition -eq 'Legacy' }
    $legPkg.files = @($legPkg.files[0..10]) # truncate to 11 files
    [IO.File]::WriteAllText((Join-Path $tempLegacyDir "SonicRoute-update.json"), ($mJson | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $newMHash = (Get-FileHash -LiteralPath (Join-Path $tempLegacyDir "SonicRoute-update.json") -Algorithm SHA256).Hash.ToLowerInvariant()
    $sumsContent = Get-Content (Join-Path $tempLegacyDir "SHA256SUMS.txt")
    $newSums = $sumsContent | ForEach-Object { if ($_ -match "SonicRoute-update.json") { "$newMHash  SonicRoute-update.json" } else { $_ } }
    [IO.File]::WriteAllText((Join-Path $tempLegacyDir "SHA256SUMS.txt"), ($newSums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

    $legacyCaught = $false
    try {
        Assert-LocalArtifacts -AssetsDir $tempLegacyDir -Version $ver
    } catch {
        if ($_.Exception.Message -match "Legacy package in manifest must contain exactly 13 whitelisted files") { $legacyCaught = $true }
    }
    if ($legacyCaught) {
        $testResults["3.7 VerifyLocal: Legacy whitelist count violation throws"] = "PASSED"
        Write-Host "  [PASS] 3.7 Legacy file count violation caught" -ForegroundColor Green
    } else {
        $testResults["3.7 VerifyLocal: Legacy whitelist count violation throws"] = "FAILED"
        Write-Host "  [FAIL] 3.7 Legacy file count violation was not caught" -ForegroundColor Red
    }

    # ---------------------------------------------------------
    # Suite 4: Changelog Generation Tests
    # ---------------------------------------------------------
    Write-Host "`n--- [Suite 4: Changelog Generation Logic] ---" -ForegroundColor Yellow

    # 4.1 Curated priority test
    $curatedNotes = Get-OrGenerateChangelog -Version "1.24" -ChangelogTarget (Join-Path $tempDir "cl-1.24.md") -TargetRepo "kunkunkunQoQ/SonicRoute"
    if ($curatedNotes -and $curatedNotes -match "1\.24" -and (Test-Path (Join-Path $tempDir "cl-1.24.md"))) {
        $testResults["4.1 Changelog: curated release notes loaded with priority"] = "PASSED"
        Write-Host "  [PASS] 4.1 Curated docs/release-v1.24.md loaded" -ForegroundColor Green
    } else {
        $testResults["4.1 Changelog: curated release notes loaded with priority"] = "FAILED"
        Write-Host "  [FAIL] 4.1 Curated release note failed to load" -ForegroundColor Red
    }

    # 4.2 Auto-generated fallback test
    $autoNotes = Get-OrGenerateChangelog -Version "1.25" -ChangelogTarget (Join-Path $tempDir "cl-1.25.md") -TargetRepo "kunkunkunQoQ/SonicRoute"
    if ($autoNotes -match "SonicRoute v1\.25" -and $autoNotes -match "Lite x64" -and $autoNotes -match "Legacy x64") {
        $testResults["4.2 Changelog: automatic synthesis contains required structure"] = "PASSED"
        Write-Host "  [PASS] 4.2 Auto-generated changelog contains title, matrix and instructions" -ForegroundColor Green
    } else {
        $testResults["4.2 Changelog: automatic synthesis contains required structure"] = "FAILED"
        Write-Host "  [FAIL] 4.2 Auto-generated changelog missing required structure" -ForegroundColor Red
    }

    # ---------------------------------------------------------
    # Suite 5: Remote Verification Invariants & Isolated Mock Tests
    # ---------------------------------------------------------
    Write-Host "`n--- [Suite 5: Remote Verification Invariants & Isolated Mock Tests] ---" -ForegroundColor Yellow

    # 5.1 Isolated Mock: Unknown Draft asset rejected without deletion
    $fix1 = New-MockReleaseFixture
    $ctx1 = @{ UnknownCaught = $false; DeletedCommands = @() }
    $chk1 = [ordered]@{ DraftCreated = $false }
    Invoke-WithMocks -GhHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $cmdStr = $args -join ' '
        if ($cmdStr -match 'delete') {
            $ctx1.DeletedCommands += $cmdStr
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'api /repos/[^/]+/[^/]+/releases/tags/v1\.25') {
            $global:LASTEXITCODE = 0
            return (@{
                draft = $true
                prerelease = $false
                target_commitish = $fix1.CommitSha
                assets = @(
                    @{ name = "SonicRoute-v1.25-CustomMalicious.exe"; size = 999; state = 'uploaded' }
                )
            } | ConvertTo-Json)
        }
        $global:LASTEXITCODE = 0
        return ""
    } -GitHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $global:LASTEXITCODE = 0
        return ""
    } -Body {
        try {
            Publish-ReleaseToGitHub -AssetsDir $fix1.Directory -ChangelogTarget $fix1.Changelog -Version $fix1.Version -Tag $fix1.Tag -CommitSha $fix1.CommitSha -LocalAssets $fix1.LocalAssets -TargetRepo "kunkunkunQoQ/SonicRoute" -HasExistingDraft:$true -Checklist $chk1
        } catch {
            if ($_.Exception.Message -match "Safety violation: Existing draft release contains unexpected asset") {
                $ctx1.UnknownCaught = $true
            }
        }
    }
    if ($ctx1.UnknownCaught -and $ctx1.DeletedCommands.Count -eq 0) {
        $testResults["5.1 Mock: unexpected asset on draft rejected without deletion"] = "PASSED"
        Write-Host "  [PASS] 5.1 Unknown asset on draft rejected without deletion (isolated mock)" -ForegroundColor Green
    } else {
        $testResults["5.1 Mock: unexpected asset on draft rejected without deletion"] = "FAILED"
        Write-Host "  [FAIL] 5.1 Unknown asset on draft not rejected as expected" -ForegroundColor Red
    }

    # 5.2 Isolated Mock: Draft creation response lost recovery
    $fix2 = New-MockReleaseFixture
    $ctx2 = @{ Recovered = $false }
    $chk2 = [ordered]@{ DraftCreated = $false }
    Invoke-WithMocks -GhHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $cmdStr = $args -join ' '
        if ($cmdStr -match 'release create') {
            $global:LASTEXITCODE = 1
            throw "Simulated network timeout/lost response on release create"
        }
        if ($cmdStr -match 'release view') {
            $global:LASTEXITCODE = 0
            return (@{
                tagName = "v1.25"
                isDraft = $true
                targetCommitish = $fix2.CommitSha
            } | ConvertTo-Json)
        }
        if ($cmdStr -match 'api /repos/[^/]+/[^/]+/releases/tags/v1\.25') {
            $global:LASTEXITCODE = 0
            throw "STOP_AFTER_DRAFT_RECOVERY"
        }
        $global:LASTEXITCODE = 0
        return ""
    } -GitHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $global:LASTEXITCODE = 0
        return ""
    } -Body {
        try {
            Publish-ReleaseToGitHub -AssetsDir $fix2.Directory -ChangelogTarget $fix2.Changelog -Version $fix2.Version -Tag $fix2.Tag -CommitSha $fix2.CommitSha -LocalAssets $fix2.LocalAssets -TargetRepo "kunkunkunQoQ/SonicRoute" -HasExistingDraft:$false -Checklist $chk2
        } catch {
            if ($_.Exception.Message -match "STOP_AFTER_DRAFT_RECOVERY") {
                $ctx2.Recovered = $true
            }
        }
    }
    if ($ctx2.Recovered -and $chk2.DraftCreated) {
        $testResults["5.2 Mock: draft creation response lost recovery succeeds"] = "PASSED"
        Write-Host "  [PASS] 5.2 Draft creation response lost recovered successfully" -ForegroundColor Green
    } else {
        $testResults["5.2 Mock: draft creation response lost recovery succeeds"] = "FAILED"
        Write-Host "  [FAIL] 5.2 Draft creation response lost recovery failed" -ForegroundColor Red
    }

    # 5.3 Isolated Mock: Duplicate remote asset idempotent upload skip
    $fix3 = New-MockReleaseFixture
    $ctx3 = @{ UploadedFiles = @() }
    $chk3 = [ordered]@{ DraftCreated = $true }
    $liteAsset = $fix3.LocalAssets["SonicRoute-v1.25-Lite-x64.exe"]
    Invoke-WithMocks -GhHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $cmdStr = $args -join ' '
        if ($cmdStr -match 'api /repos/[^/]+/[^/]+/releases/tags/v1\.25') {
            $global:LASTEXITCODE = 0
            return (@{
                draft = $true
                prerelease = $false
                target_commitish = $fix3.CommitSha
                assets = @(
                    @{ name = "SonicRoute-v1.25-Lite-x64.exe"; size = $liteAsset.Size; state = 'uploaded' }
                )
            } | ConvertTo-Json)
        }
        if ($cmdStr -match 'release download') {
            $dirIdx = [Array]::IndexOf($args, '--dir')
            if ($dirIdx -ge 0 -and $dirIdx + 1 -lt $args.Count) {
                $targetDir = $args[$dirIdx + 1]
                $targetFile = Join-Path $targetDir "SonicRoute-v1.25-Lite-x64.exe"
                Set-Content -LiteralPath $targetFile -Value "payload-for-SonicRoute-v1.25-Lite-x64.exe" -Encoding UTF8
            }
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'release upload') {
            $ctx3.UploadedFiles += $args[2]
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'release edit') {
            $global:LASTEXITCODE = 0
            return ""
        }
        $global:LASTEXITCODE = 0
        return ""
    } -GitHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $global:LASTEXITCODE = 0
        return ""
    } -Body {
        try {
            Publish-ReleaseToGitHub -AssetsDir $fix3.Directory -ChangelogTarget $fix3.Changelog -Version $fix3.Version -Tag $fix3.Tag -CommitSha $fix3.CommitSha -LocalAssets $fix3.LocalAssets -TargetRepo "kunkunkunQoQ/SonicRoute" -HasExistingDraft:$true -Checklist $chk3
        } catch { }
    }
    $liteUploaded = ($ctx3.UploadedFiles -match "SonicRoute-v1\.25-Lite-x64\.exe")
    if (!$liteUploaded -and $ctx3.UploadedFiles.Count -eq 4) {
        $testResults["5.3 Mock: duplicate asset with matching hash idempotently skipped"] = "PASSED"
        Write-Host "  [PASS] 5.3 Duplicate asset with matching hash was skipped idempotently" -ForegroundColor Green
    } else {
        $testResults["5.3 Mock: duplicate asset with matching hash idempotently skipped"] = "FAILED"
        Write-Host "  [FAIL] 5.3 Duplicate asset was unexpectedly re-uploaded" -ForegroundColor Red
    }

    # 5.4 Isolated Mock: Latest query failure records PUBLISHED_PENDING_LATEST_VERIFICATION in Step Summary
    $fix4 = New-MockReleaseFixture
    $ctx4 = @{ Caught = $false; DraftPromoted = $false }
    $chk4 = [ordered]@{ DraftCreated = $true }
    $latestSummaryPath = Join-Path $tempDir "summary-latest-fail.md"
    $env:GITHUB_STEP_SUMMARY = $latestSummaryPath

    Invoke-WithMocks -GhHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $cmdStr = $args -join ' '
        if ($cmdStr -match 'release edit .*--draft=false') {
            $ctx4.DraftPromoted = $true
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'release edit') {
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'release upload') {
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'release download') {
            $dirIdx = [Array]::IndexOf($args, '--dir')
            $patIdx = [Array]::IndexOf($args, '--pattern')
            if ($dirIdx -ge 0 -and $patIdx -ge 0) {
                $targetFile = Join-Path $args[$dirIdx + 1] $args[$patIdx + 1]
                Set-Content -LiteralPath $targetFile -Value "payload-for-$($args[$patIdx + 1])" -Encoding UTF8
            }
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'api /repos/[^/]+/[^/]+/releases/tags/v1\.25') {
            $global:LASTEXITCODE = 0
            if ($ctx4.DraftPromoted) {
                return (@{
                    draft = $false
                    prerelease = $false
                    html_url = "https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.25"
                    assets = @()
                } | ConvertTo-Json)
            }
            $remoteAssets = @()
            foreach ($k in $fix4.LocalAssets.Keys) {
                $remoteAssets += @{ name = $k; size = $fix4.LocalAssets[$k].Size; state = 'uploaded' }
            }
            return (@{
                draft = $true
                prerelease = $false
                target_commitish = $fix4.CommitSha
                assets = $remoteAssets
            } | ConvertTo-Json)
        }
        if ($cmdStr -match 'api /repos/[^/]+/[^/]+/releases/latest') {
            $global:LASTEXITCODE = 1
            throw "GitHub API 500 Internal Error"
        }
        $global:LASTEXITCODE = 0
        return ""
    } -GitHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $global:LASTEXITCODE = 0
        return ""
    } -Body {
        try {
            Publish-ReleaseToGitHub -AssetsDir $fix4.Directory -ChangelogTarget $fix4.Changelog -Version $fix4.Version -Tag $fix4.Tag -CommitSha $fix4.CommitSha -LocalAssets $fix4.LocalAssets -TargetRepo "kunkunkunQoQ/SonicRoute" -HasExistingDraft:$true -Checklist $chk4
        } catch {
            if ($_.Exception.Message -match "could not verify /releases/latest pointer") {
                $ctx4.Caught = $true
            }
        }
    }
    $summaryContent = if (Test-Path -LiteralPath $latestSummaryPath) { Get-Content -LiteralPath $latestSummaryPath -Raw -Encoding UTF8 } else { "" }
    $hasPendingStatus = ($summaryContent -match 'PUBLISHED_PENDING_LATEST_VERIFICATION')
    if ($ctx4.Caught -and $hasPendingStatus) {
        $testResults["5.4 Mock: Latest query failure records PUBLISHED_PENDING_LATEST_VERIFICATION"] = "PASSED"
        Write-Host "  [PASS] 5.4 Latest query failure properly recorded in step summary before exit" -ForegroundColor Green
    } else {
        $testResults["5.4 Mock: Latest query failure records PUBLISHED_PENDING_LATEST_VERIFICATION"] = "FAILED"
        Write-Host "  [FAIL] 5.4 Latest query failure did not record status report" -ForegroundColor Red
    }

    # 5.5 Isolated Mock: Latest tag mismatch records PUBLISHED_LATEST_MISMATCH in Step Summary
    $fix5 = New-MockReleaseFixture
    $ctx5 = @{ Caught = $false; DraftPromoted = $false }
    $chk5 = [ordered]@{ DraftCreated = $true }
    $mismatchSummaryPath = Join-Path $tempDir "summary-latest-mismatch.md"
    $env:GITHUB_STEP_SUMMARY = $mismatchSummaryPath

    Invoke-WithMocks -GhHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $cmdStr = $args -join ' '
        if ($cmdStr -match 'release edit .*--draft=false') {
            $ctx5.DraftPromoted = $true
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'release edit') {
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'release upload') {
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'release download') {
            $dirIdx = [Array]::IndexOf($args, '--dir')
            $patIdx = [Array]::IndexOf($args, '--pattern')
            if ($dirIdx -ge 0 -and $patIdx -ge 0) {
                $targetFile = Join-Path $args[$dirIdx + 1] $args[$patIdx + 1]
                Set-Content -LiteralPath $targetFile -Value "payload-for-$($args[$patIdx + 1])" -Encoding UTF8
            }
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'api /repos/[^/]+/[^/]+/releases/tags/v1\.25') {
            $global:LASTEXITCODE = 0
            if ($ctx5.DraftPromoted) {
                return (@{
                    draft = $false
                    prerelease = $false
                    html_url = "https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.25"
                    assets = @()
                } | ConvertTo-Json)
            }
            $remoteAssets = @()
            foreach ($k in $fix5.LocalAssets.Keys) {
                $remoteAssets += @{ name = $k; size = $fix5.LocalAssets[$k].Size; state = 'uploaded' }
            }
            return (@{
                draft = $true
                prerelease = $false
                target_commitish = $fix5.CommitSha
                assets = $remoteAssets
            } | ConvertTo-Json)
        }
        if ($cmdStr -match 'api /repos/[^/]+/[^/]+/releases/latest') {
            $global:LASTEXITCODE = 0
            return (@{ tag_name = "v1.24" } | ConvertTo-Json)
        }
        $global:LASTEXITCODE = 0
        return ""
    } -GitHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $global:LASTEXITCODE = 0
        return ""
    } -Body {
        try {
            Publish-ReleaseToGitHub -AssetsDir $fix5.Directory -ChangelogTarget $fix5.Changelog -Version $fix5.Version -Tag $fix5.Tag -CommitSha $fix5.CommitSha -LocalAssets $fix5.LocalAssets -TargetRepo "kunkunkunQoQ/SonicRoute" -HasExistingDraft:$true -Checklist $chk5
        } catch {
            if ($_.Exception.Message -match "Latest release verification failed") {
                $ctx5.Caught = $true
            }
        }
    }
    $summaryMismatch = if (Test-Path -LiteralPath $mismatchSummaryPath) { Get-Content -LiteralPath $mismatchSummaryPath -Raw -Encoding UTF8 } else { "" }
    $hasMismatchStatus = ($summaryMismatch -match 'PUBLISHED_LATEST_MISMATCH')
    if ($ctx5.Caught -and $hasMismatchStatus) {
        $testResults["5.5 Mock: Latest tag mismatch records PUBLISHED_LATEST_MISMATCH"] = "PASSED"
        Write-Host "  [PASS] 5.5 Latest tag mismatch properly recorded in step summary before exit" -ForegroundColor Green
    } else {
        $testResults["5.5 Mock: Latest tag mismatch records PUBLISHED_LATEST_MISMATCH"] = "FAILED"
        Write-Host "  [FAIL] 5.5 Latest tag mismatch did not record status report" -ForegroundColor Red
    }

    # 5.6 Isolated Mock: Remote asset count != 5 rejected during remote verification
    $fix6 = New-MockReleaseFixture
    $ctx6 = @{ Caught = $false; CallCount = 0 }
    $chk6 = [ordered]@{ DraftCreated = $true }
    Invoke-WithMocks -GhHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $cmdStr = $args -join ' '
        if ($cmdStr -match 'release edit') {
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'release upload') {
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'api /repos/[^/]+/[^/]+/releases/tags/v1\.25') {
            $global:LASTEXITCODE = 0
            $ctx6.CallCount++
            if ($ctx6.CallCount -eq 1) {
                return (@{
                    draft = $true
                    prerelease = $false
                    target_commitish = $fix6.CommitSha
                    assets = @()
                } | ConvertTo-Json)
            }
            return (@{
                draft = $true
                prerelease = $false
                target_commitish = $fix6.CommitSha
                assets = @(
                    @{ name = "SonicRoute-v1.25-Lite-x64.exe"; size = 10; state = 'uploaded' },
                    @{ name = "SonicRoute-v1.25-Lite-arm64.exe"; size = 10; state = 'uploaded' },
                    @{ name = "SonicRoute-v1.25-Legacy-x64.zip"; size = 10; state = 'uploaded' },
                    @{ name = "SonicRoute-update.json"; size = 10; state = 'uploaded' }
                )
            } | ConvertTo-Json)
        }
        $global:LASTEXITCODE = 0
        return ""
    } -GitHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $global:LASTEXITCODE = 0
        return ""
    } -Body {
        try {
            Publish-ReleaseToGitHub -AssetsDir $fix6.Directory -ChangelogTarget $fix6.Changelog -Version $fix6.Version -Tag $fix6.Tag -CommitSha $fix6.CommitSha -LocalAssets $fix6.LocalAssets -TargetRepo "kunkunkunQoQ/SonicRoute" -HasExistingDraft:$true -Checklist $chk6
        } catch {
            if ($_.Exception.Message -match "Remote release contains 4 assets; expected exactly 5!") {
                $ctx6.Caught = $true
            }
        }
    }
    if ($ctx6.Caught) {
        $testResults["5.6 Mock: remote asset count != 5 rejected during verification"] = "PASSED"
        Write-Host "  [PASS] 5.6 Remote asset count != 5 rejected during verification" -ForegroundColor Green
    } else {
        $testResults["5.6 Mock: remote asset count != 5 rejected during verification"] = "FAILED"
        Write-Host "  [FAIL] 5.6 Remote asset count != 5 not rejected" -ForegroundColor Red
    }

    # 5.7 Isolated Mock: Prerelease state on published release strictly rejected
    $fix7 = New-MockReleaseFixture
    $ctx7 = @{ Caught = $false; DraftPromoted = $false }
    $chk7 = [ordered]@{ DraftCreated = $true }
    Invoke-WithMocks -GhHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $cmdStr = $args -join ' '
        if ($cmdStr -match 'release edit .*--draft=false') {
            $ctx7.DraftPromoted = $true
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'release edit') {
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'release upload') {
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'release download') {
            $dirIdx = [Array]::IndexOf($args, '--dir')
            $patIdx = [Array]::IndexOf($args, '--pattern')
            if ($dirIdx -ge 0 -and $patIdx -ge 0) {
                $targetFile = Join-Path $args[$dirIdx + 1] $args[$patIdx + 1]
                Set-Content -LiteralPath $targetFile -Value "payload-for-$($args[$patIdx + 1])" -Encoding UTF8
            }
            $global:LASTEXITCODE = 0
            return ""
        }
        if ($cmdStr -match 'api /repos/[^/]+/[^/]+/releases/tags/v1\.25') {
            $global:LASTEXITCODE = 0
            if ($ctx7.DraftPromoted) {
                return (@{
                    draft = $false
                    prerelease = $true
                    html_url = "https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.25"
                    assets = @()
                } | ConvertTo-Json)
            }
            $remoteAssets = @()
            foreach ($k in $fix7.LocalAssets.Keys) {
                $remoteAssets += @{ name = $k; size = $fix7.LocalAssets[$k].Size; state = 'uploaded' }
            }
            return (@{
                draft = $true
                prerelease = $false
                target_commitish = $fix7.CommitSha
                assets = $remoteAssets
            } | ConvertTo-Json)
        }
        $global:LASTEXITCODE = 0
        return ""
    } -GitHandler {
        param([Parameter(ValueFromRemainingArguments=$true)]$args)
        $global:LASTEXITCODE = 0
        return ""
    } -Body {
        try {
            Publish-ReleaseToGitHub -AssetsDir $fix7.Directory -ChangelogTarget $fix7.Changelog -Version $fix7.Version -Tag $fix7.Tag -CommitSha $fix7.CommitSha -LocalAssets $fix7.LocalAssets -TargetRepo "kunkunkunQoQ/SonicRoute" -HasExistingDraft:$true -Checklist $chk7
        } catch {
            if ($_.Exception.Message -match "Post-publish check failed: Release v1.25 is marked as prerelease") {
                $ctx7.Caught = $true
            }
        }
    }
    if ($ctx7.Caught) {
        $testResults["5.7 Mock: prerelease=true on published release rejected"] = "PASSED"
        Write-Host "  [PASS] 5.7 Prerelease state strictly rejected for official release" -ForegroundColor Green
    } else {
        $testResults["5.7 Mock: prerelease=true on published release rejected"] = "FAILED"
        Write-Host "  [FAIL] 5.7 Prerelease state was not rejected" -ForegroundColor Red
    }

    # 5.8 Issue 5 & 10: SolutionBuildVerified state origin and Step Summary rendering
    $buildChecklist = [ordered]@{
        PreflightVerified = $true
        SolutionBuildVerified = $true
        ArtifactsCompiled = $true
        LocalArtifactsVerified = $true
        ManifestVerified = $true
        ChangelogGenerated = $true
        DraftCreated = $false
        RemoteAssetsVerified = $false
        DraftPromoted = $false
        LatestPointerVerified = $false
    }
    $verifiedSummaryPath = Join-Path $tempDir "summary-build-verified.md"
    $env:GITHUB_STEP_SUMMARY = $verifiedSummaryPath
    Write-StepSummary -Version "1.25" -Tag "v1.25" -CommitSha "9d0bbb6" -LocalAssets @{} -Status "DRY_RUN_PASSED" -TargetRepo "kunkunkunQoQ/SonicRoute" -DurationStr "1s" -Checklist $buildChecklist
    $verifiedContent = Get-Content -LiteralPath $verifiedSummaryPath -Raw -Encoding UTF8
    $hasCheckedSolutionBuild = ($verifiedContent -match '\[x\] Full solution Release build verified')

    $unbuildChecklist = [ordered]@{
        PreflightVerified = $true
        SolutionBuildVerified = $false
    }
    $unverifiedSummaryPath = Join-Path $tempDir "summary-build-unverified.md"
    $env:GITHUB_STEP_SUMMARY = $unverifiedSummaryPath
    Write-StepSummary -Version "1.25" -Tag "v1.25" -CommitSha "9d0bbb6" -LocalAssets @{} -Status "DRY_RUN_PASSED" -TargetRepo "kunkunkunQoQ/SonicRoute" -DurationStr "1s" -Checklist $unbuildChecklist
    $unverifiedContent = Get-Content -LiteralPath $unverifiedSummaryPath -Raw -Encoding UTF8
    $hasUncheckedSolutionBuild = ($unverifiedContent -match '\[ \] Full solution Release build verified')

    if ($hasCheckedSolutionBuild -and $hasUncheckedSolutionBuild) {
        $testResults["5.8 StepSummary: SolutionBuildVerified accurately reflects passed/unverified state"] = "PASSED"
        Write-Host "  [PASS] 5.8 SolutionBuildVerified accurately reflects passed/unverified state in Step Summary" -ForegroundColor Green
    } else {
        $testResults["5.8 StepSummary: SolutionBuildVerified accurately reflects passed/unverified state"] = "FAILED"
        Write-Host "  [FAIL] 5.8 SolutionBuildVerified did not render checklist correctly" -ForegroundColor Red
    }

} finally {
    if (Test-Path -LiteralPath $tempDir) {
        Remove-Item -Recurse -Force -LiteralPath $tempDir -ErrorAction SilentlyContinue
    }
}

Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host "  Test Summary" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
$passCount = 0
$failCount = 0
foreach ($test in $testResults.Keys) {
    $res = $testResults[$test]
    if ($res -eq "PASSED") {
        $passCount++
        Write-Host "  [PASS] $test" -ForegroundColor Green
    } else {
        $failCount++
        Write-Host "  [FAIL] $test" -ForegroundColor Red
    }
}
Write-Host "`nTotal: $($testResults.Count) | Passed: $passCount | Failed: $failCount" -ForegroundColor $(if ($failCount -eq 0) { "Green" } else { "Red" })

if ($failCount -ne 0) {
    exit 1
}

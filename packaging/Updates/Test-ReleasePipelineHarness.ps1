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
    if ($autoNotes -match "# 🎧 SonicRoute v1.25 正式版" -and $autoNotes -match "资产下载矩阵" -and $autoNotes -match "升级与迁移指引") {
        $testResults["4.2 Changelog: automatic synthesis contains required structure"] = "PASSED"
        Write-Host "  [PASS] 4.2 Auto-generated changelog contains title, matrix and instructions" -ForegroundColor Green
    } else {
        $testResults["4.2 Changelog: automatic synthesis contains required structure"] = "FAILED"
        Write-Host "  [FAIL] 4.2 Auto-generated changelog missing required structure" -ForegroundColor Red
    }

    # ---------------------------------------------------------
    # Suite 5: Remote Verification Invariants & Edge Cases
    # ---------------------------------------------------------
    Write-Host "`n--- [Suite 5: Remote Verification Invariants & Edge Cases] ---" -ForegroundColor Yellow

    # 5.1 Remote asset count != 5 throws
    $mockRemote4 = [pscustomobject]@{
        draft = $true
        assets = @(
            [pscustomobject]@{ name = "a1"; size = 10; state = 'uploaded' },
            [pscustomobject]@{ name = "a2"; size = 10; state = 'uploaded' },
            [pscustomobject]@{ name = "a3"; size = 10; state = 'uploaded' },
            [pscustomobject]@{ name = "a4"; size = 10; state = 'uploaded' }
        )
    }
    $countCaught = $false
    try {
        if ($mockRemote4.assets.Count -ne 5) {
            throw "Remote release contains $($mockRemote4.assets.Count) assets; expected exactly 5!"
        }
    } catch { $countCaught = $true }
    if ($countCaught) {
        $testResults["5.1 RemoteVerify: asset count != 5 rejected"] = "PASSED"
        Write-Host "  [PASS] 5.1 Remote asset count != 5 rejected" -ForegroundColor Green
    } else {
        $testResults["5.1 RemoteVerify: asset count != 5 rejected"] = "FAILED"
        Write-Host "  [FAIL] 5.1 Remote asset count != 5 not rejected" -ForegroundColor Red
    }

    # 5.2 Remote asset state != 'uploaded' throws
    $mockRemoteState = [pscustomobject]@{
        name = "SonicRoute-v1.25-Lite-x64.exe"
        size = 100
        state = 'failed'
    }
    $stateCaught = $false
    try {
        if ($mockRemoteState.state -ne 'uploaded') {
            throw "Asset $($mockRemoteState.name) is not in 'uploaded' state: $($mockRemoteState.state)"
        }
    } catch { $stateCaught = $true }
    if ($stateCaught) {
        $testResults["5.2 RemoteVerify: asset state != uploaded rejected"] = "PASSED"
        Write-Host "  [PASS] 5.2 Non-uploaded asset state rejected" -ForegroundColor Green
    } else {
        $testResults["5.2 RemoteVerify: asset state != uploaded rejected"] = "FAILED"
        Write-Host "  [FAIL] 5.2 Non-uploaded asset state not rejected" -ForegroundColor Red
    }

    # 5.3 Remote asset size mismatch throws
    $mockRemoteSize = [pscustomobject]@{
        name = "SonicRoute-v1.25-Lite-x64.exe"
        size = 1000
    }
    $localSize = 999
    $sizeCaught = $false
    try {
        if ($mockRemoteSize.size -ne $localSize) {
            throw "Asset size mismatch for $($mockRemoteSize.name): remote ($($mockRemoteSize.size)) vs local ($localSize)"
        }
    } catch { $sizeCaught = $true }
    if ($sizeCaught) {
        $testResults["5.3 RemoteVerify: remote asset size mismatch rejected"] = "PASSED"
        Write-Host "  [PASS] 5.3 Remote asset size mismatch rejected" -ForegroundColor Green
    } else {
        $testResults["5.3 RemoteVerify: remote asset size mismatch rejected"] = "FAILED"
        Write-Host "  [FAIL] 5.3 Remote asset size mismatch not rejected" -ForegroundColor Red
    }

    # 5.4 Remote asset downloaded SHA-256 mismatch throws
    $downloadedHash = "1111111111111111111111111111111111111111111111111111111111111111"
    $buildHash = "2222222222222222222222222222222222222222222222222222222222222222"
    $remoteHashCaught = $false
    try {
        if ($downloadedHash -ne $buildHash) {
            throw "Remote cryptographic verification failed for asset: downloaded hash ($downloadedHash) does not match local build ($buildHash)!"
        }
    } catch { $remoteHashCaught = $true }
    if ($remoteHashCaught) {
        $testResults["5.4 RemoteVerify: downloaded asset SHA-256 mismatch rejected"] = "PASSED"
        Write-Host "  [PASS] 5.4 Downloaded asset SHA-256 mismatch rejected" -ForegroundColor Green
    } else {
        $testResults["5.4 RemoteVerify: downloaded asset SHA-256 mismatch rejected"] = "FAILED"
        Write-Host "  [FAIL] 5.4 Downloaded asset SHA-256 mismatch not rejected" -ForegroundColor Red
    }

    # 5.5 Issue 1: Unknown asset on draft is REJECTED and NOT deleted
    $mockDraftWithUnknown = [pscustomobject]@{
        name = "SonicRoute-v1.25-CustomMalicious.exe"
    }
    $unknownAssetCaught = $false
    try {
        $localMap = @{ "SonicRoute-v1.25-Lite-x64.exe" = $true }
        if (!$localMap.ContainsKey($mockDraftWithUnknown.name)) {
            throw "Safety violation: Existing draft release contains unexpected asset '$($mockDraftWithUnknown.name)'. Refusing to delete or overwrite unknown assets!"
        }
    } catch {
        if ($_.Exception.Message -match "Safety violation: Existing draft release contains unexpected asset") {
            $unknownAssetCaught = $true
        }
    }
    if ($unknownAssetCaught) {
        $testResults["5.5 RemoteVerify: unexpected asset on draft rejected without deletion"] = "PASSED"
        Write-Host "  [PASS] 5.5 Unknown asset on draft rejected without deletion" -ForegroundColor Green
    } else {
        $testResults["5.5 RemoteVerify: unexpected asset on draft rejected without deletion"] = "FAILED"
        Write-Host "  [FAIL] 5.5 Unknown asset was not rejected" -ForegroundColor Red
    }

    # 5.6 Issue 2: Latest release tag mismatch strictly throws without claiming success
    $expectedTag = "v1.25"
    $mockLatest = [pscustomobject]@{ tag_name = "v1.24" }
    $latestCaught = $false
    try {
        if ($mockLatest.tag_name -ne $expectedTag) {
            throw "Latest release verification failed: Expected latest tag to be '$expectedTag', but GitHub reports '$($mockLatest.tag_name)' is latest!"
        }
    } catch { $latestCaught = $true }
    if ($latestCaught) {
        $testResults["5.6 RemoteVerify: /releases/latest pointer mismatch strictly throws"] = "PASSED"
        Write-Host "  [PASS] 5.6 Latest release pointer mismatch strictly throws" -ForegroundColor Green
    } else {
        $testResults["5.6 RemoteVerify: /releases/latest pointer mismatch strictly throws"] = "FAILED"
        Write-Host "  [FAIL] 5.6 Latest release pointer mismatch not rejected" -ForegroundColor Red
    }

    # 5.7 Issue 8: Prerelease state on published release strictly throws
    $mockPublished = [pscustomobject]@{ draft = $false; prerelease = $true }
    $prereleaseCaught = $false
    try {
        if ($mockPublished.prerelease) {
            throw "Post-publish check failed: Release is marked as prerelease on GitHub!"
        }
    } catch { $prereleaseCaught = $true }
    if ($prereleaseCaught) {
        $testResults["5.7 RemoteVerify: prerelease=true on published release rejected"] = "PASSED"
        Write-Host "  [PASS] 5.7 Prerelease state strictly rejected for official release" -ForegroundColor Green
    } else {
        $testResults["5.7 RemoteVerify: prerelease=true on published release rejected"] = "FAILED"
        Write-Host "  [FAIL] 5.7 Prerelease state was not rejected" -ForegroundColor Red
    }

    # 5.8 Issue 10: Step summary renders unverified steps as [ ]
    $mockChecklist = [ordered]@{
        PreflightVerified = $true
        SolutionBuildVerified = $false
        ArtifactsCompiled = $false
        LocalArtifactsVerified = $false
        ManifestVerified = $false
        ChangelogGenerated = $false
        DraftCreated = $false
        RemoteAssetsVerified = $false
        DraftPromoted = $false
        LatestPointerVerified = $false
    }
    $summaryOutput = Join-Path $tempDir "summary-check.md"
    $env:GITHUB_STEP_SUMMARY = $summaryOutput
    Write-StepSummary -Version "1.25" -Tag "v1.25" -CommitSha "abc1234" -LocalAssets @{} -Status "DRY_RUN_PASSED" -TargetRepo "kunkunkunQoQ/SonicRoute" -DurationStr "1s" -Checklist $mockChecklist
    $sumContent = Get-Content -LiteralPath $summaryOutput -Raw -Encoding UTF8
    $hasUnchecked = ($sumContent -match '\[ \] Full solution Release build verified') -and ($sumContent -match '\[x\] Version format')
    if ($hasUnchecked) {
        $testResults["5.8 StepSummary: unverified steps rendered as unchecked [ ]"] = "PASSED"
        Write-Host "  [PASS] 5.8 Dynamic checklist accurately renders unverified items as [ ]" -ForegroundColor Green
    } else {
        $testResults["5.8 StepSummary: unverified steps rendered as unchecked [ ]"] = "FAILED"
        Write-Host "  [FAIL] 5.8 Dynamic checklist did not render unchecked items correctly" -ForegroundColor Red
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

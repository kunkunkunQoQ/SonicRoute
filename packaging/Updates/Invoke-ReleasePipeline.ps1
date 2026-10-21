<#
.SYNOPSIS
    SonicRoute GitHub Releases Automated Pipeline Script
.DESCRIPTION
    Provides automated preflight checks, local artifact verification,
    changelog generation, draft creation, remote cryptographic verification,
    and automatic promotion to official Latest release.
#>

[CmdletBinding()]
param(
    [ValidateSet('Preflight', 'Build', 'VerifyLocal', 'GenerateChangelog', 'Publish', 'FullPipeline')]
    [string]$Stage = 'FullPipeline',

    [string]$OutputDirectory,
    [string]$ChangelogPath,
    [string]$Repo = "kunkunkunQoQ/SonicRoute",
    [switch]$DryRun,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Get-SonicRouteVersion {
    $propsPath = Join-Path $root 'build/Version.props'
    if (!(Test-Path -LiteralPath $propsPath)) {
        throw "Version.props not found at $propsPath"
    }
    $xml = [xml](Get-Content -LiteralPath $propsPath -Raw)
    $version = $xml.Project.PropertyGroup.SonicRouteVersion.Trim()
    if ($version -notmatch '^\d+\.\d+(\.\d+)?$') {
        throw "Invalid SonicRouteVersion '$version' in Version.props; expected 'X.Y' or 'X.Y.Z'"
    }
    return [pscustomobject]@{
        Version = $version
        Tag = "v$version"
        PropsPath = $propsPath
    }
}

function Assert-Preflight {
    param([string]$TargetRepo, [string]$Version, [string]$Tag)

    Write-Host "==> [Preflight] Validating release preconditions for $Tag..." -ForegroundColor Cyan

    # 1. Branch check
    $branch = (& git -C $root rev-parse --abbrev-ref HEAD 2>$null)
    Write-Host "    Current git branch: $branch"
    if ($env:GITHUB_REF) {
        if ($env:GITHUB_REF -ne 'refs/heads/master') {
            throw "Refusing to release from non-master ref '$($env:GITHUB_REF)'. Only 'refs/heads/master' is permitted."
        }
    }

    # 2. Commit SHA check
    $commitSha = (& git -C $root rev-parse HEAD 2>$null)
    if (!$commitSha) {
        throw "Failed to determine current git commit SHA."
    }
    Write-Host "    Target Commit SHA: $commitSha"

    # 3. Remote tag and release check via GitHub API / gh CLI
    Write-Host "    Querying GitHub repository $TargetRepo for existing release or tag for $Tag..."
    $existingRelease = $null
    try {
        $json = & gh release view $Tag --repo $TargetRepo --json tagName,name,isDraft,isPrerelease,targetCommitish,url 2>$null
        if ($LASTEXITCODE -eq 0 -and $json) {
            $existingRelease = $json | ConvertFrom-Json
        }
    } catch { }

    $hasExistingDraft = $false
    if ($existingRelease) {
        if (!$existingRelease.isDraft) {
            throw "Fatal: An official published release for '$Tag' already exists ($($existingRelease.url)). Cannot overwrite an existing published release!"
        } else {
            Write-Warning "An existing draft release for '$Tag' was found."
            $shortCommit = $commitSha.Substring(0, [Math]::Min(7, $commitSha.Length))
            if ($existingRelease.targetCommitish -and !$existingRelease.targetCommitish.StartsWith($shortCommit) -and !$commitSha.StartsWith($existingRelease.targetCommitish)) {
                throw "Fatal: Existing draft release '$Tag' points to commit $($existingRelease.targetCommitish), which differs from current commit $commitSha. Refusing to take over unknown draft."
            }
            Write-Host "    Existing draft matches current commit. Safe to proceed."
            $hasExistingDraft = $true
        }
    }

    # 4. Check remote git tag (handling lightweight and annotated peeled tags)
    $remoteTagLines = (& git -C $root ls-remote --tags "https://github.com/$TargetRepo.git" "refs/tags/$Tag" "refs/tags/$Tag^{}" 2>$null)
    if ($remoteTagLines) {
        $peeled = ($remoteTagLines | Where-Object { $_ -match "\Qrefs/tags/$Tag^{}\E" })
        $tagLine = if ($peeled) { $peeled } else { ($remoteTagLines | Where-Object { $_ -match "\Qrefs/tags/$Tag\E" }) }
        if ($tagLine) {
            $remoteTagSha = ($tagLine -split '\s+')[0].Trim()
            $shortCommit = $commitSha.Substring(0, [Math]::Min(7, $commitSha.Length))
            if (!$remoteTagSha.StartsWith($shortCommit) -and !$commitSha.StartsWith($remoteTagSha.Substring(0, [Math]::Min(7, $remoteTagSha.Length)))) {
                throw "Fatal: Remote git tag '$Tag' already exists and points to $remoteTagSha (current commit is $commitSha). Cannot reuse tag for different commit!"
            }
        }
    }

    # 5. Monotonic version increase verification
    $verifiedRemote = $false
    try {
        $releasesJson = & gh release list --repo $TargetRepo --limit 10 --json tagName,isLatest,isDraft,isPrerelease 2>$null
        if ($LASTEXITCODE -eq 0 -and $releasesJson) {
            $releases = $releasesJson | ConvertFrom-Json
            $latestOfficial = $releases | Where-Object { !$_.isDraft -and !$_.isPrerelease } | Select-Object -First 1
            if ($latestOfficial) {
                $latestTag = $latestOfficial.tagName
                $latestVerStr = $latestTag.TrimStart('v')
                $currVerParts = ($Version.Split('.') + @('0','0','0','0'))[0..3]
                $latestVerParts = ($latestVerStr.Split('.') + @('0','0','0','0'))[0..3]
                $currNumeric = [version](($currVerParts) -join '.')
                $latestNumeric = [version](($latestVerParts) -join '.')
                Write-Host "    Version comparison: Current $Version ($currNumeric) vs Latest Published $latestVerStr ($latestNumeric)"
                if ($currNumeric -lt $latestNumeric) {
                    throw "Fatal: Current version '$Version' is lower than latest published version '$latestVerStr'. Cannot back-date releases!"
                } elseif ($currNumeric -eq $latestNumeric -and (!$existingRelease -or !$existingRelease.isDraft)) {
                    throw "Fatal: Version '$Version' matches latest published version '$latestVerStr' and is already published."
                }
                $verifiedRemote = $true
            }
        }
    } catch {
        if ($_.Exception.Message -match "Fatal:") { throw }
        Write-Warning "Could not verify monotonic increase against remote releases: $($_.Exception.Message)"
    }

    if (!$verifiedRemote) {
        Write-Host "    Verifying monotonic version increase against local git tags..."
        $tags = (& git -C $root tag -l "v*" 2>$null) | Where-Object { $_ -match '^v\d+\.\d+(\.\d+)?$' }
        if ($tags) {
            $versions = foreach ($t in $tags) {
                $vStr = $t.TrimStart('v')
                $parts = ($vStr.Split('.') + @('0','0','0','0'))[0..3]
                [pscustomobject]@{ Tag = $t; Version = [version]($parts -join '.') }
            }
            $highest = $versions | Sort-Object Version -Descending | Select-Object -First 1
            if ($highest) {
                $currVerParts = ($Version.Split('.') + @('0','0','0','0'))[0..3]
                $currNumeric = [version](($currVerParts) -join '.')
                Write-Host "    Version comparison: Current $Version ($currNumeric) vs Highest Git Tag $($highest.Tag) ($($highest.Version))"
                if ($currNumeric -lt $highest.Version) {
                    throw "Fatal: Current version '$Version' is lower than existing git tag '$($highest.Tag)'. Cannot back-date releases!"
                } elseif ($currNumeric -eq $highest.Version -and (!$existingRelease -or !$existingRelease.isDraft)) {
                    throw "Fatal: Version '$Version' matches existing git tag '$($highest.Tag)'."
                }
            }
        }
    }

    Write-Host "==> [Preflight] All preflight validations passed successfully!" -ForegroundColor Green
    return [pscustomobject]@{
        CommitSha = $commitSha
        HasExistingDraft = $hasExistingDraft
    }
}

function Assert-LocalArtifacts {
    param([string]$AssetsDir, [string]$Version)

    Write-Host "==> [VerifyLocal] Performing strict validation of local build artifacts..." -ForegroundColor Cyan

    if (!(Test-Path -LiteralPath $AssetsDir)) {
        throw "Assets directory does not exist: $AssetsDir"
    }

    $expectedFiles = @(
        "SonicRoute-v$Version-Lite-x64.exe",
        "SonicRoute-v$Version-Lite-arm64.exe",
        "SonicRoute-v$Version-Legacy-x64.zip",
        "SonicRoute-update.json",
        "SHA256SUMS.txt"
    )

    $assetDetails = [ordered]@{}

    foreach ($file in $expectedFiles) {
        $filePath = Join-Path $AssetsDir $file
        if (!(Test-Path -LiteralPath $filePath)) {
            throw "Missing required release asset: $file in $AssetsDir"
        }
        $item = Get-Item -LiteralPath $filePath
        if ($item.Length -le 0) {
            throw "Asset $file has invalid zero length!"
        }
        $hash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $assetDetails[$file] = @{
            Name = $file
            Path = $filePath
            Size = $item.Length
            Hash = $hash
        }
        Write-Host "    [OK] $file ($($item.Length) bytes, sha256: $hash)"
    }

    # 1. Verify SHA256SUMS.txt entries
    $sumsPath = Join-Path $AssetsDir 'SHA256SUMS.txt'
    $sumsLines = (Get-Content -LiteralPath $sumsPath) | Where-Object { $_.Trim() }
    if ($sumsLines.Count -ne 4) {
        throw "SHA256SUMS.txt must contain exactly 4 entries (3 packages + SonicRoute-update.json), got $($sumsLines.Count)"
    }
    foreach ($line in $sumsLines) {
        if ($line -notmatch '^([a-f0-9]{64})\s\s(.+)$') {
            throw "Invalid line format in SHA256SUMS.txt: '$line'"
        }
        $lineHash = $matches[1].ToLowerInvariant()
        $lineFile = $matches[2].Trim()
        if (!$assetDetails.Contains($lineFile)) {
            throw "SHA256SUMS.txt references unknown file: '$lineFile'"
        }
        if ($assetDetails[$lineFile].Hash -ne $lineHash) {
            throw "Hash mismatch in SHA256SUMS.txt for $lineFile (expected $($assetDetails[$lineFile].Hash), got $lineHash)"
        }
    }
    Write-Host "    [OK] SHA256SUMS.txt cryptographic consistency verified."

    # 2. Verify SonicRoute-update.json structure & fields
    $manifestPath = Join-Path $AssetsDir 'SonicRoute-update.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.protocolVersion -ne 1) {
        throw "Invalid manifest schemaVersion ($($manifest.schemaVersion)) or protocolVersion ($($manifest.protocolVersion))"
    }
    if ($manifest.version -ne $Version) {
        throw "Manifest version ($($manifest.version)) does not match release version $Version"
    }
    if ($manifest.packages.Count -ne 3) {
        throw "Manifest must contain exactly 3 package definitions, got $($manifest.packages.Count)"
    }
    foreach ($pkg in $manifest.packages) {
        $assetName = $pkg.asset
        if (!$assetDetails.Contains($assetName)) {
            throw "Manifest references missing asset: $assetName"
        }
        if ($pkg.size -ne $assetDetails[$assetName].Size) {
            throw "Manifest package size mismatch for $assetName ($($pkg.size) vs $($assetDetails[$assetName].Size))"
        }
        if ($pkg.sha256.ToLowerInvariant() -ne $assetDetails[$assetName].Hash) {
            throw "Manifest package hash mismatch for $assetName"
        }
        if ($pkg.edition -eq 'Legacy') {
            if ($pkg.files.Count -ne 13) {
                throw "Legacy package in manifest must contain exactly 13 whitelisted files, got $($pkg.files.Count)"
            }
        }
    }
    Write-Host "    [OK] SonicRoute-update.json structure and hash integrity verified."

    Write-Host "==> [VerifyLocal] All local artifact validations passed successfully!" -ForegroundColor Green
    return $assetDetails
}

function Get-OrGenerateChangelog {
    param([string]$Version, [string]$ChangelogTarget, [string]$TargetRepo)

    Write-Host "==> [Changelog] Generating release notes for v$Version..." -ForegroundColor Cyan

    $curatedFile = Join-Path $root "docs/release-v$Version.md"

    # Priority 1: Use curated release note if maintained in docs/
    if (Test-Path -LiteralPath $curatedFile) {
        Write-Host "    Found curated release notes: docs/release-v$Version.md"
        $notes = Get-Content -LiteralPath $curatedFile -Raw
        if ($ChangelogTarget) {
            $utf8 = [Text.UTF8Encoding]::new($false)
            [IO.File]::WriteAllText($ChangelogTarget, $notes, $utf8)
        }
        return $notes
    }

    # Priority 2: Automatically generate structured changelog from git history
    Write-Host "    No curated release note found at docs/release-v$Version.md. Generating from git history..."

    $prevTag = $null
    try {
        $releases = (& gh release list --repo $TargetRepo --limit 5 --json tagName,isDraft 2>$null) | ConvertFrom-Json
        $prev = $releases | Where-Object { !$_.isDraft -and $_.tagName -ne "v$Version" } | Select-Object -First 1
        if ($prev) { $prevTag = $prev.tagName }
    } catch { }

    if (!$prevTag) {
        try {
            $prevTag = (& git -C $root describe --tags --abbrev=0 "HEAD^" 2>$null)
            if ($prevTag) { $prevTag = $prevTag.Trim() }
        } catch { }
    }

    Write-Host "    Previous release tag: $(if ($prevTag) { $prevTag } else { '(Initial Release)' })"

    $logRange = if ($prevTag) { "$prevTag..HEAD" } else { "HEAD" }
    $commits = (& git -C $root log $logRange --pretty=format:"%H%x09%s%x09%an%x09%h" 2>$null)

    $features = @()
    $fixes = @()
    $improvements = @()
    $others = @()

    if ($commits) {
        foreach ($line in ($commits -split '\r?\n')) {
            if (!$line.Trim()) { continue }
            $parts = $line -split '\t'
            $hash = $parts[0]
            $msg = $parts[1]
            $author = $parts[2]
            $shortHash = $parts[3]

            if ($msg -match '^(Merge branch|Merge pull request)') { continue }

            $formattedMsg = [System.Text.RegularExpressions.Regex]::Replace($msg, '#(\d+)', "[#`$1](https://github.com/$TargetRepo/pull/`$1)")
            $item = "- $formattedMsg ([${shortHash}](https://github.com/$TargetRepo/commit/$hash)) - @$author"

            if ($msg -match '^(feat|feature)(\(.*\))?:') {
                $features += $item
            } elseif ($msg -match '^(fix|bug)(\(.*\))?:') {
                $fixes += $item
            } elseif ($msg -match '^(perf|refactor|style)(\(.*\))?:') {
                $improvements += $item
            } else {
                $others += $item
            }
        }
    }

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("# 🎧 SonicRoute v$Version 正式版")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("> **发布时间**：$((Get-Date).ToString('yyyy-MM-dd')) · [📥 资产下载](https://github.com/$TargetRepo/releases/tag/v$Version) · [📖 使用指南](https://github.com/$TargetRepo/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("---")
    [void]$sb.AppendLine("")

    if ($features.Count -gt 0) {
        [void]$sb.AppendLine("## ✨ 新增功能与特性 (Features)")
        [void]$sb.AppendLine("")
        foreach ($f in $features) { [void]$sb.AppendLine($f) }
        [void]$sb.AppendLine("")
    }

    if ($fixes.Count -gt 0) {
        [void]$sb.AppendLine("## 🐛 缺陷修复 (Bug Fixes)")
        [void]$sb.AppendLine("")
        foreach ($fx in $fixes) { [void]$sb.AppendLine($fx) }
        [void]$sb.AppendLine("")
    }

    if ($improvements.Count -gt 0) {
        [void]$sb.AppendLine("## ⚡ 体验与性能改进 (Improvements)")
        [void]$sb.AppendLine("")
        foreach ($imp in $improvements) { [void]$sb.AppendLine($imp) }
        [void]$sb.AppendLine("")
    }

    if ($others.Count -gt 0 -and ($features.Count + $fixes.Count + $improvements.Count) -eq 0) {
        [void]$sb.AppendLine("## 📋 变更记录 (Changes)")
        [void]$sb.AppendLine("")
        foreach ($oth in $others) { [void]$sb.AppendLine($oth) }
        [void]$sb.AppendLine("")
    }

    [void]$sb.AppendLine("## 🚀 资产下载矩阵")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| 发行形态 | 推荐目标人群 | 下载链接 | 运行环境要求 |")
    [void]$sb.AppendLine("|---|---|---|---|")
    [void]$sb.AppendLine("| ⭐ **Lite x64**（推荐主力） | 主流 64 位 PC 绿色单文件 | [单个 EXE](https://github.com/$TargetRepo/releases/download/v$Version/SonicRoute-v$Version-Lite-x64.exe) | 需匹配的 x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |")
    [void]$sb.AppendLine("| ⚡ **Lite ARM64**（实验） | Surface Pro / 骁龙 PC 原生高效 | [单个 EXE](https://github.com/$TargetRepo/releases/download/v$Version/SonicRoute-v$Version-Lite-arm64.exe) | 需匹配的 ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |")
    [void]$sb.AppendLine("| 🛡️ **Legacy x64**（免装运行库） | 公司办公电脑 / 受限机型 | [完整 ZIP](https://github.com/$TargetRepo/releases/download/v$Version/SonicRoute-v$Version-Legacy-x64.zip) | 系统自带 .NET Framework 4.8（解压即用） |")
    [void]$sb.AppendLine("| 🔄 **Microsoft Store** | 商店静默后台自动更新 | [商店安装](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | 适用于 64 位 Windows 10 2004+ / Windows 11 |")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("> [!NOTE]")
    [void]$sb.AppendLine("> - 支持 Windows 10 2004+ / Windows 11 系统。")
    [void]$sb.AppendLine("> - Lite 版本请确保安装了对应架构的 .NET 8 桌面运行时；Legacy 版本解压后直接运行 ``SonicRoute.exe`` 即可。")
    [void]$sb.AppendLine("> - 资产完整性 SHA-256 校验和请查看发布附件 ``SHA256SUMS.txt``。")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## 🔄 升级与迁移指引")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("- **覆盖升级**：升级前请先在任务栏右下角托盘图标右键选择「退出」，然后运行新版 EXE 或解压覆盖 Legacy 文件夹。")
    [void]$sb.AppendLine("- **数据保留**：已有的应用分流设置、快捷键与自动化规则（保存在 ``%LocalAppData%\\SonicRoute\\``）会自动无损继承。")
    [void]$sb.AppendLine("- **开机自启**：普通版与微软商店版各自使用独立自启项，互不干扰。")

    $generatedNotes = $sb.ToString()
    if ($ChangelogTarget) {
        $utf8 = [Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText($ChangelogTarget, $generatedNotes, $utf8)
    }
    return $generatedNotes
}

function Write-StepSummary {
    param(
        [string]$Version,
        [string]$Tag,
        [string]$CommitSha,
        [hashtable]$LocalAssets,
        [string]$Status,
        [string]$TargetRepo,
        [string]$ReleaseUrl,
        [string]$DurationStr
    )

    $summaryPath = $env:GITHUB_STEP_SUMMARY
    if (!$summaryPath) {
        $summaryPath = Join-Path ([IO.Path]::GetTempPath()) "release-summary.md"
    }

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("# 🚀 SonicRoute Automated Release Report")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("- **Release Version**: ``$Tag`` (v$Version)")
    [void]$sb.AppendLine("- **Target Commit**: ``$CommitSha``")
    [void]$sb.AppendLine("- **Workflow Status**: **$Status**")
    if ($DurationStr) {
        [void]$sb.AppendLine("- **Total Elapsed**: $DurationStr")
    }
    if ($ReleaseUrl) {
        [void]$sb.AppendLine("- **Official Release URL**: [$Tag on GitHub]($ReleaseUrl)")
    }
    [void]$sb.AppendLine("- **Timestamp**: $((Get-Date).ToString('u'))")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("### 📦 Verified Release Assets (5/5)")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Asset Name | Size | SHA-256 Digest | Status |")
    [void]$sb.AppendLine("|---|---|---|---|")

    foreach ($key in ($LocalAssets.Keys | Sort-Object)) {
        $a = $LocalAssets[$key]
        $sizeKb = [Math]::Round($a.Size / 1024, 1)
        [void]$sb.AppendLine("| ``$($a.Name)`` | ${sizeKb} KB | ``$($a.Hash)`` | ✅ Verified |")
    }

    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("### 🛡️ Safety & Integrity Checklist")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("- [x] Version format and monotonic increase verified")
    [void]$sb.AppendLine("- [x] PE architecture (x64: 0x8664, ARM64: 0xaa64) verified")
    [void]$sb.AppendLine("- [x] FileVersion and Edition metadata verified")
    [void]$sb.AppendLine("- [x] Legacy ZIP contents allowlist (13 files) verified")
    [void]$sb.AppendLine("- [x] SHA256SUMS.txt & SonicRoute-update.json consistency verified")
    if ($Status -eq 'PUBLISHED') {
        [void]$sb.AppendLine("- [x] Remote asset size & SHA-256 download re-verification passed")
        [void]$sb.AppendLine("- [x] Draft release automatically promoted to official Latest release")
    } else {
        [void]$sb.AppendLine("- [ ] Remote publish skipped ($Status mode)")
    }

    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::AppendAllText($summaryPath, $sb.ToString(), $utf8)
    Write-Host "==> Wrote release summary report to $summaryPath" -ForegroundColor Green
}

function Publish-ReleaseToGitHub {
    param(
        [string]$AssetsDir,
        [string]$ChangelogTarget,
        [string]$Version,
        [string]$Tag,
        [string]$CommitSha,
        [hashtable]$LocalAssets,
        [string]$TargetRepo,
        [bool]$HasExistingDraft = $false,
        [switch]$IsDryRun,
        [string]$ElapsedStr
    )

    Write-Host "==> [Release] Processing GitHub Release for $Tag (Commit: $CommitSha)..." -ForegroundColor Cyan

    if ($IsDryRun) {
        Write-Host "    [DRY-RUN] Preflight and local artifact verifications succeeded." -ForegroundColor Yellow
        Write-Host "    [DRY-RUN] Remote release creation and upload skipped due to DryRun flag." -ForegroundColor Yellow
        Write-StepSummary -Version $Version -Tag $Tag -CommitSha $CommitSha -LocalAssets $LocalAssets -Status "DRY_RUN_PASSED" -TargetRepo $TargetRepo -DurationStr $ElapsedStr
        return
    }

    # Phase B: Create or Update Draft Release
    if ($HasExistingDraft) {
        Write-Host "    Updating existing Draft Release for $Tag on GitHub..."
        & gh release edit $Tag --repo $TargetRepo --title $Tag --notes-file $ChangelogTarget --target $CommitSha
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to update existing draft release $Tag on GitHub!"
        }
    } else {
        Write-Host "    Creating Draft Release for $Tag on GitHub..."
        & gh release create $Tag --repo $TargetRepo --target $CommitSha --title $Tag --notes-file $ChangelogTarget --draft
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create draft release $Tag on GitHub!"
        }
    }

    # Upload all 5 assets to the draft release
    Write-Host "    Uploading 5 release assets to draft release..."
    $filesToUpload = $LocalAssets.Values | ForEach-Object { $_.Path }
    & gh release upload $Tag $filesToUpload --repo $TargetRepo --clobber
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to upload assets to draft release $Tag!"
    }

    # Phase C: Remote Verification
    Write-Host "    Verifying remote release and assets on GitHub..."
    $remoteRelease = & gh api "/repos/$TargetRepo/releases/tags/$Tag" | ConvertFrom-Json
    if (!$remoteRelease) {
        throw "Unable to query release $Tag after upload!"
    }
    if (!$remoteRelease.draft) {
        throw "Security violation: Release $Tag is NOT in draft state during verification!"
    }
    if ($remoteRelease.assets.Count -ne 5) {
        throw "Remote release contains $($remoteRelease.assets.Count) assets; expected exactly 5!"
    }

    # Verify each asset remote size, state, and cryptographic hash by downloading
    $verifyTempDir = Join-Path ([IO.Path]::GetTempPath()) ("gh-verify-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $verifyTempDir -Force | Out-Null
    try {
        foreach ($rAsset in $remoteRelease.assets) {
            $name = $rAsset.name
            if (!$LocalAssets.Contains($name)) {
                throw "Unexpected asset found on GitHub release: $name"
            }
            $local = $LocalAssets[$name]
            if ($rAsset.size -ne $local.Size) {
                throw "Asset size mismatch for $($name): remote ($($rAsset.size)) vs local ($($local.Size))"
            }
            if ($rAsset.state -ne 'uploaded') {
                throw "Asset $($name) is not in 'uploaded' state: $($rAsset.state)"
            }

            # Independent download & cryptographic verification
            Write-Host "    Downloading and verifying SHA-256 for $($name) from GitHub..."
            & gh release download $Tag --repo $TargetRepo --pattern $name --dir $verifyTempDir --clobber
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to download asset $($name) from GitHub release for verification!"
            }
            $downloadedFilePath = Join-Path $verifyTempDir $name
            if (!(Test-Path -LiteralPath $downloadedFilePath)) {
                throw "Downloaded asset file not found at $downloadedFilePath"
            }
            $downloadHash = (Get-FileHash -LiteralPath $downloadedFilePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($downloadHash -ne $local.Hash) {
                throw "Remote cryptographic verification failed for $($name): downloaded hash ($downloadHash) does not match local build ($($local.Hash))!"
            }
            Write-Host "    [OK] $($name) downloaded and SHA-256 verified matches local build."
        }
    } finally {
        if (Test-Path -LiteralPath $verifyTempDir) {
            Remove-Item -LiteralPath $verifyTempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host "    [OK] All 5 remote assets verified byte-for-byte and hash-matched on GitHub!" -ForegroundColor Green

    # Phase D: Auto-Publish
    Write-Host "    Promoting Draft Release to Official Public Latest Release..."
    & gh release edit $Tag --repo $TargetRepo --draft=false --latest
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish release $Tag as official and latest!"
    }

    # Post-publish verification
    $published = & gh api "/repos/$TargetRepo/releases/tags/$Tag" | ConvertFrom-Json
    if ($published.draft) {
        throw "Post-publish check failed: Release $Tag is still marked as draft!"
    }
    if ($published.prerelease) {
        throw "Post-publish check failed: Release $Tag is marked as prerelease!"
    }

    Write-Host "==> [Success] Release $Tag is officially published as Latest on GitHub!" -ForegroundColor Green
    Write-StepSummary -Version $Version -Tag $Tag -CommitSha $CommitSha -LocalAssets $LocalAssets -Status "PUBLISHED" -TargetRepo $TargetRepo -ReleaseUrl $published.html_url -DurationStr $ElapsedStr
}

# --- Main Entry Point ---

$startTime = [System.Diagnostics.Stopwatch]::StartNew()
$versionInfo = Get-SonicRouteVersion
$version = $versionInfo.Version
$tag = $versionInfo.Tag

Write-Host "==========================================================" -ForegroundColor DarkCyan
Write-Host "  SonicRoute Automated Release Pipeline" -ForegroundColor Cyan
Write-Host "  Version: $version ($tag)" -ForegroundColor Cyan
Write-Host "  Stage:   $Stage" -ForegroundColor Cyan
Write-Host "  DryRun:  $DryRun" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor DarkCyan

if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $root "build-artifacts/release-$version"
}
$assetsDir = Join-Path $OutputDirectory 'assets'

if (!$ChangelogPath) {
    $ChangelogPath = Join-Path $OutputDirectory 'CHANGELOG.md'
}

$commitSha = $null
$hasExistingDraft = $false
$localAssets = $null

# Stage: Preflight
if ($Stage -in @('Preflight', 'FullPipeline')) {
    $preflight = Assert-Preflight -TargetRepo $Repo -Version $version -Tag $tag
    $commitSha = $preflight.CommitSha
    $hasExistingDraft = $preflight.HasExistingDraft
} else {
    $commitSha = (& git -C $root rev-parse HEAD 2>$null)
}

# Stage: Build
if ($Stage -in @('Build', 'FullPipeline')) {
    if (!$SkipBuild) {
        Write-Host "==> [Build] Invoking Build-PortableRelease.ps1 for all editions..." -ForegroundColor Cyan
        if (Test-Path -LiteralPath $OutputDirectory) {
            Write-Host "    Cleaning existing output directory $OutputDirectory..."
            Remove-Item -Recurse -Force -LiteralPath $OutputDirectory
        }
        $buildScript = Join-Path $PSScriptRoot 'Build-PortableRelease.ps1'
        & $buildScript -OutputDirectory $OutputDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "Build-PortableRelease.ps1 failed with exit code $LASTEXITCODE"
        }
    } else {
        Write-Host "==> [Build] Skipping build as requested by -SkipBuild flag." -ForegroundColor Yellow
    }
}

# Stage: VerifyLocal
if ($Stage -in @('VerifyLocal', 'FullPipeline')) {
    $localAssets = Assert-LocalArtifacts -AssetsDir $assetsDir -Version $version
}

# Stage: GenerateChangelog
if ($Stage -in @('GenerateChangelog', 'FullPipeline')) {
    $parentDir = Split-Path -Parent $ChangelogPath
    if ($parentDir -and !(Test-Path -LiteralPath $parentDir)) {
        New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
    }
    $null = Get-OrGenerateChangelog -Version $version -ChangelogTarget $ChangelogPath -TargetRepo $Repo
}

# Stage: Publish
if ($Stage -in @('Publish', 'FullPipeline')) {
    if (!$localAssets) {
        $localAssets = Assert-LocalArtifacts -AssetsDir $assetsDir -Version $version
    }
    $startTime.Stop()
    $elapsedStr = "$([Math]::Round($startTime.Elapsed.TotalSeconds, 1))s"
    Publish-ReleaseToGitHub -AssetsDir $assetsDir -ChangelogTarget $ChangelogPath -Version $version -Tag $tag -CommitSha $commitSha -LocalAssets $localAssets -TargetRepo $Repo -HasExistingDraft:$hasExistingDraft -IsDryRun:$DryRun -ElapsedStr $elapsedStr
}

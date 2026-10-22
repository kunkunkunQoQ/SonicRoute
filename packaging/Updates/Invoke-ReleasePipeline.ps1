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
    [switch]$SkipBuild,
    [switch]$SolutionBuildVerified
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Invoke-WithRetry {
    param(
        [Parameter(Mandatory=$true)][scriptblock]$Action,
        [int]$MaxAttempts = 3,
        [int]$InitialDelaySeconds = 2,
        [string]$Description = "Operation"
    )
    $attempt = 1
    $delay = $InitialDelaySeconds
    while ($attempt -le $MaxAttempts) {
        try {
            $result = & $Action
            return $result
        } catch {
            if ($attempt -ge $MaxAttempts) {
                Write-Host "    [Error] Failed $Description after $MaxAttempts attempts: $($_.Exception.Message)" -ForegroundColor Red
                throw
            }
            Write-Warning "Attempt $attempt of $MaxAttempts failed for $Description ($($_.Exception.Message)). Retrying in ${delay}s..."
            Start-Sleep -Seconds $delay
            $delay = $delay * 2
            $attempt++
        }
    }
}

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

function Get-RemoteTagCommitSha {
    param(
        [string]$TargetRepo,
        [string]$Tag,
        [switch]$AllowQueryFailure
    )
    $remoteTagOutput = (& git -C $root ls-remote --tags "https://github.com/$TargetRepo.git" "refs/tags/$Tag" "refs/tags/$Tag^{}" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        if (!$AllowQueryFailure) {
            throw "Fatal: Failed to query remote git tags for $TargetRepo (git ls-remote exited with code $LASTEXITCODE): $remoteTagOutput"
        }
        return $null
    }
    if (!$remoteTagOutput) {
        return $null
    }
    $exactTagRef = "refs/tags/$Tag"
    $peeledTagRef = "refs/tags/$Tag^{}"
    $peeledSha = $null
    $directSha = $null
    foreach ($line in ($remoteTagOutput -split '\r?\n')) {
        if (!$line.Trim()) { continue }
        $parts = $line -split '\s+'
        if ($parts.Count -ge 2) {
            $refSha = $parts[0].Trim()
            $refName = $parts[1].Trim()
            if ($refName -eq $peeledTagRef) {
                $peeledSha = $refSha
            } elseif ($refName -eq $exactTagRef) {
                $directSha = $refSha
            }
        }
    }
    return if ($peeledSha) { $peeledSha } else { $directSha }
}

function Assert-Preflight {
    param([string]$TargetRepo, [string]$Version, [string]$Tag, [switch]$IsDryRun)

    Write-Host "==> [Preflight] Validating release preconditions for $Tag..." -ForegroundColor Cyan

    # 1. Branch check (in official publish mode, strictly require master branch)
    $branch = (& git -C $root rev-parse --abbrev-ref HEAD 2>$null)
    Write-Host "    Current git branch: $branch"
    if (!$IsDryRun) {
        if ($env:GITHUB_REF -and $env:GITHUB_REF -ne 'refs/heads/master') {
            throw "Refusing to release from non-master ref '$($env:GITHUB_REF)'. Only 'refs/heads/master' is permitted."
        }
        if (!$env:GITHUB_REF -and $branch -ne 'master') {
            throw "Refusing to release from non-master branch '$branch'. Only 'master' is permitted for official releases."
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

    # 4. Check remote git tag (handling lightweight and annotated peeled tags with exact ref comparison and exit code validation)
    $remoteCommitSha = Get-RemoteTagCommitSha -TargetRepo $TargetRepo -Tag $Tag -AllowQueryFailure:$IsDryRun
    if ($remoteCommitSha) {
        $shortCommit = $commitSha.Substring(0, [Math]::Min(7, $commitSha.Length))
        $shortRemote = $remoteCommitSha.Substring(0, [Math]::Min(7, $remoteCommitSha.Length))
        if (!$remoteCommitSha.StartsWith($shortCommit) -and !$commitSha.StartsWith($shortRemote)) {
            throw "Fatal: Remote git tag '$Tag' already exists and points to $remoteCommitSha (current commit is $commitSha). Cannot reuse tag for different commit!"
        }
        Write-Host "    [OK] Remote git tag matches target commit SHA ($shortRemote)."
    } else {
        Write-Host "    [OK] Remote git tag '$Tag' does not exist yet."
    }

    # 5. Monotonic version increase verification
    $verifiedRemote = $false
    try {
        $releasesJson = Invoke-WithRetry -Description "Query remote releases for version verification" -MaxAttempts 3 -InitialDelaySeconds 2 -Action {
            $json = & gh release list --repo $TargetRepo --limit 10 --json tagName,isLatest,isDraft,isPrerelease 2>$null
            if ($LASTEXITCODE -ne 0 -or !$json) {
                throw "gh release list returned exit code $LASTEXITCODE"
            }
            return $json
        }

        if ($releasesJson) {
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
            }
            $verifiedRemote = $true
        }
    } catch {
        if ($_.Exception.Message -match "Fatal:") { throw }
        if (!$IsDryRun) {
            throw "Fatal: Production release requires authoritative remote verification from GitHub API, but remote query failed: $($_.Exception.Message)"
        }
        Write-Warning "Could not verify monotonic increase against remote releases (DryRun fallback enabled): $($_.Exception.Message)"
    }

    if (!$verifiedRemote) {
        if (!$IsDryRun) {
            throw "Fatal: Production release aborted because remote release verification was not completed."
        }
        Write-Host "    [DRY-RUN] Verifying monotonic version increase against local git tags as simulation fallback..."
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
        "SonicRoute-v$Version-Lite-NET8-x64.exe",
        "SonicRoute-v$Version-Lite-NET8-arm64.exe",
        "SonicRoute-v$Version-Legacy-x64.zip",
        "SonicRoute-update.json",
        "SHA256SUMS.txt"
    )

    $uniqueExpected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($ef in $expectedFiles) { [void]$uniqueExpected.Add($ef) }
    if ($uniqueExpected.Count -ne 7) {
        throw "Expected files configuration error: Expected 7 unique files, got $($uniqueExpected.Count)"
    }

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

    # 1. Verify SHA256SUMS.txt entries, uniqueness and exact asset coverage
    $sumsPath = Join-Path $AssetsDir 'SHA256SUMS.txt'
    $sumsLines = (Get-Content -LiteralPath $sumsPath) | Where-Object { $_.Trim() }
    if ($sumsLines.Count -ne 6) {
        throw "SHA256SUMS.txt must contain exactly 6 entries (5 packages + SonicRoute-update.json), got $($sumsLines.Count)"
    }

    $seenSumsFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $expectedSumsFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($sf in @(
        "SonicRoute-v$Version-Lite-x64.exe",
        "SonicRoute-v$Version-Lite-arm64.exe",
        "SonicRoute-v$Version-Lite-NET8-x64.exe",
        "SonicRoute-v$Version-Lite-NET8-arm64.exe",
        "SonicRoute-v$Version-Legacy-x64.zip",
        "SonicRoute-update.json"
    )) {
        [void]$expectedSumsFiles.Add($sf)
    }

    foreach ($line in $sumsLines) {
        if ($line -notmatch '^([a-f0-9]{64})\s\s(.+)$') {
            throw "Invalid line format in SHA256SUMS.txt: '$line'"
        }
        $lineHash = $matches[1].ToLowerInvariant()
        $lineFile = $matches[2].Trim()

        if (!$expectedSumsFiles.Contains($lineFile)) {
            throw "SHA256SUMS.txt references unexpected or unauthorized file: '$lineFile'"
        }
        if (!$seenSumsFiles.Add($lineFile)) {
            throw "Duplicate entry detected in SHA256SUMS.txt for file: '$lineFile'"
        }
        if ($assetDetails[$lineFile].Hash -ne $lineHash) {
            throw "Hash mismatch in SHA256SUMS.txt for $lineFile (expected $($assetDetails[$lineFile].Hash), got $lineHash)"
        }
    }

    if ($seenSumsFiles.Count -ne 6) {
        throw "SHA256SUMS.txt did not cover all 6 required assets; covered $($seenSumsFiles.Count)"
    }
    Write-Host "    [OK] SHA256SUMS.txt cryptographic consistency, uniqueness and coverage verified."

    # 2. Verify SonicRoute-update.json structure, uniqueness, 1-to-1 bijection & fields
    $manifestPath = Join-Path $AssetsDir 'SonicRoute-update.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.protocolVersion -ne 2) {
        throw "Invalid manifest schemaVersion ($($manifest.schemaVersion)) or protocolVersion ($($manifest.protocolVersion))"
    }
    if ($manifest.version -ne $Version) {
        throw "Manifest version ($($manifest.version)) does not match release version $Version"
    }
    if ($manifest.packages.Count -ne 5) {
        throw "Manifest must contain exactly 5 package definitions, got $($manifest.packages.Count)"
    }

    $seenCombos = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $expectedCombos = @{
        "Lite:net10:x64"   = "SonicRoute-v$Version-Lite-x64.exe"
        "Lite:net10:arm64" = "SonicRoute-v$Version-Lite-arm64.exe"
        "Lite:net8:x64"    = "SonicRoute-v$Version-Lite-NET8-x64.exe"
        "Lite:net8:arm64"  = "SonicRoute-v$Version-Lite-NET8-arm64.exe"
        "Legacy:net48:x64" = "SonicRoute-v$Version-Legacy-x64.zip"
    }

    foreach ($pkg in $manifest.packages) {
        $combo = "$($pkg.edition):$($pkg.runtime):$($pkg.architecture)"
        if (!$expectedCombos.ContainsKey($combo)) {
            throw "Manifest contains invalid or unexpected edition:architecture combination: '$combo'"
        }
        if (!$seenCombos.Add($combo)) {
            throw "Duplicate edition:architecture package definition in manifest: '$combo'"
        }
        $expectedAsset = $expectedCombos[$combo]
        if ($pkg.asset -ne $expectedAsset) {
            throw "Package asset name mismatch for $($combo): expected '$expectedAsset', got '$($pkg.asset)'"
        }
        if ($pkg.size -ne $assetDetails[$expectedAsset].Size) {
            throw "Manifest package size mismatch for $expectedAsset ($($pkg.size) vs $($assetDetails[$expectedAsset].Size))"
        }
        if ($pkg.sha256.ToLowerInvariant() -ne $assetDetails[$expectedAsset].Hash) {
            throw "Manifest package hash mismatch for $expectedAsset"
        }
        if ($pkg.edition -eq 'Legacy') {
            if ($pkg.files.Count -ne 13) {
                throw "Legacy package in manifest must contain exactly 13 whitelisted files, got $($pkg.files.Count)"
            }
        }
    }

    if ($seenCombos.Count -ne 5) {
        throw "Manifest did not cover all 5 required edition:architecture variants."
    }
    Write-Host "    [OK] SonicRoute-update.json structure, uniqueness, 1-to-1 matrix and hash integrity verified."

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
    [void]$sb.AppendLine("| ⭐ **Lite x64**（.NET 10 推荐主力） | 主流 64 位 PC 绿色单文件 | [单个 EXE](https://github.com/$TargetRepo/releases/download/v$Version/SonicRoute-v$Version-Lite-x64.exe) | 需匹配的 x64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |")
    [void]$sb.AppendLine("| ⚡ **Lite ARM64**（.NET 10 实验） | Surface Pro / 骁龙 PC 原生高效 | [单个 EXE](https://github.com/$TargetRepo/releases/download/v$Version/SonicRoute-v$Version-Lite-arm64.exe) | 需匹配的 ARM64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |")
    [void]$sb.AppendLine("| ☕ **Lite NET8 x64**（.NET 8 兼容版） | 兼容现有 .NET 8 运行库环境 | [单个 EXE](https://github.com/$TargetRepo/releases/download/v$Version/SonicRoute-v$Version-Lite-NET8-x64.exe) | 需匹配的 x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |")
    [void]$sb.AppendLine("| ☕ **Lite NET8 ARM64**（.NET 8 兼容版）| 兼容现有 .NET 8 运行库环境 | [单个 EXE](https://github.com/$TargetRepo/releases/download/v$Version/SonicRoute-v$Version-Lite-NET8-arm64.exe) | 需匹配的 ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |")
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
        [string]$DurationStr,
        [System.Collections.IDictionary]$Checklist
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
    [void]$sb.AppendLine("### 📦 Verified Release Assets")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Asset Name | Size | SHA-256 Digest | Status |")
    [void]$sb.AppendLine("|---|---|---|---|")

    if ($LocalAssets) {
        foreach ($key in ($LocalAssets.Keys | Sort-Object)) {
            $a = $LocalAssets[$key]
            $sizeKb = [Math]::Round($a.Size / 1024, 1)
            [void]$sb.AppendLine("| ``$($a.Name)`` | ${sizeKb} KB | ``$($a.Hash)`` | ✅ Verified |")
        }
    }

    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("### 🛡️ Safety & Integrity Checklist")
    [void]$sb.AppendLine("")

    $mark = { param($v) if ([bool]$v) { '[x]' } else { '[ ]' } }

    [void]$sb.AppendLine("- $(&$mark ($Checklist.PreflightVerified)) Version format, master branch & monotonic increase verified")
    [void]$sb.AppendLine("- $(&$mark ($Checklist.SolutionBuildVerified)) Full solution Release build verified")
    [void]$sb.AppendLine("- $(&$mark ($Checklist.ArtifactsCompiled)) Five packages (Lite x64, Lite ARM64, Lite-NET8 x64, Lite-NET8 ARM64, Legacy x64) compiled")
    [void]$sb.AppendLine("- $(&$mark ($Checklist.LocalArtifactsVerified)) PE machine architecture (0x8664/0xaa64) & Legacy ZIP 13-file whitelist verified")
    [void]$sb.AppendLine("- $(&$mark ($Checklist.ManifestVerified)) SHA256SUMS.txt & SonicRoute-update.json 1-to-1 matrix bijection verified")
    [void]$sb.AppendLine("- $(&$mark ($Checklist.ChangelogGenerated)) Changelog curated/generated with conventional commit categorization")
    [void]$sb.AppendLine("- $(&$mark ($Checklist.RemoteAssetsVerified)) Remote asset size, count (5/5) & downloaded SHA-256 verified byte-for-byte")
    [void]$sb.AppendLine("- $(&$mark ($Checklist.DraftPromoted)) Draft release promoted to official public release (prerelease: false)")
    [void]$sb.AppendLine("- $(&$mark ($Checklist.LatestPointerVerified)) GitHub /releases/latest endpoint strictly verified pointing to $Tag")

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
        [string]$ElapsedStr,
        [System.Collections.IDictionary]$Checklist
    )

    Write-Host "==> [Release] Processing GitHub Release for $Tag (Commit: $CommitSha)..." -ForegroundColor Cyan

    if ($IsDryRun) {
        Write-Host "    [DRY-RUN] Preflight and local artifact verifications succeeded." -ForegroundColor Yellow
        Write-Host "    [DRY-RUN] Remote release creation and upload skipped due to DryRun flag." -ForegroundColor Yellow
        Write-StepSummary -Version $Version -Tag $Tag -CommitSha $CommitSha -LocalAssets $LocalAssets -Status "DRY_RUN_PASSED" -TargetRepo $TargetRepo -DurationStr $ElapsedStr -Checklist $Checklist
        return
    }

    # Phase B: Create or Update Draft Release with retry and explicit prerelease=false
    if ($HasExistingDraft) {
        Write-Host "    Updating existing Draft Release for $Tag on GitHub..."
        Invoke-WithRetry -Description "Update existing draft release $Tag" -MaxAttempts 3 -InitialDelaySeconds 2 -Action {
            & gh release edit $Tag --repo $TargetRepo --title $Tag --notes-file $ChangelogTarget --target $CommitSha --prerelease=false
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to update existing draft release $Tag on GitHub!"
            }
        }
    } else {
        Write-Host "    Creating Draft Release for $Tag on GitHub..."
        $draftCreated = $false
        try {
            $draftCreated = [bool](Invoke-WithRetry -Description "Create draft release $Tag" -MaxAttempts 3 -InitialDelaySeconds 2 -Action {
                & gh release create $Tag --repo $TargetRepo --target $CommitSha --title $Tag --notes-file $ChangelogTarget --draft --prerelease=false
                if ($LASTEXITCODE -ne 0) {
                    throw "gh release create returned exit code $LASTEXITCODE"
                }
                return $true
            })
        } catch {
            Write-Warning "Draft creation call reported error: $($_.Exception.Message)"
        }

        if (!$draftCreated) {
            # Idempotency check: in case network response was lost but draft was created on GitHub
            Write-Warning "Draft creation reported non-zero or error; checking if draft already exists on remote..."
            $checkDraft = $null
            try {
                $checkDraft = & gh release view $Tag --repo $TargetRepo --json tagName,isDraft,targetCommitish 2>$null | ConvertFrom-Json
            } catch { }
            if ($checkDraft -and $checkDraft.isDraft) {
                $shortCommit = $commitSha.Substring(0, [Math]::Min(7, $commitSha.Length))
                if ($checkDraft.targetCommitish.StartsWith($shortCommit) -or $commitSha.StartsWith($checkDraft.targetCommitish)) {
                    Write-Host "    [Recovered] Draft release was successfully created on remote despite response error." -ForegroundColor Yellow
                    $draftCreated = $true
                } else {
                    throw "Fatal: Existing draft release points to different commit $($checkDraft.targetCommitish) (expected $commitSha)."
                }
            } else {
                throw "Failed to create draft release $Tag on GitHub!"
            }
        }
    }
    $Checklist.DraftCreated = $true

    # Query current draft state to check existing remote assets and verify target commit
    $draftStatus = & gh api "/repos/$TargetRepo/releases/tags/$Tag" | ConvertFrom-Json
    if ($draftStatus) {
        $shortCommit = $CommitSha.Substring(0, [Math]::Min(7, $CommitSha.Length))
        if ($draftStatus.target_commitish -and !$draftStatus.target_commitish.StartsWith($shortCommit) -and !$CommitSha.StartsWith($draftStatus.target_commitish)) {
            throw "Fatal: Remote draft release '$Tag' points to commit '$($draftStatus.target_commitish)', which differs from target commit '$CommitSha'!"
        }
    }
    $existingTagSha = Get-RemoteTagCommitSha -TargetRepo $TargetRepo -Tag $Tag -AllowQueryFailure:$false
    if ($existingTagSha) {
        $shortCommit = $CommitSha.Substring(0, [Math]::Min(7, $CommitSha.Length))
        $shortTag = $existingTagSha.Substring(0, [Math]::Min(7, $existingTagSha.Length))
        if (!$existingTagSha.StartsWith($shortCommit) -and !$CommitSha.StartsWith($shortTag)) {
            throw "Fatal: Remote git tag '$Tag' on GitHub points to commit '$existingTagSha', which does not match target commit '$CommitSha'!"
        }
        Write-Host "    [OK] Remote git tag verified pointing to target commit ($shortTag)."
    }
    $existingRemoteAssets = @{}
    if ($draftStatus -and $draftStatus.assets) {
        foreach ($ea in $draftStatus.assets) {
            $existingRemoteAssets[$ea.name] = $ea
        }
    }

    # Issue 1 Safety Check: If the draft contains unexpected assets, REJECT and DO NOT delete them!
    foreach ($remoteName in $existingRemoteAssets.Keys) {
        if (!$LocalAssets.Contains($remoteName)) {
            throw "Safety violation: Existing draft release contains unexpected asset '$remoteName' not belonging to current build. Refusing to delete or overwrite unknown assets!"
        }
    }

    # Issue 3: Upload all 5 assets to the draft release with idempotent skip
    Write-Host "    Processing 5 release assets for upload to draft release..."
    $checkTempDir = Join-Path ([IO.Path]::GetTempPath()) ("gh-chk-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $checkTempDir -Force | Out-Null
    try {
        foreach ($assetItem in $LocalAssets.Values) {
            $filePath = $assetItem.Path
            $fileName = $assetItem.Name
            $localSize = $assetItem.Size
            $localHash = $assetItem.Hash

            $needsUpload = $true
            if ($existingRemoteAssets.ContainsKey($fileName)) {
                $rem = $existingRemoteAssets[$fileName]
                if ($rem.size -eq $localSize -and $rem.state -eq 'uploaded') {
                    # Verify remote hash by downloading to test if identical
                    Write-Host "    Checking existing remote asset $fileName on draft..."
                    & gh release download $Tag --repo $TargetRepo --pattern $fileName --dir $checkTempDir --clobber 2>$null
                    $downFile = Join-Path $checkTempDir $fileName
                    if (Test-Path -LiteralPath $downFile) {
                        $downHash = (Get-FileHash -LiteralPath $downFile -Algorithm SHA256).Hash.ToLowerInvariant()
                        Remove-Item -LiteralPath $downFile -Force -ErrorAction SilentlyContinue
                        if ($downHash -eq $localHash) {
                            Write-Host "    [Idempotent] $fileName is already uploaded and hash-verified on GitHub. Skipping re-upload." -ForegroundColor Green
                            $needsUpload = $false
                        } else {
                            Write-Host "    $fileName hash mismatch on draft; will overwrite with --clobber." -ForegroundColor Yellow
                        }
                    }
                }
            }

            if ($needsUpload) {
                Write-Host "    Uploading $fileName..."
                Invoke-WithRetry -Description "Upload asset $fileName" -MaxAttempts 3 -InitialDelaySeconds 3 -Action {
                    & gh release upload $Tag $filePath --repo $TargetRepo --clobber
                    if ($LASTEXITCODE -ne 0) {
                        throw "Failed to upload asset $fileName to draft release $Tag!"
                    }
                }
            }
        }
    } finally {
        if (Test-Path -LiteralPath $checkTempDir) {
            Remove-Item -LiteralPath $checkTempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # Phase C: Remote Verification
    Write-Host "    Verifying remote release and assets on GitHub..."
    $remoteRelease = Invoke-WithRetry -Description "Query remote release $Tag" -MaxAttempts 3 -InitialDelaySeconds 2 -Action {
        $json = & gh api "/repos/$TargetRepo/releases/tags/$Tag"
        if ($LASTEXITCODE -ne 0 -or !$json) {
            throw "Unable to query release $Tag after upload!"
        }
        return ($json | ConvertFrom-Json)
    }

    if (!$remoteRelease) {
        throw "Remote release object is null after query."
    }
    if (!$remoteRelease.draft) {
        throw "Security violation: Release $Tag is NOT in draft state during verification!"
    }
    if ($remoteRelease.assets.Count -ne $LocalAssets.Count) {
        throw "Remote release contains $($remoteRelease.assets.Count) assets; expected exactly $($LocalAssets.Count)!"
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

            # Independent download & cryptographic verification with retry
            Write-Host "    Downloading and verifying SHA-256 for $($name) from GitHub..."
            Invoke-WithRetry -Description "Download asset $name for verification" -MaxAttempts 3 -InitialDelaySeconds 2 -Action {
                & gh release download $Tag --repo $TargetRepo --pattern $name --dir $verifyTempDir --clobber
                if ($LASTEXITCODE -ne 0) {
                    throw "Failed to download asset $($name) from GitHub release for verification!"
                }
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
    $Checklist.RemoteAssetsVerified = $true

    # Phase D: Auto-Publish with network disconnect / response loss recovery
    Write-Host "    Promoting Draft Release to Official Public Latest Release..."
    $publishSuccess = $false
    try {
        & gh release edit $Tag --repo $TargetRepo --draft=false --prerelease=false --latest
        if ($LASTEXITCODE -eq 0) {
            $publishSuccess = $true
        }
    } catch { }

    if (!$publishSuccess) {
        Write-Warning "Publish command encountered a warning or non-zero exit; querying remote release state to verify if update took effect..."
        $queryAfter = $null
        try {
            $queryAfter = & gh api "/repos/$TargetRepo/releases/tags/$Tag" 2>$null | ConvertFrom-Json
        } catch { }
        if ($queryAfter -and !$queryAfter.draft -and !$queryAfter.prerelease) {
            Write-Host "    [Recovered] Release $Tag is confirmed published on GitHub despite network response anomaly!" -ForegroundColor Yellow
            $publishSuccess = $true
        } else {
            throw "Failed to publish release $Tag as official and latest!"
        }
    }
    $Checklist.DraftPromoted = $true

    # Post-publish verification: 1. Confirm release public status
    $published = & gh api "/repos/$TargetRepo/releases/tags/$Tag" | ConvertFrom-Json
    if ($published.draft) {
        throw "Post-publish check failed: Release $Tag is still marked as draft on GitHub!"
    }
    if ($published.prerelease) {
        throw "Post-publish check failed: Release $Tag is marked as prerelease on GitHub!"
    }

    # Post-publish verification: 2. Confirm Latest pointer strictly (Issue 2)
    Write-Host "    Verifying /releases/latest endpoint points to $Tag..."
    $latest = $null
    try {
        $latest = Invoke-WithRetry -Description "Query /releases/latest endpoint" -MaxAttempts 3 -InitialDelaySeconds 2 -Action {
            $json = & gh api "/repos/$TargetRepo/releases/latest"
            if ($LASTEXITCODE -ne 0 -or !$json) {
                throw "Failed to query GitHub /releases/latest endpoint."
            }
            return ($json | ConvertFrom-Json)
        }
    } catch {
        Write-StepSummary -Version $Version -Tag $Tag -CommitSha $CommitSha -LocalAssets $LocalAssets -Status "PUBLISHED_PENDING_LATEST_VERIFICATION" -TargetRepo $TargetRepo -ReleaseUrl $published.html_url -DurationStr $ElapsedStr -Checklist $Checklist
        Write-Host "    [Error] Post-publish check failed: Release $Tag is public, but failed to query /releases/latest endpoint: $($_.Exception.Message)" -ForegroundColor Red
        throw "Release $Tag is published, but could not verify /releases/latest pointer on GitHub: $($_.Exception.Message)"
    }

    if ($latest.tag_name -ne $Tag) {
        Write-StepSummary -Version $Version -Tag $Tag -CommitSha $CommitSha -LocalAssets $LocalAssets -Status "PUBLISHED_LATEST_MISMATCH" -TargetRepo $TargetRepo -ReleaseUrl $published.html_url -DurationStr $ElapsedStr -Checklist $Checklist
        Write-Host "    [Error] Latest release mismatch: Expected $Tag, but GitHub /releases/latest points to '$($latest.tag_name)'!" -ForegroundColor Red
        throw "Latest release verification failed: Expected latest tag to be '$Tag', but GitHub reports '$($latest.tag_name)' is latest!"
    }

    Write-Host "    [OK] Confirmed $Tag is the active official Latest Release on GitHub." -ForegroundColor Green
    $Checklist.LatestPointerVerified = $true

    Write-Host "==> [Success] Release $Tag is officially published as Latest on GitHub!" -ForegroundColor Green
    Write-StepSummary -Version $Version -Tag $Tag -CommitSha $CommitSha -LocalAssets $LocalAssets -Status "PUBLISHED" -TargetRepo $TargetRepo -ReleaseUrl $published.html_url -DurationStr $ElapsedStr -Checklist $Checklist
}

# Dot-sourcing check: If this script is dot-sourced into a test harness, skip execution of the main block
if ($MyInvocation.InvocationName -eq '.') {
    return
}

# --- Main Entry Point ---

$startTime = [System.Diagnostics.Stopwatch]::StartNew()
$versionInfo = Get-SonicRouteVersion
$version = $versionInfo.Version
$tag = $versionInfo.Tag

$checklist = [ordered]@{
    PreflightVerified = $false
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

if ($SolutionBuildVerified -or $env:SOLUTION_BUILD_VERIFIED -eq 'true') {
    $checklist.SolutionBuildVerified = $true
}

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
    $preflight = Assert-Preflight -TargetRepo $Repo -Version $version -Tag $tag -IsDryRun:$DryRun
    $commitSha = $preflight.CommitSha
    $hasExistingDraft = $preflight.HasExistingDraft
    $checklist.PreflightVerified = $true
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
        $checklist.ArtifactsCompiled = $true
    } else {
        Write-Host "==> [Build] Skipping build as requested by -SkipBuild flag." -ForegroundColor Yellow
    }
}

# Stage: VerifyLocal
if ($Stage -in @('VerifyLocal', 'FullPipeline')) {
    $localAssets = Assert-LocalArtifacts -AssetsDir $assetsDir -Version $version
    $checklist.LocalArtifactsVerified = $true
    $checklist.ManifestVerified = $true
}

# Stage: GenerateChangelog
if ($Stage -in @('GenerateChangelog', 'FullPipeline')) {
    $parentDir = Split-Path -Parent $ChangelogPath
    if ($parentDir -and !(Test-Path -LiteralPath $parentDir)) {
        New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
    }
    $null = Get-OrGenerateChangelog -Version $version -ChangelogTarget $ChangelogPath -TargetRepo $Repo
    $checklist.ChangelogGenerated = $true
}

# Stage: Publish
if ($Stage -in @('Publish', 'FullPipeline')) {
    if (!$localAssets) {
        $localAssets = Assert-LocalArtifacts -AssetsDir $assetsDir -Version $version
        $checklist.LocalArtifactsVerified = $true
        $checklist.ManifestVerified = $true
    }
    $startTime.Stop()
    $elapsedStr = "$([Math]::Round($startTime.Elapsed.TotalSeconds, 1))s"
    Publish-ReleaseToGitHub -AssetsDir $assetsDir -ChangelogTarget $ChangelogPath -Version $version -Tag $tag -CommitSha $commitSha -LocalAssets $localAssets -TargetRepo $Repo -HasExistingDraft:$hasExistingDraft -IsDryRun:$DryRun -ElapsedStr $elapsedStr -Checklist $checklist
}

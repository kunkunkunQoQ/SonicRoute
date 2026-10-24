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

. "$PSScriptRoot/ReleaseLayout.ps1"

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

function Invoke-Utf8Native([scriptblock]$Action) {
    $previous=[Console]::OutputEncoding
    try {
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        & $Action
    } finally { [Console]::OutputEncoding=$previous }
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
    if ($peeledSha) { return $peeledSha }
    return $directSha
}

function Read-RemoteReleaseHistory([string]$TargetRepo) {
    $all=@()
    for($page=1;$page -le 100;$page++){
        $batch=@(Invoke-WithRetry -Description "Read release history page $page" -Action {
            $json=Invoke-Utf8Native { & gh api "/repos/$TargetRepo/releases?per_page=100&page=$page" }
            if($LASTEXITCODE -ne 0 -or !$json){throw 'GitHub release history query failed.'}
            $items=($json-join "`n")|ConvertFrom-Json
            if($null -ne $items){foreach($item in @($items)){if(!$item -or !$item.tag_name -or $item.draft -isnot [bool] -or $item.prerelease -isnot [bool]){throw 'Invalid release history response.'}}}
            return $items
        })
        $all+=$batch
        if($batch.Count -lt 100){return $all}
    }
    throw 'Release history exceeds the bounded pagination limit.'
}

function Read-RemoteRelease([string]$TargetRepo,[string]$Tag,[long]$ReleaseId=0) {
    # Bind all post-create operations to the server's ID. A successful create does
    # not guarantee immediate visibility through tag or release-list lookups.
    return Invoke-WithRetry -Description "Read release $Tag" -Action {
        if($ReleaseId -gt 0){
            $json=Invoke-Utf8Native { & gh api "/repos/$TargetRepo/releases/$ReleaseId" }
            if($LASTEXITCODE -ne 0 -or !$json){throw 'GitHub release ID query failed.'}
            $release=($json -join "`n")|ConvertFrom-Json
            if($release.id -ne $ReleaseId){throw 'GitHub returned a different release ID.'}
            return $release
        }
        $found=@(Read-RemoteReleaseHistory $TargetRepo|Where-Object{$_.tag_name -ceq $Tag})
        if($found.Count -gt 1){throw 'Duplicate release tag in server response.'}
        if($found.Count -ne 1){throw "GitHub release query failed: $Tag was not found."}
        return $found[0]
    }
}

function Save-RemoteReleaseAsset([string]$TargetRepo,[long]$AssetId,[string]$Destination) {
    if($TargetRepo -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $AssetId -le 0){throw 'Invalid asset download identity.'}
    # PowerShell 5 redirects native stdout as text. Copy the gh binary stream
    # directly, retaining gh's authentication and redirect handling.
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Command gh -CommandType Application).Source,
        "api /repos/$TargetRepo/releases/assets/$AssetId -H `"Accept: application/octet-stream`"")
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    try{
        $errors=$process.StandardError.ReadToEndAsync()
        $file=[IO.File]::Open($Destination,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$process.StandardOutput.BaseStream.CopyTo($file)}finally{$file.Dispose()}
        $process.WaitForExit();$null=$errors.GetAwaiter().GetResult()
        if($process.ExitCode -ne 0){throw 'GitHub release asset download failed.'}
    }finally{
        if(!$process.HasExited){$process.Kill();$process.WaitForExit()}
        $process.Dispose()
    }
}

function Assert-RemoteDraft($Release,[string]$Tag,[string]$CommitSha,$LocalAssets) {
    if(!$Release -or $Release.tag_name -cne $Tag -or $Release.draft -ne $true -or $Release.prerelease -ne $false -or
       $CommitSha -cnotmatch '^[a-f0-9]{40}$' -or $Release.target_commitish -cne $CommitSha){throw 'Refusing to modify a release without a matching draft and full commit SHA.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($asset in @($Release.assets)){
        if(!$asset -or !$seen.Add($asset.name) -or ($LocalAssets -and @($LocalAssets.Keys) -cnotcontains $asset.name)){throw 'Unknown or duplicate asset on existing draft; nothing will be deleted.'}
    }
}

function Assert-RemoteAssetMetadata($Release,$LocalAssets) {
    if(@($Release.assets).Count -ne $LocalAssets.Count){throw 'Remote asset count does not match the validated seven-file set.'}
    foreach($asset in $Release.assets){
        if(@($LocalAssets.Keys) -cnotcontains $asset.name -or $asset.size -ne $LocalAssets[$asset.name].Size -or
           $asset.state -cne 'uploaded' -or $asset.digest -cne ('sha256:'+$LocalAssets[$asset.name].Hash)){throw 'Remote asset identity/size/digest changed before publication.'}
    }
}

function Assert-BuildReceipt([string]$OutputDirectory,[string]$Version,[string]$CommitSha,$LocalAssets) {
    $path=Join-Path $OutputDirectory 'build-receipt.json'
    if(!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -gt 64KB){throw 'Missing or invalid build receipt. Build a clean candidate before publishing or running a full DryRun.'}
    $receipt=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
    $source=Get-ReleaseSourceState $root
    if($receipt.schemaVersion -ne 1 -or $receipt.version -cne $Version -or $receipt.commit -cne $CommitSha -or
       $receipt.tree -cne $source.Tree -or $source.Commit -cne $CommitSha -or !$source.Clean -or $receipt.clean -ne $true -or
       $receipt.solutionBuildVerified -ne $true -or @($receipt.assets).Count -ne $LocalAssets.Count){throw 'Candidate receipt does not match a clean, solution-verified source commit.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($asset in $receipt.assets){
        if(!$seen.Add($asset.name) -or @($LocalAssets.Keys) -cnotcontains $asset.name -or $asset.size -ne $LocalAssets[$asset.name].Size -or $asset.sha256 -ne $LocalAssets[$asset.name].Hash){throw 'Candidate assets differ from their build receipt.'}
    }
    return $receipt
}

function Assert-Preflight {
    param([string]$TargetRepo,[string]$Version,[string]$Tag,[switch]$IsDryRun)
    Write-Host "==> [Preflight] Validating source, full commit binding and release history..." -ForegroundColor Cyan
    if($TargetRepo -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Version -notmatch '^\d+\.\d+(\.\d+)?$' -or $Tag -cne "v$Version"){throw 'Invalid repository/version/tag.'}
    $source=Get-ReleaseSourceState $root
    $branch=(& git -C $root rev-parse --abbrev-ref HEAD|Out-String).Trim()
    if($LASTEXITCODE -ne 0){throw 'Cannot identify source branch.'}
    if(!$IsDryRun){
        if(!$source.Clean){throw 'Production publishing requires a clean source tree, including untracked files.'}
        if($env:GITHUB_REF){
            if($env:GITHUB_REF -cne 'refs/heads/master' -or $env:GITHUB_SHA -cne $source.Commit -or $env:GITHUB_REPOSITORY -cne $TargetRepo){throw 'CI ref, repository or commit differs from the checked-out source.'}
            if($branch -notin @('HEAD','master')){throw 'Refusing production publish from a non-master source branch.'}
        }elseif($branch -cne 'master'){throw 'Refusing production publish from a non-master source branch.'}
    }
    $all=@();$verified=$false
    try {
        $all=@(Read-RemoteReleaseHistory $TargetRepo)
        $verified=$true
    }catch{
        if(!$IsDryRun -or $env:GITHUB_ACTIONS -eq 'true'){throw "Production/CI validation requires authoritative release history: $($_.Exception.Message)"}
        Write-Warning "DryRun remote history NOT VERIFIED: $($_.Exception.Message)"
    }
    $existing=@($all|Where-Object{$_.tag_name -ceq $Tag})
    if($existing.Count -gt 1){throw 'Duplicate release tag in server response.'}
    $publishedCurrent=$existing.Count -eq 1 -and !$existing[0].draft -and !$existing[0].prerelease
    $readOnlyRevalidation=[bool]($IsDryRun -and $publishedCurrent)
    if($publishedCurrent -and !$IsDryRun){throw 'Published version is immutable; increase the version before production publication.'}
    $hasDraft=$existing.Count -eq 1 -and !$publishedCurrent
    if($hasDraft){Assert-RemoteDraft $existing[0] $Tag $source.Commit $null}
    $remoteTag=Get-RemoteTagCommitSha -TargetRepo $TargetRepo -Tag $Tag -AllowQueryFailure:$IsDryRun
    if($remoteTag -and $remoteTag -cne $source.Commit -and !$readOnlyRevalidation){throw 'Remote tag points to a different full commit SHA.'}
    $parts=($Version.Split('.')+@('0','0','0','0'))[0..3];$current=[version]($parts-join '.')
    $tags=if($verified){@($all|Where-Object{!$_.draft -and !$_.prerelease}|ForEach-Object{$_.tag_name})}else{@(& git -C $root tag -l 'v*')}
    foreach($publishedTag in $tags){
        if($publishedTag -notmatch '^[vV]?(\d+\.\d+(\.\d+)?(\.\d+)?)$'){continue}
        $remoteParts=($Matches[1].Split('.')+@('0','0','0','0'))[0..3]
        $remoteVersion=[version]($remoteParts-join '.')
        if($current -lt $remoteVersion -or ($current -eq $remoteVersion -and !$readOnlyRevalidation)){throw "Version $Version is not newer than published/tagged version $publishedTag."}
    }
    if($readOnlyRevalidation){Write-Host '    Read-only revalidation of an already published version; production publication remains forbidden.' -ForegroundColor Yellow}
    if(!$source.Clean){Write-Warning 'Dirty source: DryRun only; artifacts cannot be used for production publishing.'}
    return [pscustomobject]@{CommitSha=$source.Commit;HasExistingDraft=$hasDraft;RemoteVerified=$verified;SourceClean=$source.Clean;ReadOnlyRevalidation=$readOnlyRevalidation}
}

function Assert-ReleaseNotes([string]$Path,[string]$Version) {
    if(!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -gt 512KB){throw 'Missing or oversized candidate release notes.'}
    $utf8=[Text.UTF8Encoding]::new($false,$true)
    $notes=[IO.File]::ReadAllText($Path,$utf8)
    if([string]::IsNullOrWhiteSpace($notes) -or $notes -match '[\x00-\x08\x0b\x0c\x0e-\x1f]'){throw 'Candidate release notes contain invalid text.'}
    $curated=Join-Path $root "docs/release-v$Version.md"
    if((Test-Path -LiteralPath $curated) -and $notes -cne [IO.File]::ReadAllText($curated,$utf8)){throw 'Candidate release notes differ from the reviewed UTF-8 source; regenerate the changelog.'}
    return $notes
}
function Assert-LocalArtifacts {
    param([string]$AssetsDir, [string]$Version)
    Write-Host "==> [VerifyLocal] Validating bytes, PE/runtime identities and metadata..." -ForegroundColor Cyan
    $matrix=@(Get-ReleaseMatrix $Version)
    $expectedFiles=@($matrix.Asset)+@('SonicRoute-update.json','SHA256SUMS.txt')
    $items=@(Get-ChildItem -LiteralPath $AssetsDir -Force -ErrorAction Stop)
    if($items.Count -ne $expectedFiles.Count -or @($items|Where-Object{$_.PSIsContainer -or $expectedFiles -cnotcontains $_.Name}).Count -ne 0){throw 'Release asset directory must contain exactly the seven expected files.'}
    $assetDetails=[ordered]@{}
    foreach($file in $expectedFiles){
        $path=Join-Path $AssetsDir $file;$item=Get-Item -LiteralPath $path
        if($item.Length -le 0 -or $item.Length -gt 128MB -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw "Invalid release asset: $file"}
        $assetDetails[$file]=@{Name=$file;Path=[IO.Path]::GetFullPath($path);Size=$item.Length;Hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
    $sumNames=@($matrix.Asset)+@('SonicRoute-update.json')
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($line in (Get-Content -LiteralPath (Join-Path $AssetsDir 'SHA256SUMS.txt'))){
        if(!$line){continue}
        if($line -cnotmatch '^([a-fA-F0-9]{64})  ([A-Za-z0-9._-]+)$'){throw 'Invalid SHA256SUMS.txt line.'}
        $hash=$Matches[1];$name=$Matches[2]
        if($sumNames -cnotcontains $name -or !$seen.Add($name)){throw 'Unexpected or duplicate SHA256SUMS.txt entry.'}
        if($assetDetails[$name].Hash -ne $hash){throw "Hash mismatch in SHA256SUMS.txt for $name"}
    }
    if($seen.Count -ne $sumNames.Count){throw 'SHA256SUMS.txt has incomplete coverage.'}
    $manifest=Get-Content -LiteralPath (Join-Path $AssetsDir 'SonicRoute-update.json') -Raw|ConvertFrom-Json
    if(($manifest.schemaVersion -isnot [int] -and $manifest.schemaVersion -isnot [long]) -or
       ($manifest.protocolVersion -isnot [int] -and $manifest.protocolVersion -isnot [long]) -or
       $manifest.schemaVersion -ne 1 -or $manifest.protocolVersion -ne 2 -or $manifest.version -cne $Version){throw 'Invalid manifest schema/protocol/version.'}
    if($manifest.packages.Count -ne $matrix.Count){throw 'Invalid manifest package count.'}
    $seenPackages=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($pkg in $manifest.packages){
        $variant=@($matrix|Where-Object{$_.Edition -ceq $pkg.edition -and $_.Runtime -ceq $pkg.runtime -and $_.Architecture -ceq $pkg.architecture})
        if($variant.Count -ne 1){throw 'Manifest contains invalid or unexpected edition:architecture combination (including runtime).'}
        if(!$seenPackages.Add($pkg.asset)){throw 'Duplicate package definition in manifest.'}
        if(($pkg.size -isnot [int] -and $pkg.size -isnot [long]) -or $pkg.asset -cne $variant[0].Asset -or $pkg.size -ne $assetDetails[$pkg.asset].Size -or $pkg.sha256 -cnotmatch '^[a-fA-F0-9]{64}$' -or $pkg.sha256 -ne $assetDetails[$pkg.asset].Hash){throw 'Manifest package identity/size/hash mismatch.'}
        $files=@(Get-ReleasePackageFiles $variant[0] $AssetsDir $Version)
        if($pkg.files.Count -ne $files.Count){throw 'Manifest per-file count differs from the actual package.'}
        $seenFiles=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($file in $pkg.files){
            $actual=@($files|Where-Object{$_.path -ceq $file.path})
            if(($file.size -isnot [int] -and $file.size -isnot [long]) -or $actual.Count -ne 1 -or !$seenFiles.Add($file.path) -or $actual[0].size -ne $file.size -or $file.sha256 -cnotmatch '^[a-fA-F0-9]{64}$' -or $actual[0].sha256 -ne $file.sha256){throw 'Manifest per-file path/size/hash differs from the actual package.'}
        }
    }
    Write-Host "==> [VerifyLocal] All seven release assets validated." -ForegroundColor Green
    return $assetDetails
}
function Get-OrGenerateChangelog {
    param([string]$Version, [string]$ChangelogTarget, [string]$TargetRepo)

    Write-Host "==> [Changelog] Generating release notes for v$Version..." -ForegroundColor Cyan

    $curatedFile = Join-Path $root "docs/release-v$Version.md"

    # Priority 1: Use curated release note if maintained in docs/
    if (Test-Path -LiteralPath $curatedFile) {
        Write-Host "    Found curated release notes: docs/release-v$Version.md"
        $notes = [IO.File]::ReadAllText($curatedFile, [Text.UTF8Encoding]::new($false, $true))
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
        $releaseJson = Invoke-Utf8Native { & gh release list --repo $TargetRepo --limit 5 --json tagName,isDraft 2>$null }
        $releases = ($releaseJson -join "`n") | ConvertFrom-Json
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
    $commits = Invoke-Utf8Native { & git -C $root log $logRange --pretty=format:"%H%x09%s%x09%an%x09%h" 2>$null }

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

    # Follow the v1.27 layout, deriving the content from this version's commits.
    # Curated notes above remain authoritative and are never reformatted.
    [void]$sb.AppendLine("## ✨ 核心更新亮点")
    [void]$sb.AppendLine("")
    $highlights = @($features + $fixes + $improvements)
    if ($highlights.Count -eq 0) { $highlights = @($others) }
    foreach ($item in ($highlights | Select-Object -First 6)) { [void]$sb.AppendLine($item) }
    if ($highlights.Count -eq 0) { [void]$sb.AppendLine("未提取到可展示的提交记录，请参阅本版人工维护的发布说明。") }
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("---")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## 📋 详细演进说明")
    [void]$sb.AppendLine("")
    $section = 0

    if ($features.Count -gt 0) {
        $section++
        [void]$sb.AppendLine("### $section. 新增功能与特性")
        [void]$sb.AppendLine("")
        foreach ($f in $features) { [void]$sb.AppendLine($f) }
        [void]$sb.AppendLine("")
    }

    if ($fixes.Count -gt 0) {
        $section++
        [void]$sb.AppendLine("### $section. 缺陷修复")
        [void]$sb.AppendLine("")
        foreach ($fx in $fixes) { [void]$sb.AppendLine($fx) }
        [void]$sb.AppendLine("")
    }

    if ($improvements.Count -gt 0) {
        $section++
        [void]$sb.AppendLine("### $section. 体验与性能改进")
        [void]$sb.AppendLine("")
        foreach ($imp in $improvements) { [void]$sb.AppendLine($imp) }
        [void]$sb.AppendLine("")
    }

    if ($others.Count -gt 0) {
        $section++
        [void]$sb.AppendLine("### $section. 其他变更")
        [void]$sb.AppendLine("")
        foreach ($oth in $others) { [void]$sb.AppendLine($oth) }
        [void]$sb.AppendLine("")
    }

    if ($section -eq 0) { [void]$sb.AppendLine("未提取到可展示的提交记录。") }
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("---")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## 📋 升级与验证说明")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("- **更新渠道**：便携版只匹配当前发行类型、运行时与架构；Microsoft Store 版继续由商店更新。自动检查默认关闭。")
    [void]$sb.AppendLine("- **变更来源**：以上内容由本版提交记录自动汇总。功能效果、性能数据及已验证/未验证场景应以本版人工维护的发布说明和测试记录为准。")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("---")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## 🚀 资产下载矩阵")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| 发行形态 | 推荐目标人群 | 下载链接 | 运行环境要求 |")
    [void]$sb.AppendLine("|---|---|---|---|")
    [void]$sb.AppendLine("| ⭐ **Lite x64**（.NET 10 推荐主力） | 主流 64 位 PC 绿色单文件 | [单个 EXE](https://github.com/$TargetRepo/releases/download/v$Version/SonicRoute-v$Version-Lite-x64.exe) | 需匹配的 x64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |")
    [void]$sb.AppendLine("| ⚡ **Lite ARM64**（.NET 10 实验） | Surface Pro / 骁龙 PC 原生高效 | [单个 EXE](https://github.com/$TargetRepo/releases/download/v$Version/SonicRoute-v$Version-Lite-arm64.exe) | 需匹配的 ARM64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |")
    [void]$sb.AppendLine("| ☕ **Lite NET8 x64**（.NET 8 兼容版） | 兼容现有 .NET 8 运行库环境 | [单个 EXE](https://github.com/$TargetRepo/releases/download/v$Version/SonicRoute-v$Version-Lite-NET8-x64.exe) | 需匹配的 x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |")
    [void]$sb.AppendLine("| ☕ **Lite NET8 ARM64**（.NET 8 兼容版） | 兼容现有 .NET 8 运行库环境 | [单个 EXE](https://github.com/$TargetRepo/releases/download/v$Version/SonicRoute-v$Version-Lite-NET8-arm64.exe) | 需匹配的 ARM64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |")
    [void]$sb.AppendLine("| 🛡️ **Legacy x64**（免装运行库） | 公司办公电脑 / 受限机型 | [完整 ZIP](https://github.com/$TargetRepo/releases/download/v$Version/SonicRoute-v$Version-Legacy-x64.zip) | 系统自带 .NET Framework 4.8（解压即用） |")
    [void]$sb.AppendLine("| 🔄 **Microsoft Store** | 由商店提供更新，在架版本以商店页面为准 | [商店安装](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | x64 .NET 10 Desktop Runtime；MSIX 不内置运行时 |")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("> [!NOTE]")
    [void]$sb.AppendLine("> - 支持 Windows 10 2004+ / Windows 11 系统。")
    [void]$sb.AppendLine("> - 默认 Lite 需要对应架构的 .NET 10 Desktop Runtime；带 NET8 后缀的兼容版需要 .NET 8。2026 年 11 月之后停止提供和维护 NET8 版；x64 用户可手动选择 .NET 10 或 Legacy，ARM64 用户请选择 .NET 10，不跨运行时自动替换。")
    [void]$sb.AppendLine("> - Lite 与 Store 为框架依赖部署，不假定商店会自动安装 .NET 10 Desktop Runtime。")
    [void]$sb.AppendLine("> - 发布附件提供 ``SHA256SUMS.txt`` 和 ``SonicRoute-update.json``（Protocol 2）。SHA-256 用于完整性校验，不是独立发布者数字签名。")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("---")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## 🔄 升级与数据保留指引")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("- **v1.25 及后续便携版**：打开「设置 → 软件更新」，检查、下载并完成校验后，确认重启更新。")
    [void]$sb.AppendLine("- **v1.24 及更早版本或手动更新**：先在托盘菜单选择「退出」，再替换对应 EXE；Legacy 请完整解压 ZIP。")
    [void]$sb.AppendLine("- **用户数据**：设置、快捷键、自动化规则及自定义图标继续使用 ``%LocalAppData%\SonicRoute\``；更新器不替换这些数据，建议手动更新前备份。")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("---")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## 📚 相关链接")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("- [Wiki 使用指南](https://github.com/$TargetRepo/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [自动化规则](https://github.com/$TargetRepo/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [常见问题](https://github.com/$TargetRepo/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)")
    [void]$sb.AppendLine("- [完整版本历史](https://github.com/$TargetRepo/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) · [问题反馈](https://github.com/$TargetRepo/issues/new/choose)")

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

    [void]$sb.AppendLine("- $(&$mark ($Checklist.PreflightVerified)) Source/ref and authoritative release history checked under the selected publish/DryRun policy")
    if($Checklist.ReadOnlyRevalidation){[void]$sb.AppendLine('- [x] Read-only revalidation of an existing release; this candidate is not authorized to overwrite that release')}
    [void]$sb.AppendLine("- $(&$mark ($Checklist.SolutionBuildVerified)) Full solution Release build verified")
    [void]$sb.AppendLine("- $(&$mark ($Checklist.ArtifactsCompiled)) Five packages (Lite x64, Lite ARM64, Lite-NET8 x64, Lite-NET8 ARM64, Legacy x64) compiled")
    [void]$sb.AppendLine("- $(&$mark ($Checklist.LocalArtifactsVerified)) PE machine architecture (0x8664/0xaa64) & Legacy ZIP 13-file whitelist verified")
    [void]$sb.AppendLine("- $(&$mark ($Checklist.ManifestVerified)) SHA256SUMS.txt & SonicRoute-update.json 1-to-1 matrix bijection verified")
    [void]$sb.AppendLine("- $(&$mark ($Checklist.ChangelogGenerated)) Changelog curated/generated with conventional commit categorization")
    [void]$sb.AppendLine("- $(&$mark ($Checklist.RemoteAssetsVerified)) Remote asset size, count ($($LocalAssets.Count)/$($LocalAssets.Count)) & downloaded SHA-256 verified byte-for-byte")
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
    $expectedNotes=Assert-ReleaseNotes $ChangelogTarget $Version
    $Checklist.ChangelogGenerated=$true

    if ($IsDryRun) {
        $dryStatus=if(!$Checklist.PreflightVerified){'DRY_RUN_LOCAL_ONLY_REMOTE_NOT_VERIFIED'}elseif($Checklist.ReadOnlyRevalidation){'DRY_RUN_REVALIDATED_PUBLISHED_VERSION'}else{'DRY_RUN_PASSED'}
        Write-Host "    [DRY-RUN] Local candidate verified; remote preflight verified: $([bool]$Checklist.PreflightVerified)." -ForegroundColor Yellow
        Write-Host "    [DRY-RUN] Remote release creation and upload skipped due to DryRun flag." -ForegroundColor Yellow
        Write-StepSummary -Version $Version -Tag $Tag -CommitSha $CommitSha -LocalAssets $LocalAssets -Status $dryStatus -TargetRepo $TargetRepo -DurationStr $ElapsedStr -Checklist $Checklist
        return
    }

    foreach($asset in $LocalAssets.Values){
        if((Get-Item -LiteralPath $asset.Path).Length -ne $asset.Size -or (Get-FileHash -LiteralPath $asset.Path).Hash -ne $asset.Hash){throw 'Local artifact changed after validation; no remote mutation was attempted.'}
    }

    # Phase B: create once, retain the returned ID, and always preserve tag identity.
    $releaseId=0L
    if ($HasExistingDraft) {
        $beforeEdit = Read-RemoteRelease $TargetRepo $Tag
        Assert-RemoteDraft $beforeEdit $Tag $CommitSha $LocalAssets
        $releaseId=[long]$beforeEdit.id
        if($releaseId -le 0){throw 'Draft has no valid release ID.'}
        Write-Host "    Updating existing Draft Release for $Tag on GitHub..."
        Invoke-WithRetry -Description "Update existing draft release $Tag" -MaxAttempts 3 -InitialDelaySeconds 2 -Action {
            & gh api --method PATCH "/repos/$TargetRepo/releases/$releaseId" -f "tag_name=$Tag" -f "target_commitish=$CommitSha" -f "name=$Tag" -F "body=@$ChangelogTarget" -F draft=true -F prerelease=false | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to update existing draft release $Tag on GitHub!"
            }
        }
    } else {
        Write-Host "    Creating Draft Release for $Tag on GitHub..."
        try {
            # Do not retry POST blindly: a lost response can otherwise create
            # multiple drafts. Recover through the bounded lookup below.
            $json=Invoke-Utf8Native { & gh api --method POST "/repos/$TargetRepo/releases" -f "tag_name=$Tag" -f "target_commitish=$CommitSha" -f "name=$Tag" -F "body=@$ChangelogTarget" -F draft=true -F prerelease=false }
            if($LASTEXITCODE -ne 0 -or !$json){throw 'Draft create response was unavailable.'}
            $created=($json -join "`n")|ConvertFrom-Json
            Assert-RemoteDraft $created $Tag $CommitSha $LocalAssets
            $releaseId=[long]$created.id
            if($releaseId -le 0){throw 'Draft create response has no valid release ID.'}
        } catch {
            Write-Warning "Draft creation call reported error: $($_.Exception.Message)"
            $checkDraft = Read-RemoteRelease $TargetRepo $Tag
            Assert-RemoteDraft $checkDraft $Tag $CommitSha $LocalAssets
            $releaseId=[long]$checkDraft.id
            if($releaseId -le 0){throw 'Recovered draft has no valid release ID.'}
            Write-Host '    [Recovered] Matching draft exists despite the lost create response.' -ForegroundColor Yellow
        }
    }
    $Checklist.DraftCreated = $true

    # Query current draft state to check existing remote assets and verify target commit
    $draftStatus = Read-RemoteRelease $TargetRepo $Tag $releaseId
    Assert-RemoteDraft $draftStatus $Tag $CommitSha $LocalAssets
    $existingTagSha = Get-RemoteTagCommitSha -TargetRepo $TargetRepo -Tag $Tag -AllowQueryFailure:$false
    if ($existingTagSha) {
        if ($existingTagSha -cne $CommitSha) {
            throw "Fatal: Remote git tag '$Tag' on GitHub points to commit '$existingTagSha', which does not match target commit '$CommitSha'!"
        }
        Write-Host "    [OK] Remote git tag matches the full target commit $CommitSha."
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

    Write-Host "    Processing $($LocalAssets.Count) release assets for the draft..."
    $checkTempDir = Join-Path ([IO.Path]::GetTempPath()) ("gh-chk-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $checkTempDir -Force | Out-Null
    try {
        foreach ($assetItem in $LocalAssets.Values) {
            Assert-RemoteDraft (Read-RemoteRelease $TargetRepo $Tag $releaseId) $Tag $CommitSha $LocalAssets
            $filePath = $assetItem.Path
            $fileName = $assetItem.Name
            $localSize = $assetItem.Size
            $localHash = $assetItem.Hash
            if((Get-Item -LiteralPath $filePath).Length -ne $localSize -or (Get-FileHash -LiteralPath $filePath).Hash -ne $localHash){throw 'Local artifact changed after validation; upload stopped.'}

            $needsUpload = $true
            if ($existingRemoteAssets.ContainsKey($fileName)) {
                $rem = $existingRemoteAssets[$fileName]
                if ($rem.size -eq $localSize -and $rem.state -eq 'uploaded') {
                    # Verify remote hash by downloading to test if identical
                    Write-Host "    Checking existing remote asset $fileName on draft..."
                    $downFile = Join-Path $checkTempDir $fileName
                    Save-RemoteReleaseAsset $TargetRepo ([long]$rem.id) $downFile
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
                    $current=Read-RemoteRelease $TargetRepo $Tag $releaseId
                    Assert-RemoteDraft $current $Tag $CommitSha $LocalAssets
                    $sameName=@($current.assets|Where-Object{$_.name -ceq $fileName})
                    if($sameName.Count -eq 1){
                        $present=$sameName[0]
                        # A lost upload response is safe to reuse only after the
                        # final independent download; never blindly POST twice.
                        if($present.size -eq $localSize -and $present.state -ceq 'uploaded' -and $present.digest -ceq ('sha256:'+$localHash)){return}
                        if([long]$present.id -le 0){throw 'Invalid existing asset ID.'}
                        & gh api --method DELETE "/repos/$TargetRepo/releases/assets/$($present.id)" | Out-Null
                        if($LASTEXITCODE -ne 0){throw 'Failed to replace a known draft asset.'}
                    }
                    $encoded=[Uri]::EscapeDataString($fileName)
                    & gh api --method POST "https://uploads.github.com/repos/$TargetRepo/releases/$releaseId/assets?name=$encoded" --input $filePath -H 'Content-Type: application/octet-stream' | Out-Null
                    if ($LASTEXITCODE -ne 0) {
                        throw "Failed to upload asset $fileName to draft release $Tag!"
                    }
                }
            }
        }
    } finally {
        if (Test-Path -LiteralPath $checkTempDir) {
            if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($checkTempDir)) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -or [IO.Path]::GetFileName($checkTempDir) -notmatch '^gh-chk-[a-f0-9]{32}$'){throw 'Invalid owned scratch path.'}
            Remove-Item -LiteralPath $checkTempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # Phase C: Remote Verification
    Write-Host "    Verifying remote release and assets on GitHub..."
    $remoteRelease = Read-RemoteRelease $TargetRepo $Tag $releaseId

    if (!$remoteRelease) {
        throw "Remote release object is null after query."
    }
    if (!$remoteRelease.draft) {
        throw "Security violation: Release $Tag is NOT in draft state during verification!"
    }
    Assert-RemoteDraft $remoteRelease $Tag $CommitSha $LocalAssets
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
            if($rAsset.digest -cne ('sha256:'+$local.Hash)){throw "GitHub asset digest is missing or differs from the local hash: $name"}

            # Independent download & cryptographic verification with retry
            Write-Host "    Downloading and verifying SHA-256 for $($name) from GitHub..."
            Invoke-WithRetry -Description "Download asset $name for verification" -MaxAttempts 3 -InitialDelaySeconds 2 -Action {
                Save-RemoteReleaseAsset $TargetRepo ([long]$rAsset.id) (Join-Path $verifyTempDir $name)
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
            if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($verifyTempDir)) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -or [IO.Path]::GetFileName($verifyTempDir) -notmatch '^gh-verify-[a-f0-9]{32}$'){throw 'Invalid owned scratch path.'}
            Remove-Item -LiteralPath $verifyTempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host "    [OK] All $($LocalAssets.Count) remote assets downloaded and hash-verified." -ForegroundColor Green
    $Checklist.RemoteAssetsVerified = $true

    # Phase D: Auto-Publish with network disconnect / response loss recovery
    $beforePromotion=Read-RemoteRelease $TargetRepo $Tag $releaseId
    Assert-RemoteDraft $beforePromotion $Tag $CommitSha $LocalAssets
    Assert-RemoteAssetMetadata $beforePromotion $LocalAssets
    if($beforePromotion.body -cne $expectedNotes){throw 'Remote draft release notes differ from the reviewed UTF-8 source; publication stopped.'}
    Write-Host "    Promoting Draft Release to Official Public Latest Release..."
    $publishSuccess = $false
    try {
        & gh api --method PATCH "/repos/$TargetRepo/releases/$releaseId" -f "tag_name=$Tag" -f "target_commitish=$CommitSha" -F draft=false -F prerelease=false -f make_latest=true | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $publishSuccess = $true
        }
    } catch { }

    if (!$publishSuccess) {
        Write-Warning "Publish command encountered a warning or non-zero exit; querying remote release state to verify if update took effect..."
        $queryAfter = $null
        try {
            $queryAfter = Read-RemoteRelease $TargetRepo $Tag $releaseId
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
    $published = Read-RemoteRelease $TargetRepo $Tag $releaseId
    if ($published.draft) {
        throw "Post-publish check failed: Release $Tag is still marked as draft on GitHub!"
    }
    if ($published.prerelease) {
        throw "Post-publish check failed: Release $Tag is marked as prerelease on GitHub!"
    }
    if($published.target_commitish -cne $CommitSha){throw 'Published release target differs from the validated full commit SHA.'}
    if($published.body -cne $expectedNotes){throw 'Published release notes differ from the reviewed UTF-8 source.'}
    Assert-RemoteAssetMetadata $published $LocalAssets

    # Post-publish verification: 2. Confirm Latest pointer strictly (Issue 2)
    Write-Host "    Verifying /releases/latest endpoint points to $Tag..."
    $latest = $null
    try {
        $latest = Invoke-WithRetry -Description "Query /releases/latest endpoint" -MaxAttempts 3 -InitialDelaySeconds 2 -Action {
            $json = Invoke-Utf8Native { & gh api "/repos/$TargetRepo/releases/latest" }
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

    if ($latest.tag_name -cne $Tag -or $latest.id -ne $releaseId) {
        Write-StepSummary -Version $Version -Tag $Tag -CommitSha $CommitSha -LocalAssets $LocalAssets -Status "PUBLISHED_LATEST_MISMATCH" -TargetRepo $TargetRepo -ReleaseUrl $published.html_url -DurationStr $ElapsedStr -Checklist $Checklist
        Write-Host "    [Error] Latest release mismatch: Expected $Tag, but GitHub /releases/latest points to '$($latest.tag_name)'!" -ForegroundColor Red
        throw "Latest release verification failed: expected $Tag (ID $releaseId), received $($latest.tag_name) (ID $($latest.id))."
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
    ReadOnlyRevalidation = $false
}

if ($SolutionBuildVerified -or $env:SOLUTION_BUILD_VERIFIED -eq 'true') { Write-Warning 'SolutionBuildVerified input is advisory only; the build receipt supplies the verified result.' }

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
if ($Stage -in @('Preflight', 'Publish', 'FullPipeline')) {
    $preflight = Assert-Preflight -TargetRepo $Repo -Version $version -Tag $tag -IsDryRun:$DryRun
    $commitSha = $preflight.CommitSha
    $hasExistingDraft = $preflight.HasExistingDraft
    $checklist.PreflightVerified = $preflight.RemoteVerified
    $checklist.ReadOnlyRevalidation = $preflight.ReadOnlyRevalidation
} else {
    $commitSha = (& git -C $root rev-parse HEAD 2>$null)
}

# Stage: Build
if ($Stage -in @('Build', 'FullPipeline')) {
    if (!$SkipBuild) {
        Write-Host "==> [Build] Invoking Build-PortableRelease.ps1 for all editions..." -ForegroundColor Cyan
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
    $receipt=Assert-BuildReceipt $OutputDirectory $version $commitSha $localAssets
    $checklist.SolutionBuildVerified=$receipt.solutionBuildVerified
    $checklist.ArtifactsCompiled=$true
    $startTime.Stop()
    $elapsedStr = "$([Math]::Round($startTime.Elapsed.TotalSeconds, 1))s"
    Publish-ReleaseToGitHub -AssetsDir $assetsDir -ChangelogTarget $ChangelogPath -Version $version -Tag $tag -CommitSha $commitSha -LocalAssets $localAssets -TargetRepo $Repo -HasExistingDraft:$hasExistingDraft -IsDryRun:$DryRun -ElapsedStr $elapsedStr -Checklist $checklist
}

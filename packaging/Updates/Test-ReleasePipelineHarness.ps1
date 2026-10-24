<# Exercises real release functions and real packages. Only git/gh, binary download and retry delays are substituted.
   Never writes to the network. Retains fixtures and a machine-readable report. #>
[CmdletBinding()]
param([string]$AssetsDirectory,[string]$ReportPath)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/Invoke-ReleasePipeline.ps1"
$harnessRoot=Join-Path $root ('build-artifacts/release-harness-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $harnessRoot -Force|Out-Null
if(!$AssetsDirectory){& "$PSScriptRoot/Build-PortableRelease.ps1" -OutputDirectory "$harnessRoot/candidate";$AssetsDirectory="$harnessRoot/candidate/assets"}
$AssetsDirectory=[IO.Path]::GetFullPath($AssetsDirectory)
$version=(Get-SonicRouteVersion).Version
$golden=Assert-LocalArtifacts $AssetsDirectory $version
$results=[Collections.Generic.List[object]]::new()
$savedEnv=@{};foreach($name in @('GITHUB_REF','GITHUB_SHA','GITHUB_REPOSITORY','GITHUB_ACTIONS','GITHUB_STEP_SUMMARY')){
    $savedEnv[$name]=[Environment]::GetEnvironmentVariable($name);[Environment]::SetEnvironmentVariable($name,$null)
}
$env:GITHUB_STEP_SUMMARY=Join-Path $harnessRoot 'summaries.md'
function Reset-Mock {
    $script:mock=@{Head=('a'*40);Tree=('c'*40);Branch='master';Dirty=$false;RemoteTag=$null;History=@(@{tag_name='v1.24';draft=$false;prerelease=$false});
        Draft=$true;Exists=$false;Target=('a'*40);Assets=@{};Calls=[Collections.Generic.List[string]]::new();Mutations=0;Uploads=0;
        ApiFailure=$false;CreateLost=$false;UploadFailures=0;DownloadCorrupt=$false;NoDigest=$false;PublishLost=$false;LatestWrong=$false;LatestFailure=$false;PromoteEarly=$false;TagApiUnavailable=$false;
        HiddenDraftHistory=$false;HistoryLag=0;ReadIdFailures=0;WrongId=$false;UploadLost=$false;NextAssetId=100;Body='';CorruptNotes=$false;LatestWrongId=$false;Commits=@()}
    $global:LASTEXITCODE=0
}
function git {
    $line=$args -join ' ';$global:LASTEXITCODE=0
    if($line -match 'rev-parse --abbrev-ref HEAD$'){return $mock.Branch}
    if($line -match 'rev-parse HEAD\^\{tree\}$'){return $mock.Tree}
    if($line -match 'rev-parse HEAD$'){return $mock.Head}
    if($line -match 'status --porcelain --untracked-files=all$'){if($mock.Dirty){return '?? untracked.cs'};return}
    if($line -match 'ls-remote --tags '){if($mock.RemoteTag){return "$($mock.RemoteTag)`trefs/tags/v$version"};return}
    if($line -match 'tag -l '){return 'v1.24'}
    if($line -match ' log '){return $mock.Commits}
    throw "Unimplemented git boundary: $line"
}
function Remote-Object {
    $assets=@($mock.Assets.Values|ForEach-Object{@{id=$_.Id;name=$_.Name;size=$_.Size;state='uploaded';digest=$(if($mock.NoDigest){$null}else{'sha256:'+$_.Hash})}})
    return @{id=12345;tag_name="v$version";draft=$mock.Draft;prerelease=$false;target_commitish=$mock.Target;assets=$assets;body=$(if($mock.CorruptNotes){'corrupt notes'}else{$mock.Body});html_url='https://example.invalid/release'}
}
function gh {
    $a=@($args);$line=$a -join ' ';$mock.Calls.Add($line);$global:LASTEXITCODE=0
    if($line -match '^release list '){return '[{"tagName":"v1.24","isDraft":false}]'}
    if($a[0] -eq 'api'){
        if($mock.ApiFailure){$global:LASTEXITCODE=1;return}
        if($a -contains '--method'){
            $method=$a[([array]::IndexOf($a,'--method')+1)]
            $mock.Mutations++
            if($method -eq 'POST' -and $line -match '/releases -f '){
                Require ($a -contains "tag_name=v$version" -and $a -contains 'draft=true' -and $a -contains 'prerelease=false') 'Create lost explicit draft identity'
                $mock.Exists=$true
                $bodyArg=@($a|Where-Object{$_ -like 'body=@*'})[0]
                $mock.Body=[IO.File]::ReadAllText($bodyArg.Substring(6))
                if($mock.CreateLost){$global:LASTEXITCODE=1;return}
                return (Remote-Object|ConvertTo-Json -Depth 8 -Compress)
            }
            if($method -eq 'PATCH' -and $line -match '/releases/12345 '){
                Require ($a -contains "tag_name=v$version" -and $a -contains "target_commitish=$($mock.Target)" -and $a -contains 'prerelease=false') 'Edit lost tag/target identity'
                $bodyArg=@($a|Where-Object{$_ -like 'body=@*'})
                if($bodyArg.Count){$mock.Body=[IO.File]::ReadAllText($bodyArg[0].Substring(6))}
                if($a -contains 'draft=false'){$mock.Draft=$false;if($mock.PublishLost){$global:LASTEXITCODE=1}}
                return (Remote-Object|ConvertTo-Json -Depth 8 -Compress)
            }
            if($method -eq 'POST' -and $line -match 'https://uploads.github.com/repos/kunkunkunQoQ/SonicRoute/releases/12345/assets\?name='){
                $mock.Uploads++;if($mock.UploadFailures -gt 0){$mock.UploadFailures--;$global:LASTEXITCODE=1;return}
                $path=$a[([array]::IndexOf($a,'--input')+1)];$name=[IO.Path]::GetFileName($path)
                Require (!$mock.Assets.ContainsKey($name)) 'Repeated POST would create duplicate assets'
                $mock.NextAssetId++
                $mock.Assets[$name]=@{Id=$mock.NextAssetId;Name=$name;Path=$path;Size=(Get-Item -LiteralPath $path).Length;Hash=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()}
                if($mock.PromoteEarly){$mock.Draft=$false}
                if($mock.UploadLost){$mock.UploadLost=$false;$global:LASTEXITCODE=1}
                return '{}'
            }
            if($method -eq 'DELETE' -and $line -match '/releases/assets/(\d+)$'){
                $id=[long]$Matches[1];$asset=@($mock.Assets.Values|Where-Object{$_.Id -eq $id})
                Require ($asset.Count -eq 1) 'Unknown asset ID deleted';$mock.Assets.Remove($asset[0].Name);return
            }
            throw "Unimplemented mutation: $line"
        }
        if($a[1] -match '/releases\?per_page=100&page=(\d+)$'){
            $pageNumber=[int]$Matches[1]
            $history=@($mock.History)
            if($mock.Exists -and !$mock.HiddenDraftHistory -and $mock.HistoryLag -le 0){$history=@(Remote-Object)+$history}
            if($mock.HistoryLag -gt 0){$mock.HistoryLag--}
            return (ConvertTo-Json -InputObject @($history|Select-Object -Skip (($pageNumber-1)*100) -First 100) -Depth 8 -Compress)
        }
        if($a[1] -match '/releases/latest$'){
            if($mock.LatestFailure){$global:LASTEXITCODE=1;return}
            $r=Remote-Object;if($mock.LatestWrong){$r.tag_name='v1.24'};if($mock.LatestWrongId){$r.id=99999};return ($r|ConvertTo-Json -Depth 8 -Compress)
        }
        if($a[1] -match '/releases/tags/'){
            if(!$mock.Exists -or $mock.TagApiUnavailable){$global:LASTEXITCODE=1;return};return (Remote-Object|ConvertTo-Json -Depth 8 -Compress)
        }
        if($a[1] -match '/releases/12345$'){
            if(!$mock.Exists -or $mock.ReadIdFailures -gt 0){$mock.ReadIdFailures--;$global:LASTEXITCODE=1;return}
            $object=Remote-Object;if($mock.WrongId){$object.id=99999}
            return ($object|ConvertTo-Json -Depth 8 -Compress)
        }
    }
    throw "Unimplemented gh boundary: $line"
}
function Save-RemoteReleaseAsset([string]$TargetRepo,[long]$AssetId,[string]$Destination){
    $found=@($mock.Assets.Values|Where-Object{$_.Id -eq $AssetId -and $AssetId -gt 0})
    Require ($found.Count -eq 1) 'Unknown asset download ID'
    Copy-Item -LiteralPath $found[0].Path -Destination $Destination -Force
    if($mock.DownloadCorrupt){[IO.File]::AppendAllText($Destination,'corrupt')}
}
function Start-Sleep {param([int]$Seconds)}
function Require([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function Reject([scriptblock]$Action,[string]$Pattern='.'){
    $caught=$null;try{& $Action|Out-Null}catch{$caught=$_.Exception.Message}
    Require ($null -ne $caught) 'Expected production function to reject the input.'
    Require ($caught -match $Pattern) "Wrong rejection reason: $caught (expected $Pattern)"
}
function Case([string]$CaseTitle,[scriptblock]$Action){
    Reset-Mock;$watch=[Diagnostics.Stopwatch]::StartNew()
    try{& $Action|Out-Null;$results.Add(@{name=$CaseTitle;status='PASS';milliseconds=$watch.ElapsedMilliseconds});Write-Host "PASS $CaseTitle" -ForegroundColor Green}
    catch{$results.Add(@{name=$CaseTitle;status='FAIL';error=$_.Exception.Message;milliseconds=$watch.ElapsedMilliseconds});Write-Host "FAIL $CaseTitle : $($_.Exception.Message)" -ForegroundColor Red}
}
function Fixture {
    $dir=Join-Path $harnessRoot ('case-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $dir|Out-Null
    Copy-Item -LiteralPath @(Get-ChildItem -LiteralPath $AssetsDirectory -File|ForEach-Object{$_.FullName}) -Destination $dir;return $dir
}
function Save-Manifest([string]$Dir,$Manifest){
    [IO.File]::WriteAllText((Join-Path $Dir 'SonicRoute-update.json'),($Manifest|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    $lines=@(Get-ReleaseMatrix $version|ForEach-Object{$_.Asset})+@('SonicRoute-update.json')|ForEach-Object{((Get-FileHash -LiteralPath (Join-Path $Dir $_)).Hash.ToLowerInvariant())+'  '+$_}
    [IO.File]::WriteAllLines((Join-Path $Dir 'SHA256SUMS.txt'),[string[]]$lines,[Text.UTF8Encoding]::new($false))
}
function Refresh-Package([string]$Dir,$Manifest,[string]$Asset){
    $p=@($Manifest.packages|Where-Object{$_.asset -ceq $Asset})[0];$path=Join-Path $Dir $Asset
    $p.size=(Get-Item -LiteralPath $path).Length;$p.sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
    if($p.edition -eq 'Lite'){$p.files[0].size=$p.size;$p.files[0].sha256=$p.sha256};Save-Manifest $Dir $Manifest
}
function Preflight {Assert-Preflight 'kunkunkunQoQ/SonicRoute' $version "v$version"}
function Publish([switch]$DryRun,[bool]$Existing=$false,$Assets=$golden,[string]$NotesPath){
    $script:checks=@{DraftCreated=$false;RemoteAssetsVerified=$false;DraftPromoted=$false;LatestPointerVerified=$false}
    if(!$NotesPath){
        $NotesPath=Join-Path $harnessRoot 'notes.md'
        $curated=Join-Path $root "docs/release-v$version.md"
        $text=if(Test-Path -LiteralPath $curated){[IO.File]::ReadAllText($curated)}else{'Fixture release notes'}
        [IO.File]::WriteAllText($NotesPath,$text,[Text.UTF8Encoding]::new($false))
    }
    Publish-ReleaseToGitHub -AssetsDir $AssetsDirectory -ChangelogTarget $NotesPath -Version $version -Tag "v$version" -CommitSha $mock.Head -LocalAssets $Assets -TargetRepo 'kunkunkunQoQ/SonicRoute' -HasExistingDraft:$Existing -IsDryRun:$DryRun -ElapsedStr 'test' -Checklist $checks
}
try {
    Case 'curated UTF8 release notes preserve original Unicode' {
        $expected='SonicRoute '+[char]0x6b63+[char]0x5f0f+[char]0x7248+' '+[char]::ConvertFromUtf32(0x1f3a7)+"`r`nhttps://github.com/kunkunkunQoQ/SonicRoute"
        $root=Join-Path $harnessRoot 'curated-notes'
        New-Item -ItemType Directory -Path (Join-Path $root 'docs')|Out-Null
        [IO.File]::WriteAllText((Join-Path $root "docs/release-v$version.md"),$expected,[Text.UTF8Encoding]::new($false))
        $target=Join-Path $harnessRoot 'unicode-release-notes.md'
        $actual=Get-OrGenerateChangelog $version $target 'kunkunkunQoQ/SonicRoute'
        Require ($actual -ceq $expected) 'Curated notes were decoded with the wrong encoding'
        $saved=[Text.UTF8Encoding]::new($false,$true).GetString([IO.File]::ReadAllBytes($target))
        Require ($saved -ceq $expected) 'Generated file differs from original UTF8 notes'
        Require ($mock.Mutations -eq 0) 'Generating notes mutated GitHub'
    }
    Case 'retry executes real action until success' {$script:n=0;$r=Invoke-WithRetry -Action {$script:n++;if($n -lt 3){throw 'transient'};'ok'};Require ($n -eq 3 -and $r -eq 'ok') 'Retry count/result incorrect'}
    Case 'permanent failure stops after bound' {$script:n=0;Reject {Invoke-WithRetry -Action {$script:n++;throw 'permanent'}} 'permanent';Require ($n -eq 3) 'Retry bound incorrect'}
    Case 'generated notes follow v1.27 layout with current links and complete details' {
        $root=Join-Path $harnessRoot 'generated-notes'
        $mock.Commits=@(
            (('a'*40)+"`tfeat: new feature`ttester`taaaaaaa"),
            (('b'*40)+"`tfix: corrected target`ttester`tbbbbbbb"),
            (('c'*40)+"`tperf: reuse objects`ttester`tccccccc"),
            (('d'*40)+"`tdocs: usage clarification`ttester`tddddddd")
        )
        $target=Join-Path $harnessRoot 'generated-notes.md'
        $notes=Get-OrGenerateChangelog '2.34' $target 'example/SonicRoute'
        $last=-1
        foreach($heading in @('## ✨ 核心更新亮点','## 📋 详细演进说明','## 📋 升级与验证说明','## 🚀 资产下载矩阵','## 🔄 升级与数据保留指引','## 📚 相关链接')){
            $index=$notes.IndexOf($heading,[StringComparison]::Ordinal)
            Require ($index -gt $last) "Missing or misordered section: $heading";$last=$index
        }
        Require ($notes.Contains('### 1. 新增功能与特性') -and $notes.Contains('### 4. 其他变更') -and $notes.Contains('docs: usage clarification')) 'Detailed groups dropped non-feature commits'
        foreach($asset in @('Lite-x64.exe','Lite-arm64.exe','Lite-NET8-x64.exe','Lite-NET8-arm64.exe','Legacy-x64.zip')){
            Require ($notes.Contains("https://github.com/example/SonicRoute/releases/download/v2.34/SonicRoute-v2.34-$asset")) 'Download link has a stale version/repository'
        }
        Require (!$notes.Contains('v1.27') -and $notes.Contains('MSIX 不内置运行时') -and $notes.Contains('不是独立发布者数字签名') -and !$notes.Contains('无损继承')) 'Copied old version facts or overstated upgrade guarantees'
        Require ([IO.File]::ReadAllText($target,[Text.UTF8Encoding]::new($false,$true)) -ceq $notes) 'Saved generated UTF8 differs'
        Require ($mock.Mutations -eq 0) 'Generating notes mutated GitHub'
    }
    Case 'highlight limit retains all commits in detailed notes' {
        $root=Join-Path $harnessRoot 'limited-highlights'
        $mock.Commits=@(1..8|ForEach-Object{('a'*40)+"`tfix: distinct item $_`ttester`taaaaaaa"})
        $notes=Get-OrGenerateChangelog '2.34' $null 'example/SonicRoute'
        $overview=($notes -split '## 📋 详细演进说明',2)[0]
        Require ([regex]::Matches($overview,'(?m)^- fix: distinct item ').Count -eq 6) 'Highlight limit was not six'
        Require ([regex]::Matches($notes,'(?m)^- fix: distinct item 8 ').Count -eq 1) 'Omitted highlight was lost from details'
    }
    Case 'documentation-only and empty history do not invent feature or test claims' {
        $root=Join-Path $harnessRoot 'documentation-notes'
        $mock.Commits=@(('a'*40)+"`tdocs: improve guide`ttester`taaaaaaa")
        $notes=Get-OrGenerateChangelog '2.34' $null 'example/SonicRoute'
        Require ([regex]::Matches($notes,'(?m)^- docs: improve guide ').Count -eq 2 -and $notes.Contains('### 1. 其他变更')) 'Documentation-only notes lost overview or detail'
        Require (!$notes.Contains('### 1. 新增功能') -and !$notes.Contains('PASS')) 'Generated unsupported feature or validation claim'
        $mock.Commits=@()
        $empty=Get-OrGenerateChangelog '2.34' $null 'example/SonicRoute'
        Require ($empty.Contains('未提取到可展示的提交记录') -and !$empty.Contains('PASS')) 'Empty history invented changes or validation'
    }
    Case 'real preflight accepts exact clean source' {$r=Preflight;Require ($r.RemoteVerified -and $r.CommitSha -ceq $mock.Head) 'Unverified result'}
    Case 'preflight rejects unrelated source branch' {$mock.Branch='feature/test';Reject {Preflight} 'non-master'}
    Case 'preflight includes untracked files' {$mock.Dirty=$true;Reject {Preflight} 'clean source'}
    Case 'CI commit mismatch' {$env:GITHUB_REF='refs/heads/master';$env:GITHUB_REPOSITORY='kunkunkunQoQ/SonicRoute';$env:GITHUB_SHA='b'*40;try{Reject {Preflight} 'CI ref'}finally{$env:GITHUB_REF=$null;$env:GITHUB_SHA=$null;$env:GITHUB_REPOSITORY=$null}}
    Case 'published current version immutable' {$mock.History+=@{tag_name="v$version";draft=$false;prerelease=$false};Reject {Preflight} 'immutable'}
    Case 'DryRun revalidates published version from later clean commit without mutation' {$mock.History+=@{tag_name="v$version";draft=$false;prerelease=$false};$mock.RemoteTag='b'*40;$r=Assert-Preflight 'kunkunkunQoQ/SonicRoute' $version "v$version" -IsDryRun;Require ($r.ReadOnlyRevalidation -and !$r.HasExistingDraft -and $r.RemoteVerified -and $mock.Mutations -eq 0) 'Published DryRun was rejected or changed publication eligibility'}
    Case 'published DryRun still refuses a downgrade' {$mock.History+=@{tag_name="v$version";draft=$false;prerelease=$false};$mock.History+=@{tag_name='v9.0';draft=$false;prerelease=$false};Reject {Assert-Preflight 'kunkunkunQoQ/SonicRoute' $version "v$version" -IsDryRun} 'not newer'}
    Case 'numeric version maximum beyond first page' {$mock.History=@(1..100|ForEach-Object{@{tag_name="test$_";draft=$true;prerelease=$false}})+@(@{tag_name='v9.0';draft=$false;prerelease=$false});Reject {Preflight} 'not newer';Require (@($mock.Calls|Where-Object{$_ -match 'page=2$'}).Count -eq 1) 'Second page not queried'}
    Case 'untrusted release history fails closed' {$mock.ApiFailure=$true;Reject {Preflight} 'authoritative release history'}
    Case 'DryRun offline explicitly unverified' {$mock.ApiFailure=$true;$r=Assert-Preflight 'kunkunkunQoQ/SonicRoute' $version "v$version" -IsDryRun;Require (!$r.RemoteVerified -and $mock.Mutations -eq 0) 'Offline DryRun incorrectly verified'}
    Case 'CI DryRun cannot hide remote preflight failure' {$mock.ApiFailure=$true;$env:GITHUB_ACTIONS='true';try{Reject {Assert-Preflight 'kunkunkunQoQ/SonicRoute' $version "v$version" -IsDryRun} 'authoritative release history'}finally{$env:GITHUB_ACTIONS=$null}}
    Case 'remote tag must match complete SHA' {$mock.RemoteTag='b'*40;Reject {Preflight} 'full commit SHA'}
    Case 'draft abbreviated target refused' {$mock.History+=@{tag_name="v$version";draft=$true;prerelease=$false;target_commitish=$mock.Head.Substring(0,7);assets=@()};Reject {Preflight} 'full commit SHA'}
    Case 'all actual golden packages accepted' {Require ($golden.Count -eq 7) 'Wrong count'}
    foreach($name in @($golden.Keys)){Case "missing asset $name" {$dir=Fixture;Remove-Item -LiteralPath (Join-Path $dir $name);Reject {Assert-LocalArtifacts $dir $version} 'exactly the seven'}}
    Case 'unexpected asset rejected' {$dir=Fixture;Set-Content "$dir/extra.ps1" 'untrusted';Reject {Assert-LocalArtifacts $dir $version} 'exactly the seven'}
    Case 'extra directory rejected' {$dir=Fixture;New-Item -ItemType Directory "$dir/nested"|Out-Null;Reject {Assert-LocalArtifacts $dir $version} 'exactly the seven'}
    Case 'changed bytes without updated checksums' {$dir=Fixture;[IO.File]::AppendAllText("$dir/SonicRoute-v$version-Lite-NET8-x64.exe",'tampered');Reject {Assert-LocalArtifacts $dir $version} 'Hash mismatch'}
    Case 'hash-consistent non-PE rejected' {$dir=Fixture;$m=Get-Content "$dir/SonicRoute-update.json" -Raw|ConvertFrom-Json;$asset="SonicRoute-v$version-Lite-NET8-x64.exe";[IO.File]::WriteAllText((Join-Path $dir $asset),'not an executable');Refresh-Package $dir $m $asset;Reject {Assert-LocalArtifacts $dir $version} 'version/edition|PE'}
    Case 'real ARM64 cannot masquerade as x64' {$dir=Fixture;$m=Get-Content "$dir/SonicRoute-update.json" -Raw|ConvertFrom-Json;$asset="SonicRoute-v$version-Lite-NET8-x64.exe";Copy-Item "$dir/SonicRoute-v$version-Lite-NET8-arm64.exe" (Join-Path $dir $asset) -Force;Refresh-Package $dir $m $asset;Reject {Assert-LocalArtifacts $dir $version} 'PE architecture'}
    Case 'NET10 cannot masquerade as NET8' {$dir=Fixture;$m=Get-Content "$dir/SonicRoute-update.json" -Raw|ConvertFrom-Json;$asset="SonicRoute-v$version-Lite-NET8-x64.exe";Copy-Item "$dir/SonicRoute-v$version-Lite-x64.exe" (Join-Path $dir $asset) -Force;Refresh-Package $dir $m $asset;Reject {Assert-LocalArtifacts $dir $version} 'version/edition/runtime'}
    Case 'Protocol 1 refused for release matrix' {$dir=Fixture;$m=Get-Content "$dir/SonicRoute-update.json" -Raw|ConvertFrom-Json;$m.protocolVersion=1;Save-Manifest $dir $m;Reject {Assert-LocalArtifacts $dir $version} 'schema/protocol'}
    Case 'string Protocol refused like client' {$dir=Fixture;$m=Get-Content "$dir/SonicRoute-update.json" -Raw|ConvertFrom-Json;$m.protocolVersion='2';Save-Manifest $dir $m;Reject {Assert-LocalArtifacts $dir $version} 'schema/protocol'}
    Case 'duplicate package refused' {$dir=Fixture;$m=Get-Content "$dir/SonicRoute-update.json" -Raw|ConvertFrom-Json;$m.packages[1]=$m.packages[0];Save-Manifest $dir $m;Reject {Assert-LocalArtifacts $dir $version} 'Duplicate package'}
    Case 'missing runtime refused' {$dir=Fixture;$m=Get-Content "$dir/SonicRoute-update.json" -Raw|ConvertFrom-Json;$m.packages[0].runtime='';Save-Manifest $dir $m;Reject {Assert-LocalArtifacts $dir $version} 'unexpected edition'}
    Case 'unsafe Legacy per-file path refused' {$dir=Fixture;$m=Get-Content "$dir/SonicRoute-update.json" -Raw|ConvertFrom-Json;$m.packages[4].files[0].path='../config.json';Save-Manifest $dir $m;Reject {Assert-LocalArtifacts $dir $version} 'per-file path'}
    Case 'incorrect individual file hash refused' {$dir=Fixture;$m=Get-Content "$dir/SonicRoute-update.json" -Raw|ConvertFrom-Json;$m.packages[4].files[0].sha256='0'*64;Save-Manifest $dir $m;Reject {Assert-LocalArtifacts $dir $version} 'per-file path'}
    Case 'duplicate checksum line refused' {$dir=Fixture;Add-Content "$dir/SHA256SUMS.txt" (Get-Content "$dir/SHA256SUMS.txt"|Select-Object -First 1);Reject {Assert-LocalArtifacts $dir $version} 'duplicate SHA256SUMS'}
    Case 'embedded runtime dependency actually verified' {
        $dir=Fixture;$asset="SonicRoute-v$version-Lite-NET8-x64.exe";$path=Join-Path $dir $asset
        $config=[SonicRouteReleaseBundle]::Read($path);$replacement=$config.Replace('net8.0','net9.0').Replace('8.0.0','9.0.0')
        $bytes=[IO.File]::ReadAllBytes($path);$offset=[Text.Encoding]::GetEncoding(28591).GetString($bytes).IndexOf($config,[StringComparison]::Ordinal)
        Require ($offset -ge 0 -and $replacement.Length -eq $config.Length -and $replacement -ne $config) 'Invalid runtime fixture'
        $changed=[Text.Encoding]::UTF8.GetBytes($replacement);[array]::Copy($changed,0,$bytes,$offset,$changed.Length);[IO.File]::WriteAllBytes($path,$bytes)
        $m=Get-Content "$dir/SonicRoute-update.json" -Raw|ConvertFrom-Json;Refresh-Package $dir $m $asset;Reject {Assert-LocalArtifacts $dir $version} 'embedded target framework'
    }
    Case 'unsafe ZIP rejected even with consistent outer hash' {
        $dir=Fixture;$asset="SonicRoute-v$version-Legacy-x64.zip";$path=Join-Path $dir $asset
        $zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Update)
        try{$e=$zip.CreateEntry('../config.json');$writer=[IO.StreamWriter]::new($e.Open());try{$writer.Write('unsafe')}finally{$writer.Dispose()}}finally{$zip.Dispose()}
        $m=Get-Content "$dir/SonicRoute-update.json" -Raw|ConvertFrom-Json;Refresh-Package $dir $m $asset;Reject {Assert-LocalArtifacts $dir $version} 'Unsafe or unknown Legacy ZIP entry'
    }
    Case 'receipt binds actual assets and source tree' {
        $receiptDir=Join-Path $harnessRoot 'receipt';New-Item -ItemType Directory $receiptDir|Out-Null
        $r=@{schemaVersion=1;version=$version;commit=$mock.Head;tree=$mock.Tree;clean=$true;solutionBuildVerified=$true;assets=@($golden.Values|ForEach-Object{@{name=$_.Name;size=$_.Size;sha256=$_.Hash}})}
        $r|ConvertTo-Json -Depth 8|Set-Content "$receiptDir/build-receipt.json"
        Assert-BuildReceipt $receiptDir $version $mock.Head $golden|Out-Null
        $mock.Tree='d'*40;Reject {Assert-BuildReceipt $receiptDir $version $mock.Head $golden} 'clean, solution-verified'
        $mock.Tree=$r.tree;$mock.Dirty=$true;Reject {Assert-BuildReceipt $receiptDir $version $mock.Head $golden} 'clean, solution-verified'
    }
    Case 'DryRun causes no remote mutations' {Publish -DryRun;Require ($mock.Mutations -eq 0) 'DryRun mutated remote'}
    Case 'missing notes stop before any remote mutation' {Reject {Publish -NotesPath "$harnessRoot/missing-notes.md"} 'Missing';Require ($mock.Mutations -eq 0) 'Remote mutation preceded notes validation'}
    Case 'mismatched UTF8 notes stop before any remote mutation' {
        $root=Join-Path $harnessRoot 'mismatch-notes';New-Item -ItemType Directory -Path (Join-Path $root 'docs')|Out-Null
        [IO.File]::WriteAllText((Join-Path $root "docs/release-v$version.md"),'Reviewed source')
        $bad=Join-Path $root 'bad.md';[IO.File]::WriteAllText($bad,'Different generated notes')
        Reject {Publish -NotesPath $bad} 'reviewed UTF-8 source';Require ($mock.Mutations -eq 0) 'Remote mutation preceded source text comparison'
    }
    Case 'complete real publisher success' {Publish;Require (!$mock.Draft -and $mock.Uploads -eq 7 -and $checks.LatestPointerVerified) 'Incomplete publisher path'}
    Case 'lost create and promotion response recovery' {$mock.CreateLost=$true;$mock.PublishLost=$true;Publish;Require ($checks.DraftPromoted -and $checks.LatestPointerVerified) 'Recovery failed'}
    Case 'draft without a Git tag can publish safely' {$mock.TagApiUnavailable=$true;Publish;Require ($checks.LatestPointerVerified -and !@($mock.Calls|Where-Object{$_ -match '/releases/tags/'}).Count) 'Draft lookup still depends on a Git tag'}
    Case 'new draft absent from history still publishes by returned ID' {$mock.HiddenDraftHistory=$true;Publish;Require ($checks.LatestPointerVerified -and $mock.Uploads -eq 7) 'New draft still requires list visibility'}
    Case 'transient release ID lookup failure recovers' {$mock.ReadIdFailures=2;Publish;Require ($checks.LatestPointerVerified) 'Bounded ID recovery failed'}
    Case 'different returned release ID blocks upload' {$mock.WrongId=$true;Reject {Publish} 'different release ID';Require ($mock.Uploads -eq 0 -and $mock.Draft) 'Wrong release accepted'}
    Case 'lost create response waits for history without duplicate POST' {$mock.CreateLost=$true;$mock.HistoryLag=2;Publish;Require ($checks.LatestPointerVerified -and @($mock.Calls|Where-Object{$_ -match 'api --method POST /repos/[^ ]+/releases -f '}).Count -eq 1) 'Draft POST retried or recovery failed'}
    Case 'unrecoverable create response leaves one draft and stops' {$mock.CreateLost=$true;$mock.HiddenDraftHistory=$true;Reject {Publish} 'not found';Require ($mock.Exists -and $mock.Draft -and $mock.Uploads -eq 0 -and @($mock.Calls|Where-Object{$_ -match 'api --method POST /repos/[^ ]+/releases -f '}).Count -eq 1) 'Unsafe duplicate create or upload'}
    Case 'lost upload response reuses verified metadata without duplicate POST' {$mock.UploadLost=$true;Publish;Require ($checks.LatestPointerVerified -and $mock.Uploads -eq 7) 'Upload POST duplicated'}
    Case 'duplicate draft lookup is refused' {$mock.Exists=$true;$mock.History+=Remote-Object;Reject {Read-RemoteRelease 'kunkunkunQoQ/SonicRoute' "v$version"} 'Duplicate release tag'}
    Case 'draft lookup includes later release pages' {$mock.History=@(1..100|ForEach-Object{@{tag_name="old-$_";draft=$false;prerelease=$false}})+@(Remote-Object);$found=Read-RemoteRelease 'kunkunkunQoQ/SonicRoute' "v$version";Require ($found.tag_name -ceq "v$version" -and $found.draft) 'Draft on later page missed'}
    Case 'upload transient failure recovers' {$mock.UploadFailures=1;Publish;Require ($mock.Uploads -eq 8 -and $checks.LatestPointerVerified) 'Upload retry failed'}
    Case 'permanent upload failure leaves draft' {$mock.UploadFailures=10;Reject {Publish} 'Failed to upload';Require ($mock.Draft -and !$checks.DraftPromoted) 'Unsafe promotion'}
    Case 'identical existing assets avoid upload' {$mock.Exists=$true;foreach($a in $golden.Values){$copy=$a.Clone();$mock.NextAssetId++;$copy.Id=$mock.NextAssetId;$mock.Assets[$a.Name]=$copy};Publish -Existing $true;Require ($mock.Uploads -eq 0 -and $checks.LatestPointerVerified) 'Idempotent run uploaded again'}
    Case 'changed known draft asset replaced using its ID' {$mock.Exists=$true;$copy=$golden['SHA256SUMS.txt'].Clone();$copy.Id=100;$copy.Size=1;$copy.Hash='0'*64;$mock.Assets[$copy.Name]=$copy;Publish -Existing $true;Require ($checks.LatestPointerVerified -and @($mock.Calls|Where-Object{$_ -match 'api --method DELETE /repos/[^ ]+/releases/assets/100$'}).Count -eq 1) 'Known replacement missed exact asset ID'}
    Case 'published release never edited' {$mock.Exists=$true;$mock.Draft=$false;Reject {Publish -Existing $true} 'matching draft';Require ($mock.Mutations -eq 0) 'Published release mutated'}
    Case 'unknown draft asset retained and aborts' {$mock.Exists=$true;$mock.Assets['other.exe']=@{Name='other.exe';Size=1;Hash=('0'*64)};Reject {Publish -Existing $true} 'Unknown or duplicate';Require ($mock.Mutations -eq 0 -and $mock.Assets.ContainsKey('other.exe')) 'Unknown asset modified'}
    Case 'partial SHA draft cannot be reused' {$mock.Exists=$true;$mock.Target=$mock.Head.Substring(0,7);Reject {Publish -Existing $true} 'full commit SHA';Require ($mock.Mutations -eq 0) 'Draft edited'}
    Case 'corrupt remote bytes prevent promotion' {$mock.DownloadCorrupt=$true;Reject {Publish} 'cryptographic verification';Require ($mock.Draft -and !$checks.DraftPromoted) 'Bad bytes published'}
    Case 'missing API digest prevents promotion' {$mock.NoDigest=$true;Reject {Publish} 'digest is missing';Require ($mock.Draft) 'Missing digest published'}
    Case 'remote note corruption prevents promotion' {$mock.CorruptNotes=$true;Reject {Publish} 'release notes differ';Require ($mock.Draft -and !$checks.DraftPromoted) 'Corrupt notes published'}
    Case 'concurrent publication stops next upload' {$mock.PromoteEarly=$true;Reject {Publish} 'matching draft';Require ($mock.Uploads -eq 1) 'Continued upload after external promotion'}
    Case 'Latest mismatch recorded as published failure' {$mock.LatestWrong=$true;Reject {Publish} 'Latest release verification';Require (!$mock.Draft -and $checks.DraftPromoted -and !$checks.LatestPointerVerified) 'Publication state hidden'}
    Case 'Latest with another ID cannot pass using the same tag' {$mock.LatestWrongId=$true;Reject {Publish} 'Latest release verification';Require (!$checks.LatestPointerVerified) 'Different release ID accepted as Latest'}
    Case 'Latest query failure recorded as published failure' {$mock.LatestFailure=$true;Reject {Publish} 'could not verify';Require (!$mock.Draft -and !$checks.LatestPointerVerified) 'Publication state hidden'}
    Case 'changed local bytes refused before remote mutation' {$dir=Fixture;$a=@{};foreach($key in $golden.Keys){$a[$key]=@{Name=$key;Path=(Join-Path $dir $key);Hash=$golden[$key].Hash;Size=$golden[$key].Size}};[IO.File]::AppendAllText($a['SHA256SUMS.txt'].Path,'x');Reject {Publish -Assets $a} 'no remote mutation';Require ($mock.Mutations -eq 0) 'Remote changed before local check'}
} finally {
    foreach($name in $savedEnv.Keys){[Environment]::SetEnvironmentVariable($name,$savedEnv[$name])}
    if(!$ReportPath){$ReportPath=Join-Path $harnessRoot 'results.json'}
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath),(@{schemaVersion=1;assets=$AssetsDirectory;fixtureRoot=$harnessRoot;results=@($results.ToArray());passed=@($results|Where-Object{$_.status -eq 'PASS'}).Count;failed=@($results|Where-Object{$_.status -eq 'FAIL'}).Count}|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
}
Write-Host "Harness report: $ReportPath"
if(@($results|Where-Object{$_.status -eq 'FAIL'}).Count -ne 0){throw 'Release harness failed; see individual cases and retained fixtures.'}

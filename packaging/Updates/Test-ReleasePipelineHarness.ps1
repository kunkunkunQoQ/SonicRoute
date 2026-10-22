<# Exercises real release functions and real packages. Only git/gh and retry delays are substituted.
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
        ApiFailure=$false;CreateLost=$false;UploadFailures=0;DownloadCorrupt=$false;NoDigest=$false;PublishLost=$false;LatestWrong=$false;LatestFailure=$false;PromoteEarly=$false}
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
    throw "Unimplemented git boundary: $line"
}
function Remote-Object {
    $assets=@($mock.Assets.Values|ForEach-Object{@{name=$_.Name;size=$_.Size;state='uploaded';digest=$(if($mock.NoDigest){$null}else{'sha256:'+$_.Hash})}})
    return @{tag_name="v$version";draft=$mock.Draft;prerelease=$false;target_commitish=$mock.Target;assets=$assets;html_url='https://example.invalid/release'}
}
function gh {
    $a=@($args);$line=$a -join ' ';$mock.Calls.Add($line);$global:LASTEXITCODE=0
    if($a[0] -eq 'api'){
        if($mock.ApiFailure){$global:LASTEXITCODE=1;return}
        if($a[1] -match '/releases\?per_page=100&page=(\d+)$'){
            return (ConvertTo-Json -InputObject @($mock.History|Select-Object -Skip (([int]$Matches[1]-1)*100) -First 100) -Depth 8 -Compress)
        }
        if($a[1] -match '/releases/latest$'){
            if($mock.LatestFailure){$global:LASTEXITCODE=1;return}
            $r=Remote-Object;if($mock.LatestWrong){$r.tag_name='v1.24'};return ($r|ConvertTo-Json -Depth 8 -Compress)
        }
        if($a[1] -match '/releases/tags/'){
            if(!$mock.Exists){$global:LASTEXITCODE=1;return};return (Remote-Object|ConvertTo-Json -Depth 8 -Compress)
        }
    }
    if($a[0] -eq 'release'){
        if($a[1] -in @('create','edit','upload')){$mock.Mutations++}
        switch($a[1]){
            'create' {$mock.Exists=$true;if($mock.CreateLost){$global:LASTEXITCODE=1};return}
            'edit' {if($a -contains '--draft=false'){$mock.Draft=$false;if($mock.PublishLost){$global:LASTEXITCODE=1}};return}
            'upload' {
                $mock.Uploads++;if($mock.UploadFailures -gt 0){$mock.UploadFailures--;$global:LASTEXITCODE=1;return}
                $path=$a[3];$name=[IO.Path]::GetFileName($path)
                $mock.Assets[$name]=@{Name=$name;Path=$path;Size=(Get-Item -LiteralPath $path).Length;Hash=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()}
                if($mock.PromoteEarly){$mock.Draft=$false};return
            }
            'download' {
                $name=$a[([array]::IndexOf($a,'--pattern')+1)];$dir=$a[([array]::IndexOf($a,'--dir')+1)]
                if(!$mock.Assets.ContainsKey($name)){$global:LASTEXITCODE=1;return}
                Copy-Item -LiteralPath $mock.Assets[$name].Path -Destination (Join-Path $dir $name) -Force
                if($mock.DownloadCorrupt){[IO.File]::AppendAllText((Join-Path $dir $name),'corrupt')};return
            }
        }
    }
    throw "Unimplemented gh boundary: $line"
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
function Publish([switch]$DryRun,[bool]$Existing=$false,$Assets=$golden){
    $script:checks=@{DraftCreated=$false;RemoteAssetsVerified=$false;DraftPromoted=$false;LatestPointerVerified=$false}
    Publish-ReleaseToGitHub -AssetsDir $AssetsDirectory -ChangelogTarget "$harnessRoot/notes.md" -Version $version -Tag "v$version" -CommitSha $mock.Head -LocalAssets $Assets -TargetRepo 'kunkunkunQoQ/SonicRoute' -HasExistingDraft:$Existing -IsDryRun:$DryRun -ElapsedStr 'test' -Checklist $checks
}
try {
    Case 'retry executes real action until success' {$script:n=0;$r=Invoke-WithRetry -Action {$script:n++;if($n -lt 3){throw 'transient'};'ok'};Require ($n -eq 3 -and $r -eq 'ok') 'Retry count/result incorrect'}
    Case 'permanent failure stops after bound' {$script:n=0;Reject {Invoke-WithRetry -Action {$script:n++;throw 'permanent'}} 'permanent';Require ($n -eq 3) 'Retry bound incorrect'}
    Case 'real preflight accepts exact clean source' {$r=Preflight;Require ($r.RemoteVerified -and $r.CommitSha -ceq $mock.Head) 'Unverified result'}
    Case 'preflight rejects unrelated source branch' {$mock.Branch='feature/test';Reject {Preflight} 'non-master'}
    Case 'preflight includes untracked files' {$mock.Dirty=$true;Reject {Preflight} 'clean source'}
    Case 'CI commit mismatch' {$env:GITHUB_REF='refs/heads/master';$env:GITHUB_REPOSITORY='kunkunkunQoQ/SonicRoute';$env:GITHUB_SHA='b'*40;try{Reject {Preflight} 'CI ref'}finally{$env:GITHUB_REF=$null;$env:GITHUB_SHA=$null;$env:GITHUB_REPOSITORY=$null}}
    Case 'published current version immutable' {$mock.History+=@{tag_name="v$version";draft=$false;prerelease=$false};Reject {Preflight} 'matching draft'}
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
    Case 'complete real publisher success' {Publish;Require (!$mock.Draft -and $mock.Uploads -eq 7 -and $checks.LatestPointerVerified) 'Incomplete publisher path'}
    Case 'lost create and promotion response recovery' {$mock.CreateLost=$true;$mock.PublishLost=$true;Publish;Require ($checks.DraftPromoted -and $checks.LatestPointerVerified) 'Recovery failed'}
    Case 'upload transient failure recovers' {$mock.UploadFailures=1;Publish;Require ($mock.Uploads -eq 8 -and $checks.LatestPointerVerified) 'Upload retry failed'}
    Case 'permanent upload failure leaves draft' {$mock.UploadFailures=10;Reject {Publish} 'Failed to upload';Require ($mock.Draft -and !$checks.DraftPromoted) 'Unsafe promotion'}
    Case 'identical existing assets avoid upload' {$mock.Exists=$true;foreach($a in $golden.Values){$mock.Assets[$a.Name]=$a};Publish -Existing $true;Require ($mock.Uploads -eq 0 -and $checks.LatestPointerVerified) 'Idempotent run uploaded again'}
    Case 'published release never edited' {$mock.Exists=$true;$mock.Draft=$false;Reject {Publish -Existing $true} 'matching draft';Require ($mock.Mutations -eq 0) 'Published release mutated'}
    Case 'unknown draft asset retained and aborts' {$mock.Exists=$true;$mock.Assets['other.exe']=@{Name='other.exe';Size=1;Hash=('0'*64)};Reject {Publish -Existing $true} 'Unknown or duplicate';Require ($mock.Mutations -eq 0 -and $mock.Assets.ContainsKey('other.exe')) 'Unknown asset modified'}
    Case 'partial SHA draft cannot be reused' {$mock.Exists=$true;$mock.Target=$mock.Head.Substring(0,7);Reject {Publish -Existing $true} 'full commit SHA';Require ($mock.Mutations -eq 0) 'Draft edited'}
    Case 'corrupt remote bytes prevent promotion' {$mock.DownloadCorrupt=$true;Reject {Publish} 'cryptographic verification';Require ($mock.Draft -and !$checks.DraftPromoted) 'Bad bytes published'}
    Case 'missing API digest prevents promotion' {$mock.NoDigest=$true;Reject {Publish} 'digest is missing';Require ($mock.Draft) 'Missing digest published'}
    Case 'concurrent publication stops next upload' {$mock.PromoteEarly=$true;Reject {Publish} 'matching draft';Require ($mock.Uploads -eq 1) 'Continued upload after external promotion'}
    Case 'Latest mismatch recorded as published failure' {$mock.LatestWrong=$true;Reject {Publish} 'Latest release verification';Require (!$mock.Draft -and $checks.DraftPromoted -and !$checks.LatestPointerVerified) 'Publication state hidden'}
    Case 'Latest query failure recorded as published failure' {$mock.LatestFailure=$true;Reject {Publish} 'could not verify';Require (!$mock.Draft -and !$checks.LatestPointerVerified) 'Publication state hidden'}
    Case 'changed local bytes refused before remote mutation' {$dir=Fixture;$a=@{};foreach($key in $golden.Keys){$a[$key]=@{Name=$key;Path=(Join-Path $dir $key);Hash=$golden[$key].Hash;Size=$golden[$key].Size}};[IO.File]::AppendAllText($a['SHA256SUMS.txt'].Path,'x');Reject {Publish -Assets $a} 'no remote mutation';Require ($mock.Mutations -eq 0) 'Remote changed before local check'}
} finally {
    foreach($name in $savedEnv.Keys){[Environment]::SetEnvironmentVariable($name,$savedEnv[$name])}
    if(!$ReportPath){$ReportPath=Join-Path $harnessRoot 'results.json'}
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath),(@{schemaVersion=1;assets=$AssetsDirectory;fixtureRoot=$harnessRoot;results=@($results.ToArray());passed=@($results|Where-Object{$_.status -eq 'PASS'}).Count;failed=@($results|Where-Object{$_.status -eq 'FAIL'}).Count}|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
}
Write-Host "Harness report: $ReportPath"
if(@($results|Where-Object{$_.status -eq 'FAIL'}).Count -ne 0){throw 'Release harness failed; see individual cases and retained fixtures.'}

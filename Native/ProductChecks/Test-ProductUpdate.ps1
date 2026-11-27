[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProductExe,
      [Parameter(Mandatory)][ValidateSet('Full','Lite')][string]$Edition,
      [Parameter(Mandatory)][string]$EvidenceDirectory)

$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $evidence){throw 'Choose a new evidence directory.'}
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$binary=(Get-Item -LiteralPath $ProductExe).FullName
$profile='handoff'
$instanceRoot=Join-Path $evidence "SonicRoute.NativeCandidate/$profile/$Edition"
$data=Join-Path $instanceRoot 'data'
$updates=Join-Path $data 'Updates'
$target=Join-Path $instanceRoot 'install/SonicRoute.exe'
$offlineManifest=Join-Path $updates 'SonicRoute-update.json'
$updateResult=Join-Path $updates 'last-result.json'
[IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $updates 'downloads')) | Out-Null
Copy-Item -LiteralPath $binary -Destination $target
# Unsigned PE overlay distinguishes old and new bytes while both revisions run the actual candidate.
$stream=[IO.File]::Open($target,[IO.FileMode]::Append,[IO.FileAccess]::Write)
try{ $stream.Write([Text.Encoding]::ASCII.GetBytes('SonicRoute isolated old-update-fixture')) }finally{$stream.Dispose()}
$original=Join-Path $evidence 'old-image.fixture'
Copy-Item -LiteralPath $target -Destination $original
$initialHash=(Get-FileHash -LiteralPath $target).Hash
$payloadHash=(Get-FileHash -LiteralPath $binary).Hash.ToLowerInvariant()
$size=(Get-Item -LiteralPath $binary).Length
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo($binary).ProductVersion
$asset=if($Edition -eq 'Full'){"SonicRoute-v$version-x64.exe"}else{"SonicRoute-v$version-Lite-x64.exe"}
$file=@{Path='SonicRoute.exe'; Size=$size; Sha256=$payloadHash}
$package=@{Edition=$Edition;Runtime='net10';Architecture='x64';Asset=$asset;Size=$size;Sha256=$payloadHash;Files=@($file)}
$manifest=@{SchemaVersion=1;ProtocolVersion=2;Version=$version;Packages=@($package)}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $offlineManifest -Encoding utf8NoBOM
Copy-Item -LiteralPath $binary -Destination (Join-Path $updates "downloads/$payloadHash.package")

function Start-Candidate([string]$path,[string[]]$arguments,[bool]$capture=$false){
    $info=[Diagnostics.ProcessStartInfo]::new($path)
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $info.WorkingDirectory=Split-Path -Parent $path
    $info.Environment['SONICROUTE_CANDIDATE_ROOT']=$evidence
    $info.Environment['SONICROUTE_CANDIDATE_ID']=$profile
    $info.RedirectStandardOutput=$capture
    $info.RedirectStandardError=$capture
    foreach($argument in $arguments){$info.ArgumentList.Add($argument)}
    [Diagnostics.Process]::Start($info)
}

function Invoke-Control([string[]]$arguments){
    $client=Start-Candidate $binary (@('--control')+$arguments) $true
    try{
        $stdout=$client.StandardOutput.ReadToEndAsync()
        $stderr=$client.StandardError.ReadToEndAsync()
        if(!$client.WaitForExit(20000)){throw "Candidate control timeout ($arguments)"}
        [pscustomobject]@{Code=$client.ExitCode;Text=$stdout.Result.Trim();Error=$stderr.Result.Trim()}
    }finally{$client.Dispose()}
}

function Get-LiveStatus([int]$previous=0){
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do{
        $r=Invoke-Control @('status')
        if($r.Code -eq 0 -and $r.Text.StartsWith('{')){
            $status=$r.Text | ConvertFrom-Json
            if($status.Pid -ne $previous -and $status.DataDirectory -eq $data){
                if($status.Edition -cne $Edition -or $status.Packaged -ne $false -or $status.NativeAot -ne $true -or $status.ReflectionJson -ne $false){
                    throw 'Unexpected Native AOT product identity.'
                }
                return $status
            }
        }
        Start-Sleep -Milliseconds 125
    }while($timer.Elapsed.TotalSeconds -lt 24)
    throw "Could not find replacement candidate PID (last $($r | ConvertTo-Json -Compress))."
}

function Wait-CandidateExit([int]$candidatePid){
    $live=Get-Process -Id $candidatePid -ErrorAction SilentlyContinue
    if($null -eq $live){return}
    try{
        if($live.Path -ne $target){throw 'Candidate PID was reused by a different path.'}
        if(!$live.WaitForExit(15000)){throw "Candidate PID $candidatePid is still running."}
    }finally{$live.Dispose()}
}

function Wait-Outcome([string]$value){
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do{
        if(Test-Path -LiteralPath $updateResult){
            try {
                $journal=[IO.File]::ReadAllText($updateResult) | ConvertFrom-Json
                if($journal.Status -eq $value){return $journal}
            } catch [IO.IOException] {
                # The worker may still have the result file open during atomic replacement.
            }
        }
        Start-Sleep -Milliseconds 125
    }while($timer.Elapsed.TotalSeconds -lt 20)
    throw "Timed out waiting for $value."
}

$hostProcess=Start-Candidate $target @()
try{
    $old=Get-LiveStatus
    $restart=Invoke-Control @('restart')
    if($restart.Text -ne 'ok'){throw "Restart returned $($restart.Text)"}
    $restarted=Get-LiveStatus $old.Pid
    Wait-CandidateExit $old.Pid
    $wrong=Invoke-Control @('install',(Join-Path $evidence 'outside.json'))
    if($wrong.Code -eq 0){throw 'External manifest was accepted.'}
    $install=Invoke-Control @('install',$offlineManifest)
    if($install.Text -ne 'ok'){throw "Installer returned $($install.Text)"}
    $updated=Get-LiveStatus $restarted.Pid
    Wait-CandidateExit $restarted.Pid
    $during=Invoke-Control @('exit')
    if($during.Code -eq 0 -or $during.Text -ne 'error: update_in_progress'){throw "No active-worker protection: $($during | ConvertTo-Json -Compress)"}
    $commit=Wait-Outcome 'committed'
    if((Get-FileHash $target).Hash -ne $payloadHash){throw 'Committed target hash mismatch.'}
    $exit=Invoke-Control @('exit')
    if($exit.Text -ne 'ok'){throw 'Updated host failed to exit.'}
    Wait-CandidateExit $updated.Pid

    # Reconstruct the on-disk post-Apply/pre-ack state from the real journal and
    # hash-verified backup. Only the interrupted journal is injected. Recovery,
    # binary replacement, worker/child launches, and live IPC all use product code.
    $planPath=Join-Path $updates "transactions/$($commit.Id)/plan.json"
    $plan=Get-Content -Raw -LiteralPath $planPath | ConvertFrom-Json
    $backupDir=Join-Path (Split-Path $target) ".sonicroute-update-$($commit.Id)/backup"
    [IO.Directory]::CreateDirectory($backupDir) | Out-Null
    Copy-Item -LiteralPath $original -Destination (Join-Path $backupDir 'SonicRoute.exe')
    $plan.Status='starting'
    $plan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $planPath -Encoding utf8NoBOM
    [IO.File]::WriteAllText((Join-Path (Split-Path $target) '.sonicroute-update.pending'),$commit.Id)
    $bootstrap=Start-Candidate $target @()
    try { if(!$bootstrap.WaitForExit(10000)){throw 'Recovery bootstrap failed to hand off.'} } finally { $bootstrap.Dispose() }
    $rollback=Wait-Outcome 'rolledBack'
    $restored=Get-LiveStatus $updated.Pid
    if((Get-FileHash -LiteralPath $target).Hash -ne $initialHash){throw 'Restored executable differs from the original.'}
    $exit=Invoke-Control @('exit')
    if($exit.Text -ne 'ok'){throw 'Restored instance failed to exit.'}
    Wait-CandidateExit $restored.Pid
    $receipt=[ordered]@{
        edition=$Edition;runtime='net10';protocol=2;version=$version;testIdentity=$profile;privateData=$data
        pids=@($old.Pid,$restarted.Pid,$updated.Pid,$restored.Pid);committed=$commit;rolledBack=$rollback
        oldSha256=$initialHash;newSha256=$payloadHash;restoredSha256=(Get-FileHash -LiteralPath $target).Hash
        restart='ready event -> previous PID exit -> replacement accepts IPC'
        inProgressExit=$during;externalManifest=$wrong;noRecoveryMarker=!(Test-Path (Join-Path (Split-Path $target) '.sonicroute-update.pending'))
        recoveryFixture='interrupted starting journal reconstructed after positive commit; old PE differs by test-only overlay'
        remainingProcess=(Get-Process -Id $restored.Pid -ErrorAction SilentlyContinue) -ne $null
    }
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'receipt.json') -Encoding utf8NoBOM
    $receipt | ConvertTo-Json -Depth 8
}finally{
    $hostProcess.Dispose()
    # On assertion failure release only this explicitly scoped candidate if it is listening.
    $null=Invoke-Control @('exit')
}

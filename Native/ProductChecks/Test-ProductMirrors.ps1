[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProductExe,
      [Parameter(Mandatory)][string]$PcmSourceExe,
      [Parameter(Mandatory)][ValidateSet('Full','Lite')][string]$Edition,
      [Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $root){throw 'Use a new evidence directory.'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$hostExe=(Get-Item -LiteralPath $ProductExe).FullName
$sourceExe=Join-Path $root 'DisposablePcmEmitter.exe'
Copy-Item -LiteralPath $PcmSourceExe -Destination $sourceExe
function Start-Owned([string]$exe,[string[]]$arguments,[bool]$capture,[bool]$inputPipe=$false){
    $info=[Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$capture
    $info.RedirectStandardError=$capture
    $info.RedirectStandardInput=$inputPipe
    $info.Environment['SONICROUTE_CANDIDATE_ROOT']=$root
    $info.Environment['SONICROUTE_CANDIDATE_ID']='mirror'
    $info.Environment['SONICROUTE_CANDIDATE_METRICS']='1'
    foreach($arg in $arguments){$info.ArgumentList.Add($arg)}
    [Diagnostics.Process]::Start($info)
}
function Control([string[]]$arguments,[bool]$permitError=$false){
    $client=Start-Owned $hostExe (@('--control')+$arguments) $true
    try{
        $output=$client.StandardOutput.ReadToEndAsync()
        $errors=$client.StandardError.ReadToEndAsync()
        if(!$client.WaitForExit(11000)){throw "timeout $arguments"}
        if(!$permitError -and $client.ExitCode -ne 0){throw "$arguments => $($output.Result) $($errors.Result)"}
        return $output.Result.Trim()
    }finally{$client.Dispose()}
}
function Start-Source{
    $p=Start-Owned $sourceExe @('--source') $true $true
    try{
        $line=$p.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(6)).GetAwaiter().GetResult()
        if($line -ne 'READY'){throw "No PCM source (received $line)."}
        return $p
    }catch{ $p.StandardInput.Close(); $p.Dispose(); throw }
}
function Stop-Source($p){
    if($null -eq $p){return}
    try{
        if(!$p.HasExited){$p.StandardInput.WriteLine('stop');$p.StandardInput.Close()}
        if(!$p.WaitForExit(4000)){throw 'PCM source failed to retire.'}
    }finally{$p.Dispose()}
}
function MirrorStatus([int]$sourceId){Control @('mirror-status',"$sourceId") | ConvertFrom-Json}
$server=Start-Owned $hostExe @() $false
$source=$null
try{
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do{
        $line=Control @('status') $true
        if($line.StartsWith('{')){break}
        Start-Sleep -Milliseconds 100
    }while([DateTime]::UtcNow -lt $deadline)
    $status=$line|ConvertFrom-Json
    if($status.Pid -ne $server.Id -or !$status.NativeAot){throw 'Unexpected candidate identity.'}
    $source=Start-Source
    $firstPid=$source.Id
    $default=Control @('default','render','multimedia')
    $devices=Control @('devices','render')|ConvertFrom-Json
    $extras=@($devices | Where-Object { $_.Id -ne $default } | Select-Object -First 2 -ExpandProperty Id)
    if($extras.Count -ne 2){throw 'Need two distinct extra output endpoints.'}
    $sentinel='@@SYSTEM_DEFAULT@@'
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $set=Control (@('outputs-set',"$firstPid",$sentinel,$sentinel)+$extras)
    $setMs=$timer.Elapsed.TotalMilliseconds
    if($set -ne 'ok'){throw $set}
    $initial=MirrorStatus $firstPid
    Start-Sleep -Seconds 5
    $metrics=MirrorStatus $firstPid
    if(!$metrics.Active -or !$metrics.EventDriven -or $metrics.Sinks.Count -ne 2 -or $metrics.Packets -lt 100){throw 'Expected an active event-driven dual-output product stream.'}
    foreach($sink in $metrics.Sinks){
        if($sink.SubmittedFrames -lt 96000 -or $sink.NonSilentPacketFrames -le 0){throw 'Endpoint did not receive non-silent PCM.'}
    }
    $saved=Join-Path $status.DataDirectory 'playback-outputs.json'
    if(!(Test-Path -LiteralPath $saved)){throw 'Selection did not persist.'}
    Copy-Item -LiteralPath $saved -Destination (Join-Path $root 'saved-multi.json')
    $oldSelection=Control @('outputs-get',"$firstPid")|ConvertFrom-Json
    $bad=Control @('outputs-set',"$firstPid",$extras[0],$extras[0],'{missing-test-endpoint}') $true
    if(!$bad.StartsWith('error:')){throw 'Unusable extra endpoint was accepted.'}
    $afterFailure=Control @('outputs-get',"$firstPid")|ConvertFrom-Json
    if(Compare-Object $oldSelection $afterFailure){throw 'Failed transaction changed live selection.'}
    Stop-Source $source
    $source=$null
    $source=Start-Source
    $secondPid=$source.Id
    $deadline=[DateTime]::UtcNow.AddSeconds(7)
    do{
        $restored=MirrorStatus $secondPid
        if($restored.Active -and $restored.Sinks.Count -eq 2 -and $restored.Sinks[1].NonSilentPacketFrames -gt 0){break}
        Start-Sleep -Milliseconds 110
    }while([DateTime]::UtcNow -lt $deadline)
    if(!$restored.Active -or $restored.Sinks.Count -ne 2 -or $restored.Sinks[1].NonSilentPacketFrames -le 0){throw 'Saved app did not regain its two mirrors after its PID changed.'}
    if((MirrorStatus $firstPid).Active){throw 'Exited source still owns a capture route.'}
    $replaced=Control @('outputs-set',"$secondPid",$sentinel,$sentinel)
    if($replaced -ne 'ok' -or (MirrorStatus $secondPid).Active){throw 'Manual single-output selection failed to retire the restored mirrors.'}
    $remaining=Get-Content -Raw -LiteralPath $saved|ConvertFrom-Json -AsHashtable
    if($remaining.Count -ne 0){throw 'Manual single selection left a stale saved intent.'}
    if((Control @('default','render','multimedia')) -ne $default){throw 'System default endpoint changed.'}
    $null=Control (@('outputs-set',"$secondPid",$sentinel,$sentinel)+$extras)
    $activeAtExit=MirrorStatus $secondPid
    if(!$activeAtExit.Active -or $activeAtExit.Sinks.Count -ne 2){throw 'Expected both output clients open when exiting.'}
    if((Control @('exit')) -ne 'ok' -or !$server.WaitForExit(10000)){throw 'Product failed to exit with live streams.'}
    if($source.HasExited){throw 'Emitter ended before host shutdown.'}
    $exitReceipt=Get-Content -Raw -LiteralPath (Join-Path (Split-Path $status.DataDirectory) 'last-exit.json')|ConvertFrom-Json
    if($exitReceipt.Pid -ne $server.Id -or ($exitReceipt.AfterServiceStop.PSObject.Properties.Value | Where-Object {$_ -ne 0})){
        throw 'Audio owners remained after service shutdown.'
    }
    Stop-Source $source
    $source=$null
    $receipt=[ordered]@{edition=$Edition;product=$hostExe;productSha256=(Get-FileHash $hostExe).Hash;hostPid=$server.Id
        stimulus='separate isolated PCM16 400 Hz process; product does all routing and capture';sourcePid=$firstPid;restoredPid=$secondPid
        defaultRetained=$default;explicitTargets=$extras;selectionRoundTripMs=$setMs;firstSnapshot=$initial;sample=$metrics
        failedSelection=$bad;rollbackKept=$afterFailure;restored=$restored;manualOverrideRetired=$true;liveSinksAtExit=$activeAtExit.Sinks.Count;release=$exitReceipt;exit=$server.ExitCode}
    $receipt|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $root 'receipt.json') -Encoding utf8NoBOM
    $receipt|ConvertTo-Json -Depth 7
}finally{
    Stop-Source $source
    if(!$server.HasExited){$null=Control @('exit') $true;$null=$server.WaitForExit(10000)}
    $server.Dispose()
}

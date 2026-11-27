#requires -Version 7.4
param([Parameter(Mandatory)][int]$Transport, [Parameter(Mandatory)][string]$DeviceCode,
    [Parameter(Mandatory)][string]$Report, [Parameter(Mandatory)][long]$MediaId,
    [Parameter(Mandatory)][int]$Target,
    [int]$ExistingTestSession,
    [switch]$Windowed,
    [ValidateSet('bin.mt.plus','cn.wenyu.bodian')][string]$Player='bin.mt.plus',
    [ValidateSet('shizuku_root','shizuku_shell','direct_root')][string]$Backend='shizuku_root',
    [string]$Adb=(Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe'))
$ErrorActionPreference='Stop'
if (Test-Path -LiteralPath $Report) { throw 'Choose a fresh evidence file.' }
if ((& $Adb -t $Transport shell getprop ro.product.device).Trim() -ne $DeviceCode) { throw 'Unexpected device.' }
if ((& $Adb -t $Transport shell dumpsys activity services org.sonicroute.app) -match 'ServiceRecord\{') { throw 'SonicRoute is currently in use.' }
$uri="content://media/external/audio/media/$MediaId"
$entry=& $Adb -t $Transport shell content query --uri $uri --projection _display_name
if ($entry -notmatch '_display_name=SonicRoute-Interop-[A-F0-9]+\.wav$') { throw 'Select the known test recording.' }
$pkg=& $Adb -t $Transport shell pm list packages -U $Player
if ($pkg -notmatch "^package:$([regex]::Escape($Player)) uid:(\d+)$") { throw 'Independent player is unavailable.' }
$uid=[int]$Matches[1]
function Snapshot {
    $lines=& $Adb -t $Transport shell dumpsys audio
    $tracks=@(foreach($line in $lines){
        if($line -match "AudioPlaybackConfiguration piid:(\d+) deviceIds:\[([^\]]*)\].*?u/pid:${uid}/\d+ state:started.*?usage=USAGE_(MEDIA|UNKNOWN)\b.*?sessionId:(\d+)" ){
            [pscustomobject]@{PlayerId=[long]$Matches[1];Usage=$Matches[3];Session=$Matches[4];DeviceIds=@($Matches[2].Split(',',[StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object {[int]$_.Trim()})}
        }
    })
    $output='';$standby='';$inTable=$false
    $mixer=@(foreach($line in (& $Adb -t $Transport shell dumpsys media.audio_flinger)){
        if($line -match '^Output thread '){$output='';$standby='';$inTable=$false}
        elseif($line -match '^\s+Standby: (yes|no)\s*$'){$standby=$Matches[1]}
        elseif($line -match '^\s+Output devices: .*?\((AUDIO_DEVICE_OUT_[A-Z0-9_| ,]+)\)'){$output=$Matches[1]}
        elseif($line -match '^\s*Type\s+Id\s+Active\s+Client\(pid/uid\)'){$inTable=$true}
        elseif($inTable -and $line -match "^\s*(?:\S+\s+)?(\d+)\s+yes\s+\d+/\s*${uid}\s+(\d+)\s+\d+\s+\S+\s+\S+\s+\S+\s+\S+\s+(\d+)\s+(\d+)\s+" ){
            [pscustomobject]@{Output=$output;Standby=$standby;Track=$Matches[1];Session=$Matches[2];Rate=$Matches[3];Stream=$Matches[4]}
        } elseif($inTable -and [string]::IsNullOrWhiteSpace($line)){$inTable=$false}
    })
    $music=@($mixer | Where-Object {$_.Standby -eq 'no' -and $_.Stream -eq '3'} | ForEach-Object {$_.Session})
    return [pscustomobject]@{Tracks=@($tracks | Where-Object {$_.Usage -eq 'MEDIA' -or $_.Session -in $music});Mixer=$mixer}
}
$record=[ordered]@{Player=$Player;Uid=$uid;Backend=$Backend;Target=$Target;Media=$entry;Result='pending'}
$run=[guid]::NewGuid().ToString('N')
$instrument=$null
try {
    $initial=Snapshot
    $component=if($Player -eq 'bin.mt.plus'){'bin.mt.plus/.MusicPlayerActivity'}else{'cn.wenyu.bodian/.EntryActivity'}
    if ($ExistingTestSession) {
        if (!$initial.Tracks.Count -or @($initial.Tracks | Where-Object {$_.Session -ne [string]$ExistingTestSession}).Count) {
            throw 'The previously identified test media session has ended or changed.'
        }
    } else {
        if ($initial.Tracks.Count) { throw 'The selected player already has active audio. Its playback was left with the user.' }
        & $Adb -t $Transport shell am start -W --grant-read-uri-permission -a android.intent.action.VIEW -d $uri -t audio/wav -n $component
    }
    $deadline=[DateTime]::UtcNow.AddSeconds(17)
    do { $record.Before=Snapshot; if($record.Before.Tracks.Count){break};Start-Sleep -Milliseconds 400 } while([DateTime]::UtcNow -lt $deadline)
    if(!$record.Before.Tracks.Count){throw 'The third-party player did not start the selected media track.'}
    if($record.Before.Tracks.DeviceIds -contains $Target){throw 'Target already in use by this player. Choose a different physical endpoint.'}
    $info=[Diagnostics.ProcessStartInfo]::new($Adb)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true
    foreach($arg in @('-t',[string]$Transport,'shell','am','instrument','-w','-r','-e','mode','route','-e','run',$run,
        '-e','backend',$Backend,'-e','player',$Player,'-e','target',[string]$Target,'org.sonicroute.app.test/org.sonicroute.app.test.LanInteropRunner')){$info.ArgumentList.Add($arg)}
    $instrument=[Diagnostics.Process]::Start($info)
    do {
        $line=$instrument.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(43)).GetAwaiter().GetResult()
        if($line -eq 'INSTRUMENTATION_STATUS: launch=ready') {
            $windowMode = if ($Windowed) {5} else {1}
            $null=& $Adb -t $Transport shell am start -W --windowingMode $windowMode -n org.sonicroute.app/.MainActivity
        }
        if($line -eq 'INSTRUMENTATION_STATUS: route=verified'){
            $record.Routed=Snapshot
            $null=& $Adb -t $Transport shell run-as org.sonicroute.app touch "cache/route-$run.sampled"
        }
        if($line -match '^INSTRUMENTATION_RESULT: (interop|routeReason)=(.*)$'){$record[$Matches[1]]=$Matches[2]}
    } while($null -ne $line)
    $record.Restored=Snapshot
    $before=@($record.Before.Tracks | ForEach-Object {$_.PlayerId})
    $matching=@($record.Routed.Tracks | Where-Object {$_.PlayerId -in $before -and $_.DeviceIds.Count -eq 1 -and $_.DeviceIds -contains $Target})
    $restored=@($record.Restored.Tracks | Where-Object {$_.PlayerId -in $matching.PlayerId -and $_.DeviceIds -notcontains $Target})
    if($record.interop -ne 'passed' -or !$matching.Count -or !$restored.Count){throw 'The application and independent Android observations did not confirm a full route and restore.'}
    $record.Result='passed'
} catch { $record.Result='failed';$record.Detail=$_.Exception.Message;throw }
finally {
    $record|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $Report -Encoding utf8
    if($instrument){$instrument.Dispose()}
}

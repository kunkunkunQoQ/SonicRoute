#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$AudioSource,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Full','Lite')][string]$Edition='Full'
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path $root){throw 'Supply a fresh fixture directory.'}
$id='Current'+[guid]::NewGuid().ToString('N')
$sourceName='SRA'+[guid]::NewGuid().ToString('N').Substring(0,10)
$fixture=Join-Path $root "SonicRoute.NativeCandidate/$id/$Edition"
$data=Join-Path $fixture 'data'
$install=Join-Path $fixture 'install'
New-Item $data,$install -ItemType Directory -Force|Out-Null
$serverExe=Join-Path $install 'SonicRoute.exe'
$sourceExe=Join-Path $root "$sourceName.exe"
Copy-Item -LiteralPath $Executable -Destination $serverExe
Copy-Item -LiteralPath $AudioSource -Destination $sourceExe
[ordered]@{Language='zh-CN';StartMinimized=$true;AutoCheckUpdates=$false;Hotkeys=@{};TrayWheelPaused=$true;DefaultAppMode='fixed';FixedAppName=$sourceName}|ConvertTo-Json|Set-Content "$data/config.json" -Encoding utf8
function Start-Child([string]$exe,[string[]]$arguments){
    $info=[Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WorkingDirectory=$root
    $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.Environment['SONICROUTE_CANDIDATE_ROOT']=$root;$info.Environment['SONICROUTE_CANDIDATE_ID']=$id
    foreach($a in $arguments){$info.ArgumentList.Add($a)}
    [Diagnostics.Process]::Start($info)
}
function Command([string[]]$parameters,[switch]$ExpectedFailure){
    $client=Start-Child $serverExe (@('--control')+$parameters)
    try{
        $output=$client.StandardOutput.ReadToEndAsync()
        $errorText=$client.StandardError.ReadToEndAsync()
        if(!$client.WaitForExit(6000)){throw 'Client timed out.'}
        $result=$output.Result.Trim()
        if((!$ExpectedFailure -and $client.ExitCode -ne 0) -or ($ExpectedFailure -and $client.ExitCode -eq 0)){
            throw "Unexpected exit $($client.ExitCode) for $($parameters[0]): $result $($errorText.Result)"
        }
        return $result
    }finally{$client.Dispose()}
}
# Use the product's existing framing for overlapping clients. The pipe name is bound
# to this fixture's private path, session, current user and edition.
if(!('SonicRoute.Core.RuleCommandFrame' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot '../../SonicRoute.Core/RuleCommandFrame.cs')}
$key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($fixture).ToUpperInvariant()))).Substring(0,24)
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$session=[Diagnostics.Process]::GetCurrentProcess().SessionId
$pipeName="SonicRoute.NativeControl.NativeCandidate_${key}_${Edition}.$session.$sid"
function Open-Request([string]$verb,[string[]]$params){
    $channel=[IO.Pipes.NamedPipeClientStream]::new('.',$pipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly)
    try{
        $channel.Connect(3000)
        $frame=[ordered]@{Verb=$verb;Args=$params}|ConvertTo-Json -Compress
        [SonicRoute.Core.RuleCommandFrame]::WriteFrameAsync($channel,$frame,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
        [pscustomobject]@{Pipe=$channel;Result=[SonicRoute.Core.RuleCommandFrame]::ReadFrameAsync($channel,[Threading.CancellationToken]::None)}
    }catch{$channel.Dispose();throw}
}
function Finish-Request($request){
    try{$request.Result.WaitAsync([TimeSpan]::FromSeconds(6)).GetAwaiter().GetResult()}
    finally{$request.Pipe.Dispose()}
}
function Own-Source {
    $child=Start-Child $sourceExe @('--source')
    if($child.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() -ne 'READY'){throw 'Source did not start.'}
    return $child
}
function Check([bool]$test,[string]$name){if(!$test){throw "Failed $name"}; $checks.Add($name)}
$checks=[Collections.Generic.List[string]]::new()
$record=[ordered]@{Edition=$Edition;Candidate=(Get-FileHash $serverExe).Hash;Source=(Get-FileHash $sourceExe).Hash;Checks=$checks}
$server=$null;$sourceProcess=$null;$original=$null
try{
    $server=Start-Child $serverExe @()
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    while($true){
        try{$status=Command @('status')|ConvertFrom-Json;if($status.Pid -eq $server.Id){break}}catch{}
        if($server.HasExited -or $deadline.Elapsed.TotalSeconds -gt 12){throw 'Candidate failed to become ready.'}
        Start-Sleep -Milliseconds 120
    }
    Check ($status.Edition -eq $Edition -and $status.NativeAot -and [IO.Path]::GetFullPath($status.DataDirectory) -eq [IO.Path]::GetFullPath($data)) 'isolated product PID, config and AOT'
    $devices=@(Command @('devices','render')|ConvertFrom-Json)
    $default=Command @('default','render','console')
    $ids=@($devices|ForEach-Object Id|Select-Object -Unique)
    if($ids.Count -lt 2){throw 'Two distinct physical render endpoints are required.'}
    $first=$ids[0];$second=$ids[1]
    $sourceProcess=Own-Source
    $target=$sourceProcess.Id.ToString()
    $record.SourcePid=$sourceProcess.Id
    $original=Command @('route',$target,'render')
    $visibility=[Diagnostics.Stopwatch]::StartNew()
    do {
        $resolved=Command @('current',$target)|ConvertFrom-Json
        if($resolved.Pid -eq $sourceProcess.Id -and $resolved.Process -eq $sourceName){break}
        Start-Sleep -Milliseconds 100
    }while($visibility.Elapsed.TotalSeconds -lt 3)
    $record.SessionVisibleAfterMs=$visibility.ElapsedMilliseconds
    Check ($resolved.Pid -eq $sourceProcess.Id -and $resolved.Process -eq $sourceName) 'frozen fixed-name choice resolves the real self-created session'
    $record.Original=$original
    Check ((Command @('cycle-output',$target)) -eq 'ok') 'actual current-output shortcut plans and applies via product service'
    $state=Command @('output-state')|ConvertFrom-Json
    Check ($state.Pid -eq $sourceProcess.Id -and $state.Status -eq 1) 'optimistic output generation confirmed by policy readback'
    $record.Cycle=$state
    Check ((Command @('outputs-set',$target,$second,$first,$second)) -eq 'ok') 'manual multi-output selection'
    $multi=Command @('output-state')|ConvertFrom-Json
    Check ($multi.Status -eq 1 -and $multi.Generation -gt $state.Generation -and $multi.Requested -eq $second -and $multi.Observed -eq $second -and $multi.ObservedOutputs.Count -eq 2 -and $first -in $multi.ObservedOutputs -and $second -in $multi.ObservedOutputs) 'native primary and both selected outputs confirmed in one generation'
    $record.Multi=$multi
    $rejection=Command @('outputs-set',$target,$first,$first,'SonicRoute-Missing-Endpoint') -ExpectedFailure
    $failed=Command @('output-state')|ConvertFrom-Json
    Check ($failed.Status -eq 2 -and $failed.Generation -gt $multi.Generation -and $failed.Observed -eq $second -and $first -in $failed.ObservedOutputs -and $second -in $failed.ObservedOutputs) 'failed write reports authoritative rollback to prior two-output state'
    $record.Failure=$failed;$record.FailureReply=$rejection
    Check ((Command @('route',$target,'render',$first)) -eq 'ok') 'latest manual selection overrides prior multi-output intent'
    $single=Command @('output-state')|ConvertFrom-Json
    Check ($single.Status -eq 1 -and $single.Observed -eq $first -and $single.Generation -gt $failed.Generation) 'latest manual generation confirmed'
    $record.Manual=$single
    # Submit a manual single selection while an actual multi-output write is pending.
    # Status polling is confined to this bounded external validation.
    $old=Open-Request 'outputs-set' @($target,$second,$first,$second)
    $new=$null
    try{
        $deadline=[Diagnostics.Stopwatch]::StartNew()
        while(!$old.Result.IsCompleted -and $deadline.Elapsed.TotalSeconds -lt 2){
            $during=Finish-Request (Open-Request 'output-state' @())|ConvertFrom-Json
            if($during.Generation -gt $single.Generation -and $during.Status -eq 0){
                $new=Open-Request 'route' @($target,'render',$first)
                $record.PendingObserved=$during
                break
            }
        }
        $oldReply=Finish-Request $old
        if(!$new){throw 'Overlapping acceptance not observed within the real write window.'}
        Check ($oldReply -eq 'ok' -and (Finish-Request $new) -eq 'ok') 'manual command accepted during pending multi-output write'
        $last=Command @('output-state')|ConvertFrom-Json
        Check ($last.Generation -gt $record.PendingObserved.Generation -and $last.Pid -eq $sourceProcess.Id -and $last.Status -eq 1 -and $last.Observed -eq $first) 'superseded completion preserves latest manual generation and observed policy route'
        $record.AfterOverlap=$last
    }finally{
        $old.Pipe.Dispose()
        if($new){$new.Pipe.Dispose()}
    }
    Check ((Command @('default','render','console')) -eq $default) 'system default unchanged'
}finally{
    try{
        if($sourceProcess -and $null -ne $original -and $server -and !$server.HasExited){
            if($sourceProcess.HasExited){$sourceProcess.Dispose();$sourceProcess=Own-Source}
            $back=if($original -eq 'default'){'@@SYSTEM_DEFAULT@@'}else{$original}
            $reset=Command @('outputs-set',$sourceProcess.Id.ToString(),$back,$back)
            Check ($reset -eq 'ok' -and (Command @('route',$sourceProcess.Id.ToString(),'render')) -eq $original) 'fixture route restored before exit'
        }
    }finally{
        if($sourceProcess){if(!$sourceProcess.HasExited){$sourceProcess.StandardInput.Close()};$null=$sourceProcess.WaitForExit(13000);$sourceProcess.Dispose()}
        if($server){if(!$server.HasExited){$null=Command @('exit')};if(!$server.WaitForExit(8000)){throw 'Isolated candidate exit timed out.'};$record.Exit=$server.ExitCode;$server.Dispose()}
        $record|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'current-app.json') -Encoding utf8
    }
}

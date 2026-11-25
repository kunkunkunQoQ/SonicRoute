#requires -Version 7.0
param([Parameter(Mandatory)][ValidateSet('Wpf','Native')][string]$Mode,
      [Parameter(Mandatory)][ValidateSet('Full','Lite')][string]$Edition,
      [Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$Emitter,
      [Parameter(Mandatory)][string]$AcceptedMirrorReceipt,[Parameter(Mandatory)][string]$OutputDirectory,
      [string]$WpfHook)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $root){throw 'Use a new fixture directory.'}
$actualExe=(Get-Item $Executable).FullName
$hash=(Get-FileHash $actualExe).Hash
$wpfHashes=@{Full='C4289848808DB9B8D15367690181FC2CBE9A87A482F310394F0C4C034B750F1B';Lite='8F53515A3F48E683ECC1C66EE3918ABB03DD1D1291E5F4F56B3324E397F7CE6D'}
if($Mode -eq 'Wpf' -and $hash -ne $wpfHashes[$Edition]){throw 'Expected frozen 1.36 Windows desktop executable.'}
$instance='PerfWpf_'+[guid]::NewGuid().ToString('N')
$fixture=if($Mode -eq 'Wpf'){Join-Path $root $instance}else{Join-Path $root "SonicRoute.NativeCandidate/cold/$Edition"}
$data=Join-Path $fixture 'data'
[IO.Directory]::CreateDirectory($data)|Out-Null
[IO.Directory]::CreateDirectory((Join-Path $fixture install))|Out-Null
$target=Join-Path $fixture 'install/SonicRoute.exe'
Copy-Item $actualExe $target
Copy-Item (Join-Path $PSScriptRoot 'IdleProfile.json') (Join-Path $data 'config.json')
$sourceExe=Join-Path $data 'DisposablePcmEmitter.exe'
Copy-Item $Emitter $sourceExe
$extras=(Get-Content -Raw $AcceptedMirrorReceipt|ConvertFrom-Json).explicitTargets
if($extras.Count -ne 2 -or @($extras|Where-Object {$_ -notmatch '^\{0\.0\.0\.00000000\}\.\{[0-9a-f-]{36}\}$'}).Count -gt 0){throw 'Expected two tested physical render endpoint IDs.'}
if(!('SonicRouteProcessMetrics' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot ProcessCounters.cs)}
$envVars=@{SONICROUTE_CANDIDATE_ROOT=$root;SONICROUTE_CANDIDATE_ID='cold'}
if($Mode -eq 'Wpf'){
    $envVars.DOTNET_STARTUP_HOOKS=(Get-Item $WpfHook).FullName
    $envVars.DOTNET_BUNDLE_EXTRACT_BASE_DIR=Join-Path $fixture bundle
    $envVars.SONICROUTE_INSTANCE_ID=$instance
    $envVars.SONICROUTE_DATA_DIRECTORY=$data
    $envVars.SONICROUTE_MEASURE_EDITION=$Edition
    [IO.File]::WriteAllText((Join-Path $data "$instance.fixture"),'')
}
function Start-Owned([string]$path,[string[]]$arguments,[bool]$isSource){
    $info=[Diagnostics.ProcessStartInfo]::new($path)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WorkingDirectory=Split-Path $path
    if(!$isSource){foreach($entry in $envVars.GetEnumerator()){$info.Environment[$entry.Key]=$entry.Value}}
    $info.RedirectStandardInput=$isSource; $info.RedirectStandardOutput=$isSource
    foreach($arg in $arguments){$info.ArgumentList.Add($arg)}
    [Diagnostics.Process]::Start($info)
}
function Native-Request([string]$verb,[string[]]$parameters=@()){
    $key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($fixture.ToUpperInvariant()))).Substring(0,24)
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
    $name="SonicRoute.NativeControl.NativeCandidate_${key}_$Edition.$session.$sid"
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$name,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::CurrentUserOnly)
    try{
        $pipe.Connect(3000)
        $bytes=[Text.Encoding]::UTF8.GetBytes((@{Verb=$verb;Args=$parameters}|ConvertTo-Json -Compress))
        $pipe.Write([BitConverter]::GetBytes($bytes.Length));$pipe.Write($bytes);$pipe.Flush()
        $head=[byte[]]::new(4);$pipe.ReadExactly($head);$count=[BitConverter]::ToInt32($head)
        if($count -le 0 -or $count -gt 16384){throw 'Invalid response size.'}
        $body=[byte[]]::new($count);$pipe.ReadExactly($body)
        [Text.Encoding]::UTF8.GetString($body)
    }finally{$pipe.Dispose()}
}
function Usage{
    $server.Refresh()
    $m=[SonicRouteProcessMetrics]::ReadMemory($server)
    [pscustomobject]@{CPU=$server.TotalProcessorTime.TotalMilliseconds;PrivateBytes=$m.PrivateUsage.ToUInt64();
        PrivateWS=$m.PrivateWorkingSet.ToUInt64();Commit=$m.PagefileUsage.ToUInt64();Threads=$server.Threads.Count;Handles=$server.HandleCount}
}
$source=$null
$server=Start-Owned $target @() $false
try{
    if($Mode -eq 'Wpf'){
        $limit=[DateTime]::UtcNow.AddSeconds(20)
        while(!(Test-Path (Join-Path $data wpf.ready))){if($server.HasExited -or [DateTime]::UtcNow -gt $limit){throw 'No WPF host.'};Start-Sleep -Milliseconds 80}
    }else{
        $native=Native-Request status|ConvertFrom-Json
        if($native.Pid -ne $server.Id -or $native.DataDirectory -ne $data){throw 'Wrong native host.'}
    }
    $source=Start-Owned $sourceExe @('--source') $true
    if($source.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(6)).GetAwaiter().GetResult() -ne 'READY'){throw 'Emitter failed.'}
    $outputs=@('@@SYSTEM_DEFAULT@@')+$extras
    if($Mode -eq 'Wpf'){
        @{Pid=$source.Id;Outputs=$outputs}|ConvertTo-Json|Set-Content (Join-Path $data 'measure-route.json') -Encoding utf8NoBOM
        if([SonicRouteProcessMetrics]::SampleWpf($server.Id,$instance,$Edition,200) -ne 1){throw 'Frozen product did not accept fixture call.'}
        $resultPath=Join-Path $data route-result.json
        $limit=[DateTime]::UtcNow.AddSeconds(7)
        while(!(Test-Path $resultPath)){if([DateTime]::UtcNow -gt $limit){throw 'No route result.'};Start-Sleep -Milliseconds 30}
        $result=Get-Content -Raw $resultPath|ConvertFrom-Json
        if($result.Error){throw $result.Error}
        $elapsed=$result.ElapsedMs
    }else{
        $watch=[Diagnostics.Stopwatch]::StartNew()
        if((Native-Request 'outputs-set' (@($source.Id.ToString(),$outputs[0])+$outputs)) -ne 'ok'){throw 'Native selection failed.'}
        $elapsed=$watch.Elapsed.TotalMilliseconds
        $s=Native-Request 'mirror-status' @($source.Id.ToString())|ConvertFrom-Json
        if(!$s.Active -or $s.Sinks.Count -ne 2){throw 'Missing mirror.'}
    }
    $before=Usage
    $clock=[Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 4
    $after=Usage
    $receipt=[ordered]@{mode=$Mode;edition=$Edition;sha256=$hash;configSha256=(Get-FileHash (Join-Path $PSScriptRoot IdleProfile.json)).Hash
        sourceSha256=(Get-FileHash $sourceExe).Hash;targets=$outputs;coldSelectionMs=$elapsed;activeSeconds=$clock.Elapsed.TotalSeconds
        cpuMs=$after.CPU-$before.CPU;start=$before;end=$after;pid=$server.Id;sourcePid=$source.Id}
    $receipt|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root receipt.json) -Encoding utf8NoBOM
    $receipt|ConvertTo-Json -Depth 5
}finally{
    if($source){$source.StandardInput.Close();$null=$source.WaitForExit(5000);$source.Dispose()}
    if(!$server.HasExited){
        if($Mode -eq 'Wpf'){$null=[SonicRouteProcessMetrics]::SampleWpf($server.Id,$instance,$Edition,101)}
        else{$null=Native-Request exit}
        if(!$server.WaitForExit(12000)){throw 'Measurement host did not exit.'}
    }
    $server.Dispose()
}

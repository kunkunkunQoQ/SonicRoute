#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidateSet('Wpf','Native')][string]$Mode,
    [Parameter(Mandatory)][ValidateSet('Full','Lite')][string]$Edition,
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$WpfHook,
    [string]$ProfilePath = (Join-Path $PSScriptRoot 'IdleProfile.json'),
    [ValidateSet('Interactive','Private')][string]$Desktop = 'Interactive',
    [ValidateRange(2,60)][int]$Seconds=8
)
$ErrorActionPreference='Stop'
if([IntPtr]::Size -ne 8){throw 'Run this measurement from a 64-bit PowerShell.'}
$source=(Get-Item -LiteralPath $Executable).FullName
$root=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $root){throw 'Select a new output directory for each run.'}
if($Mode -eq 'Wpf'){
    $approvedHash=if($Edition -eq 'Full'){'C4289848808DB9B8D15367690181FC2CBE9A87A482F310394F0C4C034B750F1B'}else{'8F53515A3F48E683ECC1C66EE3918ABB03DD1D1291E5F4F56B3324E397F7CE6D'}
    if((Get-FileHash -LiteralPath $source).Hash -ne $approvedHash){throw 'This test only supports the frozen 1.36 x64 WPF release.'}
    $WpfHook=(Get-Item -LiteralPath $WpfHook).FullName
}
New-Item -ItemType Directory $root|Out-Null
$profile='Perf'+[guid]::NewGuid().ToString('N')
$instance='PerfWpf_'+[guid]::NewGuid().ToString('N')
if($Mode -eq 'Native'){
    $fixture=Join-Path $root "SonicRoute.NativeCandidate/$profile/$Edition"
}else{
    $fixture=Join-Path $root $instance
}
$data=Join-Path $fixture 'data'
$install=Join-Path $fixture 'install'
New-Item -ItemType Directory $data,$install|Out-Null
$exe=Join-Path $install 'SonicRoute.exe'
Copy-Item -LiteralPath $source -Destination $exe
Copy-Item -LiteralPath $ProfilePath -Destination (Join-Path $data 'config.json')
$envVars=@{SONICROUTE_CANDIDATE_ROOT=$root;SONICROUTE_CANDIDATE_ID=$profile}
if($Mode -eq 'Wpf'){
    $envVars['SONICROUTE_DATA_DIRECTORY']=$data
    $envVars['SONICROUTE_INSTANCE_ID']=$instance
    $envVars['SONICROUTE_MEASURE_EDITION']=$Edition
    $envVars['DOTNET_STARTUP_HOOKS']=$WpfHook
    $envVars['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=Join-Path $fixture 'bundle'
    ''|Set-Content -LiteralPath (Join-Path $data ($instance+'.fixture'))
}
function Start-Owned([string[]]$CliArgs,[bool]$Redirect){
    $info=[Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $info.WorkingDirectory=$install
    $info.RedirectStandardOutput=$Redirect
    $info.RedirectStandardError=$Redirect
    foreach($key in $envVars.Keys){$info.Environment[$key]=$envVars[$key]}
    foreach($arg in $CliArgs){$info.ArgumentList.Add($arg)}
    [Diagnostics.Process]::Start($info)
}
function Invoke-Control([string]$Verb){
    $client=Start-Owned -CliArgs @('--control',$Verb) -Redirect $true
    try{
        $read=$client.StandardOutput.ReadToEndAsync()
        $errors=$client.StandardError.ReadToEndAsync()
        if(!$client.WaitForExit(7000)){throw "Timed out waiting for the isolated $Verb client."}
        if($client.ExitCode -ne 0){throw "Control $Verb exited $($client.ExitCode): $($errors.Result)"}
        $read.Result
    }finally{$client.Dispose()}
}
if(!('SonicRouteProcessMetrics' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'ProcessCounters.cs')}
function Read-Gc(){
    if($Mode -eq 'Native'){
        $s=Invoke-Control status|ConvertFrom-Json
        if($s.Pid -ne $server.Id -or $s.Edition -ne $Edition -or !$s.NativeAot -or ([IO.Path]::GetFullPath($s.DataDirectory) -ne [IO.Path]::GetFullPath($data))){throw 'Unexpected candidate identity.'}
        return $s|Select-Object Allocated,ManagedHeap,LastGcHeap,Gen0,Gen1,Gen2,Messages,Dispatches,TimerFires,SystemEvents,LastSignal
    }
    $null=[SonicRouteProcessMetrics]::SampleWpf($server.Id,$instance,$Edition,100)
    $gc=[ordered]@{}
    $i=0
    foreach($key in @('Allocated','ManagedHeap','LastGcHeap','Gen0','Gen1','Gen2','Messages')){
        $gc[$key]=[SonicRouteProcessMetrics]::SampleWpf($server.Id,$instance,$Edition,$i++)
    }
    return $gc
}
function Get-Usage(){
    $server.Refresh()
    $m=[SonicRouteProcessMetrics]::ReadMemory($server)
    return [ordered]@{
        Utc=[datetime]::UtcNow.ToString('o'); CpuMs=$server.TotalProcessorTime.TotalMilliseconds
        PrivateBytes=$m.PrivateUsage.ToUInt64();PrivateWorkingSet=$m.PrivateWorkingSet.ToUInt64()
        PrivateCommit=$m.PagefileUsage.ToUInt64();SharedCommit=$m.SharedCommit;WorkingSet=$m.WorkingSet.ToUInt64()
        Threads=$server.Threads.Count;Handles=$server.HandleCount
        Gdi=[SonicRouteProcessMetrics]::GetGuiResources($server.Handle,0);User=[SonicRouteProcessMetrics]::GetGuiResources($server.Handle,1)
    }
}
$server=$null
$counters=$null
$result=$null
try{
    $server=Start-Owned -CliArgs @() -Redirect $false
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    while($deadline.Elapsed.TotalSeconds -lt 20){
        if($server.HasExited){throw "Fixture exited early: $($server.ExitCode)"}
        if($Mode -eq 'Wpf'){
            if(Test-Path (Join-Path $data 'wpf.ready')){break}
        }else{
            try{if((Invoke-Control status|ConvertFrom-Json).Pid -eq $server.Id){break}}catch{}
        }
        Start-Sleep -Milliseconds 120
    }
    if($deadline.Elapsed.TotalSeconds -ge 20){throw 'Isolated fixture startup timed out.'}
    $null=Read-Gc
    $automation=if($Mode -eq 'Native'){try{Invoke-Control automation-status}catch{$null}}
    $counters=[SonicRouteThreadCounters]::new()
    $null=$counters.Sample($server.Id)
    Start-Sleep -Seconds 4
    $calibration=Read-Gc
    $managedClock=[Diagnostics.Stopwatch]::StartNew()
    $startGc=Read-Gc
    $managedStart=$managedClock.Elapsed.TotalSeconds
    $startSwitches=$counters.Sample($server.Id)
    $start=Get-Usage
    $clock=[Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $Seconds
    $finish=Get-Usage
    $wall=$clock.Elapsed.TotalSeconds
    $endSwitches=$counters.Sample($server.Id)
    $endGc=Read-Gc
    $managedDuration=$managedClock.Elapsed.TotalSeconds-$managedStart
    $count=0L
    $common=0
    foreach($id in $endSwitches.Keys){if($startSwitches.ContainsKey($id)){$count+=($endSwitches[$id]-$startSwitches[$id]);$common++}}
    $modules=@($server.Modules|ForEach-Object {$_.ModuleName}|Sort-Object -Unique)
    $gpuError=$null
    try{$gpu=@(Get-CimInstance Win32_PerfRawData_GPUPerformanceCounters_GPUProcessMemory -Filter "Name LIKE 'pid_$($server.Id)_%'" -OperationTimeoutSec 5|Select-Object Name,DedicatedUsage,SharedUsage)}catch{$gpu=@();$gpuError=$_.Exception.Message}
    $result=[ordered]@{
        Mode=$Mode;Edition=$Edition;Pid=$server.Id;File=$exe;FileSize=(Get-Item $exe).Length;SHA256=(Get-FileHash $exe).Hash
        Windows=[Environment]::OSVersion.VersionString;Desktop=$Desktop;InputConfigSHA256=(Get-FileHash -LiteralPath $ProfilePath).Hash;BackgroundStatus=$automation
        Seconds=$wall;CpuMs=$finish.CpuMs-$start.CpuMs;CpuPercentOfOneCore=($finish.CpuMs-$start.CpuMs)/($wall*10)
        ContextSwitchesOnCommonThreads=$count;CommonThreads=$common;ThreadArrivals=$endSwitches.Count-$common;ThreadDepartures=$startSwitches.Count-$common
        ContextSwitchesByThread=@(foreach($threadId in $endSwitches.Keys){if($startSwitches.ContainsKey($threadId)){
            [ordered]@{Tid=$threadId;Delta=$endSwitches[$threadId]-$startSwitches[$threadId]}
        }})
        Start=$start;End=$finish;StartManaged=$startGc;EndManaged=$endGc;ManagedDurationSeconds=$managedDuration;BackToBackSampleBytes=$startGc.Allocated-$calibration.Allocated
        AllocatedBytesPerSecond=($endGc.Allocated-$startGc.Allocated)/$managedDuration
        Modules=$modules;Gpu=$gpu;GpuQueryError=$gpuError
    }
}finally{
    if($counters){$counters.Dispose()}
    if($server){
        try{
            if(!$server.HasExited){
                try{
                    if($Mode -eq 'Wpf'){$null=[SonicRouteProcessMetrics]::SampleWpf($server.Id,$instance,$Edition,101)}else{$null=Invoke-Control exit}
                }finally{
                    if(!$server.WaitForExit(5000)){
                        # Keep the Process object from the launch and verify that its image is our own copied file.
                        if($server.Path -ne $exe){throw 'Failed to confirm the launched measurement process.'}
                        $server.Kill();$server.WaitForExit()
                        if($result){$result['Cleanup']='forced exit of fixture after timeout'}
                    }
                }
            }
            if($result){$result['ExitCode']=$server.ExitCode}
        }finally{$server.Dispose()}
    }
}
if($result){$result|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $root 'metrics.json') -Encoding utf8}

#requires -Version 7.4
param([Parameter(Mandatory)][int]$Transport, [Parameter(Mandatory)][string]$DeviceCode,
    [Parameter(Mandatory)][string]$Report, [ValidateRange(5,60)][int]$Seconds = 12,
    [string]$Adb = (Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe'))
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Report) { throw 'Choose a new report path.' }
if ((& $Adb -t $Transport shell getprop ro.product.device).Trim() -ne $DeviceCode) { throw 'Unexpected device.' }
if ((& $Adb -t $Transport shell dumpsys activity services org.sonicroute.app) -match 'ServiceRecord\{' -notmatch 'org\.sonicroute\.platform\.MediaAccessService') {
    throw 'The product has an active service.'
}
$ticksPerSecond = [int]((& $Adb -t $Transport shell getconf CLK_TCK) -join '')
if ($ticksPerSecond -le 0) { throw 'Kernel clock tick resolution unavailable.' }
function Read-ProcessTime([int]$AppPid) {
    $stat = (& $Adb -t $Transport shell run-as org.sonicroute.app cat "/proc/$AppPid/stat") -join ''
    if ($LASTEXITCODE -ne 0 -or $stat -notmatch '^\d+ \(.*\) (.*)$') { throw 'App process statistics unavailable.' }
    $fields = $Matches[1] -split '\s+'
    return [long]$fields[11] + [long]$fields[12]
}
function Measure-Scene([int]$AppPid) {
    $start = Read-ProcessTime $AppPid
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $Seconds
    $end = Read-ProcessTime $AppPid
    $timer.Stop()
    $memory = (& $Adb -t $Transport shell dumpsys meminfo $AppPid) -join "`n"
    if ($memory -notmatch 'TOTAL PSS:\s*([\d,]+)\s+TOTAL RSS:\s*([\d,]+)') { throw 'App PSS/RSS unavailable.' }
    [pscustomobject]@{ Seconds = $timer.Elapsed.TotalSeconds; CpuTicks = $end-$start;
        CpuPercentOfOneCore = 100 * ($end-$start) / $ticksPerSecond / $timer.Elapsed.TotalSeconds;
        PssKiB = [int]$Matches[1].Replace(',',''); RssKiB = [int]$Matches[2].Replace(',','') }
}
$launch = (& $Adb -t $Transport shell am start -W --windowingMode 1 -n org.sonicroute.app/.MainActivity) -join "`n"
if ($launch -notmatch '(?m)^LaunchState:\s*(\w+)') { throw 'Launch state unavailable.' }
$launchState = $Matches[1]
if ($launch -notmatch '(?m)^TotalTime:\s*(\d+)') { throw 'AM launch time unavailable.' }
$launchMs = [int]$Matches[1]
$appPid = [int]((& $Adb -t $Transport shell pidof org.sonicroute.app) -join '')
$foreground = Measure-Scene $appPid
$focus = (& $Adb -t $Transport shell dumpsys window | Select-String 'mCurrentFocus=') -join ''
if ($focus -notmatch 'org\.sonicroute\.app/') { throw 'The foreground changed before the background measurement.' }
& $Adb -t $Transport shell input keyevent KEYCODE_HOME
$background = Measure-Scene $appPid
$record = [ordered]@{ Device = $DeviceCode; Api = [int]((& $Adb -t $Transport shell getprop ro.build.version.sdk) -join '');
    Utc = [DateTime]::UtcNow.ToString('o'); Pid = $appPid; TicksPerSecond = $ticksPerSecond;
    LaunchState = $launchState; LaunchMs = $launchMs; Foreground = $foreground; Background = $background }
$record | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $Report -Encoding utf8
Get-Content -LiteralPath $Report

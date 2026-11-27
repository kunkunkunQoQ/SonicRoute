#requires -Version 7.4
param([Parameter(Mandatory)][string]$Adb,[Parameter(Mandatory)][int]$Transport,[switch]$WithUidEstimate)
$ErrorActionPreference='Stop'
$battery=(& $Adb -t $Transport shell dumpsys battery) -join "`n"
$powerState=(& $Adb -t $Transport shell dumpsys power | Select-String 'mWakefulness=') -join ' '
if ($battery -notmatch 'AC powered:' -or $powerState -notmatch 'mWakefulness=(\w+)') {throw 'Phone power data unavailable.'}
$wake=$Matches[1]
$level=if ($battery -match '(?m)^\s*level:\s*(\d+)') {[int]$Matches[1]}
$voltage=if ($battery -match '(?m)^\s*voltage:\s*(\d+)') {[int]$Matches[1]}
$temperature=if ($battery -match '(?m)^\s*temperature:\s*(-?\d+)') {[int]$Matches[1]/10}
$charge=if ($battery -match '(?im)^\s*charge counter:\s*(-?\d+)') {[long]$Matches[1]}
$estimate=$null
if ($WithUidEstimate) {
    $identity=((& $Adb -t $Transport shell pm list packages -U org.sonicroute.app) -match '^package:org\.sonicroute\.app uid:\d+$') -join ''
    if ($identity -notmatch 'uid:(\d+)') {throw 'Unable to isolate the test UID.'}
    $appUid=[int]$Matches[1]
    $usage=((& $Adb -t $Transport shell dumpsys batterystats --checkin org.sonicroute.app) -match "^\d+,$appUid,l,pwi,uid,") -join ''
    if ($usage -match '^\d+,\d+,l,pwi,uid,([\d.]+)') {$estimate=$Matches[1]}
}
$procId=((& $Adb -t $Transport shell pidof org.sonicroute.app) -join '').Trim()
$cpuTicks=$null
if ($procId -match '^\d+$') {
    $stat=(& $Adb -t $Transport shell run-as org.sonicroute.app cat "/proc/$procId/stat" 2>$null) -join ''
    if ($stat -match '^\d+ \(.*\) (.*)$') {$fields=$Matches[1] -split '\s+';$cpuTicks=[long]$fields[11]+[long]$fields[12]}
}
[pscustomobject]@{Utc=[DateTime]::UtcNow.ToString('o');Wakefulness=$wake;BatteryPercent=$level;BatteryVoltageMillivolts=$voltage;
    TemperatureC=$temperature;RawChargeCounter=$charge;Plugged=[bool]($battery -match '(?im)^\s*(?:AC|USB|Wireless|Dock) powered:\s*true');
    AppEstimatedMahSinceCharge=$estimate;AppPid=$procId;AppCpuTicks=$cpuTicks}

#requires -Version 7.4
param([Parameter(Mandatory)][int]$Transport,[Parameter(Mandatory)][string]$DeviceCode,[Parameter(Mandatory)][string]$Report,
    [ValidateRange(60,900)][int]$Seconds=180,[string]$Adb=(Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe'))
$ErrorActionPreference='Stop'
if ((Test-Path -LiteralPath $Report) -or (& $Adb -t $Transport shell getprop ro.product.device).Trim() -ne $DeviceCode) {throw 'Unexpected report or device.'}
if ((& $Adb -t $Transport shell dumpsys activity services org.sonicroute.app) -match 'ServiceRecord\{.*SonicRouteConnectionService') {throw 'This baseline requires the PC receiver to be off.'}
$sample=Join-Path $PSScriptRoot 'Get-AndroidPowerSample.ps1'
$rows=[Collections.Generic.List[object]]::new()
$clock=[Diagnostics.Stopwatch]::StartNew()
for ($at=0; $at -le $Seconds; $at+=30) {
    $delay=$at*1000-$clock.ElapsedMilliseconds
    if ($delay -gt 0) {Start-Sleep -Milliseconds $delay}
    $row=& $sample -Adb $Adb -Transport $Transport -WithUidEstimate:($at -eq 0 -or $at+30 -gt $Seconds)
    if ($at -eq 0 -and ($row.Plugged -or $row.Wakefulness -eq 'Awake')) {throw 'Expected an unplugged, screen-off phone.'}
    $rows.Add($row)
    $rows.ToArray() | ConvertTo-Json | Set-Content -LiteralPath $Report
}
"Read $($rows.Count) phone power samples in $([math]::Round($clock.Elapsed.TotalSeconds,1)) s."

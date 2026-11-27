#requires -Version 7.4
param([Parameter(Mandatory)][int]$Transport, [Parameter(Mandatory)][string]$DeviceCode,
    [Parameter(Mandatory)][string]$Report,
    [string]$Adb=(Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe'))
$ErrorActionPreference='Stop'
if ((Test-Path -LiteralPath $Report) -or (& $Adb -t $Transport shell getprop ro.product.device).Trim() -ne $DeviceCode) {throw 'Unexpected destination or device.'}
$package='org.sonicroute.app'
$null=& $Adb -t $Transport shell am start -W --windowingMode 1 -n "$package/.MainActivity"
function Read-Navigation {
    $tree=(& $Adb -t $Transport exec-out uiautomator dump /dev/tty 2>$null) -join ''
    if ($tree -notmatch '(?s)(<\?xml.*?</hierarchy>)') {throw 'Navigation not available.'}
    return [xml]$Matches[1]
}
$pages=@('nav_pc','nav_rules','nav_settings','nav_audio')
$tree=Read-Navigation
$coords=@{}
foreach($page in $pages) {
    $node=$tree.SelectSingleNode("//node[@resource-id='${package}:id/$page' and @enabled='true']")
    if (!$node -or $node.bounds -notmatch '^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$') {throw "Missing navigation control $page"}
    $coords[$page]=@([int](([int]$Matches[1]+[int]$Matches[3])/2),[int](([int]$Matches[2]+[int]$Matches[4])/2))
}
$null=& $Adb -t $Transport shell dumpsys gfxinfo $package reset
foreach($round in 1..3) {
    foreach($page in $pages) {
        & $Adb -t $Transport shell input tap $coords[$page][0] $coords[$page][1]
        if ($LASTEXITCODE -ne 0) {throw 'Input failed.'}
        Start-Sleep -Milliseconds 340
    }
}
$raw=(& $Adb -t $Transport shell dumpsys gfxinfo $package framestats) -join "`n"
$tree=Read-Navigation
if (!$tree.SelectSingleNode("//node[@resource-id='${package}:id/nav_audio' and (@selected='true' or @checked='true')]")) {throw 'The final tap did not reach the audio page.'}
$summary=[regex]::Match($raw,'(?m)^Total frames rendered:\s*(\d+)')
$jank=[regex]::Match($raw,'(?m)^Janky frames:\s*(\d+)\s*\(([\d.]+)%\)')
if (!$summary.Success -or !$jank.Success -or [int]$summary.Groups[1].Value -lt 12) {throw 'Frame statistics are incomplete.'}
$percentiles=[ordered]@{}
foreach ($p in @(50,90,95,99)) {
    $value=[regex]::Match($raw,"(?m)^${p}th percentile:\s*(\d+)ms")
    if ($value.Success) {$percentiles[[string]$p]=[int]$value.Groups[1].Value}
}
$rawFile=[IO.Path]::ChangeExtension($Report,'.framestats.txt')
$raw | Set-Content -LiteralPath $rawFile
[ordered]@{Device=$DeviceCode;Utc=[DateTime]::UtcNow.ToString('o');Rounds=3;Path=$pages;PauseMs=340;
    Rendered=[int]$summary.Groups[1].Value;Janky=[int]$jank.Groups[1].Value;JankyPercent=[double]$jank.Groups[2].Value;
    FramePercentilesMs=$percentiles;Raw=[IO.Path]::GetFileName($rawFile)} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $Report
Get-Content -LiteralPath $Report

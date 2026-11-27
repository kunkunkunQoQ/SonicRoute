#requires -Version 7.4
param([Parameter(Mandatory)][int]$Transport, [Parameter(Mandatory)][string]$DeviceCode,
    [Parameter(Mandatory)][ValidateSet('appearance','saf-restore','media-session','owned-focus','business-ui')][string]$Mode,
    [ValidateSet('record','cold','updated')][string]$Phase,
    [string]$ScreenshotDirectory,
    [Parameter(Mandatory)][string]$Report,
    [string]$Adb = (Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe'))
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Report) { throw 'A fresh results path is required.' }
if (($Mode -eq 'saf-restore') -ne [bool]$Phase) { throw 'Only SAF runs require a phase.' }
if ($ScreenshotDirectory -and ($Mode -notin 'appearance','business-ui' -or (Test-Path -LiteralPath $ScreenshotDirectory))) {
    throw 'Phone screenshots require a supported mode and a new directory.'
}
if ((& $Adb -t $Transport shell getprop ro.product.device).Trim() -ne $DeviceCode) { throw 'Unexpected device.' }
if ((& $Adb -t $Transport shell dumpsys activity services org.sonicroute.app) -match 'ServiceRecord\{.*SonicRouteConnectionService') {
    throw 'An active SonicRoute connection must finish first.'
}
$info = [Diagnostics.ProcessStartInfo]::new($Adb)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$arguments = @('-t',[string]$Transport,'shell','am','instrument','-w','-r','-e','mode',$Mode)
if ($Phase) { $arguments += @('-e','phase',$Phase) }
if ($ScreenshotDirectory) {
    $evidence = [guid]::NewGuid().ToString('N')
    $null = New-Item -ItemType Directory -Path $ScreenshotDirectory
    $arguments += @('-e','evidence',$evidence)
}
$arguments += 'org.sonicroute.app.test/org.sonicroute.app.test.LanInteropRunner'
foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
$child = [Diagnostics.Process]::Start($info)
$log = [Collections.Generic.List[string]]::new()
$images = [Collections.Generic.HashSet[string]]::new()
try {
    while ($null -ne ($line = $child.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(55)).GetAwaiter().GetResult())) {
        $log.Add($line)
        if ($line -eq 'INSTRUMENTATION_STATUS: launch=ready') {
            $null = & $Adb -t $Transport shell am start -W --windowingMode 1 -n org.sonicroute.app/.MainActivity
            if ($LASTEXITCODE -ne 0) { throw 'The product activity did not launch.' }
        }
        if ($ScreenshotDirectory -and $line -match '^INSTRUMENTATION_STATUS: image=((?:(?:dark|light-en|landscape-en)-nav_(?:audio|devices|pc|rules|settings)|route-(?:dialog|search-mt|search-empty|output-options|permission-entry)|rule-(?:editor|saved|menu))\.png)$') {
            $name = $Matches[1]
            if (!$images.Add($name)) { throw 'Duplicate screenshot.' }
            $image = Join-Path $ScreenshotDirectory $name
            & $Adb -t $Transport exec-out run-as org.sonicroute.app cat "cache/ui-$evidence/$name" > $image
            if ($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath $image).Length -lt 2048) { throw 'The phone screenshot is incomplete.' }
            $header = [IO.File]::ReadAllBytes($image)
            if ([Convert]::ToHexString($header,0,8) -ne '89504E470D0A1A0A') { throw 'Expected real PNG pixels from the phone.' }
            & $Adb -t $Transport shell run-as org.sonicroute.app rm "cache/ui-$evidence/$name"
        }
    }
    if (!$child.WaitForExit(5000) -or $child.ExitCode -ne 0 -or $log -notcontains 'INSTRUMENTATION_CODE: -1') {
        throw "Phone UI check failed ($Mode $Phase)."
    }
    if ($ScreenshotDirectory) {
        $expected=if ($Mode -eq 'appearance') {15} elseif ($images.Contains('route-permission-entry.png')) {8} else {7}
        if ($images.Count -ne $expected) { throw "Expected $expected phone screenshots, received $($images.Count)." }
        & $Adb -t $Transport shell run-as org.sonicroute.app rmdir "cache/ui-$evidence"
    }
} finally {
    $log | Set-Content -LiteralPath $Report -Encoding utf8
    if (!$child.HasExited) { $child.Kill() }
    $child.Dispose()
}
$log | Select-String 'INSTRUMENTATION_RESULT|INSTRUMENTATION_CODE'

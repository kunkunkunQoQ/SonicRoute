#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$Checks,
    [Parameter(Mandatory)][string]$FullProduct,
    [Parameter(Mandatory)][string]$Results,
    [Parameter(Mandatory)][int]$Transport,
    [Parameter(Mandatory)][string]$DeviceCode,
    [ValidateSet('current','speaker','bluetooth')][string]$Output = 'current',
    [switch]$CheckVolume,
    [switch]$CheckServiceRestart,
    [switch]$CheckWifiReturn,
    [ValidateRange(0,180)][int]$ConnectedWindowSeconds = 0,
    [switch]$PrivateUiHandoff,
    [string]$ClipboardChecks,
    [string]$MediaChecks,
    [switch]$TemporaryMediaConsent,
    [string]$AliasChecks,
    [string]$FilesChecks,
    [switch]$ConfirmFixtureFile,
    [string]$ScreenChecks,
    [string]$CaptureChecks,
    [ValidateSet('bin.mt.plus','cn.wenyu.bodian')][string]$CapturePackage,
    [long]$CaptureMediaId,
    [string]$SoakChecks,
    [ValidateRange(10,300)][int]$SoakSeconds = 180,
    [switch]$LockDuringSoak,
    [string]$Adb = (Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe')
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Results)
if (Test-Path -LiteralPath $root) { throw 'Choose a fresh result directory.' }
if ((& $Adb -t $Transport shell getprop ro.product.device).Trim() -ne $DeviceCode) { throw 'Unexpected test device.' }
if ((& $Adb -t $Transport shell dumpsys activity services org.sonicroute.app) -match 'ServiceRecord\{' -notmatch 'org\.sonicroute\.platform\.MediaAccessService') {
    throw 'The phone already has an active SonicRoute service.'
}
$run = [guid]::NewGuid().ToString('N')
if ($CheckWifiReturn -and ($CheckServiceRestart -or $CheckVolume -or $ClipboardChecks -or $FilesChecks -or $AliasChecks -or $ScreenChecks -or $CaptureChecks -or $MediaChecks -or $SoakChecks)) {
    throw 'The physical Wi-Fi return test owns one dedicated run.'
}
if ($PrivateUiHandoff -and (!$ConnectedWindowSeconds -or $CheckWifiReturn -or $CheckServiceRestart)) {
    throw 'The private-window run needs a bounded capture interval.'
}
if ($TemporaryMediaConsent -and !$MediaChecks) {throw 'Temporary media sharing requires the controlled WAV check.'}
if ($CapturePackage) {
    if (!$CaptureChecks -or $CaptureMediaId -le 0) { throw 'A capture checker and test recording are required.' }
    $captureUri = "content://media/external/audio/media/$CaptureMediaId"
    $source = & $Adb -t $Transport shell content query --uri $captureUri --projection _display_name
    if ($source -notmatch '_display_name=SonicRoute-Interop-[A-F0-9]+\.wav$') { throw 'The capture source must be a generated test recording.' }
    $component = if ($CapturePackage -eq 'bin.mt.plus') { 'bin.mt.plus/.MusicPlayerActivity' } else { 'cn.wenyu.bodian/.EntryActivity' }
}
$profile = Join-Path $root "SonicRoute.NativeCandidate/$run/Full/data"
$null = New-Item -ItemType Directory -Path $profile
$candidateCopy = Join-Path $root 'product/SonicRoute.exe'
$null = New-Item -ItemType Directory -Path (Split-Path $candidateCopy)
Copy-Item -LiteralPath ([IO.Path]::GetFullPath($FullProduct)) -Destination $candidateCopy
$FullProduct = $candidateCopy
$baselineConfig = Join-Path $PSScriptRoot '../../当前项目/Native/Measurements/IdleProfile.json'
Copy-Item -LiteralPath $baselineConfig -Destination (Join-Path $profile 'config.json')
function Start-Child([string]$Command, [string[]]$Arguments, [switch]$IsCandidate) {
    $info = [Diagnostics.ProcessStartInfo]::new($Command)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $info.ArgumentList.Add($arg) }
    if ($IsCandidate) {
        $info.Environment['SONICROUTE_CANDIDATE_ROOT'] = $root
        $info.Environment['SONICROUTE_CANDIDATE_ID'] = $run
        $info.Environment['SONICROUTE_DISABLE_DEFAULT_HOTKEYS'] = '1'
    }
    return [Diagnostics.Process]::Start($info)
}
function Read-Line([Diagnostics.Process]$Child, [int]$Seconds) {
    return $Child.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds($Seconds)).GetAwaiter().GetResult()
}
$phoneCursor = @{ Next = 0 }
function Read-PhoneLine([int]$Seconds) {
    if (!$CheckWifiReturn) { return Read-Line $instrument $Seconds }
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $lines = @(& $Adb -t $Transport exec-out run-as org.sonicroute.app cat "cache/interop-$run.events" 2>$null | Where-Object { $_ -match '^INSTRUMENTATION_STATUS: ' })
        if ($lines.Count -gt $phoneCursor.Next) { $item = $lines[$phoneCursor.Next]; $phoneCursor.Next++; return $item }
        Start-Sleep -Milliseconds 180
    } while ([DateTime]::UtcNow -lt $until)
    throw 'The detached phone fixture did not reach its next pre-roam checkpoint.'
}
function Control([string]$Verb) {
    $tool = Start-Child $FullProduct @('--control', $Verb) -IsCandidate
    try {
        $read = $tool.StandardOutput.ReadToEndAsync()
        if (!$tool.WaitForExit(8000) -or $tool.ExitCode -ne 0) { throw "Isolated control failed ($Verb)." }
        return $read.Result
    } finally { $tool.Dispose() }
}
function Record([string]$Value) {
    if ($Value -match '(?i)sonicroute://|(?:^|[?&])s=|INSTRUMENTATION_STATUS: wrapped=') { throw 'Unexpected sensitive result.' }
    $Value | Add-Content -LiteralPath (Join-Path $root 'interop.log') -Encoding utf8
    Write-Output $Value
}
function Measure-LockState([switch]$WithUidEstimate) {
    & (Join-Path $PSScriptRoot 'Get-AndroidPowerSample.ps1') -Adb $Adb -Transport $Transport -WithUidEstimate:$WithUidEstimate
}
if ($LockDuringSoak -and !$SoakChecks) { throw 'A live soak check is required for the screen-off measurement.' }
$rsa = [Security.Cryptography.RSA]::Create(3072)
$hostTest = $instrument = $server = $null
try {
    Record "FULL_SHA256 $((Get-FileHash -LiteralPath $FullProduct).Hash)"
    $hostTest = Start-Child (Get-Command dotnet).Source @([IO.Path]::GetFullPath($Checks),'--srap-live-pcm',$profile,$Output)
    $errors = $hostTest.StandardError.ReadToEndAsync()
    $pin = $null
    do {
        $line = Read-Line $hostTest 20
        if ($null -eq $line) { throw 'Windows probe exited before invitation.' }
        if ($line -match '^PUBLIC_PIN ([0-9A-F]{64})$') { $pin = $Matches[1] }
    } until ($line -eq 'INVITE')
    if (!$pin) { throw 'Windows public pin unavailable.' }
    $wrap = [Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo()).TrimEnd('=').Replace('+','-').Replace('/','_')
    $aliasFlag = if ($AliasChecks) { 'yes' } else { 'no' }
    $clipboardFlag = if ($ClipboardChecks) { 'yes' } else { 'no' }
    $confirmFileFlag = if ($FilesChecks -and $ConfirmFixtureFile) { 'yes' } else { 'no' }
    $suiteSeconds = [Math]::Min(660, 360 + $ConnectedWindowSeconds + $(if ($SoakChecks) { $SoakSeconds } else { 0 }))
    $captureFlag = if ($CapturePackage) { $CapturePackage } else { 'none' }
    $restartFlag = if ($CheckServiceRestart) { 'yes' } else { 'no' }
    $mediaFlag = if ($MediaChecks) { 'yes' } else { 'no' }
    $consentFlag = if ($TemporaryMediaConsent) { 'yes' } else { 'no' }
    $phoneArgs = @('-t', [string]$Transport,'shell','am','instrument')
    $wifiFlag = if ($CheckWifiReturn) { 'yes' } else { $phoneArgs += @('-w','-r'); 'no' }
    $phoneArgs += @('-e','wifiReturn',$wifiFlag,'-e','alias',$aliasFlag,'-e','clipboard',$clipboardFlag,'-e','confirmFixtureFile',$confirmFileFlag,'-e','captureSource',$captureFlag,'-e','restart',$restartFlag,'-e','media',$mediaFlag,'-e','temporaryMediaConsent',$consentFlag,'-e','seconds',[string]$suiteSeconds,'-e','pin',$pin,
        '-e','run',$run,'-e','wrap',$wrap,'org.sonicroute.app.test/org.sonicroute.app.test.LanInteropRunner')
    $instrument = Start-Child $Adb $phoneArgs
    $instrumentErrors = $instrument.StandardError.ReadToEndAsync()
    do {
        $line = Read-PhoneLine 60
        if ($null -eq $line -or $line -match 'interop=failed|INSTRUMENTATION_FAILED') { throw "Android test did not issue an invitation: $line" }
        if ($line -eq 'INSTRUMENTATION_STATUS: launch=ready') {
            $null = & $Adb -t $Transport shell am start -W --windowingMode 1 -n org.sonicroute.app/.MainActivity
            if ($LASTEXITCODE -ne 0) { throw 'Android product activity failed to launch.' }
        }
    } until ($line -match '^INSTRUMENTATION_STATUS: wrapped=([a-zA-Z0-9+/=]+)$')
    $plaintext = $rsa.Decrypt([Convert]::FromBase64String($Matches[1]), [Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
    try { $hostTest.StandardInput.WriteLine([Text.Encoding]::UTF8.GetString($plaintext)); $hostTest.StandardInput.Flush() }
    finally { [Array]::Clear($plaintext) }
    while ($null -ne ($line = Read-Line $hostTest 80)) { Record $line }
    if (!$hostTest.WaitForExit(6000) -or $hostTest.ExitCode -ne 0) { Record $errors.Result; throw 'Windows PCM interoperability failed.' }
    do {
        $line = Read-PhoneLine 25
        if ($null -eq $line) { throw 'Android session ended before clean disconnect.' }
    } until ($line -eq 'INSTRUMENTATION_STATUS: peer=disconnected')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $server = Start-Child $FullProduct @() -IsCandidate
    do {
        $line = Read-PhoneLine 50
        if ($null -eq $line) { throw 'Native Full did not reconnect.' }
    } until ($line -eq 'INSTRUMENTATION_STATUS: peer=connected')
    $elapsed = $watch.ElapsedMilliseconds
    $status = (Control 'status') | ConvertFrom-Json
    if ($status.Pid -ne $server.Id -or !$status.NativeAot -or $status.Edition -ne 'Full' -or
        [IO.Path]::GetFullPath($status.DataDirectory) -ne [IO.Path]::GetFullPath($profile)) { throw 'Unexpected product instance.' }
    Record "Native Full PID=$($server.Id) restored the paired mTLS connection in ${elapsed}ms."
    if ($ConnectedWindowSeconds) {
        if ($PrivateUiHandoff) {
            $null = Control 'exit'
            if (!$server.WaitForExit(10000) -or $server.ExitCode -ne 0) {throw 'The isolated desktop candidate did not release its profile.'}
            do {
                $notice = Read-PhoneLine 20
                if ($null -eq $notice) {throw 'Android did not observe the first desktop session close.'}
            } until ($notice -eq 'INSTRUMENTATION_STATUS: peer=disconnected')
            Record 'PRIVATE_UI_READY Original Full exited cleanly; its isolated profile can be opened on the private desktop.'
        }
        $readyInfo = [ordered]@{Pid=$server.Id; CandidateId=$run; CandidateRoot=$root; Executable=$FullProduct; TrustedData=$profile; PhoneTransport=$Transport; PrivateDesktop=[bool]$PrivateUiHandoff}
        $readyInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'ui-window.json')
        if (!$PrivateUiHandoff) {Record 'CONNECTED_UI_READY Android and Native Full share a real trusted session.'}
        $finishAt = [DateTime]::UtcNow.AddSeconds($ConnectedWindowSeconds)
        if ($PrivateUiHandoff) {
            do {
                $remaining = [int][Math]::Ceiling(($finishAt-[DateTime]::UtcNow).TotalSeconds)
                if ($remaining -le 0) {throw 'No authenticated private desktop session.'}
                $notice = Read-PhoneLine $remaining
                if ($null -eq $notice) {throw 'Android ended before the private desktop connected.'}
            } until ($notice -eq 'INSTRUMENTATION_STATUS: peer=connected')
            [DateTime]::UtcNow.ToString('o') | Set-Content -LiteralPath (Join-Path $root 'ui-peer-live.txt')
            Record 'PASS Android independently observed the same trusted PC reconnect on the private desktop.'
        }
        while (!(Test-Path -LiteralPath (Join-Path $root 'ui-window.done')) -and [DateTime]::UtcNow -lt $finishAt) {
            if (!$PrivateUiHandoff -and $server.HasExited) { throw 'The authenticated product instance left before UI inspection.' }
            Start-Sleep -Milliseconds 250
        }
        if ($PrivateUiHandoff -and !(Test-Path -LiteralPath (Join-Path $root 'ui-window.done'))) { throw 'Private desktop capture did not finish in time.' }
    }
    if ($CheckWifiReturn) {
        $phoneFile = Join-Path $profile 'android-phone.json'
        $initial = @((Get-Content -LiteralPath $phoneFile -Raw | ConvertFrom-Json).Phones | Where-Object AppPublicKeySha256)[0]
        $oldEnd = [Net.IPEndPoint]::Parse($initial.LastAppAddress)
        function Product-OwnsEndpoint([Net.IPEndPoint]$Endpoint) {
            $filter = 'OwningProcess = {0} AND State = 5' -f $server.Id
            return [bool]@(Get-CimInstance -Namespace root/StandardCimv2 -ClassName MSFT_NetTCPConnection -Filter $filter -ErrorAction Stop |
                Where-Object { $_.RemotePort -eq $Endpoint.Port -and ([Net.IPAddress]::Parse($_.RemoteAddress)).MapToIPv6().Equals($Endpoint.Address.MapToIPv6()) }).Count
        }
        if (!(Product-OwnsEndpoint $oldEnd)) { throw 'The original Full process does not own the authenticated socket.' }
        & $Adb -t $Transport shell run-as org.sonicroute.app touch "cache/interop-$run.roam"
        do { $line=Read-PhoneLine 12 } until ($line -eq 'INSTRUMENTATION_STATUS: roam=armed')
        Record 'WIFI_RETURN_READY Same Native Full PID and paired phone authenticated. The phone may now leave and return to Wi-Fi.'
        $lost = $false; $restored = $false; $until = [Datetime]::UtcNow.AddSeconds(180)
        while (!$restored -and [DateTime]::UtcNow -lt $until) {
            if ($server.HasExited) { throw 'The observed Full instance exited.' }
            if (!$lost -and !(Product-OwnsEndpoint $oldEnd)) { $lost = $true; $watch.Restart(); Record 'WIFI_RETURN observed original product socket close' }
            if ($lost) {
                $saved = @((Get-Content -LiteralPath $phoneFile -Raw | ConvertFrom-Json).Phones | Where-Object { $_.Id -eq $initial.Id -and $_.AppPublicKeySha256 -eq $initial.AppPublicKeySha256 })
                if ($saved.Count -ne 1) { throw 'The trusted profile changed during the return test.' }
                $last = [Net.IPEndPoint]::Parse($saved[0].LastAppAddress)
                if (!$last.Equals($oldEnd) -and (Product-OwnsEndpoint $last)) {
                    $restored=$true; Record "PASS same Native Full PID and trusted profile authenticated a new endpoint after Wi-Fi returned ($($watch.ElapsedMilliseconds)ms after observed socket loss); no ADB command during recovery."
                }
            }
            if (!$restored) { Start-Sleep -Milliseconds 400 }
        }
        if (!$restored) { throw 'No confirmed new authenticated endpoint was observed after Wi-Fi departure.' }
        Start-Sleep -Seconds 3
        $null = Control 'exit'
        if (!$server.WaitForExit(10000) -or $server.ExitCode -ne 0) { throw 'Isolated product exit failed.' }
        Record 'The detached phone fixture retires its single test peer after observing its authenticated return.'
        return
    }
    if ($CheckServiceRestart) {
        & $Adb -t $Transport shell run-as org.sonicroute.app touch "cache/interop-$run.restart"
        if ($LASTEXITCODE -ne 0) { throw 'Unable to start the scoped listener restart.' }
        $portChange = $null
        do {
            $line = Read-Line $instrument 100
            if ($null -eq $line -or $line -match 'interop=failed') { throw 'The restarted phone listener did not reconnect.' }
            if ($line -eq 'INSTRUMENTATION_STATUS: restart=stopped') { Record 'PHONE_RESTART stopped'; $watch.Restart() }
            if ($line -match '^INSTRUMENTATION_STATUS: restart=(new_port|same_port)$') { $portChange = $Matches[1]; Record "PHONE_RESTART listening $portChange" }
        } until ($line -eq 'INSTRUMENTATION_STATUS: restart=reconnected')
        if (!$portChange -or $server.HasExited -or ((Control 'status') | ConvertFrom-Json).Pid -ne $server.Id) { throw 'The original isolated Full instance was lost.' }
        Record "PASS the same Native Full instance recovered the pinned phone after its listener reopened ($portChange, $($watch.ElapsedMilliseconds)ms from stopped)."
    }
    if (!$server.HasExited) {$null = Control 'exit'}
    if (!$server.WaitForExit(10000) -or $server.ExitCode -ne 0) { throw 'Native Full did not exit cleanly.' }
    $extra = @()
    $runClipboard = [bool]$ClipboardChecks
    if ($runClipboard) {
        & $Adb -t $Transport shell run-as org.sonicroute.app touch "cache/interop-$run.clip-opt"
        if ($LASTEXITCODE -ne 0) { throw 'Unable to prepare the local clipboard test.' }
        do {
            $line = Read-Line $instrument 18
            if ($null -eq $line) { throw 'The phone did not check the local clipboard.' }
        } until ($line -match '^INSTRUMENTATION_STATUS: clipboard=(prepared|skipped_nonempty)$')
        $runClipboard = $Matches[1] -eq 'prepared'
    }
    if ($CheckVolume) { $extra += ,@($Checks, '--srap-live-volume') }
    if ($runClipboard) { $extra += ,@($ClipboardChecks, '--srap-live-clipboard') }
    if ($MediaChecks) {
        $selection=if ($TemporaryMediaConsent) {'fixture-control'} else {'fixture'}
        $extra += ,@($MediaChecks, '--srap-live-media',$selection)
    }
    if ($ScreenChecks) { $extra += ,@($ScreenChecks, '--srap-live-screen') }
    if ($CaptureChecks) { $extra += ,@($CaptureChecks, '--srap-live-capture') }
    if ($FilesChecks) { $extra += ,@($FilesChecks, '--srap-live-files') }
    if ($AliasChecks) { $extra += ,@($AliasChecks, '--srap-live-alias',[string]$Transport,'keep-adb') }
    if ($SoakChecks) { $extra += ,@($SoakChecks, '--srap-live-soak',[string]$SoakSeconds) }
    foreach ($probe in $extra) {
        $arguments = @([IO.Path]::GetFullPath($probe[0]), $probe[1], $profile)
        if ($probe.Count -gt 2) { $arguments += $probe[2..($probe.Count-1)] }
        $extraProcess = Start-Child (Get-Command dotnet).Source $arguments
        try {
            $extraErrors = $extraProcess.StandardError.ReadToEndAsync()
            while ($null -ne ($line = Read-Line $extraProcess 185)) {
                Record $line
                if ($TemporaryMediaConsent -and $probe[1] -eq '--srap-live-media' -and $line -eq 'MEDIA_FIXTURE_KEY_REQUIRED') {
                    & $Adb -t $Transport shell run-as org.sonicroute.app touch "cache/interop-$run.media-lookup"
                    $until=[DateTime]::UtcNow.AddSeconds(12)
                    do {
                        $mediaKey=((& $Adb -t $Transport exec-out run-as org.sonicroute.app cat "cache/interop-$run.media-key" 2>$null) -join '').Trim()
                        if ($mediaKey -match '^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$') {break}
                        Start-Sleep -Milliseconds 150
                    } while ([DateTime]::UtcNow -lt $until)
                    if ($mediaKey -notmatch '^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$') {throw 'The phone did not confirm the private test WAV identity.'}
                    $extraProcess.StandardInput.WriteLine($mediaKey)
                    $extraProcess.StandardInput.Flush()
                }
                if ($probe[1] -eq '--srap-live-soak' -and $line -match '^SOAK_SAMPLE number=1 ') {
                    & $Adb -t $Transport shell run-as org.sonicroute.app touch "cache/interop-$run.soak-idle"
                    if ($LASTEXITCODE -ne 0) { throw 'Unable to suspend test instrumentation sampling.' }
                    if ($LockDuringSoak) {
                        & $Adb -t $Transport shell input keyevent KEYCODE_SLEEP
                        Start-Sleep -Milliseconds 600
                        $lockStart = Measure-LockState -WithUidEstimate
                        $lockStart | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'screen-off-start.json')
                        Record "LOCK_START wakefulness=$($lockStart.Wakefulness)"
                    }
                }
                if ($LockDuringSoak -and $probe[1] -eq '--srap-live-soak' -and $line -match '^SOAK_SAMPLE number=(\d+) ' -and [int]$Matches[1] -gt 1) {
                    Measure-LockState | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $root 'screen-off-samples.jsonl')
                }
                if ($LockDuringSoak -and $probe[1] -eq '--srap-live-soak' -and $line -match '^SOAK_RESULT ') {
                    $lockEnd = Measure-LockState -WithUidEstimate
                    $lockEnd | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'screen-off-end.json')
                    Record "LOCK_END wakefulness=$($lockEnd.Wakefulness)"
                }
                if ($CapturePackage -and $probe[1] -eq '--srap-live-capture') {
                    if ($line -eq 'CAPTURE_GRANT_REQUIRED') {
                        $null = & $Adb -t $Transport shell am start -W --windowingMode 1 -n org.sonicroute.app/.MainActivity
                        if ($LASTEXITCODE -ne 0) { throw 'The product did not regain foreground for the capture consent.' }
                        & $Adb -t $Transport shell run-as org.sonicroute.app touch "cache/interop-$run.capture"
                        if ($LASTEXITCODE -ne 0) { throw 'The capture fixture could not open its scoped consent dialog.' }
                    } elseif ($line -eq "CAPTURE_PACKAGE $CapturePackage") {
                        $null = & $Adb -t $Transport shell am start -W --grant-read-uri-permission -a android.intent.action.VIEW -d $captureUri -t audio/wav -n $component
                        if ($LASTEXITCODE -ne 0) { throw 'Unable to play the generated recording in the selected app.' }
                    }
                }
                if ($ConfirmFixtureFile -and $probe[1] -eq '--srap-live-files' -and $line -match '^FILES_OFFER name=(SonicRoute-transfer-[a-f0-9]{12}\.bin) bytes=1048593 sha256=[0-9A-F]{64}\.') {
                    $fileName = $Matches[1]
                    & $Adb -t $Transport shell run-as org.sonicroute.app touch "cache/interop-$run.$fileName"
                    if ($LASTEXITCODE -ne 0) { throw 'Unable to identify the generated fixture file to the test APK.' }
                }
                if ($probe[1] -eq '--srap-live-clipboard' -and $line -match '^CLIPBOARD_WATCH_READY ([a-f0-9]{32})$') {
                    $challenge = $Matches[1]
                    & $Adb -t $Transport shell run-as org.sonicroute.app touch "cache/interop-$run.clip-$challenge" "cache/interop-$run.clip-request"
                    if ($LASTEXITCODE -ne 0) { throw 'Unable to notify the foreground clipboard fixture.' }
                }
            }
            if (!$extraProcess.WaitForExit(7000) -or $extraProcess.ExitCode -ne 0) {
                Record $extraErrors.Result
                throw "Verification failed for $($probe[1])."
            }
        } finally {
            if (!$extraProcess.HasExited) { $extraProcess.Kill() }
            $extraProcess.Dispose()
        }
    }
    & $Adb -t $Transport shell run-as org.sonicroute.app touch "cache/interop-$run.done"
    if ($LASTEXITCODE -ne 0) { throw 'Unable to signal test completion.' }
    $result = $instrument.StandardOutput.ReadToEndAsync()
    if (!$instrument.WaitForExit(20000) -or $result.Result -notmatch 'INSTRUMENTATION_RESULT: interop=passed') {
        throw 'Android did not confirm the full peer lifecycle.'
    }
    if ($ClipboardChecks) {
        if ($result.Result -match 'INSTRUMENTATION_RESULT: clipboard=passed') { Record 'Android ClipboardManager confirmed the test text and restored the empty clipboard.' }
        elseif ($result.Result -match 'INSTRUMENTATION_RESULT: clipboard=skipped_nonempty') { Record 'SKIP phone clipboard already contained data. It was preserved.' }
        else { throw 'Phone clipboard verification or restoration was not observed.' }
    }
    if ($TemporaryMediaConsent) {
        if ($result.Result -notmatch 'INSTRUMENTATION_RESULT: remote_media=pause_and_resume_callbacks_confirmed' -or $result.Result -notmatch 'INSTRUMENTATION_RESULT: media_sharing=restored_off') {
            throw 'Remote callbacks and restoration of the temporary phone sharing setting were not both confirmed.'
        }
        Record 'Android verified actual pause/play callbacks, resumed its test WAV, and restored media sharing to off.'
    }
    Record 'Android confirmed pairing, connection, and test cleanup.'
} finally {
    if ($server -and !$server.HasExited) { try { $null = Control 'exit' } catch { Write-Warning 'Isolated product exit failed.' } }
    if ($instrument -and !$instrument.HasExited) {
        $null = & $Adb -t $Transport shell run-as org.sonicroute.app touch "cache/interop-$run.done"
    }
    if ($CheckWifiReturn -and $instrument -and !$restored) {
        $null = & $Adb -t $Transport shell run-as org.sonicroute.app touch "cache/interop-$run.done" 2>$null
    }
    if ($hostTest -and !$hostTest.HasExited) { $hostTest.Kill() }
    foreach ($child in @($hostTest, $server, $instrument)) { if ($child) { $child.Dispose() } }
    $rsa.Dispose()
}

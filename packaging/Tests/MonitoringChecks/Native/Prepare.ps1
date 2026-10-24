param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$text = [IO.File]::ReadAllText((Join-Path $sourceRoot 'SonicRoute.Core/AudioMeterService.cs'))
# Real Windows COM and product control flow. Only namespace and counters change,
# so the test can observe read/scan counts without adding diagnostic hooks to production.
$text = "using SonicRoute.Core;`n" + $text.Replace('namespace SonicRoute.Core', 'namespace MonitoringNativeChecks.Observed')
$text = $text.Replace('new MMDeviceEnumeratorComObject()', 'MonitoringNativeChecks.Counters.CreateEnumerator()')
$text = $text.Replace('int hr = handle.Meter.GetPeakValue', 'System.Threading.Interlocked.Increment(ref MonitoringNativeChecks.Counters.PeakReads); int hr = handle.Meter.GetPeakValue')
$text = $text.Replace('Marshal.ThrowExceptionForHR(handle.Session.GetState', 'System.Threading.Interlocked.Increment(ref MonitoringNativeChecks.Counters.StateReads); Marshal.ThrowExceptionForHR(handle.Session.GetState')
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'ObservedAudioMeter.cs'), $text, [Text.UTF8Encoding]::new($false))

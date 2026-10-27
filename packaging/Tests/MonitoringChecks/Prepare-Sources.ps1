param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$files = @('SonicRoute.Core/AudioMeterService.cs','SonicRoute.Core/MicrophoneStateMonitor.cs',
    'SonicRoute.Core/EndpointVolumeSubscription.cs','SonicRoute.Core/OutputVolumeMonitor.cs',
    'SonicRoute.Core/Compat/MathEx.cs',
    'SonicRoute.Core/Interop/AudioNotificationInterfaces.cs','SonicRoute.Core/Interop/WasapiInterfaces.cs',
    'SonicRoute.Core/Interop/ComInterop.cs','SonicRoute/ProcessChangeSource.cs')
foreach ($file in $files) {
    $text = [IO.File]::ReadAllText((Join-Path $sourceRoot $file))
    # Replace OS construction/release only. Run loops, COM registration/state,
    # notification queues, recovery, cancellation and lifetime stay product code.
    $text = $text.Replace('new MMDeviceEnumeratorComObject()', 'MonitoringChecks.Boundary.CreateEnumerator()')
    $text = $text.Replace('Marshal.ReleaseComObject(', 'MonitoringChecks.Boundary.Release(')
    [IO.File]::WriteAllText((Join-Path $OutputDirectory ([IO.Path]::GetFileName($file))), $text, [Text.UTF8Encoding]::new($false))
}

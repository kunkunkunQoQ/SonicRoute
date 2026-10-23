param([string]$SourceRoot, [string]$OutputDirectory, [string]$Revision = '')
$ErrorActionPreference = 'Stop'
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$encoding = [Text.UTF8Encoding]::new($false)
function Read-Source([string]$relative) {
    if ($Revision) {
        $info = [Diagnostics.ProcessStartInfo]::new('git')
        $info.UseShellExecute = $false
        $info.RedirectStandardOutput = $info.RedirectStandardError = $true
        $info.StandardOutputEncoding = $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
        foreach ($argument in @('-C', $SourceRoot, 'show', "${Revision}:$relative")) { $info.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($info)
        try {
            $content = $process.StandardOutput.ReadToEnd()
            $errorText = $process.StandardError.ReadToEnd()
            $process.WaitForExit()
            if ($process.ExitCode) { throw "Cannot read $relative from ${Revision}: $errorText" }
            return $content.TrimStart([char]0xFEFF)
        }
        finally { $process.Dispose() }
    }
    return [IO.File]::ReadAllText((Join-Path $SourceRoot $relative))
}
$files = @('AudioService.cs', 'SystemVolumeService.cs', 'GlobalMicMuteService.cs', 'SessionVolumeService.cs',
    'Interop/WasapiInterfaces.cs', 'Interop/AudioNotificationInterfaces.cs', 'Interop/ComInterop.cs',
    'Interop/ISimpleAudioVolume.cs', 'Interop/PolicyConfigClient.cs', 'Models/AudioAppInfo.cs',
    'Models/AudioDeviceInfo.cs', 'Compat/MathEx.cs', 'Compat/CompatEnv.cs', 'Compat/Net48CompilerShims.cs')
foreach ($file in $files) {
    $text = Read-Source "SonicRoute.Core/$file"
    # Only OS construction/release boundaries are substituted. Method bodies, locks,
    # cache rules, retries and checked operations are compiled from the product sources.
    $text = $text.Replace('new MMDeviceEnumeratorComObject()', 'AudioPathChecks.Boundary.CreateEnumerator()')
    $text = $text.Replace('Marshal.ReleaseComObject(', 'AudioPathChecks.Boundary.Release(')
    $text = $text.Replace('new PolicyConfigClient()', 'AudioPathChecks.Boundary.CreatePolicyClient()')
    [IO.File]::WriteAllText((Join-Path $OutputDirectory ([IO.Path]::GetFileName($file))), $text, $encoding)
}
$panel = Read-Source 'SonicRoute/QuickPanelModernWindow.xaml.cs'
$anchor = 'var vols = await _uiLifetime.ReadAsync((Apps: apps, ShowOutputName: showOutputName), static state =>'
$at = $panel.IndexOf($anchor, [StringComparison]::Ordinal)
if ($at -lt 0) { throw 'Panel row reader anchor changed; inspect the test extraction.' }
$start = $panel.IndexOf('{', $at)
$normalized = $panel.Replace("`r`n", "`n")
$at = $normalized.IndexOf($anchor, [StringComparison]::Ordinal)
$start = $normalized.IndexOf('{', $at)
$end = $normalized.IndexOf("`n                });", $start, [StringComparison]::Ordinal)
if ($end -lt 0) { throw 'Panel row reader end changed; inspect the test extraction.' }
$body = $normalized.Substring($start + 1, $end - $start - 1)
$reader = @"
using System;
using System.Collections.Generic;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
namespace AudioPathChecks {
internal static class PanelRows {
 public static Dictionary<int, (int vol, bool muted, string? output)> Read(List<AudioAppInfo> apps, bool names) {
  var state = (Apps: apps, ShowOutputName: names);
$body
 }
}
}
"@
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'PanelRows.cs'), $reader, $encoding)

param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$app = [IO.File]::ReadAllText((Join-Path $root 'SonicRoute/App.xaml.cs'))
$start = $app.IndexOf('private async Task ExecuteHotkeyCoreAsync', [StringComparison]::Ordinal)
$end = $app.IndexOf('internal async Task AdjustDefaultOutputVolumeAsync', $start, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -le $start) { throw 'Hotkey method boundaries changed; inspect the fixture.' }
$method = $app.Substring($start, $end - $start)
$cycleStart = $app.IndexOf('private static List<AudioDeviceInfo> BuildVisibleDevices', [StringComparison]::Ordinal)
$cycleEnd = $app.IndexOf('private async void Quit()', $cycleStart, [StringComparison]::Ordinal)
if ($cycleStart -lt 0 -or $cycleEnd -le $cycleStart) { throw 'Device cycle method boundaries changed; inspect the fixture.' }
# The tray and hotkey paths share the actual default-volume handler and readback/OSD path.
$method += $app.Substring($end, $cycleStart - $end)
# Exercise the actual three cycle handlers behind recording wrappers; OS audio writes stay faked.
$cycle = $app.Substring($cycleStart, $cycleEnd - $cycleStart).Replace('CycleDeviceAsync(', 'CycleDeviceUnderTestAsync(').Replace('CycleAllAppsDeviceAsync(', 'CycleAllAppsDeviceUnderTestAsync(').Replace('CycleSystemDefaultDeviceAsync(', 'CycleSystemDefaultDeviceUnderTestAsync(')
$method += $cycle
$source = "using SonicRoute.Core; using SonicRoute.Core.Compat; using SonicRoute.Core.Interop; using SonicRoute.Core.Models; namespace SonicRoute { public sealed partial class App { $method } }"
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'HotkeyDispatch.cs'), $source, [Text.UTF8Encoding]::new($false))
$mouse = [IO.File]::ReadAllText((Join-Path $root 'SonicRoute/MouseHotkeyHook.cs'))
# Keep the real hook callback and lifecycle. Deliver packets through the same
# registered delegate, while OS registration, forwarding and key state stay local.
$boundaries = [ordered]@{
    'SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0)' = 'MouseInputBoundary.Install(_proc.Invoke)'
    'UnhookWindowsHookEx(_hook)' = 'MouseInputBoundary.Uninstall(_hook)'
    'CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam)' = 'MouseInputBoundary.Forward()'
    'Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam)' = 'MouseInputBoundary.ReadPacket<MSLLHOOKSTRUCT>(lParam)'
    '(GetAsyncKeyState(vk) & 0x8000) != 0' = 'MouseInputBoundary.IsDown(vk)'
}
foreach ($entry in $boundaries.GetEnumerator()) {
    if (!$mouse.Contains($entry.Key)) { throw "Mouse input fixture boundary changed: $($entry.Key)" }
    $mouse = $mouse.Replace($entry.Key, $entry.Value)
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'MouseHotkeyHook.cs'), $mouse, [Text.UTF8Encoding]::new($false))
$tray = [IO.File]::ReadAllText((Join-Path $root 'SonicRoute/TrayWheelService.cs'))
function Section([string]$startText, [string]$endText) {
    $from = $tray.IndexOf($startText, [StringComparison]::Ordinal)
    $to = $tray.IndexOf($endText, $from, [StringComparison]::Ordinal)
    if ($from -lt 0 -or $to -le $from) { throw "Tray method boundaries changed: $startText" }
    return $tray.Substring($from, $to - $from)
}
$trayMethods = (Section 'public void Start(MouseHotkeyHook' 'private bool TryGetTrayIconIdentity') + (Section 'private async Task SwitchOutputDeviceAsync' '// Shared OSD controller')
$traySource = "using System.Runtime.InteropServices; using SonicRoute.Core; using SonicRoute.Core.Compat; using SonicRoute.Core.Models; namespace SonicRoute { internal sealed partial class TrayWheelService { $trayMethods } }"
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'TrayWheelDispatch.cs'), $traySource, [Text.UTF8Encoding]::new($false))

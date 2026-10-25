using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute;

// Actual hook dispatch, area policy, pending processing, target cache and audio handlers
// are extracted unchanged by Prepare-Hotkeys.ps1. Native hit testing and audio are fakes.
internal sealed partial class TrayWheelService : IDisposable
{
    private const int WM_MOUSEWHEEL = 0x020A;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT
    { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    private IntPtr _hook = IntPtr.Zero;
    private bool _disposed, _dispatchScheduled;
    private int _pendingDelta, _pendingDeviceDelta;
    private readonly object _appsLock = new();
    private List<AudioAppInfo>? _cachedApps;
    private DateTime _cachedAt;
    internal bool OverIcon = true;
    internal bool Pending => _dispatchScheduled;
    internal int VolumeNotices, DeviceNotices;
    private static IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data) => IntPtr.Zero;
    private bool IsOverOurIcon(POINT point) => OverIcon;
    private static IntPtr FindWindow(string? name, string? text) => new(1);
    private static IntPtr FindWindowEx(IntPtr parent, IntPtr child, string name, string? text) => new(2);
    private static bool GetWindowRect(IntPtr window, out RECT rect) { rect = new() { Left = 0, Top = 0, Right = 100, Bottom = 100 }; return true; }
    private static bool PtInRect(ref RECT rect, POINT point) => point.x >= rect.Left && point.x < rect.Right && point.y >= rect.Top && point.y < rect.Bottom;
    private void ShowOsd(string name, string text) { DeviceNotices++; }
    private void ShowVolumeOsd(string name, int volume, string text, bool muted, string target) { VolumeNotices++; }
    internal void Feed(int delta)
    {
        var data = new MSLLHOOKSTRUCT { pt = new() { x = 50, y = 50 }, mouseData = unchecked((uint)(delta << 16)) };
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<MSLLHOOKSTRUCT>());
        try { Marshal.StructureToPtr(data, pointer, false); HookProc(0, new IntPtr(WM_MOUSEWHEEL), pointer); }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    public void Dispose() { _disposed = true; DiscardPendingWheel(); }
}

internal static class TrayWheelChecks
{
    internal static async Task<int> Run(App app)
    {
        int passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
        async Task Drain(TrayWheelService wheel)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (wheel.Pending) { if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException("tray dispatch"); await Task.Delay(10); }
        }
        var config = ConfigService.Load();
        config.TrayWheelEverywhere = false; config.TrayWheelPaused = false;
        AudioService.EndpointApplied = true;
        AudioService.Apps.Add(new AudioAppInfo { ProcessId = 101234, ProcessName = "HotkeyTarget", DisplayName = "Target" });
        AudioService.Outputs.AddRange(new[] { "A", "B", "C" }.Select(id => new AudioDeviceInfo { Id = id, DisplayName = id, Flow = EDataFlow.eRender }));
        using var wheel = new TrayWheelService();
        try
        {
            foreach (int delta in new[] { 120, -120 })
            {
                AudioService.Endpoints[(101234, EDataFlow.eRender)] = "B";
                AudioService.EndpointWrites.Clear(); SessionVolumeService.AdjustTargets.Clear();
                KeyBoundary.Down.Add(0x12); wheel.Feed(delta); KeyBoundary.Down.Clear();
                await Drain(wheel);
                Check(AudioService.EndpointWrites.Count == 1 && AudioService.EndpointWrites[0].Id == (delta > 0 ? "A" : "C"), "Alt wheel captures direction even after Alt release: " + delta);
                Check(SessionVolumeService.AdjustTargets.Count == 0, "Alt wheel does not adjust volume: " + delta);
            }
            AudioService.Endpoints[(101234, EDataFlow.eRender)] = "A";
            AudioService.EndpointWrites.Clear();
            KeyBoundary.Down.Add(0x12); wheel.Feed(-120); wheel.Feed(-120); KeyBoundary.Down.Clear();
            await Drain(wheel);
            Check(AudioService.EndpointWrites.Count == 1 && AudioService.EndpointWrites[0].Id == "C", "coalesced Alt wheel ticks apply one final route write");
            AudioService.EndpointWrites.Clear(); SessionVolumeService.AdjustTargets.Clear();
            wheel.Feed(120); await Drain(wheel);
            Check(SessionVolumeService.AdjustTargets.Count == 1 && AudioService.EndpointWrites.Count == 0, "ordinary wheel retains volume behavior only");
            AudioService.EndpointWrites.Clear(); SessionVolumeService.AdjustTargets.Clear();
            KeyBoundary.Down.Add(0x12); wheel.Feed(-120); KeyBoundary.Down.Clear(); wheel.Feed(120); await Drain(wheel);
            Check(AudioService.EndpointWrites.Count == 1 && SessionVolumeService.AdjustTargets.Count == 1, "mixed pending input preserves distinct device and volume operations");
            AudioService.EndpointWrites.Clear(); SessionVolumeService.AdjustTargets.Clear();
            wheel.OverIcon = false; KeyBoundary.Down.Add(0x12); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(AudioService.EndpointWrites.Count == 0 && !wheel.Pending, "icon-only area rejects tray area outside own icon");
            config.TrayWheelEverywhere = true;
            KeyBoundary.Down.Add(0x12); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(AudioService.EndpointWrites.Count == 1, "saved whole-tray area also enables Alt wheel within that area");
            AudioService.EndpointWrites.Clear(); SessionVolumeService.AdjustTargets.Clear();
            app.BypassTrayWheel = true;
            KeyBoundary.Down.Add(0x12); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(AudioService.EndpointWrites.Count == 0 && SessionVolumeService.AdjustTargets.Count == 0, "global mouse binding/capture bypass prevents region action");
            app.BypassTrayWheel = false;
            config.TrayWheelPaused = true;
            KeyBoundary.Down.Add(0x12); wheel.Feed(-120); KeyBoundary.Down.Clear(); wheel.Feed(120); await Drain(wheel);
            Check(AudioService.EndpointWrites.Count == 0 && SessionVolumeService.AdjustTargets.Count == 0, "paused region blocks both volume and device wheel");
            config.TrayWheelPaused = false;
            KeyBoundary.Down.Add(0x12); wheel.Feed(-120); KeyBoundary.Down.Clear(); wheel.Feed(120);
            wheel.DiscardPendingWheel(); await Drain(wheel);
            Check(AudioService.EndpointWrites.Count == 0 && SessionVolumeService.AdjustTargets.Count == 0, "discard clears both pending input types");
            wheel.Dispose(); KeyBoundary.Down.Add(0x12); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(AudioService.EndpointWrites.Count == 0 && !wheel.Pending, "disposed tray service queues no further work");
        }
        finally
        {
            KeyBoundary.Down.Clear(); app.BypassTrayWheel = false;
            AudioService.EndpointApplied = false; AudioService.Apps.Clear(); AudioService.Outputs.Clear();
            AudioService.Endpoints.Clear(); AudioService.EndpointWrites.Clear(); SessionVolumeService.AdjustTargets.Clear();
            config.TrayWheelEverywhere = false; config.TrayWheelPaused = false;
        }
        return passed;
    }
}

using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute;

// Actual hook dispatch, area policy, pending processing, target cache and audio handlers
// and rectangle/cache decisions are extracted unchanged by Prepare-Hotkeys.ps1.
// Only native identity/API/key boundaries and audio are fakes.
internal sealed partial class TrayWheelService : IDisposable
{
    private const int WM_MOUSEWHEEL = 0x020A;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT
    { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct NOTIFYICONIDENTIFIER
    { public int cbSize; public IntPtr hWnd; public int uID; public Guid guidItem; }
    private static readonly int TrayIdentifierSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>();
    private IntPtr _hook = IntPtr.Zero;
    private bool _disposed, _dispatchScheduled;
    private int _pendingDelta, _pendingDeviceDelta, _pendingSystemDeviceDelta, _pendingSystemVolumeDelta;
    private readonly object _appsLock = new();
    private List<AudioAppInfo>? _cachedApps;
    private DateTime _cachedAt;
    internal bool OverIcon = true;
    internal IntPtr IconWindow = new(1);
    internal int IconId = 1, IdentityReads;
    internal static int ShellResult, ShellCalls;
    private static RECT ShellRect = new() { Left = 0, Top = 0, Right = 100, Bottom = 100 };
    internal bool Pending => _dispatchScheduled;
    internal int VolumeNotices, DeviceNotices;
    internal string? LastDeviceText;
    private static IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data) => IntPtr.Zero;
    private bool TryGetTrayIconIdentity(out IntPtr hwnd, out int id)
    { IdentityReads++; hwnd = IconWindow; id = IconId; return OverIcon && hwnd != IntPtr.Zero; }
    private static int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT rect)
    { ShellCalls++; rect = ShellRect; return ShellResult; }
    internal static void SetShellRect(int left, int top, int right, int bottom)
        => ShellRect = new() { Left = left, Top = top, Right = right, Bottom = bottom };
    internal bool Hit(int x, int y) => IsOverOurIcon(new POINT { x = x, y = y });
    internal void ExpireIconRect() => _cachedIconRectAt = System.Diagnostics.Stopwatch.GetTimestamp() - 30L * System.Diagnostics.Stopwatch.Frequency;
    private static IntPtr FindWindow(string? name, string? text) => new(1);
    private static IntPtr FindWindowEx(IntPtr parent, IntPtr child, string name, string? text) => new(2);
    private static bool GetWindowRect(IntPtr window, out RECT rect) { rect = new() { Left = 0, Top = 0, Right = 100, Bottom = 100 }; return true; }
    private static bool PtInRect(ref RECT rect, POINT point) => point.x >= rect.Left && point.x < rect.Right && point.y >= rect.Top && point.y < rect.Bottom;
    private void ShowOsd(string name, string text) { DeviceNotices++; LastDeviceText = text; }
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
        using (var hit = new TrayWheelService())
        {
            try
            {
                TrayWheelService.ShellResult = 0;
                TrayWheelService.SetShellRect(0, 0, 100, 100);
                Check(hit.Hit(50, 50), "S_OK zero HRESULT accepts the live icon rectangle");
                Check(hit.Hit(-4, 50) && !hit.Hit(-5, 50) && !hit.Hit(104, 50), "icon hit keeps four-pixel margin and exclusive right edge");
                TrayWheelService.SetShellRect(200, 100, 300, 200);
                Check(!hit.Hit(50, 50) && hit.Hit(250, 150), "live icon relocation takes effect on the next input");
                TrayWheelService.ShellResult = unchecked((int)0x80004005);
                TrayWheelService.SetShellRect(0, 0, 100, 100);
                Check(hit.Hit(250, 150) && !hit.Hit(50, 50), "failed HRESULT ignores its output and uses only the same icon last-good rectangle");
                hit.ExpireIconRect();
                Check(!hit.Hit(250, 150), "failed API with expired rectangle rejects the hit without Explorer fallback");
                TrayWheelService.ShellResult = 0;
                TrayWheelService.SetShellRect(200, 100, 300, 200);
                Check(hit.Hit(250, 150), "successful API restores hit testing after cache expiry");
                hit.IconId++;
                TrayWheelService.ShellResult = unchecked((int)0x80004005);
                Check(!hit.Hit(250, 150), "changed icon ID never inherits another icon rectangle");
                TrayWheelService.ShellResult = 0;
                Check(hit.Hit(250, 150), "new icon ID establishes its own rectangle on success");
                hit.IconWindow = new IntPtr(2);
                TrayWheelService.ShellResult = unchecked((int)0x80004005);
                Check(!hit.Hit(250, 150), "rebuilt native window never inherits the old handle rectangle");
                TrayWheelService.ShellResult = 0;
                Check(hit.Hit(250, 150), "rebuilt native window recovers on the next successful query");
                TrayWheelService.SetShellRect(0, 0, 0, 0);
                Check(hit.Hit(250, 150), "empty success rectangle retains short-term recovery");
                hit.ExpireIconRect();
                Check(!hit.Hit(250, 150), "empty success rectangle cannot create a reliable cache");
                hit.IconWindow = IntPtr.Zero;
                int calls = TrayWheelService.ShellCalls;
                Check(!hit.Hit(250, 150) && TrayWheelService.ShellCalls == calls, "missing icon window skips the shell API");
            }
            finally
            {
                TrayWheelService.ShellResult = 0;
                TrayWheelService.SetShellRect(0, 0, 100, 100);
            }
        }
        AudioService.EndpointApplied = true;
        AudioService.Apps.Add(new AudioAppInfo { ProcessId = 101234, ProcessName = "HotkeyTarget", DisplayName = "Target" });
        AudioService.Outputs.AddRange(new[] { "A", "B", "C" }.Select(id => new AudioDeviceInfo { Id = id, DisplayName = id, Flow = EDataFlow.eRender }));
        using var wheel = new TrayWheelService();
        int originalStep = config.VolumeStep;
        try
        {
            int identityReads = wheel.IdentityReads, shellCalls = TrayWheelService.ShellCalls;
            wheel.Feed(0);
            Check(!wheel.Pending && wheel.IdentityReads == identityReads && TrayWheelService.ShellCalls == shellCalls, "zero wheel delta skips hit testing and dispatch entirely");
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
            // System switching must not resolve an application or touch its persisted route.
            config.TrayWheelEverywhere = false; wheel.OverIcon = true;
            AudioService.EndpointWrites.Clear(); SessionVolumeService.AdjustTargets.Clear();
            AudioService.Apps.Clear(); AudioService.ThrowApps = true;
            int appReads = AudioService.AppReads;
            foreach (int delta in new[] { 120, -120 })
            {
                AudioService.DefaultOutput = "B"; SystemDefaultDeviceService.Writes.Clear();
                KeyBoundary.Down.Add(0x12); KeyBoundary.Down.Add(0x10); wheel.Feed(delta); KeyBoundary.Down.Clear();
                await Drain(wheel);
                Check(SystemDefaultDeviceService.Writes.Count == 1 && SystemDefaultDeviceService.Writes[0] == (EDataFlow.eRender, delta > 0 ? "A" : "C"), "Alt Shift wheel changes system playback in captured direction without an app: " + delta);
            }
            Check(AudioService.AppReads == appReads && AudioService.EndpointWrites.Count == 0 && SessionVolumeService.AdjustTargets.Count == 0, "system wheel skips application discovery, routing and volume entirely");
            // Shift must reuse the real default-volume handler, even with failed app discovery.
            config.VolumeStep = 7;
            SystemDefaultDeviceService.Writes.Clear(); SystemVolumeService.Volumes.Clear(); app.VolumeNotices.Clear();
            foreach (int delta in new[] { 120, -120 })
            {
                AudioService.DefaultOutput = "B"; SystemVolumeService.Volumes.Clear();
                KeyBoundary.Down.Add(0x10); wheel.Feed(delta); KeyBoundary.Down.Clear(); await Drain(wheel);
                Check(SystemVolumeService.Volumes.Single() == ("B", delta > 0 ? 7 : -7, true), "Shift captures system volume direction and configured step after release: " + delta);
                Check(app.VolumeNotices.Last().Target == "device:B", "Shift OSD reuses actual default endpoint target");
            }
            Check(AudioService.AppReads == appReads && AudioService.EndpointWrites.Count == 0 && SessionVolumeService.AdjustTargets.Count == 0 && SystemDefaultDeviceService.Writes.Count == 0, "Shift adjusts system volume without app lookup or route/default-device changes");
            var panel = new QuickPanelModernWindow { IsVisible = true }; app.TestPanel = panel;
            SystemVolumeService.VolumeMuted = true; AudioService.DefaultOutput = "C"; SystemVolumeService.Volumes.Clear();
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); wheel.Feed(120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(SystemVolumeService.Volumes.Single() == ("C", 7, true), "Shift resolves current default each batch and coalesces like ordinary volume scroll");
            Check(panel.SystemVolumeReadbacks.Single() == ("C", 57, true) && app.VolumeNotices.Last().Muted, "Shift synchronizes an existing panel and retains muted OSD state");
            app.TestPanel = null; SystemVolumeService.VolumeMuted = false; SystemVolumeService.Volumes.Clear();
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(SystemVolumeService.Volumes.Count == 0, "opposing Shift input cancels without a volume operation");
            AudioService.DefaultOutput = null;
            int notices = app.VolumeNotices.Count;
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(SystemVolumeService.Volumes.Count == 0 && app.VolumeNotices.Count == notices && TrayFixture.LastText == "Ov.NoDevice", "missing default produces feedback without changing an app or another device");
            AudioService.DefaultOutput = "B"; SystemVolumeService.VolumeReadable = false;
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(app.VolumeNotices.Count == notices && TrayFixture.LastText == "Ov.NoDevice", "unreadable default volume does not report success");
            SystemVolumeService.VolumeReadable = true; SystemVolumeService.Succeeds = false;
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(app.VolumeNotices.Count == notices && app.LastOsdText == "Ov.VolAdjustFail", "failed system volume write uses existing failure feedback");
            SystemVolumeService.Succeeds = true; SystemVolumeService.Volumes.Clear();
            foreach (var sample in new[] { (Configured: 0, Expected: 1), (Configured: 100, Expected: 20) })
            {
                config.VolumeStep = sample.Configured; SystemVolumeService.Volumes.Clear();
                KeyBoundary.Down.Add(0x10); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
                Check(SystemVolumeService.Volumes.Single().Value == -sample.Expected, "Shift shares configured step bounds: " + sample.Configured);
            }
            config.VolumeStep = 7; SystemVolumeService.Volumes.Clear();
            AudioService.DefaultOutput = "A"; SystemDefaultDeviceService.Writes.Clear();
            KeyBoundary.Down.Add(0x12); KeyBoundary.Down.Add(0x10); wheel.Feed(-120); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(SystemDefaultDeviceService.Writes.Count == 1 && SystemDefaultDeviceService.Writes[0].Id == "C", "system wheel coalesces multiple ticks into one default-device write");
            config.HiddenOutputDevices.Add("C"); AudioService.DefaultOutput = "B"; SystemDefaultDeviceService.Writes.Clear();
            KeyBoundary.Down.Add(0x12); KeyBoundary.Down.Add(0x10); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(SystemDefaultDeviceService.Writes.Single().Id == "A", "system wheel respects retained playback devices");
            config.HiddenOutputDevices.Clear(); SystemDefaultDeviceService.Succeeds = false; SystemDefaultDeviceService.Writes.Clear();
            KeyBoundary.Down.Add(0x12); KeyBoundary.Down.Add(0x10); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(AudioService.DefaultOutput == "A" && wheel.LastDeviceText == "Ov.NoDevice", "failed system switch preserves default and reports failure");
            SystemDefaultDeviceService.Succeeds = true;
            var outputs = AudioService.Outputs.ToArray(); AudioService.Outputs.Clear(); SystemDefaultDeviceService.Writes.Clear();
            KeyBoundary.Down.Add(0x12); KeyBoundary.Down.Add(0x10); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(SystemDefaultDeviceService.Writes.Count == 0 && wheel.LastDeviceText == "Ov.NoDevice", "empty playback list performs no default write");
            AudioService.Outputs.AddRange(outputs); AudioService.ThrowApps = false;
            AudioService.Apps.Add(new AudioAppInfo { ProcessId = 101234, ProcessName = "HotkeyTarget", DisplayName = "Target" });
            AudioService.Endpoints[(101234, EDataFlow.eRender)] = "B"; AudioService.DefaultOutput = "B";
            SystemDefaultDeviceService.Writes.Clear(); AudioService.EndpointWrites.Clear(); SessionVolumeService.AdjustTargets.Clear(); SystemVolumeService.Volumes.Clear();
            KeyBoundary.Down.Add(0x12); wheel.Feed(-120); KeyBoundary.Down.Add(0x10); wheel.Feed(120); KeyBoundary.Down.Clear();
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); KeyBoundary.Down.Clear(); wheel.Feed(120); await Drain(wheel);
            Check(AudioService.EndpointWrites.Single().Id == "C" && SystemDefaultDeviceService.Writes.Single().Id == "A" && SessionVolumeService.AdjustTargets.Count == 1 && SystemVolumeService.Volumes.Single().Id == "A", "four coalesced input modes retain independent actions; system volume follows switched default");
            SystemDefaultDeviceService.Writes.Clear(); AudioService.EndpointWrites.Clear(); SessionVolumeService.AdjustTargets.Clear(); SystemVolumeService.Volumes.Clear();
            wheel.OverIcon = false;
            KeyBoundary.Down.Add(0x12); KeyBoundary.Down.Add(0x10); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(SystemDefaultDeviceService.Writes.Count == 0 && !wheel.Pending, "system wheel also respects own-icon area boundary");
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(SystemVolumeService.Volumes.Count == 0 && !wheel.Pending, "Shift respects own-icon area boundary");
            config.TrayWheelEverywhere = true;
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(SystemVolumeService.Volumes.Count == 1, "Shift also works in the saved whole-tray region");
            SystemVolumeService.Volumes.Clear();
            AudioService.EndpointWrites.Clear(); SessionVolumeService.AdjustTargets.Clear();
            app.BypassTrayWheel = true;
            KeyBoundary.Down.Add(0x12); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            KeyBoundary.Down.Add(0x12); KeyBoundary.Down.Add(0x10); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(AudioService.EndpointWrites.Count == 0 && SessionVolumeService.AdjustTargets.Count == 0 && SystemDefaultDeviceService.Writes.Count == 0 && SystemVolumeService.Volumes.Count == 0, "global mouse binding/capture bypass prevents all region actions");
            app.BypassTrayWheel = false;
            config.TrayWheelPaused = true;
            KeyBoundary.Down.Add(0x12); wheel.Feed(-120); KeyBoundary.Down.Clear(); wheel.Feed(120); await Drain(wheel);
            KeyBoundary.Down.Add(0x12); KeyBoundary.Down.Add(0x10); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(AudioService.EndpointWrites.Count == 0 && SessionVolumeService.AdjustTargets.Count == 0 && SystemDefaultDeviceService.Writes.Count == 0 && SystemVolumeService.Volumes.Count == 0, "paused region blocks all four wheel modes");
            config.TrayWheelPaused = false;
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); KeyBoundary.Down.Clear(); config.TrayWheelPaused = true; await Drain(wheel);
            Check(SystemVolumeService.Volumes.Count == 0, "pause after Shift queues discards pending system volume");
            config.TrayWheelPaused = false;
            KeyBoundary.Down.Add(0x12); wheel.Feed(-120); KeyBoundary.Down.Clear(); wheel.Feed(120);
            KeyBoundary.Down.Add(0x12); KeyBoundary.Down.Add(0x10); wheel.Feed(-120); KeyBoundary.Down.Clear();
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); KeyBoundary.Down.Clear();
            wheel.DiscardPendingWheel(); await Drain(wheel);
            Check(AudioService.EndpointWrites.Count == 0 && SessionVolumeService.AdjustTargets.Count == 0 && SystemDefaultDeviceService.Writes.Count == 0 && SystemVolumeService.Volumes.Count == 0, "discard clears all four pending input types");
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); KeyBoundary.Down.Clear(); wheel.Dispose();
            KeyBoundary.Down.Add(0x12); wheel.Feed(-120); KeyBoundary.Down.Clear(); await Drain(wheel);
            KeyBoundary.Down.Add(0x10); wheel.Feed(120); KeyBoundary.Down.Clear(); await Drain(wheel);
            Check(AudioService.EndpointWrites.Count == 0 && SystemVolumeService.Volumes.Count == 0 && !wheel.Pending, "disposed tray service queues no further work");
        }
        finally
        {
            KeyBoundary.Down.Clear(); app.BypassTrayWheel = false;
            app.TestPanel = null; config.VolumeStep = originalStep;
            SystemVolumeService.Succeeds = SystemVolumeService.VolumeReadable = true; SystemVolumeService.VolumeMuted = false; SystemVolumeService.Volumes.Clear();
            AudioService.ThrowApps = false; SystemDefaultDeviceService.Succeeds = true; SystemDefaultDeviceService.Writes.Clear(); config.HiddenOutputDevices.Clear();
            AudioService.EndpointApplied = false; AudioService.Apps.Clear(); AudioService.Outputs.Clear();
            AudioService.Endpoints.Clear(); AudioService.EndpointWrites.Clear(); SessionVolumeService.AdjustTargets.Clear();
            config.TrayWheelEverywhere = false; config.TrayWheelPaused = false;
        }
        return passed;
    }
}

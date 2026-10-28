using System.Reflection;
using SonicRoute.Core.Interop;

namespace SonicRoute;
public sealed partial class App
{
    private QuickPanelModernWindow? _quickPanel = null;
    private readonly TrayFixture _trayWheel = new();
    internal bool IsHotkeyCaptureActive => false;
    internal bool BypassTrayWheel;
    internal bool ShouldBypassTrayWheel(int delta) => BypassTrayWheel;
    internal int GlobalCalls;
    private void ToggleQuickPanel() { }
    internal Task TestHotkey(string action) => ExecuteHotkeyCoreAsync(action);
    internal readonly List<(int Pid, bool Muted)> AppInputReadbacks = new();
    internal void ApplyAppInputMuteReadback(int pid, bool muted) => AppInputReadbacks.Add((pid, muted));
    internal void ApplyAppMuteReadback(int pid, bool muted) => _quickPanel?.ApplyAppMuteReadback(pid, muted);
    internal QuickPanelModernWindow? TestPanel { get => _quickPanel; set => _quickPanel = value; }
    private Task<string?> CycleDeviceAsync(int pid, EDataFlow flow) => Task.FromResult<string?>("test-device");
    private Task<string?> CycleAllAppsDeviceAsync(EDataFlow flow) { GlobalCalls++; return Task.FromResult<string?>("test-device"); }
    private Task<string?> CycleSystemDefaultDeviceAsync(EDataFlow flow) { GlobalCalls++; return Task.FromResult<string?>("test-device"); }
}
internal sealed class QuickPanelModernWindow
{
    internal bool IsVisible { get; set; }
    internal readonly List<(int Pid, bool Muted)> MuteReadbacks = new();
    internal void ApplyAppMuteReadback(int pid, bool muted) => MuteReadbacks.Add((pid, muted));
    internal readonly List<bool> MicReadbacks = new();
    internal void ApplyMicMuteReadback(bool muted) => MicReadbacks.Add(muted);
    internal void ApplyAppVolumeReadback(int pid, int volume) { }
    internal readonly List<(string? Device, int Volume, bool Muted)> SystemVolumeReadbacks = new();
    internal void ApplySystemVolumeReadback(string? device, int volume, bool muted) => SystemVolumeReadbacks.Add((device, volume, muted));
}
internal sealed class TrayFixture
{
    internal static int Notices;
    internal static string? LastText;
    internal void ShowOsd(string title, string text) { Notices++; LastText = text; }
    internal void ShowVolumeOsd(string title, int volume, string text, bool muted = false, string? target = null) { }
}
internal static class KeyBoundary { internal static readonly HashSet<int> Down = new(); }
internal static class HotkeyChecks
{
    internal static async Task<int> Run(bool includeTray = true)
    {
        int passed = 0;
        void Check(bool result, string text) { if (!result) throw new Exception(text); passed++; Console.WriteLine("PASS " + text); }
        var app = new App(); AudioService.ThrowApps = true; AudioService.AppReads = 0;
        foreach (string action in new[] { HotkeyActions.ActMuteInput, HotkeyActions.ActSwitchAllOutput, HotkeyActions.ActSwitchAllInput,
            HotkeyActions.ActSetDefaultOutput, HotkeyActions.ActSetDefaultInput, HotkeyActions.ActSystemVolUp, HotkeyActions.ActSystemVolDown })
        { await app.TestHotkey(action); Check(AudioService.AppReads == 0, "global hotkey bypasses unrelated failing current-app discovery: " + action); }
        Check(app.GlobalCalls == 4 && SystemVolumeService.Volumes.Count == 2, "global device and actual volume handlers execute despite unavailable app discovery");
        foreach (string action in new[] { HotkeyActions.ActMute, HotkeyActions.ActVolUp, HotkeyActions.ActVolDown, HotkeyActions.ActSwitchOutput, HotkeyActions.ActSwitchInput })
        { int notices = TrayFixture.Notices; await app.TestHotkey(action); Check(TrayFixture.Notices == notices + 1, "current-app discovery failure gives feedback without unhandled task: " + action); }
        AudioService.ThrowApps = false;
        AudioService.Apps.Add(new SonicRoute.Core.Models.AudioAppInfo { ProcessId = 101234, ProcessName = "HotkeyTarget", DisplayName = "Target" });
        try
        {
            foreach (bool visible in new[] { false, true })
            {
                var panel = new QuickPanelModernWindow { IsVisible = visible };
                app.TestPanel = visible ? panel : null;
                int writes = SystemVolumeService.Writes.Count, mutes = SessionVolumeService.MuteTargets.Count;
                await app.TestHotkey(HotkeyActions.ActMute);
                Check(SessionVolumeService.MuteTargets.Count == mutes + 1 && SessionVolumeService.MuteTargets.Last() == 101234
                    && SystemVolumeService.Writes.Count == writes, "restored playback mute targets the app session with panel visible=" + visible);
                Check(panel.MuteReadbacks.Count == (visible ? 1 : 0), "restored playback mute synchronizes only an existing panel: " + visible);
                SessionVolumeService.MuteApplied = false;
                int readbacks = panel.MuteReadbacks.Count;
                await app.TestHotkey(HotkeyActions.ActMute);
                Check(panel.MuteReadbacks.Count == readbacks, "failed playback mute leaves panel state unchanged: " + visible);
                SessionVolumeService.MuteApplied = true;
                int micWrites = GlobalMicMuteService.Writes.Count, micNotices = app.MicNotices;
                await app.TestHotkey(HotkeyActions.ActMuteInput);
                Check(GlobalMicMuteService.Writes.Count == micWrites + 1 && app.MicNotices == micNotices + 1
                    && panel.MicReadbacks.Count == (visible ? 1 : 0), "microphone hotkey executes and notifies once with panel visible=" + visible);
            }
        }
        finally { app.TestPanel = null; AudioService.Apps.Clear(); SessionVolumeService.MuteApplied = true; }
        var modifiers = typeof(MouseHotkeyHook).GetMethod("GetPressedMods", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var pair in new[] { (Keys: new[] { 0x5B }, Expected: 8u), (Keys: new[] { 0x5C }, Expected: 8u),
            (Keys: new[] { 0x5B, 0x5C }, Expected: 8u), (Keys: new[] { 0x11, 0x5C }, Expected: 10u),
            (Keys: new[] { 0x12, 0x10, 0x5C }, Expected: 13u), (Keys: Array.Empty<int>(), Expected: 0u) })
        { KeyBoundary.Down.Clear(); foreach (int key in pair.Keys) KeyBoundary.Down.Add(key); Check((uint)modifiers.Invoke(null, null)! == pair.Expected, "mouse modifier mapping: " + string.Join(",", pair.Keys)); }
        KeyBoundary.Down.Clear();
        Check(HotkeyActions.All.Length == 14 && HotkeyActions.All.Distinct().Count() == 14
            && HotkeyActions.All.Contains(HotkeyActions.ActMute) && HotkeyActions.Defaults[HotkeyActions.ActMute] == "Ctrl+Shift+M"
            && !HotkeyActions.All.Contains("静音当前应用麦克风") && !HotkeyActions.Defaults.ContainsKey("静音当前应用麦克风")
            && HotkeyActions.Groups.All(group => !group.Actions.Contains("静音当前应用麦克风")),
            "14 actions restore playback mute and omit retired app microphone shortcut from registration, defaults and groups");
        if (includeTray) passed += await TrayWheelChecks.Run(app);
        return passed;
    }
}

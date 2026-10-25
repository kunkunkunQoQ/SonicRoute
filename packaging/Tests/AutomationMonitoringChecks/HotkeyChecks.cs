using System.Reflection;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

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
    internal QuickPanelModernWindow? TestPanel { get => _quickPanel; set => _quickPanel = value; }
    private Task AdjustDefaultOutputVolumeAsync(bool increase) { GlobalCalls++; return Task.CompletedTask; }
    internal readonly List<(string Scope,int Pid,EDataFlow Flow,bool Previous)> CycleCalls = new();
    private Task<string?> CycleDeviceAsync(int pid, EDataFlow flow, bool previous)
    { CycleCalls.Add(("app",pid,flow,previous)); return CycleDeviceUnderTestAsync(pid,flow,previous); }
    private Task<string?> CycleAllAppsDeviceAsync(EDataFlow flow, bool previous)
    { CycleCalls.Add(("all",-1,flow,previous)); GlobalCalls++; return CycleAllAppsDeviceUnderTestAsync(flow,previous); }
    private Task<string?> CycleSystemDefaultDeviceAsync(EDataFlow flow, bool previous)
    { CycleCalls.Add(("system",-1,flow,previous)); GlobalCalls++; return CycleSystemDefaultDeviceUnderTestAsync(flow,previous); }
}
internal sealed class QuickPanelModernWindow
{
    internal bool IsVisible { get; set; }
    internal readonly List<(int Pid, bool Muted)> MuteReadbacks = new();
    internal void ApplyAppMuteReadback(int pid, bool muted) => MuteReadbacks.Add((pid, muted));
    internal readonly List<bool> MicReadbacks = new();
    internal void ApplyMicMuteReadback(bool muted) => MicReadbacks.Add(muted);
    internal void ApplyAppVolumeReadback(int pid, int volume) { }
}
internal sealed class TrayFixture
{
    internal static int Notices;
    internal static string LastText = "";
    internal void ShowOsd(string title, string text) { Notices++; LastText=text; }
    internal void ShowVolumeOsd(string title, int volume, string text, bool muted = false, string? target = null) { }
}
internal static class KeyBoundary { internal static readonly HashSet<int> Down = new(); }
internal static class HotkeyChecks
{
    internal static async Task<int> Run()
    {
        int passed = 0;
        void Check(bool result, string text) { if (!result) throw new Exception(text); passed++; Console.WriteLine("PASS " + text); }
        var app = new App(); AudioService.ThrowApps = true; AudioService.AppReads = 0;
        foreach (string action in new[] { HotkeyActions.ActMuteInput,
            HotkeyActions.ActSwitchAllOutputPrevious, HotkeyActions.ActSwitchAllOutputNext,
            HotkeyActions.ActSwitchAllInputPrevious, HotkeyActions.ActSwitchAllInputNext,
            HotkeyActions.ActSetDefaultOutputPrevious, HotkeyActions.ActSetDefaultOutputNext,
            HotkeyActions.ActSetDefaultInputPrevious, HotkeyActions.ActSetDefaultInputNext,
            HotkeyActions.ActSystemVolUp, HotkeyActions.ActSystemVolDown })
        {
            int reads = AudioService.AppReads;
            await app.TestHotkey(action);
            bool allApps = action is HotkeyActions.ActSwitchAllOutputPrevious or HotkeyActions.ActSwitchAllOutputNext
                or HotkeyActions.ActSwitchAllInputPrevious or HotkeyActions.ActSwitchAllInputNext;
            Check(AudioService.AppReads == reads + (allApps ? 1 : 0), "global hotkey avoids current-target discovery; all-app handler only reads its own batch: " + action);
        }
        Check(app.GlobalCalls == 10, "global device and volume handlers execute despite unavailable current-app discovery");
        foreach (string action in new[] { HotkeyActions.ActMute, HotkeyActions.ActVolUp, HotkeyActions.ActVolDown,
            HotkeyActions.ActSwitchOutputPrevious, HotkeyActions.ActSwitchOutputNext, HotkeyActions.ActSwitchInputPrevious, HotkeyActions.ActSwitchInputNext })
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
                    && SystemVolumeService.Writes.Count == writes, "current-app mute targets session with panel visible=" + visible);
                Check(panel.MuteReadbacks.Count == (visible ? 1 : 0), "current-app mute synchronizes only an existing panel: " + visible);
                SessionVolumeService.MuteApplied = false;
                int readbacks = panel.MuteReadbacks.Count;
                await app.TestHotkey(HotkeyActions.ActMute);
                Check(panel.MuteReadbacks.Count == readbacks, "failed mute leaves panel state unchanged: " + visible);
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
        passed += await CheckDeviceDirections(app);
        passed += await TrayWheelChecks.Run(app);
        return passed;
    }

    private static async Task<int> CheckDeviceDirections(App app)
    {
        int passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
        var pairs = new[] {
            (Scope:"app", Flow:EDataFlow.eRender, Previous:HotkeyActions.ActSwitchOutputPrevious, Next:HotkeyActions.ActSwitchOutputNext, Id:"切换当前应用快捷设备", Combo:"Ctrl+Alt+D"),
            (Scope:"app", Flow:EDataFlow.eCapture, Previous:HotkeyActions.ActSwitchInputPrevious, Next:HotkeyActions.ActSwitchInputNext, Id:"切换当前应用麦克风设备", Combo:"Ctrl+Alt+Shift+D"),
            (Scope:"all", Flow:EDataFlow.eRender, Previous:HotkeyActions.ActSwitchAllOutputPrevious, Next:HotkeyActions.ActSwitchAllOutputNext, Id:"切换全局应用输出设备", Combo:"Ctrl+Alt+Shift+O"),
            (Scope:"all", Flow:EDataFlow.eCapture, Previous:HotkeyActions.ActSwitchAllInputPrevious, Next:HotkeyActions.ActSwitchAllInputNext, Id:"切换全局应用输入设备", Combo:"Ctrl+Alt+Shift+I"),
            (Scope:"system", Flow:EDataFlow.eRender, Previous:HotkeyActions.ActSetDefaultOutputPrevious, Next:HotkeyActions.ActSetDefaultOutputNext, Id:"切换系统默认输出设备", Combo:"Ctrl+Alt+O"),
            (Scope:"system", Flow:EDataFlow.eCapture, Previous:HotkeyActions.ActSetDefaultInputPrevious, Next:HotkeyActions.ActSetDefaultInputNext, Id:"切换系统默认输入设备", Combo:"Ctrl+Alt+I")
        };
        Check(HotkeyActions.All.Length == HotkeyActions.All.Distinct().Count()
            && HotkeyActions.Groups.SelectMany(g => g.Actions).OrderBy(a => a).SequenceEqual(HotkeyActions.All.OrderBy(a => a)), "each action appears exactly once in registration and settings groups");
        Check(HotkeyActions.All.Take(14).SequenceEqual(new[] { HotkeyActions.ActVolUp, HotkeyActions.ActVolDown,
            HotkeyActions.ActSystemVolUp, HotkeyActions.ActSystemVolDown, HotkeyActions.ActSwitchOutputNext,
            HotkeyActions.ActMute, HotkeyActions.ActMuteInput, HotkeyActions.ActSwitchAllOutputNext, HotkeyActions.ActSwitchAllInputNext,
            HotkeyActions.ActResetAllApps, HotkeyActions.ActSetDefaultOutputNext, HotkeyActions.ActSetDefaultInputNext,
            HotkeyActions.ActSwitchInputNext, HotkeyActions.ActPanel }), "existing registration/conflict priority precedes all new previous actions");
        foreach (var pair in pairs)
        {
            Check(pair.Next == pair.Id && HotkeyActions.Defaults[pair.Next] == pair.Combo && !HotkeyActions.Defaults.ContainsKey(pair.Previous),
                "old action ID/default retained on next; previous unbound: " + pair.Scope + pair.Flow);
            var saved = System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"Hotkeys\":{" + System.Text.Json.JsonSerializer.Serialize(pair.Id) + ":\"\"}}")!;
            Check(saved.Hotkeys.TryGetValue(pair.Next, out var combo) && combo == "", "old explicit cleared binding still belongs to next: " + pair.Scope + pair.Flow);
            bool microphone = pair.Scope != "system" && pair.Flow == EDataFlow.eCapture;
            Check(HotkeyActions.RequiresMicrophoneOption(pair.Previous) == microphone && HotkeyActions.RequiresMicrophoneOption(pair.Next) == microphone,
                "microphone visibility and registration agree for both directions: " + pair.Scope + pair.Flow);
        }
        Check(PanelDevices.CycleIndex(0, 3, true) == 2 && PanelDevices.CycleIndex(2, 3, false) == 0, "directional wrap at first/last item");
        Check(PanelDevices.CycleIndex(-1, 3, true) == 2 && PanelDevices.CycleIndex(-1, 3, false) == 0, "unknown/hidden current item uses direction boundary");
        Check(PanelDevices.CycleIndex(0, 0, true) == -1 && PanelDevices.CycleIndex(0, 1, true) == 0, "empty and singleton cycle boundaries");
        Check(PanelDevices.CycleIndex(1, 4, true, 2) == 3 && PanelDevices.CycleIndex(1, 4, false, 10) == 3, "coalesced wheel steps wrap without extra route writes");
        var config = ConfigService.Load();
        AudioService.Apps.Add(new AudioAppInfo { ProcessId = 101234, ProcessName = "HotkeyTarget", DisplayName = "Target" });
        AudioService.Apps.Add(new AudioAppInfo { ProcessId = 202345, ProcessName = "SecondTarget", DisplayName = "Second" });
        AudioService.EndpointApplied = true;
        try
        {
            foreach (var pair in pairs)
            {
                var devices = pair.Flow == EDataFlow.eRender ? AudioService.Outputs : AudioService.Inputs;
                var hidden = pair.Flow == EDataFlow.eRender ? config.HiddenOutputDevices : config.HiddenInputDevices;
                devices.Clear(); hidden.Clear();
                devices.AddRange(new[] { "A", "B", "C" }.Select(id => new AudioDeviceInfo { Id = id, DisplayName = id, Flow = pair.Flow }));
                config.DeviceNames["C"] = "Custom C";
                foreach (bool previous in new[] { true, false })
                {
                    AudioService.Endpoints[(101234, pair.Flow)] = AudioPolicyConfig.GenerateDeviceId("B", pair.Flow);
                    AudioService.Endpoints[(202345, pair.Flow)] = AudioPolicyConfig.GenerateDeviceId("A", pair.Flow);
                    if (pair.Flow == EDataFlow.eRender) AudioService.DefaultOutput = AudioPolicyConfig.GenerateDeviceId("B", pair.Flow);
                    else AudioService.DefaultInput = AudioPolicyConfig.GenerateDeviceId("B", pair.Flow);
                    AudioService.EndpointWrites.Clear(); SystemDefaultDeviceService.Writes.Clear();
                    await app.TestHotkey(previous ? pair.Previous : pair.Next);
                    var call = app.CycleCalls.Last(); string expected = previous ? "A" : "C";
                    Check(call.Scope == pair.Scope && call.Flow == pair.Flow && call.Previous == previous
                        && (pair.Scope != "app" || call.Pid == 101234), "dispatch scope/flow/direction: " + pair.Scope + pair.Flow + previous);
                    Check(pair.Scope == "system" ? SystemDefaultDeviceService.Writes.Count == 1 && SystemDefaultDeviceService.Writes[0] == (pair.Flow, expected)
                        : AudioService.EndpointWrites.Count == (pair.Scope == "all" ? 2 : 1) && AudioService.EndpointWrites.All(w => w.Flow == pair.Flow && w.Id == expected),
                        "actual cycle method selects expected device: " + pair.Scope + pair.Flow + previous);
                    Check(TrayFixture.LastText.EndsWith(previous ? "A" : "Custom C", StringComparison.Ordinal), "OSD uses selected device/custom name: " + pair.Scope + pair.Flow + previous);
                }
                AudioService.Endpoints[(101234, pair.Flow)] = "B";
                if (pair.Flow == EDataFlow.eRender) AudioService.DefaultOutput = "B"; else AudioService.DefaultInput = "B";
                AudioService.EndpointApplied = false; SystemDefaultDeviceService.Succeeds = false;
                await app.TestHotkey(pair.Previous);
                Check(TrayFixture.LastText == L10n.T(pair.Flow == EDataFlow.eRender ? "Ov.NoDevice" : "Ov.NoMicDevice")
                    && AudioService.Endpoints[(101234, pair.Flow)] == "B", "route failure gives failure feedback without altering previous route: " + pair.Scope + pair.Flow);
                AudioService.EndpointApplied = true; SystemDefaultDeviceService.Succeeds = true;
                if (pair.Scope != "system")
                {
                    string system = pair.Flow == EDataFlow.eRender ? AudioService.SystemDefaultDeviceId : AudioService.SystemDefaultInputDeviceId;
                    foreach (bool previous in new[] { true, false })
                    {
                        AudioService.Endpoints[(101234, pair.Flow)] = null;
                        AudioService.EndpointWrites.Clear();
                        await app.TestHotkey(previous ? pair.Previous : pair.Next);
                        Check(AudioService.EndpointWrites.All(w => w.Id == (previous ? "C" : "A")), "following default is the virtual anchor: " + pair.Scope + pair.Flow + previous);
                    }
                    AudioService.Endpoints[(101234, pair.Flow)] = AudioPolicyConfig.GenerateDeviceId("A", pair.Flow);
                    AudioService.EndpointWrites.Clear(); await app.TestHotkey(pair.Previous);
                    Check(AudioService.EndpointWrites.All(w => w.Id == system) && AudioService.Endpoints[(101234, pair.Flow)] == null, "previous returns application routes to system default: " + pair.Scope + pair.Flow);
                    hidden.Add(system);
                }
                hidden.Add("B"); AudioService.Endpoints[(101234, pair.Flow)] = "B";
                if (pair.Flow == EDataFlow.eRender) AudioService.DefaultOutput = "B"; else AudioService.DefaultInput = "B";
                AudioService.EndpointWrites.Clear(); SystemDefaultDeviceService.Writes.Clear();
                await app.TestHotkey(pair.Previous);
                Check(pair.Scope == "system" ? SystemDefaultDeviceService.Writes.All(w => w.Id == "C")
                    : AudioService.EndpointWrites.All(w => w.Id == "C"), "hidden current device falls back to last visible real device: " + pair.Scope + pair.Flow);
                devices.Clear(); AudioService.EndpointWrites.Clear(); SystemDefaultDeviceService.Writes.Clear();
                await app.TestHotkey(pair.Next);
                Check(AudioService.EndpointWrites.Count == 0 && SystemDefaultDeviceService.Writes.Count == 0, "no visible devices cause no writes: " + pair.Scope + pair.Flow);
                hidden.Clear();
            }
        }
        finally
        {
            AudioService.EndpointApplied = false; SystemDefaultDeviceService.Succeeds = true; AudioService.Apps.Clear(); AudioService.Inputs.Clear(); AudioService.Outputs.Clear();
            AudioService.Endpoints.Clear(); AudioService.EndpointWrites.Clear(); SystemDefaultDeviceService.Writes.Clear();
            AudioService.DefaultInput = "mic-a"; AudioService.DefaultOutput = "speaker-a";
            config.HiddenInputDevices.Clear(); config.HiddenOutputDevices.Clear(); config.DeviceNames.Clear();
        }
        return passed;
    }
}

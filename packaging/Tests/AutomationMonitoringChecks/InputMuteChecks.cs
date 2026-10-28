using System.Reflection;
using SonicRoute.Core;
using SonicRoute.Core.Models;

namespace SonicRoute;

internal static class InputMuteChecks
{
    internal static async Task<int> Run()
    {
        int passed = 0;
        void Check(bool value, string text) { if (!value) throw new Exception(text); passed++; Console.WriteLine("PASS " + text); }
        var app = new App();
        var config = ConfigService.Load();
        config.DefaultAppMode = "fixed"; config.FixedAppName = "InputTargetB";
        AudioService.Apps.Clear(); AudioService.ThrowApps = false;
        AudioService.Apps.Add(new AudioAppInfo { ProcessId = 101234, ProcessName = "InputTargetA" });
        AudioService.Apps.Add(new AudioAppInfo { ProcessId = 202345, ProcessName = "InputTargetB" });
        SessionVolumeService.InputApplied = true;
        var execute = typeof(AutoRuleService).GetMethod("ExecuteStepAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        Task<bool> Step(AutoRuleAction action, bool muted = false, CancellationToken token = default) =>
            (Task<bool>)execute.Invoke(null, new object?[] { new AutoRuleStep { Action = action, Muted = muted, TargetApp = "InputTargetB" },
                token, new HashSet<string>(), null, null })!;
        try
        {
            Check((int)AutoRuleAction.SetAppInputMute == 28 && (int)AutoRuleAction.HideQuickPanel == 36
                && (int)AutoRuleAction.SetCurrentAppInputMute == 37 && (int)AutoRuleAction.ToggleCurrentAppInputMute == 38,
                "current-app actions append stable IDs without changing saved rules");
            Check(!AutoRuleActions.NeedsApp(AutoRuleAction.SetCurrentAppInputMute)
                && AutoRuleActions.SetsMute(AutoRuleAction.SetCurrentAppInputMute)
                && !AutoRuleActions.SetsMute(AutoRuleAction.ToggleCurrentAppInputMute),
                "current-app set/toggle reuse the correct editor parameters");
            Check(!HotkeyActions.All.Contains("静音当前应用麦克风")
                && !HotkeyActions.Defaults.ContainsKey("静音当前应用麦克风")
                && HotkeyActions.Defaults[HotkeyActions.ActMute] == "Ctrl+Shift+M"
                && HotkeyActions.Defaults[HotkeyActions.ActMuteInput] == "Ctrl+Shift+N",
                "retired app microphone shortcut leaves playback and global microphone defaults intact");
            int globalWrites = GlobalMicMuteService.Writes.Count;
            var beforeRetired = SessionVolumeService.LastInput;
            int retiredReads = AudioService.AppReads, retiredNotices = TrayFixture.Notices;
            await app.TestHotkey("静音当前应用麦克风");
            Check(SessionVolumeService.LastInput == beforeRetired && GlobalMicMuteService.Writes.Count == globalWrites
                && app.AppInputReadbacks.Count == 0 && AudioService.AppReads == retiredReads && TrayFixture.Notices == retiredNotices,
                "saved app microphone shortcut performs no discovery, audio write, readback or OSD");
            var beforeGlobal = SessionVolumeService.LastInput;
            await app.TestHotkey(HotkeyActions.ActMuteInput);
            Check(GlobalMicMuteService.Writes.Count == globalWrites + 1 && SessionVolumeService.LastInput == beforeGlobal,
                "existing global microphone shortcut keeps its original scope");
            Check(await Step(AutoRuleAction.SetCurrentAppInputMute, false)
                && SessionVolumeService.LastInput == (202345, (bool?)false), "automation current-app set applies its explicit off state");
            config.FixedAppName = "InputTargetA";
            Check(await Step(AutoRuleAction.ToggleCurrentAppInputMute)
                && SessionVolumeService.LastInput == (101234, (bool?)null), "automation resolves the current target at execution time");
            Check(await Step(AutoRuleAction.SetAppInputMute, true)
                && SessionVolumeService.LastInput == (202345, (bool?)true), "existing specified-app input action remains distinct");
            SessionVolumeService.InputApplied = false;
            int readbacks = app.AppInputReadbacks.Count;
            Check(!await Step(AutoRuleAction.SetCurrentAppInputMute, true) && app.AppInputReadbacks.Count == readbacks,
                "failed capture verification fails automation without publishing success");
            SessionVolumeService.InputApplied = true;
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel(); var previous = SessionVolumeService.LastInput; bool rejected = false;
                try { await Step(AutoRuleAction.ToggleCurrentAppInputMute, token: canceled.Token); }
                catch (OperationCanceledException) { rejected = true; }
                Check(rejected && SessionVolumeService.LastInput == previous, "canceled automation does not write a capture session");
            }
            AudioService.Apps.Clear(); var beforeMissing = SessionVolumeService.LastInput;
            Check(!await Step(AutoRuleAction.SetCurrentAppInputMute, true) && SessionVolumeService.LastInput == beforeMissing,
                "automation without a current app fails without an audio write");
            int notices = TrayFixture.Notices;
            await app.TestHotkey("静音当前应用麦克风");
            Check(TrayFixture.Notices == notices && SessionVolumeService.LastInput == beforeMissing,
                "retired microphone shortcut also remains inert without a current app");
            Check(GlobalMicMuteService.Writes.Count == globalWrites + 1,
                "all current-app paths leave global microphone state untouched");
            return passed;
        }
        finally { AudioService.Apps.Clear(); SessionVolumeService.InputApplied = true; }
    }
}

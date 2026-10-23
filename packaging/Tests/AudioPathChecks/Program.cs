using System.Diagnostics;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace AudioPathChecks;

internal static class Program
{
    private static int _passed, _failed;
    private static readonly Dictionary<string, int> Counts = new();
    private static int Expected(int before, int after)
    {
#if BASELINE
        return before;
#else
        return after;
#endif
    }
    private static void Assert(bool value, string detail)
    { if (!value) throw new InvalidOperationException(detail); }
    private static void Case(string name, Action test)
    {
        try { Boundary.Reset(); test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { _failed++; Console.WriteLine("FAIL " + name + ": " + error); }
    }
    private static void Metric(string key, int actual, int before, int after)
    { Counts[key] = actual; Assert(actual == Expected(before, after), key + "=" + actual); }
    private static (int Volume, bool Muted) Read(int pid)
    {
#if BASELINE
        return (SessionVolumeService.GetVolumePercent(pid), SessionVolumeService.IsMuted(pid));
#else
        return SessionVolumeService.ReadState(pid);
#endif
    }
    public static int Main()
    {
        Case("device mute uses one activation and actual state", () =>
        {
            var device = Boundary.Add(EDataFlow.eRender);
            Assert(SystemVolumeService.ToggleMute(device.Id) && device.Volume.Muted, "mute");
            Metric("deviceToggleActivations", Boundary.Activations, 3, 1);
        });
        Case("device checked toggle refuses unreadable initial state", () =>
        {
            var device = Boundary.Add(EDataFlow.eRender); device.Volume.ReadFailures = 10;
            Assert(!SystemVolumeService.ToggleMuteChecked().Applied && device.Volume.Writes == 0, "unknown state wrote");
        });
        Case("device checked toggle verifies ignored write", () =>
        {
            var device = Boundary.Add(EDataFlow.eRender); device.Volume.IgnoreWrite = true;
            var state = SystemVolumeService.ToggleMuteChecked(device.Id);
            Assert(!state.Applied && !state.Muted, "ignored write accepted");
        });
        Case("device checked toggle releases on write exception", () =>
        {
            var device = Boundary.Add(EDataFlow.eRender); device.Volume.ThrowWrite = true;
            Assert(!SystemVolumeService.ToggleMuteChecked(device.Id).Applied && Boundary.Releases == 3, "release or error");
        });
        Case("device volume write and readback share endpoint", () =>
        {
            var device = Boundary.Add(EDataFlow.eRender);
#if BASELINE
            bool applied = SystemVolumeService.SetVolumePercent(device.Id, 73);
            int actual = SystemVolumeService.GetVolumePercent(device.Id);
#else
            var (applied, actual) = SystemVolumeService.SetVolumePercentChecked(device.Id, 73);
#endif
            Assert(applied && actual == 73, "volume readback");
            Metric("deviceWriteActivations", Boundary.Activations, 2, 1);
        });
        Case("global microphone toggle shares initial discovery and retains topology verification", () =>
        {
            var a = Boundary.Add(EDataFlow.eCapture); var b = Boundary.Add(EDataFlow.eCapture);
            b.Volume.Muted = true;
            Assert(GlobalMicMuteService.Toggle() && a.Volume.Muted && b.Volume.Muted, "all mute");
            Metric("microphoneToggleEnumerators", Boundary.Enumerators, 3, 2);
        });
        Case("all-muted microphones toggle off", () =>
        {
            var a = Boundary.Add(EDataFlow.eCapture); a.Volume.Muted = true;
            Assert(!GlobalMicMuteService.Toggle() && !a.Volume.Muted, "unmute");
        });
        Case("microphone checked set rejects partial write", () =>
        {
            var a = Boundary.Add(EDataFlow.eCapture); var b = Boundary.Add(EDataFlow.eCapture); b.Volume.WriteFails = true;
            var state = GlobalMicMuteService.SetMuteChecked(true);
            Assert(!state.Applied && state.State.MutedCount == 1 && a.Volume.Muted, "partial write");
        });
        Case("microphone checked set keeps no-device unknown", () =>
        { Assert(!GlobalMicMuteService.SetMuteChecked(true).Applied, "no device accepted"); });
        Case("all playback toggle shares initial discovery and retains final verification", () =>
        {
            var a = new Session(101); var b = new Session(102);
            Boundary.Add(EDataFlow.eRender, a, b);
            Assert(SessionVolumeService.ToggleAllMute() && a.Muted && b.Muted, "all playback mute");
            Metric("playbackToggleEnumerators", Boundary.Enumerators, 3, 2);
        });
        Case("mixed sessions within one process are detected", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(103) { Muted = true }, new Session(103));
            Assert(SessionVolumeService.AllMuted() == (Expected(1, 0) != 0), "same PID mixed mute state");
        });
        Case("app state read preserves first-session semantics", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(104) { Volume = .43f, Muted = true }, new Session(104) { Volume = .19f });
            var state = Read(104); Assert(state.Volume == 43 && state.Muted, "first session");
        });
        Case("missing app state does not repeat a complete scan", () =>
        {
            var state = Read(999); Assert(state.Volume == -1 && !state.Muted, "missing app state");
            Metric("missingStateEnumerators", Boundary.Enumerators, 2, 1);
        });
        Case("app state recovers after getter failure", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(105) { VolumeReadFailures = 1, Muted = true });
            var state = Read(105); Assert(state.Volume == 60 && state.Muted && Boundary.Enumerators == 2, "getter recovery");
        });
        Case("app enumeration avoids device display properties and repeated lookup", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(106)); Boundary.Add(EDataFlow.eCapture, new Session(107));
            Assert(AudioService.GetApps(true).Count == 2, "application list");
            Metric("appEnumerationPropertyStores", Boundary.Properties, 2, 0);
            Metric("appEnumerationDeviceLookups", Boundary.Lookups, 2, 0);
        });
        Case("device display enumeration still reads friendly name", () =>
        {
            Boundary.Add(EDataFlow.eRender);
            Assert(AudioService.GetDevices(EDataFlow.eRender)[0].DisplayName == "设备 名称" && Boundary.Properties == 1, "device name");
        });
        Case("app aggregation keeps active state and demand-loaded icon metadata", () =>
        {
            var a = new Session(108) { State = AudioSessionState.Inactive }; var b = new Session(108);
            Boundary.Add(EDataFlow.eRender, a); Boundary.Add(EDataFlow.eCapture, b);
            AudioService.SetAppIconMetadataEnabled(false);
            var apps = AudioService.GetApps(true);
            Assert(apps.Count == 1 && apps[0].HasActiveSession && a.IconReads + b.IconReads == 0, "aggregation or icon disabled");
            AudioService.SetAppIconMetadataEnabled(true);
            apps = AudioService.GetApps(true);
            Assert(apps[0].IconPath != null && a.IconReads + b.IconReads == 1, "session icon metadata");
            AudioService.SetAppIconMetadataEnabled(false);
        });
        Case("expired and system-process sessions remain excluded", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(0), new Session(109) { State = AudioSessionState.Expired }, new Session(110));
            Assert(AudioService.GetApps(true).Single().ProcessId == 110, "filter changed");
        });
        Case("ordinary app cache hit avoids enumeration", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(111));
            var first = AudioService.GetApps(true); Boundary.ClearCounts();
            Assert(ReferenceEquals(first, AudioService.GetApps()) && Boundary.Enumerators == 0, "cache miss");
        });
        Case("panel route names share one policy per batch", () =>
        {
            var apps = Enumerable.Range(120, 5).Select(pid => new AudioAppInfo { ProcessId = (uint)pid }).ToList();
            Boundary.Add(EDataFlow.eRender, apps.Select(a => new Session(a.ProcessId)).ToArray());
            var rows = PanelRows.Read(apps, true);
            Assert(rows.Count == 5 && rows[120].output == "endpoint120", "panel state");
            Metric("panelRoutePolicyObjects", Boundary.Policies, 5, 1);
            Assert(Boundary.PolicyDisposals == Boundary.Policies, "policy release");
        });
        Case("disabled route names create no policy", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(130));
            var rows = PanelRows.Read(new List<AudioAppInfo> { new() { ProcessId = 130 } }, false);
            Assert(rows[130].output == null && Boundary.Policies == 0, "disabled feature loaded");
        });
        Case("one route failure does not stop other rows", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(131), new Session(132)); Boundary.RouteFailures.Add(131);
            var rows = PanelRows.Read(new List<AudioAppInfo> { new() { ProcessId = 131 }, new() { ProcessId = 132 } }, true);
            Assert(rows[131].output == AudioService.SystemDefaultDeviceId && rows[132].output == "endpoint132", "route error recovery");
        });
        Case("input mute still forces post-write discovery and validation", () =>
        {
            var session = new Session(140); Boundary.Add(EDataFlow.eCapture, session);
            var result = SessionVolumeService.ChangeInputMuteChecked(140, true);
            Assert(result.Applied && result.Muted && session.Muted && Boundary.Enumerators == 2, "input verification removed");
        });
        Case("input mute catches successful HRESULT without effect", () =>
        {
            Boundary.Add(EDataFlow.eCapture, new Session(141) { IgnoreWrite = true });
            Assert(!SessionVolumeService.ChangeInputMuteChecked(141, true).Applied, "ignored capture write");
        });
        Case("canceled batch never enumerates or writes", () =>
        {
            var session = new Session(142); Boundary.Add(EDataFlow.eRender, session);
            try { SessionVolumeService.ChangeAllPlaybackMuteChecked(true, new CancellationToken(true)); throw new Exception("not canceled"); }
            catch (OperationCanceledException) { }
            Assert(session.Writes == 0 && Boundary.Enumerators == 0, "canceled write");
        });
        Case("system default output uses explicit ordinary roles", () =>
        {
            Assert(SystemDefaultDeviceService.SetDefault(EDataFlow.eRender, "output").Success, "default output");
            Assert(string.Join(",", Boundary.PolicyRoles) == (Expected(0, 1) == 0 ? "0" : "0,1"), "output roles");
        });
        Case("system default input uses same ordinary roles", () =>
        {
            Assert(SystemDefaultDeviceService.SetDefault(EDataFlow.eCapture, "input").Success, "default input");
            Assert(string.Join(",", Boundary.PolicyRoles) == (Expected(0, 1) == 0 ? "1" : "0,1"), "input roles");
        });
#if !BASELINE
        Case("empty visible list skips session discovery with route names disabled", () =>
        {
            var rows = PanelRows.Read(new List<AudioAppInfo>(), false);
            Assert(rows.Count == 0 && Boundary.Enumerators == 0 && Boundary.Policies == 0,
                $"empty list enumerators={Boundary.Enumerators} policies={Boundary.Policies}");
        });
        Case("all hidden apps skip discovery and route policy creation", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(143));
            var rows = PanelRows.Read(new List<AudioAppInfo>(), true);
            Assert(rows.Count == 0 && Boundary.Enumerators == 0 && Boundary.Policies == 0,
                $"hidden apps enumerators={Boundary.Enumerators} policies={Boundary.Policies}");
        });
        Case("volume readback failure keeps unknown actual value", () =>
        {
            var d = Boundary.Add(EDataFlow.eRender); d.Volume.VolumeReadFailures = 10;
            var result = SystemVolumeService.SetVolumePercentChecked(d.Id, 80);
            Assert(result.Applied && result.Actual == -1 && Boundary.Activations == 1, "readback unknown");
        });
        Case("failed volume write still reads actual value", () =>
        {
            var d = Boundary.Add(EDataFlow.eRender); d.Volume.WriteFails = true;
            var result = SystemVolumeService.SetVolumePercentChecked(d.Id, 80);
            Assert(!result.Applied && result.Actual == 50 && Boundary.Activations == 1, "write failure");
        });
        Case("microphone toggle refuses uncertain endpoint collection", () =>
        {
            var a = Boundary.Add(EDataFlow.eCapture); Boundary.Add(EDataFlow.eCapture).ActivationFails = true;
            var result = GlobalMicMuteService.ToggleChecked();
            Assert(!result.Applied && !result.State.Known && a.Volume.Writes == 0, "uncertain toggle wrote");
        });
        Case("microphone toggle refuses unknown mute state", () =>
        {
            var a = Boundary.Add(EDataFlow.eCapture); a.Volume.ReadFailures = 10;
            Assert(!GlobalMicMuteService.ToggleChecked().Applied && a.Volume.Writes == 0, "unknown mute wrote");
        });
        Case("microphone toggle verifies endpoints added during write", () =>
        {
            var a = Boundary.Add(EDataFlow.eCapture);
            a.Volume.AfterMuteWrite = () => Boundary.Add(EDataFlow.eCapture);
            var result = GlobalMicMuteService.ToggleChecked();
            Assert(!result.Applied && !result.State.AllMuted && result.State.Count == 2, "new microphone hidden");
        });
        Case("all playback partial failure is not reported as success", () =>
        {
            var a = new Session(150); var b = new Session(151) { WriteFails = true };
            Boundary.Add(EDataFlow.eRender, a, b);
            var result = SessionVolumeService.ToggleAllMuteChecked();
            Assert(!result.Applied && !result.Muted && a.Muted && !b.Muted && Boundary.Enumerators == 2, "partial result");
        });
        Case("all playback verifies replacement sessions after a write", () =>
        {
            var a = new Session(156); var device = Boundary.Add(EDataFlow.eRender, a);
            a.AfterMuteWrite = () => device.Sessions[0] = new Session(156);
            var result = SessionVolumeService.ToggleAllMuteChecked();
            Assert(!result.Applied && !result.Muted && Boundary.Enumerators == 2, "stale written session accepted");
        });
        Case("all playback unknown initial state does not write", () =>
        {
            var a = new Session(152) { ReadFailures = 10 }; Boundary.Add(EDataFlow.eRender, a);
            Assert(!SessionVolumeService.ToggleAllMuteChecked().Applied && a.Writes == 0, "unknown playback wrote");
        });
        Case("all playback no sessions is not success", () =>
        { Assert(!SessionVolumeService.ToggleAllMuteChecked().Applied, "empty playback accepted"); });
        Case("combined state read survives independent mute failure", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(153) { ReadFailures = 10 });
            var state = SessionVolumeService.ReadState(153);
            Assert(state.Volume == 60 && !state.Muted && Boundary.Enumerators == 2, "independent values");
        });
        Case("combined state read holds references across concurrent refresh", () =>
        {
            var a = new Session(154); Boundary.Add(EDataFlow.eRender, a); SessionVolumeService.Refresh(true);
            Task? refresh = null;
            a.OnReadVolume = () =>
            {
                a.OnReadVolume = null; refresh = Task.Run(() => SessionVolumeService.Refresh(true));
                Assert(!refresh.Wait(40), "refresh released objects while reading");
            };
            Assert(SessionVolumeService.ReadState(154).Volume == 60, "read value");
            Assert(refresh != null && refresh.Wait(5000), "refresh failed to finish");
        });
        Case("failed device enumeration does not publish empty app cache", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(155)); AudioService.GetApps(true);
            Boundary.ScanFailure = -1;
            try { AudioService.GetApps(true); throw new Exception("scan failure swallowed"); }
            catch (InvalidOperationException) { }
            Boundary.ScanFailure = 0;
            Assert(AudioService.GetApps(true).Count == 1, "cache recovery");
        });
        Case("default role failure is reported and policy released", () =>
        {
            Boundary.PolicyFailureRole = 0;
            Assert(!SystemDefaultDeviceService.SetDefault(EDataFlow.eRender, "device").Success, "role failure");
            Assert(string.Join(",", Boundary.PolicyRoles) == "0,1" && Boundary.Releases == 1, "roles or release");
        });
        Case("invalid default direction is rejected without activation", () =>
        {
            Assert(!SystemDefaultDeviceService.SetDefault(EDataFlow.eAll, "device").Success && Boundary.Policies == 0, "invalid direction");
        });
#endif
        Console.WriteLine("COUNTS {" + string.Join(",", Counts.Select(p => "\"" + p.Key + "\":" + p.Value)) + "}");
        Console.WriteLine($"RESULT passed={_passed} failed={_failed} runtime={Environment.Version} audioWrites=FAKES_ONLY");
        return _failed == 0 ? 0 : 1;
    }
}

using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

internal sealed partial class ProductAutomation
{
    internal async Task<bool> ExecuteStepAsync(AutoRuleStep step, ExecutionHistoryStep? detail, CancellationToken token)
    {
        if (step.Action is AutoRuleAction.ShowQuickPanel or AutoRuleAction.HideQuickPanel)
            return await FeedbackAsync(step.Action == AutoRuleAction.ShowQuickPanel ? FeedbackKind.ShowPanel : FeedbackKind.HidePanel, "", "", token);
        return await Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (step.Action is AutoRuleAction.SetGlobalMicMute or AutoRuleAction.ToggleGlobalMicMute)
            {
                bool muted = audio.GlobalMicMute(step.Action == AutoRuleAction.SetGlobalMicMute ? step.Muted : null);
                await FeedbackAsync(FeedbackKind.Osd, L10n.T("Ov.MuteMic"), L10n.T(muted ? "Ov.MicMuted" : "Ov.MicUnmuted"), token);
                return true;
            }
            if (step.Action == AutoRuleAction.ResetAllAppRouting) return audio.ResetRoutes();

            bool targetApp = AutoRuleActions.NeedsApp(step.Action);
            bool current = step.Action is AutoRuleAction.SetCurrentAppInputMute or AutoRuleAction.ToggleCurrentAppInputMute;
            int pid = 0;
            if (current || targetApp)
            {
                var app = current ? host.CurrentApp.Resolve() : FindApp(step.TargetApp);
                if (app == null) { if (detail != null) detail.Reason = "app_unavailable"; return false; }
                pid = checked((int)app.ProcessId);
                if (detail != null) { detail.TargetPid = pid; detail.TargetApp = AppDisplayName.Get(app); }
            }

            bool batch = step.Action is AutoRuleAction.SetAllAppsVolume or AutoRuleAction.AdjustAllAppsVolume or
                AutoRuleAction.SetAllAppsMute or AutoRuleAction.ToggleAllAppsMute;
            if (batch || current || step.Action is AutoRuleAction.SetAppInputMute or AutoRuleAction.ToggleAppInputMute)
            {
                var result = audio.BatchVolume(batch ? Flow.Render : Flow.Capture, batch ? null : pid,
                    AutoRuleActions.SetsVolume(step.Action) ? Math.Clamp(step.Volume,0,100) : null,
                    AutoRuleActions.AdjustsVolume(step.Action) ? Math.Clamp(step.VolumeDelta,-100,100) : null,
                    AutoRuleActions.SetsMute(step.Action) ? step.Muted : null, token);
                if (detail != null)
                {
                    detail.SucceededTargets = result.Succeeded; detail.TotalTargets = result.Total;
                    if (result.Total == 0) detail.Reason = "no_sessions";
                    else if (result.Succeeded < result.Total) detail.Reason = "partial_failure";
                }
                return result.Total > 0 && result.Total == result.Succeeded;
            }

            bool input = AutoRuleActions.UsesCaptureDevice(step.Action) || step.Action is AutoRuleAction.SetDefaultInputVolume
                or AutoRuleAction.AdjustDefaultInputVolume or AutoRuleAction.SetDefaultInputMute or AutoRuleAction.ToggleDefaultInputMute;
            Flow flow = input ? Flow.Capture : Flow.Render;
            string? endpoint = null;
            if (step.Action is AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput)
            {
                string target = string.IsNullOrWhiteSpace(step.TargetDeviceId) ? AudioService.SystemDefaultDeviceId : step.TargetDeviceId;
                return flow == Flow.Capture ? audio.SetRoute(pid, flow, target) :
                    Success(await host.CurrentApp.SelectOutputAsync(pid, target));
            }
            if (AutoRuleActions.NeedsDevice(step.Action))
            {
                if (string.IsNullOrWhiteSpace(step.TargetDeviceId) || AudioService.IsSystemDefault(step.TargetDeviceId))
                { if (detail != null) detail.Reason = "device_not_selected"; return false; }
                var device = ProductAudio.Devices(flow).FirstOrDefault(d => Same(d.Id,step.TargetDeviceId));
                if (device == null) { if (detail != null) detail.Reason = "device_unavailable"; return false; }
                endpoint = device.Id;
                if (detail != null) { detail.TargetDeviceId = device.Id; detail.TargetDeviceName = device.Name; }
                if (step.Action is AutoRuleAction.SetSystemOutput or AutoRuleAction.SetSystemInput)
                    return audio.SetDefault(flow, endpoint);
            }
            bool toggle = step.Action is AutoRuleAction.ToggleSystemMute or AutoRuleAction.ToggleAppMute
                or AutoRuleAction.TogglePlaybackDeviceMute or AutoRuleAction.ToggleDefaultInputMute or AutoRuleAction.ToggleMicrophoneMute;
            int? volume = AutoRuleActions.SetsVolume(step.Action) ? Math.Clamp(step.Volume,0,100) : null;
            int? delta = AutoRuleActions.AdjustsVolume(step.Action) ? Math.Clamp(step.VolumeDelta,-100,100) : null;
            bool? mute = AutoRuleActions.SetsMute(step.Action) ? step.Muted : null;
            if (!toggle && volume == null && delta == null && mute == null) return false;
            return audio.Volume(flow, endpoint, pid, volume, mute, delta ?? 0, toggle).Count > 0;
        },token).ConfigureAwait(false);
    }

    private static bool Success(ControlReply result) => !result.Text.StartsWith("error:", StringComparison.Ordinal);
    private static bool Same(string? left,string? right) => left != null && right != null &&
        string.Equals(AudioPolicyConfig.UnpackDeviceId(left),AudioPolicyConfig.UnpackDeviceId(right),StringComparison.OrdinalIgnoreCase);

    private AudioAppInfo? FindApp(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string clean = Path.GetFileName(name.Trim());
        if (clean.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)) clean = clean[..^4];
        var apps = audio.Apps();
        bool Matches(string? v) => string.Equals(v,clean,StringComparison.OrdinalIgnoreCase) || string.Equals(v,name,StringComparison.OrdinalIgnoreCase);
        return apps.FirstOrDefault(a => Matches(a.ProcessName)) ?? apps.FirstOrDefault(a => Matches(a.Label));
    }

    private sealed record InputFeedback(string Title, string Text, int? Volume = null, bool? Muted = null, bool Success = true);
    private readonly Queue<(string Action, int Pid, nint Hwnd, int Steps)> inputs = new(32);
    private bool processingInputs;

    private void Hotkey(string action)
    {
        if (action.StartsWith(AutoRuleService.HotkeyPrefix, StringComparison.Ordinal))
        {
            _ = AutoRuleService.ExecuteByHotkeyAsync(action[AutoRuleService.HotkeyPrefix.Length..]);
            return;
        }
        if (action == HotkeyActions.ActPanel) { feedback.Publish(FeedbackKind.TogglePanel); return; }
        nint hwnd = ShellNative.GetForegroundWindow();
        int pid = hwnd == 0 ? ForegroundAppService.GetForegroundProcessId() : ProductWindowPid(hwnd);
        QueueInput(action, pid, hwnd, 1);
    }

    private void Wheel(ShellSignal signal)
    {
        int steps = (int)Math.Max(1, Math.Min(int.MaxValue, Math.Abs(signal.Value) / 120));
        if (signal.Value < 0) steps = -steps;
        nint hwnd = signal.Window;
        int pid = hwnd == 0 ? ForegroundAppService.GetForegroundProcessId() : ProductWindowPid(hwnd);
        if (hwnd == 0) hwnd = ShellNative.GetForegroundWindow();
        switch (signal.Modifiers)
        {
            case 5: QueueInput(HotkeyActions.ActSetDefaultOutput, pid, hwnd, -steps); break;
            case 1: QueueInput(HotkeyActions.ActSwitchOutput, pid, hwnd, -steps); break;
            case 4: QueueInput(steps > 0 ? HotkeyActions.ActSystemVolUp : HotkeyActions.ActSystemVolDown, pid, hwnd, 1); break;
            default: QueueInput(steps > 0 ? HotkeyActions.ActVolUp : HotkeyActions.ActVolDown, pid, hwnd, 1); break;
        }
    }

    private void QueueInput(string action, int pid, nint hwnd, int steps)
    {
        if (action == HotkeyActions.ActSwitchOutput) { _ = host.CurrentApp.CycleOutputAsync(pid, hwnd: hwnd, steps: steps); return; }
        if (inputs.Count >= 128) { feedback.Publish(FeedbackKind.Osd, HotkeyActions.DisplayName(action), L10n.T("Auto.Running")); return; }
        inputs.Enqueue((action, pid, hwnd, steps));
        if (!processingInputs) _ = DrainInputs();
    }

    private async Task DrainInputs()
    {
        processingInputs = true;
        try
        {
            while (!stopped && inputs.TryDequeue(out var command))
            {
                try
                {
                    var result = await Task.Run(() => ExecuteInput(command.Action, command.Pid, command.Hwnd, command.Steps));
                    if (!stopped)
                    {
                        feedback.Publish(FeedbackKind.Osd, result.Title, result.Text, result.Volume, result.Muted, result.Success);
                        host.NotifyAudioVisualChanged();
                    }
                }
                catch (Exception error)
                {
                    if (!stopped)
                        feedback.Publish(FeedbackKind.Osd, HotkeyActions.DisplayName(command.Action), error.Message, success: false);
                }
            }
        }
        finally { processingInputs = false; }
    }

    private async Task<InputFeedback> ExecuteInput(string action, int foreground, nint hwnd, int steps)
    {
        if (action == HotkeyActions.ActMuteInput)
        {
            bool muted = audio.GlobalMicMute(null);
            return new(L10n.T("Ov.MuteMic"), L10n.T(muted ? "Ov.MicMuted" : "Ov.MicUnmuted"), Muted: muted);
        }
        if (action == HotkeyActions.ActResetAllApps)
        {
            bool ok = audio.ResetRoutes();
            return new(HotkeyActions.DisplayName(action), L10n.T(ok ? "Act.ResetAllAppsDone" : "Act.ResetAllAppsFailed"), Success: ok);
        }
        if (action is HotkeyActions.ActSetDefaultInput or HotkeyActions.ActSetDefaultOutput
            or HotkeyActions.ActSwitchInput or HotkeyActions.ActSwitchAllInput or HotkeyActions.ActSwitchAllOutput)
        {
            bool output = action is HotkeyActions.ActSetDefaultOutput or HotkeyActions.ActSwitchAllOutput;
            bool system = action is HotkeyActions.ActSetDefaultOutput or HotkeyActions.ActSetDefaultInput;
            var flow = output ? Flow.Render : Flow.Capture;
            var hidden = output ? config.HiddenOutputDevices : config.HiddenInputDevices;
            var devices = (output ? audio.RenderDevicesWithPhone() : ProductAudio.Devices(flow))
                .Where(d => !hidden.Contains(d.Id)).ToList();
            string defaultId = output ? AudioService.SystemDefaultDeviceId : AudioService.SystemDefaultInputDeviceId;
            if (!system && !hidden.Contains(defaultId))
                devices.Insert(0, new(defaultId, L10n.T(output ? "Dev.SystemDefaultOut" : "Dev.SystemDefaultIn")));
            if (devices.Count == 0) throw new InvalidOperationException(L10n.T("Ov.NoDevice"));
            AudioAppInfo? singleApp = null;
            List<AudioAppInfo> apps;
            if (system) apps = [];
            else if (action == HotkeyActions.ActSwitchInput)
            {
                singleApp = host.CurrentApp.Resolve(foreground, hwnd) ?? throw new InvalidOperationException(L10n.T("Ov.NoSession"));
                apps = [singleApp];
            }
            else apps = audio.Apps();
            if (!system && apps.Count == 0) throw new InvalidOperationException(L10n.T("Ov.NoSession"));
            if (singleApp != null) host.CurrentApp.RecordUsedApp(singleApp);
            string? old = system ? ProductAudio.DefaultId(flow) : audio.ReadRoute(apps[0], flow);
            old ??= action == HotkeyActions.ActSwitchInput ? defaultId : ProductAudio.DefaultId(flow);
            int index = devices.FindIndex(d => Same(d.Id, old));
            var target = devices[DeviceCycle.Index(index, devices.Count, steps < 0, Math.Abs(steps))];
            if (system) audio.SetDefault(flow, target.Id);
            else
            {
                int writes = 0;
                foreach (var a in apps)
                {
                    try
                    {
                        if (flow == Flow.Render)
                        {
#if SONICROUTE_PHONE
                            if (a.IsExternal)
                            {
                                var reply = await audio.SetExternalOutputsAsync(a, [target.Id]);
                                if (Success(reply)) writes++;
                                continue;
                            }
#endif
                            if (!a.IsExternal && Success(await host.CurrentApp.SelectOutputAsync((int)a.ProcessId, target.Id)))
                                writes++;
                        }
                        else if (!a.IsExternal && audio.SetRoute((int)a.ProcessId, flow, target.Id))
                        {
                            writes++;
                        }
                    }
                    catch (Exception e) { AutoRuleScheduler.Log("Hotkey route: " + e.Message); }
                }
                if (writes == 0) throw new InvalidOperationException(L10n.T("Ov.NoDevice"));
            }
            string devLabel = config.DeviceNames.TryGetValue(target.Id, out string? name) && !string.IsNullOrWhiteSpace(name) ? name : target.Name;
            string prefix = output ? "🔊 " : "🎙️ ";
            string title = singleApp != null ? AppDisplayName.Get(singleApp) : HotkeyActions.DisplayName(action);
            return new(title, prefix + devLabel);
        }

        int step = Math.Clamp(config.VolumeStep, 1, 20);
        AudioAppInfo? targetApp = null;
        if (action is HotkeyActions.ActMute or HotkeyActions.ActVolUp or HotkeyActions.ActVolDown)
        {
            targetApp = host.CurrentApp.Resolve(foreground, hwnd) ?? throw new InvalidOperationException(L10n.T("Ov.NoOutputSession"));
            host.CurrentApp.RecordUsedApp(targetApp);
        }
        bool toggle = action == HotkeyActions.ActMute;
        var state = targetApp != null
            ? audio.Volume(targetApp, delta: toggle ? 0 : action == HotkeyActions.ActVolUp ? step : -step, toggle: toggle)
            : audio.Volume(Flow.Render, null, 0,
                delta: toggle ? 0 : action is HotkeyActions.ActVolUp or HotkeyActions.ActSystemVolUp ? step : -step, toggle: toggle);
        string volTitle = targetApp != null ? AppDisplayName.Get(targetApp) : HotkeyActions.DisplayName(action);
        string volText = state.Muted ? L10n.T("Ov.AppMuted") : $"{state.Percent}%";
        return new(volTitle, volText, Volume: state.Percent, Muted: state.Muted);
    }

    [System.Runtime.InteropServices.LibraryImport("user32")] private static partial uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    private static int ProductWindowPid(nint hwnd) { GetWindowThreadProcessId(hwnd,out var pid); return (int)pid; }
}

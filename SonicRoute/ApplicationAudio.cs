using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Compat;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
#endif

namespace SonicRoute;

/// <summary>Shared application targets for UI, automation, hotkeys and tray. Native APIs only receive real PIDs.</summary>
internal static class ApplicationAudio
{
    internal static event Action? Changed;
    internal static event Action? OutputsChanged;
    internal static void NotifyChanged()
    {
        var changed = Changed;
        if (changed != null) foreach (Action listener in changed.GetInvocationList())
            try { listener(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex.Message); }
    }
    internal static void NotifyOutputsChanged()
    {
        var changed = OutputsChanged;
        if (changed != null) foreach (Action listener in changed.GetInvocationList())
            try { listener(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex.Message); }
        NotifyChanged();
    }
    internal static bool IsPhoneOutputDevice(string? deviceId) =>
#if SONICROUTE_PHONE
        AndroidOutputIdentity.IsPhoneOutput(deviceId);
#else
        false;
#endif
    internal static List<AudioDeviceInfo> GetOutputDevices() =>
        MergeExternalOutputs(AudioService.GetDevices(EDataFlow.eRender));
    internal static List<AudioDeviceInfo> MergeExternalOutputs(IEnumerable<AudioDeviceInfo> windowsOutputs)
    {
        var list = windowsOutputs.Where(d => !IsPhoneOutputDevice(d.Id)).ToList();
#if SONICROUTE_PHONE
        foreach (var phoneOut in AndroidAudioHost.GetOutputDevices())
        {
            if (!list.Any(d => string.Equals(d.Id, phoneOut.Id, StringComparison.OrdinalIgnoreCase)))
                list.Add(phoneOut);
        }
#endif
        return list;
    }
    internal static IEnumerable<AudioDeviceInfo> OutputsForApp(AudioAppInfo? app, IEnumerable<AudioDeviceInfo> outputs)
    {
#if SONICROUTE_PHONE
        if (app?.IsExternal == true)
        {
            var source = Source(app);
            var list = outputs.Where(d => !IsPhoneOutputDevice(d.Id)).ToList();
            // An active incoming source can outlive the device-discovery snapshot.
            if (source != null)
                list.Add(AndroidAudioHost.SpeakerOutput(source.Device));
            return list;
        }
#endif
        return outputs;
    }
    internal static List<AudioAppInfo> GetApps(bool refresh = false) => MergeExternal(AudioService.GetApps(refresh));
    internal static bool HasExternal =>
#if SONICROUTE_PHONE
        AndroidAudioHost.IsCreated && AndroidAudioHost.Sources.Sources.Count > 0;
#else
        false;
#endif
    internal static string? ForegroundAppName(int pid)
    {
#if SONICROUTE_PHONE
        if (AndroidAudioHost.AreDisplaysCreated && AndroidAudioHost.Displays.FindApplication(pid) is { } display)
            return AndroidKey(display.App.Package);
#endif
        return ForegroundAppService.GetProcessNameSafe(pid);
    }
    internal static async Task<List<AudioAppInfo>> GetAppsAsync(bool refresh = false) => MergeExternal(await AudioService.GetAppsAsync(refresh).ConfigureAwait(false));
    internal static List<AudioAppInfo> MergeExternal(IEnumerable<AudioAppInfo> apps)
    {
        var result = apps.Where(a => !a.IsExternal).ToList();
#if SONICROUTE_PHONE
        if (AndroidAudioHost.IsCreated) result.AddRange(AndroidAudioHost.Sources.Sources.Select(Describe));
#endif
        return result;
    }
#if SONICROUTE_PHONE
    internal static string AndroidKey(string package) => "android:" + package;
    internal static AudioAppInfo Describe(AndroidAudioSource source) => new()
    {
        ProcessId = 0, ExternalSourceId = source.Id, ProcessName = AndroidKey(source.App.Package),
        DisplayName = source.App.Name, SourceLabel = "来自 " + AndroidAudioHost.Phone.GetDisplayName(source.Device), ExternalIconPng = source.App.IconPng,
        HasActiveSession = source.Session.State.State == CaptureState.Receiving
    };
    private static AndroidAudioSource? Source(AudioAppInfo app) => !app.IsExternal || !AndroidAudioHost.IsCreated ? null
        : AndroidAudioHost.Sources.Sources.FirstOrDefault(s => s.Id == app.ExternalSourceId);
    private static bool IsSourceSpeaker(AndroidAudioSource source, string deviceId) =>
        AndroidOutputIdentity.TryParseDeviceId(deviceId, out string phoneKey, out string sinkKey)
        && string.Equals(phoneKey, AndroidDeviceIdentity.Key(source.Device), StringComparison.OrdinalIgnoreCase)
        && string.Equals(sinkKey, "speaker", StringComparison.OrdinalIgnoreCase);
#endif
    internal static (int Volume, bool Muted, bool MuteKnown) ReadStateChecked(AudioAppInfo app)
    {
        if (!app.IsExternal) return SessionVolumeService.ReadStateChecked((int)app.ProcessId);
#if SONICROUTE_PHONE
        var state = Source(app)?.Session.State;
        if (state != null && state.State == CaptureState.Receiving) return (state.Volume, state.Muted, true);
#endif
        return (-1, false, false);
    }
    internal static int GetVolumePercent(AudioAppInfo app) => ReadStateChecked(app).Volume;
    internal static bool IsMuted(AudioAppInfo app) => ReadStateChecked(app).Muted;
    internal static bool HasSession(AudioAppInfo app) => ReadStateChecked(app).Volume >= 0;
    internal static bool SetVolumePercent(AudioAppInfo app, int percent) => SetVolumePercentChecked(app, percent).Applied;
    internal static (bool Applied, int Actual) SetVolumePercentChecked(AudioAppInfo app, int percent)
    {
        if (!app.IsExternal) return SessionVolumeService.SetVolumePercentChecked((int)app.ProcessId, percent);
#if SONICROUTE_PHONE
        var source = Source(app);
        if (source?.Session.State.State == CaptureState.Receiving)
        { source.Session.SetVolume(percent); return (source.Session.State.Volume == MathEx.Clamp(percent, 0, 100), source.Session.State.Volume); }
#endif
        return (false, -1);
    }
    internal static (int Current, int Requested, int Actual, bool Applied) AdjustVolumePercent(AudioAppInfo app, int delta)
    {
        if (!app.IsExternal) return SessionVolumeService.AdjustVolumePercent((int)app.ProcessId, delta);
#if SONICROUTE_PHONE
        var source = Source(app);
        if (source == null) return (-1, -1, -1, false);
        lock (source.Session)
        {
            int current = GetVolumePercent(app), requested = MathEx.Clamp(current + delta, 0, 100);
            var result = current < 0 ? (Applied: false, Actual: -1) : SetVolumePercentChecked(app, requested);
            return (current, requested, result.Actual, result.Applied);
        }
#else
        return (-1, -1, -1, false);
#endif
    }
    internal static bool SetMute(AudioAppInfo app, bool muted)
    {
        if (!app.IsExternal) return SessionVolumeService.SetMute((int)app.ProcessId, muted);
#if SONICROUTE_PHONE
        var source = Source(app);
        if (source?.Session.State.State == CaptureState.Receiving)
        { source.Session.SetMute(muted); return source.Session.State.Muted == muted; }
#endif
        return false;
    }
    internal static (bool Muted, bool Applied) ToggleMuteChecked(AudioAppInfo app)
    {
        if (!app.IsExternal) return SessionVolumeService.ToggleMuteChecked((int)app.ProcessId);
#if SONICROUTE_PHONE
        var source = Source(app);
        if (source == null) return (false, false);
        lock (source.Session)
        {
            var state = ReadStateChecked(app); bool target = !state.Muted;
            return (target, state.MuteKnown && SetMute(app, target));
        }
#else
        return (false, false);
#endif
    }
    internal static string? GetPhoneRoutedDeviceId(int processId) =>
#if SONICROUTE_PHONE
        AndroidAudioHost.TryGetPcAppPhoneOutput(processId);
#else
        null;
#endif
    internal static string? GetPersistedEndpoint(AudioAppInfo app, EDataFlow flow) =>
        !app.IsExternal ? GetPersistedEndpoint((int)app.ProcessId, flow) : GetExternalEndpoint(app, flow);

    internal static string? GetPersistedEndpoint(int processId, EDataFlow flow)
    {
#if SONICROUTE_PHONE
        if (flow == EDataFlow.eRender && AndroidAudioHost.TryGetPcAppPhoneOutput(processId) is { } phoneOut)
            return phoneOut;
#endif
        return AudioService.GetPersistedEndpoint(processId, flow);
    }

    private static string? GetExternalEndpoint(AudioAppInfo app, EDataFlow flow)
    {
#if SONICROUTE_PHONE
        if (flow == EDataFlow.eRender) return Source(app)?.Session.State.OutputId;
#endif
        return null;
    }

    internal static (bool Success, int HResult, string Message) ApplyEndpoint(AudioAppInfo app, EDataFlow flow, string deviceId)
    {
        if (!app.IsExternal)
        {
#if SONICROUTE_PHONE
            if (flow == EDataFlow.eRender)
            {
                if (AndroidOutputIdentity.IsPhoneOutput(deviceId))
                    return AndroidAudioHost.RoutePcAppToPhone((int)app.ProcessId, deviceId);
                AndroidAudioHost.StopPcAppToPhone((int)app.ProcessId, restorePreviousEndpoint: false);
            }
#endif
            return AudioService.ApplyEndpoint((int)app.ProcessId, flow, deviceId);
        }
#if SONICROUTE_PHONE
        // Synchronous callers (automation/hotkeys/tray) already execute on workers.
        return ApplyExternalEndpointAsync(app, flow, deviceId).GetAwaiter().GetResult();
#else
        return (false, -1, "此音频会话没有录音路由或已经结束。");
#endif
    }
    internal static Task<(bool Success, int HResult, string Message)> ApplyEndpointAsync(AudioAppInfo app, EDataFlow flow, string deviceId)
    {
#if SONICROUTE_PHONE
        if (app.IsExternal) return ApplyExternalEndpointAsync(app, flow, deviceId);
#endif
        return Task.FromResult(ApplyEndpoint(app, flow, deviceId));
    }
#if SONICROUTE_PHONE
    private static async Task<(bool Success, int HResult, string Message)> ApplyExternalEndpointAsync(AudioAppInfo app, EDataFlow flow, string deviceId)
    {
        var source = Source(app);
        if (source != null && flow == EDataFlow.eRender)
        {
            try
            {
                if (AndroidOutputIdentity.IsPhoneOutput(deviceId))
                {
                    if (!IsSourceSpeaker(source, deviceId)) return (false, -1, "请选择此应用所在手机的扬声器。");
                    var owner = (App)System.Windows.Application.Current;
                    var payload = new AndroidAppDrag(source.Device, source.App);
                    // Presentation ownership belongs to the UI dispatcher. Stop it before
                    // retiring the source so late display notifications cannot recapture it.
                    var returned = owner.Dispatcher.CheckAccess() ? owner.ReturnAndroidToPhoneAsync(payload)
                        : owner.Dispatcher.InvokeAsync(() => owner.ReturnAndroidToPhoneAsync(payload)).Task.Unwrap();
                    await returned.ConfigureAwait(false);
                }
                else await source.Session.SetOutputAsync(AudioService.IsSystemDefault(deviceId) ? null : deviceId).ConfigureAwait(false);
                return (true, 0, "");
            }
            catch (Exception ex) { return (false, ex.HResult, ex.Message); }
        }
        return (false, -1, "此音频会话没有录音路由或已经结束。");
    }
#endif
    internal static (bool Muted, bool Applied) ChangeInputMuteChecked(AudioAppInfo app, bool? muted, CancellationToken token = default)
        => app.IsExternal ? (false, false) : SessionVolumeService.ChangeInputMuteChecked((int)app.ProcessId, muted, token);

    internal static (int Succeeded, int Total) ChangeAllPlaybackVolumeChecked(int value, bool relative, CancellationToken token = default)
    {
        (int Succeeded, int Total) result = (0, 0);
        foreach (var app in GetApps(refresh: true))
        {
            token.ThrowIfCancellationRequested(); if (!HasSession(app)) continue; ++result.Total;
            if (relative ? AdjustVolumePercent(app, value).Applied : SetVolumePercent(app, value)) ++result.Succeeded;
        }
        return result;
    }
    internal static (int Succeeded, int Total) ChangeAllPlaybackMuteChecked(bool? muted, CancellationToken token = default)
    {
        bool target = muted ?? !AllMuted();
        (int Succeeded, int Total) result = (0, 0);
        foreach (var app in GetApps(refresh: true))
        { token.ThrowIfCancellationRequested(); if (!HasSession(app)) continue; ++result.Total; if (SetMute(app, target)) ++result.Succeeded; }
        return result;
    }
    internal static bool AllMuted()
    {
        var states = GetApps().Select(ReadStateChecked).Where(s => s.MuteKnown).ToArray();
        return states.Length > 0 && states.All(s => s.Muted);
    }
    internal static (bool Muted, bool Applied) ToggleAllMuteChecked()
    {
        bool target = !AllMuted(); var result = ChangeAllPlaybackMuteChecked(target);
        return (target, result.Total > 0 && result.Succeeded == result.Total);
    }
    internal static (int OutOk, int InOk, int Total, bool Applied) ResetAllPersistedEndpointsChecked()
    {
#if SONICROUTE_PHONE
        int phoneStopped = AndroidAudioHost.StopAllPcAppsToPhone(restorePreviousEndpoint: true);
#else
        const int phoneStopped = 0;
#endif
        var native = AudioService.ResetAllPersistedEndpointsChecked();
        var external = MergeExternal(Array.Empty<AudioAppInfo>());
        int changed = external.Count(app => ApplyEndpoint(app, EDataFlow.eRender, AudioService.SystemDefaultDeviceId).Success);
        return (native.OutOk + changed + phoneStopped, native.InOk, native.Total + external.Count, native.Applied && changed == external.Count);
    }
}

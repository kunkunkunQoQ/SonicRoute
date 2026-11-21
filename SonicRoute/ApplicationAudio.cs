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
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
#endif

namespace SonicRoute;

/// <summary>Shared application targets for UI, automation, hotkeys and tray. Native APIs only receive real PIDs.</summary>
internal static class ApplicationAudio
{
#if SONICROUTE_PHONE
    static ApplicationAudio() => AppDisplayName.ExternalNameResolver = ResolveExternalDisplayName;
    internal static void EnsureNameResolver() => AppDisplayName.ExternalNameResolver = ResolveExternalDisplayName;

    private static string? ResolveExternalDisplayName(string identifier)
    {
        if (string.Equals(identifier, AirPlayHost.AppKey, StringComparison.OrdinalIgnoreCase)) return "AirPlay";
        const string prefix = "android:";
        if (string.IsNullOrWhiteSpace(identifier) || !identifier.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return AndroidAudioHost.TryResolveAndroidName(identifier.Substring(prefix.Length).Trim());
    }
#endif

    internal static string FormatAppIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return "";
        const string prefix = "android:";
        if (!identifier!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return identifier;
        string package = identifier.Substring(prefix.Length).Trim();
#if SONICROUTE_PHONE
        var customNames = ConfigService.Load().AppNames;
        if (customNames.TryGetValue(identifier, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;
        string? resolved = AndroidAudioHost.TryResolveAndroidName(package);
        if (!string.IsNullOrWhiteSpace(resolved)) return resolved!;
#endif
        if (package.Length == 0 || string.Equals(package, "sonicroute:device-audio", StringComparison.OrdinalIgnoreCase))
            return "Phone";
        return package;
    }

    internal static event Action? Changed;
    internal static event Action? OutputsChanged;
#if SONICROUTE_PHONE
    internal static event Action<AudioUiChange>? UiChanged;
    internal static void NotifySourceChanged(string? id) =>
        PublishChange(new(id == null ? AudioUiScope.SourceList : AudioUiScope.SourceState, id));
    private static void PublishChange(AudioUiChange change)
    {
        if (UiChanged is { } subscribers)
            foreach (Action<AudioUiChange> listener in subscribers.GetInvocationList())
                try { listener(change); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex.Message); }
        PublishChanged();
    }
#endif
    internal static void NotifyRouteChanged(int pid)
    {
#if SONICROUTE_PHONE
        PublishChange(new(AudioUiScope.Routes, Pid: pid));
#else
        PublishChanged();
#endif
    }
    internal static void NotifyChanged()
    {
#if SONICROUTE_PHONE
        PublishChange(AudioUiChange.All);
#else
        PublishChanged();
#endif
    }
    private static void PublishChanged()
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
#if SONICROUTE_PHONE
        PublishChange(new(AudioUiScope.Outputs));
#else
        PublishChanged();
#endif
    }
    internal static bool IsPhoneOutputDevice(string? deviceId) =>
#if SONICROUTE_PHONE
        AndroidOutputIdentity.IsPhoneOutput(deviceId);
#else
        false;
#endif
    internal static bool IsExternalOutputDevice(string? deviceId) => IsPhoneOutputDevice(deviceId);

    internal static List<AudioDeviceInfo> GetOutputDevices() =>
        MergeExternalOutputs(AudioService.GetDevices(EDataFlow.eRender));
    internal static List<AudioDeviceInfo> MergeExternalOutputs(IEnumerable<AudioDeviceInfo> windowsOutputs)
    {
        var list = windowsOutputs.Where(d => !IsExternalOutputDevice(d.Id)).ToList();
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
        if (app is { IsExternal: true })
        {
            if (app.ProcessName == AirPlayHost.AppKey) return outputs;
            var source = Source(app);
            if (source == null) return outputs.Where(d => !IsExternalOutputDevice(d.Id)).ToList();
            var phoneOutputs = AndroidAudioHost.OutputsForDevice(source.Device);
            var remaining = new Dictionary<string, AudioDeviceInfo>(StringComparer.OrdinalIgnoreCase);
            // The source handset can keep playing natively. Give its current route one button;
            // players on other handsets need the address of their own media output.
            foreach (var phone in phoneOutputs)
                if (!AndroidOutputIdentity.IsPhoneCurrentOutput(phone.Id)) remaining.TryAdd(phone.Id, phone);
            var config = ConfigService.Load();
            var list = new List<AudioDeviceInfo>();
            bool addedNative = false;
            void AddNative()
            {
                if (addedNative) return;
                string currentId = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(source.Device), AndroidOutputSink.Current.Key);
                const string nativeId = AndroidAudioSession.SourcePhoneOutputId;
                list.Add(config.DeviceNames.TryGetValue(currentId, out var name) && !string.IsNullOrWhiteSpace(name)
                    ? new AudioDeviceInfo { Id = nativeId, DisplayName = name, Flow = EDataFlow.eRender }
                    : new PanelDevices.LocalizedDevice("Phone.NativeAudioOutput") { Id = nativeId, Flow = EDataFlow.eRender });
                addedNative = true;
            }
            foreach (var device in outputs)
            {
                if (!IsPhoneOutputDevice(device.Id)) { list.Add(device); continue; }
                if (IsSourcePhoneOutput(source, device.Id)) AddNative();
                else if (AndroidOutputIdentity.IsPhoneCurrentOutput(device.Id)) { list.Add(device); continue; }
                if (remaining.Remove(device.Id, out var phone)) list.Add(PanelDevices.WithDeviceName(phone, config));
            }
            AddNative();
            // A live source can retain access to its own outputs during device-discovery changes.
            foreach (var phone in phoneOutputs)
                if (remaining.Remove(phone.Id)) list.Add(PanelDevices.WithDeviceName(phone, config));
            return list;
        }
#endif
        return outputs;
    }
    internal static List<AudioAppInfo> GetApps(bool refresh = false) => MergeExternal(AudioService.GetApps(refresh));
    internal static bool HasExternal =>
#if SONICROUTE_PHONE
        AirPlayHost.Source != null || (AndroidAudioHost.IsCreated && AndroidAudioHost.Sources.Sources.Count > 0);
#else
        false;
#endif
    internal static string? ForegroundAppName(int pid) => ForegroundAppName(pid, 0);

    internal static string? ForegroundAppName(int pid, nint hwnd)
    {
#if SONICROUTE_PHONE
        if (AndroidAudioHost.AreDisplaysCreated)
        {
            nint targetHwnd = hwnd;
            if (targetHwnd == 0)
            {
                nint live = GetForegroundWindow();
                if (live != 0 && GetWindowThreadProcessId(live, out uint livePid) != 0 && (int)livePid == pid)
                    targetHwnd = live;
            }
            if (AndroidAudioHost.Displays.FindApplication(targetHwnd, pid) is { } display)
                return AndroidKey(display.App.Package);
        }
#endif
        return ForegroundAppService.GetProcessNameSafe(pid);
    }

#if SONICROUTE_PHONE
    internal static nint GetForegroundWindowHandle() => GetForegroundWindow();

    internal static (int Pid, nint Hwnd) GetForegroundWindowContext()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0) return (0, 0);
        GetWindowThreadProcessId(hwnd, out uint pid);
        return ((int)pid, hwnd);
    }

    internal static bool IsVirtualDisplayWindow(nint hwnd)
        => hwnd != 0 && AndroidAudioHost.AreDisplaysCreated && AndroidAudioHost.Displays.OwnsWindow(hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
#endif
    internal static async Task<List<AudioAppInfo>> GetAppsAsync(bool refresh = false) => MergeExternal(await AudioService.GetAppsAsync(refresh).ConfigureAwait(false));
    internal static List<AudioAppInfo> MergeExternal(IEnumerable<AudioAppInfo> apps)
    {
        var result = apps.Where(a => !a.IsExternal).ToList();
#if SONICROUTE_PHONE
        if (AndroidAudioHost.IsCreated) result.AddRange(AndroidAudioHost.Sources.Sources.Select(Describe));
        if (AirPlayHost.Source is { } airPlay) result.Add(Describe(airPlay));
#endif
        return result;
    }
#if SONICROUTE_PHONE
    internal static string AndroidKey(string package) => "android:" + package;
    internal static string AndroidName(AndroidApp app, AndroidDevice? device = null) => app.IsDeviceAudio && device != null
        ? AndroidAudioHost.Phone.GetDisplayName(device) : app.Name;
    internal static AudioAppInfo Describe(AndroidAudioSource source) => new()
    {
        ProcessId = 0, ExternalSourceId = source.Id, ProcessName = AndroidKey(source.App.Package),
        DisplayName = AndroidName(source.App, source.Device), SourceLabel = string.Format(L10n.T("Phone.FromDevice"), AndroidAudioHost.Phone.GetDisplayName(source.Device)), ExternalIconPng = source.App.IconPng,
        HasActiveSession = source.Session.State.State == CaptureState.Receiving
    };
    internal static AudioAppInfo Describe(AirPlayAudioOutput source) => new()
    {
        ProcessId = 0, ExternalSourceId = source.Id, ProcessName = AirPlayHost.AppKey,
        DisplayName = source.Name, SourceLabel = "AirPlay", HasActiveSession = source.Pcm != null
    };
    private static AndroidAudioSource? Source(AudioAppInfo app) => !app.IsExternal || !AndroidAudioHost.IsCreated ? null
        : AndroidAudioHost.Sources.FindSource(app.ExternalSourceId!);
    internal static IPhoneAudioSession? ExternalSession(AudioAppInfo app) => Source(app)?.Session
        ?? (IPhoneAudioSession?)AirPlayHost.Find(app.ExternalSourceId);
    private static bool IsSourcePhoneOutput(AndroidAudioSource source, string deviceId) =>
        AndroidOutputIdentity.TryParseDeviceId(deviceId, out string phoneKey, out _)
        && string.Equals(phoneKey, AndroidDeviceIdentity.Key(source.Device), StringComparison.OrdinalIgnoreCase);
#endif
    internal static (int Volume, bool Muted, bool MuteKnown) ReadStateChecked(AudioAppInfo app)
    {
        if (!app.IsExternal) return SessionVolumeService.ReadStateChecked((int)app.ProcessId);
#if SONICROUTE_PHONE
        var state = ExternalSession(app)?.State;
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
        var session = ExternalSession(app);
        if (session?.State.State == CaptureState.Receiving)
        { session.SetVolume(percent); return (session.State.Volume == MathEx.Clamp(percent, 0, 100), session.State.Volume); }
#endif
        return (false, -1);
    }
    internal static (int Current, int Requested, int Actual, bool Applied) AdjustVolumePercent(AudioAppInfo app, int delta)
    {
        if (!app.IsExternal) return SessionVolumeService.AdjustVolumePercent((int)app.ProcessId, delta);
#if SONICROUTE_PHONE
        var session = ExternalSession(app);
        if (session == null) return (-1, -1, -1, false);
        lock (session)
        {
            var state = session.State;
            int current = state.State == CaptureState.Receiving ? state.Volume : -1;
            int requested = MathEx.Clamp(current + delta, 0, 100);
            if (current < 0) return (current, requested, -1, false);
            session.SetVolume(requested);
            int actual = session.State.Volume;
            return (state.Volume, requested, actual, actual == requested);
        }
#else
        return (-1, -1, -1, false);
#endif
    }
    internal static bool SetMute(AudioAppInfo app, bool muted)
    {
        if (!app.IsExternal) return SessionVolumeService.SetMute((int)app.ProcessId, muted);
#if SONICROUTE_PHONE
        var session = ExternalSession(app);
        if (session?.State.State == CaptureState.Receiving)
        { session.SetMute(muted); return session.State.State == CaptureState.Receiving && session.State.Muted == muted; }
#endif
        return false;
    }
    internal static (bool Muted, bool Applied) ToggleMuteChecked(AudioAppInfo app)
    {
        if (!app.IsExternal) return SessionVolumeService.ToggleMuteChecked((int)app.ProcessId);
#if SONICROUTE_PHONE
        var session = ExternalSession(app);
        if (session == null) return (false, false);
        lock (session)
        {
            var state = session.State; bool target = !state.Muted;
            if (state.State != CaptureState.Receiving) return (true, false);
            session.SetMute(target);
            return (target, session.State.State == CaptureState.Receiving && session.State.Muted == target);
        }
#else
        return (false, false);
#endif
    }
    internal static string? GetPhoneRoutedDeviceId(int processId) =>
#if SONICROUTE_PHONE
        AndroidAudioHost.TryGetPcAppPhoneOutput(processId) ?? LocalAppOutputMirrors.GetExclusivePhone(processId, out _);
#else
        null;
#endif
    internal static string? GetPersistedEndpoint(AudioAppInfo app, EDataFlow flow) =>
        !app.IsExternal ? GetPersistedEndpoint((int)app.ProcessId, flow) : GetExternalEndpoint(app, flow);

    internal static string? GetPersistedEndpoint(int processId, EDataFlow flow)
    {
#if SONICROUTE_PHONE
        if (flow == EDataFlow.eRender)
        {
            if (GetPhoneRoutedDeviceId(processId) is { } phoneOut) return phoneOut;
        }
#endif
        return AudioService.GetPersistedEndpoint(processId, flow);
    }

    private static string? GetExternalEndpoint(AudioAppInfo app, EDataFlow flow)
    {
#if SONICROUTE_PHONE
        if (flow == EDataFlow.eRender) return ExternalSession(app)?.State.OutputId;
#endif
        return null;
    }

    internal static string GetExpectedOutputId(AudioAppInfo app, string deviceId)
    {
#if SONICROUTE_PHONE
        if (AndroidOutputIdentity.IsPhoneCurrentOutput(deviceId) && Source(app) is { } source && IsSourcePhoneOutput(source, deviceId))
            return AndroidAudioSession.SourcePhoneOutputId;
#endif
        return deviceId;
    }

    internal static (bool Success, int HResult, string Message) ApplyEndpoint(AudioAppInfo app, EDataFlow flow, string deviceId)
    {
        if (!app.IsExternal)
        {
            if (flow == EDataFlow.eRender)
            {
                bool managed = LocalAppOutputMirrors.GetExtraOutputs((int)app.ProcessId).Length != 0;
#if SONICROUTE_PHONE
                managed |= AppPlaybackOutputs.IsManaged((int)app.ProcessId) || AndroidOutputIdentity.IsPhoneOutput(deviceId);
#endif
                if (managed || AppPlaybackRestore.HasSavedChoice((int)app.ProcessId))
                    return AppPlaybackOutputs.SetAsync(app, [deviceId], deviceId).GetAwaiter().GetResult();
#if SONICROUTE_PHONE
                AndroidAudioHost.StopPcAppToPhone((int)app.ProcessId, restorePreviousEndpoint: false);
#endif
            }
            var result = AudioService.ApplyEndpoint((int)app.ProcessId, flow, deviceId);
            if (!result.Success)
                result.Message = string.Format(L10n.T("Dev.RouteFailed"), result.Message);
            return result;
        }
#if SONICROUTE_PHONE
        // Synchronous callers (automation/hotkeys/tray) already execute on workers.
        return ApplyExternalEndpointAsync(app, flow, deviceId).GetAwaiter().GetResult();
#else
        return (false, -1, L10n.T("Phone.Err.NoAudioRouteOrEnded"));
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
        if (flow == EDataFlow.eRender && AirPlayHost.Find(app.ExternalSourceId) is { } airPlay)
        {
            try
            {
                await airPlay.SetOutputAsync(AudioService.IsSystemDefault(deviceId) ? null : deviceId).ConfigureAwait(false);
                return (true, 0, "");
            }
            catch (Exception ex)
            {
                string message = L10n.T("Phone.AirPlayAudioError");
                if (IsPhoneOutputDevice(deviceId))
                    message = AndroidError.TryGetCode(ex, out var code) ? AndroidErrorTranslation.Format(code, ex.Message)
                        : L10n.T("Phone.Err.PhoneAudioOutputFailed");
                return (false, ex.HResult, message);
            }
        }
        var source = Source(app);
        if (source != null && flow == EDataFlow.eRender)
        {
            try
            {
                if (AndroidOutputIdentity.IsPhoneOutput(deviceId))
                {
                    bool own = IsSourcePhoneOutput(source, deviceId);
                    bool current = AndroidOutputIdentity.IsPhoneCurrentOutput(deviceId);
                    if (!own && !current) return (false, -1, L10n.T("Phone.Err.SelectSourcePhoneOutput"));
                    await source.Session.SetOutputAsync(own && current ? AndroidAudioSession.SourcePhoneOutputId : deviceId).ConfigureAwait(false);
                }
                else await source.Session.SetOutputAsync(AudioService.IsSystemDefault(deviceId) ? null : deviceId).ConfigureAwait(false);
                return (true, 0, "");
            }
            catch (Exception ex) { return (false, ex.HResult, AndroidErrorTranslation.Format(ex)); }
        }
        return (false, -1, L10n.T("Phone.Err.NoAudioRouteOrEnded"));
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
        var result = ApplyAllPlaybackMute(muted, token);
        return (result.Succeeded, result.Total);
    }
    private static (bool Muted, int Succeeded, int Total) ApplyAllPlaybackMute(bool? muted, CancellationToken token)
    {
        // One target list and one state read per app for both the decision and the write.
        // Unknown mute states remain unknown; a read failure must not turn into "all muted".
        token.ThrowIfCancellationRequested();
        var apps = GetApps(refresh: true);
        var states = new List<(AudioAppInfo App, int Volume)>(apps.Count);
        bool anyKnown = false, allMuted = true;
        foreach (var app in apps)
        {
            token.ThrowIfCancellationRequested();
            var state = ReadStateChecked(app);
            states.Add((app, state.Volume));
            if (state.MuteKnown) { anyKnown = true; allMuted &= state.Muted; }
        }
        bool target = muted ?? !(anyKnown && allMuted);
        (int Succeeded, int Total) result = (0, 0);
        foreach (var state in states)
        {
            token.ThrowIfCancellationRequested(); if (state.Volume < 0) continue; ++result.Total;
            if (SetMute(state.App, target)) ++result.Succeeded;
        }
        return (target, result.Succeeded, result.Total);
    }
    internal static bool CombineExternalMute(bool nativeMuted)
    {
        if (!nativeMuted) return false;
#if SONICROUTE_PHONE
        if (AndroidAudioHost.IsCreated)
            foreach (var source in AndroidAudioHost.Sources.Sources)
                if (source.Session.State is { State: CaptureState.Receiving, Muted: false }) return false;
        if (AirPlayHost.Source?.State is { State: CaptureState.Receiving, Muted: false }) return false;
#endif
        return true;
    }
    internal static bool AllMuted()
    {
        var states = GetApps().Select(ReadStateChecked).Where(s => s.MuteKnown).ToArray();
        return states.Length > 0 && states.All(s => s.Muted);
    }
    internal static (bool Muted, bool Applied) ToggleAllMuteChecked()
    {
        var result = ApplyAllPlaybackMute(null, default);
        return (result.Muted, result.Total > 0 && result.Succeeded == result.Total);
    }
    internal static (int OutOk, int InOk, int Total, bool Applied) ResetAllPersistedEndpointsChecked()
    {
        bool cleared = AppPlaybackRestore.ClearAsync().GetAwaiter().GetResult();
        LocalAppOutputMirrors.ClearAllAsync().GetAwaiter().GetResult();
#if SONICROUTE_PHONE
        AppPlaybackOutputs.ForgetAll();
        int phoneStopped = AndroidAudioHost.StopAllPcAppsToPhone(restorePreviousEndpoint: true);
#else
        const int phoneStopped = 0;
#endif
        var native = AudioService.ResetAllPersistedEndpointsChecked();
        var external = MergeExternal(Array.Empty<AudioAppInfo>());
        int changed = external.Count(app => ApplyEndpoint(app, EDataFlow.eRender, AudioService.SystemDefaultDeviceId).Success);
        return (native.OutOk + changed + phoneStopped, native.InOk, native.Total + external.Count, cleared && native.Applied && changed == external.Count);
    }
}

#if SONICROUTE_PHONE
[Flags]
internal enum AudioUiScope { None = 0, SourceState = 1, SourceList = 2, Routes = 4, Outputs = 8 }

internal readonly record struct AudioUiChange(AudioUiScope Scope, string? SourceId = null, int Pid = 0)
{
    internal static readonly AudioUiChange All = new(AudioUiScope.SourceList | AudioUiScope.Routes | AudioUiScope.Outputs);
    internal bool Has(AudioUiScope scope) => (Scope & scope) != 0;

    // Multiple sources or PIDs widen only their part of the pending update.
    internal AudioUiChange Merge(AudioUiChange next)
    {
        string? source = Has(AudioUiScope.SourceState) ? SourceId : next.SourceId;
        if (Has(AudioUiScope.SourceState) && next.Has(AudioUiScope.SourceState) && source != next.SourceId) source = null;
        int pid = Has(AudioUiScope.Routes) ? Pid : next.Pid;
        if (Has(AudioUiScope.Routes) && next.Has(AudioUiScope.Routes) && pid != next.Pid) pid = 0;
        return new(Scope | next.Scope, source, pid);
    }
}

internal sealed class PendingAudioUiChange
{
    private AudioUiChange _pending;
    internal bool Add(AudioUiChange change)
    {
        lock (this)
        {
            bool post = _pending.Scope == AudioUiScope.None;
            _pending = _pending.Merge(change);
            return post;
        }
    }
    internal AudioUiChange Take()
    {
        lock (this) { var change = _pending; _pending = default; return change; }
    }
}
#endif

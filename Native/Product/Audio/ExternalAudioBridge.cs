#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Product.Views;

namespace SonicRoute.Native.Product;

/// <summary>
/// Native AOT Full-edition bridge connecting PhonePreview / AirPlayPreview external sessions and
/// PC-to-phone audio routing into ProductAudio, ProductRouting, and ProductCurrentApp without WPF/NAudio.
/// </summary>
internal sealed class ExternalAudioBridge : IDisposable
{
    private readonly Func<PhonePreview?> _tryGetPhone;
    private readonly Func<PhonePreview> _getPhone;
    private readonly Func<AirPlayPreview?> _tryGetAirPlay;
    private readonly Action _onChanged;
    private readonly Func<IEnumerable<string>, CancellationToken, Task>? _prepareSaved;
    private readonly AndroidAudioSettingsStore? _preferences;
    internal PhonePreview? ExistingPhone => _tryGetPhone();
    internal const string AirPlaySourcePrefix = ExternalAudioPreference.AirPlayPrefix;

    private static string AirPlayVisibilityKey(AirPlayStatus status) =>
        ExternalAudioPreference.AirPlayKey(status.SenderId, status.Sender!);

    internal AudioDeviceInfo? ConnectedAirPlaySender()
    {
        var status = _tryGetAirPlay()?.Status;
        return status is { Stage: AirPlayStage.Connected, Sender: { Length: > 0 } sender }
            ? new AudioDeviceInfo { Id = AirPlayVisibilityKey(status), DisplayName = sender, Flow = EDataFlow.eRender }
            : null;
    }

    internal bool IsQuickPanelSourceVisible(AudioAppInfo app, AppConfig config)
    {
        if (app.ProcessName is { } process && config.HiddenPanelApps.Contains(process, StringComparer.OrdinalIgnoreCase)) return false;
        if (!string.Equals(app.ProcessName, "airplay:receiver", StringComparison.OrdinalIgnoreCase)) return true;
        if (app.ExternalPreferenceKey is { } key) return config.IsDeviceShown(EDataFlow.eRender, key);
        var receiver = _tryGetAirPlay();
        if (receiver?.Status is not { Stage: AirPlayStage.Connected } status || receiver.Source?.Id != app.ExternalSourceId)
            return false;
        return config.IsDeviceShown(EDataFlow.eRender, AirPlayVisibilityKey(status));
    }

    internal IEnumerable<AudioDeviceInfo> AirPlaySenders()
    {
        var connected = ConnectedAirPlaySender();
        foreach (var choice in _preferences?.ExternalAudio ?? [])
            if (choice.App == null && choice.Visible && choice.Key != connected?.Id)
                yield return new AudioDeviceInfo { Id = choice.Key, DisplayName = choice.Name, Flow = EDataFlow.eRender };
        if (connected != null) yield return connected;
    }

    internal Task PrepareSavedPhoneOutputsAsync(IEnumerable<string> outputs, CancellationToken token) =>
        _prepareSaved?.Invoke(outputs, token) ?? Task.CompletedTask;

    internal ExternalAudioBridge(
        Func<PhonePreview?> tryGetPhone,
        Func<PhonePreview> getPhone,
        Func<AirPlayPreview?> tryGetAirPlay,
        Action onChanged, Func<IEnumerable<string>, CancellationToken, Task>? prepareSaved = null,
        AndroidAudioSettingsStore? preferences = null)
    {
        _tryGetPhone = tryGetPhone;
        _getPhone = getPhone;
        _tryGetAirPlay = tryGetAirPlay;
        _onChanged = onChanged;
        _prepareSaved = prepareSaved;
        _preferences = preferences;
        AppDisplayName.ExternalNameResolver = ResolveExternalDisplayName;
    }

    private string? ResolveExternalDisplayName(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return null;
        string pkg = identifier.StartsWith("android:", StringComparison.OrdinalIgnoreCase)
            ? identifier["android:".Length..]
            : identifier;
        var phone = _tryGetPhone();
        if (phone != null)
        {
            foreach (var src in phone.Sources.Sources)
            {
                if (string.Equals(src.App.Package, pkg, StringComparison.OrdinalIgnoreCase))
                    return src.App.IsDeviceAudio ? phone.Store.GetDisplayName(src.Device) : src.App.Name;
            }
            if (phone.Displays.FindByPackage(pkg) is { } display)
                return display.App.IsDeviceAudio ? phone.Store.GetDisplayName(display.Device) : display.App.Name;
            foreach (var p in phone.Store.Profiles)
            {
                var found = p.RetainedApps.FirstOrDefault(a => string.Equals(a.Package, pkg, StringComparison.OrdinalIgnoreCase));
                if (found != null) return found.IsDeviceAudio ? phone.Store.GetProfileDisplayName(p) : found.Name;
            }
        }
        return null;
    }

    internal void AppendExternalApps(List<AudioAppInfo> result, bool includeOffline = true)
    {
        var phone = _tryGetPhone();
        var airPlay = _tryGetAirPlay()?.Source;
        var sources = phone?.Sources.Sources ?? [];
        if (!includeOffline)
        {
            foreach (var source in sources)
                if (source.Session.State.State == CaptureState.Receiving)
                    result.Add(AndroidCard(source.App, phone!.Store.GetDisplayName(source.Device),
                        ExternalAudioPreference.AndroidKey(source.Device, source.App.Package), source));
            if (airPlay != null) result.Add(AirPlayCard(airPlay.Name, airPlay.PreferenceKey, airPlay));
            return;
        }
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var choice in _preferences?.ExternalAudio ?? phone?.Settings.ExternalAudio ?? [])
        {
            if (!choice.Visible) continue;
            if (choice.App is { } app)
            {
                var profile = phone?.Store.Profiles.FirstOrDefault(p => AndroidDeviceIdentity.StableKey(p.LastDevice) == choice.DeviceKey);
                if (profile == null || phone == null) continue;
                var live = sources.FirstOrDefault(source => source.App.Package == app.Package &&
                    AndroidDeviceIdentity.MatchesSavedDevice(profile.LastDevice, source.Device));
                string name = phone.Store.GetProfileDisplayName(profile);
                result.Add(AndroidCard(live?.App ?? app, name, choice.Key, live));
                if (live != null) added.Add(live.Id);
            }
            else
            {
                var live = airPlay?.PreferenceKey == choice.Key ? airPlay : null;
                result.Add(AirPlayCard(live?.Name ?? choice.Name, choice.Key, live));
                if (live != null) added.Add(live.Id);
            }
        }
        if (phone != null)
        {
            foreach (var source in sources)
                if (!added.Contains(source.Id))
                    result.Add(AndroidCard(source.App, phone.Store.GetDisplayName(source.Device),
                        ExternalAudioPreference.AndroidKey(source.Device, source.App.Package), source));
        }
        if (airPlay != null && !added.Contains(airPlay.Id))
            result.Add(AirPlayCard(airPlay.Name, airPlay.PreferenceKey, airPlay));
    }

    private static AudioAppInfo AndroidCard(AndroidApp app, string phoneName, string? key, AndroidAudioSource? live) =>
        new()
        {
            ProcessId = 0, ExternalSourceId = live?.Id ?? key, ExternalPreferenceKey = key,
            ProcessName = "android:" + app.Package, DisplayName = app.IsDeviceAudio ? phoneName : app.Name,
            SourceLabel = string.Format(L10n.T("Phone.FromDevice"), phoneName), ExternalIconPng = app.IconPng,
            HasActiveSession = live?.Session.State.State == CaptureState.Receiving,
            IsExternalOffline = live?.Session.State.State != CaptureState.Receiving,
        };

    private static AudioAppInfo AirPlayCard(string name, string? key, AirPlayAudioOutput? live) =>
        new()
        {
            ProcessId = 0, ExternalSourceId = live?.Id ?? key, ExternalPreferenceKey = key, ProcessName = "airplay:receiver",
            DisplayName = name, SourceLabel = "AirPlay", HasActiveSession = live != null, IsExternalOffline = live == null,
        };

    private ExternalAudioPreference? FindPreference(AudioAppInfo app) =>
        (_preferences ?? _tryGetPhone()?.Settings)?.FindExternalAudio(app.ExternalPreferenceKey);

    internal async Task StopExternalSourceAsync(AudioAppInfo app)
    {
        string? id = app.ExternalSourceId;
        if (id == null) return;
        var phone = _tryGetPhone();
        if (phone?.Sources.FindSource(id) is { } live)
            await phone.Sources.RemoveAsync(live.Id).ConfigureAwait(false);
        else if (_tryGetAirPlay() is { } airPlay && airPlay.Source is { } sender
            && (sender.Id == id || sender.PreferenceKey != null && sender.PreferenceKey == app.ExternalPreferenceKey))
            airPlay.Disconnect(sender.Id);
        else if (FindPreference(app) is { } choice)
        {
            if (choice.App != null && phone?.Store.Profiles.FirstOrDefault(p => AndroidDeviceIdentity.StableKey(p.LastDevice) == choice.DeviceKey)
                ?.LastDevice is { } device)
                await phone.Sources.RemoveApplicationAsync(device, choice.App.Package).ConfigureAwait(false);
            else _preferences?.StopExternalAudio(choice.Key);
            phone?.QueueSourceReconciliation(refreshRoutes: false);
            _onChanged();
        }
    }

    internal static string?[] ResolveAvailableOutputs(string?[] desired)
    {
        if (desired.Length == 1 && desired[0] == null) return desired;
        HashSet<string>? windows = null;
        var active = new List<string?>(desired.Length);
        foreach (string? id in desired)
        {
            if (id == null || id == AndroidAudioSession.SourcePhoneOutputId) active.Add(id);
            else if (AndroidOutputIdentity.IsPhoneOutput(id))
            {
                if (PcRoutingApi.ResolvePersistentOutputId(id) is { } available) active.Add(available);
            }
            else
            {
                if (windows == null)
                {
                    try { windows = ProductAudio.Devices(Flow.Render).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase); }
                    catch (Exception error)
                    {
                        ExceptionDiagnostics.Record("ExternalAudio.ResolveOutputs", error);
                        windows = new(StringComparer.OrdinalIgnoreCase);
                    }
                }
                var endpoint = AudioPolicyConfig.UnpackDeviceId(id);
                if (windows.Contains(endpoint)) active.Add(endpoint);
            }
        }
        return active.Count == 0 ? [null] : active.ToArray();
    }

    internal IPhoneAudioSession? FindSession(string? externalSourceId)
    {
        if (string.IsNullOrWhiteSpace(externalSourceId)) return null;
        if (_tryGetPhone()?.Sources.FindSource(externalSourceId) is { } src)
            return src.Session;
        if (_tryGetAirPlay()?.Source is { } ap && string.Equals(ap.Id, externalSourceId, StringComparison.Ordinal))
            return ap;
        return null;
    }

    internal static event Action? PeakActivity
    {
        add => AndroidPcmSource.PeakActivity += value;
        remove => AndroidPcmSource.PeakActivity -= value;
    }

    internal (bool Active, float Peak) ReadPeak(string? sourceId)
    {
        var session = FindSession(sourceId);
        return session?.State is { State: CaptureState.Receiving, Muted: false, Volume: > 0 } && session.Pcm is { } pcm
            ? pcm.ReadMeterPeak() : (false, 0f);
    }

    internal VolumeState ReadExternalVolume(AudioAppInfo app)
    {
        var session = FindSession(app.ExternalSourceId);
        if (session == null || session.State.State != CaptureState.Receiving)
            return FindPreference(app) is { } saved ? new(saved.Volume, saved.Muted, 0) : new(-1, false, 0);
        return new(session.State.Volume, session.State.Muted, 1);
    }

    internal VolumeState SetExternalVolume(string? sourceId, int? level = null, bool? mute = null, int delta = 0, bool toggle = false)
    {
        var session = FindSession(sourceId)
            ?? throw new InvalidOperationException(L10n.T("Ov.NoOutputSession"));
        lock (session)
        {
            var state = session.State;
            if (state.State != CaptureState.Receiving)
                throw new InvalidOperationException(L10n.T("Ov.NoOutputSession"));
            if (level.HasValue || delta != 0)
            {
                int targetVol = Math.Clamp(level ?? (state.Volume + delta), 0, 100);
                session.SetVolume(targetVol);
            }
            if (mute.HasValue || toggle)
            {
                bool targetMute = mute ?? !state.Muted;
                session.SetMute(targetMute);
            }
            var after = session.State;
            _onChanged();
            return new(after.Volume, after.Muted, 1);
        }
    }

    internal string[] GetExternalOutputs(AudioAppInfo app)
    {
        var session = FindSession(app.ExternalSourceId);
        if (session?.State.State != CaptureState.Receiving && FindPreference(app) is { } saved)
            return saved.Outputs.Select(id => id ?? AudioService.SystemDefaultDeviceId).ToArray();
        if (session is IMultiOutputAudioSession multi)
        {
            var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? id in multi.GetOutputs())
                list.Add(string.IsNullOrWhiteSpace(id) || AudioService.IsSystemDefault(id)
                    ? AudioService.SystemDefaultDeviceId
                    : AudioPolicyConfig.UnpackDeviceId(id));
            if (multi is AndroidAudioSession android && android.UsesSourcePhonePlayback)
                list.Add(AndroidAudioSession.SourcePhoneOutputId);
            if (list.Count == 0) list.Add(AudioService.SystemDefaultDeviceId);
            return list.ToArray();
        }
        string? single = session?.State.OutputId;
        return [string.IsNullOrWhiteSpace(single) ? AudioService.SystemDefaultDeviceId : AudioPolicyConfig.UnpackDeviceId(single)];
    }

    // Settings and all-app commands use a device-qualified address, while the
    // source handset's session records its own native playback as a local alias.
    internal string? AddressedOutput(AudioAppInfo app, string? id)
    {
        if (id == AndroidAudioSession.SourcePhoneOutputId && FindSession(app.ExternalSourceId)?.State.Device is { } device)
            return AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), AndroidOutputSink.Current.Key);
        if (id == AndroidAudioSession.SourcePhoneOutputId && FindPreference(app)?.DeviceKey is { } key)
            return AndroidOutputIdentity.FormatDeviceId(key, AndroidOutputSink.Current.Key);
        return id;
    }

    internal async Task<ControlReply> SetExternalOutputAsync(AudioAppInfo app, string id)
    {
        var session = FindSession(app.ExternalSourceId);
        if (session == null) return new("error: external_session_unavailable");
        try
        {
            await session.SetOutputAsync(AudioService.IsSystemDefault(id) ? null : id).ConfigureAwait(false);
            _onChanged();
            var state = session.State;
            if (!ReferenceEquals(session, FindSession(app.ExternalSourceId)) || state.State != CaptureState.Receiving)
                return new("error: external_session_unavailable");
            string? actual = AddressedOutput(app, state.OutputId ?? AudioService.SystemDefaultDeviceId);
            return string.Equals(actual, AddressedOutput(app, id), StringComparison.OrdinalIgnoreCase)
                ? new("ok") : new("error: output_readback_mismatch");
        }
        catch (Exception ex) { return new("error: " + ex.Message); }
    }

    internal async Task<ControlReply> SetExternalOutputsAsync(AudioAppInfo app, string[] outputs)
    {
        var session = FindSession(app.ExternalSourceId);
        if (session is not IMultiOutputAudioSession multi)
            return new("error: external_session_unavailable");
        try
        {
            var normalized = outputs
                .Select(id => string.IsNullOrWhiteSpace(id) || AudioService.IsSystemDefault(id) ? null : id)
                .ToArray();
            if (normalized.Length == 0) normalized = [null];
            await multi.SetOutputsAsync(normalized).ConfigureAwait(false);
            _onChanged();
            if (!ReferenceEquals(session, FindSession(app.ExternalSourceId)) || session.State.State != CaptureState.Receiving)
                return new("error: external_session_unavailable");
            var requested = normalized.Select(id => AddressedOutput(app, id ?? AudioService.SystemDefaultDeviceId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return requested.SetEquals(GetExternalOutputs(app).Select(id => AddressedOutput(app, id)))
                ? new("ok") : new("error: output_readback_mismatch");
        }
        catch (Exception ex)
        {
            return new("error: " + ex.Message);
        }
    }

    internal Endpoint[] GetPhoneOutputs(AudioAppInfo? menuTarget = null, bool includeCurrent = false)
    {
        var phone = _tryGetPhone();
        if (phone == null) return [];
        var source = menuTarget?.ExternalSourceId is { } sourceId ? phone.Sources.FindSource(sourceId) : null;
        if (menuTarget is { IsExternal: true } && source == null && _tryGetAirPlay()?.Source?.Id != menuTarget.ExternalSourceId) return [];
        var devices = phone.Connections.OnlineDevices.Where(d => !d.IsApp).ToArray();
        var list = new List<Endpoint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = menuTarget != null || includeCurrent ? ConfigService.Load().DeviceNames : null;
        void AddCurrent(string key, bool own, string phoneName)
        {
            string current = AndroidOutputIdentity.FormatDeviceId(key, AndroidOutputSink.Current.Key);
            string? alias = aliases?.GetValueOrDefault(current);
            string name = string.IsNullOrWhiteSpace(alias) ? L10n.T("Phone.NativeAudioOutput") : alias;
            if (string.IsNullOrWhiteSpace(alias) && devices.Length > 1 && !own) name += " · " + phoneName;
            list.Add(new(own ? AndroidAudioSession.SourcePhoneOutputId : current, name, VisibilityId: own ? current : null));
        }
        void AddDevice(AndroidDevice online)
        {
            string key = AndroidDeviceIdentity.Key(online);
            if (!seen.Add(key)) return;
            string phoneName = phone.Store.GetDisplayName(online);
            if (aliases != null)
            {
                bool own = source != null && AndroidDeviceIdentity.Matches(source.Device, online);
                AddCurrent(key, own, phoneName);
                if (source != null && !own) return;
            }
            var profile = phone.Store.FindProfile(online);
            var sinks = profile?.OutputSinks is { Count: > 0 } configured
                ? AndroidOutputSink.NormalizeList(configured)
                : [AndroidOutputSink.DefaultSpeaker];
            foreach (var sink in sinks)
            {
                if (sink.Kind == AndroidOutputSinkKind.Current) continue;
                string devId = AndroidOutputIdentity.FormatDeviceId(key, sink.Key);
                string label = PhoneAppPresentation.OutputLabel(sink, phoneName);
                list.Add(new Endpoint(devId, label));
            }
        }
        foreach (var device in devices) AddDevice(device);
        return list.ToArray();
    }

    internal string? GetPhoneRoute(int pid, out bool exclusive)
    {
        exclusive = false;
        return pid > 0 ? _tryGetPhone()?.Pc.GetPersistedEndpoint(pid, out exclusive) : null;
    }

    internal (bool Success, int HResult, string Message) RoutePcAppToPhone(int pid, string targetDeviceId, bool silenceLocal)
    {
        if (pid <= 0) return (false, -1, L10n.T("Phone.Err.InvalidProcessId"));
        if (!AndroidOutputIdentity.TryParseDeviceId(targetDeviceId, out string phoneKey, out _))
            return (false, -1, L10n.T("Phone.Err.InvalidPhoneOutputDevice"));
        var phone = _getPhone();
        var connected = phone.Connections.ResolveKey(phoneKey);
        if (connected == null || connected.IsApp)
            return (false, -1, L10n.T("Phone.Err.PhoneOffline"));
        var result = phone.Pc.RouteProcess(pid, targetDeviceId, connected, silenceLocal);
        if (result.Success) _onChanged();
        return result;
    }

    internal bool StopPcAppToPhone(int pid, bool restorePreviousEndpoint)
    {
        var phone = _tryGetPhone();
        if (phone == null) return false;
        bool stopped = phone.Pc.StopProcess(pid, restorePreviousEndpoint);
        if (stopped) _onChanged();
        return stopped;
    }

    internal int StopAllPcToPhone(bool restorePreviousEndpoint = true)
    {
        var phone = _tryGetPhone();
        if (phone == null) return 0;
        int count = phone.Pc.StopAll(restorePreviousEndpoint);
        if (count > 0) _onChanged();
        return count;
    }

    internal bool IsVirtualDisplayWindow(nint hwnd) =>
        hwnd != 0 && _tryGetPhone() is { } phone && phone.Displays.OwnsWindow(hwnd);

    internal string? TryResolveVirtualDisplayPackage(nint hwnd, int pid)
    {
        if (_tryGetPhone() is not { } phone) return null;
        if (phone.Displays.FindApplication(hwnd, pid) is { } match)
            return "android:" + match.App.Package;
        return null;
    }

    internal IPcProcessSilenceLease? AcquireLocalSilenceLease(int pid) => PhoneMirrorIsolation.AcquireLegacy(pid);

    public void Dispose()
    {
        if (ReferenceEquals(AppDisplayName.ExternalNameResolver, (Func<string, string?>)ResolveExternalDisplayName))
            AppDisplayName.ExternalNameResolver = null;
    }
}
#endif

#if SONICROUTE_PHONE
using System;
using System.Collections.Concurrent;
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

namespace SonicRoute.Native.Product;

/// <summary>
/// Native AOT Full-edition bridge connecting PhonePreview / AirPlayPreview external sessions and
/// PC-to-phone audio routing into ProductAudio, ProductRouting, and ProductCurrentApp without WPF/NAudio.
/// </summary>
internal sealed class ExternalAudioBridge : IDisposable
{
    private static readonly Guid SilenceMuteContext = new("7E2A9B10-58E1-4F7C-8B4A-4B2C9C1F3E81");
    private readonly Func<PhonePreview?> _tryGetPhone;
    private readonly Func<PhonePreview> _getPhone;
    private readonly Func<AirPlayPreview?> _tryGetAirPlay;
    private readonly Action _onChanged;
    private readonly ConcurrentDictionary<int, bool> _silenceLocalPrefs = new();
    private readonly object _silenceGate = new();
    private readonly Dictionary<string, ReservedEndpointState> _reservedSilence = new(StringComparer.OrdinalIgnoreCase);

    internal ExternalAudioBridge(
        Func<PhonePreview?> tryGetPhone,
        Func<PhonePreview> getPhone,
        Func<AirPlayPreview?> tryGetAirPlay,
        Action onChanged)
    {
        _tryGetPhone = tryGetPhone;
        _getPhone = getPhone;
        _tryGetAirPlay = tryGetAirPlay;
        _onChanged = onChanged;
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

    internal void AppendExternalApps(List<AudioAppInfo> result)
    {
        if (_tryGetPhone() is { } phone)
        {
            foreach (var source in phone.Sources.Sources)
            {
                string deviceName = phone.Store.GetDisplayName(source.Device);
                result.Add(new AudioAppInfo
                {
                    ProcessId = 0,
                    ExternalSourceId = source.Id,
                    ProcessName = "android:" + source.App.Package,
                    DisplayName = source.App.IsDeviceAudio ? deviceName : source.App.Name,
                    SourceLabel = string.Format(L10n.T("Phone.FromDevice"), deviceName),
                    ExternalIconPng = source.App.IconPng,
                    HasActiveSession = source.Session.State.State == CaptureState.Receiving,
                });
            }
        }
        if (_tryGetAirPlay()?.Source is { } airPlay)
        {
            result.Add(new AudioAppInfo
            {
                ProcessId = 0,
                ExternalSourceId = airPlay.Id,
                ProcessName = "airplay:receiver",
                DisplayName = airPlay.Name,
                SourceLabel = "AirPlay",
                HasActiveSession = airPlay.Pcm != null,
            });
        }
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

    internal VolumeState ReadExternalVolume(AudioAppInfo app)
    {
        var session = FindSession(app.ExternalSourceId);
        if (session == null || session.State.State != CaptureState.Receiving)
            return new(-1, false, 0);
        return new(session.State.Volume, session.State.Muted, 1);
    }

    internal VolumeState SetExternalVolume(AudioAppInfo app, int? level = null, bool? mute = null, int delta = 0, bool toggle = false)
    {
        var session = FindSession(app.ExternalSourceId)
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

    internal async Task<ControlReply> SetExternalOutputsAsync(AudioAppInfo app, string[] outputs)
    {
        if (FindSession(app.ExternalSourceId) is not IMultiOutputAudioSession multi)
            return new("error: external_session_unavailable");
        try
        {
            var normalized = outputs
                .Select(id => string.IsNullOrWhiteSpace(id) || AudioService.IsSystemDefault(id) ? null : id)
                .ToArray();
            if (normalized.Length == 0) normalized = [null];
            await multi.SetOutputsAsync(normalized).ConfigureAwait(false);
            _onChanged();
            return new("ok");
        }
        catch (Exception ex)
        {
            return new("error: " + ex.Message);
        }
    }

    internal Endpoint[] GetPhoneOutputs()
    {
        var phone = _tryGetPhone();
        if (phone == null) return [];
        var devices = phone.Discovery.Devices ?? [];
        var list = new List<Endpoint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
        {
            if (!string.Equals(d.State, "device", StringComparison.Ordinal)) continue;
            var online = phone.Connections.ResolveKey(AndroidDeviceIdentity.Key(d));
            if (online == null) continue;
            string key = AndroidDeviceIdentity.Key(online);
            if (!seen.Add(key)) continue;
            string phoneName = phone.Store.GetDisplayName(online);
            var profile = phone.Store.FindProfile(online);
            var sinks = profile?.OutputSinks is { Count: > 0 } configured
                ? AndroidOutputSink.NormalizeList(configured)
                : [AndroidOutputSink.DefaultSpeaker];
            foreach (var sink in sinks)
            {
                if (sink.Kind == AndroidOutputSinkKind.Current) continue;
                string devId = AndroidOutputIdentity.FormatDeviceId(key, sink.Key);
                string label = sink.Kind == AndroidOutputSinkKind.Speaker
                    ? $"{phoneName} · {L10n.T("Phone.OutputSpeaker")}"
                    : $"{phoneName} · {sink.Name}";
                list.Add(new Endpoint(devId, label));
            }
        }
        return list.ToArray();
    }

    internal string? GetPhoneRoutedDeviceId(int pid)
    {
        if (pid <= 0) return null;
        return _tryGetPhone()?.Pc.GetPersistedEndpoint(pid);
    }

    internal (bool Success, int HResult, string Message) RoutePcAppToPhone(int pid, string targetDeviceId, bool silenceLocal)
    {
        if (pid <= 0) return (false, -1, L10n.T("Phone.Err.InvalidProcessId"));
        if (!AndroidOutputIdentity.TryParseDeviceId(targetDeviceId, out string phoneKey, out _))
            return (false, -1, L10n.T("Phone.Err.InvalidPhoneOutputDevice"));
        var phone = _getPhone();
        var connected = phone.Connections.ResolveKey(phoneKey)
            ?? (phone.Discovery.Devices ?? []).FirstOrDefault(d => string.Equals(d.State, "device", StringComparison.Ordinal)
                && string.Equals(AndroidDeviceIdentity.Key(d), phoneKey, StringComparison.OrdinalIgnoreCase));
        if (connected == null)
            return (false, -1, L10n.T("Phone.Err.PhoneOffline"));
        _silenceLocalPrefs[pid] = silenceLocal;
        var result = phone.Pc.RouteProcess(pid, targetDeviceId, connected);
        if (result.Success) _onChanged();
        return result;
    }

    internal bool StopPcAppToPhone(int pid, bool restorePreviousEndpoint)
    {
        _silenceLocalPrefs.TryRemove(pid, out _);
        var phone = _tryGetPhone();
        if (phone == null) return false;
        bool stopped = phone.Pc.StopProcess(pid, restorePreviousEndpoint);
        if (stopped) _onChanged();
        return stopped;
    }

    internal int StopAllPcToPhone(bool restorePreviousEndpoint = true)
    {
        _silenceLocalPrefs.Clear();
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

    internal IPcProcessSilenceLease? AcquireLocalSilenceLease(int pid)
    {
        if (pid <= 0) return null;
        if (_silenceLocalPrefs.TryGetValue(pid, out bool silence) && !silence)
            return null;
        return TryAcquireEndpointLease(pid);
    }

    private sealed class ReservedEndpointState
    {
        internal required string EndpointId;
        internal bool WasMuted;
        internal int RefCount;
    }

    private sealed class NativeSilenceLease : IPcProcessSilenceLease
    {
        private readonly ExternalAudioBridge _bridge;
        private readonly int _pid;
        private readonly string _reservedEndpoint;
        private readonly string? _prevMultimedia;
        private readonly string? _prevConsole;
        private int _released;

        internal NativeSilenceLease(ExternalAudioBridge bridge, int pid, string reservedEndpoint, string? prevMultimedia, string? prevConsole)
        {
            _bridge = bridge;
            _pid = pid;
            _reservedEndpoint = reservedEndpoint;
            _prevMultimedia = prevMultimedia;
            _prevConsole = prevConsole;
        }

        public void Release(bool restorePreviousEndpoint)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _bridge.ReleaseEndpointLease(_pid, _reservedEndpoint, _prevMultimedia, _prevConsole, restorePreviousEndpoint);
        }

        public void Dispose() => Release(restorePreviousEndpoint: true);
    }

    private IPcProcessSilenceLease? TryAcquireEndpointLease(int pid)
    {
        try
        {
            using var mta = new MtaScope();
            using var endpoints = new Endpoints();
            var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            {
                try
                {
                    using var def = endpoints.Default(Flow.Render, role);
                    defaults.Add(Endpoints.Id(def));
                }
                catch (COMException) { }
            }
            string? candidate = endpoints.Active(Flow.Render)
                .Select(e => e.Id)
                .FirstOrDefault(id => !defaults.Contains(id));
            if (string.IsNullOrWhiteSpace(candidate)) return null;

            string? prevMedia;
            string? prevConsole;
            using (var policy = new Policy())
            {
                prevMedia = policy.Read((uint)pid, Flow.Render, Role.Multimedia);
                prevConsole = policy.Read((uint)pid, Flow.Render, Role.Console);
            }

            lock (_silenceGate)
            {
                if (!_reservedSilence.TryGetValue(candidate, out var state))
                {
                    using var dev = endpoints.Open(candidate);
                    using var vol = Endpoints.Activate(dev, Endpoints.VolumeInterface);
                    (_, bool wasMuted) = Endpoints.ReadVolume(vol, endpoint: true);
                    if (!wasMuted) Endpoints.SetMute(vol, endpoint: true, true, SilenceMuteContext);
                    state = new ReservedEndpointState { EndpointId = candidate, WasMuted = wasMuted, RefCount = 0 };
                    _reservedSilence[candidate] = state;
                }
                state.RefCount++;
            }

            try
            {
                string full = AudioPolicyConfig.EnsureFullDeviceId(candidate, EDataFlow.eRender);
                using var policy = new Policy();
                policy.Write((uint)pid, Flow.Render, Role.Multimedia, full);
                policy.Write((uint)pid, Flow.Render, Role.Console, full);
            }
            catch
            {
                ReleaseEndpointLease(pid, candidate, prevMedia, prevConsole, restorePreviousEndpoint: false);
                return null;
            }

            return new NativeSilenceLease(this, pid, candidate, prevMedia, prevConsole);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private void ReleaseEndpointLease(int pid, string reservedEndpoint, string? prevMedia, string? prevConsole, bool restorePreviousEndpoint)
    {
        try
        {
            using var mta = new MtaScope();
            if (restorePreviousEndpoint)
            {
                using var policy = new Policy();
                try { policy.Write((uint)pid, Flow.Render, Role.Multimedia, prevMedia); } catch (COMException) { }
                try { policy.Write((uint)pid, Flow.Render, Role.Console, prevConsole); } catch (COMException) { }
            }

            lock (_silenceGate)
            {
                if (_reservedSilence.TryGetValue(reservedEndpoint, out var state) && --state.RefCount <= 0)
                {
                    _reservedSilence.Remove(reservedEndpoint);
                    if (!state.WasMuted)
                    {
                        using var endpoints = new Endpoints();
                        using var dev = endpoints.Open(reservedEndpoint);
                        using var vol = Endpoints.Activate(dev, Endpoints.VolumeInterface);
                        Endpoints.SetMute(vol, endpoint: true, false, SilenceMuteContext);
                    }
                }
            }
        }
        catch (COMException) { }
    }

    public void Dispose()
    {
        if (ReferenceEquals(AppDisplayName.ExternalNameResolver, (Func<string, string?>)ResolveExternalDisplayName))
            AppDisplayName.ExternalNameResolver = null;
        _silenceLocalPrefs.Clear();
    }
}
#endif

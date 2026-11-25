using System.Globalization;
using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

internal sealed record VolumeState(int Percent, bool Muted, int Count);
internal sealed record AppSession(uint Pid, string? Process, string Label, bool Active);

internal sealed partial class ProductAudio : IDisposable
{
    private readonly ProductRouting _routing = new();
    private readonly object _catalogGate = new(), _volumeGate = new();
    private List<AudioAppInfo>? _apps;
    private long _appsAt;
    private bool _disposed;
    private readonly Dictionary<(int Pid, Flow Flow), VolumeGroup> _groups = new();
    private static readonly Guid Context = new("2CF052AA-46C6-4158-AEF3-C279D17FBC22");
    internal event Action? AudioChanged;
#if SONICROUTE_PHONE
    internal ExternalAudioBridge? ExternalAudio
    {
        get => _routing.ExternalAudio;
        set => _routing.ExternalAudio = value;
    }
#endif
    internal void Start(bool resident) { if (resident) _routing.Start(); }
    internal void NotifyAudioChanged() => AudioChanged?.Invoke();
    internal void InvalidateAppsCache() { lock (_catalogGate) { _apps = null; _appsAt = 0; } }
    internal void InvalidateVolumeCache()
    {
        using var apartment = new MtaScope();
        lock (_volumeGate)
        {
            foreach (var group in _groups.Values) group.Dispose();
            _groups.Clear();
        }
    }

    internal static Endpoint[] Devices(Flow flow)
    {
        using var apartment = new MtaScope();
        using var devices = new Endpoints();
        return devices.Active(flow).ToArray();
    }

    internal static Endpoint? DefaultEndpoint(Flow flow)
    {
        using var apartment = new MtaScope();
        using var devices = new Endpoints();
        try
        {
            using var endpoint = devices.Default(flow, Role.Console);
            string id = Endpoints.Id(endpoint);
            try { return new(id, Endpoints.ReadName(endpoint)); }
            catch (COMException) { return new(id, ""); }
        }
        catch (COMException error) when (error.HResult == unchecked((int)0x80070490)) { return null; }
    }

    internal static string DefaultId(Flow flow, Role role = Role.Console)
    {
        using var apartment = new MtaScope();
        using var devices = new Endpoints();
        using var endpoint = devices.Default(flow, role);
        return Endpoints.Id(endpoint);
    }

    internal bool SetDefault(Flow flow, string endpoint)
    {
        string id = AudioPolicyConfig.UnpackDeviceId(endpoint);
        if (!Devices(flow).Any(e => Same(e.Id, id))) throw new ArgumentException("Unknown endpoint for requested flow.");
        using var apartment = new MtaScope();
        lock (_volumeGate)
        {
            string console = DefaultId(flow, Role.Console);
            Endpoints.SetDefault(id, Role.Console);
            try { Endpoints.SetDefault(id, Role.Multimedia); }
            catch
            {
                if (Same(DefaultId(flow, Role.Console), id)) Endpoints.SetDefault(console, Role.Console);
                throw;
            }
        }
        return true;
    }

    internal List<AudioAppInfo> Apps() => Apps(refresh: false);

    internal List<AudioAppInfo> Apps(bool refresh)
    {
        List<AudioAppInfo> list;
        lock (_catalogGate)
        {
            if (!refresh && _apps != null && Environment.TickCount64 - _appsAt < 1000)
                list = new(_apps);
            else
            {
                var catalog = new AudioAppCatalog();
                using var apartment = new MtaScope();
                using var devices = new Endpoints();
                foreach (var flow in new[] { Flow.Render, Flow.Capture })
                    VisitAll(devices, flow, (pid, state, control) =>
                    {
                        if (state == 2 || pid == 0) return;
                        var app = catalog.Add(pid, state == 1, Endpoints.DisplayName(control));
                        if (app != null && string.IsNullOrWhiteSpace(app.IconPath))
                        {
                            string? iconPath = Endpoints.IconPath(control);
                            if (!string.IsNullOrWhiteSpace(iconPath)) app.IconPath = iconPath;
                        }
                    });
                _apps = catalog.Sorted();
                _appsAt = Environment.TickCount64;
                list = new(_apps);
            }
        }
#if SONICROUTE_PHONE
        ExternalAudio?.AppendExternalApps(list);
#endif
        return list;
    }

    internal Endpoint[] RenderDevicesWithPhone()
    {
        var win = Devices(Flow.Render);
#if SONICROUTE_PHONE
        var phone = ExternalAudio?.GetPhoneOutputs() ?? [];
        if (phone.Length > 0) return [.. win, .. phone];
#endif
        return win;
    }

    private static void VisitAll(Endpoints devices, Flow flow, Action<uint,uint,Com> visit)
    {
        foreach (var item in devices.Active(flow))
        {
            try
            {
                using var endpoint = devices.Open(item.Id);
                Endpoints.VisitSessions(endpoint, visit);
            }
            catch (COMException) { /* A device can disappear between enumeration and activation. */ }
        }
    }

    internal string? ReadRoute(int pid, Flow flow)
    {
#if SONICROUTE_PHONE
        if (flow == Flow.Render && ExternalAudio?.GetPhoneRoutedDeviceId(pid) is { } phoneOut)
            return phoneOut;
#endif
        return PcRoutingApi.GetPersistedEndpoint(pid, (EDataFlow)flow);
    }

    internal string? ReadRoute(AudioAppInfo app, Flow flow)
    {
        if (!app.IsExternal) return ReadRoute(checked((int)app.ProcessId), flow);
#if SONICROUTE_PHONE
        if (flow == Flow.Render && ExternalAudio != null)
            return ExternalAudio.GetExternalOutputs(app).FirstOrDefault();
#endif
        return null;
    }

    internal string[] GetSelectedOutputs(AudioAppInfo app)
    {
        if (!app.IsExternal)
        {
            int pid = checked((int)app.ProcessId);
            var set = AppPlaybackOutputs.GetSelected(pid, PcRoutingApi.GetPersistedEndpoint(pid, EDataFlow.eRender));
#if SONICROUTE_PHONE
            if (ExternalAudio?.GetPhoneRoutedDeviceId(pid) is { } phoneOut)
            {
                if (set.Count == 1 && Core.AudioService.IsSystemDefault(set.First()))
                    return [phoneOut];
                set.Add(phoneOut);
            }
#endif
            return set.ToArray();
        }
#if SONICROUTE_PHONE
        if (ExternalAudio != null) return ExternalAudio.GetExternalOutputs(app);
#endif
        return [Core.AudioService.SystemDefaultDeviceId];
    }

    internal bool SetRoute(int pid, Flow flow, string? endpoint)
    {
        var result = PcRoutingApi.ApplyEndpoint(pid, (EDataFlow)flow, endpoint);
        if (!result.Success) throw new COMException(result.Message, result.HResult);
        NotifyAudioChanged();
        return true;
    }

    internal Task<ControlReply> SelectOutputAsync(int pid, string target)
        => _routing.SelectOutputAsync(pid, target);

#if SONICROUTE_PHONE
    internal Task<ControlReply> SetExternalOutputsAsync(AudioAppInfo app, string[] outputs) =>
        ExternalAudio != null
            ? ExternalAudio.SetExternalOutputsAsync(app, outputs)
            : Task.FromResult(new ControlReply("error: phone_unavailable"));
#endif

    internal bool ResetRoutes()
    {
        using var apartment = new MtaScope();
#if SONICROUTE_PHONE
        ExternalAudio?.StopAllPcToPhone(restorePreviousEndpoint: true);
#endif
        bool cleared = AppPlaybackRestore.ClearAsync().GetAwaiter().GetResult();
        LocalAppOutputMirrors.ClearAllAsync().GetAwaiter().GetResult();
        using var policy = new Policy();
        try
        {
            policy.ClearAll();
            InvalidateAppsCache();
            NotifyAudioChanged();
            return cleared;
        }
        catch (COMException)
        {
            var pids = Apps().Where(a => !a.IsExternal && a.ProcessId > 0).Select(app => (int)app.ProcessId).ToHashSet();
            foreach (var process in System.Diagnostics.Process.GetProcesses())
                using (process) if (process.Id > 0) pids.Add(process.Id);

            foreach (int pid in pids)
                foreach (Flow flow in new[] { Flow.Render, Flow.Capture })
                    PcRoutingApi.ApplyEndpoint(pid, (EDataFlow)flow, null);
            InvalidateAppsCache();
            NotifyAudioChanged();
            // A per-process fallback cannot confirm routes of applications that have exited.
            return false;
        }
    }

    internal VolumeState Volume(AudioAppInfo app, int? level = null, bool? mute = null, int delta = 0, bool toggle = false)
    {
#if SONICROUTE_PHONE
        if (app.IsExternal)
        {
            if (ExternalAudio == null) throw new InvalidOperationException("No active external audio session.");
            if (level == null && !mute.HasValue && delta == 0 && !toggle)
                return ExternalAudio.ReadExternalVolume(app);
            return ExternalAudio.SetExternalVolume(app.ExternalSourceId, level, mute, delta, toggle);
        }
#endif
        return Volume(Flow.Render, null, checked((int)app.ProcessId), level, mute, delta, toggle);
    }

    internal VolumeState Volume(Flow flow, string? endpoint, int pid = 0, int? level = null,
        bool? mute = null, int delta = 0, bool toggle = false)
    {
        if (pid < 0) throw new ArgumentOutOfRangeException(nameof(pid));
        using var apartment = new MtaScope();
        lock (_volumeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (pid == 0)
            {
                using var devices = new Endpoints();
                using var device = string.IsNullOrEmpty(endpoint) || Core.AudioService.IsSystemDefault(endpoint)
                    ? devices.Default(flow, Role.Console) : devices.Open(AudioPolicyConfig.UnpackDeviceId(endpoint));
                using var volume = Endpoints.Activate(device, Endpoints.VolumeInterface);
                return Change([volume], true, level, mute, delta, toggle);
            }
            var group = GetGroup(pid, flow);
            try { return Change(group.Sources, false, level, mute, delta, toggle); }
            catch (COMException)
            {
                // Retry discovery/read without repeating an already applied relative change.
                InvalidateGroup(pid, flow);
                throw;
            }
        }
    }

    private static VolumeState Change(IReadOnlyList<Com> sources, bool endpoint, int? level, bool? mute, int delta, bool toggle)
    {
        if (sources.Count == 0) throw new InvalidOperationException("No active session for this process and flow.");
        var first = sources[0];
        var before = Endpoints.ReadVolume(first, endpoint);
        int percent = (int)Math.Round(before.Level * 100);
        if (level == null && !mute.HasValue && delta == 0 && !toggle) return new(percent, before.Muted, sources.Count);
        float requested = Math.Clamp(level ?? percent + delta, 0, 100) / 100f;
        int applied = 0;
        foreach (var source in sources)
        {
            try
            {
                if (level.HasValue || delta != 0) Endpoints.SetVolume(source, endpoint, requested, Context);
                if (mute.HasValue || toggle) Endpoints.SetMute(source, endpoint, mute ?? !before.Muted, Context);
                applied++;
            }
            catch (COMException) when (!endpoint && sources.Count > 1) { }
        }
        if (applied == 0) throw new InvalidOperationException("Every volume write failed.");
        var actual = Endpoints.ReadVolume(first, endpoint);
        return new((int)Math.Round(actual.Level * 100), actual.Muted, applied);
    }

    private sealed class VolumeGroup(int pid) : IDisposable
    {
        internal readonly int Pid = pid;
        internal readonly long At = Environment.TickCount64;
        internal readonly MtaLifetime Apartment = new();
        internal readonly List<Com> Sources = new();
        public void Dispose() { foreach (var c in Sources) c.Dispose(); Apartment.Dispose(); }
    }

    private void InvalidateGroup(int pid, Flow flow)
    { if (_groups.Remove((pid, flow), out var group)) group.Dispose(); }

    private void ClearStaleGroups()
    {
        long now = Environment.TickCount64;
        List<(int Pid, Flow Flow)>? stale = null;
        foreach (var pair in _groups)
        {
            if (now - pair.Value.At >= 5000 || _groups.Count > 64)
                (stale ??= new()).Add(pair.Key);
        }
        if (stale == null) return;
        foreach (var key in stale) InvalidateGroup(key.Pid, key.Flow);
    }

    private VolumeGroup GetGroup(int pid, Flow flow)
    {
        if (_groups.TryGetValue((pid, flow), out var group) && group.Sources.Count != 0
            && Environment.TickCount64 - group.At < 5000) return group;
        InvalidateGroup(pid, flow);
        ClearStaleGroups();
        group = new(pid);
        try
        {
            using var devices = new Endpoints();
            VisitAll(devices, flow, (p, state, control) =>
            {
                if (p == pid && state != 2) group.Sources.Add(control.Query(Endpoints.SimpleVolume));
            });
            _groups.Add((pid, flow), group);
            return group;
        }
        catch { group.Dispose(); throw; }
    }

    internal bool GlobalPlaybackMute(bool? set = null)
    {
        lock (_volumeGate)
        {
            var devices = Devices(Flow.Render);
            if (devices.Length == 0) throw new InvalidOperationException("No render endpoints.");
            bool target = set ?? !devices.All(d => Volume(Flow.Render, d.Id).Muted);
            Exception? failure = null;
            foreach (var d in devices)
            {
                try { Volume(Flow.Render, d.Id, mute: target); }
                catch (Exception error) { failure ??= error; }
            }
            if (failure != null) throw new InvalidOperationException("One or more playback writes failed.", failure);
            var final = Devices(Flow.Render);
            if (final.Length == 0 || !final.All(d => Volume(Flow.Render, d.Id).Muted == target))
                throw new InvalidOperationException("Playback mute state changed during operation.");
            return target;
        }
    }

    internal bool GlobalMicMute(bool? set)
    {
        lock (_volumeGate)
        {
            var devices = Devices(Flow.Capture);
            if (devices.Length == 0) throw new InvalidOperationException("No capture endpoints.");
            bool target = set ?? !devices.All(d => Volume(Flow.Capture, d.Id).Muted);
            Exception? failure = null;
            foreach (var d in devices)
            {
                try { Volume(Flow.Capture, d.Id, mute: target); }
                catch (Exception error) { failure ??= error; }
            }
            if (failure != null) throw new InvalidOperationException("One or more microphone writes failed.", failure);
            var final = Devices(Flow.Capture);
            if (final.Length == 0 || !final.All(d => Volume(Flow.Capture, d.Id).Muted == target))
                throw new InvalidOperationException("Microphone state changed during operation.");
            return target;
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _routing.Dispose();
        using var apt = new MtaScope();
        lock (_volumeGate)
        {
            _disposed = true;
            foreach (var group in _groups.Values) group.Dispose();
            _groups.Clear();
        }
        StopObservation();
    }
}

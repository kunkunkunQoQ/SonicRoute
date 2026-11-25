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
    private readonly Dictionary<Flow, VolumeGroup> _groups = new(2);
    private static readonly Guid Context = new("2CF052AA-46C6-4158-AEF3-C279D17FBC22");
    internal void Start(bool resident) { if (resident) _routing.Start(); }

    internal static Endpoint[] Devices(Flow flow)
    {
        using var apartment = new MtaScope();
        using var devices = new Endpoints();
        return devices.Active(flow).ToArray();
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

    internal List<AudioAppInfo> Apps()
    {
        lock (_catalogGate)
        {
            if (_apps != null && Environment.TickCount64 - _appsAt < 1000) return new(_apps);
            var catalog = new AudioAppCatalog();
            using var apartment = new MtaScope();
            using var devices = new Endpoints();
            foreach (var flow in new[] { Flow.Render, Flow.Capture })
                VisitAll(devices, flow, (pid, state, control) =>
                {
                    if (state != 2 && pid != 0) catalog.Add(pid, state == 1, Endpoints.DisplayName(control));
                });
            _apps = catalog.Sorted();
            _appsAt = Environment.TickCount64;
            return new(_apps);
        }
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

    internal string? ReadRoute(int pid, Flow flow) => PcRoutingApi.GetPersistedEndpoint(pid, (EDataFlow)flow);
    internal bool SetRoute(int pid, Flow flow, string? endpoint)
    {
        var result = PcRoutingApi.ApplyEndpoint(pid, (EDataFlow)flow, endpoint);
        if (!result.Success) throw new COMException(result.Message, result.HResult);
        return true;
    }
    internal Task<ControlReply> SelectOutputAsync(int pid, string target)
        => _routing.SelectOutputAsync(pid, target);

    internal bool ResetRoutes()
    {
        using var apartment = new MtaScope();
        bool cleared = AppPlaybackRestore.ClearAsync().GetAwaiter().GetResult();
        LocalAppOutputMirrors.ClearAllAsync().GetAwaiter().GetResult();
        using var policy = new Policy();
        try { policy.ClearAll(); return cleared; }
        catch (COMException)
        {
            var pids = Apps().Select(app => (int)app.ProcessId).ToHashSet();
            foreach (var process in System.Diagnostics.Process.GetProcesses())
                using (process) if (process.Id > 0) pids.Add(process.Id);

            foreach (int pid in pids)
                foreach (Flow flow in new[] { Flow.Render, Flow.Capture })
                    PcRoutingApi.ApplyEndpoint(pid, (EDataFlow)flow, null);
            // A per-process fallback cannot confirm routes of applications that have exited.
            return false;
        }
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
                InvalidateGroup(flow);
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

    private void InvalidateGroup(Flow flow)
    { if (_groups.Remove(flow, out var group)) group.Dispose(); }

    private VolumeGroup GetGroup(int pid, Flow flow)
    {
        if (_groups.TryGetValue(flow, out var group) && group.Pid == pid && group.Sources.Count != 0
            && Environment.TickCount64 - group.At < 5000) return group;
        InvalidateGroup(flow);
        group = new(pid);
        try
        {
            using var devices = new Endpoints();
            VisitAll(devices, flow, (p, state, control) =>
            {
                if (p == pid && state != 2) group.Sources.Add(control.Query(Endpoints.SimpleVolume));
            });
            _groups.Add(flow, group);
            return group;
        }
        catch { group.Dispose(); throw; }
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

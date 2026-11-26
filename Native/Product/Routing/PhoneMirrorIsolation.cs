#if SONICROUTE_PHONE
using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

// The lease pools, playback migration checks and fallback order are shared with the desktop product.
// All Windows audio work runs on the calling MTA, never on the panel's input thread.
internal static partial class PhoneMirrorIsolation
{
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
    private static readonly Guid MuteContext = Guid.NewGuid();
    private static readonly SourceSpeakerLeases Speakers = new(pid => ReadSpeaker(pid), ReadMute,
        (id, mute) => WriteMute(id, mute, allowDefault: true));
    private static readonly LocalSilenceLeasePool Legacy = Pool(verifyMigration: false);
    private static readonly LocalSilenceLeasePool Mirrors = Pool(verifyMigration: true);
    private static readonly AlreadyMutedOutputLeases AlreadyMuted = new(AllOutputsAlreadyMuted);
    private static IsolationEvents? _events;
    private static int _users;

    private static LocalSilenceLeasePool Pool(bool verifyMigration) => new(
        (owners, reserved) => Candidates(owners, reserved, verifyMigration), ReadMute,
        (id, mute) => WriteMute(id, mute, allowDefault: false), ReadRoles, WriteRoles)
    {
        VerifyPlaybackRoute = verifyMigration ? VerifyMigration : null,
        ObservePlayback = (pid, id, revoke) => new PlaybackWatch(pid, id, revoke)
    };

    internal static IPcProcessSilenceLease? AcquireLegacy(int pid) => Acquire(pid, false, out _, legacy: true);

    internal static IPcProcessSilenceLease? Acquire(int pid, bool allowSpeakerMute, out bool mutedSpeaker, bool legacy = false)
    {
        mutedSpeaker = false;
        if (pid <= 0 || pid == Environment.ProcessId) return null;
        using var apartment = new MtaScope();
        lock (LocalSilenceLeasePool.Gate)
        {
            _events ??= new IsolationEvents();
            ++_users;
            bool acquired = false;
            try
            {
                _events.Refresh();
                IPcProcessSilenceLease? lease;
                if (legacy) lease = Legacy.Acquire(pid);
                else
                {
                    lease = AlreadyMuted.Acquire(pid);
                    if (lease == null)
                    {
                        var original = ReadSpeaker(pid);
                        lease = Mirrors.AcquireForPlayback(pid, original.Active);
                        if (lease == null && allowSpeakerMute && original.Endpoint is { } expected && _events.Observes(expected) &&
                            AndroidAudioHost.WaitForOriginalSpeaker(expected, original.Active, () => ReadSpeaker(pid), WaitForAudio))
                        {
                            lease = Speakers.Acquire(pid, expected, original.Active);
                            mutedSpeaker = lease != null;
                        }
                    }
                }
                if (lease == null) return null;
                acquired = true;
                return new MonitoredLease(lease);
            }
            finally { if (!acquired) ReleaseObserver(); }
        }
    }

    private sealed class MonitoredLease(IPcProcessSilenceLease lease) : IPcProcessSilenceLease
    {
        private IPcProcessSilenceLease? _lease = lease;
        public CancellationToken Invalidated { get; } = lease.Invalidated;
        public void Dispose() => Release(true);
        public void Release(bool restorePreviousEndpoint)
        {
            if (Interlocked.Exchange(ref _lease, null) is not { } current) return;
            using var apartment = new MtaScope();
            lock (LocalSilenceLeasePool.Gate)
            {
                try { current.Release(restorePreviousEndpoint); }
                finally { ReleaseObserver(); }
            }
        }
    }

    private static void ReleaseObserver()
    {
        --_users;
        if (_users == 0) TrimObserver();
    }

    private static void TrimObserver()
    {
        // An unplugged endpoint with a pending mute restoration needs only topology notifications.
        if (Mirrors.HasReservations || Legacy.HasReservations || Speakers.HasReservations) _events?.ClearEndpoints();
        else Shutdown();
    }

    internal static void Shutdown()
    {
        lock (LocalSilenceLeasePool.Gate)
        {
            var events = _events;
            _events = null;
            events?.Dispose();
        }
    }

    private static (string? Multimedia, string? Console) ReadRoles(int pid)
    {
        using var policy = new Policy();
        string? Read(Role role) => policy.Read((uint)pid, Flow.Render, role) is string full ? AudioPolicyConfig.UnpackDeviceId(full) : null;
        return (Read(Role.Multimedia), Read(Role.Console));
    }

    private static bool WriteRoles(int pid, (string? Multimedia, string? Console) roles)
    {
        using var policy = new PcRoutingPolicy(EDataFlow.eRender);
        int Write(ERole role, string? id) => policy.SetDefaultEndPoint(id == null ? null : Policy.FullId(id, Flow.Render), pid, role);
        int media = Write(ERole.eMultimedia, roles.Multimedia);
        int console = Write(ERole.eConsole, roles.Console);
        return media >= 0 && console >= 0;
    }

    private static (bool Muted, bool Known) ReadMute(string id)
    {
        try
        {
            using var endpoints = new Endpoints();
            using var dev = endpoints.Open(id);
            using var vol = Endpoints.Activate(dev, Endpoints.VolumeInterface);
            return (vol.UInt32(15) != 0, true);
        }
        catch (COMException) { return (false, false); }
    }

    private static bool WriteMute(string id, bool muted, bool allowDefault)
    {
        try
        {
            using var endpoints = new Endpoints();
            if (muted && !allowDefault && Defaults(endpoints).Contains(id)) return false;
            using var dev = endpoints.Open(id);
            using var vol = Endpoints.Activate(dev, Endpoints.VolumeInterface);
            Endpoints.SetMute(vol, endpoint: true, muted, MuteContext);
            return true;
        }
        catch (COMException) { return false; }
    }

    private static HashSet<string> Defaults(Endpoints devices)
    {
        var result = new HashSet<string>(Ids);
        foreach (var role in Enum.GetValues<Role>())
        {
            try { using var dev = devices.Default(Flow.Render, role); result.Add(Endpoints.Id(dev)); }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490)) { }
        }
        return result;
    }

    private static bool AllOutputsAlreadyMuted()
    {
        using var devices = new Endpoints();
        var outputs = devices.Active(Flow.Render);
        return outputs.Count > 0 && outputs.All(d => _events?.Observes(d.Id) == true && !Legacy.OwnsEndpoint(d.Id) && !Mirrors.OwnsEndpoint(d.Id)
            && !Speakers.OwnsEndpoint(d.Id) && ReadMute(d.Id) == (true, true));
    }

    private static bool IsLoopback(uint pid, Com control)
    {
        if (pid != Environment.ProcessId) return false;
        using var control2 = control.Query(Endpoints.SessionControl2);
        return Endpoints.SessionId(control2, instance: false).EndsWith("%b{" + ProcessCaptureIdentity.SessionId + "}", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> Candidates(IReadOnlySet<int> owners, string? reserved, bool mirror)
    {
        using var devices = new Endpoints();
        var defaults = Defaults(devices);
        var candidates = new List<(string Id, int Rank)>();
        foreach (var output in devices.Active(Flow.Render))
        {
            if (_events?.Observes(output.Id) != true || defaults.Contains(output.Id) || (reserved != null && !Ids.Equals(reserved, output.Id))
                || (mirror ? Legacy : Mirrors).OwnsEndpoint(output.Id) || Speakers.OwnsEndpoint(output.Id)
                || AndroidAudioHost.IsMonitorOrInput(output.Name)) continue;
            try
            {
                using var dev = devices.Open(output.Id);
                bool occupied = false, dormant = false;
                Endpoints.VisitSessions(dev, (pid, state, session) =>
                {
                    if (occupied || state == 2 || IsLoopback(pid, session) || owners.Any(root => AudioProcessTree.ContainsProcess(root, (int)pid))) return;
                    if (state == 1) occupied = true;
                    else if (pid != 0) dormant = true;
                });
                if (occupied) continue;
                if (reserved != null) return [output.Id];
                using var volume = Endpoints.Activate(dev, Endpoints.VolumeInterface);
                bool hardware = (volume.UInt32(19) & 2) != 0; // IAudioEndpointVolume.QueryHardwareSupport
                if (!hardware)
                {
                    try { hardware = AndroidAudioHost.IsHardwareRenderBus(Endpoints.ConnectedAdapterId(dev)); }
                    catch (COMException) { }
                }
                candidates.Add((output.Id, (hardware ? 4 : dormant ? 0 : 2) + (volume.UInt32(15) != 0 ? 1 : 0)));
            }
            catch (COMException) { /* An endpoint whose state is unknown cannot be reserved. */ }
        }
        return candidates.OrderByDescending(c => c.Rank).Select(c => c.Id).ToArray();
    }

    private static (string? Endpoint, bool Active) ReadSpeaker(int pid, bool includeReserved = false)
    {
        using var devices = new Endpoints();
        var usable = new HashSet<string>(Ids);
        var dormant = new HashSet<string>(Ids);
        string? active = null;
        foreach (var endpoint in devices.Active(Flow.Render))
        {
            string id = endpoint.Id;
            if (_events?.Observes(id) == true && (includeReserved || (!Mirrors.OwnsEndpoint(id) && !Legacy.OwnsEndpoint(id)))) usable.Add(id);
            using var dev = devices.Open(id);
            bool playing = false;
            Endpoints.VisitSessions(dev, (owner, state, session) =>
            {
                if (state == 2 || !AudioProcessTree.ContainsProcess(pid, (int)owner) || IsLoopback(owner, session)) return;
                if (state == 1) playing = true;
                else dormant.Add(id);
            });
            if (!playing) continue;
            if (active != null) return (null, true);
            active = id;
        }
        if (active != null) return (usable.Contains(active) ? active : null, true);
        var (media, console) = ReadRoles(pid);
        if (media != null && console != null && !Ids.Equals(media, console)) return (null, false);
        string? expected = media ?? console ?? (dormant.Count == 1 ? dormant.First() : null);
        if (expected != null) return (usable.Contains(expected) ? expected : null, false);
        using var system = devices.Default(Flow.Render, Role.Multimedia);
        string idDefault = Endpoints.Id(system);
        return (usable.Contains(idDefault) && (dormant.Count == 0 || dormant.Contains(idDefault)) ? idDefault : null, false);
    }

    private static void WaitForAudio() => Thread.Sleep(100);

    private static bool VerifyMigration(int pid, string target, bool wasActive, IReadOnlySet<int> owners)
    {
        using var devices = new Endpoints();
        return AndroidAudioHost.WaitForPlaybackMigration(wasActive, Observe, WaitForAudio);
        (bool, bool, bool) Observe()
        {
            if (Defaults(devices).Contains(target)) return (false, false, false);
            bool safe = false, onTarget = false, elsewhere = false;
            foreach (var endpoint in devices.Active(Flow.Render))
            {
                bool here = Ids.Equals(endpoint.Id, target);
                using var dev = devices.Open(endpoint.Id);
                if (here)
                {
                    using var vol = Endpoints.Activate(dev, Endpoints.VolumeInterface);
                    safe = vol.UInt32(15) != 0;
                }
                Endpoints.VisitSessions(dev, (sessionPid, state, session) =>
                {
                    if (state != 1 || IsLoopback(sessionPid, session)) return;
                    if (AudioProcessTree.ContainsProcess(pid, (int)sessionPid))
                    {
                        if (here) onTarget = true;
                        else elsewhere = true;
                    }
                    else if (here && !owners.Any(p => AudioProcessTree.ContainsProcess(p, (int)sessionPid))) safe = false;
                });
            }
            return (safe, onTarget, elsewhere);
        }
    }

    private sealed class PlaybackWatch(int pid, string target, Action revoke) : ILocalPlaybackWatch
    {
        public void Refresh()
        {
            var (actual, playing) = ReadSpeaker(pid, includeReserved: true);
            if (playing && !Ids.Equals(actual, target)) revoke();
        }
        public void Dispose() { }
    }
}
#endif

#if SONICROUTE_PHONE
using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

internal static partial class PhoneMirrorIsolation
{
    private sealed class IsolationEvents : IDisposable
    {
        private readonly MtaLifetime _lifetime = new();
        private readonly IDisposable _topology;
        private readonly Dictionary<string, EndpointWatch> _outputs = new(Ids);
        private int _queued;

        internal IsolationEvents()
        {
            try { _topology = ProductAudio.WatchDevices(QueueRefresh); }
            catch { _lifetime.Dispose(); throw; }
        }

        private void QueueRefresh()
        {
            if (Interlocked.Exchange(ref _queued, 1) != 0) return;
            ThreadPool.QueueUserWorkItem(static events => events.Revalidate(), this, preferLocal: false);
        }

        private void Revalidate()
        {
            lock (LocalSilenceLeasePool.Gate)
            {
                Interlocked.Exchange(ref _queued, 0);
                if (!ReferenceEquals(_events, this)) return;
                try
                {
                    using var apartment = new MtaScope();
                    if (_users != 0) Refresh();
                    AlreadyMuted.Revalidate();
                    Legacy.Revalidate();
                    Mirrors.Revalidate();
                    Speakers.Revalidate();
                    if (_users == 0) TrimObserver();
                }
                catch (Exception ex) { ExceptionDiagnostics.Record("PhoneIsolation.Revalidate", ex); }
            }
        }

        // Run under the lease gate. Registrations survive state changes and are detached on last release.
        internal void Refresh()
        {
            using var devices = new Endpoints();
            var live = new HashSet<string>(Ids);
            foreach (var output in devices.Active(Flow.Render))
            {
                try
                {
                    using var dev = devices.Open(output.Id);
                    if (!_outputs.TryGetValue(output.Id, out var watch))
                    {
                        watch = new EndpointWatch(output.Id, dev, QueueRefresh);
                        _outputs.Add(output.Id, watch);
                    }
                    watch.RefreshSessions(dev, QueueRefresh);
                    live.Add(output.Id);
                }
                catch (COMException ex) { ExceptionDiagnostics.Record("PhoneIsolation.ObserveEndpoint", ex); }
            }
            foreach (var id in _outputs.Keys.Where(id => !live.Contains(id)).ToArray()) Remove(id);
        }

        internal bool Observes(string id) => _outputs.ContainsKey(id);

        internal void ClearEndpoints()
        {
            foreach (var id in _outputs.Keys.ToArray()) Remove(id);
        }

        private void Remove(string id)
        {
            if (_outputs.Remove(id, out var watch)) watch.Dispose();
        }

        public void Dispose()
        {
            try { ClearEndpoints(); }
            finally { try { _topology.Dispose(); } finally { _lifetime.Dispose(); } }
        }
    }

    private sealed class EndpointWatch : IDisposable
    {
        private readonly Com _volume;
        private readonly Subscription _mute;
        private readonly SessionCreationWatch _creation;
        private readonly Dictionary<string, (Com Control, Subscription Subscription)> _sessions = new(StringComparer.Ordinal);

        internal EndpointWatch(string id, Com device, Action changed)
        {
            _volume = Endpoints.Activate(device, Endpoints.VolumeInterface);
            try
            {
                int muted = (int)_volume.UInt32(15);
                var notice = new Notice(endpointMute: (next, context) =>
                {
                    int value = next ? 1 : 0;
                    if (Interlocked.Exchange(ref muted, value) == value) return;
                    if (!next)
                    {
                        AlreadyMuted.NoteUnmuted();
                        if (context != MuteContext)
                        {
                            Mirrors.NoteUnmuted(id);
                            Legacy.NoteUnmuted(id);
                            Speakers.NoteUnmuted(id);
                        }
                    }
                    changed();
                });
                _mute = Subscription.ForEndpoint(_volume, notice);
                try { _creation = new SessionCreationWatch(device, _ => changed(), includeSystem: true); }
                catch { Unsubscribe(_mute); throw; }
            }
            catch { _volume.Dispose(); throw; }
        }

        internal void RefreshSessions(Com device, Action changed)
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            Endpoints.VisitSessions(device, (pid, state, control) =>
            {
                if (state == 2 || IsLoopback(pid, control)) return;
                var control2 = control.Query(Endpoints.SessionControl2);
                bool retained = false;
                try
                {
                    string key = Endpoints.SessionId(control2, instance: true);
                    live.Add(key);
                    if (_sessions.ContainsKey(key)) return;
                    var subscription = Subscription.ForSession(control2, new Notice(changed, stateOnly: true));
                    _sessions.Add(key, (control2, subscription));
                    retained = true;
                }
                finally { if (!retained) control2.Dispose(); }
            });
            foreach (var key in _sessions.Keys.Where(key => !live.Contains(key)).ToArray()) RemoveSession(key);
        }

        private void RemoveSession(string key)
        {
            if (!_sessions.Remove(key, out var session)) return;
            try { Unsubscribe(session.Subscription); }
            finally { session.Control.Dispose(); }
        }

        private static void Unsubscribe(IDisposable owner)
        {
            try { owner.Dispose(); }
            catch (COMException ex) { ExceptionDiagnostics.Record("PhoneIsolation.Unregister", ex); }
        }

        public void Dispose()
        {
            Unsubscribe(_creation);
            foreach (var key in _sessions.Keys.ToArray()) RemoveSession(key);
            try { Unsubscribe(_mute); }
            finally { _volume.Dispose(); }
        }
    }
}
#endif

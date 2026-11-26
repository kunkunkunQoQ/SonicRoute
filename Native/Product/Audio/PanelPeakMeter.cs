using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

// UI requests and WASAPI notifications share one serialized worker in the MTA.
// The UI reads published scalar values without taking the worker's COM locks.
internal sealed class PanelPeakMeter(Action<PanelPeakMeter, bool> changed) : IDisposable
{
    [Flags]
    private enum Work { None = 0, Sample = 1, Sessions = 2, Topology = 4, State = 8, Catalog = 16, Stop = 32 }

    private readonly object _queue = new();
    private Work _pending;
    private bool _queued, _stopping;
    private MtaLifetime? _apartment;
    private IDisposable? _deviceWatch;
    private readonly Dictionary<string, Output> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Session Session, PeakValue Value)> _active = [];
    private volatile Dictionary<int, PeakValue>? _targets;
    private volatile bool _sampling;

    private sealed class PeakValue
    {
        internal float Next;
        internal volatile float Published;
    }

    internal bool IsSampling => _sampling;
    internal float Read(int pid) => _targets?.GetValueOrDefault(pid)?.Published ?? 0f;

    // Called on the window thread. No work is queued by a repaint of the same viewport.
    internal void SetTargets(HashSet<int> pids)
    {
        var previous = _targets;
        if (previous != null && previous.Count == pids.Count)
        {
            bool same = true;
            foreach (int pid in pids)
                if (!previous.ContainsKey(pid)) { same = false; break; }
            if (same) return;
        }

        var next = new Dictionary<int, PeakValue>(pids.Count);
        foreach (int pid in pids) next.Add(pid, previous?.GetValueOrDefault(pid) ?? new PeakValue());
        _targets = next;
        Request(previous == null ? Work.Topology : Work.Sessions);
    }

    internal void Sample()
    {
        if (_sampling) Request(Work.Sample);
    }

    private void Request(Work work)
    {
        lock (_queue)
        {
            if (_stopping) return;
            _stopping = work == Work.Stop;
            _pending |= work;
            if (_queued) return;
            _queued = true;
            ThreadPool.UnsafeQueueUserWorkItem(static meter => meter.Run(), this, preferLocal: false);
        }
    }

    private void Run()
    {
        while (true)
        {
            Work work;
            lock (_queue)
            {
                work = _pending;
                _pending = Work.None;
                if (work == Work.None) { _queued = false; return; }
            }
            try
            {
                using var mta = new MtaScope();
                if ((work & Work.Stop) != 0)
                {
                    Unsubscribe(_deviceWatch);
                    foreach (var output in _outputs.Values) output.Dispose();
                    _outputs.Clear();
                    _active.Clear();
                    _apartment?.Dispose();
                    return;
                }
                Update(work);
            }
            catch (COMException error) { ExceptionDiagnostics.Record("PanelPeakMeter.Worker", error); }
        }
    }

    private void Update(Work work)
    {
        var targets = _targets!;
        bool catalogChanged = (work & Work.Catalog) != 0;
        bool wasSampling = _sampling;
        if ((work & Work.Topology) != 0)
        {
            _apartment ??= new MtaLifetime();
            try { _deviceWatch ??= ProductAudio.WatchDevices(() => Request(Work.Topology | Work.Catalog)); }
            catch (COMException error) { ExceptionDiagnostics.Record("PanelPeakMeter.WatchDevices", error); }
            RefreshOutputs();
        }
        if ((work & (Work.Topology | Work.Sessions)) != 0)
            foreach (var output in _outputs.Values) output.RefreshSessions(targets);

        if ((work & (Work.Topology | Work.Sessions | Work.State)) != 0)
        {
            _active.Clear();
            foreach (var output in _outputs.Values)
                output.RefreshActive(targets, _active, ref catalogChanged);
        }

        foreach (var value in targets.Values) value.Next = 0f;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var (session, value) = _active[i];
            try { value.Next = MathF.Max(value.Next, Math.Clamp(session.Meter.Scalar(3), 0f, 1f)); }
            catch (COMException)
            {
                session.Failed = true;
                _active.RemoveAt(i);
                Request(Work.State);
            }
        }
        foreach (var value in targets.Values) value.Published = value.Next;
        _sampling = _active.Count != 0;
        if (catalogChanged || _sampling != wasSampling) changed(this, catalogChanged);
    }

    private void RefreshOutputs()
    {
        using var devices = new Endpoints();
        var live = devices.Active(Flow.Render).Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in _outputs.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            _outputs[id].Dispose();
            _outputs.Remove(id);
        }
        foreach (string id in live)
        {
            if (_outputs.ContainsKey(id)) continue;
            Com? device = null;
            try
            {
                device = devices.Open(id);
                var watch = new SessionCreationWatch(device, pid =>
                    Request(_targets?.ContainsKey(pid) == true ? Work.Sessions : Work.Catalog));
                _outputs.Add(id, new Output(device, watch, () => Request(Work.State)));
                device = null;
            }
            catch (COMException error) { ExceptionDiagnostics.Record("PanelPeakMeter.Endpoint", error); }
            finally { device?.Dispose(); }
        }
    }

    public void Dispose() => Request(Work.Stop);

    private static void Unsubscribe(IDisposable? token)
    {
        try { token?.Dispose(); }
        catch (COMException error) { ExceptionDiagnostics.Record("PanelPeakMeter.Unregister", error); }
    }

    private sealed class Output(Com device, SessionCreationWatch creation, Action stateChanged) : IDisposable
    {
        private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

        internal void RefreshSessions(Dictionary<int, PeakValue> targets)
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (targets.Count != 0)
                    Endpoints.VisitSessions(device, (pid, state, borrowed) =>
                    {
                        if (state == 2 || !targets.ContainsKey((int)pid)) return;
                        try
                        {
                            using var identity = borrowed.Query(Endpoints.SessionControl2);
                            string key = Endpoints.SessionId(identity, instance: true);
                            if (!_sessions.ContainsKey(key))
                                _sessions.Add(key, new Session((int)pid, borrowed, stateChanged));
                            live.Add(key);
                        }
                        catch (COMException) { } // The session can disappear while being attached.
                    });
            }
            catch (COMException error) { ExceptionDiagnostics.Record("PanelPeakMeter.Sessions", error); }
            foreach (string key in _sessions.Keys.Where(key => !live.Contains(key)).ToArray()) Remove(key);
        }

        internal void RefreshActive(Dictionary<int, PeakValue> targets, List<(Session, PeakValue)> active, ref bool catalogChanged)
        {
            List<string>? expired = null;
            foreach (var (key, session) in _sessions)
            {
                try
                {
                    uint state = session.Failed ? 2 : session.Control.UInt32(3);
                    if (state != 2 && targets.TryGetValue(session.Pid, out var value))
                    {
                        var volume = Endpoints.ReadVolume(session.Volume, false);
                        catalogChanged |= session.Muted.HasValue && session.Muted.Value != volume.Muted;
                        session.Muted = volume.Muted;
                        if (state == 1 && !volume.Muted && volume.Level > 0f) active.Add((session, value));
                        continue;
                    }
                }
                catch (COMException) { } // Expired or disconnected; release all of its handles.
                (expired ??= []).Add(key);
            }
            if (expired != null)
                foreach (string key in expired) Remove(key);
        }

        private void Remove(string key)
        {
            if (_sessions.Remove(key, out var session)) session.Dispose();
        }

        public void Dispose()
        {
            Unsubscribe(creation);
            foreach (var session in _sessions.Values) session.Dispose();
            _sessions.Clear();
            device.Dispose();
        }
    }

    private sealed class Session : IDisposable
    {
        internal readonly int Pid;
        internal readonly Com Control, Meter, Volume;
        private readonly Subscription _subscription;
        internal bool? Muted;
        internal bool Failed;

        internal Session(int pid, Com borrowed, Action stateChanged)
        {
            Pid = pid;
            Control = borrowed.Query(Endpoints.SessionControl2);
            try
            {
                Meter = borrowed.Query(ProductAudio.SessionPeakMeter);
                Volume = borrowed.Query(Endpoints.SimpleVolume);
                _subscription = Subscription.ForSession(Control, new Notice(stateChanged));
            }
            catch
            {
                Volume?.Dispose();
                Meter?.Dispose();
                Control.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Unsubscribe(_subscription);
            Volume.Dispose();
            Meter.Dispose();
            Control.Dispose();
        }
    }
}

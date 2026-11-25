using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

internal sealed partial class ProductAudio
{
    private readonly object _observationGate = new();
    private IDisposable? _observation;
    private long _observationGeneration, _notifications;
    internal static IDisposable WatchDevices(Action changed)
    {
        var notice = new Notice(changed);
        var source = DeviceNotificationHub.Subscribe(notice);
        return new ReleaseAction(() => { notice.Stop(); source.Dispose(); });
    }

    private sealed class ReleaseAction(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    internal IDisposable WatchEndpoint(Flow flow, string? endpoint, Action changed) => Observe(flow, endpoint, 0, changed);
    private IDisposable Observe(Flow flow, string? endpoint, int pid, Action changed)
    {
        using var apartment = new MtaScope();
        var lifetime = new MtaLifetime();
        List<(Com Source, Subscription? Token)> owners = [];
        Action release = () =>
        {
            try
            {
                for (int i = owners.Count - 1; i >= 0; --i)
                {
                    try { owners[i].Token?.Dispose(); }
                    catch (Exception e) { SonicRoute.Core.ExceptionDiagnostics.Record("Audio.Unregister", e); }
                    finally { owners[i].Source.Dispose(); }
                }
            }
            finally { lifetime.Dispose(); }
        };
        void Add(Com retained, bool deviceLevel)
        {
            int index = owners.Count;
            owners.Add((retained, null));
            var sink = new Notice(changed);
            var token = deviceLevel ? Subscription.ForEndpoint(retained, sink) : Subscription.ForSession(retained, sink);
            owners[index] = (retained, token);
        }
        try
        {
            using var devices = new Endpoints();
            if (pid == 0)
            {
                using var device = string.IsNullOrEmpty(endpoint) ? devices.Default(flow, Role.Console) : devices.Open(endpoint);
                Add(Endpoints.Activate(device, Endpoints.VolumeInterface), true);
            }
            else
                VisitAll(devices, flow, (p, state, control) =>
                {
                    if (p == pid && state != 2) Add(control.Query(Endpoints.SessionControl2), false);
                });
            if (!owners.Any(p => p.Token != null)) throw new InvalidOperationException("No available notification source.");
            return new ReleaseAction(release);
        }
        catch { release(); throw; }
    }

    private void BeginObservation(Flow flow, string target)
    {
        lock (_observationGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long generation = _observationGeneration + 1;
            void Changed()
            {
                if (Volatile.Read(ref _observationGeneration) == generation) Interlocked.Increment(ref _notifications);
            }
            var fresh = target == "devices" ? WatchDevices(Changed) :
                int.TryParse(target, out int pid) && pid > 0 ? Observe(flow, null, pid, Changed) :
                WatchEndpoint(flow, target == "default" ? null : target, Changed);
            Volatile.Write(ref _observationGeneration, generation);
            var old = _observation;
            _observation = fresh;
            old?.Dispose();
        }
    }
    private void StopObservation()
    {
        lock (_observationGate)
        {
            Interlocked.Increment(ref _observationGeneration);
            _observation?.Dispose();
            _observation = null;
        }
    }
}

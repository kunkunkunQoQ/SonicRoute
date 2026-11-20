#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute;

// Share endpoint mute ownership with app-only routes. A new output or a manual unmute revokes the
// group instead of silently changing the settings of a newly connected speaker.
internal sealed class SystemSpeakerLeases(Func<string[]> outputs, Func<string, IPcProcessSilenceLease?> acquire)
{
    private readonly HashSet<Group> _active = [];

    internal IPcProcessSilenceLease? Acquire()
    {
        lock (LocalSilenceLeasePool.Gate)
        {
            var ids = outputs().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length == 0) return null;
            var leases = new List<IPcProcessSilenceLease>();
            try
            {
                foreach (var id in ids)
                {
                    var lease = acquire(id);
                    if (lease == null) return null;
                    leases.Add(lease);
                }
                var group = new Group(this, ids, leases.ToArray());
                leases.Clear();
                _active.Add(group);
                try
                {
                    if (!group.Invalidated.IsCancellationRequested && group.Ids.SetEquals(outputs())) return group;
                }
                catch { group.Dispose(); throw; }
                group.Dispose();
                return null;
            }
            finally
            {
                ReleaseAll(leases);
            }
        }
    }

    private static void ReleaseAll(IEnumerable<IPcProcessSilenceLease> leases)
    {
        foreach (var lease in leases)
        {
            try { lease.Dispose(); }
            catch (Exception ex) { ExceptionDiagnostics.Record("WindowsLink.SystemSpeakerRestore", ex); }
        }
    }

    internal void Revalidate()
    {
        lock (LocalSilenceLeasePool.Gate)
        {
            if (_active.Count == 0) return;
            string[] ids;
            try { ids = outputs(); }
            catch (System.Runtime.InteropServices.COMException) { ids = []; }
            foreach (var group in _active.ToArray()) if (!group.Ids.SetEquals(ids)) group.Invalidate();
        }
    }

    private sealed class Group(SystemSpeakerLeases owner, string[] ids, IPcProcessSilenceLease[] leases) : IPcProcessSilenceLease
    {
        internal readonly HashSet<string> Ids = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(leases.Select(l => l.Invalidated).ToArray());
        public CancellationToken Invalidated => _stop.Token;
        internal void Invalidate() => _stop.Cancel();
        public void Release(bool restorePreviousEndpoint) => Dispose();
        public void Dispose()
        {
            lock (LocalSilenceLeasePool.Gate)
            {
                if (!owner._active.Remove(this)) return;
                _stop.Dispose();
                ReleaseAll(leases);
            }
        }
    }
}
#endif

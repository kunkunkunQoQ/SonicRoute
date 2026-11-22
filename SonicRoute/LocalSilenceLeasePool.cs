#if SONICROUTE_PHONE
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

/// <summary>Own only successful routes and mute changes whose previous state is known.</summary>
internal sealed class LocalSilenceLeasePool
{
    private readonly Func<IReadOnlySet<int>, string?, IReadOnlyList<string>> _find;
    private readonly Func<string, (bool Muted, bool Known)> _readMute;
    private readonly Func<string, bool, bool> _setMute;
    private readonly Func<int, (string? Multimedia, string? Console)> _readRoute;
    private readonly Func<int, (string? Multimedia, string? Console), bool> _applyRoute;
    // The two routing paths must not reserve the same output concurrently.
    internal static readonly object Gate = new();
    private readonly ConcurrentDictionary<string, Reservation> _reservations = new(StringComparer.OrdinalIgnoreCase);
    internal Func<int, string, bool, IReadOnlySet<int>, bool>? VerifyPlaybackRoute { get; init; }
    internal Func<int, string, Action, ILocalPlaybackWatch>? ObservePlayback { get; init; }

    internal LocalSilenceLeasePool(Func<string?> findEndpoint, Func<string, (bool Muted, bool Known)> readMute,
        Func<string, bool, bool> setMute, Func<int, (string? Multimedia, string? Console)> readRoute,
        Func<int, (string? Multimedia, string? Console), bool> applyRoute)
        : this((_, current) => (current ?? findEndpoint()) is string id ? [id] : [], readMute, setMute, readRoute, applyRoute)
    { }

    internal LocalSilenceLeasePool(Func<IReadOnlySet<int>, string?, IReadOnlyList<string>> find,
        Func<string, (bool Muted, bool Known)> readMute, Func<string, bool, bool> setMute,
        Func<int, (string? Multimedia, string? Console)> readRoute,
        Func<int, (string? Multimedia, string? Console), bool> applyRoute)
    {
        _find = find;
        _readMute = readMute;
        _setMute = setMute;
        _readRoute = readRoute;
        _applyRoute = applyRoute;
    }

    internal bool OwnsEndpoint(string endpoint) =>
        _reservations.TryGetValue(endpoint, out var reservation) && Volatile.Read(ref reservation.Committed);

    internal void NoteUnmuted(string endpoint)
    {
        if (_reservations.TryGetValue(endpoint, out var reservation))
            Volatile.Write(ref reservation.UserUnmuted, true);
    }

    internal void Revalidate()
    {
        if (_reservations.IsEmpty) return;
        lock (Gate)
        {
            foreach (var reservation in _reservations.Values)
            {
                if (reservation.Leases.Count == 0) { RestoreMute(reservation); continue; }
                if (reservation.Invalid) continue;
                try
                {
                    if (!Volatile.Read(ref reservation.UserUnmuted) && Eligible(reservation.Leases.Keys.ToHashSet(), reservation.Endpoint)
                        && _readMute(reservation.Endpoint) is (true, true))
                    {
                        foreach (var (pid, lease) in reservation.Leases)
                        {
                            if (!OwnsBothRoutes(pid, reservation.Endpoint)) lease.Invalidate();
                            else lease.Watch?.Refresh();
                        }
                        continue;
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException) { System.Diagnostics.Trace.WriteLine(ex); }
                reservation.Invalid = true;
                foreach (var lease in reservation.Leases.Values) lease.Invalidate();
            }
        }
    }

    internal IPcProcessSilenceLease? Acquire(int processId) => AcquireForPlayback(processId, wasActive: false);

    internal IPcProcessSilenceLease? AcquireForPlayback(int processId, bool wasActive)
    {
        lock (Gate)
        {
            var existing = _reservations.Values;
            if (existing.Any(r => r.Leases.ContainsKey(processId))) return null;
            // Share an already muted endpoint first. One app refusing that endpoint
            // must not invalidate another app there, or prevent trying a different one.
            foreach (var reservation in existing)
            {
                if (reservation.Leases.Count == 0) { RestoreMute(reservation); continue; }
                if (reservation.Invalid || Volatile.Read(ref reservation.UserUnmuted)) continue;
                var owners = reservation.Leases.Keys.Append(processId).ToHashSet();
                if (Eligible(owners, reservation.Endpoint) && TryCandidate(processId, reservation.Endpoint, wasActive, owners, reservation) is { } shared)
                    return shared;
            }

            var process = new HashSet<int> { processId };
            var candidates = _find(process, null);
            for (int i = 0; i < candidates.Count; i++)
            {
                string id = candidates[i];
                // Previous attempts may take time while Windows migrates a session.
                if (_reservations.ContainsKey(id) || (i != 0 && !Eligible(process, id))) continue;
                if (TryCandidate(processId, id, wasActive, process, null) is { } lease) return lease;
            }
            return null;
        }
    }

    private bool Eligible(IReadOnlySet<int> owners, string endpoint)
    {
        try { return _find(owners, endpoint).Contains(endpoint, StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        { System.Diagnostics.Trace.WriteLine(ex); return false; }
    }

    private Lease? TryCandidate(int processId, string endpoint, bool wasActive, IReadOnlySet<int> owners, Reservation? reservation)
    {
        try
        {
            var previous = _readRoute(processId);
            bool needsMute = false;
            if (reservation == null)
            {
                var state = _readMute(endpoint);
                if (!state.Known) return null;
                needsMute = !state.Muted;
                reservation = new Reservation(endpoint);
                _reservations[endpoint] = reservation;
            }

            Lease? lease = null;
            bool wrotePolicy = false, success = false;
            try
            {
                if (needsMute)
                {
                    if (!_setMute(endpoint, true)) return null;
                    reservation.ChangedMute = true;
                }
                if (_readMute(endpoint) is not (true, true)) return null;
                wrotePolicy = true; // A failed policy call may already have changed one role.
                if (!_applyRoute(processId, (endpoint, endpoint)) || !OwnsBothRoutes(processId, endpoint)
                    || !(VerifyPlaybackRoute?.Invoke(processId, endpoint, wasActive, owners) ?? true)) return null;
                lease = new Lease(this, processId, previous, reservation);
                reservation.Leases.Add(processId, lease);
                lease.Watch = ObservePlayback?.Invoke(processId, endpoint, lease.Invalidate);
                lease.Watch?.Refresh();
                if (!Volatile.Read(ref reservation.UserUnmuted) && !lease.Invalidated.IsCancellationRequested
                    && (lease.Watch == null || Eligible(owners, endpoint)))
                {
                    Volatile.Write(ref reservation.Committed, true);
                    success = true;
                    return lease;
                }
                return null;
            }
            finally
            {
                if (!success)
                {
                    try { if (wrotePolicy) RestoreOwnedRoles(processId, previous, endpoint); }
                    finally
                    {
                        if (lease != null) lease.Release(restorePreviousEndpoint: false);
                        else if (reservation.Leases.Count == 0) RestoreMute(reservation);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        { System.Diagnostics.Trace.WriteLine(ex); return null; }
    }

    private bool OwnsBothRoutes(int processId, string endpoint)
    {
        var current = _readRoute(processId);
        return string.Equals(current.Multimedia, endpoint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.Console, endpoint, StringComparison.OrdinalIgnoreCase);
    }

    private void RestoreOwnedRoles(int processId, (string? Multimedia, string? Console) previous, string endpoint)
    {
        bool NeedsRestore(string? role, string? original) =>
            string.Equals(role, endpoint, StringComparison.OrdinalIgnoreCase) && !string.Equals(original, endpoint, StringComparison.OrdinalIgnoreCase);
        try
        {
            var current = _readRoute(processId);
            var next = (NeedsRestore(current.Multimedia, previous.Multimedia) ? previous.Multimedia : current.Multimedia,
                NeedsRestore(current.Console, previous.Console) ? previous.Console : current.Console);
            if (next == current) return;
            _applyRoute(processId, next);
            var restored = _readRoute(processId);
            if (NeedsRestore(restored.Multimedia, previous.Multimedia) || NeedsRestore(restored.Console, previous.Console))
                throw new System.IO.IOException(L10n.T("Exp.LangRestoreFail"));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Do not treat a failed rollback as an ordinary candidate failure: a retry
            // would save the temporary endpoint as this application's original policy.
            throw new System.IO.IOException(L10n.T("Exp.LangRestoreFail"), ex);
        }
    }
    private void RestoreMute(Reservation reservation)
    {
        if (reservation.ChangedMute && !Volatile.Read(ref reservation.UserUnmuted))
        {
            var state = _readMute(reservation.Endpoint);
            if (!state.Known || (state.Muted && !_setMute(reservation.Endpoint, false))) return;
        }
        _reservations.TryRemove(reservation.Endpoint, out _);
    }
    private sealed class Reservation(string endpoint)
    {
        internal string Endpoint { get; } = endpoint;
        internal Dictionary<int, Lease> Leases { get; } = new();
        internal bool ChangedMute;
        internal bool UserUnmuted;
        internal bool Invalid;
        internal bool Committed;
    }
    private sealed class Lease(LocalSilenceLeasePool owner, int processId,
        (string? Multimedia, string? Console) previous, Reservation reservation) : IPcProcessSilenceLease
    {
        private int _released;
        private readonly CancellationTokenSource _invalidation = new();
        internal ILocalPlaybackWatch? Watch;
        public CancellationToken Invalidated => _invalidation.Token;
        internal void Invalidate()
        {
            lock (Gate) if (_released == 0) _invalidation.Cancel();
        }
        public void Release(bool restorePreviousEndpoint)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (Gate)
            {
                try
                {
                    if (restorePreviousEndpoint)
                        owner.RestoreOwnedRoles(processId, previous, reservation.Endpoint);
                }
                catch { /* The process may have exited before its routes could be restored. */ }
                finally
                {
                    reservation.Leases.Remove(processId);
                    try { Watch?.Dispose(); }
                    finally
                    {
                        _invalidation.Dispose();
                        if (reservation.Leases.Count == 0) owner.RestoreMute(reservation);
                    }
                }
            }
        }
        public void Dispose() => Release(true);
    }

}

internal interface ILocalPlaybackWatch : IDisposable
{
    void Refresh();
}

// When the user has already muted every output, process loopback can keep capturing
// without a routing or volume change. An endpoint becoming audible ends these leases.
internal sealed class AlreadyMutedOutputLeases(Func<bool> allMuted)
{
    private readonly object _gate = new();
    private long _unmuteRevision;
    private Group? _active;

    internal void NoteUnmuted() => Interlocked.Increment(ref _unmuteRevision);

    internal IPcProcessSilenceLease? Acquire(int processId)
    {
        lock (_gate)
        {
            long revision = Volatile.Read(ref _unmuteRevision);
            if (_active != null && (_active.Revision != revision || _active.Invalidated.IsCancellationRequested)) return null;
            if (!allMuted() || revision != Volatile.Read(ref _unmuteRevision)) return null;
            var group = _active ??= new Group(revision);
            return group.Processes.Add(processId) ? new Lease(this, group, processId) : null;
        }
    }

    internal void Revalidate()
    {
        lock (_gate)
            if (_active is { } group && !group.Invalidated.IsCancellationRequested
                && (group.Revision != Volatile.Read(ref _unmuteRevision) || !allMuted()))
                group.Invalidated.Cancel();
    }

    private sealed class Group(long revision)
    {
        internal long Revision { get; } = revision;
        internal CancellationTokenSource Invalidated { get; } = new();
        internal HashSet<int> Processes { get; } = new();
    }

    private sealed class Lease(AlreadyMutedOutputLeases owner, Group group, int pid) : IPcProcessSilenceLease
    {
        private int _released;
        public CancellationToken Invalidated { get; } = group.Invalidated.Token;
        public void Release(bool restorePreviousEndpoint) => Dispose();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._gate)
            {
                group.Processes.Remove(pid);
                if (group.Processes.Count != 0) return;
                owner._active = null;
                group.Invalidated.Dispose();
            }
        }
    }
}
#endif

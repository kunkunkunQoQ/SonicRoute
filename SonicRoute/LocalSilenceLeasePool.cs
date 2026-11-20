#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

/// <summary>Own only successful routes and mute changes whose previous state is known.</summary>
internal sealed class LocalSilenceLeasePool
{
    private readonly Func<IReadOnlySet<int>, string?, string?> _find;
    private readonly Func<string, (bool Muted, bool Known)> _readMute;
    private readonly Func<string, bool, bool> _setMute;
    private readonly Func<int, (string? Multimedia, string? Console)> _readRoute;
    private readonly Func<int, (string? Multimedia, string? Console), bool> _applyRoute;
    // The two routing paths must not reserve the same output concurrently.
    internal static readonly object Gate = new();
    private Reservation? _reservation;
    private readonly Dictionary<int, Lease> _leases = new();
    internal Func<int, string, bool>? VerifyPlaybackRoute { get; init; }
    internal Func<int, string, Action, ILocalPlaybackWatch>? ObservePlayback { get; init; }

    internal LocalSilenceLeasePool(Func<string?> findEndpoint, Func<string, (bool Muted, bool Known)> readMute,
        Func<string, bool, bool> setMute, Func<int, (string? Multimedia, string? Console)> readRoute,
        Func<int, (string? Multimedia, string? Console), bool> applyRoute)
        : this((_, current) => current ?? findEndpoint(), readMute, setMute, readRoute, applyRoute)
    { }

    internal LocalSilenceLeasePool(Func<IReadOnlySet<int>, string?, string?> find,
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

    internal bool OwnsEndpoint(string endpoint)
    {
        lock (Gate) return string.Equals(_reservation?.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase);
    }

    internal void NoteUnmuted(string endpoint)
    {
        var reservation = Volatile.Read(ref _reservation);
        if (reservation != null && string.Equals(reservation.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
            Volatile.Write(ref reservation.UserUnmuted, true);
    }

    internal void Revalidate()
    {
        lock (Gate)
        {
            if (_reservation is not { } reservation || reservation.Invalid) return;
            bool valid = false;
            try
            {
                valid = !Volatile.Read(ref reservation.UserUnmuted) && _find(_leases.Keys.ToHashSet(), reservation.Endpoint) != null
                    && _readMute(reservation.Endpoint) is (true, true);
                if (valid)
                    foreach (var (pid, lease) in _leases)
                    {
                        if (!OwnsBothRoutes(pid, reservation.Endpoint)) lease.Invalidate();
                        else lease.Watch?.Refresh();
                    }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            { valid = false; System.Diagnostics.Trace.WriteLine(ex); }
            finally
            {
                if (!valid)
                {
                    reservation.Invalid = true;
                    foreach (var lease in _leases.Values) lease.Invalidate();
                }
            }
        }
    }

    internal IPcProcessSilenceLease? Acquire(int processId)
    {
        lock (Gate)
        {
            if (_leases.ContainsKey(processId) || _reservation?.Invalid == true) return null;
            var previous = _readRoute(processId);
            string? endpoint = _find(_leases.Keys.Append(processId).ToHashSet(), _reservation?.Endpoint);
            if (endpoint == null) return null;
            if (_reservation == null)
            {
                var state = _readMute(endpoint);
                if (!state.Known || (!state.Muted && !_setMute(endpoint, true))) return null;
                _reservation = new Reservation(endpoint, !state.Muted);
            }
            try
            {
                if (_readMute(endpoint) is (true, true) && _applyRoute(processId, (endpoint, endpoint)) && OwnsBothRoutes(processId, endpoint)
                    && (VerifyPlaybackRoute?.Invoke(processId, endpoint) ?? true))
                {
                    var lease = new Lease(this, processId, previous, endpoint);
                    _leases.Add(processId, lease);
                    try
                    {
                        lease.Watch = ObservePlayback?.Invoke(processId, endpoint, lease.Invalidate);
                        lease.Watch?.Refresh();
                        if (lease.Watch != null && (_find(_leases.Keys.ToHashSet(), endpoint) == null || lease.Invalidated.IsCancellationRequested))
                        {
                            lease.Dispose();
                            return null;
                        }
                    }
                    catch { lease.Dispose(); throw; }
                    return lease;
                }
                // Windows sets two roles; a failed result may already have changed one.
                RestoreOwnedRoles(processId, previous, endpoint);
                return null;
            }
            catch { RestoreOwnedRoles(processId, previous, endpoint); throw; }
            finally { if (_leases.Count == 0) RestoreMute(); }
        }
    }

    private bool OwnsBothRoutes(int processId, string endpoint)
    {
        var current = _readRoute(processId);
        return string.Equals(current.Multimedia, endpoint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.Console, endpoint, StringComparison.OrdinalIgnoreCase);
    }

    private void RestoreOwnedRoles(int processId, (string? Multimedia, string? Console) previous, string endpoint)
    {
        try
        {
            var current = _readRoute(processId);
            var next = (string.Equals(current.Multimedia, endpoint, StringComparison.OrdinalIgnoreCase) ? previous.Multimedia : current.Multimedia,
                string.Equals(current.Console, endpoint, StringComparison.OrdinalIgnoreCase) ? previous.Console : current.Console);
            if (next != current) _applyRoute(processId, next);
        }
        catch { }
    }
    private void RestoreMute()
    {
        var reservation = _reservation;
        _reservation = null;
        if (reservation == null) return;
        if (reservation.ChangedMute && !Volatile.Read(ref reservation.UserUnmuted)
            && _readMute(reservation.Endpoint) is (true, true)) _setMute(reservation.Endpoint, false);
    }
    private sealed class Reservation(string endpoint, bool changedMute)
    {
        internal string Endpoint { get; } = endpoint;
        internal bool ChangedMute { get; } = changedMute;
        internal bool UserUnmuted;
        internal bool Invalid;
    }
    private sealed class Lease(LocalSilenceLeasePool owner, int processId,
        (string? Multimedia, string? Console) previous, string endpoint) : IPcProcessSilenceLease
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
                        owner.RestoreOwnedRoles(processId, previous, endpoint);
                }
                catch { /* The process may have exited before its routes could be restored. */ }
                finally
                {
                    owner._leases.Remove(processId);
                    Watch?.Dispose();
                    _invalidation.Dispose();
                    if (owner._leases.Count == 0) owner.RestoreMute();
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

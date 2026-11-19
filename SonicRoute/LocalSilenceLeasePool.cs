#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Threading;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

/// <summary>Own only successful routes and mute changes whose previous state is known.</summary>
internal sealed class LocalSilenceLeasePool(Func<string?> findEndpoint,
    Func<string, (bool Muted, bool Known)> readMute, Func<string, bool, bool> setMute,
    Func<int, (string? Multimedia, string? Console)> readRoute,
    Func<int, (string? Multimedia, string? Console), bool> applyRoute)
{
    private readonly object _gate = new();
    private string? _endpoint;
    private bool _changedMute;
    private readonly HashSet<int> _leases = new();

    internal IPcProcessSilenceLease? Acquire(int processId)
    {
        lock (_gate)
        {
            if (_leases.Contains(processId)) return null;
            var previous = readRoute(processId);
            if (_endpoint == null)
            {
                string? candidate = findEndpoint();
                if (candidate == null) return null;
                var state = readMute(candidate);
                if (!state.Known || (!state.Muted && !setMute(candidate, true))) return null;
                _endpoint = candidate;
                _changedMute = !state.Muted;
            }
            try
            {
                if (applyRoute(processId, (_endpoint, _endpoint)) && OwnsBothRoutes(processId, _endpoint))
                {
                    _leases.Add(processId);
                    return new Lease(this, processId, previous, _endpoint);
                }
                // Windows sets two roles; a failed result may already have changed one.
                RestoreRoute(processId, previous);
                return null;
            }
            catch { RestoreRoute(processId, previous); throw; }
            finally { if (_leases.Count == 0) RestoreMute(); }
        }
    }

    private bool OwnsBothRoutes(int processId, string endpoint)
    {
        var current = readRoute(processId);
        return string.Equals(current.Multimedia, endpoint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.Console, endpoint, StringComparison.OrdinalIgnoreCase);
    }

    private void RestoreRoute(int processId, (string? Multimedia, string? Console) previous)
    {
        try { applyRoute(processId, previous); }
        catch { }
    }
    private void RestoreMute()
    {
        try { if (_endpoint != null && _changedMute) setMute(_endpoint, false); }
        finally { _endpoint = null; _changedMute = false; }
    }
    private sealed class Lease(LocalSilenceLeasePool owner, int processId,
        (string? Multimedia, string? Console) previous, string endpoint) : IPcProcessSilenceLease
    {
        private int _released;
        public void Release(bool restorePreviousEndpoint)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._gate)
            {
                try
                {
                    if (restorePreviousEndpoint)
                    {
                        var current = owner.GetRoute(processId);
                        var next = (string.Equals(current.Multimedia, endpoint, StringComparison.OrdinalIgnoreCase)
                                ? previous.Multimedia : current.Multimedia,
                            string.Equals(current.Console, endpoint, StringComparison.OrdinalIgnoreCase)
                                ? previous.Console : current.Console);
                        if (next != current) owner.RestoreRoute(processId, next);
                    }
                }
                catch { /* The process may have exited before its routes could be restored. */ }
                finally
                {
                    owner._leases.Remove(processId);
                    if (owner._leases.Count == 0) owner.RestoreMute();
                }
            }
        }
        public void Dispose() => Release(true);
    }

    private (string? Multimedia, string? Console) GetRoute(int processId) => readRoute(processId);
}
#endif

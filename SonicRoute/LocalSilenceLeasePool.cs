#if SONICROUTE_PHONE
using System;
using System.Threading;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute;

/// <summary>Own only successful routes and mute changes whose previous state is known.</summary>
internal sealed class LocalSilenceLeasePool(Func<string?> findEndpoint,
    Func<string, (bool Muted, bool Known)> readMute, Func<string, bool, bool> setMute,
    Func<int, string?> readRoute, Func<int, string, bool> applyRoute)
{
    private readonly object _gate = new();
    private string? _endpoint;
    private bool _changedMute;
    private int _leases;

    internal IPcProcessSilenceLease? Acquire(int processId)
    {
        lock (_gate)
        {
            string? previous = readRoute(processId);
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
                if (applyRoute(processId, _endpoint))
                {
                    ++_leases;
                    return new Lease(this, processId, previous);
                }
                // Windows sets two roles; a failed result may already have changed one.
                RestoreRoute(processId, previous);
                return null;
            }
            catch { RestoreRoute(processId, previous); throw; }
            finally { if (_leases == 0) RestoreMute(); }
        }
    }

    private void RestoreRoute(int processId, string? previous)
    {
        try { applyRoute(processId, string.IsNullOrWhiteSpace(previous) ? AudioService.SystemDefaultDeviceId : previous); }
        catch { }
    }
    private void RestoreMute()
    {
        try { if (_endpoint != null && _changedMute) setMute(_endpoint, false); }
        finally { _endpoint = null; _changedMute = false; }
    }
    private sealed class Lease(LocalSilenceLeasePool owner, int processId, string? previous) : IPcProcessSilenceLease
    {
        private int _released;
        public void Release(bool restorePreviousEndpoint)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._gate)
            {
                if (restorePreviousEndpoint) owner.RestoreRoute(processId, previous);
                if (--owner._leases == 0) owner.RestoreMute();
            }
        }
        public void Dispose() => Release(true);
    }
}
#endif

#if SONICROUTE_PHONE
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

// Fallback for a shared speaker. Own only the endpoint mute, never the application's route or session volume.
internal sealed class SourceSpeakerLeases(Func<int, (string? Endpoint, bool Active)> findSpeaker,
    Func<string, (bool Muted, bool Known)> readMute, Func<string, bool, bool> setMute)
{
    private readonly ConcurrentDictionary<string, Speaker> _speakers = new(StringComparer.OrdinalIgnoreCase);
    internal Func<int, string, Action, ILocalPlaybackWatch>? ObservePlayback { get; init; }

    internal bool OwnsEndpoint(string endpoint) => _speakers.ContainsKey(endpoint);

    // Do not take the audio routing lock from a Core Audio callback.
    internal void NoteUnmuted(string endpoint)
    {
        if (_speakers.TryGetValue(endpoint, out var speaker)) Volatile.Write(ref speaker.UserUnmuted, true);
    }

    internal IPcProcessSilenceLease? Acquire(int pid, string? expected, bool requirePlayback)
    {
        lock (LocalSilenceLeasePool.Gate)
        {
            var (endpoint, active) = findSpeaker(pid);
            if (endpoint == null || !string.Equals(endpoint, expected, StringComparison.OrdinalIgnoreCase) || (requirePlayback && !active)) return null;
            return AcquireEndpointCore(endpoint, pid, requirePlayback);
        }
    }

    internal IPcProcessSilenceLease? AcquireEndpoint(string endpoint, int pid = 0) => AcquireEndpointCore(endpoint, pid, false);

    private IPcProcessSilenceLease? AcquireEndpointCore(string endpoint, int pid, bool requirePlayback)
    {
        lock (LocalSilenceLeasePool.Gate)
        {
            var before = readMute(endpoint);
            if (!before.Known) return null;
            if (!_speakers.TryGetValue(endpoint, out var speaker))
            {
                speaker = new Speaker(endpoint);
                _speakers[endpoint] = speaker;
            }
            else if (UserChanged(speaker) || !before.Muted) return null;

            var lease = new Lease(this, speaker, pid);
            speaker.Leases.Add(lease);
            try
            {
                if (before.Muted || setMute(endpoint, true))
                {
                    if (!before.Muted) speaker.ChangedMute = true;
                    if (pid != 0) lease.Watch = ObservePlayback?.Invoke(pid, endpoint, lease.Invalidate);
                    if (IsMuted(speaker) && IsStillOnSpeaker(lease, requirePlayback))
                    {
                        lease.Watch?.Refresh();
                        if (!lease.Invalidated.IsCancellationRequested && !UserChanged(speaker)) return lease;
                    }
                }
            }
            catch
            {
                lease.Dispose();
                throw;
            }
            lease.Dispose();
            return null;
        }
    }

    private static bool UserChanged(Speaker speaker) => Volatile.Read(ref speaker.UserUnmuted);
    private bool IsMuted(Speaker speaker) => !UserChanged(speaker) && readMute(speaker.Id) is (true, true);
    private bool IsStillOnSpeaker(Lease lease, bool requirePlayback = false)
    {
        if (lease.Pid == 0) return true;
        var (endpoint, active) = findSpeaker(lease.Pid);
        return (!requirePlayback || active) && string.Equals(endpoint, lease.Speaker.Id, StringComparison.OrdinalIgnoreCase);
    }

    internal void Revalidate()
    {
        if (_speakers.IsEmpty) return;
        lock (LocalSilenceLeasePool.Gate)
        {
            foreach (var speaker in _speakers.Values)
            {
                if (speaker.Leases.Count == 0) { Restore(speaker); continue; }
                bool stillMuted;
                try { stillMuted = IsMuted(speaker); }
                catch (COMException) { stillMuted = false; }
                foreach (var lease in speaker.Leases.ToArray())
                {
                    if (lease.Invalidated.IsCancellationRequested) continue;
                    try
                    {
                        if (stillMuted && IsStillOnSpeaker(lease)) lease.Watch?.Refresh();
                        else lease.Invalidate();
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException) { lease.Invalidate(); }
                }
            }
        }
    }

    private void Restore(Speaker speaker)
    {
        if (speaker.ChangedMute && !UserChanged(speaker))
        {
            // A disconnected device can come back. Retry only on the next device notification.
            var current = readMute(speaker.Id);
            if (!current.Known || current.Muted && !setMute(speaker.Id, false)) return;
        }
        _speakers.TryRemove(speaker.Id, out _);
    }

    private sealed class Speaker(string id)
    {
        internal string Id { get; } = id;
        internal bool ChangedMute;
        internal bool UserUnmuted;
        internal HashSet<Lease> Leases { get; } = [];
    }

    private sealed class Lease(SourceSpeakerLeases owner, Speaker speaker, int pid) : IPcProcessSilenceLease
    {
        private readonly CancellationTokenSource _stop = new();
        private bool _released;
        internal int Pid { get; } = pid;
        internal Speaker Speaker { get; } = speaker;
        internal ILocalPlaybackWatch? Watch;
        public CancellationToken Invalidated => _stop.Token;

        internal void Invalidate()
        {
            lock (LocalSilenceLeasePool.Gate) if (!_released) _stop.Cancel();
        }

        public void Release(bool restorePreviousEndpoint) => Dispose();

        public void Dispose()
        {
            lock (LocalSilenceLeasePool.Gate)
            {
                if (_released) return;
                _released = true;
                Speaker.Leases.Remove(this);
                try { Watch?.Dispose(); }
                finally
                {
                    _stop.Dispose();
                    if (Speaker.Leases.Count == 0) owner.Restore(Speaker);
                }
            }
        }
    }
}
#endif

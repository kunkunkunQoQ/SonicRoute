#if SONICROUTE_PHONE
using System.IO;
using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute;

internal static partial class LocalAppOutputMirrors
{
    private static LocalIsolationNotifications? _phoneIsolationWatch;

    internal static string? GetExclusivePhone(int pid, out bool mutedSpeaker)
    {
        mutedSpeaker = false;
        if (!Routes.TryGetValue(pid, out var route) || !route.PhoneOnly) return null;
        mutedSpeaker = route.MutedSpeaker;
        return route.Sinks.FirstOrDefault()?.Id;
    }

    internal static Task SetExclusivePhoneOutputsAsync(int pid, string[] phones, bool allowSpeakerMute)
    {
        if (phones.Length == 0 || phones.Any(id => !AndroidOutputIdentity.IsPhoneOutput(id)))
            throw new ArgumentException(L10n.T("Phone.Err.InvalidPhoneOutputDevice"));
        return SetOutputsAsync(pid, null, phones, phoneOnly: true, allowSpeakerMute, default);
    }

    internal static Task PhoneRemovedAsync(AndroidDevice device, bool recover = false) =>
        PruneAsync((_, sink) => sink.Player is AndroidPhonePcmPlayer phone && AndroidDeviceIdentity.MatchesSavedDevice(device, phone.Device), recover);

    static partial void ReleaseUnusedPhoneWatch()
    {
        if (_phoneIsolationWatch != null && !Routes.Values.Any(r => r.PhoneOnly))
        {
            _ = _phoneIsolationWatch.StopAsync();
            _phoneIsolationWatch = null;
        }
    }

    static partial void NotifyPrunedPhoneOutputs(Route route, Sink[] previous, Sink[] retained, bool recover)
    {
        foreach (var sink in previous)
            if (sink.Player is AndroidPhonePcmPlayer phone && !retained.Contains(sink))
                AppPlaybackOutputs.OutputEnded(route.ProcessId, sink.Id, recover && !AndroidAudioHost.Phone.IsRemoved(phone.Device));
    }

    private sealed partial class Route
    {
        private IPcProcessSilenceLease? _silence;
        private CancellationTokenRegistration _invalidated;
        internal bool PhoneOnly => Volatile.Read(ref _silence) != null;
        internal bool MutedSpeaker { get; private set; }

        partial void UpdateIsolation(bool phoneOnly, bool allowSpeakerMute)
        {
            if (phoneOnly && !PhoneOnly) Isolate(allowSpeakerMute);
            else if (!phoneOnly) ReleaseIsolation();
        }

        private void Isolate(bool allowSpeakerMute)
        {
            _phoneIsolationWatch ??= new LocalIsolationNotifications();
            IPcProcessSilenceLease? lease = null;
            (string? Endpoint, bool Active) original = default;
            try { lease = AndroidAudioHost.AcquireMirrorSilenceLease(ProcessId, out original); }
            catch (Exception ex) when (allowSpeakerMute && (ex is InvalidOperationException or COMException))
            {
                ExceptionDiagnostics.Record("LocalAppOutputMirrors.Isolate", ex);
            }
            bool muteSpeaker = lease == null && allowSpeakerMute;
            if (muteSpeaker) lease = AndroidAudioHost.AcquireSpeakerFallback(ProcessId, original);
            if (lease == null) throw new IOException(L10n.T("Phone.AppKeptOutput"));
            try
            {
                _invalidated = lease.Invalidated.Register(() => _ = PruneAsync((route, _) => route == this && _silence == lease));
                lease.Invalidated.ThrowIfCancellationRequested();
                MutedSpeaker = muteSpeaker;
                Volatile.Write(ref _silence, lease);
            }
            catch { _invalidated.Dispose(); lease.Dispose(); throw; }
        }

        partial void ReleaseIsolation()
        {
            if (!PhoneOnly) return;
            _invalidated.Dispose();
            Interlocked.Exchange(ref _silence, null)?.Dispose();
            MutedSpeaker = false;
        }
    }
}
#endif

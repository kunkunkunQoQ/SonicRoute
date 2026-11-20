#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.WindowsLink;

namespace SonicRoute;

internal static partial class AndroidAudioHost
{
    private static readonly SourceSpeakerLeases SourceSpeakers = new(ReadAppSpeaker, ReadOutputMute,
        (id, mute) => SetOutputMute(id, mute, allowDefault: true))
    {
        ObservePlayback = (pid, id, revoke) => new WindowsLinkPlaybackWatch(pid, id, revoke)
    };

    private static readonly SystemSpeakerLeases SystemSpeakers = new(ActiveRenderIds,
        id => SilenceLeases.OwnsEndpoint(id) || MirrorSilenceLeases.OwnsEndpoint(id) ? null : SourceSpeakers.AcquireEndpoint(id));

    private static string[] ActiveRenderIds()
    {
        using var devices = new WasapiDeviceLease.Enumerator();
        var ids = new List<string>();
        foreach (var lease in devices.ActiveRender())
            using (lease) ids.Add(lease.Device.ID);
        return ids.ToArray();
    }

    internal static IPcProcessSilenceLease? AcquireSystemSpeakerSilence() => SystemSpeakers.Acquire();

    internal static bool HasWindowsLinkSpeakerFallback()
    {
        using var devices = new WasapiDeviceLease.Enumerator();
        using var speaker = devices.TryDefault(Role.Multimedia);
        return speaker != null;
    }

    internal static IPcProcessSilenceLease? AcquireWindowsLinkSpeakerFallback(int processId) =>
        AcquireSpeakerFallback(processId, ReadAppSpeaker(processId));

    internal static bool CanMuteAppSpeaker(int processId) => processId > 0 && processId != Environment.ProcessId
        && TryGetPcAppPhoneOutput(processId) == null && ReadAppSpeaker(processId).Endpoint != null;

    internal static (IPcProcessSilenceLease? Lease, bool MutedSpeaker) AcquireAppSilence(int processId,
        bool allowSpeakerMute, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        (string? Endpoint, bool Active) original = default;
        try
        {
            if (AcquireMirrorSilenceLease(processId, out original) is { } spare) return (spare, false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            if (!allowSpeakerMute && original.Endpoint == null) throw;
            ExceptionDiagnostics.Record("AndroidAudioHost.AcquireAppSilence", ex);
        }
        token.ThrowIfCancellationRequested();
        if (!allowSpeakerMute)
        {
            if (original.Endpoint != null || CanMuteAppSpeaker(processId))
                throw new SourceSpeakerConsentRequiredException(L10n.T("Phone.WindowsLinkMuteSpeakerDesc"));
            return (null, false);
        }
        var fallback = AcquireSpeakerFallback(processId, original);
        return (fallback, fallback != null);
    }

    internal static IPcProcessSilenceLease? AcquireSpeakerFallback(int processId, (string? Endpoint, bool Active) original)
    {
        if (processId <= 0 || processId == Environment.ProcessId || TryGetPcAppPhoneOutput(processId) != null) return null;
        if (original.Active && original.Endpoint == null) return null;
        string? expected = original.Endpoint;
        if (expected == null)
        {
            var (media, console) = ReadSilenceRoles(processId);
            if (media != null && console != null && !string.Equals(media, console, StringComparison.OrdinalIgnoreCase)) return null;
            using var devices = new WasapiDeviceLease.Enumerator();
            using var system = devices.TryDefault(Role.Multimedia);
            expected = media ?? console ?? system?.Device.ID;
        }
        if (expected == null) return null;
        // A policy rollback may finish before the player reopens its original WASAPI session.
        // Never mute a spare just because it transiently hosts the last active session.
        bool requirePlayback = original.Active || original.Endpoint == null;
        bool restored = WaitForOriginalSpeaker(expected, requirePlayback,
            () => ReadAppSpeaker(processId), static () => System.Threading.Thread.Sleep(100));
        return restored ? SourceSpeakers.Acquire(processId, expected, requirePlayback) : null;
    }

    private static bool WaitForOriginalSpeaker(string expected, bool requirePlayback,
        Func<(string? Endpoint, bool Active)> observe, Action wait)
    {
        int matches = 0;
        for (int sample = 0; sample < 20; sample++)
        {
            var current = observe();
            if (string.Equals(current.Endpoint, expected, StringComparison.OrdinalIgnoreCase) && (!requirePlayback || current.Active))
            {
                if (!requirePlayback || ++matches == 3) return true;
            }
            else matches = 0;
            if (sample < 19) wait();
        }
        return false;
    }

    private static (string? Endpoint, bool Active) ReadAppSpeaker(int processId)
    {
        using var devices = new WasapiDeviceLease.Enumerator();
        string? active = null;
        var dormant = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lease in devices.ActiveRender())
        using (lease)
        {
            var device = lease.Device;
            string id = device.ID;
            if (!SilenceLeases.OwnsEndpoint(id) && !MirrorSilenceLeases.OwnsEndpoint(id)) usable.Add(id);
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                var state = session.State;
                if (state == AudioSessionState.AudioSessionStateExpired) continue;
                int pid = (int)session.GetProcessID;
                if (!WindowsLinkPlaybackWatch.ContainsProcess(processId, pid) || WindowsLinkPlaybackWatch.IsOwnLoopback(session, pid)) continue;
                if (state == AudioSessionState.AudioSessionStateInactive)
                {
                    dormant.Add(id);
                    continue;
                }
                if (active != null && !string.Equals(active, id, StringComparison.OrdinalIgnoreCase)) return (null, true);
                active = id;
                break;
            }
        }
        if (active != null) return (usable.Contains(active) ? active : null, true);

        var (media, console) = ReadSilenceRoles(processId);
        if (media != null && console != null && !string.Equals(media, console, StringComparison.OrdinalIgnoreCase)) return (null, false);
        string? preferred = media ?? console;
        if (preferred == null && dormant.Count == 1) preferred = System.Linq.Enumerable.Single(dormant);
        if (preferred != null) return (usable.Contains(preferred) ? preferred : null, false);
        using var speaker = devices.TryDefault(Role.Multimedia);
        string? defaultId = speaker?.Device.ID;
        return (defaultId != null && usable.Contains(defaultId) && (dormant.Count == 0 || dormant.Contains(defaultId)) ? defaultId : null, false);
    }
}
#endif

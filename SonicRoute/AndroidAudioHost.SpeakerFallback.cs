#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

internal static partial class AndroidAudioHost
{
    private static readonly SourceSpeakerLeases SourceSpeakers = new(FindAppSpeaker, ReadOutputMute,
        (id, mute) => SetOutputMute(id, mute, allowDefault: true))
    {
        ObservePlayback = (pid, id, revoke) => new LocalPlaybackWatch(pid, id, revoke)
    };

    internal static IPcProcessSilenceLease? AcquireSpeakerFallback(int processId) =>
        processId > 0 && processId != Environment.ProcessId && TryGetPcAppPhoneOutput(processId) == null
            ? SourceSpeakers.Acquire(processId) : null;

    internal static bool CanMuteAppSpeaker(int processId) => processId > 0 && processId != Environment.ProcessId
        && TryGetPcAppPhoneOutput(processId) == null && FindAppSpeaker(processId) != null;

    private static string? FindAppSpeaker(int processId)
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
                if (state == AudioSessionState.AudioSessionStateExpired ||
                    !LocalPlaybackWatch.ContainsProcess(processId, (int)session.GetProcessID)) continue;
                if (state == AudioSessionState.AudioSessionStateInactive)
                {
                    dormant.Add(id);
                    continue;
                }
                if (active != null && !string.Equals(active, id, StringComparison.OrdinalIgnoreCase)) return null;
                active = id;
                break;
            }
        }
        if (active != null) return usable.Contains(active) ? active : null;

        var (media, console) = ReadSilenceRoles(processId);
        if (media != null && console != null && !string.Equals(media, console, StringComparison.OrdinalIgnoreCase)) return null;
        string? preferred = media ?? console;
        if (preferred == null && dormant.Count == 1) preferred = System.Linq.Enumerable.Single(dormant);
        if (preferred != null) return usable.Contains(preferred) ? preferred : null;
        using var speaker = devices.TryDefault(Role.Multimedia);
        string? defaultId = speaker?.Device.ID;
        return defaultId != null && usable.Contains(defaultId) && (dormant.Count == 0 || dormant.Contains(defaultId)) ? defaultId : null;
    }
}
#endif

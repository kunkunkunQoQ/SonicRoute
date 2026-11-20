#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

internal static partial class AndroidAudioHost
{
    private static readonly SourceSpeakerLeases SourceSpeakers = new(FindAppSpeaker, ReadWindowsLinkOutputMute,
        (id, mute) => SetWindowsLinkMute(id, mute, allowDefault: true))
    {
        ObservePlayback = (pid, id, revoke) => new WindowsLinkPlaybackWatch(pid, id, revoke)
    };

    private static readonly SystemSpeakerLeases SystemSpeakers = new(ActiveRenderIds,
        id => SilenceLeases.OwnsEndpoint(id) || WindowsLinkSilenceLeases.OwnsEndpoint(id) ? null : SourceSpeakers.AcquireEndpoint(id));

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
            if (!SilenceLeases.OwnsEndpoint(id) && !WindowsLinkSilenceLeases.OwnsEndpoint(id)) usable.Add(id);
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                var state = session.State;
                if (state == AudioSessionState.AudioSessionStateExpired ||
                    !WindowsLinkPlaybackWatch.ContainsProcess(processId, (int)session.GetProcessID)) continue;
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

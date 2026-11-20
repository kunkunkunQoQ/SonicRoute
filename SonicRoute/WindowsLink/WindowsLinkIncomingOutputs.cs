#if SONICROUTE_PHONE
using System.IO;
using System.Runtime.CompilerServices;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using SonicRoute.WindowsLink;

namespace SonicRoute;

internal static class WindowsLinkIncomingOutputs
{
    internal sealed class SpeakerConsent(string message) : Exception(message);
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
    private static readonly ConditionalWeakTable<LinkAudioPlayback, SemaphoreSlim> Changes = new();

    internal static IEnumerable<AudioDeviceInfo> Choices(WindowsLinkAudioSource source, IEnumerable<AudioDeviceInfo> choices)
        => PeerChoices(choices, (service, peer) => CanControlSource(source, peer) || service.CanRelayTo(source, peer));

    internal static IEnumerable<AudioDeviceInfo> PhoneChoices(IEnumerable<AudioDeviceInfo> choices) =>
        PeerChoices(choices, static (_, peer) => WindowsLinkService.CanReceiveExternalAudio(peer));

    private static IEnumerable<AudioDeviceInfo> PeerChoices(IEnumerable<AudioDeviceInfo> choices,
        Func<WindowsLinkService, LinkPeer, bool> allowed)
    {
        var service = WindowsLinkHub.Service;
        var peers = service?.Peers.Where(p => p.Connected).ToDictionary(p => p.Id, Ids);
        var listed = new HashSet<string>(Ids);
        foreach (var choice in choices)
        {
            if (!WindowsLinkOutputIdentity.TryParsePeerId(choice.Id, out string id)) yield return choice;
            else if (peers != null && peers.TryGetValue(id, out var target) && allowed(service!, target))
            {
                listed.Add(target.Id);
                yield return choice;
            }
        }
        if (service == null || peers == null) yield break;
        foreach (var target in peers.Values)
        {
            if (listed.Contains(target.Id) || !allowed(service, target)) continue;
            yield return new AudioDeviceInfo
            {
                Id = WindowsLinkOutputIdentity.FormatDeviceId(target.Id),
                DisplayName = string.Format(L10n.T("Phone.WindowsLinkOutputFormat"), target.DisplayName),
                Flow = SonicRoute.Core.Interop.EDataFlow.eRender
            };
        }
    }

    private static bool CanControlSource(WindowsLinkAudioSource source, LinkPeer peer) =>
        !source.IsRelayed && Ids.Equals(peer.Id, source.PeerId) && peer.SupportsSourcePlayback;

    internal static HashSet<string> GetSelected(WindowsLinkAudioSource source)
    {
        var selected = source.Session.GetOutputs().Select(id => id ?? AudioService.SystemDefaultDeviceId).ToHashSet(Ids);
        var service = WindowsLinkHub.Service;
        if (service?.GetIncomingMedia(source.StreamId) is { DestinationOnly: false } &&
            service.Peers.Any(peer => peer.Connected && CanControlSource(source, peer)))
            selected.Add(WindowsLinkOutputIdentity.FormatDeviceId(source.PeerId));
        return selected;
    }

    internal static async Task ApplyAsync(WindowsLinkAudioSource source, string[] selection, bool allowSpeakerMute)
    {
        var gate = Changes.GetValue(source.Session, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var service = WindowsLinkHub.Service;
            var track = service?.GetIncomingMedia(source.StreamId);
            if (service == null || track == null) throw new IOException(L10n.T("Phone.Err.NoAudioRouteOrEnded"));
            var peers = service.Peers.Where(p => p.Connected).ToDictionary(p => p.Id, Ids);
            bool canControl = peers.TryGetValue(source.PeerId, out var origin) && CanControlSource(source, origin);
            string originId = WindowsLinkOutputIdentity.FormatDeviceId(source.PeerId);
            bool playOnSource = selection.Contains(originId, Ids);
            foreach (var output in selection)
                if (WindowsLinkOutputIdentity.TryParsePeerId(output, out var id) &&
                    (!peers.TryGetValue(id, out var target) || !CanControlSource(source, target) && !service.CanRelayTo(source, target)))
                    throw new IOException(L10n.T("Ov.CurrentUnavailable"));

            bool changeSource = canControl && playOnSource == track.DestinationOnly;
            if (selection.Length == 1 && playOnSource)
            {
                if (changeSource) await service.SetSourcePlaybackAsync(source.StreamId, true, false).ConfigureAwait(false);
                await service.StopStreamAsync(source.StreamId).ConfigureAwait(false);
                return;
            }

            var receivers = selection.Where(id => !Ids.Equals(id, originId)).Select(id => AudioService.IsSystemDefault(id) ? null : id).ToArray();
            if (changeSource && !playOnSource && !allowSpeakerMute && track.Kind == LinkMediaKind.SystemAudio)
                throw new SpeakerConsent(L10n.T("Phone.WindowsLinkMuteAllOutputs"));

            var previous = source.Session.GetOutputs();
            await source.Session.SetOutputsAsync(receivers, remember: !changeSource).ConfigureAwait(false);
            if (!changeSource) return;
            try
            {
                await service.SetSourcePlaybackAsync(source.StreamId, playOnSource, allowSpeakerMute).ConfigureAwait(false);
                if (!previous.ToHashSet(Ids).SetEquals(receivers)) source.Session.RememberOutputs(previous);
            }
            catch (Exception ex)
            {
                try { await source.Session.SetOutputsAsync(previous, remember: false).ConfigureAwait(false); }
                catch (Exception rollback) { ExceptionDiagnostics.Record("WindowsLink.IncomingOutputs.Restore", rollback); }
                if (!playOnSource && !allowSpeakerMute && ex is SourceSpeakerConsentRequiredException)
                    throw new SpeakerConsent(L10n.T("Phone.WindowsLinkMuteSpeakerDesc"));
                throw;
            }
        }
        finally { gate.Release(); }
    }
}
#endif

#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.WindowsLink;

namespace SonicRoute;

internal static partial class AppPlaybackOutputs
{
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
    private static readonly Dictionary<int, ProcessGate> Gates = [];
    private sealed class ProcessGate { internal readonly SemaphoreSlim Changes = new(1, 1); internal int Users; }

    private static LinkMedia[] Streams(int pid) => WindowsLinkHub.Service?.GetOutgoingAppStreams(pid) ?? [];

    private static string Endpoint(string? id) => string.IsNullOrWhiteSpace(id) || AudioService.IsSystemDefault(id)
        ? AudioService.SystemDefaultDeviceId : AudioPolicyConfig.UnpackDeviceId(id);

    internal static HashSet<string> GetSelected(int pid, string? persistedEndpoint)
    {
        var streams = Streams(pid);
        var outputs = LocalAppOutputMirrors.GetExtraOutputs(pid).ToHashSet(Ids);
        if (!streams.Any(s => s.DestinationOnly) && LocalAppOutputMirrors.GetExclusivePhone(pid, out _) == null)
            outputs.Add(Endpoint(AndroidAudioHost.TryGetPcAppPhoneOutput(pid) ?? persistedEndpoint));
        foreach (var stream in streams) outputs.Add(WindowsLinkOutputIdentity.FormatDeviceId(stream.PeerId));
        return outputs;
    }

    internal static async Task<(bool Success, int HResult, string Message)> SetAsync(
        AudioAppInfo app, string[] selected, string? preferredPrimary, bool allowSpeakerMute)
    {
        var outputs = selected.Select(Endpoint).Distinct(Ids).ToArray();
        if (outputs.Length == 0) outputs = [AudioService.SystemDefaultDeviceId];
        if (app.IsExternal)
        {
            if (WindowsLinkHub.FindIncomingAudio(app.ExternalSourceId) is { } incoming)
            {
                try
                {
                    await WindowsLinkIncomingOutputs.ApplyAsync(incoming, outputs, allowSpeakerMute).ConfigureAwait(false);
                    return (true, 0, L10n.T("History.Status.success"));
                }
                catch (Exception ex) { return RouteError(ex); }
            }
            if (ApplicationAudio.ExternalSession(app) is IMultiOutputAudioSession multi)
            {
                try
                {
                    await multi.SetOutputsAsync(outputs.Select(id => AudioService.IsSystemDefault(id) ? null : id).ToArray()).ConfigureAwait(false);
                    return (true, 0, L10n.T("History.Status.success"));
                }
                catch (Exception ex) { return RouteError(ex); }
            }
            return outputs.Length == 1 ? await ApplicationAudio.ApplyEndpointAsync(app, EDataFlow.eRender, outputs[0])
                : (false, -1, L10n.T("Phone.StatusOperationFailed"));
        }
        if (app.ProcessId == 0 || app.ProcessId > int.MaxValue)
            return (false, -1, L10n.T("Phone.Err.InvalidProcessId"));
        int pid = (int)app.ProcessId;
        bool reset = selected.Length == 0;
        return await SerializeAsync(pid, async () =>
        {
            var processHistory = HistoryFor(pid);
            if (reset) Forget(pid, processHistory);
            long epoch = Volatile.Read(ref _historyEpoch);
            string? saved = AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender);
            var before = GetSelected(pid, saved);
            var result = await ApplyAsync(pid, outputs, Endpoint(preferredPrimary), allowSpeakerMute).ConfigureAwait(false);
            if (result.Success && !reset && Volatile.Read(ref _historyEpoch) == epoch)
                Remember(pid, before.ToArray(), outputs, Endpoint(preferredPrimary), allowSpeakerMute);
            return result;
        }).ConfigureAwait(false);
    }

    private static async Task<T> SerializeAsync<T>(int pid, Func<Task<T>> action)
    {
        ProcessGate gate;
        lock (Gates)
        {
            if (!Gates.TryGetValue(pid, out gate!)) Gates.Add(pid, gate = new ProcessGate());
            gate.Users++;
        }
        await gate.Changes.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(action).ConfigureAwait(false);
        }
        finally
        {
            gate.Changes.Release();
            lock (Gates)
                if (--gate.Users == 0) { Gates.Remove(pid); gate.Changes.Dispose(); }
        }
    }

    private static async Task<(bool Success, int HResult, string Message)> ApplyAsync(
        int pid, string[] outputs, string preferred, bool allowSpeakerMute)
    {
        var undo = new Stack<Func<Task>>();
        var service = WindowsLinkHub.Service;
        var peers = outputs.Where(WindowsLinkOutputIdentity.IsWindowsLinkOutput).ToArray();
        var physical = outputs.Where(id => !WindowsLinkOutputIdentity.IsWindowsLinkOutput(id) && !AndroidOutputIdentity.IsPhoneOutput(id)).ToArray();
        var phones = outputs.Where(AndroidOutputIdentity.IsPhoneOutput).ToArray();
        var previous = Streams(pid);
        string? previousPhone = AndroidAudioHost.TryGetPcAppPhoneOutput(pid);
        bool previousPhoneOnly = LocalAppOutputMirrors.GetExclusivePhone(pid, out bool previousSpeakerMute) != null;
        string[] oldExtras = LocalAppOutputMirrors.GetExtraOutputs(pid);
        string? oldPrimary = previousPhoneOnly || previous.Any(s => s.DestinationOnly) ? null
            : previousPhone ?? Endpoint(AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender));
        string? primary = physical.FirstOrDefault(AudioService.IsSystemDefault)
            ?? physical.FirstOrDefault(id => Ids.Equals(id, preferred)) ?? physical.FirstOrDefault();
        bool phoneOnly = primary == null && peers.Length == 0;
        var extras = physical.Concat(phones).Where(id => !Ids.Equals(id, primary)).ToArray();

        // Retain the current mute owner if possible. An older receiver can remain a shared output.
        string? owner = null;
        if (peers.Length != 0 && primary == null)
        {
            owner = previous.Where(s => s.DestinationOnly).Select(s => WindowsLinkOutputIdentity.FormatDeviceId(s.PeerId))
                .FirstOrDefault(id => peers.Contains(id, Ids));
            owner ??= peers.OrderByDescending(id => Ids.Equals(id, preferred)).FirstOrDefault(id =>
                WindowsLinkOutputIdentity.TryParsePeerId(id, out var peerId)
                && service?.Peers.Any(p => Ids.Equals(p.Id, peerId) && p.Connected && p.Capabilities.HasFlag(LinkCapabilities.AudioReady)) == true);
            if (owner == null) return (false, -1, L10n.T("Phone.WindowsLinkExclusiveUnavailable"));
        }
        try
        {
            // Android's original per-process route can own a dedicated mute endpoint. Release it
            // before requesting exclusive Windows playback, preserving its original policy on failure.
            if (previousPhone != null) StopPhone();
            if (previousPhoneOnly && !phoneOnly)
            {
                await LocalAppOutputMirrors.SetExtraOutputsAsync(pid, null, oldExtras).ConfigureAwait(false);
                undo.Push(RestoreMirrors);
            }
            foreach (var output in peers.OrderByDescending(id => Ids.Equals(id, owner)))
            {
                var existing = previous.FirstOrDefault(s => Ids.Equals(WindowsLinkOutputIdentity.FormatDeviceId(s.PeerId), output));
                bool exclusive = Ids.Equals(output, owner);
                if (existing != null && existing.DestinationOnly == exclusive) continue;
                bool restoreSpeaker = existing != null && service?.UsesSpeakerMute(existing.Id) == true;
                await StartPeer(output, exclusive, allowSpeakerMute).ConfigureAwait(false);
                if (existing != null) undo.Push(() => StartPeer(output, existing.DestinationOnly, restoreSpeaker));
                else if (Streams(pid).FirstOrDefault(s => Ids.Equals(WindowsLinkOutputIdentity.FormatDeviceId(s.PeerId), output)) is { } added)
                    undo.Push(() => service!.StopStreamAsync(added.Id));
            }

            var removed = previous.Where(s => !peers.Contains(WindowsLinkOutputIdentity.FormatDeviceId(s.PeerId), Ids)).ToArray();
            if (phoneOnly && removed.Any(s => s.DestinationOnly))
            {
                // Confirm the Android players before relinquishing the Windows receiver's isolation.
                await LocalAppOutputMirrors.SetExtraOutputsAsync(pid, null, extras).ConfigureAwait(false);
                undo.Push(RestoreMirrors);
            }
            if (primary != null || phoneOnly)
            {
                // An exclusive lease may temporarily own both Windows render roles. Let it restore
                // them before reading or editing the user's actual per-app output.
                foreach (var stream in removed.Where(s => s.DestinationOnly)) await StopPeer(stream).ConfigureAwait(false);
                if (primary != null)
                {
                    using var policy = new AudioPolicyConfig(EDataFlow.eRender);
                    var baseline = (Media: policy.GetDefaultEndPoint(pid, ERole.eMultimedia),
                        Console: policy.GetDefaultEndPoint(pid, ERole.eConsole));
                    if (!Ids.Equals(primary, Endpoint(baseline.Media)) || !Ids.Equals(primary, Endpoint(baseline.Console)))
                    {
                        // The OS may change one render role before returning a failure for the other.
                        undo.Push(() =>
                        {
                            using var restore = new AudioPolicyConfig(EDataFlow.eRender);
                            int media = restore.SetDefaultEndPoint(baseline.Media, pid, ERole.eMultimedia);
                            int console = restore.SetDefaultEndPoint(baseline.Console, pid, ERole.eConsole);
                            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(media < 0 ? media : console);
                            return Task.CompletedTask;
                        });
                        Require(AudioService.ApplyEndpoint(pid, EDataFlow.eRender, primary));
                    }
                }
            }

            if (extras.Length != 0 || oldExtras.Length != 0)
            {
                if (phoneOnly) await LocalAppOutputMirrors.SetExclusivePhoneOutputsAsync(pid, extras, allowSpeakerMute).ConfigureAwait(false);
                else await LocalAppOutputMirrors.SetExtraOutputsAsync(pid, primary, extras).ConfigureAwait(false);
                undo.Push(RestoreMirrors);
            }
            foreach (var stream in removed.Where(s => owner != null || !s.DestinationOnly))
                await StopPeer(stream).ConfigureAwait(false);
            return (true, 0, L10n.T("History.Status.success"));
        }
        catch (Exception ex)
        {
            while (undo.TryPop(out var restore))
            {
                try { await restore().ConfigureAwait(false); }
                catch (Exception rollback) { ExceptionDiagnostics.Record("AppPlaybackOutputs.Restore", rollback); }
            }
            return RouteError(ex);
        }
        finally { ApplicationAudio.NotifyChanged(); }

        async Task StartPeer(string output, bool only, bool speaker) =>
            Require(await WindowsLinkHub.RoutePcAppToPeerAsync(pid, output, only, speaker, keepOtherPeers: true).ConfigureAwait(false));

        Task RestoreMirrors() => previousPhoneOnly
            ? LocalAppOutputMirrors.SetExclusivePhoneOutputsAsync(pid, oldExtras, previousSpeakerMute)
            : LocalAppOutputMirrors.SetExtraOutputsAsync(pid, oldPrimary, oldExtras);

        async Task StopPeer(LinkMedia stream)
        {
            bool restoreSpeaker = service!.UsesSpeakerMute(stream.Id);
            await service.StopStreamAsync(stream.Id).ConfigureAwait(false);
            undo.Push(() => StartPeer(WindowsLinkOutputIdentity.FormatDeviceId(stream.PeerId), stream.DestinationOnly, restoreSpeaker));
        }

        void StopPhone()
        {
            if (AndroidAudioHost.StopPcAppToPhone(pid, restorePreviousEndpoint: true))
                undo.Push(() => { Require(AndroidAudioHost.RoutePcAppToPhone(pid, previousPhone!)); return Task.CompletedTask; });
        }
    }

    private static (bool Success, int HResult, string Message) RouteError(Exception error)
    {
        string detail = AndroidErrorTranslation.Format(error);
        if (error is WindowsLinkIncomingOutputs.SpeakerConsent || error.HResult == SourceSpeakerConsentRequiredException.ErrorCode)
            return (false, error.HResult, detail);
        return (false, error.HResult, string.Format(L10n.T("Dev.RouteFailed"), detail));
    }

    private sealed class RouteFailure : Exception
    {
        internal RouteFailure(string message, int result) : base(message) { HResult = result; }
    }

    private static void Require((bool Success, int HResult, string Message) result)
    {
        if (!result.Success) throw new RouteFailure(result.Message, result.HResult);
    }
}
#endif

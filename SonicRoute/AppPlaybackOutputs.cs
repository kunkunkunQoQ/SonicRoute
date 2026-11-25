#if SONICROUTE_PHONE
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.WindowsLink;
#endif
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute;

internal static partial class AppPlaybackOutputs
{
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
    private static readonly Dictionary<int, ProcessGate> Gates = [];
#if SONICROUTE_IDLE_WORKING_SET_TRIM
    internal static bool HasPendingChanges { get { lock (Gates) return Gates.Count != 0; } }
#endif
    private sealed class ProcessGate { internal readonly SemaphoreSlim Changes = new(1, 1); internal int Users; }

#if SONICROUTE_PHONE
    private static LinkMedia[] Streams(int pid) => WindowsLinkHub.Service?.GetOutgoingAppStreams(pid) ?? [];
#endif

    private static string Endpoint(string? id) => string.IsNullOrWhiteSpace(id) || AudioService.IsSystemDefault(id)
        ? AudioService.SystemDefaultDeviceId : AudioPolicyConfig.UnpackDeviceId(id!);

    internal static HashSet<string> GetSelected(int pid, string? persistedEndpoint)
    {
        var outputs = LocalAppOutputMirrors.GetExtraOutputs(pid).ToHashSet(Ids);
#if SONICROUTE_PHONE
        var streams = Streams(pid);
        foreach (var stream in streams) outputs.Add(WindowsLinkOutputIdentity.FormatDeviceId(stream.PeerId));
        if (streams.Any(s => s.DestinationOnly) || LocalAppOutputMirrors.GetExclusivePhone(pid, out _) != null) return outputs;
        persistedEndpoint = AndroidAudioHost.TryGetPcAppPhoneOutput(pid) ?? persistedEndpoint;
#endif
        outputs.Add(Endpoint(persistedEndpoint));
        return outputs;
    }

    internal static HashSet<string> GetSelectedForApp(AudioAppInfo app, string? persistedEndpoint)
    {
        if (!app.IsExternal) return GetSelected((int)app.ProcessId, persistedEndpoint);
#if SONICROUTE_PHONE
        if (WindowsLinkHub.FindIncomingAudio(app.ExternalSourceId) is { } incoming)
            return WindowsLinkIncomingOutputs.GetSelected(incoming);
        if (ApplicationAudio.ExternalSession(app) is IMultiOutputAudioSession multi)
        {
            var outputs = multi.GetOutputs().Select(Endpoint).ToHashSet(Ids);
            if (multi is AndroidAudioSession android && android.UsesSourcePhonePlayback)
                outputs.Add(AndroidAudioSession.SourcePhoneOutputId);
            return outputs;
        }
#endif
        return new HashSet<string>(Ids) { Endpoint(persistedEndpoint) };
    }

    internal static Task<(bool Success, int HResult, string Message)> SetSelectedAsync(
        AudioAppInfo app, string[] selected, string? preferredPrimary) =>
        SetAsync(app, selected, preferredPrimary, allowSpeakerMute: !app.IsExternal && !selected.Any(ApplicationAudio.IsWindowsLinkOutputDevice));

    internal static async Task<(bool Success, int HResult, string Message)> SetAsync(
        AudioAppInfo app, string[] selected, string? preferredPrimary, bool allowSpeakerMute)
    {
        var outputs = selected.Select(Endpoint).Distinct(Ids).ToArray();
        if (outputs.Length == 0) outputs = [AudioService.SystemDefaultDeviceId];
        if (app.IsExternal)
        {
#if SONICROUTE_PHONE
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
                catch (Exception ex)
                {
                    return multi is AirPlayAudioOutput
                        ? (false, ex.HResult, ApplicationAudio.FormatAirPlayRouteError(ex, outputs))
                        : RouteError(ex);
                }
            }
#endif
            return outputs.Length == 1 ? await ApplicationAudio.ApplyEndpointAsync(app, EDataFlow.eRender, outputs[0])
                : (false, -1, L10n.T("Phone.StatusOperationFailed"));
        }
        if (app.ProcessId == 0 || app.ProcessId > int.MaxValue)
            return (false, -1, L10n.T("Phone.Err.InvalidProcessId"));
        int pid = (int)app.ProcessId;
        return await SerializeAsync(pid, async () =>
        {
#if SONICROUTE_PHONE
            bool reset = selected.Length == 0;
            var history = HistoryFor(pid);
            if (reset) Forget(pid, history);
            long epoch = Volatile.Read(ref _historyEpoch);
            string? saved = AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender);
            var before = GetSelected(pid, saved);
#endif
            var identity = outputs.Length > 1 || SavedPlaybackStore.ReadAll().Count != 0 ? PlaybackAppIdentity.Read(pid) : null;
            string preferred = Endpoint(preferredPrimary);
            var result = await ApplyAsync(pid, outputs, preferred, allowSpeakerMute).ConfigureAwait(false);
#if SONICROUTE_PHONE
            if (result.Success && !reset && Volatile.Read(ref _historyEpoch) == epoch)
                Remember(pid, before.ToArray(), outputs, preferred, allowSpeakerMute);
#endif
            if (result.Success && !AppPlaybackRestore.Save(pid, identity, outputs, preferred))
                return (false, unchecked((int)0x80004005), L10n.T("St.SaveFailed"));
            return result;
        }).ConfigureAwait(false);
    }

    internal static Task<bool> RestoreAsync(int pid, PlaybackAppIdentity app, string[] outputs, string preferred, Func<bool> stillWanted) =>
        SerializeAsync(pid, async () =>
        {
            if (!stillWanted() || PlaybackAppIdentity.Read(pid) != app) return false;
            string? saved = AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender);
            var before = GetSelected(pid, saved);
            if (before.SetEquals(outputs) && (outputs.All(ApplicationAudio.IsExternalOutputDevice) || Ids.Equals(Endpoint(saved), preferred)))
                return true;
            bool allowSpeakerMute = !outputs.Any(ApplicationAudio.IsWindowsLinkOutputDevice);
            var result = await ApplyAsync(pid, outputs, preferred, allowSpeakerMute).ConfigureAwait(false);
#if SONICROUTE_PHONE
            if (result.Success) Remember(pid, before.ToArray(), outputs, preferred, allowSpeakerMute);
#endif
            if (!result.Success) System.Diagnostics.Trace.WriteLine("Playback restore: " + result.Message);
            return result.Success;
        });

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
#if SONICROUTE_PHONE
        var physical = outputs.Where(id => !ApplicationAudio.IsExternalOutputDevice(id)).ToArray();
#else
        var physical = outputs;
#endif
        string? primary = physical.FirstOrDefault(id => Ids.Equals(id, preferred))
            ?? physical.FirstOrDefault(AudioService.IsSystemDefault)
            ?? physical.FirstOrDefault();
        string[] oldExtras = LocalAppOutputMirrors.GetExtraOutputs(pid);
#if SONICROUTE_PHONE
        var service = WindowsLinkHub.Service;
        var peers = outputs.Where(WindowsLinkOutputIdentity.IsWindowsLinkOutput).ToArray();
        var previous = Streams(pid);
        var extras = outputs.Where(id => !Ids.Equals(id, primary) && !WindowsLinkOutputIdentity.IsWindowsLinkOutput(id)).ToArray();
        bool phoneOnly = primary == null && peers.Length == 0;
        string? previousPhone = AndroidAudioHost.TryGetPcAppPhoneOutput(pid);
        bool previousPhoneOnly = LocalAppOutputMirrors.GetExclusivePhone(pid, out bool previousSpeakerMute) != null;

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
#else
        var extras = outputs.Where(id => !Ids.Equals(id, primary)).ToArray();
#endif
        try
        {
            using var reservation = outputs.Length > 1 ? LocalAppOutputMirrors.Reserve(pid) : null;
#if SONICROUTE_PHONE
            if (previousPhone != null) StopPhone();
            if (previousPhoneOnly && !phoneOnly)
            {
                await LocalAppOutputMirrors.SetExtraOutputsAsync(pid, oldExtras).ConfigureAwait(false);
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
                await LocalAppOutputMirrors.SetExtraOutputsAsync(pid, extras).ConfigureAwait(false);
                undo.Push(RestoreMirrors);
            }
            if (primary != null || phoneOnly)
            {
                // An exclusive lease may temporarily own both Windows render roles. Let it restore
                // them before reading or editing the user's actual per-app output.
                foreach (var stream in removed.Where(s => s.DestinationOnly)) await StopPeer(stream).ConfigureAwait(false);
            }
#endif
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

            if (extras.Length != 0 || oldExtras.Length != 0)
            {
#if SONICROUTE_PHONE
                if (phoneOnly) await LocalAppOutputMirrors.SetExclusivePhoneOutputsAsync(pid, extras, allowSpeakerMute).ConfigureAwait(false);
                else
#endif
                    await LocalAppOutputMirrors.SetExtraOutputsAsync(pid, extras).ConfigureAwait(false);
#if SONICROUTE_PHONE
                undo.Push(RestoreMirrors);
#endif
            }
#if SONICROUTE_PHONE
            foreach (var stream in removed.Where(s => owner != null || !s.DestinationOnly))
                await StopPeer(stream).ConfigureAwait(false);
#endif
            return (true, 0, L10n.T("History.Status.success"));
        }
        catch (Exception ex)
        {
            while (undo.Count > 0)
            {
                var restore = undo.Pop();
                try { await restore().ConfigureAwait(false); }
                catch (Exception rollback) { ExceptionDiagnostics.Record("AppPlaybackOutputs.Restore", rollback); }
            }
            return RouteError(ex);
        }
        finally { ApplicationAudio.NotifyRouteChanged(pid); }

#if SONICROUTE_PHONE
        async Task StartPeer(string output, bool only, bool speaker) =>
            Require(await WindowsLinkHub.RoutePcAppToPeerAsync(pid, output, only, speaker, keepOtherPeers: true).ConfigureAwait(false));

        Task RestoreMirrors() => previousPhoneOnly
            ? LocalAppOutputMirrors.SetExclusivePhoneOutputsAsync(pid, oldExtras, previousSpeakerMute)
            : LocalAppOutputMirrors.SetExtraOutputsAsync(pid, oldExtras);

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
#endif
    }

    private static (bool Success, int HResult, string Message) RouteError(Exception error)
    {
#if SONICROUTE_PHONE
        string detail = AndroidErrorTranslation.Format(error);
        if (error is WindowsLinkIncomingOutputs.SpeakerConsent || error.HResult == SourceSpeakerConsentRequiredException.ErrorCode)
            return (false, error.HResult, detail);
#else
        string detail = error.Message;
#endif
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

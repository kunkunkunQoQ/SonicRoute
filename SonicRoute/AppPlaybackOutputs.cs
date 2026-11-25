#if SONICROUTE_NATIVE_PC
using AudioService = SonicRoute.Native.Product.PcRoutingApi;
using ApplicationAudio = SonicRoute.Native.Product.PcRoutingApi;
using AudioPolicyConfig = SonicRoute.Native.Product.PcRoutingPolicy;
#endif
#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
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

    private static string Endpoint(string? id) => string.IsNullOrWhiteSpace(id) || AudioService.IsSystemDefault(id)
        ? AudioService.SystemDefaultDeviceId : AudioPolicyConfig.UnpackDeviceId(id!);

    internal static HashSet<string> GetSelected(int pid, string? persistedEndpoint)
    {
        var outputs = LocalAppOutputMirrors.GetExtraOutputs(pid).ToHashSet(Ids);
#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
        if (LocalAppOutputMirrors.GetExclusivePhone(pid, out _) != null) return outputs;
        persistedEndpoint = AndroidAudioHost.TryGetPcAppPhoneOutput(pid) ?? persistedEndpoint;
#endif
        outputs.Add(Endpoint(persistedEndpoint));
        return outputs;
    }

    internal static HashSet<string> GetSelected(AudioAppInfo app, string? persistedEndpoint)
    {
        if (!app.IsExternal) return GetSelected((int)app.ProcessId, persistedEndpoint);
#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
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

    internal static Task<(bool Success, int HResult, string Message)> SetAsync(
        AudioAppInfo app, string[] selected, string? preferredPrimary)
        => SetAsync(app, selected, preferredPrimary, nativeIfUnmanaged: false);

    internal static async Task<(bool Success, int HResult, string Message)> SetAsync(
        AudioAppInfo app, string[] selected, string? preferredPrimary, bool nativeIfUnmanaged)
    {
        var outputs = selected.Select(Endpoint).Distinct(Ids).ToArray();
        if (outputs.Length == 0) outputs = [AudioService.SystemDefaultDeviceId];
        if (app.IsExternal)
        {
#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
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
            // A simple app switch stays on the native OS path. Test saved/mirrored
            // ownership inside the same per-process gate used for explicit selections.
            if (nativeIfUnmanaged && outputs.Length == 1 && !ApplicationAudio.IsPhoneOutputDevice(outputs[0])
                && LocalAppOutputMirrors.GetExtraOutputs(pid).Length == 0 && !AppPlaybackRestore.HasSavedChoice(pid)
#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
                && !IsManaged(pid)
#endif
                )
                return AudioService.ApplyEndpoint(pid, EDataFlow.eRender, outputs[0]);
#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
            bool reset = selected.Length == 0;
            if (reset) Forget(pid, HistoryFor(pid));
            long epoch = Volatile.Read(ref _historyEpoch);
            string? saved = AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender);
            var before = GetSelected(pid, saved);
#endif
            var identity = outputs.Length > 1 || SavedPlaybackStore.ReadAll().Count != 0 ? PlaybackAppIdentity.Read(pid) : null;
            string preferred = Endpoint(preferredPrimary);
            var result = await ApplyAsync(pid, outputs, preferred, allowSpeakerMute: true).ConfigureAwait(false);
#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
            if (result.Success && !reset && Volatile.Read(ref _historyEpoch) == epoch)
                Remember(pid, before.ToArray(), outputs, preferred);
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
            if (before.SetEquals(outputs) && (outputs.All(ApplicationAudio.IsPhoneOutputDevice) || Ids.Equals(Endpoint(saved), preferred)))
                return true;
            var result = await ApplyAsync(pid, outputs, preferred, allowSpeakerMute: true).ConfigureAwait(false);
#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
            if (result.Success) Remember(pid, before.ToArray(), outputs, preferred);
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

    internal static string? PreferredPrimary(string[] physical, string preferred) =>
        physical.FirstOrDefault(id => Ids.Equals(id, preferred)) ?? physical.FirstOrDefault(AudioService.IsSystemDefault) ?? physical.FirstOrDefault();

    private static async Task<(bool Success, int HResult, string Message)> ApplyAsync(
        int pid, string[] outputs, string preferred, bool allowSpeakerMute)
    {
        var undo = new Stack<Func<Task>>();
        var physical = outputs.Where(id => !ApplicationAudio.IsPhoneOutputDevice(id)).ToArray();
        string? primary = PreferredPrimary(physical, preferred);
        var extras = outputs.Where(id => !Ids.Equals(id, primary)).ToArray();
        string[] oldExtras = LocalAppOutputMirrors.GetExtraOutputs(pid);
#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
        string? previousPhone = AndroidAudioHost.TryGetPcAppPhoneOutput(pid);
        bool previousPhoneOnly = LocalAppOutputMirrors.GetExclusivePhone(pid, out bool previousSpeakerMute) != null;
#endif
        try
        {
            using var reservation = outputs.Length > 1 ? LocalAppOutputMirrors.Reserve(pid) : null;
#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
            if (previousPhone != null) StopPhone();
            if (previousPhoneOnly && primary != null)
            {
                await LocalAppOutputMirrors.SetExtraOutputsAsync(pid, oldExtras).ConfigureAwait(false);
                undo.Push(RestoreMirrors);
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
#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
                if (primary == null) await LocalAppOutputMirrors.SetExclusivePhoneOutputsAsync(pid, extras, allowSpeakerMute).ConfigureAwait(false);
                else
#endif
                    await LocalAppOutputMirrors.SetExtraOutputsAsync(pid, extras).ConfigureAwait(false);
            }
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

#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
        Task RestoreMirrors() => previousPhoneOnly
            ? LocalAppOutputMirrors.SetExclusivePhoneOutputsAsync(pid, oldExtras, previousSpeakerMute)
            : LocalAppOutputMirrors.SetExtraOutputsAsync(pid, oldExtras);

        void StopPhone()
        {
            if (AndroidAudioHost.StopPcAppToPhone(pid, restorePreviousEndpoint: true))
                undo.Push(() => { Require(AndroidAudioHost.RoutePcAppToPhone(pid, previousPhone!)); return Task.CompletedTask; });
        }
#endif
    }

    private static (bool Success, int HResult, string Message) RouteError(Exception error)
    {
#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
        string detail = AndroidErrorTranslation.Format(error);
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

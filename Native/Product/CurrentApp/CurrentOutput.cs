using System.Diagnostics;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

internal enum OutputIntentStatus { Pending, Confirmed, Rejected }
internal sealed record OutputIntentState(long Generation, int Pid, string? Requested, string? Observed, OutputIntentStatus Status, string[]? RequestedOutputs = null, string[]? ObservedOutputs = null);

internal sealed partial class ProductCurrentApp
{
    private sealed class RouteScope
    {
        internal string? Target;
        internal Task Apply = Task.CompletedTask;
    }

    private readonly object _routesGate = new();
    private readonly Dictionary<string, RouteScope> _scopes = new(StringComparer.Ordinal);
    private Task _planning = Task.CompletedTask;
    private int _outstanding;
    private long _lastRequest;
    private OutputIntentState? _lastOutput;
    internal OutputIntentState? LastOutput => Volatile.Read(ref _lastOutput);
    internal event Action<OutputIntentState>? OutputChanged;

    internal Task<ControlReply> CycleOutputAsync(int? foregroundPid = null, nint hwnd = 0, int steps = 1, int? requiredPid = null)
    {
        int captured = foregroundPid ?? WindowProcess(hwnd == 0 ? ShellNative.GetForegroundWindow() : hwnd);
        return QueueOutput(captured, hwnd, null, steps, null, requiredPid);
    }

    internal Task<ControlReply> SelectOutputAsync(int pid, string target, FeedbackKind failureFeedback = FeedbackKind.Osd) =>
        QueueOutput(pid, 0, target, 1, null, failureFeedback: failureFeedback);

    internal Task<ControlReply> SetOutputsAsync(int pid, string preferred, string[] selected, FeedbackKind failureFeedback = FeedbackKind.Osd)
    {
        var outputs = selected.Select(Normal).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var effective = outputs.Length == 0 ? [AudioService.SystemDefaultDeviceId] : outputs;
        var primaryChoices = effective;
#if SONICROUTE_PHONE
        var local = effective.Where(id => !PcRoutingApi.IsPhoneOutputDevice(id)).ToArray();
        if (local.Length > 0) primaryChoices = local;
#endif
        string target = AppPlaybackOutputs.PreferredPrimary(primaryChoices, Normal(preferred))!;
        return QueueOutput(pid, 0, target, 1, outputs, failureFeedback: failureFeedback);
    }

    private static string FormatSwitchingTitle(string? appTitle)
    {
        if (string.IsNullOrWhiteSpace(appTitle)) return L10n.T("Ov.SwitchingOutput");
        string template = L10n.T("Ov.Switching");
        return template.IndexOf("{0}", StringComparison.Ordinal) >= 0
            ? string.Format(template, appTitle)
            : appTitle + " · " + template;
    }

    private static string FormatFinalTitle(string? appTitle) =>
        string.IsNullOrWhiteSpace(appTitle) ? L10n.T("Ov.VolumeTitle") : appTitle!;

    private static string FormatFailedTitle(string? appTitle) =>
        string.IsNullOrWhiteSpace(appTitle)
            ? L10n.T("Ov.OutputSwitchFailed")
            : L10n.T("Ov.OutputSwitchFailed") + " · " + appTitle;

    private Task<ControlReply> QueueOutput(int pid, nint hwnd, string? explicitTarget, int steps, string[]? selected, int? requiredPid = null, FeedbackKind failureFeedback = FeedbackKind.Osd)
    {
        bool showOsd = failureFeedback == FeedbackKind.Osd;
        lock (_routesGate)
        {
            if (_closed || _outstanding >= 32) return Task.FromResult(new ControlReply("error: output_queue_busy"));
            ++_outstanding;
            long generation = ++_lastRequest;
            Task previousPlanning = _planning;
            var plan = Task.Run(async () =>
            {
                await previousPlanning.ConfigureAwait(false);
                try
                {
                    AudioAppInfo? resolvedApp = explicitTarget is null
                        ? Resolve(pid, hwnd)
                        : audio.Apps().FirstOrDefault(a => !a.IsExternal && (int)a.ProcessId == pid);
                    int targetPid = explicitTarget is null ? checked((int)(resolvedApp?.ProcessId ?? 0)) : pid;
                    if (requiredPid.HasValue && targetPid != requiredPid.Value) throw new InvalidOperationException("Requested session is unavailable.");
                    if (targetPid <= 0 && resolvedApp is not { IsExternal: true }) throw new InvalidOperationException(L10n.T("Ov.NoOutputSession"));
                    var config = ConfigService.Load();
                    var visible = explicitTarget == null || showOsd ? Visible(config, resolvedApp) : null;
                    if (explicitTarget == null && visible!.Count == 0) throw new InvalidOperationException(L10n.T("Ov.NoDevice"));
                    if (resolvedApp != null) RecordUsedApp(resolvedApp, failureFeedback);
                    string appName = resolvedApp != null ? AppDisplayName.Get(resolvedApp) : (ForegroundAppService.GetProcessNameSafe(targetPid) ?? "");
                    string? old = resolvedApp != null ? audio.ReadRoute(resolvedApp, Flow.Render) : audio.ReadRoute(targetPid, Flow.Render);
                    string scopeKey = resolvedApp?.Identity ?? "windows:" + (uint)targetPid;
                    string target;
                    Task<ControlReply> write;
                    lock (_routesGate)
                    {
                        if (!_scopes.TryGetValue(scopeKey, out var scope)) _scopes.Add(scopeKey, scope = new());
                        if (explicitTarget == null)
                        {
                            int index = visible!.FindIndex(d => Same(d.Id, scope.Target ?? old));
                            int offset = (int)Math.Min(Math.Abs((long)steps), int.MaxValue);
                            target = visible[DeviceCycle.Index(index, visible.Count, steps < 0, offset)].Id;
                        }
                        else target = explicitTarget;
                        scope.Target = target;
                        Publish(new(generation, targetPid, target, old, OutputIntentStatus.Pending, selected),
                            FormatSwitchingTitle(appName), showOsd ? "🔊 " + Label(target, visible!, config) : "", showOsd);
                        Task previousWrite = scope.Apply;
                        write = Task.Run(() => ApplyAsync(previousWrite, targetPid, resolvedApp, appName, target, generation, explicitTarget == null, selected, showOsd));
                        scope.Apply = write;
                        _ = RetireAsync(scopeKey, scope, write);
                    }
                    return write;
                }
                catch (Exception error)
                {
                    Publish(new(generation, pid, explicitTarget, null, OutputIntentStatus.Rejected),
                        FormatFailedTitle(null), error.Message, showOsd);
                    return Task.FromResult(new ControlReply("error: " + error.Message));
                }
            });
            _planning = plan;
            return FinishAsync(plan.Unwrap());
        }
    }

    private async Task<ControlReply> ApplyAsync(Task previous, int pid, AudioAppInfo? app, string appName, string target, long generation, bool cycle, string[]? selected, bool showOsd)
    {
        await previous.ConfigureAwait(false);
        ControlReply result;
        try
        {
            if (cycle && !Visible(ConfigService.Load(), app).Any(d => Same(d.Id, target)))
                throw new InvalidOperationException(L10n.T("Ov.NoDevice"));
#if SONICROUTE_PHONE
            if (app is { IsExternal: true })
                result = audio.ExternalAudio is { } bridge ? await bridge.SetExternalOutputAsync(app, target).ConfigureAwait(false)
                    : new("error: external_session_unavailable");
            else
#endif
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) throw new InvalidOperationException(L10n.T("Ov.NoOutputSession"));
                result = selected == null
                    ? await audio.SelectOutputAsync(pid, target).ConfigureAwait(false)
                    : await audio.ExecuteAsync(new("outputs-set", [pid.ToString(System.Globalization.CultureInfo.InvariantCulture), target, .. selected])).ConfigureAwait(false);
            }
        }
        catch (Exception error) { result = new("error: " + error.Message); }

        // Read back even when a partial policy write threw. A stale result can complete its caller,
        // but cannot replace a newer user's pending/final feedback on the STA.
        try
        {
            string? actual;
            HashSet<string>? observed = null;
            if (selected == null) actual = app != null ? audio.ReadRoute(app, Flow.Render) : audio.ReadRoute(pid, Flow.Render);
            else observed = audio.ReadPlaybackSelection(pid, out actual);
            bool primaryMatched = Same(actual, target);
#if SONICROUTE_PHONE
            // A phone-only mirror group has no Windows primary. Its reported first
            // sink can differ from the user's range anchor; the actual set is authoritative.
            if (observed is { Count: > 1 } && observed.All(PcRoutingApi.IsPhoneOutputDevice))
                primaryMatched = actual != null && observed.Contains(actual);
#endif
            bool matched = primaryMatched && (observed == null ||
                observed.SetEquals(selected!.Length == 0 ? [AudioService.SystemDefaultDeviceId] : selected));
            bool success = !result.Text.StartsWith("error", StringComparison.OrdinalIgnoreCase) && matched;
            var config = ConfigService.Load();
            Publish(new(generation, pid, target, actual, success ? OutputIntentStatus.Confirmed : OutputIntentStatus.Rejected, selected, observed?.ToArray()),
                success ? FormatFinalTitle(appName) : FormatFailedTitle(appName),
                showOsd ? "🔊 " + Label(actual, Visible(config, app), config) : "", showOsd);
            audio.NotifyAudioChanged();
            if (!matched) return new("error: output_readback_mismatch, actual=" + (actual ?? AudioService.SystemDefaultDeviceId));
            return result;
        }
        catch (Exception error)
        {
            Publish(new(generation, pid, target, null, OutputIntentStatus.Rejected),
                FormatFailedTitle(appName), error.Message, showOsd);
            return new("error: " + error.Message);
        }
    }

    private List<Endpoint> Visible(AppConfig config, AudioAppInfo? app)
    {
        var devices = audio.RenderDevicesWithPhone(app, includePhoneCurrent: true)
            .Where(d => config.IsDeviceShown(EDataFlow.eRender, d.PreferenceId)).ToList();
        if (config.IsDeviceShown(EDataFlow.eRender, AudioService.SystemDefaultDeviceId))
            devices.Insert(0, new(AudioService.SystemDefaultDeviceId, L10n.T("Dev.SystemDefaultOut")));
        return devices;
    }

    private static string Normal(string? id) => string.IsNullOrWhiteSpace(id) || AudioService.IsSystemDefault(id)
        ? AudioService.SystemDefaultDeviceId : AudioPolicyConfig.UnpackDeviceId(id);
    private static bool Same(string? first, string? second) =>
        string.Equals(Normal(first), Normal(second), StringComparison.OrdinalIgnoreCase);

    private static string Label(string? id, List<Endpoint> devices, AppConfig config)
    {
        id ??= AudioService.SystemDefaultDeviceId;
        if (config.DeviceNames.TryGetValue(id, out string? custom) && !string.IsNullOrWhiteSpace(custom)) return custom;
        return devices.Find(d => Same(d.Id, id))?.Name
            ?? L10n.T(AudioService.IsSystemDefault(id) ? "Dev.SystemDefaultOut" : "Dev.Unknown");
    }

    private void Publish(OutputIntentState state, string title, string text, bool showOsd)
    {
        lock (_routesGate)
        {
            if (_closed || state.Generation != _lastRequest) return;
            Volatile.Write(ref _lastOutput, state);
        }
        owner.TryPost(() =>
        {
            if (_closed || LastOutput != state || state.Generation != Interlocked.Read(ref _lastRequest)) return;
            bool? success = state.Status == OutputIntentStatus.Confirmed ? true
                : state.Status == OutputIntentStatus.Rejected ? false
                : null;
            if (showOsd)
                feedback.Publish(state.Status == OutputIntentStatus.Pending ? FeedbackKind.OutputPending : FeedbackKind.OutputSettled, title, text, success: success);
            else
                feedback.Publish(FeedbackKind.OutputSilent);
            OutputChanged?.Invoke(state);
        });
    }

    private async Task RetireAsync(string key, RouteScope scope, Task write)
    {
        await write.ConfigureAwait(false);
        Task acceptedPlans;
        lock (_routesGate) acceptedPlans = _planning;
        await acceptedPlans.ConfigureAwait(false);
        lock (_routesGate)
        {
            if (_scopes.TryGetValue(key, out var current) && ReferenceEquals(current, scope) && scope.Apply == write)
                _scopes.Remove(key);
        }
    }

    private async Task<ControlReply> FinishAsync(Task<ControlReply> work)
    {
        try { return await work.ConfigureAwait(false); }
        finally
        {
            lock (_routesGate)
            {
                if (--_outstanding == 0) _scopes.Clear();
            }
        }
    }

    internal async Task DrainAsync()
    {
        Task planning;
        lock (_routesGate) planning = _planning;
        await planning.ConfigureAwait(false);
        Task[] writes;
        lock (_routesGate) writes = _scopes.Values.Select(s => s.Apply).ToArray();
        await Task.WhenAll(writes).ConfigureAwait(false);
    }
}

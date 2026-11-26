using System.Diagnostics;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
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
    private readonly Dictionary<int, RouteScope> _scopes = new();
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

    internal Task<ControlReply> SelectOutputAsync(int pid, string target, bool showOsd = true) =>
        QueueOutput(pid, 0, target, 1, null, showOsd: showOsd);

    internal Task<ControlReply> SetOutputsAsync(int pid, string preferred, string[] selected, bool showOsd = true)
    {
        var outputs = selected.Select(Normal).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var effective = outputs.Length == 0 ? [AudioService.SystemDefaultDeviceId] : outputs;
        string target = AppPlaybackOutputs.PreferredPrimary(effective, Normal(preferred))!;
        return QueueOutput(pid, 0, target, 1, outputs, showOsd: showOsd);
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

    private Task<ControlReply> QueueOutput(int pid, nint hwnd, string? explicitTarget, int steps, string[]? selected, int? requiredPid = null, bool showOsd = true)
    {
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
                    SonicRoute.Core.Models.AudioAppInfo? resolvedApp = explicitTarget is null
                        ? Resolve(pid, hwnd)
                        : audio.Apps().FirstOrDefault(a => !a.IsExternal && (int)a.ProcessId == pid);
                    int targetPid = explicitTarget is null ? checked((int)(resolvedApp?.ProcessId ?? 0)) : pid;
#if SONICROUTE_PHONE
                    if (explicitTarget is null && resolvedApp is { IsExternal: true })
                    {
                        var cfg = ConfigService.Load();
                        var vis = Visible(cfg);
                        if (vis.Count == 0) throw new InvalidOperationException(L10n.T("Ov.NoDevice"));
                        string appTitle = AppDisplayName.Get(resolvedApp);
                        string? cur = audio.ReadRoute(resolvedApp, Flow.Render) ?? AudioService.SystemDefaultDeviceId;
                        int idx = vis.FindIndex(d => Same(d.Id, cur));
                        int off = (int)Math.Min(Math.Abs((long)steps), int.MaxValue);
                        string nextTarget = vis[DeviceCycle.Index(idx, vis.Count, steps < 0, off)].Id;
                        RecordUsedApp(resolvedApp, showOsd);
                        Publish(new(generation, 0, nextTarget, cur, OutputIntentStatus.Pending, selected),
                            FormatSwitchingTitle(appTitle), "🔊 " + Label(nextTarget, vis, cfg), showOsd);
                        return Task.Run(async () =>
                        {
                            var reply = await audio.SetExternalOutputsAsync(resolvedApp, [nextTarget]).ConfigureAwait(false);
                            bool ok = !reply.Text.StartsWith("error", StringComparison.OrdinalIgnoreCase);
                            string? actual = audio.ReadRoute(resolvedApp, Flow.Render);
                            var curCfg = ConfigService.Load();
                            Publish(new(generation, 0, nextTarget, actual, ok ? OutputIntentStatus.Confirmed : OutputIntentStatus.Rejected),
                                ok ? FormatFinalTitle(appTitle) : FormatFailedTitle(appTitle),
                                "🔊 " + Label(actual, Visible(curCfg), curCfg), showOsd);
                            if (ok) audio.NotifyAudioChanged();
                            return reply;
                        });
                    }
#endif
                    if (requiredPid.HasValue && targetPid != requiredPid.Value) throw new InvalidOperationException("Requested session is unavailable.");
                    if (targetPid <= 0) throw new InvalidOperationException(L10n.T("Ov.NoOutputSession"));
                    var config = ConfigService.Load();
                    var visible = Visible(config);
                    if (explicitTarget == null && visible.Count == 0) throw new InvalidOperationException(L10n.T("Ov.NoDevice"));
                    if (resolvedApp != null) RecordUsedApp(resolvedApp, showOsd);
                    string appName = resolvedApp != null ? AppDisplayName.Get(resolvedApp) : (ForegroundAppService.GetProcessNameSafe(targetPid) ?? "");
                    string? old = audio.ReadRoute(targetPid, Flow.Render);
                    string target;
                    Task<ControlReply> write;
                    lock (_routesGate)
                    {
                        if (!_scopes.TryGetValue(targetPid, out var scope)) _scopes.Add(targetPid, scope = new());
                        if (explicitTarget == null)
                        {
                            int index = visible.FindIndex(d => Same(d.Id, scope.Target ?? old));
                            int offset = (int)Math.Min(Math.Abs((long)steps), int.MaxValue);
                            target = visible[DeviceCycle.Index(index, visible.Count, steps < 0, offset)].Id;
                        }
                        else target = explicitTarget;
                        scope.Target = target;
                        Publish(new(generation, targetPid, target, old, OutputIntentStatus.Pending, selected),
                            FormatSwitchingTitle(appName), "🔊 " + Label(target, visible, config), showOsd);
                        Task previousWrite = scope.Apply;
                        write = Task.Run(() => ApplyAsync(previousWrite, targetPid, appName, target, generation, explicitTarget == null, selected, showOsd));
                        scope.Apply = write;
                        _ = RetireAsync(targetPid, scope, write);
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

    private async Task<ControlReply> ApplyAsync(Task previous, int pid, string appName, string target, long generation, bool cycle, string[]? selected, bool showOsd)
    {
        await previous.ConfigureAwait(false);
        ControlReply result;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) throw new InvalidOperationException(L10n.T("Ov.NoOutputSession"));
            if (cycle && !Visible(ConfigService.Load()).Any(d => Same(d.Id, target)))
                throw new InvalidOperationException(L10n.T("Ov.NoDevice"));
            result = selected == null
                ? await audio.SelectOutputAsync(pid, target).ConfigureAwait(false)
                : await audio.ExecuteAsync(new("outputs-set", [pid.ToString(System.Globalization.CultureInfo.InvariantCulture), target, .. selected])).ConfigureAwait(false);
        }
        catch (Exception error) { result = new("error: " + error.Message); }

        // Read back even when a partial policy write threw. A stale result can complete its caller,
        // but cannot replace a newer user's pending/final feedback on the STA.
        try
        {
            var actual = audio.ReadRoute(pid, Flow.Render);
            var observed = selected == null ? null : AppPlaybackOutputs.GetSelected(pid, actual);
            bool matched = Same(actual, target) && (observed == null ||
                observed.SetEquals(selected!.Length == 0 ? [AudioService.SystemDefaultDeviceId] : selected));
            bool success = !result.Text.StartsWith("error", StringComparison.OrdinalIgnoreCase) && matched;
            var config = ConfigService.Load();
            Publish(new(generation, pid, target, actual, success ? OutputIntentStatus.Confirmed : OutputIntentStatus.Rejected, selected, observed?.ToArray()),
                success ? FormatFinalTitle(appName) : FormatFailedTitle(appName),
                "🔊 " + Label(actual, Visible(config), config), showOsd);
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

    private List<Endpoint> Visible(AppConfig config)
    {
        var devices = audio.RenderDevicesWithPhone().Where(d => !config.HiddenOutputDevices.Contains(d.Id)).ToList();
        if (!config.HiddenOutputDevices.Contains(AudioService.SystemDefaultDeviceId))
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

    private async Task RetireAsync(int pid, RouteScope scope, Task write)
    {
        await write.ConfigureAwait(false);
        Task acceptedPlans;
        lock (_routesGate) acceptedPlans = _planning;
        await acceptedPlans.ConfigureAwait(false);
        lock (_routesGate)
        {
            if (_scopes.TryGetValue(pid, out var current) && ReferenceEquals(current, scope) && scope.Apply == write)
                _scopes.Remove(pid);
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

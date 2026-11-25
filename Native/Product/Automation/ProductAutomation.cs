using SonicRoute.Core;
using SonicRoute.Core.Models;

namespace SonicRoute.Native.Product;

internal sealed partial class ProductAutomation(ApplicationHost host, NativeApplicationWindow window, AppConfig config,
    FeedbackState feedback, ProductAudio audio) : IDisposable
{
    private readonly OwnerContext context = new(window);
    private HotkeyService? hotkeys;
    private IDisposable? reload, directoryRetry;
    private bool resident, stopped, scheduled;
    private int reloadPosted;
    private ProductMicMonitor? microphones;

    internal bool NeedsForeground => resident && AutoRuleService.WatchesForeground;
    internal string? ProcessName(int pid) => ForegroundAppService.GetProcessNameSafe(pid);

    // Watcher diff/baseline mutations remain on the STA even across asynchronous process queries.
    // Commands from pipes use independent tasks and only dispatch their visual feedback.
    private sealed class OwnerContext(NativeApplicationWindow window) : SynchronizationContext
    {
        internal void Enter(Action work)
        {
            var old = Current;
            SetSynchronizationContext(this);
            try { work(); } finally { SetSynchronizationContext(old); }
        }
        public override void Post(SendOrPostCallback d, object? state) =>
            window.PostContinuation(() => Enter(() => d(state)));
    }

    internal bool TryPost(Action work) => window.TryPost(() => context.Enter(work));
    internal IDisposable ScheduleOnce(TimeSpan delay, Action work) => window.ScheduleOnce(delay, () => context.Enter(work));
    internal void Start(bool resident)
    {
        this.resident = resident;
        AutoRuleService.Product = this;
        if (!resident) return;
        context.Enter(() =>
        {
            hotkeys = new HotkeyService(window, !config.TrayWheelPaused, config.TrayWheelEverywhere);
            hotkeys.HotkeyPressed += Hotkey;
            AutoRuleStore.Changed += RulesChanged;
            ReloadRules();
            microphones = new ProductMicMonitor(audio, window, config, feedback);
            microphones.Start();
            var startup = AutoRuleStore.LoadAll().Where(r => r.Enabled && r.Trigger == AutoRuleTrigger.Startup)
                .Select(r => r.Id).ToArray();
            if (startup.Length > 0) TryPost(() => _ = AutoRuleService.ExecuteStartupAsync(startup));
        });
    }

    private void ReloadRules()
    {
        if (stopped) return;
        var rules = AutoRuleStore.LoadAll();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string action in HotkeyActions.All)
        {
            if (!config.ExperimentalMic && action is HotkeyActions.ActSwitchInput or HotkeyActions.ActSwitchAllInput) continue;
            map[action] = config.Hotkeys.TryGetValue(action, out var value) ? value : HotkeyActions.Defaults[action];
        }
        foreach (var rule in rules)
            if (rule.Enabled && rule.Trigger == AutoRuleTrigger.Hotkey && !string.IsNullOrWhiteSpace(rule.Hotkey))
                map[AutoRuleService.HotkeyPrefix + rule.Id] = rule.Hotkey;
        hotkeys?.Reload(map);
        AutoRuleService.RefreshWatcher();
        host.RefreshForegroundInterest();
        bool needsSchedule = rules.Any(r => r.Enabled && r.Trigger == AutoRuleTrigger.Schedule);
        if (needsSchedule || scheduled) AutoRuleScheduler.RefreshScheduler();
        scheduled = needsSchedule;
        UpdateRuleDirectoryPolicy();
    }

    private void RulesChanged()
    {
        if (Interlocked.Exchange(ref reloadPosted, 1) != 0) return;
        if (!TryPost(() =>
        {
            Interlocked.Exchange(ref reloadPosted, 0);
            if (stopped) return;
            reload?.Dispose();
            reload = ScheduleOnce(TimeSpan.FromMilliseconds(150), () => { reload = null; ReloadRules(); });
        })) Interlocked.Exchange(ref reloadPosted, 0);
    }

    private void UpdateRuleDirectoryPolicy()
    {
        bool need = AutoRuleStore.NeedsExternalChangeProbe || (config.BackgroundPollingFallbackEnabled && AutoRuleStore.LoadAll().Count != 0);
        if (!need) { directoryRetry?.Dispose(); directoryRetry = null; return; }
        if (directoryRetry == null) directoryRetry = ScheduleOnce(TimeSpan.FromSeconds(2), () =>
        {
            directoryRetry = null;
            _ = CheckDirectoryAsync();
        });
    }

    private async Task CheckDirectoryAsync()
    {
        try { await Task.Run(AutoRuleStore.CheckForExternalChanges); }
        finally { if (!stopped) UpdateRuleDirectoryPolicy(); }
    }

    internal void OnSignal(ShellSignal signal) => context.Enter(() =>
    {
        if (stopped || !resident) return;
        switch (signal.Kind)
        {
            case ShellSignalKind.Hotkey: hotkeys?.Dispatch((int)signal.Value); break;
            case ShellSignalKind.TrayWheel: Wheel(signal); break;
            case ShellSignalKind.Foreground: AutoRuleService.NotifyForeground(ForegroundAppService.GetForegroundProcessId()); break;
            case ShellSignalKind.Time when scheduled: AutoRuleScheduler.RefreshScheduler(false); break;
            case ShellSignalKind.Power when signal.Value is 6 or 7 or 18:
            case ShellSignalKind.Session when signal.Value is 1 or 3 or 8:
                AutoRuleService.RecoverWatcher();
                AutoRuleStore.RecoverMonitoring();
                if (scheduled) AutoRuleScheduler.RefreshScheduler(false);
                microphones?.InvalidateTopology();
                ReloadRules();
                break;
            case ShellSignalKind.Device: microphones?.InvalidateTopology(); break;
        }
    });

    internal Task<string> ExecuteRuleAsync(string selector) => Task.Run(async () =>
    {
        AutoRuleStore.Invalidate();
        var rule = RuleCommandLine.SelectRule(selector, AutoRuleStore.LoadAll(), out var failure);
        if (rule == null) return RuleCommandLine.EncodeResult(failure!);
        var execution = await AutoRuleService.ExecuteWithResultAsync(rule, "command").ConfigureAwait(false);
        await AutoRuleService.FlushExecutionHistoryAsync().ConfigureAwait(false);
        return RuleCommandLine.EncodeResult(new()
        {
            ExitCode = execution.Busy ? 5 : execution.Canceled ? 7 : execution.Failed > 0 ? 1 : 0,
            Code = execution.Busy ? "rule_busy" : execution.Canceled ? "canceled" : execution.Failed > 0 ? "step_failed" : "ok",
            RuleId = rule.Id, RuleName = rule.Name, Succeeded = execution.Succeeded, Failed = execution.Failed,
            Stopped = execution.Stopped, Skipped = execution.Skipped
        });
    });

    internal Task<bool> FeedbackAsync(FeedbackKind kind, string title, string text, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryPost(() =>
        {
            try
            {
                if (stopped || token.IsCancellationRequested) { tcs.TrySetResult(false); return; }
                feedback.Publish(kind, title, text);
                tcs.TrySetResult(true);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })) tcs.TrySetResult(false);
        return tcs.Task;
    }

    internal Task<ControlReply?> TryControlAsync(ControlRequest request)
    {
        if (request.Verb != "automation-status" || request.Args.Length != 0) return Task.FromResult<ControlReply?>(null);
        var tcs = new TaskCompletionSource<ControlReply?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryPost(() =>
        {
            string keys = string.Join(",", hotkeys?.RegistrationStatus.Where(p => p.Value.Length != 0)
                .Select(p => p.Key + "=" + p.Value) ?? []);
            tcs.TrySetResult(new($"process={AutoRuleService.ProcessDetection};foreground={window.ForegroundRegistered};schedule={scheduled};mouse={window.MouseRegistered};keys={keys};keyboard={window.HotkeyCount};mic={microphones?.Count ?? 0};deadlines={window.DeadlineCount}"));
        })) tcs.TrySetResult(new("error: host_busy"));
        return tcs.Task;
    }

    public void Dispose()
    {
        stopped = true;
        AutoRuleStore.Changed -= RulesChanged;
        reload?.Dispose();
        directoryRetry?.Dispose();
        hotkeys?.Dispose();
        microphones?.Dispose();
        AutoRuleScheduler.Shutdown();
        AutoRuleService.Shutdown();
        ProcessChangeSource.WhenReleased().GetAwaiter().GetResult();
        AutoRuleScheduler.FlushLogs();
        AutoRuleService.StopExecutionHistory();
    }
}

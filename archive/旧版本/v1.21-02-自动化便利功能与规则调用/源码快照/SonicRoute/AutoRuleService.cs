using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using SonicRoute.Core;
using SonicRoute.Core.Compat;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute
{
    /// <summary>
    /// 极简自动化规则引擎（v1.16 新增）。
    /// - 快捷键触发：规则快捷键以 "Rule:{Id}" 动作并入 HotkeyService 注册 map（App 构建），
    ///   HotkeyPressed 分发给 ExecuteByHotkeyAsync；冲突时该动作不注册（RegistrationStatus 无记录 = 冲突）。
    /// - 应用启动 / 前台切换触发：1 秒 DispatcherTimer 轮询（仅当存在启用规则时启动，无新依赖，
    ///   与 CurrentAppService 前台监听机制一致）；应用启动 = 进程快照 diff（新出现），应用退出 = 进程快照 diff（消失），前台切换 = GetForegroundWindow。
    /// - 执行全部复用 Core 现有静态服务（AudioService / SessionVolumeService / SystemVolumeService /
    ///   SystemDefaultDeviceService），不修改 OSD、主音量、设备枚举等无关功能。
    /// - 应用控制按进程名解析到当前有音频会话的 PID 执行（不误改其他应用或系统音量）。
    /// - 高级操作（启动程序 / PowerShell）不默认提权；是否执行外部程序在 UI 编辑规则时明确提示。
    /// - 启动程序（v1.18 起）：每个启动项独立保存路径 + 打开方式，打开方式为空 = Windows 默认方式，
    ///   非空 = 用指定 EXE 打开并把目标路径作为参数；启动模式支持「全部启动 / 随机启动一个」。
    /// </summary>
    public static class AutoRuleService
    {
        public const string HotkeyPrefix = "Rule:";
        private static readonly HashSet<string> _executingRules = new(StringComparer.Ordinal);
        private static readonly CancellationTokenSource _executionLifetime = new();
        private static volatile bool _shuttingDown;
        private static int _startupExecuted;
        internal static event Action? ExecutionStateChanged;

        internal static string[] RunningRuleIds()
        {
            lock (_executingRules) return _executingRules.ToArray();
        }

        private static void NotifyExecutionStateChanged()
        {
            try { ExecutionStateChanged?.Invoke(); }
            catch (Exception error) { AutoRuleScheduler.Log("执行状态通知失败: " + error.Message); }
        }

        internal readonly struct ExecutionResult
        {
            internal ExecutionResult(int succeeded, int failed, bool busy = false, bool canceled = false, bool stopped = false, int skipped = 0)
            { Succeeded = succeeded; Failed = failed; Busy = busy; Canceled = canceled; Stopped = stopped; Skipped = skipped; }
            internal readonly int Succeeded, Failed, Skipped;
            internal readonly bool Busy, Canceled, Stopped;
        }

        internal static string DescribeResult(ExecutionResult result) => result.Busy ? L10n.T("Auto.Running")
            : result.Canceled ? L10n.T("Auto.RunCanceled")
            : result.Stopped ? string.Format(L10n.T("Auto.RunStopped"), result.Succeeded, result.Failed, result.Skipped)
            : result.Succeeded == 0 ? L10n.T("Auto.RunFailed")
            : result.Failed > 0 ? string.Format(L10n.T("Auto.RunPartial"), result.Succeeded, result.Failed)
            : string.Format(L10n.T("Auto.RunDone"), result.Succeeded);

        internal static bool IsRunning(string id)
        {
            lock (_executingRules) return _executingRules.Contains(id);
        }

        /// <summary>启动触发只派发一次；保存/刷新规则不会重触发，没有新监听器。</summary>
        internal static async Task ExecuteStartupAsync(IReadOnlyList<string> ids)
        {
            if (_shuttingDown || Interlocked.Exchange(ref _startupExecuted, 1) != 0) return;
            foreach (var id in ids)
            {
                if (_shuttingDown) return;
                var rule = FindRule(id);
                if (rule is { Enabled: true, Trigger: AutoRuleTrigger.Startup }) await ExecuteAsync(rule);
            }
        }

        internal static void Shutdown()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            _executionLifetime.Cancel();
            ++_watchGeneration;
            _watchTimer?.Stop(); _watchTimer = null;
            _watchTickPending = false;
            _runningSnapshot = null; _snapshotBaselineTask = null; _snapshotTargetName = null;
            _processSnapshot?.Dispose(); _processSnapshot = null;
        }

        /// <summary>随机启动用的随机源（net48 无 Random.Shared）。
        /// Random 实例非线程安全，而规则执行可能来自后台线程，故统一加锁访问。</summary>
        private static readonly Random _random = new();

        private static int NextRandom(int maxExclusive)
        {
            lock (_random) return _random.Next(maxExclusive);
        }

        /// <summary>按 Id 查规则。</summary>
        public static AutoRule? FindRule(string ruleId) =>
            AutoRuleStore.Find(ruleId);

        /// <summary>快捷键触发入口（App.ExecuteHotkeyAsync 分发）。</summary>
        public static async Task ExecuteByHotkeyAsync(string ruleId)
        {
            var r = FindRule(ruleId);
            if (r is { Enabled: true } && r.Trigger == AutoRuleTrigger.Hotkey)
                await ExecuteAsync(r);
        }

        // ---------- 触发监听（应用启动 / 前台切换） ----------

        private static DispatcherTimer? _watchTimer;
        private static HashSet<string>? _runningSnapshot;
        private static string? _snapshotTargetName;
        private static Task<HashSet<string>>? _snapshotBaselineTask;
        private static ProcessSnapshotService? _processSnapshot;
        private static int _lastFgPid;
        private static bool _watchTickRunning;
        private static bool _watchTickPending;
        private static int _watchGeneration;
        private static long _watchRulesRevision = -1;
        private static List<AutoRule> _watchRules = new();

        // RefreshWatcher/TickCoreAsync 均由 Dispatcher 调用。未变化时复用只读快照，
        // 避免每秒深复制全部规则和步骤；执行引擎另行 CloneSteps，不修改该快照。
        private static List<AutoRule> GetWatchRules()
        {
            var snapshot = AutoRuleStore.ReadSnapshot(_watchRulesRevision);
            if (snapshot.Rules != null)
            {
                _watchRules = snapshot.Rules.Where(r => r.Enabled
                    && (r.Trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppSwitch or AutoRuleTrigger.AppExit)).ToList();
                _watchRulesRevision = snapshot.Revision;
            }
            return _watchRules;
        }

        private static string? GetSnapshotTarget(List<AutoRule> rules)
        {
            string? target = null;
            foreach (var rule in rules)
            {
                if (rule.Trigger is not (AutoRuleTrigger.AppStart or AutoRuleTrigger.AppExit)) continue;
                // 空目标旧规则仍支持任意应用；多目标共用一次全量枚举。
                if (string.IsNullOrWhiteSpace(rule.TriggerApp)
                    || rule.TriggerApp.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
                if (target != null && !string.Equals(target, rule.TriggerApp, StringComparison.OrdinalIgnoreCase)) return null;
                target = rule.TriggerApp;
            }
            return target;
        }

        private static void StartSnapshotBaseline(string? target)
        {
            _snapshotTargetName = target;
            _runningSnapshot = null;
            _processSnapshot?.Dispose();
            var snapshot = _processSnapshot = new ProcessSnapshotService(target);
            _snapshotBaselineTask = Task.Run(() =>
            {
                try { return snapshot.Capture(); }
                catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
            });
        }

        /// <summary>根据启用规则刷新监听器（有启用触发规则才启动，无则停止，避免常驻开销）。</summary>
        public static void RefreshWatcher()
        {
            if (_shuttingDown) return;
            var rules = GetWatchRules();
            bool need = rules.Count > 0;
            bool needsSnapshot = rules.Any(r => r.Trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppExit);
            string? target = GetSnapshotTarget(rules);
            if (need && _watchTimer == null)
            {
                _watchGeneration++;
                if (needsSnapshot) StartSnapshotBaseline(target);
                _lastFgPid = -1;
                _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _watchTimer.Tick += async (_, _) => await TickAsync();
                _watchTimer.Start();
            }
            else if (need && _watchTimer != null && needsSnapshot
                && (_runningSnapshot == null && _snapshotBaselineTask == null
                    || !string.Equals(target, _snapshotTargetName, StringComparison.OrdinalIgnoreCase)))
            {
                _watchGeneration++;
                StartSnapshotBaseline(target);
            }
            else if (need && !needsSnapshot && (_runningSnapshot != null || _snapshotBaselineTask != null))
            {
                _watchGeneration++;
                _runningSnapshot = null;
                _snapshotBaselineTask = null;
                _snapshotTargetName = null;
                _processSnapshot?.Dispose();
                _processSnapshot = null;
            }
            else if (!need && _watchTimer != null)
            {
                _watchGeneration++;
                _watchTimer.Stop();
                _watchTimer = null;
                _watchTickPending = false;
                _runningSnapshot = null;
                _snapshotBaselineTask = null;
                _processSnapshot?.Dispose();
                _processSnapshot = null;
            }
        }

        private static async Task TickAsync()
        {
            if (_watchTickRunning) { _watchTickPending = true; return; }
            _watchTickRunning = true;
            try
            {
                do
                {
                    _watchTickPending = false;
                    await TickCoreAsync();
                } while (_watchTickPending && _watchTimer != null);
            }
            finally { _watchTickRunning = false; }
        }

        private static async Task TickCoreAsync()
        {
            int generation = _watchGeneration;
            var rules = GetWatchRules();
            if (rules.Count == 0) { RefreshWatcher(); return; }

            // 应用启动 / 应用退出触发：进程快照 diff（新出现 = 启动；消失 = 退出）。
            // 快照与 diff 独立于 AppStart——仅建 AppExit 规则时也必须检测退出。
            if (rules.Any(r => r.Trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppExit))
            {
                string? target = GetSnapshotTarget(rules);
                if (_processSnapshot == null || !string.Equals(target, _snapshotTargetName, StringComparison.OrdinalIgnoreCase))
                    StartSnapshotBaseline(target);
                var baselineTask = _snapshotBaselineTask;
                if (baselineTask != null)
                {
                    HashSet<string> baseline;
                    try { baseline = await baselineTask; }
                    catch { baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
                    if (generation != _watchGeneration) return;
                    _runningSnapshot = baseline;
                    if (_snapshotBaselineTask == baselineTask) _snapshotBaselineTask = null;
                }
                HashSet<string> now;
                var snapshot = _processSnapshot!;
                try
                {
                    now = await Task.Run(() => snapshot.Capture());
                }
                catch { now = _runningSnapshot ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

                if (generation != _watchGeneration) return;

                if (_runningSnapshot != null)
                {
                    // 应用启动：只对"新出现"的进程名触发（启动时已存在的进程不触发）
                    if (rules.Any(r => r.Trigger == AutoRuleTrigger.AppStart))
                    {
                        foreach (var name in now)
                        {
                            if (_runningSnapshot.Contains(name)) continue;
                            foreach (var r in rules.Where(x => x.Trigger == AutoRuleTrigger.AppStart
                                && (string.IsNullOrWhiteSpace(x.TriggerApp)
                                    || string.Equals(x.TriggerApp, name, StringComparison.OrdinalIgnoreCase))))
                            {
                                await ExecuteAsync(r);
                                if (generation != _watchGeneration) return;
                            }
                        }
                    }
                    // 应用退出：只对"上一秒存在、当前消失"的进程名触发
                    if (rules.Any(r => r.Trigger == AutoRuleTrigger.AppExit))
                    {
                        foreach (var gone in _runningSnapshot)
                        {
                            if (now.Contains(gone)) continue;
                            foreach (var r in rules.Where(x => x.Trigger == AutoRuleTrigger.AppExit
                                && (string.IsNullOrWhiteSpace(x.TriggerApp)
                                    || string.Equals(x.TriggerApp, gone, StringComparison.OrdinalIgnoreCase))))
                            {
                                await ExecuteAsync(r);
                                if (generation != _watchGeneration) return;
                            }
                        }
                    }
                }
                if (generation != _watchGeneration) return;
                _runningSnapshot = now;
            }

            // 前台切换触发：前台 PID 变化且与目标进程名匹配（含空 = 任意应用）
            if (rules.Any(r => r.Trigger == AutoRuleTrigger.AppSwitch))
            {
                int fg = ForegroundAppService.GetForegroundProcessId();
                if (fg != 0 && fg != _lastFgPid)
                {
                    string? name = ForegroundAppService.GetProcessNameSafe(fg);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        foreach (var r in rules.Where(x => x.Trigger == AutoRuleTrigger.AppSwitch
                            && (string.IsNullOrWhiteSpace(x.TriggerApp)
                                || string.Equals(x.TriggerApp, name, StringComparison.OrdinalIgnoreCase))))
                        {
                            await ExecuteAsync(r);
                            if (generation != _watchGeneration) return;
                        }
                    }
                }
                if (generation != _watchGeneration) return;
                _lastFgPid = fg;
            }
        }

        // ---------- 执行 ----------

        /// <summary>执行一条规则（多步骤按顺序执行）。返回是否至少有一个步骤成功。</summary>
        public static async Task<bool> ExecuteAsync(AutoRule rule)
            => (await ExecuteWithResultAsync(rule)).Succeeded > 0;

        /// <summary>各入口共享单飞、步骤快照、失败策略及结果计数。</summary>
        internal static Task<ExecutionResult> ExecuteWithResultAsync(AutoRule rule)
            => ExecuteCoreAsync(rule, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        private static async Task<ExecutionResult> ExecuteCoreAsync(AutoRule rule, HashSet<string> ancestors)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Id)) return new ExecutionResult(0, 1);
            if (_shuttingDown) return new ExecutionResult(0, 0, canceled: true);
            string id = rule.Id, name = rule.Name;
            if (ancestors.Contains(id) || ancestors.Count >= 16)
            {
                AutoRuleScheduler.Log($"Execute 拒绝循环或过深调用: [{name}] Id={id}");
                return new ExecutionResult(0, 1);
            }
            lock (_executingRules)
            {
                if (!_executingRules.Add(id)) return new ExecutionResult(0, 0, busy: true);
            }
            int succeeded = 0, failed = 0;
            var path = new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase) { id };
            var token = _executionLifetime.Token;
            try
            {
                var steps = rule.CloneSteps();
                bool stopOnFailure = rule.StopOnFailure;
                NotifyExecutionStateChanged();
                AutoRuleScheduler.Log($"Execute 开始: [{name}] 触发={rule.Trigger} 步骤={steps.Count}");
                for (int index = 0; index < steps.Count; index++)
                {
                    var step = steps[index];
                    if (token.IsCancellationRequested) return new ExecutionResult(succeeded, failed, canceled: true);
                    bool ok = await ExecuteStepAsync(step, token, path);
                    if (token.IsCancellationRequested) return new ExecutionResult(succeeded, failed, canceled: true);
                    if (ok) succeeded++; else failed++;
                    AutoRuleScheduler.Log($"Execute 步骤: [{name}] Action={step.Action} 结果={ok}");
                    if (!ok && stopOnFailure)
                    {
                        int skipped = steps.Count - index - 1;
                        AutoRuleScheduler.Log($"Execute 失败后停止: [{name}] 跳过={skipped}");
                        return new ExecutionResult(succeeded, failed, stopped: true, skipped: skipped);
                    }
                }
                AutoRuleScheduler.Log($"Execute 完成: [{name}] 成功={succeeded} 失败={failed}");
                return new ExecutionResult(succeeded, failed);
            }
            catch (OperationCanceledException) { return new ExecutionResult(succeeded, failed, canceled: true); }
            catch (Exception ex)
            {
                AutoRuleScheduler.Log($"Execute 失败: [{name}] {ex.Message}");
                return new ExecutionResult(succeeded, failed + 1);
            }
            finally
            {
                lock (_executingRules) _executingRules.Remove(id);
                NotifyExecutionStateChanged();
            }
        }

        private static async Task<bool> ExecuteStepAsync(AutoRuleStep step, CancellationToken token, HashSet<string> path)
        {
            try
            {
                // 步骤启动延时（ms，0 = 立即执行；执行前等待，支持按步骤错峰）
                if (step.DelayMs > 0)
                    await Task.Delay(step.DelayMs, token);
                token.ThrowIfCancellationRequested();

                switch (step.Action)
                {
                    case AutoRuleAction.ExecuteRule:
                        {
                            var target = await Task.Run(() => AutoRuleStore.Find(step.TargetRuleId));
                            token.ThrowIfCancellationRequested();
                            if (target == null) return false;
                            var result = await ExecuteCoreAsync(target, path);
                            return !result.Busy && !result.Canceled && result.Failed == 0 && result.Succeeded > 0;
                        }

                    case AutoRuleAction.SetSystemOutput:
                        return await Task.Run(() => SetSystemDevice(EDataFlow.eRender, step.TargetDeviceId));

                    case AutoRuleAction.SetSystemInput:
                        return await Task.Run(() => SetSystemDevice(EDataFlow.eCapture, step.TargetDeviceId));

                    case AutoRuleAction.SetSystemVolume:
                        return await Task.Run(() =>
                            SystemVolumeService.SetVolumePercent(null, MathEx.Clamp(step.Volume, 0, 100)));

                    case AutoRuleAction.ToggleSystemMute:
                        return await Task.Run(() => SystemVolumeService.SetMute(null, !SystemVolumeService.IsMuted()));

                    case AutoRuleAction.SetSystemMute:
                        return await Task.Run(() => SystemVolumeService.SetMute(null, step.Muted));

                    case AutoRuleAction.AdjustSystemVolume:
                        return await Task.Run(() => SystemVolumeService.AdjustVolumePercent(null,
                            MathEx.Clamp(step.VolumeDelta, -100, 100)).Applied);

                    case AutoRuleAction.SetAppVolume:
                        {
                            return await Task.Run(() => WithApp(step.TargetApp, pid =>
                                SessionVolumeService.SetVolumePercent(pid, MathEx.Clamp(step.Volume, 0, 100))));
                        }

                    case AutoRuleAction.ToggleAppMute:
                        {
                            return await Task.Run(() => WithApp(step.TargetApp, pid =>
                                SessionVolumeService.ToggleMuteChecked(pid).Applied));
                        }

                    case AutoRuleAction.SetAppMute:
                        return await Task.Run(() => WithApp(step.TargetApp, pid =>
                            SessionVolumeService.SetMute(pid, step.Muted)));

                    case AutoRuleAction.AdjustAppVolume:
                        return await Task.Run(() => WithApp(step.TargetApp, pid =>
                            SessionVolumeService.AdjustVolumePercent(pid,
                                MathEx.Clamp(step.VolumeDelta, -100, 100)).Applied));

                    case AutoRuleAction.SetGlobalMicMute:
                    case AutoRuleAction.ToggleGlobalMicMute:
                        {
                            var result = await Task.Run(() =>
                            {
                                bool target = step.Action == AutoRuleAction.ToggleGlobalMicMute
                                    ? !GlobalMicMuteService.IsMuted() : step.Muted;
                                bool applied = GlobalMicMuteService.SetMute(target);
                                bool muted = GlobalMicMuteService.IsAnyMuted(ConfigService.Load().MicMuteOsdTrackInputMuted);
                                return (Applied: applied, Muted: muted);
                            });
                            var app = System.Windows.Application.Current as App;
                            if (result.Applied && !token.IsCancellationRequested && app != null && !app.Dispatcher.HasShutdownStarted)
                            {
                                await app.Dispatcher.InvokeAsync(() =>
                                {
                                    if (!token.IsCancellationRequested) app.ShowMicMuteOsd(L10n.T("Ov.MuteMic"), result.Muted);
                                });
                            }
                            return result.Applied;
                        }

                    case AutoRuleAction.SetAppOutput:
                        return await Task.Run(() => ApplyEndpointForApp(step.TargetApp, EDataFlow.eRender, step.TargetDeviceId));

                    case AutoRuleAction.SetAppInput:
                        return await Task.Run(() => ApplyEndpointForApp(step.TargetApp, EDataFlow.eCapture, step.TargetDeviceId));

                    case AutoRuleAction.LaunchProgram:
                        return await Task.Run(() => LaunchPrograms(step.EffectiveLaunchItems(), step.LaunchMode));

                    case AutoRuleAction.RunPowerShell:
                        return await Task.Run(() => RunPowerShells(step.ProgramPaths));

                    case AutoRuleAction.ShowOsd:
                        return ShowCustomOsd(step.OsdTitle, step.OsdText);
                }
            }
            catch
            {
                // 规则执行失败静默，不影响主程序
            }
            return false;
        }

        private static bool WithApp(string processName, Func<int, bool> action)
        {
            int pid = ResolveAppPid(processName);
            return pid > 0 && action(pid);
        }

        /// <summary>切换系统默认播放 / 录音设备（空或系统默认虚拟 id = 无操作失败，避免把系统默认"还原"成无意义状态）。</summary>
        private static bool SetSystemDevice(EDataFlow flow, string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || AudioService.IsSystemDefault(deviceId)) return false;
            var r = SystemDefaultDeviceService.SetDefault(flow, deviceId);
            return r.Success;
        }

        /// <summary>按进程名解析当前有音频会话的应用 PID（进程名不区分大小写，不含扩展名）。</summary>
        private static int ResolveAppPid(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return -1;
            var apps = AudioService.GetApps();
            var app = apps.FirstOrDefault(a => string.Equals(a.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            if (app == null)
            {
                // 进程名匹配不到时再按显示名兜底（如部分会话 DisplayName 与进程名不同）
                app = apps.FirstOrDefault(a => string.Equals(a.Label, processName, StringComparison.OrdinalIgnoreCase));
            }
            return app == null ? -1 : (int)app.ProcessId;
        }

        private static bool ApplyEndpointForApp(string processName, EDataFlow flow, string deviceId)
        {
            int pid = ResolveAppPid(processName);
            if (pid <= 0) return false;
            string target = string.IsNullOrWhiteSpace(deviceId)
                ? AudioService.SystemDefaultDeviceId  // 空 = 还原跟随系统默认
                : deviceId;
            var r = AudioService.ApplyEndpoint(pid, flow, target);
            return r.Success;
        }

        /// <summary>
        /// 执行启动程序动作（v1.18 起按启动项执行，每项独立打开方式）。
        /// - 启动模式 0（全部启动）：逐个执行全部启动项；启动模式 1（随机启动一个）：随机选一个完整启动项执行。
        /// - 单项执行见 <see cref="LaunchOne"/>：打开方式为空 = Windows 默认方式，非空 = 用该 EXE 打开并把目标路径作为参数。
        /// </summary>
        private static bool LaunchPrograms(List<AutoLaunchItem> items, int mode)
        {
            if (items == null || items.Count == 0) return false;

            // 随机启动一个：只随机挑选启动项，不改变该启动项自身的打开方式
            if (mode == 1)
                return LaunchOne(items[NextRandom(items.Count)]);

            bool any = false;
            foreach (var item in items)
            {
                if (LaunchOne(item)) any = true;
            }
            return any;
        }

        /// <summary>
        /// 启动单个启动项。
        /// 打开方式为空 → 默认方式（.exe 直接启动，其他文件交给 Windows 当前文件关联程序）；
        /// 打开方式非空且该 EXE 存在 → 启动该 EXE，目标路径作为参数传入（QQ音乐.exe "音乐.mp3"）；
        /// 打开方式指向的 EXE 已不存在时回退默认方式，避免启动项直接失效。
        /// </summary>
        private static bool LaunchOne(AutoLaunchItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path)) return false;
            try
            {
                var target = item.Path.Trim();
                var openWith = (item.OpenWith ?? "").Trim();
                if (openWith.Length > 0 && File.Exists(openWith))
                {
                    // 目标文件是参数，打开方式指定的 EXE 才是实际启动程序
                    Process.Start(new ProcessStartInfo(openWith)
                    {
                        Arguments = QuoteArg(target),
                        UseShellExecute = true
                    });
                    return true;
                }
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }

        /// <summary>参数加引号（含空格路径安全；内部双引号转义）。</summary>
        private static string QuoteArg(string value)
        {
            var s = (value ?? "").Replace("\"", "\\\"");
            return "\"" + s + "\"";
        }

        private static bool RunPowerShells(List<string> scripts)
        {
            bool any = false;
            foreach (var script in scripts)
            {
                if (string.IsNullOrWhiteSpace(script)) continue;
                try
                {
                    var psi = new ProcessStartInfo("powershell.exe")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    if (script.TrimEnd().EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) && File.Exists(script))
                    {
                        psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"";
                    }
                    else
                    {
                        psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" +
                            script.Replace("\"", "`\"") + "\"";
                    }
                    Process.Start(psi);
                    any = true;
                }
                catch { }
            }
            return any;
        }

        /// <summary>显示自定义 OSD（主标题 / 副标题；空标题时用默认「自动化」）。</summary>
        private static bool ShowCustomOsd(string title, string text)
        {
            try
            {
                var app = System.Windows.Application.Current as App;
                if (app == null) return false;
                var t = string.IsNullOrWhiteSpace(title) ? L10n.T("Auto.OsdDefaultTitle") : title;
                var dispatcher = System.Windows.Application.Current.Dispatcher;
                if (dispatcher.CheckAccess())
                    app.ShowOsd(t, text ?? "");
                else
                    dispatcher.BeginInvoke(() => app.ShowOsd(t, text ?? ""));
                return true;
            }
            catch { return false; }
        }
    }
}

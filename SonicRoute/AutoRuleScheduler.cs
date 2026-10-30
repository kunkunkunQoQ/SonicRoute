using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Microsoft.Win32;
using SonicRoute.Core;
using SonicRoute.Core.Models;

namespace SonicRoute
{
    /// <summary>
    /// 自动化定时调度器（v1.18 新增，配合 AutoRuleTrigger.Schedule）。
    /// - 单发等待：每次只计算所有启用定时规则中"最近一次执行时刻"，用一次性 Timer 等待，不做每秒/每分钟轮询。
    /// - 到点执行后立即重算下一次；规则增删改/启停（经 ReloadHotkeys）与系统时间变化、睡眠恢复时重新计算。
    /// - 睡眠/关机错过执行时刻：唤醒后不补执行（错过即跳过），直接重算下一次。
    /// - 仅一次规则领取并保存时停用（不删除），步骤失败不重试；下一个HH:mm，今天已过则明天，不使用日期。
    /// - 执行复用 AutoRuleService.ExecuteAsync（多步骤，与快捷键/应用触发完全一致）。
    /// </summary>
    public static class AutoRuleScheduler
    {
        private static readonly object _lock = new();
        private static System.Threading.Timer? _timer;
        private sealed class Plan
        {
            internal DateTime Due;
            internal List<AutoRule> Rules = new();
            internal bool CallbackTaken;
        }
        private static Plan? _pendingPlan;
        private static bool _started, _shutdown;

        private static readonly string LogPath = Path.Combine(
            SonicRoute.Core.AppDataRoot.DirectoryPath, "scheduler.log");
        private static readonly object _logGate = new(), _logWriteGate = new();
        private static readonly Queue<string> _logQueue = new();
        private static Task _logDrain = Task.CompletedTask;

        /// <summary>Bounded, demand-driven batches; no scheduler lock or periodic flush wakeup.</summary>
        internal static void Log(string msg)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}";
            if (line.Length > 4096) line = line.Substring(0, 4094) + Environment.NewLine;
            lock (_logGate)
            {
                if (_logQueue.Count < 512)
                {
                    _logQueue.Enqueue(line);
                    if (_logDrain.IsCompleted) _logDrain = Task.Run(DrainLogs);
                    return;
                }
            }
            // Pressure fallback preserves diagnostics instead of growing without bound or losing errors.
            WriteLogBatch(line);
        }

        private static void DrainLogs()
        {
            while (true)
            {
                var batch = new StringBuilder();
                lock (_logGate)
                {
                    if (_logQueue.Count == 0) { _logDrain = Task.CompletedTask; return; }
                    while (_logQueue.Count != 0 && batch.Length < 32 * 1024) batch.Append(_logQueue.Dequeue());
                }
                WriteLogBatch(batch.ToString());
            }
        }

        internal static void FlushLogs()
        {
            Task pending;
            lock (_logGate) pending = _logDrain;
            pending.GetAwaiter().GetResult();
        }

        private static void WriteLogBatch(string lines)
        {
            try
            {
                lock (_logWriteGate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    var fi = new FileInfo(LogPath);
                    if (!fi.Exists || fi.Length > 256 * 1024)
                        File.WriteAllText(LogPath, lines);
                    else
                        File.AppendAllText(LogPath, lines);
                }
            }
            catch { }
        }

        /// <summary>错过判定容差：Timer 到期时刻与当前时间相差超过该值视为错过（睡眠/休眠恢复），不补执行。</summary>
        private static readonly TimeSpan MissedTolerance = TimeSpan.FromMinutes(2);

        /// <summary>初始化：挂接系统时间变化与电源恢复监听，并立即重算（App.OnStartup 调用）。</summary>
        public static void Start()
        {
            lock (_lock) { if (_started) return; _started = true; _shutdown = false; }
            Log("Start: 挂接系统事件并开始调度");
            SystemEvents.TimeChanged += OnSystemTimeChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            RefreshScheduler(false);
        }

        /// <summary>停止并清理（App.OnExit / Quit 调用）。</summary>
        public static void Shutdown()
        {
            Log("Shutdown: 停止调度");
            lock (_lock)
            {
                _shutdown = true;
                if (_started)
                {
                    SystemEvents.TimeChanged -= OnSystemTimeChanged;
                    SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                    _started = false;
                }
                _timer?.Dispose();
                _timer = null;
                _pendingPlan = null;
            }
        }

        /// <summary>重新计算：读取全部启用定时规则，只等待最近一次执行时刻。规则增删改/启停后调用。</summary>
        public static void RefreshScheduler(bool preservePendingDue = true)
        {
            lock (_lock)
            {
                if (_shutdown) return;
                var now = DateTime.Now;
                var rules = AutoRuleStore.LoadAll()
                    .Where(r => r.Enabled && r.Trigger == AutoRuleTrigger.Schedule).ToList();
                Log($"Refresh: 启用定时规则 {rules.Count} 条" + (rules.Count == 0 ? "" : " [" + string.Join(",", rules.Select(r => r.Name + "@" + r.ScheduleTime + "(" + r.ScheduleMode + ")")) + "]"));
                if (rules.Count == 0)
                {
                    _timer?.Dispose();
                    _timer = null;
                    _pendingPlan = null;
                    return;
                }

                DateTime? next = null;
                var dueRules = new List<AutoRule>();
                foreach (var r in rules)
                {
                    // 到点后、Dispatcher派发前发生无关编辑，仍保留同一有效到期事件，避免重算成明天。
                    bool pendingDue = preservePendingDue && _pendingPlan != null && _pendingPlan.Due <= now
                        && now - _pendingPlan.Due <= MissedTolerance
                        && r.LastRunKey != _pendingPlan.Due.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                        && _pendingPlan.Rules.Any(old => AutoRuleStore.SameSchedule(old, r));
                    var t = pendingDue ? _pendingPlan!.Due : NextRun(r, now);
                    if (t == null) continue;
                    if (next == null || t < next)
                    {
                        next = t;
                        dueRules.Clear();
                        dueRules.Add(r);
                    }
                    else if (t == next)
                    {
                        dueRules.Add(r);
                    }
                }

                if (next == null)
                {
                    // 所有定时规则都无未来时刻：清空等待
                    _timer?.Dispose();
                    _timer = null;
                    _pendingPlan = null;
                    return;
                }

                if (preservePendingDue && _pendingPlan != null && _pendingPlan.Due == next
                    && _pendingPlan.Rules.Count == dueRules.Count
                    && _pendingPlan.Rules.Zip(dueRules, AutoRuleStore.SameSchedule).All(same => same)) return;
                var plan = new Plan { Due = next.Value, Rules = dueRules };
                _pendingPlan = plan;
                var delay = next.Value - DateTime.Now;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                Log($"Refresh: 下次执行 {next:yyyy-MM-dd HH:mm:ss.fff}，等待 {(long)delay.TotalMilliseconds}ms，规则 [{string.Join(",", dueRules.Select(r => r.Id))}]");
                _timer?.Dispose();
                // 每次安排绑定不可变计划身份；已经入队的旧回调不能读取新计划冒充其到期。
                _timer = new System.Threading.Timer(_ => OnTimerElapsed(plan), null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        private static void OnTimerElapsed(Plan plan)
        {
            lock (_lock)
            {
                if (_shutdown || !ReferenceEquals(plan, _pendingPlan) || plan.CallbackTaken) return;
                var now = DateTime.Now;
                if (now < plan.Due) { _timer?.Change(plan.Due - now, Timeout.InfiniteTimeSpan); return; }
                plan.CallbackTaken = true;
            }
            var app = System.Windows.Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                if (!app.Dispatcher.HasShutdownStarted)
                    try { app.Dispatcher.BeginInvoke(new Action(() => ExecutePlan(plan))); }
                    catch (InvalidOperationException) { }
                return;
            }
            ExecutePlan(plan);
        }

        private static void ExecutePlan(Plan plan)
        {
            lock (_lock)
            {
                if (_shutdown || !ReferenceEquals(plan, _pendingPlan)) return;
                try
                {
                    if (DateTime.Now < plan.Due || DateTime.Now - plan.Due > MissedTolerance) return;
                    string key = plan.Due.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    foreach (var expected in plan.Rules)
                    {
                        if (NextRun(expected, plan.Due) != plan.Due) continue;
                        try
                        {
                            var claimed = AutoRuleStore.ClaimScheduledRun(expected, key);
                            if (claimed != null) _ = AutoRuleService.ExecuteAsync(claimed);
                        }
                        catch (Exception error) { Log("定时规则领取/执行失败: " + error.Message); }
                    }
                }
                catch (Exception error) { Log("定时回调失败: " + error.Message); }
                finally
                {
                    _pendingPlan = null;
                    _timer?.Dispose(); _timer = null;
                    // 保存失败沿用原本次不执行策略，不以0延时反复重试写盘。
                    try { RefreshScheduler(false); } catch (Exception error) { Log("定时计划刷新失败: " + error.Message); }
                }
            }
        }

        // ---------- 下一次执行时刻计算 ----------

        private static DateTime? NextRun(AutoRule r, DateTime from)
        {
            if (!TimeSpan.TryParse(r.ScheduleTime, out var time)) return null;
            return r.ScheduleMode switch
            {
                // 仅一次：下一次 HH:mm；由领取持久化停用，与步骤成功无关。
                0 => NextDaily(time, from),
                1 => NextDaily(time, from),
                2 => NextWeekly(r.ScheduleWeekdays, time, from),
                _ => null
            };
        }

        /// <summary>下一个到达时刻：今天该时刻未过用今天，已过用明天（仅一次与每天共用）。</summary>
        private static DateTime NextDaily(TimeSpan time, DateTime from)
        {
            var today = from.Date + time;
            return today >= from ? today : today.AddDays(1);
        }

        /// <summary>每周：取所选星期中最近的未来时刻；本周全部已过则顺延到下周。</summary>
        private static DateTime? NextWeekly(List<int> weekdays, TimeSpan time, DateTime from)
        {
            var days = weekdays?.Where(w => w is >= 0 and <= 6).Select(w => (int)w).Distinct().ToList();
            if (days == null || days.Count == 0) return null;
            // 从 from 所在周的周一开始逐日检查 7 天（覆盖本周）
            var monday = from.Date.AddDays(-(((int)from.DayOfWeek + 6) % 7));
            for (int i = 0; i < 7; i++)
            {
                var candidate = monday.AddDays(i);
                if (!days.Contains((int)candidate.DayOfWeek)) continue;
                var t = candidate + time;
                if (t >= from) return t;
            }
            // 本周所选天已全部过去 → 下周
            var nextMonday = monday.AddDays(7);
            for (int i = 0; i < 7; i++)
            {
                var candidate = nextMonday.AddDays(i);
                if (!days.Contains((int)candidate.DayOfWeek)) continue;
                return candidate + time;
            }
            return null;
        }

        // ---------- 系统事件 ----------

        private static void OnSystemTimeChanged(object? sender, EventArgs e) => RefreshScheduler(false);

        private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) RefreshScheduler(false);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
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
    /// - 仅一次规则执行成功后自动禁用（不删除）；仅一次取"下一个到达时刻"（今天未过用今天，已过顺延明天），不使用日期。
    /// - 执行复用 AutoRuleService.ExecuteAsync（多步骤，与快捷键/应用触发完全一致）。
    /// </summary>
    public static class AutoRuleScheduler
    {
        private static readonly object _lock = new();
        private static System.Threading.Timer? _timer;
        private static DateTime? _pendingDue;
        private static readonly List<string> _pendingRules = new();

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonicRoute", "scheduler.log");

        /// <summary>诊断日志（调试定时触发用，限长 256KB 自动截断；失败静默）。</summary>
        internal static void Log(string msg)
        {
            try
            {
                lock (_lock)
                {
                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}";
                    var fi = new FileInfo(LogPath);
                    if (!fi.Exists || fi.Length > 256 * 1024)
                        File.WriteAllText(LogPath, line);
                    else
                        File.AppendAllText(LogPath, line);
                }
            }
            catch { }
        }

        /// <summary>错过判定容差：Timer 到期时刻与当前时间相差超过该值视为错过（睡眠/休眠恢复），不补执行。</summary>
        private static readonly TimeSpan MissedTolerance = TimeSpan.FromMinutes(2);

        /// <summary>初始化：挂接系统时间变化与电源恢复监听，并立即重算（App.OnStartup 调用）。</summary>
        public static void Start()
        {
            Log("Start: 挂接系统事件并开始调度");
            SystemEvents.TimeChanged += OnSystemTimeChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            RefreshScheduler();
        }

        /// <summary>停止并清理（App.OnExit / Quit 调用）。</summary>
        public static void Shutdown()
        {
            Log("Shutdown: 停止调度");
            SystemEvents.TimeChanged -= OnSystemTimeChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
                _pendingDue = null;
                _pendingRules.Clear();
            }
        }

        /// <summary>重新计算：读取全部启用定时规则，只等待最近一次执行时刻。规则增删改/启停后调用。</summary>
        public static void RefreshScheduler()
        {
            lock (_lock)
            {
                var rules = AutoRuleStore.LoadAll()
                    .Where(r => r.Enabled && r.Trigger == AutoRuleTrigger.Schedule).ToList();
                Log($"Refresh: 启用定时规则 {rules.Count} 条" + (rules.Count == 0 ? "" : " [" + string.Join(",", rules.Select(r => r.Name + "@" + r.ScheduleTime + "(" + r.ScheduleMode + ")")) + "]"));
                if (rules.Count == 0)
                {
                    _timer?.Dispose();
                    _timer = null;
                    _pendingDue = null;
                    _pendingRules.Clear();
                    return;
                }

                DateTime? next = null;
                var dueRules = new List<string>();
                foreach (var r in rules)
                {
                    var t = NextRun(r, DateTime.Now);
                    if (t == null) continue;
                    if (next == null || t < next)
                    {
                        next = t;
                        dueRules.Clear();
                        dueRules.Add(r.Id);
                    }
                    else if (t == next)
                    {
                        dueRules.Add(r.Id);
                    }
                }

                if (next == null)
                {
                    // 所有定时规则都无未来时刻：清空等待
                    _timer?.Dispose();
                    _timer = null;
                    _pendingDue = null;
                    _pendingRules.Clear();
                    return;
                }

                _pendingDue = next;
                _pendingRules.Clear();
                _pendingRules.AddRange(dueRules);
                var delay = next.Value - DateTime.Now;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                Log($"Refresh: 下次执行 {next:yyyy-MM-dd HH:mm:ss.fff}，等待 {(long)delay.TotalMilliseconds}ms，规则 [{string.Join(",", dueRules)}]");
                _timer ??= new System.Threading.Timer(_ => OnTimerElapsed(), null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        private static void OnTimerElapsed()
        {
            DateTime due;
            List<string> dueRules;
            lock (_lock)
            {
                due = _pendingDue ?? DateTime.MinValue;
                dueRules = new List<string>(_pendingRules);
            }

            // 错过（睡眠/休眠恢复后 Timer 补到期）：不补执行，直接重算下一次
            if (DateTime.Now - due > MissedTolerance)
            {
                Log($"Timer 触发但已错过（到期 {due:HH:mm:ss.fff}，现在 {DateTime.Now:HH:mm:ss.fff}，差 {(DateTime.Now - due).TotalSeconds:F1}s），不补执行");
                RefreshScheduler();
                return;
            }
            Log($"Timer 触发，到期 {due:yyyy-MM-dd HH:mm:ss.fff}，规则 [{string.Join(",", dueRules)}]");

            // 到点：执行所有预定该时刻且尚未执行的规则
            foreach (var id in dueRules)
            {
                var r = AutoRuleStore.Find(id);
                if (r == null || !r.Enabled || r.Trigger != AutoRuleTrigger.Schedule) continue;
                // 用到期时刻精确判定（避免 Timer 晚到导致 NextRun 把本次跳过）
                var t = NextRun(r, due);
                if (t == null || t != due) continue;
                var dueKey = due.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                if (string.Equals(r.LastRunKey, dueKey, StringComparison.Ordinal)) continue; // 防重复
                ExecuteDue(r, dueKey);
            }

            RefreshScheduler();
        }

        /// <summary>标记已执行（写 LastRunKey；仅一次自动禁用）后，复用自动化执行引擎执行。</summary>
        private static void ExecuteDue(AutoRule rule, string dueKey)
        {
            Log($"ExecuteDue: 规则 [{rule.Name}] 到期 {dueKey}，步骤数 {rule.Actions.Count}");
            rule.LastRunKey = dueKey;
            if (rule.ScheduleMode == 0) rule.Enabled = false; // 仅一次：执行后自动禁用（不删除）
            AutoRuleStore.Save(rule);

            var app = System.Windows.Application.Current;
            if (app == null) { _ = AutoRuleService.ExecuteAsync(rule); return; }
            if (app.Dispatcher.CheckAccess())
                _ = AutoRuleService.ExecuteAsync(rule);
            else
                app.Dispatcher.BeginInvoke(() => _ = AutoRuleService.ExecuteAsync(rule));
        }

        // ---------- 下一次执行时刻计算 ----------

        private static DateTime? NextRun(AutoRule r, DateTime from)
        {
            if (!TimeSpan.TryParse(r.ScheduleTime, out var time)) return null;
            return r.ScheduleMode switch
            {
                // 仅一次：取下一个到达时刻（今天未过用今天，已过用明天），执行成功后自动禁用
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

        private static void OnSystemTimeChanged(object? sender, EventArgs e) => RefreshScheduler();

        private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) RefreshScheduler();
        }
    }
}

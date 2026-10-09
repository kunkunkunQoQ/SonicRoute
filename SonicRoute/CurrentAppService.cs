using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using SonicRoute.Core;
using SonicRoute.Core.Compat;
using SonicRoute.Core.Models;

namespace SonicRoute
{
    /// <summary>
    /// 当前应用的统一解析与共享状态：快捷面板、概览、托盘滚轮、全局快捷键全部操作同一个"当前应用"。
    ///
    /// 规则（始终只落在"有音频会话的应用"上）：
    ///  1. 用户显式配置 last/fixed 时精确匹配上次操作 / 指定应用；
    ///  2. 直接前台应用（有音频，且非本程序自身——窗口未抢焦点时能立刻命中）；
    ///  3. 最近一次有音频的前台应用（前台监听维护；解决面板/概览抢焦点后永远落回列表第一个的问题）；
    ///  4. 上次操作的应用；5. 任意第一个有音频应用。
    ///
    /// 前台监听（StartForegroundWatcher）：通过前台通知记录"最近有音频的前台应用"；
    /// 在 recent 模式下自动把当前应用切到该应用，实现"最近使用的程序"自动跟随抖音/游戏等。
    /// </summary>
    public static class CurrentAppService
    {
        private static readonly object _lock = new();
        private static AudioAppInfo? _current;
        private static AudioAppInfo? _lastForegroundAudio;

        /// <summary>共享"当前应用"（面板/概览显示、快捷键静音/切设备的目标）。变更触发 CurrentChanged。</summary>
        public static event Action? CurrentChanged;

        public static AudioAppInfo? Current
        {
            get { lock (_lock) return _current; }
            set
            {
                bool changed;
                lock (_lock)
                {
                    changed = !SameApp(_current, value);
                    _current = value;
                }
                if (changed) CurrentChanged?.Invoke();
            }
        }

        /// <summary>最近一次有音频的前台应用（任何模式都记录，recent 模式用它切换当前应用）。</summary>
        public static AudioAppInfo? LastForegroundAudio
        {
            get { lock (_lock) return _lastForegroundAudio; }
            set { lock (_lock) _lastForegroundAudio = value; }
        }

        private static bool SameApp(AudioAppInfo? a, AudioAppInfo? b)
        {
            if (a == null || b == null) return a == null && b == null;
            return a.ProcessId == b.ProcessId;
        }

        /// <summary>在前台应用列表里匹配一个音频应用：先精确 PID，再按进程名（解决哔哩哔哩等
        /// 客户端 UI 进程与音频 helper 进程同名不同 PID 的情况）。已"禁用自动切换"的应用不参与匹配。</summary>
        private static AudioAppInfo? MatchForeground(List<AudioAppInfo> apps, int fgPid)
        {
            var disabled = ConfigService.Load().DisabledAutoSwitchApps;
            bool Off(AudioAppInfo a) =>
                !string.IsNullOrWhiteSpace(a.ProcessName) && disabled.Contains(a.ProcessName);

            var exact = apps.FirstOrDefault(a => a.ProcessId == (uint)fgPid);
            if (exact != null && !Off(exact)) return exact;
            string? fgName = ForegroundAppService.GetProcessNameSafe(fgPid);
            if (string.IsNullOrWhiteSpace(fgName)) return null;
            return apps.FirstOrDefault(a =>
                string.Equals(a.ProcessName, fgName, StringComparison.OrdinalIgnoreCase) && !Off(a));
        }

        public static AudioAppInfo? Resolve(List<AudioAppInfo> apps, AppConfig cfg, int? foregroundPid = null)
        {
            if (apps == null || apps.Count == 0) return null;

            var disabled = cfg.DisabledAutoSwitchApps;
            bool Off(AudioAppInfo a) =>
                !string.IsNullOrWhiteSpace(a.ProcessName) && disabled.Contains(a.ProcessName);

            AudioAppInfo? ByName(string? n) =>
                string.IsNullOrWhiteSpace(n) ? null :
                apps.FirstOrDefault(x => string.Equals(x.ProcessName, n, StringComparison.OrdinalIgnoreCase));

            // 1) 用户显式指定：last / fixed 精确匹配（被禁用自动切换的应用不自动选中，仍可手动选择）
            if (cfg.DefaultAppMode == "last")
            {
                var t = ByName(cfg.LastUsedAppName);
                if (t != null && !Off(t)) return t;
            }
            else if (cfg.DefaultAppMode == "fixed")
            {
                var t = ByName(cfg.FixedAppName);
                if (t != null && !Off(t)) return t;
            }

            // 2) 直接前台应用（有音频、非本程序）——先精确 PID，再按进程名；MatchForeground 已跳过禁用应用
            int own = AppInfo.CurrentProcessId;
            int fg = foregroundPid ?? ForegroundAppService.GetForegroundProcessId();
            if (fg > 0 && fg != own)
            {
                var a = MatchForeground(apps, fg);
                if (a != null) return a;
            }

            // 3) 最近一次有音频的前台应用（跳过禁用；面板/概览在前台时仍能回到用户正在用的应用）
            var last = LastForegroundAudio;
            if (last != null && apps.Any(x => x.ProcessId == last.ProcessId) && !Off(last)) return last;

            // 4) 上次操作的应用；5) 兜底（跳过禁用，全部禁用时回退列表第一个）
            var byLast = ByName(cfg.LastUsedAppName);
            if (byLast != null && !Off(byLast)) return byLast;
            return apps.FirstOrDefault(a => !Off(a)) ?? apps[0];
        }

        private static int _ownPid, _lastCheckedFgPid, _foregroundRetryBudget;
        private static long _foregroundGeneration;
        private static bool _started, _requested, _needsRetry;
        private static Task _foregroundWork = Task.CompletedTask;
        private static ForegroundChangeSource? _foregroundSource;
        private static DispatcherTimer? _foregroundTimer;

        /// <summary>前台通知立即匹配，1.5s 廉价 PID 检查兜底；音频查询只保留最新请求。</summary>
        public static Task StartForegroundWatcher()
        {
            if (_started) return _foregroundWork;
            _started = true;
            _ownPid = AppInfo.CurrentProcessId;
            _lastCheckedFgPid = -1;
            _foregroundSource = new ForegroundChangeSource(() => { _ = RequestForegroundAsync(); });
            _foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _foregroundTimer.Tick += (_, _) => { _ = RequestForegroundAsync(); };
            _foregroundTimer.Start();
            return RequestForegroundAsync();
        }

        public static void StopForegroundWatcher()
        {
            _started = false;
            ++_foregroundGeneration;
            _requested = false;
            _foregroundTimer?.Stop(); _foregroundTimer = null;
            _foregroundSource?.Dispose(); _foregroundSource = null;
        }

        private static Task RequestForegroundAsync()
        {
            if (!_started) return Task.CompletedTask;
            int fg = ForegroundAppService.GetForegroundProcessId();
            if (fg == _lastCheckedFgPid && !_needsRetry) return _foregroundWork;
            if (fg == _lastCheckedFgPid && !_foregroundWork.IsCompleted) return _foregroundWork;
            if (fg != _lastCheckedFgPid) _foregroundRetryBudget = 2;
            _lastCheckedFgPid = fg;
            ++_foregroundGeneration;
            _needsRetry = false;
            _requested = fg > 0 && fg != _ownPid;
            if (_requested && _foregroundWork.IsCompleted) _foregroundWork = ProcessForegroundAsync();
            return _foregroundWork;
        }

        private static async Task ProcessForegroundAsync()
        {
            while (_started && _requested)
            {
                _requested = false;
                int fg = _lastCheckedFgPid;
                long generation = _foregroundGeneration;
                AudioAppInfo? app = null;
                try { app = await Task.Run(() => MatchForeground(AudioService.GetApps(), fg)); }
                catch { }
                if (!_started || generation != _foregroundGeneration) continue;
                if (fg != ForegroundAppService.GetForegroundProcessId())
                { _ = RequestForegroundAsync(); continue; }
                // 覆盖启动会话的短延迟；长期停在无音频应用时不持续枚举 COM。
                _needsRetry = app == null && _foregroundRetryBudget-- > 0;
                if (app != null)
                    try { ApplyForeground(app); } catch { /* 订阅方失败不停止前台监听。 */ }
            }
        }
        private static void ApplyForeground(AudioAppInfo app)
        {
            var prev = LastForegroundAudio;
            if (prev != null && prev.ProcessId == app.ProcessId) return;
            LastForegroundAudio = app;
            var cfg = ConfigService.Load();
            if (cfg.DefaultAppMode == "recent") Current = app;
        }
    }
}

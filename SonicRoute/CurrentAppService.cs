using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using SonicRoute.Core;
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
    /// 前台监听（StartForegroundWatcher）：每 1.2s 记录"最近有音频的前台应用"；
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

        public static AudioAppInfo? Resolve(List<AudioAppInfo> apps, AppConfig cfg)
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
            int own = Environment.ProcessId;
            int fg = ForegroundAppService.GetForegroundProcessId();
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

        private static int _ownPid;
        private static int _lastCheckedFgPid;

        /// <summary>启动前台监听（App 启动时在 UI 线程调用）。每 1.5s 只做一次廉价的前台 PID
        /// 查询；仅当前台应用发生变化时才在后台线程做音频会话匹配，避免高频全量 COM 枚举拖慢
        /// UI（这是此前 CPU/内存占用偏高的根因）。recent 模式下把当前应用切到该应用。
        /// 启动时窗口尚未显示，先立即记录一次当前前台，避免 --panel/--main 直接启动落到
        /// 列表第一个（用户所说的"总是哔哩哔哩"）。</summary>
        public static void StartForegroundWatcher()
        {
            _ownPid = Environment.ProcessId;
            TryRecordForeground(); // 窗口未显示，前台仍是用户正在用的应用
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += async (_, _) => await TickForegroundAsync();
            timer.Start();
        }

        private static async Task TickForegroundAsync()
        {
            try
            {
                int fg = ForegroundAppService.GetForegroundProcessId();
                if (fg <= 0 || fg == _ownPid) return; // 无前台或本程序在前台：保持当前应用不变
                if (fg == _lastCheckedFgPid) return;  // 前台未变：不重复枚举
                _lastCheckedFgPid = fg;

                var app = await Task.Run(() =>
                {
                    try
                    {
                        var apps = AudioService.GetApps(); // 1s 缓存
                        return MatchForeground(apps, fg);
                    }
                    catch { return null; }
                });
                if (app == null) return; // 前台应用没有音频会话
                ApplyForeground(app);
            }
            catch
            {
                // 前台监听失败不影响核心功能
            }
        }

        /// <summary>立即同步记录一次前台音频应用（启动时调用，此时窗口未显示）。</summary>
        private static void TryRecordForeground()
        {
            try
            {
                int fg = ForegroundAppService.GetForegroundProcessId();
                if (fg <= 0 || fg == _ownPid) return;
                if (fg == _lastCheckedFgPid) return;
                _lastCheckedFgPid = fg;
                var app = MatchForeground(AudioService.GetApps(), fg);
                if (app == null) return;
                ApplyForeground(app);
            }
            catch
            {
                // 前台记录失败不影响核心功能
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

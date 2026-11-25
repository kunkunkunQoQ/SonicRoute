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
    /// 当前应用的统一解析与共享状态：快速面板、托盘滚轮、全局快捷键全部操作同一个"当前应用"。
    ///
    /// 规则（始终只落在"有音频会话的应用"上）：
    ///  1. 用户显式配置 last/fixed 时精确匹配上次操作 / 指定应用；
    ///  2. 直接前台应用（有音频，且非本程序自身——窗口未抢焦点时能立刻命中）；
    ///  3. 最近一次有音频的前台应用（前台监听维护；解决面板抢焦点后永远落回列表第一个的问题）；
    ///  4. 上次操作的应用；5. 任意第一个有音频应用。
    ///
    /// 前台监听（StartForegroundWatcher）：通过前台通知记录"最近有音频的前台应用"；
    /// 在 recent 模式下自动把当前应用切到该应用，实现"最近使用的程序"自动跟随抖音/游戏等。
    /// </summary>
    public static class CurrentAppService
    {
        private static readonly CurrentAppSelection Selection = new();
        public static event Action? CurrentChanged
        {
            add => Selection.Changed += value;
            remove => Selection.Changed -= value;
        }
        internal static event Action<int>? ForegroundChanged;
        public static AudioAppInfo? Current { get => Selection.Current; set => Selection.Current = value; }
        public static AudioAppInfo? LastForegroundAudio { get => Selection.LastForeground; set => Selection.LastForeground = value; }

        private static AudioAppInfo? MatchForeground(List<AudioAppInfo> apps, int fgPid
#if SONICROUTE_PHONE
            , nint fgHwnd = 0
#endif
            ) => CurrentAppSelection.MatchForeground(apps, ConfigService.Load(), fgPid,
#if SONICROUTE_PHONE
                fgPid != AppInfo.CurrentProcessId, () => ApplicationAudio.ForegroundAppName(fgPid, fgHwnd));
#else
                true, () => ApplicationAudio.ForegroundAppName(fgPid));
#endif

        public static AudioAppInfo? Resolve(List<AudioAppInfo> apps, AppConfig cfg, int? foregroundPid = null)
            => ResolveCore(apps, cfg, foregroundPid, IntPtr.Zero);

        internal static AudioAppInfo? ResolveWithWindow(List<AudioAppInfo> apps, AppConfig cfg, int foregroundPid, IntPtr foregroundHwnd)
            => ResolveCore(apps, cfg, foregroundPid, foregroundHwnd);

        private static AudioAppInfo? ResolveCore(List<AudioAppInfo> apps, AppConfig cfg, int? foregroundPid, IntPtr foregroundHwnd)
        {
            return Selection.Resolve(apps, cfg, () =>
            {
                int own = AppInfo.CurrentProcessId;
#if SONICROUTE_PHONE
                int fg;
                nint fgHwnd = foregroundHwnd;
                if (foregroundPid == null)
                {
                    (fg, nint liveHwnd) = ApplicationAudio.GetForegroundWindowContext();
                    if (fgHwnd == 0) fgHwnd = liveHwnd;
                }
                else
                {
                    fg = foregroundPid.Value;
                    if (fg == own && fgHwnd == 0)
                    {
                        var ctx = ApplicationAudio.GetForegroundWindowContext();
                        if (ctx.Pid == own) fgHwnd = ctx.Hwnd;
                    }
                }
                bool ownVirtualDisplay = fg == own && ApplicationAudio.IsVirtualDisplayWindow(fgHwnd);
                if (fg > 0 && (fg != own || ownVirtualDisplay))
                {
                    var a = MatchForeground(apps, fg, fgHwnd);
                    if (a != null) return a;
                }
#else
                int fg = foregroundPid ?? ForegroundAppService.GetForegroundProcessId();
                if (fg > 0 && fg != own)
                {
                    var a = MatchForeground(apps, fg);
                    if (a != null) return a;
                }
#endif
                return null;
            });
        }

        private static int _ownPid, _lastCheckedFgPid, _foregroundRetryBudget;
#if SONICROUTE_PHONE
        private static nint _lastCheckedFgHwnd;
#endif
        private static long _foregroundGeneration;
        private static bool _started, _requested, _needsRetry, _foregroundQueryFailed;
        private static Task _foregroundWork = Task.CompletedTask;
        private static ForegroundChangeSource? _foregroundSource;
        private static DispatcherTimer? _foregroundTimer;

        /// <summary>正常时仅前台通知；无音频短期重试，注册或查询失败时临时检查并恢复。</summary>
        public static Task StartForegroundWatcher()
        {
            if (_started) return _foregroundWork;
            _started = true;
            _ownPid = AppInfo.CurrentProcessId;
            _lastCheckedFgPid = -1;
#if SONICROUTE_PHONE
            _lastCheckedFgHwnd = 0;
#endif
            _needsRetry = _foregroundQueryFailed = false;
            _foregroundSource = new ForegroundChangeSource(() => { _ = RequestForegroundAsync(); });
            _foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _foregroundTimer.Tick += (_, _) =>
            {
                _foregroundTimer?.Stop();
                if (!_started) return;
                bool restoreHook = _foregroundSource?.IsInstalled != true;
                bool restored = _foregroundSource?.TryRegister() == true;
                if (!_foregroundWork.IsCompleted) { UpdateForegroundTimer(); return; }
                _ = QueueForegroundAsync(_foregroundQueryFailed || (restoreHook && restored));
                UpdateForegroundTimer();
            };
            UpdateForegroundTimer();
            return RequestForegroundAsync();
        }

        // 系统恢复等明确失效信号到来时重新挂接；不以常驻轮询判断健康接口。
        internal static void RecoverForegroundWatcher()
        {
            if (!_started) return;
            _foregroundSource?.Dispose();
            _foregroundSource = new ForegroundChangeSource(() => { _ = RequestForegroundAsync(); });
            _ = QueueForegroundAsync(true);
            UpdateForegroundTimer();
        }

        private static void UpdateForegroundTimer()
        {
            if (_foregroundTimer == null) return;
            if (_started && (ConfigService.Load().BackgroundPollingFallbackEnabled
                || _foregroundSource?.IsInstalled != true || _needsRetry || _foregroundQueryFailed))
                _foregroundTimer.Start();
            else _foregroundTimer.Stop();
        }

        internal static void RefreshMonitoringPolicy()
        {
            if (!_started) return;
            UpdateForegroundTimer();
            _ = QueueForegroundAsync(true);
        }

        public static void StopForegroundWatcher()
        {
            _started = false;
            ++_foregroundGeneration;
            _requested = false;
            _needsRetry = _foregroundQueryFailed = false;
            _foregroundTimer?.Stop(); _foregroundTimer = null;
            _foregroundSource?.Dispose(); _foregroundSource = null;
        }

        private static Task RequestForegroundAsync() => QueueForegroundAsync(false);

        private static Task QueueForegroundAsync(bool force)
        {
            if (!_started) return Task.CompletedTask;
            int fg = ForegroundAppService.GetForegroundProcessId();
#if SONICROUTE_PHONE
            nint fgHwnd = fg == _ownPid ? ApplicationAudio.GetForegroundWindowHandle() : 0;
            bool changed = fg != _lastCheckedFgPid || (fg == _ownPid && fgHwnd != _lastCheckedFgHwnd);
#else
            bool changed = fg != _lastCheckedFgPid;
#endif
            if (changed)
                try { ForegroundChanged?.Invoke(fg); } catch { }
            if (!force && !changed && !_needsRetry) return _foregroundWork;
            if (!force && !changed && !_foregroundWork.IsCompleted) return _foregroundWork;
            if (force || changed) _foregroundRetryBudget = 2;
            _lastCheckedFgPid = fg;
#if SONICROUTE_PHONE
            _lastCheckedFgHwnd = fgHwnd;
#endif
            ++_foregroundGeneration;
            _needsRetry = false;
#if SONICROUTE_PHONE
            bool ownVirtualDisplay = fg == _ownPid && ApplicationAudio.IsVirtualDisplayWindow(fgHwnd);
            _requested = fg > 0 && (fg != _ownPid || ownVirtualDisplay);
#else
            _requested = fg > 0 && fg != _ownPid;
#endif
            if (!_requested) _foregroundQueryFailed = false;
            UpdateForegroundTimer();
            if (_requested && _foregroundWork.IsCompleted) _foregroundWork = ProcessForegroundAsync();
            return _foregroundWork;
        }

        private static async Task ProcessForegroundAsync()
        {
            while (_started && _requested)
            {
                _requested = false;
                int fg = _lastCheckedFgPid;
#if SONICROUTE_PHONE
                nint fgHwnd = _lastCheckedFgHwnd;
#endif
                long generation = _foregroundGeneration;
                AudioAppInfo? app = null;
                bool failed = false;
#if SONICROUTE_PHONE
                try { app = await Task.Run(() => MatchForeground(ApplicationAudio.GetApps(), fg, fgHwnd)); }
#else
                try { app = await Task.Run(() => MatchForeground(ApplicationAudio.GetApps(), fg)); }
#endif
                catch { failed = true; }
                if (!_started || generation != _foregroundGeneration) continue;
                if (fg != ForegroundAppService.GetForegroundProcessId()
#if SONICROUTE_PHONE
                    || (fg == _ownPid && fgHwnd != ApplicationAudio.GetForegroundWindowHandle())
#endif
                    )
                { _ = RequestForegroundAsync(); continue; }
                // 覆盖启动会话的短延迟；长期停在无音频应用时不持续枚举 COM。
                _foregroundQueryFailed = failed;
                _needsRetry = !failed && app == null && _foregroundRetryBudget-- > 0;
                if (app != null)
                    try { ApplyForeground(app); } catch { /* 订阅方失败不停止前台监听。 */ }
                UpdateForegroundTimer();
            }
        }
        private static void ApplyForeground(AudioAppInfo app)
        {
            Selection.Observe(app, ConfigService.Load());
        }
    }
}

using System;
using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Core.Compat;
using SonicRoute.Core.Models;

namespace SonicRoute
{
    /// <summary>
    /// 托盘滚轮调音量：安装 WH_MOUSE_LL 低层鼠标钩子，当滚轮事件发生时光标位于
    /// 系统托盘通知区（TrayNotifyWnd / 溢出窗口）内，就对当前前台应用调节音量，
    /// 并显示一个小型 OSD 反馈。只调前台应用自己的会话音量，不动系统主音量。
    /// </summary>
    internal sealed class TrayWheelService : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PtInRect(ref RECT lprc, POINT pt);
        // 仅音跃图标模式：Shell_NotifyIconGetRect 获取托盘图标矩形（物理像素，与钩子坐标一致）
        [StructLayout(LayoutKind.Sequential)]
        private struct NOTIFYICONIDENTIFIER
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public Guid guidItem;
        }

        [DllImport("shell32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        private readonly System.Windows.Forms.NotifyIcon? _trayIcon;

        private readonly LowLevelMouseProc _proc;
        private IntPtr _hook;
        private volatile bool _disposed;

        // 滚轮合并派发（钩子只做检测；单个后台任务操作音频，结果返回 UI 显示 OSD）
        private int _pendingDelta;
        private bool _dispatchScheduled;

        // 目标应用解析缓存（与快捷面板一致，避免每次滚轮全量枚举）
        private readonly object _appsLock = new();
        private List<AudioAppInfo>? _cachedApps;
        private DateTime _cachedAt;

        private readonly OsdService _osdService;
        internal event Action? OsdAdjustFinished
        {
            add => _osdService.AdjustFinished += value;
            remove => _osdService.AdjustFinished -= value;
        }
        internal long MicStateVersion => _osdService.MicStateVersion;
        internal Task<bool> QueryMicMuteAsync(bool trackInput) => _osdService.QueryMicMuteAsync(trackInput);

        public TrayWheelService(System.Windows.Forms.NotifyIcon? trayIcon)
        {
            _trayIcon = trayIcon;
            _proc = HookProc;
            _osdService = new OsdService();
        }

        /// <summary>必须在 UI 线程调用（钩子回调将运行在安装线程的消息循环上）。</summary>
        public void Start()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (!_disposed && nCode >= 0 && (int)wParam == WM_MOUSEWHEEL)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (IsOverTray(data.pt))
                {
                    int delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    // 钩子线程绝不做 COM/音频调用：合并滚轮量，UI 入口启动后台处理。
                    System.Threading.Interlocked.Add(ref _pendingDelta, delta);
                    if (!_dispatchScheduled)
                    {
                        _dispatchScheduled = true;
                        var app = (App)System.Windows.Application.Current;
                        app.Dispatcher.BeginInvoke(new Action(ProcessPendingWheel));
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private async void ProcessPendingWheel()
        {
            try
            {
                while (!_disposed)
                {
                    int delta = System.Threading.Interlocked.Exchange(ref _pendingDelta, 0);
                    if (delta == 0) break;
                    // 记录此次输入时的前台，后台查询期间焦点变化不改本轮目标。
                    int foregroundPid = ForegroundAppService.GetForegroundProcessId();
                    var result = await Task.Run(() => AdjustVolume(delta, foregroundPid));
                    if (_disposed) return;
                    if (result.App == null)
                    {
                        ShowOsd(L10n.T("Ov.VolumeTitle"), L10n.T("Ov.NoSession"));
                        continue;
                    }
                    string proc = result.ProcessName;
                    string name = AppDisplayName.Get(proc, string.IsNullOrWhiteSpace(proc) ? "应用" : proc);
                    if (result.Current < 0) { ShowOsd(name, L10n.T("Ov.VolReadFail")); continue; }
                    ShowVolumeOsd(name, result.Actual,
                        result.Muted ? string.Format(L10n.T("Ov.MutedVol"), result.Actual) : $"🔊 {result.Actual}%",
                        result.Muted, "app:" + result.App.ProcessId);
                }
            }
            catch { /* 设备/会话退出时不让异步滚轮任务打断消息循环。 */ }
            finally
            {
                _dispatchScheduled = false;
                if (!_disposed && System.Threading.Volatile.Read(ref _pendingDelta) != 0)
                {
                    _dispatchScheduled = true;
                    _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(ProcessPendingWheel));
                }
            }
        }

        /// <summary>
        /// 判断光标是否在"可调音量区域"：
        /// 配置 TrayWheelEverywhere=true（默认）→ 整个托盘通知区（含溢出区、第二任务栏）都响应；
        /// false → 仅音跃自己的托盘图标矩形内响应（Shell_NotifyIconGetRect）。
        /// </summary>
        private bool IsOverTray(POINT pt)
        {
            try
            {
                if (!ConfigService.Load().TrayWheelEverywhere)
                    return IsOverOurIcon(pt);
            }
            catch { }

            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                if (notify != IntPtr.Zero && GetWindowRect(notify, out var r) && PtInRect(ref r, pt))
                    return true;
                IntPtr overflow = FindWindowEx(tray, IntPtr.Zero, "NotifyContainerOverflowWindow", null);
                if (overflow != IntPtr.Zero && GetWindowRect(overflow, out var r2) && PtInRect(ref r2, pt))
                    return true;
            }
            // 第二任务栏（多显示器）
            IntPtr tray2 = FindWindow("Shell_SecondaryTrayWnd", null);
            if (tray2 != IntPtr.Zero)
            {
                IntPtr notify2 = FindWindowEx(tray2, IntPtr.Zero, "TrayNotifyWnd", null);
                if (notify2 != IntPtr.Zero && GetWindowRect(notify2, out var r3) && PtInRect(ref r3, pt))
                    return true;
            }
            return false;
        }

        /// <summary>仅音跃图标模式：优先 Shell_NotifyIconGetRect 获取当前托盘图标矩形（物理像素），
        /// 失败则回退到托盘 Toolbar 按钮枚举。反射取 NotifyIcon 内部消息窗口句柄与图标 ID
        /// （.NET 8 字段名为 _window/_id，兼容旧版 window/id）。</summary>
        private bool IsOverOurIcon(POINT pt)
        {
            try
            {
                if (_trayIcon == null) return false;
                var t = _trayIcon.GetType();
                IntPtr hwnd = IntPtr.Zero;
                int id = 1;

                var winField = t.GetField("_window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                            ?? t.GetField("window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (winField != null && winField.GetValue(_trayIcon) is { } win)
                {
                    var hp = win.GetType().GetProperty("Handle", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (hp != null && hp.GetValue(win) is IntPtr h) hwnd = h;
                }
                var idField = t.GetField("_id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                           ?? t.GetField("id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (idField != null && idField.GetValue(_trayIcon) is { } v)
                {
                    long l = v is IntPtr ip ? ip.ToInt64() : Convert.ToInt64(v);
                    id = (int)l;
                }
                if (hwnd == IntPtr.Zero) return false;

                // 主方案：Shell_NotifyIconGetRect（官方 API，含溢出区/多任务栏）。
                // 注意：部分系统（含本机 Win11）返回值为 False 但 out 矩形实际有效
                // （图标物理像素矩形）。必须【先调用再检查矩形】——若写成
                // GetRect() && rect>0 的短路形式，GetRect 返回 False 时矩形检查
                // 不会执行、out 参数保持全 0，导致有效矩形被丢弃。
                var nii = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = hwnd, uID = id };
                Shell_NotifyIconGetRect(ref nii, out var r);
                if ((r.Right - r.Left) > 0 && (r.Bottom - r.Top) > 0)
                {
                    _cachedIconRect = r;
                    _cachedIconRectAt = DateTime.UtcNow;
                    var ir = InflateRect(r, 4);
                    return PtInRect(ref ir, pt);
                }

                // API 完全失败（返回 False 且矩形无效）：图标位置短期不变，
                // 用最近一次成功矩形（30s 内）判定，避免 Win11 上不可靠的 Toolbar 回退造成偶发失灵
                if ((DateTime.UtcNow - _cachedIconRectAt).TotalSeconds < 30
                    && (_cachedIconRect.Right - _cachedIconRect.Left) > 0
                    && (_cachedIconRect.Bottom - _cachedIconRect.Top) > 0)
                {
                    var ir2 = InflateRect(_cachedIconRect, 4);
                    return PtInRect(ref ir2, pt);
                }

                // 回退：托盘 Toolbar 按钮枚举（匹配 hWnd/uID 后取按钮矩形；Win11 可能无按钮窗口）
                return IsOverOurIconByToolbar(pt, hwnd, id);
            }
            catch { }
            return false;
        }

        /// <summary>图标矩形缓存：Shell_NotifyIconGetRect 偶发失败（返回 False 且矩形无效）时，用最近一次成功矩形判定。</summary>
        private RECT _cachedIconRect;
        private DateTime _cachedIconRectAt;

        /// <summary>矩形外扩 n 像素（物理像素坐标，减少图标边缘 1-2px 漏判）。</summary>
        private static RECT InflateRect(RECT r, int n)
        {
            r.Left -= n; r.Top -= n; r.Right += n; r.Bottom += n;
            return r;
        }

        private const int TB_BUTTONCOUNT = 0x418;
        private const int TB_GETBUTTON = 0x417;
        private const int TB_GETITEMRECT = 0x41D;

        [StructLayout(LayoutKind.Sequential)]
        private struct TBBUTTON
        {
            public int iBitmap;
            public int idCommand;
            public byte fsState;
            public byte fsStyle;
            public byte bReserved;
            public IntPtr dwData;   // 指向 NOTIFYICONDATA（本进程添加时传入）
            public IntPtr iString;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref TBBUTTON lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

        /// <summary>托盘 Toolbar 按钮枚举：遍历 TrayNotifyWnd/溢出窗口/第二任务栏的 ToolbarWindow32，
        /// 匹配按钮注册的 hWnd+uID（NOTIFYICONDATA，x64 布局：hWnd@8、uID@16），取按钮矩形判定光标。</summary>
        private static bool IsOverOurIconByToolbar(POINT pt, IntPtr hwnd, int id)
        {
            try
            {
                IntPtr[] trays = { FindWindow("Shell_TrayWnd", null), FindWindow("Shell_SecondaryTrayWnd", null) };
                foreach (var tray in trays)
                {
                    if (tray == IntPtr.Zero) continue;
                    IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                    if (notify != IntPtr.Zero && CheckTrayToolbar(notify, pt, hwnd, id)) return true;
                    IntPtr overflow = FindWindowEx(tray, IntPtr.Zero, "NotifyContainerOverflowWindow", null);
                    if (overflow != IntPtr.Zero && CheckTrayToolbar(overflow, pt, hwnd, id)) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool CheckTrayToolbar(IntPtr parent, POINT pt, IntPtr hwnd, int id)
        {
            try
            {
                IntPtr tb = FindWindowEx(parent, IntPtr.Zero, "ToolbarWindow32", null);
                if (tb == IntPtr.Zero) return false;
                int count = (int)SendMessage(tb, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
                if (count <= 0) return false;
                for (int i = 0; i < count; i++)
                {
                    TBBUTTON b = default;
                    if (SendMessage(tb, TB_GETBUTTON, (IntPtr)i, ref b) == IntPtr.Zero) continue;
                    if (b.dwData == IntPtr.Zero) continue;
                    IntPtr bh = (IntPtr)Marshal.ReadInt64(b.dwData, 8); // NOTIFYICONDATA.hWnd (x64)
                    int bid = Marshal.ReadInt32(b.dwData, 16);           // NOTIFYICONDATA.uID
                    if (bh == hwnd && bid == id)
                    {
                        RECT r = default;
                        if (SendMessage(tb, TB_GETITEMRECT, (IntPtr)i, ref r) != IntPtr.Zero)
                            return PtInRect(ref r, pt);
                    }
                }
            }
            catch { }
            return false;
        }

        private (AudioAppInfo? App, string ProcessName, int Current, int Actual, bool Muted) AdjustVolume(int delta, int foregroundPid)
        {
            if (_disposed) return (null, "", -1, -1, false);
            var target = ResolveTargetApp(foregroundPid);
            if (target == null) return (null, "", -1, -1, false);
            int pid = (int)target.ProcessId;
            // 已枚举出的进程名直接复用，缺失时才查询；标题仍按自定义名/进程名显示。
            string proc = target.ProcessName ?? ForegroundAppService.GetProcessNameSafe(pid) ?? "";
            int step = MathEx.Clamp(ConfigService.Load().VolumeStep, 1, 20);
            var result = SessionVolumeService.AdjustVolumePercent(pid, delta > 0 ? step : -step);
            if (result.Current < 0) return (target, proc, -1, -1, false);
            int actual = result.Actual < 0 ? result.Requested : result.Actual;
            bool muted = SessionVolumeService.IsMuted(pid);
            return (target, proc, result.Current, actual, muted);
        }

        /// <summary>
        /// 目标应用与快捷面板完全一致（CurrentAppService 同一套规则：recent/last/fixed → 前台 → 第一个）。
        /// 应用列表走 AudioService 的 1 秒缓存，避免每次滚轮全量枚举音频会话。
        /// </summary>
        private AudioAppInfo? ResolveTargetApp(int foregroundPid)
        {
            List<AudioAppInfo> apps;
            lock (_appsLock)
            {
                if (_cachedApps == null || (DateTime.UtcNow - _cachedAt).TotalMilliseconds > 1500)
                {
                    _cachedApps = AudioService.GetApps();
                    _cachedAt = DateTime.UtcNow;
                }
                apps = _cachedApps;
            }
            var cfg = ConfigService.Load();
            return CurrentAppService.Resolve(apps, cfg, foregroundPid);
        }

        // Shared OSD controller for Lite / Legacy.
        internal void ShowOsd(string app, string text) => _osdService.Show(app, text);
        internal void ShowVolumeOsd(string app, int volume, string text, bool muted = false, string? target = null)
            => _osdService.ShowVolume(app, volume, text, muted, target);
        internal void ShowMicMuteOsd(string app, bool muted) => _osdService.ShowMic(app, muted);
        internal void NotifyMicMuteOsdSettingChanged(bool on) => _ = _osdService.NotifyMicSettingChangedAsync(on);
        internal void NotifyOsdVolumeVisualChanged() => _osdService.NotifyVolumeVisualChanged();
        internal void BeginOsdAdjust() => _osdService.BeginAdjust();
        internal void CancelOsdAdjust() => _osdService.CancelAdjust();
        internal void PreviewOsd() => _osdService.Preview();
        internal void ApplyOsdSize(double w, double fs) => _osdService.ApplySize(w, fs);
        internal void SetOsdSize(int w, double fs) => _osdService.PreviewSize(w, fs);

        public void Dispose()
        {
            _disposed = true;
            System.Threading.Interlocked.Exchange(ref _pendingDelta, 0);
            _osdService.Dispose();
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}

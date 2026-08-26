using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SonicRoute.Core;
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

        private readonly LowLevelMouseProc _proc;
        private IntPtr _hook;
        private bool _disposed;

        // 滚轮合并派发（钩子线程只做检测，音量操作在 UI 线程执行）
        private int _pendingDelta;
        private bool _dispatchScheduled;

        // 目标应用解析缓存（与快捷面板一致，避免每次滚轮全量枚举）
        private readonly object _appsLock = new();
        private List<AudioAppInfo>? _cachedApps;
        private DateTime _cachedAt;

        // OSD
        private Window? _osd;
        private readonly DispatcherTimer _osdTimer;

        public TrayWheelService()
        {
            _proc = HookProc;
            _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
            _osdTimer.Tick += (_, _) => HideOsd();
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
                    // 钩子线程绝不做 COM/音频调用：合并滚轮量并派发到 UI 线程
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

        private void ProcessPendingWheel()
        {
            _dispatchScheduled = false;
            int d = System.Threading.Interlocked.Exchange(ref _pendingDelta, 0);
            if (d != 0) AdjustVolume(d);
        }

        private static bool IsOverTray(POINT pt)
        {
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

        private void AdjustVolume(int delta)
        {
            int pid = ResolveTargetPid();
            if (pid <= 0) { ShowOsd("—", "无音频会话"); return; }

            string? name = ForegroundAppService.GetProcessNameSafe(pid) ?? "应用";
            int cur = SessionVolumeService.GetVolumePercent(pid);
            if (cur < 0) { ShowOsd(name, "无法读取音量"); return; }
            int next = Math.Clamp(cur + (delta > 0 ? 4 : -4), 0, 100);
            SessionVolumeService.SetVolumePercent(pid, next);
            int actual = SessionVolumeService.GetVolumePercent(pid);
            if (actual < 0) actual = next;
            bool muted = SessionVolumeService.IsMuted(pid);
            ShowOsd(name, muted ? $"🔇 已静音 · {actual}%" : $"🔊 {actual}%");
        }

        /// <summary>
        /// 目标应用与快捷面板完全一致（CurrentAppService 同一套规则：recent/last/fixed → 前台 → 第一个）。
        /// 应用列表走 AudioService 的 1 秒缓存，避免每次滚轮全量枚举音频会话。
        /// </summary>
        private int ResolveTargetPid()
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
            var target = CurrentAppService.Resolve(apps, cfg);
            return target == null ? 0 : (int)target.ProcessId;
        }

        // ==================== OSD ====================

        /// <summary>右上角 OSD 提示（托盘滚轮/全局快捷键共用）。必须在 UI 线程调用。
        /// 外观跟随主题：背景/边框/文字用主题资源（含用户设置的透明度与强调色），换肤即生效。</summary>
        internal void ShowOsd(string app, string text)
        {
            try
            {
                if (_osd == null)
                {
                    _osd = new Window
                    {
                        WindowStyle = WindowStyle.None,
                        AllowsTransparency = true,
                        Background = System.Windows.Media.Brushes.Transparent,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        Topmost = true,
                        ResizeMode = ResizeMode.NoResize,
                        SizeToContent = SizeToContent.WidthAndHeight,
                        MaxWidth = 400,   // 限制通知最大宽度，避免超长文本撑爆/位置漂移
                        Focusable = false
                    };
                    var border = new Border
                    {
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(16, 10, 16, 10),
                        BorderThickness = new Thickness(1)
                    };
                    // 主题化：背景/边框/文字全部绑主题资源，透明度随 Theme.SurfaceBgAlpha
                    border.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceBgAlpha");
                    border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
                    border.Child = new StackPanel();
                    _osd.Content = border;
                }

                var root = (Border)_osd.Content;
                var stack = new StackPanel();
                var a = new TextBlock
                {
                    Text = app, FontSize = 12,
                    MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis
                };
                a.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
                var v = new TextBlock
                {
                    Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 3, 0, 0),
                    MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis
                };
                v.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Accent");
                stack.Children.Add(a);
                stack.Children.Add(v);
                root.Child = stack;

                if (!_osd.IsVisible)
                {
                    _osd.Show();
                    _osd.Topmost = true;
                }

                // 位置：右上角、工作区上沿。等布局完成后按实际宽度右对齐，防止文本变化导致位置不协调
                _osd.Dispatcher.BeginInvoke(new Action(RepositionOsd), DispatcherPriority.Background);

                _osdTimer.Stop();
                _osdTimer.Start();
            }
            catch
            {
                // OSD 失败不影响核心功能
            }
        }

        private void RepositionOsd()
        {
            try
            {
                if (_osd == null) return;
                var wa = SystemParameters.WorkArea;
                double w = Math.Min(_osd.ActualWidth > 0 ? _osd.ActualWidth : 360, 400);
                _osd.Left = wa.Right - w - 16;
                _osd.Top = wa.Top + 14;
            }
            catch { }
        }

        private void HideOsd()
        {
            try { _osd?.Hide(); }
            catch { }
        }

        public void Dispose()
        {
            _disposed = true;
            _osdTimer.Stop();
            try { _osd?.Close(); } catch { }
            _osd = null;
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}

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
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

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

        // OSD 调整模式（主题「调整 OSD」：拖动主体移动位置、拖右下角手柄调整大小，松手保存）
        private bool _osdAdjustMode;
        private bool _osdDragging;
        private System.Windows.Point _osdDragStart;
        /// <summary>拖拽松手已保存位置后触发（UI 线程），用于设置页复位按钮状态与同步输入框。</summary>
        internal event Action? OsdAdjustFinished;
        private TextBlock? _osdAppText;
        private TextBlock? _osdValueText;
        private Border? _osdHandle;
        private double _osdWidth = 240;       // 当前 OSD 逻辑宽度（横向自由拉长缩短）
        private double _osdFontScale = 1.0;   // 当前字号倍率（纵向调整）
        private bool _osdResizing;
        private System.Windows.Point _osdResizeStart;
        private double _osdResizeBaseWidth;
        private double _osdResizeBaseFont;

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

            // OSD 应用名优先显示用户自定义名称（与快捷键/面板/通知一致），
            // 未设置自定义名则显示进程名本身，两者都拿不到才显示"应用"
            string proc = ForegroundAppService.GetProcessNameSafe(pid) ?? "";
            string name = AppDisplayName.Get(proc, string.IsNullOrWhiteSpace(proc) ? "应用" : proc);
            int cur = SessionVolumeService.GetVolumePercent(pid);
            if (cur < 0) { ShowOsd(name, "无法读取音量"); return; }
            int step = Math.Clamp(ConfigService.Load().VolumeStep, 1, 20);
            int next = Math.Clamp(cur + (delta > 0 ? step : -step), 0, 100);
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
                    var _ocfg = ConfigService.Load();
                    _osdWidth = Math.Clamp(_ocfg.OsdWidth, 180, 600);
                    _osdFontScale = Math.Clamp(_ocfg.OsdFontScale, 0.7, 2.0);
                    _osd = new Window
                    {
                        WindowStyle = WindowStyle.None,
                        AllowsTransparency = true,
                        Background = System.Windows.Media.Brushes.Transparent,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        Topmost = true,
                        ResizeMode = ResizeMode.NoResize,
                        SizeToContent = SizeToContent.Height,
                        Width = _osdWidth,   // 固定宽度（自由拉长缩短）：文本长短不影响窗口尺寸 → 位置稳定不闪烁
                        Focusable = false
                    };
                    var border = new Border
                    {
                        CornerRadius = new CornerRadius(10 * _osdFontScale),
                        Padding = new Thickness(16 * _osdFontScale, 10 * _osdFontScale, 16 * _osdFontScale, 10 * _osdFontScale),
                        BorderThickness = new Thickness(1)
                    };
                    // 主题化：背景/边框/文字全部绑主题资源，透明度随 Theme.SurfaceBgAlpha
                    border.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceBgAlpha");
                    border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
                    border.Child = new Grid();
                    _osd.Content = border;

                    // 调整模式拖动：按住左键移动窗口，松手保存位置（仅调整模式生效）
                    _osd.MouseLeftButtonDown += (_, e) =>
                    {
                        if (!_osdAdjustMode) return;
                        _osdDragging = true;
                        _osdDragStart = e.GetPosition(null);
                        _osd.CaptureMouse();
                        e.Handled = true;
                    };
                    _osd.MouseMove += (_, e) =>
                    {
                        if (!_osdDragging) return;
                        var p = e.GetPosition(null);
                        // 拖动中限制在工作区内，防止拖出屏外
                        double ws = 1.0; try { ws = System.Windows.Media.VisualTreeHelper.GetDpi(_osd).DpiScaleX; } catch { }
                        var swa = SystemParameters.WorkArea;
                        _osd.Left = Math.Clamp(_osd.Left + (p.X - _osdDragStart.X), swa.Left / ws, Math.Max(swa.Left / ws, swa.Right / ws - _osd.ActualWidth));
                        _osd.Top = Math.Clamp(_osd.Top + (p.Y - _osdDragStart.Y), swa.Top / ws, Math.Max(swa.Top / ws, swa.Bottom / ws - _osd.ActualHeight));
                        _osd.Top += p.Y - _osdDragStart.Y;
                        e.Handled = true;
                    };
                    _osd.MouseLeftButtonUp += (_, e) =>
                    {
                        if (!_osdDragging) return;
                        _osdDragging = false;
                        _osd.ReleaseMouseCapture();
                        e.Handled = true;
                        if (_osdAdjustMode) SaveOsdDragPosition();
                    };
                }

                var root = (Border)_osd.Content;
                var grid = root.Child as Grid ?? new Grid();
                grid.Children.Clear();
                var stack = new StackPanel();
                var a = new TextBlock
                {
                    Text = app, FontSize = 12 * _osdFontScale,
                    MaxWidth = _osdWidth - 32 * _osdFontScale, TextTrimming = TextTrimming.CharacterEllipsis
                };
                a.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
                var v = new TextBlock
                {
                    Text = text, FontSize = 18 * _osdFontScale, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 3, 0, 0),
                    MaxWidth = _osdWidth - 32 * _osdFontScale, TextTrimming = TextTrimming.CharacterEllipsis
                };
                v.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Accent");
                stack.Children.Add(a);
                stack.Children.Add(v);
                _osdAppText = a;
                _osdValueText = v;
                grid.Children.Add(stack);
                // 缩放手柄：右下角三线 grip，仅调整模式显示；横向拖动自由拉长缩短宽度、纵向调整字号
                var grip = new System.Windows.Shapes.Path
                {
                    Data = System.Windows.Media.Geometry.Parse("M 3,17 L 17,3 M 8,17 L 17,8 M 13,17 L 17,13"),
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                grip.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Theme.Accent");
                var handle = new Border
                {
                    Width = 20 * _osdFontScale, Height = 20 * _osdFontScale,
                    CornerRadius = new CornerRadius(5 * _osdFontScale),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0, 0, 0)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                    Cursor = System.Windows.Input.Cursors.SizeNWSE,
                    Visibility = _osdAdjustMode ? Visibility.Visible : Visibility.Collapsed,
                    Child = grip
                };
                handle.MouseLeftButtonDown += (_, e) =>
                {
                    if (!_osdAdjustMode) return;
                    _osdResizing = true;
                    _osdResizeStart = e.GetPosition(null);
                    _osdResizeBaseWidth = _osdWidth;
                    _osdResizeBaseFont = _osdFontScale;
                    handle.CaptureMouse();
                    e.Handled = true;
                };
                handle.MouseMove += (_, e) =>
                {
                    if (!_osdResizing) return;
                    var p = e.GetPosition(null);
                    ApplyOsdSize(_osdResizeBaseWidth + (p.X - _osdResizeStart.X), _osdResizeBaseFont + (p.Y - _osdResizeStart.Y) / 300.0);
                    e.Handled = true;
                };
                handle.MouseLeftButtonUp += (_, e) =>
                {
                    if (!_osdResizing) return;
                    _osdResizing = false;
                    handle.ReleaseMouseCapture();
                    e.Handled = true;
                    if (_osdAdjustMode) SaveOsdSize();
                };
                _osdHandle = handle;
                grid.Children.Add(handle);
                root.Child = grid;

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
            }
        }

        private void RepositionOsd()
        {
            try
            {
                if (_osd == null) return;
                // 用窗口自身实际 DPI 缩放（VisualTreeHelper 对窗口返回真实值，比 GetDpiForSystem/GetDpiForWindow 可靠）
                double winScale = 1.0;
                try { winScale = System.Windows.Media.VisualTreeHelper.GetDpi(_osd).DpiScaleX; } catch { }
                if (winScale <= 0) winScale = 1.0;
                // 直接换算：SystemParameters.WorkArea 当前环境为物理像素，按窗口 DPI 缩放换算为逻辑单位
                var _swa = SystemParameters.WorkArea;
                var wa = new RECT
                {
                    Left = (int)Math.Round(_swa.Left / winScale),
                    Top = (int)Math.Round(_swa.Top / winScale),
                    Right = (int)Math.Round(_swa.Right / winScale),
                    Bottom = (int)Math.Round(_swa.Bottom / winScale)
                };
                double w = _osd.ActualWidth > 0 ? _osd.ActualWidth : _osdWidth;
                double h = _osd.ActualHeight > 0 ? _osd.ActualHeight : 80;

                // 实验设置 - 自由调整：9 宫格位置 + X/Y 偏移（默认右上角 TR）；"Custom" 用自定义坐标直接定位
                var cfg = ConfigService.Load();
                string pos = string.IsNullOrWhiteSpace(cfg.OsdPosition) ? "TR" : cfg.OsdPosition;
                double ox = cfg.OsdOffsetX;
                double oy = cfg.OsdOffsetY;

                // 自定义模式：直接使用用户输入的屏幕坐标（未设置时回退右上角）
                if (string.Equals(pos, "Custom", StringComparison.OrdinalIgnoreCase))
                {
                    if (cfg.OsdCustomX >= 0 && cfg.OsdCustomY >= 0)
                    {
                        _osd.Left = cfg.OsdCustomX;
                        _osd.Top = cfg.OsdCustomY;
                        return;
                    }
                    pos = "TR";
                }

                double left, top;
                switch (pos)
                {
                    case "TL": left = wa.Left + 16; top = wa.Top + 14; break;
                    case "T": left = wa.Left + (wa.Right - wa.Left - w) / 2; top = wa.Top + 14; break;
                    case "L": left = wa.Left + 16; top = wa.Top + (wa.Bottom - wa.Top - h) / 2; break;
                    case "C": left = wa.Left + (wa.Right - wa.Left - w) / 2; top = wa.Top + (wa.Bottom - wa.Top - h) / 2; break;
                    case "R": left = wa.Right - w - 16; top = wa.Top + (wa.Bottom - wa.Top - h) / 2; break;
                    case "BL": left = wa.Left + 16; top = wa.Bottom - h - 14; break;
                    case "B": left = wa.Left + (wa.Right - wa.Left - w) / 2; top = wa.Bottom - h - 14; break;
                    case "BR": left = wa.Right - w - 16; top = wa.Bottom - h - 14; break;
                    default: left = wa.Right - w - 16; top = wa.Top + 14; break; // TR 右上角
                }
                // 限制在光标所在屏工作区内（防跨屏残留/边缘闪烁）
                double waL = wa.Left, waT = wa.Top, waR = wa.Right, waB = wa.Bottom;
                left = Math.Clamp(left + ox, waL, Math.Max(waL, waR - w - 4));
                top = Math.Clamp(top + oy, waT, Math.Max(waT, waB - h - 4));
                _osd.Left = left;
                _osd.Top = top;
            }
            catch { }
        }

        private void HideOsd()
        {
            try
            {
                if (_osd != null)
                {
                    _osd.Hide();
                    // A2：隐藏时释放内容树（Border 下 TextBlock 等），下次显示再重建，避免常驻 UI 对象
                    if (_osd.Content is Border b) b.Child = null;
                _osdAppText = null;
                _osdValueText = null;
                _osdHandle = null;
                }
            }
            catch { }
        }

        /// <summary>进入 OSD 调整模式：显示常驻可拖动 OSD（不自动消失），拖动松手即保存位置。</summary>
        internal void BeginOsdAdjust()
        {
            try
            {
                _osdAdjustMode = true;
                ShowOsd("📍", L10n.T("Exp.OsdDragHint"));
                _osdTimer.Stop(); // 调整模式常驻，不自动隐藏
            }
            catch { }
        }

        /// <summary>退出 OSD 调整模式（不保存当前拖动位置，隐藏 OSD）。</summary>
        internal void CancelOsdAdjust()
        {
            _osdAdjustMode = false;
            _osdDragging = false;
            _osdTimer.Stop();
            HideOsd();
        }

        /// <summary>实时位置预览：按当前配置立即显示 OSD（用于偏移滑块/坐标输入联动）。</summary>
        internal void PreviewOsd()
        {
            if (_osdAdjustMode) return;
            ShowOsd("📍", L10n.T("Exp.OsdPreview"));
        }

        /// <summary>拖动松手：把当前窗口位置写入配置（Custom 模式），保存并通知设置页。</summary>
        private void SaveOsdDragPosition()
        {
            try
            {
                if (_osd == null) return;
                var cfg = ConfigService.Load();
                cfg.OsdPosition = "Custom";
                // 保存前限制在工作区内（防止拖出屏外后保存无效坐标）
                double ws = 1.0; try { ws = System.Windows.Media.VisualTreeHelper.GetDpi(_osd).DpiScaleX; } catch { }
                var swa = SystemParameters.WorkArea;
                cfg.OsdCustomX = (int)Math.Clamp(_osd.Left, swa.Left / ws, Math.Max(swa.Left / ws, swa.Right / ws - _osd.ActualWidth));
                cfg.OsdCustomY = (int)Math.Clamp(_osd.Top, swa.Top / ws, Math.Max(swa.Top / ws, swa.Bottom / ws - _osd.ActualHeight));
                cfg.OsdCustomY = (int)_osd.Top;
                ConfigService.Save(cfg);
                _osdAdjustMode = false;
                _osdTimer.Stop();
                ShowOsd("📍", L10n.T("Exp.OsdSaved"));
                OsdAdjustFinished?.Invoke();
            }
            catch { }
        }

        /// <summary>应用 OSD 尺寸（宽度自由拉长缩短 + 字号倍率）到窗口与内容（调整模式拖手柄时实时调用）。</summary>
        private void ApplyOsdSize(double w, double fs)
        {
            try
            {
                if (_osd == null) return;
                _osdWidth = Math.Clamp(w, 180, 600);
                _osdFontScale = Math.Clamp(fs, 0.7, 2.0);
                _osd.Width = _osdWidth;
                if (_osd.Content is Border b)
                {
                    b.Padding = new Thickness(16 * _osdFontScale, 10 * _osdFontScale, 16 * _osdFontScale, 10 * _osdFontScale);
                    b.CornerRadius = new CornerRadius(10 * _osdFontScale);
                }
                if (_osdAppText != null) { _osdAppText.FontSize = 12 * _osdFontScale; _osdAppText.MaxWidth = _osdWidth - 32 * _osdFontScale; }
                if (_osdValueText != null) { _osdValueText.FontSize = 18 * _osdFontScale; _osdValueText.MaxWidth = _osdWidth - 32 * _osdFontScale; }
                if (_osdHandle != null) { _osdHandle.Width = 20 * _osdFontScale; _osdHandle.Height = 20 * _osdFontScale; _osdHandle.CornerRadius = new CornerRadius(5 * _osdFontScale); }
            }
            catch { }
        }

        /// <summary>拖缩放手柄松手：保存宽度与字号倍率并退出调整模式。</summary>
        private void SaveOsdSize()
        {
            try
            {
                if (_osd == null) return;
                var cfg = ConfigService.Load();
                cfg.OsdWidth = (int)Math.Clamp(_osdWidth, 180, 600);
                cfg.OsdFontScale = Math.Clamp(_osdFontScale, 0.7, 2.0);
                ConfigService.Save(cfg);
                _osdAdjustMode = false;
                _osdTimer.Stop();
                ShowOsd("📍", L10n.T("Exp.OsdScaleSaved"));
                OsdAdjustFinished?.Invoke();
            }
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

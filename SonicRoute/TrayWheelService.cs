using System;
using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Core.Compat;
using SonicRoute.Core.Models;

namespace SonicRoute
{
    /// <summary>
    /// 复用 WH_MOUSE_LL：在配置区域内滚轮调当前应用音量，纯Alt滚轮切换其输出设备。
    /// Shift滚轮调系统默认播放音量，Alt＋Shift滚轮切换系统默认播放设备；共用原派发通道。
    /// </summary>
    internal sealed class TrayWheelService : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

        [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
        private static extern uint GetClassLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

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
        private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        // 只缓存成员元数据；窗口句柄和图标 ID 每次读取，保留句柄重建后的恢复能力。
        private static readonly System.Reflection.FieldInfo? TrayWindowField =
            typeof(System.Windows.Forms.NotifyIcon).GetField("_window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? typeof(System.Windows.Forms.NotifyIcon).GetField("window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.FieldInfo? TrayIdField =
            typeof(System.Windows.Forms.NotifyIcon).GetField("_id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? typeof(System.Windows.Forms.NotifyIcon).GetField("id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private static readonly int TrayIdentifierSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>();

        private readonly System.Windows.Forms.NotifyIcon? _trayIcon;

        private MouseHotkeyHook? _mouseHook;
        internal bool IsEnabled => _mouseHook?.TrayWheel == this;
        private volatile bool _disposed;
        private int _wheelGeneration;

        // 滚轮合并派发（钩子只做检测；单个后台任务操作音频，结果返回 UI 显示 OSD）
        private int _pendingDelta;
        private int _pendingDeviceDelta;
        private int _pendingSystemDeviceDelta;
        private int _pendingSystemVolumeDelta;
        private bool _dispatchScheduled;

        // 目标应用解析缓存（与快速面板一致，避免每次滚轮全量枚举）

        private readonly OsdService _osdService;
        internal event Action? OsdAdjustFinished
        {
            add => _osdService.AdjustFinished += value;
            remove => _osdService.AdjustFinished -= value;
        }
        internal long MicStateVersion => _osdService.MicStateVersion;

        public TrayWheelService(System.Windows.Forms.NotifyIcon? trayIcon)
        {
            _trayIcon = trayIcon;
            _osdService = new OsdService();
        }

        /// <summary>必须在 UI 线程调用（钩子回调将运行在安装线程的消息循环上）。</summary>
        public void Start(MouseHotkeyHook mouseHook)
        {
            _mouseHook = mouseHook;
            TrySetPaused(ConfigService.Load().TrayWheelPaused);
        }

        internal bool TrySetPaused(bool paused)
        {
            if (_disposed || _mouseHook == null) return false;
            if (!_mouseHook.TrySetTrayWheel(paused ? null : this, out int error)) return HookError(error);
            if (paused) DiscardPendingWheel();
            return true;
        }

        private static bool HookError(int error)
        {
            ExceptionDiagnostics.Record("TrayWheel.Hook", new System.ComponentModel.Win32Exception(error));
            return false;
        }

        internal bool HitTest(int x, int y) =>
            !_disposed && !ConfigService.Load().TrayWheelPaused && IsOverTray(new POINT { x = x, y = y });

        internal void QueueWheel(int delta, uint mods)
        {
            // 输入时捕获修饰键；音频操作和 OSD 仍通过原来的合并派发处理。
            if (mods == 1) // MOD_ALT
                System.Threading.Interlocked.Add(ref _pendingDeviceDelta, delta);
            else if (mods == 5) // MOD_ALT | MOD_SHIFT
                System.Threading.Interlocked.Add(ref _pendingSystemDeviceDelta, delta);
            else if (mods == 4) // MOD_SHIFT
                System.Threading.Interlocked.Add(ref _pendingSystemVolumeDelta, delta);
            else System.Threading.Interlocked.Add(ref _pendingDelta, delta);
            if (!_dispatchScheduled)
            {
                _dispatchScheduled = true;
                var app = (App)System.Windows.Application.Current;
                app.Dispatcher.BeginInvoke(new Action(ProcessPendingWheel));
            }
        }

        internal void DiscardPendingWheel()
        {
            System.Threading.Interlocked.Increment(ref _wheelGeneration);
            System.Threading.Interlocked.Exchange(ref _pendingDelta, 0);
            System.Threading.Interlocked.Exchange(ref _pendingDeviceDelta, 0);
            System.Threading.Interlocked.Exchange(ref _pendingSystemDeviceDelta, 0);
            System.Threading.Interlocked.Exchange(ref _pendingSystemVolumeDelta, 0);
        }

        private bool CanApplyWheel(int generation) =>
            !_disposed && System.Threading.Volatile.Read(ref _wheelGeneration) == generation;

        private async void ProcessPendingWheel()
        {
            try
            {
                while (!_disposed)
                {
                    if (ConfigService.Load().TrayWheelPaused) { DiscardPendingWheel(); break; }
                    int generation = System.Threading.Volatile.Read(ref _wheelGeneration);
                    int delta = System.Threading.Interlocked.Exchange(ref _pendingDelta, 0);
                    int deviceDelta = System.Threading.Interlocked.Exchange(ref _pendingDeviceDelta, 0);
                    int systemDeviceDelta = System.Threading.Interlocked.Exchange(ref _pendingSystemDeviceDelta, 0);
                    int systemVolumeDelta = System.Threading.Interlocked.Exchange(ref _pendingSystemVolumeDelta, 0);
                    if (delta == 0 && deviceDelta == 0 && systemDeviceDelta == 0 && systemVolumeDelta == 0) break;
                    // 记录此次输入时的前台，后台查询期间焦点变化不改本轮目标。
                    int foregroundPid = delta != 0 || deviceDelta != 0 ? ForegroundAppService.GetForegroundProcessId() : 0;
                    if (deviceDelta != 0) await SwitchOutputDeviceAsync(deviceDelta, foregroundPid, false, generation);
                    if (!CanApplyWheel(generation)) continue;
                    if (systemDeviceDelta != 0) await SwitchOutputDeviceAsync(systemDeviceDelta, foregroundPid, true, generation);
                    if (!CanApplyWheel(generation)) continue;
                    if (systemVolumeDelta != 0)
                        await ((App)System.Windows.Application.Current).AdjustDefaultOutputVolumeAsync(systemVolumeDelta > 0);
                    if (delta == 0 || !CanApplyWheel(generation)) continue;
                    var result = await Task.Run(() => AdjustVolume(delta, foregroundPid, generation));
                    if (!CanApplyWheel(generation)) continue;
                    if (result.App == null)
                    {
                        ShowOsd(L10n.T("Ov.VolumeTitle"), L10n.T("Ov.NoSession"));
                        continue;
                    }
                    string name = AppDisplayName.Get(result.App);
                    if (result.Current < 0) { ShowOsd(name, L10n.T("Ov.VolReadFail")); continue; }
                    ShowVolumeOsd(name, result.Actual,
                        result.Muted ? string.Format(L10n.T("Ov.MutedVol"), result.Actual) : $"🔊 {result.Actual}%",
                        result.Muted, result.App.Identity);
                }
            }
            catch { /* 设备/会话退出时不让异步滚轮任务打断消息循环。 */ }
            finally
            {
                _dispatchScheduled = false;
                if (!_disposed && (System.Threading.Volatile.Read(ref _pendingDelta) != 0
                    || System.Threading.Volatile.Read(ref _pendingDeviceDelta) != 0
                    || System.Threading.Volatile.Read(ref _pendingSystemDeviceDelta) != 0
                    || System.Threading.Volatile.Read(ref _pendingSystemVolumeDelta) != 0))
                {
                    _dispatchScheduled = true;
                    _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(ProcessPendingWheel));
                }
            }
        }

        // HWND values can be reused. Verify the class, owner and current parent
        // before trusting a cached window; only geometry from this input is used.
        private const int GCW_ATOM = -32;
        private const uint GA_ROOT = 2;
        private struct TrayWindow
        {
            internal IntPtr Handle;
            internal uint Atom;
            internal uint Process;
            private uint _thread;

            internal IntPtr Resolve(IntPtr parent, string name)
            {
                if (Handle != IntPtr.Zero && GetWindowThreadProcessId(Handle, out uint process) == _thread
                    && process == Process && GetClassLong(Handle, GCW_ATOM) == Atom
                    && (parent == IntPtr.Zero ? GetAncestor(Handle, GA_ROOT) == Handle : GetParent(Handle) == parent))
                    return Handle;
                IntPtr found = parent == IntPtr.Zero ? FindWindow(name, null) : FindWindowEx(parent, IntPtr.Zero, name, null);
                Handle = found;
                if (found != IntPtr.Zero)
                {
                    Atom = GetClassLong(found, GCW_ATOM);
                    _thread = GetWindowThreadProcessId(found, out Process);
                    if (Atom == 0 || _thread == 0) Handle = IntPtr.Zero;
                }
                return found; // A fresh class lookup is usable even if its identity cannot be cached yet.
            }
        }
        private TrayWindow _primaryTray, _primaryNotify, _overflow, _secondaryTray, _secondaryNotify, _otherNotify;

        private static bool RegionContains(ref TrayWindow region, IntPtr parent, string name, POINT pt)
        {
            IntPtr hwnd = region.Resolve(parent, name);
            if (hwnd == IntPtr.Zero) return false;
            if (!GetWindowRect(hwnd, out var rect))
            {
                // Explorer can replace the window between lookup and rectangle read.
                region = default;
                hwnd = region.Resolve(parent, name);
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out rect)) return false;
            }
            return PtInRect(ref rect, pt);
        }

        /// <summary>整片通知区（含溢出区和副任务栏）或仅本程序实时图标矩形。</summary>
        private bool IsOverTray(POINT pt)
        {
            try
            {
                if (!ConfigService.Load().TrayWheelEverywhere)
                    return IsOverOurIcon(pt);
            }
            catch { }

            IntPtr primary = _primaryTray.Resolve(IntPtr.Zero, "Shell_TrayWnd");
            if (primary != IntPtr.Zero && (RegionContains(ref _primaryNotify, primary, "TrayNotifyWnd", pt)
                || RegionContains(ref _overflow, primary, "NotifyContainerOverflowWindow", pt))) return true;

            IntPtr secondary = _secondaryTray.Resolve(IntPtr.Zero, "Shell_SecondaryTrayWnd");
            if (secondary == IntPtr.Zero) return false;
            if (RegionContains(ref _secondaryNotify, secondary, "TrayNotifyWnd", pt)) return true;

            // More than one secondary taskbar can share a class. Query the one
            // under this input without enumerating all display windows.
            IntPtr root = GetAncestor(WindowFromPoint(pt), GA_ROOT);
            return root != IntPtr.Zero && root != secondary && _secondaryTray.Handle != IntPtr.Zero
                && GetClassLong(root, GCW_ATOM) == _secondaryTray.Atom
                && GetWindowThreadProcessId(root, out uint process) != 0 && process == _secondaryTray.Process
                && RegionContains(ref _otherNotify, root, "TrayNotifyWnd", pt);
        }

        /// <summary>仅音跃图标模式：读取当前图标矩形；Shell 暂时失败时复用同一图标最近30秒的成功结果。</summary>
        private bool IsOverOurIcon(POINT pt)
        {
            try
            {
                if (!TryGetTrayIconIdentity(out var hwnd, out int id)) return false;
                if (_cachedIconHwnd != hwnd || _cachedIconId != id)
                {
                    _cachedIconRect = default;
                    _cachedIconHwnd = hwnd;
                    _cachedIconId = id;
                }

                // 官方返回值为 HRESULT：S_OK == 0，不是 Win32 BOOL。
                // 每次输入仍取实时位置，图标移动、DPI/任务栏变化无需等待缓存过期。
                var nii = new NOTIFYICONIDENTIFIER { cbSize = TrayIdentifierSize, hWnd = hwnd, uID = id };
                int hr = Shell_NotifyIconGetRect(ref nii, out var r);
                if (hr >= 0 && r.Right > r.Left && r.Bottom > r.Top)
                {
                    _cachedIconRect = r;
                    _cachedIconRectAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    var ir = InflateRect(r, 4);
                    return PtInRect(ref ir, pt);
                }

                // 沿用短期失败恢复，使用单调时钟；不增加轮询或 Explorer 同步消息。
                long age = System.Diagnostics.Stopwatch.GetTimestamp() - _cachedIconRectAt;
                if (age >= 0 && age < 30L * System.Diagnostics.Stopwatch.Frequency
                    && _cachedIconRect.Right > _cachedIconRect.Left
                    && _cachedIconRect.Bottom > _cachedIconRect.Top)
                {
                    var ir2 = InflateRect(_cachedIconRect, 4);
                    return PtInRect(ref ir2, pt);
                }
            }
            catch { }
            return false;
        }

        // 仅在当前图标 API 暂时失败时使用；禁止跨句柄/图标 ID 复用。
        private RECT _cachedIconRect;
        private long _cachedIconRectAt;
        private IntPtr _cachedIconHwnd;
        private int _cachedIconId;

        /// <summary>矩形外扩 n 像素（物理像素坐标，减少图标边缘 1-2px 漏判）。</summary>
        private static RECT InflateRect(RECT r, int n)
        {
            r.Left -= n; r.Top -= n; r.Right += n; r.Bottom += n;
            return r;
        }

        private bool TryGetTrayIconIdentity(out IntPtr hwnd, out int id)
        {
            hwnd = IntPtr.Zero;
            id = 0;
            if (_trayIcon == null || TrayIdField == null
                || TrayWindowField?.GetValue(_trayIcon) is not System.Windows.Forms.NativeWindow window)
                return false;
            hwnd = window.Handle;
            if (hwnd == IntPtr.Zero || TrayIdField.GetValue(_trayIcon) is not { } value) return false;
            id = unchecked((int)(value is IntPtr pointer ? pointer.ToInt64() : Convert.ToInt64(value)));
            return true;
        }

        private async Task SwitchOutputDeviceAsync(int delta, int foregroundPid, bool systemDefault, int generation)
        {
            var switched = await Task.Run(() => SwitchOutputDevice(delta, foregroundPid, systemDefault, generation));
            if (!CanApplyWheel(generation)) return;
            if (!systemDefault && switched.App == null) ShowOsd(L10n.T("Ov.VolumeTitle"), L10n.T("Ov.NoSession"));
            else ShowOsd(systemDefault ? L10n.T("Act.SetDefaultOutput") : AppDisplayName.Get(switched.App!), string.IsNullOrEmpty(switched.DeviceName)
                ? L10n.T("Ov.NoDevice") : $"🔊 {switched.DeviceName}");
        }

        private (AudioAppInfo? App, string? DeviceName) SwitchOutputDevice(int delta, int foregroundPid, bool systemDefault, int generation)
        {
            if (!CanApplyWheel(generation)) return (null, null);
            int steps = Math.Max(1, (int)(Math.Abs((long)delta) / 120));
            if (systemDefault)
                return (null, App.CycleSystemDefaultDevice(Core.Interop.EDataFlow.eRender, delta > 0, steps));
            var target = ResolveTargetApp(foregroundPid);
            if (target == null || !CanApplyWheel(generation)) return (null, null);
            return (target, App.CycleDevice(target, Core.Interop.EDataFlow.eRender, delta > 0, steps));
        }

        private (AudioAppInfo? App, string ProcessName, int Current, int Actual, bool Muted) AdjustVolume(int delta, int foregroundPid, int generation)
        {
            if (!CanApplyWheel(generation)) return (null, "", -1, -1, false);
            var target = ResolveTargetApp(foregroundPid);
            if (target == null) return (null, "", -1, -1, false);
            int pid = (int)target.ProcessId;
            // 已枚举出的进程名直接复用，缺失时才查询；标题仍按自定义名/进程名显示。
            string proc = target.ProcessName ?? ForegroundAppService.GetProcessNameSafe(pid) ?? "";
            int step = MathEx.Clamp(ConfigService.Load().VolumeStep, 1, 20);
            if (!CanApplyWheel(generation)) return (null, "", -1, -1, false);
            var result = ApplicationAudio.AdjustVolumePercent(target, delta > 0 ? step : -step);
            if (result.Current < 0) return (target, proc, -1, -1, false);
            int actual = result.Actual < 0 ? result.Requested : result.Actual;
            bool muted = ApplicationAudio.IsMuted(target);
            return (target, proc, result.Current, actual, muted);
        }

        /// <summary>
        /// 目标应用与快速面板完全一致（CurrentAppService 同一套规则：recent/last/fixed → 前台 → 第一个）。
        /// 应用列表走 AudioService 的 1 秒缓存，避免每次滚轮全量枚举音频会话。
        /// </summary>
        private AudioAppInfo? ResolveTargetApp(int foregroundPid)
        {
            var apps = ApplicationAudio.GetApps(); // Reuse the shared cache and its notification generation.
            var cfg = ConfigService.Load();
            return CurrentAppService.Resolve(apps, cfg, foregroundPid);
        }

        // Shared OSD controller for Lite / Legacy.
        internal void ShowOsd(string app, string text) => _osdService.Show(app, text);
        internal Task FinishTemporaryOsdAsync() => _osdService.FinishTemporaryNoticeAsync();
        internal void ShowVolumeOsd(string app, int volume, string text, bool muted = false, string? target = null)
            => _osdService.ShowVolume(app, volume, text, muted, target);
        internal void ShowMicMuteOsd(string app, bool muted) => _osdService.ShowMic(app, muted);
        internal void NotifyMicMuteOsdSettingChanged() => _ = _osdService.NotifyMicSettingChangedAsync();
        internal void NotifyOsdVolumeVisualChanged() => _osdService.NotifyVolumeVisualChanged();
        internal void BeginOsdAdjust() => _osdService.BeginAdjust();
        internal void CancelOsdAdjust() => _osdService.CancelAdjust();
        internal void PreviewOsd() => _osdService.Preview();
        internal void ApplyOsdSize(double w, double fs) => _osdService.ApplySize(w, fs);
        internal void SetOsdSize(int w, double fs) => _osdService.PreviewSize(w, fs);
        internal void ReleaseIdleOsd() => _osdService.ReleaseWindowIfIdle();

        public void Dispose()
        {
            _disposed = true;
            DiscardPendingWheel();
            _osdService.Dispose();
            if (IsEnabled) _mouseHook!.TrySetTrayWheel(null, out _);
            _mouseHook = null;
        }
    }
}

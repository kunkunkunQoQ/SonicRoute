using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SonicRoute.Core;
using SonicRoute.Core.Compat;

namespace SonicRoute
{
    /// <summary>OSD 的原生屏幕坐标只在此处处理；不改变 Lite / Legacy 的窗口 DPI 感知方式。</summary>
    internal static class OsdPlacement
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor, Work;
            public uint Flags;
        }

        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPhysicalCursorPos(out NativePoint point);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);

        // 原生调用统一物理坐标；句柄在进入此作用域前创建，避免改变窗口自己的 DPI 模式。
        private readonly struct NativeCoordinates : IDisposable
        {
            private readonly IntPtr _previous;
            public NativeCoordinates(bool enter)
            {
                _previous = IntPtr.Zero;
                if (enter)
                {
                    try
                    {
                        _previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
                        if (_previous == IntPtr.Zero)
                            _previous = SetThreadDpiAwarenessContext(new IntPtr(-3));
                    }
                    catch (EntryPointNotFoundException) { } // 早期 Windows 10 使用原上下文。
                }
            }
            public void Dispose()
            {
                if (_previous != IntPtr.Zero) SetThreadDpiAwarenessContext(_previous);
            }
        }

        internal static bool TryDragStart(Window window, out NativeRect rect, out NativePoint cursor)
        {
            var handle = new WindowInteropHelper(window).Handle;
            using var coordinates = new NativeCoordinates(true);
            cursor = default;
            return GetWindowRect(handle, out rect) && GetPhysicalCursorPos(out cursor);
        }

        internal static bool TryWindowRect(Window window, out NativeRect rect)
        {
            var handle = new WindowInteropHelper(window).Handle;
            using var coordinates = new NativeCoordinates(true);
            return GetWindowRect(handle, out rect);
        }

        internal static void Drag(Window window, NativeRect start, NativePoint cursorStart)
        {
            var handle = new WindowInteropHelper(window).Handle;
            using var coordinates = new NativeCoordinates(true);
            if (!GetPhysicalCursorPos(out var cursor) || !GetWindowRect(handle, out var current)) return;
            var work = WorkArea(MonitorFromPoint(cursor, 2));
            Move(handle, start.Left + cursor.X - cursorStart.X, start.Top + cursor.Y - cursorStart.Y,
                current.Right - current.Left, current.Bottom - current.Top, work);
        }

        internal static void Apply(Window window, AppConfig config)
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            bool custom = string.Equals(config.OsdPosition, "Custom", StringComparison.OrdinalIgnoreCase);
            if (custom && (!config.OsdCustomPhysicalX.HasValue || !config.OsdCustomPhysicalY.HasValue))
            {
                // 旧坐标已经是 WPF DIP；保留原解释，不再做物理像素除法。
                window.Left = config.OsdCustomX;
                window.Top = config.OsdCustomY;
            }
            using var coordinates = new NativeCoordinates(true);
            if (!GetWindowRect(handle, out var current)) return;
            int width = current.Right - current.Left, height = current.Bottom - current.Top;
            if (width <= 0 || height <= 0) return;
            int x = current.Left, y = current.Top;
            IntPtr monitor = IntPtr.Zero;
            if (custom)
            {
                x = config.OsdCustomPhysicalX ?? current.Left;
                y = config.OsdCustomPhysicalY ?? current.Top;
                var desired = new NativeRect { Left = x, Top = y, Right = AddSize(x, width), Bottom = AddSize(y, height) };
                monitor = MonitorFromRect(ref desired, 0);
            }
            if (!custom || monitor == IntPtr.Zero)
            {
                // 已保存的屏幕移除时回到主屏右上角，而非留在屏幕外。
                monitor = MonitorFromPoint(default, 1);
                var primary = WorkArea(monitor);
                double scale = WindowScale(window, handle);
                x = Round(primary.Right - width - 16 * scale + config.OsdOffsetX * scale);
                y = Round(primary.Top + 14 * scale + config.OsdOffsetY * scale);
            }
            Move(handle, x, y, width, height, WorkArea(monitor));
        }

        internal static void Save(Window window, AppConfig config)
        {
            var handle = new WindowInteropHelper(window).Handle;
            using (var coordinates = new NativeCoordinates(true))
            {
                if (!GetWindowRect(handle, out var rect)) return;
                config.OsdCustomPhysicalX = rect.Left;
                config.OsdCustomPhysicalY = rect.Top;
            }
            config.OsdPosition = "Custom";
            double scale = WindowScale(window, handle);
            config.OsdCustomX = Round(double.IsNaN(window.Left) ? config.OsdCustomPhysicalX!.Value / scale : window.Left);
            config.OsdCustomY = Round(double.IsNaN(window.Top) ? config.OsdCustomPhysicalY!.Value / scale : window.Top);
            ConfigService.Save(config);
        }

        private static NativeRect WorkArea(IntPtr monitor)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)) return info.Work;
            throw new InvalidOperationException("OSD monitor work area unavailable.");
        }

        private static void Move(IntPtr handle, int x, int y, int width, int height, NativeRect work)
        {
            x = MathEx.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
            y = MathEx.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
            if (GetWindowRect(handle, out var old) && old.Left == x && old.Top == y) return;
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
        }

        private static double WindowScale(Window window, IntPtr handle)
        {
            try { uint dpi = GetDpiForWindow(handle); if (dpi > 0) return dpi / 96.0; }
            catch (EntryPointNotFoundException) { }
            return VisualTreeHelper.GetDpi(window).DpiScaleX;
        }
        private static int Round(double value) => (int)Math.Round(MathEx.Clamp(value, int.MinValue, int.MaxValue));
        private static int AddSize(int value, int size) => (int)Math.Min(int.MaxValue, (long)value + size);
    }
}

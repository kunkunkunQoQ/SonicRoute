using System.Runtime.InteropServices;
using SonicRoute.Native.Graphics;

namespace SonicRoute.Native.Product;

internal static unsafe class ViewNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct WindowClass
    {
        internal uint Size, Style;
        internal nint Proc;
        internal int ClassBytes, WindowBytes;
        internal nint Instance, Icon, Cursor, Background, Menu, Name, SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Point(int x, int y) { internal int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Paint
    { internal nint Dc; internal int Erase; internal WinRect Rect; internal int Restore, Inc; internal fixed byte Reserved[32]; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo
    { internal uint Size; internal WinRect Area, Work; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Track
    { internal uint Size, Flags; internal nint Window; internal uint HoverTime; }
    [StructLayout(LayoutKind.Sequential)] internal struct Blend { internal byte Operation, Flags, Opacity, Format; }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo
    {
        internal uint Size; internal int Width, Height; internal ushort Planes, Bits;
        internal uint Compression, ImageSize; internal int X, Y; internal uint Colors, Important, Color;
    }

    [DllImport("user32", SetLastError = true)] internal static extern ushort RegisterClassExW(in WindowClass value);
    [DllImport("user32", CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern int UnregisterClassW(string value, nint module);
    [DllImport("user32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] internal static extern nint CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint id, nint module, nint param);
    [DllImport("user32")] internal static extern nint SendMessageW(nint window, uint message, nuint w, nint l);
    [DllImport("dwmapi")] internal static extern int DwmSetWindowAttribute(nint window, uint attribute, in int value, uint bytes);
    [DllImport("user32")] internal static extern nint DefWindowProcW(nint window, uint message, nuint w, nint l);
    [DllImport("user32")] internal static extern nint LoadCursorW(nint module, nint id);
    [DllImport("user32")] internal static extern int DestroyWindow(nint window);
    [DllImport("user32")] internal static extern int ShowWindow(nint window, int command);
    [DllImport("user32")] internal static extern int IsWindowVisible(nint window);
    [DllImport("user32")] internal static extern int IsIconic(nint window);
    [DllImport("user32")] internal static extern int SetForegroundWindow(nint window);
    [DllImport("user32")] internal static extern int GetClientRect(nint window, out WinRect rect);
    [DllImport("user32")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32")] internal static extern int SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] internal static extern int InvalidateRect(nint window, nint rect, int erase);
    [DllImport("user32")] internal static extern nint BeginPaint(nint window, out Paint paint);
    [DllImport("user32")] internal static extern int EndPaint(nint window, in Paint paint);
    [DllImport("user32")] internal static extern nint SetCapture(nint window);
    [DllImport("user32")] internal static extern int ReleaseCapture();
    [DllImport("user32")] internal static extern int TrackMouseEvent(in Track track);
    [DllImport("user32")] internal static extern int GetCursorPos(out Point point);
    [DllImport("user32")] internal static extern int ClientToScreen(nint window, ref Point point);
    [DllImport("user32")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32")] internal static extern int GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("shcore")] internal static extern int GetDpiForMonitor(nint monitor, int kind, out uint x, out uint y);
    [DllImport("user32")] internal static extern int SetLayeredWindowAttributes(nint window, uint key, byte opacity, uint flags);
    [DllImport("user32")] internal static extern nint CreatePopupMenu();
    [DllImport("user32", CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern int AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32")] internal static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [DllImport("user32")] internal static extern int DestroyMenu(nint menu);
    [DllImport("user32")] internal static extern int PostMessageW(nint window, uint message, nuint w, nint l);
    [DllImport("user32")] internal static extern int UpdateLayeredWindow(nint window, nint destDc, in Point point, in Point size,
        nint source, in Point sourcePoint, uint key, in Blend blend, uint flags);
    [DllImport("gdi32")] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] internal static extern nint CreateDIBSection(nint dc, in BitmapInfo info, uint type, out nint bits, nint section, uint offset);
    [DllImport("gdi32")] internal static extern nint SelectObject(nint dc, nint handle);
    [DllImport("gdi32")] internal static extern int DeleteDC(nint dc);
    [DllImport("gdi32")] internal static extern int DeleteObject(nint value);
    [DllImport("shell32")] internal static extern void DragAcceptFiles(nint window, int accept);
    [DllImport("shell32", CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern uint DragQueryFileW(nint drop, uint index, char* path, uint length);
    [DllImport("shell32")] internal static extern void DragFinish(nint drop);

    internal static (WinRect Work, uint Dpi) WorkArea(Point point)
    {
        var monitor = MonitorFromPoint(point, 2);
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        GetMonitorInfoW(monitor, ref info);
        return (info.Work, GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi : 96);
    }

    internal static uint Menu(nint owner, Point position, IReadOnlyList<(string Label, Action? Action)> entries)
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return 0;
        try
        {
            for (int i = 0; i < entries.Count; i++)
                AppendMenuW(menu, entries[i].Label.Length == 0 ? 0x800u : entries[i].Action == null ? 3u : 0u,
                    (nuint)(i + 1), entries[i].Label);
            SetForegroundWindow(owner);
            uint chosen = TrackPopupMenuEx(menu, 0x102, position.X, position.Y, owner, 0);
            PostMessageW(owner, 0, 0, 0);
            return chosen;
        }
        finally { DestroyMenu(menu); }
    }
}

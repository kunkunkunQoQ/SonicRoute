using System.Runtime.InteropServices;
using Microsoft.Win32;
using Win = SonicRoute.Native.Product.ShellNative;

namespace SonicRoute.Native.Product;

internal sealed unsafe partial class NativeApplicationWindow
{
    private bool darkTray, versionFour;
    private nint sharedIcon, whiteIcon;
    private Win.Rect lastIconRect;
    private long lastIconRectTime;
    private Win.Point? rightDownPoint;
    private uint lastContextEvent;
    private long lastContextAt;
    internal Win.Point TrayMenuPoint { get; private set; }

    private Win.NotifyIcon TrayData() => new() { Size = (uint)sizeof(Win.NotifyIcon), Window = Handle, Id = 1 };

    private void RestoreTray()
    {
        lastIconRect = default;
        rightDownPoint = null;
        lastContextEvent = 0;
        if (sharedIcon == 0)
        {
            int px = Math.Clamp(Win.GetSystemMetrics(49) * 2, 32, 64);
            sharedIcon = Win.LoadImageW(module, 32512, 1, px, px, 0x8000); // LR_SHARED
            if (sharedIcon == 0) sharedIcon = Win.LoadIconW(0, 32512);
        }
        darkTray = IsDarkTaskbar();
        var data = TrayData();
        data.Flags = 1 | 2; // NIF_MESSAGE | NIF_ICON. Product has no tooltip unless given one.
        if (tooltip.Length != 0)
        {
            data.Flags |= 4 | 128;
            tooltip.AsSpan(0, Math.Min(127, tooltip.Length)).CopyTo(new Span<char>(data.Tip, 127));
        }
        data.CallbackMessage = TrayMessage;
        data.Icon = ThemeIcon();
        TrayRegistered = Win.Shell_NotifyIconW(0, ref data) != 0 || Win.Shell_NotifyIconW(1, ref data) != 0;
        if (!TrayRegistered) return; // Explorer broadcasts TaskbarCreated when the notification area exists.
        data.Version = 4;
        versionFour = Win.Shell_NotifyIconW(4, ref data) != 0;
        TrayRegistrationCount++;
    }

    private void RefreshTrayTheme()
    {
        bool dark = IsDarkTaskbar();
        if (dark == darkTray || !TrayRegistered) return;
        darkTray = dark;
        var data = TrayData();
        data.Flags = 2;
        data.Icon = ThemeIcon();
        Win.Shell_NotifyIconW(1, ref data);
    }

    private static bool IsDarkTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is 0;
        }
        catch (System.Security.SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private nint ThemeIcon()
    {
        if (!darkTray) return sharedIcon;
        if (whiteIcon == 0) whiteIcon = CreateWhiteIcon(sharedIcon);
        return whiteIcon != 0 ? whiteIcon : sharedIcon;
    }

    private static nint CreateWhiteIcon(nint source)
    {
        if (Win.GetIconInfo(source, out var info) == 0) return 0;
        nint dc = 0;
        try
        {
            if (info.Color == 0 || Win.GetObjectW(info.Color, sizeof(Win.Bitmap), out var bitmap) == 0) return 0;
            if (bitmap.Width is < 1 or > 256 || bitmap.Height is < 1 or > 256) return 0;
            dc = Win.GetDC(0);
            if (dc == 0) return 0;
            var header = new Win.BitmapInfo { Size = 40, Width = bitmap.Width, Height = -bitmap.Height, Planes = 1, Bits = 32 };
            uint[] pixels = new uint[bitmap.Width * bitmap.Height];
            fixed (uint* data = pixels)
            {
                if (Win.GetDIBits(dc, info.Color, 0, (uint)bitmap.Height, data, ref header, 0) != bitmap.Height) return 0;
                // Preserve premultiplied alpha when recoloring the original product glyph.
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = (pixels[i] >> 24) * 0x01010101;
                if (Win.SetDIBits(dc, info.Color, 0, (uint)bitmap.Height, data, ref header, 0) != bitmap.Height) return 0;
            }
            return Win.CreateIconIndirect(in info);
        }
        finally
        {
            if (dc != 0) Win.ReleaseDC(0, dc);
            if (info.Mask != 0) Win.DeleteObject(info.Mask);
            if (info.Color != 0) Win.DeleteObject(info.Color);
        }
    }

    private void RemoveTray()
    {
        if (TrayRegistered)
        {
            var data = TrayData();
            Win.Shell_NotifyIconW(2, ref data);
            TrayRegistered = false;
        }
        if (whiteIcon != 0) { Win.DestroyIcon(whiteIcon); whiteIcon = 0; }
    }

    private void OnTrayMessage(nuint w, nint l)
    {
        if (!TrayRegistered || closing) return;
        if ((versionFour ? ((uint)l >> 16) : (uint)w) != 1) return;
        uint value = (ushort)l;
        switch (value)
        {
            case 0x201: // start of a new left-button gesture
                doubleClick = false;
                break;
            case 0x400 when versionFour: // NIN_SELECT; redundant alongside WM_LBUTTONUP on some shells
            case 0x202: // left up
                if (doubleClick) break;
                pendingClick?.Dispose();
                pendingClick = ScheduleOnce(TimeSpan.FromMilliseconds(280), () => { pendingClick = null; Raise(ShellSignalKind.QuickPanel); });
                break;
            case 0x203: // double click
                pendingClick?.Dispose(); pendingClick = null; doubleClick = true;
                Raise(ShellSignalKind.MainWindow);
                break;
            case 0x204: // WM_RBUTTONDOWN, v4 coordinates are signed screen pixels.
                rightDownPoint = versionFour ? TrayMousePoint(w) : null;
                lastContextEvent = 0;
                break;
            case 0x7b: // WM_CONTEXTMENU, including keyboard invocation
            case 0x205: // WM_RBUTTONUP; some Explorer hosts deliver both notifications.
                pendingClick?.Dispose(); pendingClick = null;
                doubleClick = false;
                long now = Environment.TickCount64;
                bool duplicate = lastContextEvent != 0 && lastContextEvent != value && now - lastContextAt < 500;
                lastContextEvent = value;
                lastContextAt = now;
                if (!duplicate)
                {
                    // v4 does not specify wParam coordinates for WM_CONTEXTMENU.
                    // Use the preceding mouse anchor, or the actual icon for a keyboard request.
                    if (value == 0x205 && versionFour) TrayMenuPoint = TrayMousePoint(w);
                    else if (rightDownPoint is { } point) TrayMenuPoint = point;
                    else
                    {
                        Win.GetCursorPos(out var cursor);
                        TrayMenuPoint = value == 0x7b && TryGetTrayRect(out var rect) && !rect.Contains(cursor, 1)
                            ? new Win.Point { X = rect.Left, Y = rect.Top } : cursor;
                    }
                    Raise(ShellSignalKind.TrayMenu);
                }
                rightDownPoint = null;
                break;
            case 0x401: // NIN_KEYSELECT
                pendingClick?.Dispose(); pendingClick = null;
                Raise(ShellSignalKind.QuickPanel);
                break;
        }
    }

    private static Win.Point TrayMousePoint(nuint packed) => new()
    {
        X = unchecked((short)packed), Y = unchecked((short)(packed >> 16))
    };

    internal bool TryGetTrayRect(out Win.Rect rect)
    {
        VerifyOwner();
        rect = default;
        var id = new Win.IconId { Size = (uint)sizeof(Win.IconId), Window = Handle, Id = 1 };
        return TrayRegistered && Win.Shell_NotifyIconGetRect(in id, out rect) >= 0 && rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    private bool IsOverTray(Win.Point point)
    {
        if (!wheelEverywhere)
        {
            if (TryGetTrayRect(out var rect))
            {
                lastIconRect = rect;
                lastIconRectTime = Environment.TickCount64;
            }
            return lastIconRectTime > 0 && Environment.TickCount64 - lastIconRectTime < 30000 && lastIconRect.Contains(point, 4);
        }

        // A hit only occurs on a wheel event. Resolve the taskbar under the pointer, including secondary monitors.
        nint hit = Win.WindowFromPoint(point);
        nint root = Win.GetAncestor(hit, 2);
        if (root == 0) return false;
        Span<char> name = stackalloc char[64];
        fixed (char* p = name)
        {
            int count = Win.GetClassNameW(root, p, name.Length);
            var cls = name[..count];
            if (cls.SequenceEqual("NotifyIconOverflowWindow") || cls.SequenceEqual("TopLevelWindowForOverflowXamlIsland")) return true;
            if (!cls.SequenceEqual("Shell_TrayWnd") && !cls.SequenceEqual("Shell_SecondaryTrayWnd")) return false;
        }
        nint notify = Win.FindWindowExW(root, 0, "TrayNotifyWnd", null);
        if (notify != 0 && Win.GetWindowRect(notify, out var region) != 0 && region.Contains(point)) return true;
        nint overflow = Win.FindWindowExW(root, 0, "NotifyContainerOverflowWindow", null);
        return overflow != 0 && Win.GetWindowRect(overflow, out region) != 0 && region.Contains(point);
    }
}

internal static unsafe partial class ShellNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct IconInfo { internal int Icon; internal uint X, Y; internal nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] internal struct Bitmap { internal int Type, Width, Height, Stride; internal ushort Planes, Bits; internal nint Pixels; }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo
    {
        internal uint Size; internal int Width, Height; internal ushort Planes, Bits;
        internal uint Compression, Bytes; internal int X, Y; internal uint Colors, Important, FirstColor;
    }
    [LibraryImport("user32")] internal static partial int GetCursorPos(out Point point);
    [LibraryImport("user32")] internal static partial int GetIconInfo(nint icon, out IconInfo info);
    [LibraryImport("user32")] internal static partial nint CreateIconIndirect(in IconInfo info);
    [LibraryImport("user32")] internal static partial int DestroyIcon(nint icon);
    [LibraryImport("user32")] internal static partial nint GetDC(nint window);
    [LibraryImport("user32")] internal static partial int ReleaseDC(nint window, nint dc);
    [LibraryImport("gdi32")] internal static partial int GetObjectW(nint obj, int bytes, out Bitmap bitmap);
    [LibraryImport("gdi32")] internal static partial int DeleteObject(nint obj);
    [LibraryImport("gdi32")] internal static partial int GetDIBits(nint dc,nint bmp,uint start,uint lines,uint* data,ref BitmapInfo header,uint usage);
    [LibraryImport("gdi32")] internal static partial int SetDIBits(nint dc,nint bmp,uint start,uint lines,uint* data,ref BitmapInfo header,uint usage);
}

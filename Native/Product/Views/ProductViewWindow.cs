using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Native.Graphics;
using Win = SonicRoute.Native.Product.ViewNative;

namespace SonicRoute.Native.Product;

internal enum ProductSurface { Main, Panel, Osd }

internal sealed unsafe class ProductViewWindow : IDisposable
{
    private static readonly Dictionary<nint, ProductViewWindow> Windows = new();
    private static readonly string ClassName = $"SonicRoute.NativeView.{Environment.ProcessId}";
    private static readonly nint Module = ShellNative.GetModuleHandleW(null);
    private static bool registered;
    private readonly ProductSurface surface;
    private readonly AppConfig config;
    private ViewCanvas? canvas;
    private nint dc, bitmap, savedBitmap;
    private Win.Point osdOrigin;
    private int bitmapWidth, bitmapHeight;
    private string osdTitle = "", osdText = "";
    private Action? hideRequest;
    internal ProductViews? View { get; set; }
    internal nint Handle { get; private set; }
    internal bool Visible => Handle != 0 && Win.IsWindowVisible(Handle) != 0 && Win.IsIconic(Handle) == 0;

    internal ProductViewWindow(ProductSurface surface, AppConfig config, Action? hideRequest = null)
    {
        this.surface = surface;
        this.config = config;
        this.hideRequest = hideRequest;
    }

    internal void Show(nint owner)
    {
        if (Handle != 0) { Win.ShowWindow(Handle, 9); Win.SetForegroundWindow(Handle); return; }

        Win.GetCursorPos(out var point);
        var (work, dpi) = Win.WorkArea(point);
        bool panel = surface == ProductSurface.Panel;
        if (panel && config.QuickPanelPosMode == "custom" && config.QuickPanelCustomX != -1)
            (work, dpi) = Win.WorkArea(new(config.QuickPanelCustomX, config.QuickPanelCustomY));
        int width = Math.Min((panel ? 440 : 982) * (int)dpi / 96, work.Width);
        int height = Math.Min((panel ? config.QuickPanelHeight + 48 : 740) * (int)dpi / 96, work.Height);
        int x = panel ? work.Right - width - 12 : work.Left + (work.Width - width) / 2;
        int y = panel ? work.Bottom - height - 12 : work.Top + (work.Height - height) / 2;
        if (panel && config.QuickPanelPosMode == "custom" && config.QuickPanelCustomX != -1)
        {
            x = Math.Clamp(config.QuickPanelCustomX, work.Left, Math.Max(work.Left, work.Right - width));
            y = Math.Clamp(config.QuickPanelCustomY, work.Top, Math.Max(work.Top, work.Bottom - height));
        }
        Create(panel ? 0x80088u : 0xC0000u, panel ? 0x80C40000u : 0x00CF0000u,
            panel ? L10n.T("Tray.OpenPanel") : BuildIdentity.DisplayName + " · Native AOT Preview",
            x, y, width, height, panel ? owner : 0);
        nint icon = ShellNative.LoadIconW(Module, 32512);
        if (icon != 0) { Win.SendMessageW(Handle, 0x80, 0, icon); Win.SendMessageW(Handle, 0x80, 1, icon); }
        SetOpacity();
        Win.DragAcceptFiles(Handle, 1);
        Win.ShowWindow(Handle, 5);
        if (Win.IsWindowVisible(Handle) == 0) Win.ShowWindow(Handle, 5);
        Win.SetForegroundWindow(Handle);
        View?.Opened();
    }

    internal void SetOpacity()
    {
        if (surface == ProductSurface.Osd || Handle == 0) return;
        Win.SetLayeredWindowAttributes(Handle, 0, (byte)Math.Clamp(85 + config.BackgroundOpacity * 170 / 100, 0, 255), 2);
        int dark = ViewPalette.From(config).Background == 0x151c28 ? 1 : 0;
        Win.DwmSetWindowAttribute(Handle, 20, in dark, sizeof(int));
    }

    internal bool ShowOsd(nint owner, string title, string text, byte alpha)
    {
        osdTitle = title;
        osdText = text;
        Win.GetCursorPos(out var cursor);
        var (work, dpi) = Win.WorkArea(cursor);
        int width = Math.Min(Math.Clamp(config.OsdWidth, 200, 760) * (int)dpi / 96, work.Width);
        int height = Math.Min(78 * (int)dpi / 96, work.Height);
        int gutter = 16 * (int)dpi / 96;
        int x = config.OsdPosition.EndsWith('L') ? work.Left + gutter :
            config.OsdPosition.EndsWith('C') ? work.Left + (work.Width - width) / 2 : work.Right - width - gutter;
        int y = config.OsdPosition.StartsWith('B') ? work.Bottom - height - gutter : work.Top + gutter;
        if (config.OsdPosition == "C") y = work.Top + (work.Height - height) / 2;
        x = Math.Clamp(x + config.OsdOffsetX, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y + config.OsdOffsetY, work.Top, Math.Max(work.Top, work.Bottom - height));
        if (Handle == 0)
            Create(0x80800A8, 0x80000000, "SonicRoute OSD", x, y, width, height, owner);
        canvas ??= new(layered: true);
        EnsureBitmap(width, height);
        if (!canvas.Begin(dc, width, height, dpi)) return false;
        float w = width * 96f / dpi, h = height * 96f / dpi;
        var palette = ViewPalette.From(config);
        canvas.Clear(0, 0);
        canvas.Fill(new(2, 2, w - 4, h - 4), palette.Background, 12, .96f);
        canvas.Fill(new(14, 17, 4, h - 34), palette.Accent, 2);
        canvas.Text(osdTitle, new(28, 12, w - 40, 25), palette.Text);
        canvas.Text(osdText, new(28, 38, w - 40, 25), palette.Secondary);
        if (config.OsdVolumeVisualEnabled && osdText.EndsWith('%') && int.TryParse(osdText.AsSpan(0, osdText.Length - 1), out int level))
        {
            canvas.Fill(new(28, h - 11, w - 44, 3), palette.Card, 1.5f);
            if (level > 0) canvas.Fill(new(28, h - 11, (w - 44) * Math.Min(level, 100) / 100f, 3), palette.Accent, 1.5f);
        }
        if (!canvas.End()) return false;
        osdOrigin = new(x, y);
        SetOsdAlpha(alpha);
        Win.ShowWindow(Handle, 4);
        if (Win.IsWindowVisible(Handle) == 0) Win.ShowWindow(Handle, 4);
        return true;
    }

    internal void SetOsdAlpha(byte alpha)
    {
        if (Handle == 0 || bitmap == 0) return;
        var blend = new Win.Blend { Opacity = alpha, Format = 1 };
        if (Win.UpdateLayeredWindow(Handle, 0, osdOrigin, new(bitmapWidth, bitmapHeight), dc, new(0, 0), 0, blend, 2) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateLayeredWindow");
    }

    private void EnsureBitmap(int width, int height)
    {
        if (bitmap != 0 && bitmapWidth == width && bitmapHeight == height) return;
        ReleaseBitmap();
        dc = Win.CreateCompatibleDC(0);
        if (dc == 0) throw new InvalidOperationException("OSD memory DC unavailable.");
        var info = new Win.BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
        bitmap = Win.CreateDIBSection(dc, info, 0, out _, 0, 0);
        if (bitmap == 0) throw new InvalidOperationException("OSD bitmap unavailable.");
        savedBitmap = Win.SelectObject(dc, bitmap);
        (bitmapWidth, bitmapHeight) = (width, height);
    }

    private void ReleaseBitmap()
    {
        if (dc != 0 && savedBitmap != 0) Win.SelectObject(dc, savedBitmap);
        if (bitmap != 0) Win.DeleteObject(bitmap);
        if (dc != 0) Win.DeleteDC(dc);
        bitmap = savedBitmap = dc = 0;
    }

    private void Create(uint ex, uint style, string title, int x, int y, int width, int height, nint owner)
    {
        if (!registered)
        {
            fixed (char* name = ClassName)
            {
                var wc = new Win.WindowClass
                {
                    Size = (uint)sizeof(Win.WindowClass), Style = 8, Instance = Module, Name = (nint)name,
                    Proc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProc,
                    Cursor = Win.LoadCursorW(0, 32512)
                };
                if (Win.RegisterClassExW(wc) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassEx");
            }
            registered = true;
        }
        Handle = Win.CreateWindowExW(ex, ClassName, title, style, x, y, width, height, owner, 0, Module, 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowEx");
        Windows.Add(Handle, this);
    }

    internal void Invalidate() { if (Handle != 0) Win.InvalidateRect(Handle, 0, 0); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nuint w, nint l)
    {
        if (!Windows.TryGetValue(hwnd, out var instance)) return Win.DefWindowProcW(hwnd, message, w, l);
        try { return instance.Process(message, w, l); }
        catch (Exception error)
        {
            ExceptionDiagnostics.Record("NativeView.Window", error);
            return Win.DefWindowProcW(hwnd, message, w, l);
        }
    }

    private nint Process(uint message, nuint w, nint l)
    {
        if (message == 0x84 && surface == ProductSurface.Osd) return -1;
        if (message == 0x10) { Close(); return 0; }
        if (message == 0x82) { var old = Handle; Released(); return Win.DefWindowProcW(old, message, w, l); }
        if (View == null) return Win.DefWindowProcW(Handle, message, w, l);

        float scale = 96f / Math.Max(96, Win.GetDpiForWindow(Handle));
        float x = (short)l * scale, y = (short)(l >> 16) * scale;
        switch (message)
        {
            case 0x05: View.VisibilityChanged(w != 1); Invalidate(); return 0;
            case 0x02E0:
                var bounds = *(WinRect*)l;
                Win.SetWindowPos(Handle, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x14);
                Invalidate();
                return 0;
            case 0x0F: Paint(); return 0;
            case 0x0318: PrintClient((nint)w); return 0;
            case 0x003D when (int)l == -25: return View.GetAccessibleObject(w, l);
            case 0x14: return 1;
            case 0x06 when (ushort)w == 0 && surface == ProductSurface.Panel: hideRequest?.Invoke(); break;
            case 0x0200:
                Win.TrackMouseEvent(new Win.Track { Size = (uint)sizeof(Win.Track), Flags = 2, Window = Handle });
                View.PointerMove(x, y); return 0;
            case 0x02A3: View.PointerMove(-1, -1); return 0;
            case 0x0201: View.PointerDown(x, y, (int)w); return 0;
            case 0x0202: View.PointerUp(); return 0;
            case 0x0215: View.PointerUp(); return 0;
            case 0x0205: View.PointerMenu(x, y); return 0;
            case 0x020A: View.Scroll(-(short)(w >> 16) / 120 * 3); return 0;
            case 0x0100: if (View.Key((uint)w)) return 0; break;
            case 0x0233: FilesDropped((nint)w); return 0;
        }
        return Win.DefWindowProcW(Handle, message, w, l);
    }

    private void FilesDropped(nint drop)
    {
        try
        {
            int count = (int)Math.Min(Win.DragQueryFileW(drop, uint.MaxValue, null, 0), 128);
            var paths = new List<string>(count);
            for (uint i = 0; i < count; i++)
            {
                int length = checked((int)Win.DragQueryFileW(drop, i, null, 0));
                char[] path = new char[length + 1];
                fixed (char* name = path)
                    if (Win.DragQueryFileW(drop, i, name, (uint)path.Length) > 0) paths.Add(new string(name, 0, length));
            }
            if (paths.Count != 0) View?.FilesDropped(paths);
        }
        finally { Win.DragFinish(drop); }
    }

    private void Paint()
    {
        Win.BeginPaint(Handle, out var paint);
        try
        {
            Win.GetClientRect(Handle, out var area);
            if (area.Width == 0 || area.Height == 0) return;
            uint dpi = Math.Max(96, Win.GetDpiForWindow(Handle));
            canvas ??= new();
            if (!canvas.Begin(Handle, area.Width, area.Height, dpi)) { Invalidate(); return; }
            View!.Paint(canvas, area.Width * 96f / dpi, area.Height * 96f / dpi);
            if (!canvas.End()) Invalidate();
        }
        finally { if (Handle != 0) Win.EndPaint(Handle, paint); }
    }

    private void PrintClient(nint printDc)
    {
        Win.GetClientRect(Handle, out var area);
        if (area.Width <= 0 || area.Height <= 0 || printDc == 0) return;
        uint dpi = Math.Max(96, Win.GetDpiForWindow(Handle));
        using var print = new ViewCanvas(layered: true);
        if (!print.Begin(printDc, area.Width, area.Height, dpi)) return;
        View!.Paint(print, area.Width * 96f / dpi, area.Height * 96f / dpi);
        print.End();
    }

    internal void Close() { if (Handle != 0) Win.DestroyWindow(Handle); }
    private void Released()
    {
        Windows.Remove(Handle);
        Handle = 0;
        View?.Closed();
        canvas?.Dispose();
        canvas = null;
        ReleaseBitmap();
    }

    public void Dispose() { Close(); View = null; hideRequest = null; }
}

internal readonly record struct ViewPalette(uint Background, uint Sidebar, uint Card, uint Hover, uint Text, uint Secondary, uint Accent)
{
    internal static ViewPalette From(AppConfig config)
    {
        bool light = config.ThemeMode == "light" || config.ThemeMode == "system" && AppsLight();
        string hex = config.Accent.TrimStart('#');
        uint accent = hex.Length is 6 or 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint rgb)
            ? rgb & 0xffffff : config.Accent switch
        {
            "green" => 0x39a776, "red" => 0xdc626a, "purple" => 0x9674e8,
            "orange" => 0xe88a48, "pink" => 0xe66a9d, "teal" => 0x2eaaae, _ => 0x3d91ef
        };
        return light ? new(0xf4f6f9, 0xe8edf4, 0xffffff, 0xddeafa, 0x182438, 0x425671, accent)
            : new(0x151c28, 0x101621, 0x202b3a, 0x293d55, 0xf2f5fb, 0xaebbd0, accent);
    }

    private static bool AppsLight()
    {
        return Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is 1;
    }
}

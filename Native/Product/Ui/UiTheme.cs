using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SonicRoute.Core;

namespace SonicRoute.Native.Ui;

internal readonly record struct UiColor(byte R, byte G, byte B, byte A = 255)
{
    public static UiColor Transparent => new(0, 0, 0, 0);
    public static UiColor Black => new(0, 0, 0, 255);
    public static UiColor White => new(255, 255, 255, 255);

    public static UiColor FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);
    public UiColor WithAlpha(byte alpha) => new(R, G, B, alpha);

    public UiColor WithAlphaFraction(float factor)
    {
        byte a = (byte)Math.Clamp((int)MathF.Round(A * Math.Clamp(factor, 0f, 1f)), 0, 255);
        return new(R, G, B, a);
    }

    public static UiColor Blend(UiColor a, UiColor b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        byte C(byte x, byte y) => (byte)Math.Clamp((int)Math.Round(x * (1.0 - t) + y * t), 0, 255);
        return new(C(a.R, b.R), C(a.G, b.G), C(a.B, b.B), C(a.A, b.A));
    }

    public static bool TryParseHex(string? text, out UiColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            color = new((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), 255);
            return true;
        }
        if (s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
        {
            color = new((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF), (byte)((argb >> 24) & 0xFF));
            return true;
        }
        return false;
    }

    public string ToHexRgb() => $"#{R:X2}{G:X2}{B:X2}";
}

internal readonly record struct UiPoint(float X, float Y);

internal readonly record struct UiRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public float CenterX => X + Width * 0.5f;
    public float CenterY => Y + Height * 0.5f;
    public bool IsEmpty => Width <= 0f || Height <= 0f;

    public bool Contains(float px, float py) =>
        px >= X && py >= Y && px < Right && py < Bottom;

    public UiRect Deflate(float all) => Deflate(all, all, all, all);

    public UiRect Deflate(float horizontal, float vertical) =>
        Deflate(horizontal, vertical, horizontal, vertical);

    public UiRect Deflate(float left, float top, float right, float bottom) =>
        new(X + left, Y + top, MathF.Max(0f, Width - left - right), MathF.Max(0f, Height - top - bottom));

    public UiRect Offset(float dx, float dy) => new(X + dx, Y + dy, Width, Height);
}

internal readonly struct D2dColorF
{
    public readonly float R, G, B, A;
    public D2dColorF(UiColor c)
    {
        R = c.R / 255f;
        G = c.G / 255f;
        B = c.B / 255f;
        A = c.A / 255f;
    }
}

/// <summary>
/// Theme tokens matching SonicRoute/ThemeService.cs and SonicRoute/App.xaml verbatim.
/// </summary>
internal sealed class UiThemePalette
{
    public bool IsDark { get; init; }
    public int OpacityPercent { get; init; }
    public UiColor WindowBg { get; init; }
    public UiColor WindowBgAlpha { get; init; }
    public UiColor SurfaceBg { get; init; }
    public UiColor SurfaceBgAlpha { get; init; }
    public UiColor SurfaceAlt { get; init; }
    public UiColor SurfaceCard { get; init; }
    public UiColor TextPrimary { get; init; }
    public UiColor TextSecondary { get; init; }
    public UiColor Border { get; init; }
    public UiColor NavHover { get; init; }
    public UiColor NavSelected { get; init; }
    public UiColor SliderBg { get; init; }
    public UiColor? HighContrastScrollInk { get; init; }
    public UiColor HighContrastScrollHighlight { get; init; }
    public UiColor Overlay { get; init; }

    public UiColor Accent { get; init; }
    public UiColor AccentHover { get; init; }
    public UiColor AccentPressed { get; init; }
    public UiColor AccentDisabled { get; init; }
    public UiColor AccentPeak { get; init; }
    public UiColor AccentInverse { get; init; }
    public UiColor OnAccent { get; init; }
    public UiColor AccentIconInk { get; init; }

    public UiColor Success { get; init; }
    public UiColor Fail { get; init; }
    public UiColor DeviceNativeBg { get; init; }
    public UiColor DeviceNativeFg { get; init; }
    public UiColor DeviceMirrorBorder { get; init; }
    public UiColor OsdTitleText { get; init; }
    public UiColor OsdTitle { get; init; }
    public UiColor OsdAccent { get; init; }

    public static UiThemePalette Resolve(AppConfig cfg)
    {
        bool dark = IsDarkTheme(cfg.ThemeMode);
        var contrast = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        bool useContrast = SystemParametersInfoW(0x0042 /* SPI_GETHIGHCONTRAST */, contrast.Size, ref contrast, 0)
            && (contrast.Flags & 1) != 0;
        UiColor accentColor = ResolveAccent(cfg.Accent);
        int opacity = Math.Clamp(cfg.BackgroundOpacity, 0, 100);
        byte alpha = (byte)(255 * opacity / 100);

        UiColor hover = UiColor.Blend(accentColor, dark ? UiColor.White : UiColor.Black, dark ? 0.14 : 0.10);
        UiColor pressed = UiColor.Blend(accentColor, dark ? UiColor.Black : UiColor.White, 0.12);
        UiColor disabled = UiColor.Blend(accentColor, UiColor.FromRgb(128, 128, 128), 0.55);

        int accentBrightness = (accentColor.R * 299 + accentColor.G * 587 + accentColor.B * 114) / 1000;
        bool nearExtreme = dark ? accentBrightness >= 220 : accentBrightness <= 40;
        UiColor peakTarget = dark
            ? (nearExtreme ? UiColor.Black : UiColor.White)
            : (nearExtreme ? UiColor.White : UiColor.Black);
        UiColor peak = UiColor.Blend(accentColor, peakTarget, nearExtreme ? 0.60 : (dark ? 0.82 : 0.68));

        UiColor windowBg = dark ? UiColor.FromRgb(0x1E, 0x1E, 0x1E) : UiColor.FromRgb(0xF7, 0xF8, 0xFA);
        UiColor surfaceBg = dark ? UiColor.FromRgb(0x2B, 0x2B, 0x2B) : UiColor.FromRgb(0xFF, 0xFF, 0xFF);
        UiColor surfaceAlt = dark ? UiColor.FromRgb(0x25, 0x25, 0x25) : UiColor.FromRgb(0xF1, 0xF3, 0xF6);
        UiColor textPrimary = dark ? UiColor.FromRgb(0xF3, 0xF4, 0xF6) : UiColor.FromRgb(0x1F, 0x29, 0x37);
        UiColor textSecondary = dark ? UiColor.FromRgb(0x9C, 0xA3, 0xAF) : UiColor.FromRgb(0x6B, 0x72, 0x80);
        UiColor border = dark ? UiColor.FromRgb(0x3C, 0x3C, 0x3C) : UiColor.FromRgb(0xE5, 0xE7, 0xEB);
        UiColor navHover = dark ? UiColor.FromRgb(0x33, 0x33, 0x33) : UiColor.FromRgb(0xED, 0xF0, 0xF5);
        UiColor navSelected = dark
            ? UiColor.Blend(accentColor, UiColor.Black, 0.70)
            : UiColor.Blend(accentColor, UiColor.White, 0.86);
        UiColor sliderBg = dark ? UiColor.FromRgb(0x4B, 0x4B, 0x4B) : UiColor.FromRgb(0xD8, 0xDC, 0xE2);
        UiColor overlay = dark
            ? UiColor.Blend(accentColor, UiColor.Black, 0.78)
            : UiColor.Blend(accentColor, UiColor.White, 0.90);
        UiColor surfaceCard = dark ? UiColor.FromRgb(0x23, 0x23, 0x23) : UiColor.FromRgb(0xFA, 0xFB, 0xFC);

        UiColor osdBg = dark ? UiColor.FromRgb(0x2B, 0x2B, 0x2B) : UiColor.White;
        UiColor osdTitle = UiColor.Blend(textPrimary, osdBg, 0.18);
        UiColor osdAccent = ComputeOsdAccent(accentColor, osdBg, dark);

        UiColor deviceNativeBg = dark
            ? UiColor.Blend(accentColor, UiColor.Black, 0.54)
            : UiColor.Blend(accentColor, UiColor.White, 0.75);
        UiColor darkInk = UiColor.FromRgb(0x11, 0x18, 0x27);
        UiColor deviceNativeFg = ContrastRatio(deviceNativeBg, textPrimary) >= 4.5 ? textPrimary
            : ContrastRatio(deviceNativeBg, darkInk) >= 4.5 ? darkInk
            : Luminance(deviceNativeBg) > 0.179 ? UiColor.Black : UiColor.White;
        UiColor deviceMirrorBorder = dark
            ? UiColor.Blend(accentColor, UiColor.Black, 0.45)
            : UiColor.Blend(accentColor, UiColor.White, 0.65);

        return new UiThemePalette
        {
            IsDark = dark,
            OpacityPercent = opacity,
            WindowBg = windowBg,
            WindowBgAlpha = windowBg.WithAlpha(alpha),
            SurfaceBg = surfaceBg,
            SurfaceBgAlpha = surfaceBg.WithAlpha(alpha),
            SurfaceAlt = surfaceAlt,
            SurfaceCard = surfaceCard,
            TextPrimary = textPrimary,
            TextSecondary = textSecondary,
            Border = border,
            NavHover = navHover,
            NavSelected = navSelected,
            SliderBg = sliderBg,
            HighContrastScrollInk = useContrast ? SystemColor(18 /* COLOR_BTNTEXT */) : null,
            HighContrastScrollHighlight = useContrast ? SystemColor(13 /* COLOR_HIGHLIGHT */) : default,
            Overlay = overlay,
            Accent = accentColor,
            AccentHover = hover,
            AccentPressed = pressed,
            AccentDisabled = disabled,
            AccentPeak = peak,
            AccentInverse = UiColor.FromRgb((byte)(255 - accentColor.R), (byte)(255 - accentColor.G), (byte)(255 - accentColor.B)),
            OnAccent = UiColor.White,
            AccentIconInk = Luminance(accentColor) > 0.179 ? UiColor.Black : UiColor.White,
            Success = UiColor.FromRgb(0x16, 0xA3, 0x4A),
            Fail = UiColor.FromRgb(0xDC, 0x26, 0x26),
            DeviceNativeBg = deviceNativeBg,
            DeviceNativeFg = deviceNativeFg,
            DeviceMirrorBorder = deviceMirrorBorder,
            OsdTitleText = UiColor.Blend(accentColor, dark ? UiColor.White : UiColor.Black, 0.30),
            OsdTitle = osdTitle,
            OsdAccent = osdAccent,
        };
    }

    public static bool IsDarkTheme(string? mode)
    {
        if (string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast { public uint Size, Flags; public nint Scheme; }
    [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(uint action, uint size, ref HighContrast value, uint flags);
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    private static UiColor SystemColor(int index)
    {
        uint rgb = GetSysColor(index);
        return UiColor.FromRgb((byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16));
    }

    public static UiColor ResolveAccent(string? accent)
    {
        if (UiColor.TryParseHex(accent, out var custom)) return custom.WithAlpha(255);
        return (accent ?? "").ToLowerInvariant() switch
        {
            "green" => UiColor.FromRgb(0x22, 0xC5, 0x5E),
            "purple" => UiColor.FromRgb(0xEC, 0x48, 0x99),
            _ => UiColor.FromRgb(0x2F, 0x80, 0xED),
        };
    }

    private static double Luminance(UiColor c)
    {
        static double Lin(byte ch)
        {
            double v = ch / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double ContrastRatio(UiColor a, UiColor b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static UiColor ComputeOsdAccent(UiColor accent, UiColor bg, bool dark)
    {
        double bgL = Luminance(bg);
        UiColor res = accent;
        for (int i = 0; i <= 10; i++)
        {
            double l = Luminance(res);
            if ((Math.Max(l, bgL) + 0.05) / (Math.Min(l, bgL) + 0.05) >= 3.0) break;
            res = UiColor.Blend(accent, dark ? UiColor.White : UiColor.Black, Math.Min(1.0, (i + 1) / 10.0));
        }
        return res;
    }
}

/// <summary>
/// Exact WPF 1.36 DIP layout constants extracted from MainWindow.xaml, QuickPanelModernWindow.xaml,
/// PhoneAudioWindow.xaml, and App.xaml.
/// </summary>
internal static class UiMetrics
{
    public const string FontFamily = "Segoe UI";
    public const string MonoFontFamily = "Consolas";

    public const float MainDefaultWidth = 920f;
    public const float MainDefaultHeight = 620f;
    public const float MainMinWidth = 780f;
    public const float MainMinHeight = 540f;
    public const float MainCornerRadius = 10f;
    public const float TitleBarHeight = 42f;
    public const float NavExpandedWidth = 196f;
    public const float NavCollapsedWidth = 64f;
    public const float NavItemHeight = 40f;
    public const float NavItemRadius = 8f;
    public const float NavFontSize = 13.5f;
    public const float MainContentPaddingH = 22f;
    public const float MainContentPaddingTop = 10f;
    public const float MainContentPaddingBottom = 20f;

    public const float QuickPanelWindowWidth = 372f;
    public const float QuickPanelSurfaceWidth = 366f;
    public const float QuickPanelCornerRadius = 12f;
    public const float QuickPanelMinHeight = 350f;
    public const float QuickPanelMaxHeight = 850f;
    public const float QuickPanelCompactRowHeight = 38f;
    public const float QuickPanelFullRowHeight = 54f;
    public const float OutputPopupWidth = 366f;
    public const float OutputPopupHeaderHeight = 58f;
    public const float OutputPopupRowHeight = 64f;

    public const float PhoneCompanionWidth = 235f;
    public const float PhoneCompanionGap = 6f;

    public const float ScrollBarWidth = 14f;
    public const float ScrollThumbWidth = 6f;
    public const float ScrollThumbHoverWidth = 8f;
    public const float ScrollThumbRadius = 4f;

    public const float MenuCornerRadius = 6f;
    public const float MenuItemHeight = 32f;
    public const float ComboCornerRadius = 8f;
    public const float CardCornerRadius = 10f;
    public const float ButtonCornerRadius = 6f;
}

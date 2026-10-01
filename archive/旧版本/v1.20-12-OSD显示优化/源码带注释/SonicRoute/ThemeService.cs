using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SonicRoute.Core.Compat;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace SonicRoute
{
    /// <summary>
    /// 主题服务：mode(跟随系统/浅色/深色) × accent(蓝/绿/紫)。
    /// XAML 中统一使用 DynamicResource Theme.* 引用，切换即时生效。
    /// </summary>
    public static class ThemeService
    {
        // 冻结画刷复用；容量封顶，避免拖动自定义 RGB 时把所有中间色永久留在静态缓存中。
        private const int BrushCacheLimit = 256;
        private static readonly object _brushGate = new();
        private static readonly Dictionary<int, SolidColorBrush> _brushCache = new();
        private static readonly Queue<int> _brushOrder = new();
        private static SolidColorBrush GetBrush(Color c)
        {
            int key = (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
            lock (_brushGate)
            {
                if (_brushCache.TryGetValue(key, out var cached)) return cached;
                var b = new SolidColorBrush(c);
                b.Freeze();
                _brushCache[key] = b;
                _brushOrder.Enqueue(key);
                while (_brushCache.Count > BrushCacheLimit)
                    _brushCache.Remove(_brushOrder.Dequeue());
                return b;
            }
        }
        public static bool IsDarkMode()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int v)
                    return v == 0;
            }
            catch
            {
                // 读取失败时默认浅色
            }
            return false;
        }

        public static void Apply(string mode, string accent)
        {
            bool dark = mode switch
            {
                "light" => false,
                "dark" => true,
                _ => IsDarkMode()
            };
            Apply(dark, accent);
        }

        public static void Apply(bool dark, string accent = "blue")
        {
            // accent 支持预设名（blue/green/purple）或自定义 "#RRGGBB"
            Color accentColor;
            if (accent.StartsWith("#", StringComparison.OrdinalIgnoreCase) && accent.Length == 7)
            {
                try { accentColor = (Color)System.Windows.Media.ColorConverter.ConvertFromString(accent); }
                catch { accentColor = Color.FromRgb(0x2F, 0x80, 0xED); }
            }
            else
            {
                (byte aR, byte aG, byte aB) = accent switch
                {
                    "green" => ((byte)0x22, (byte)0xC5, (byte)0x5E),
                    "purple" => ((byte)0xEC, (byte)0x48, (byte)0x99), // 粉色 #EC4899
                    _ => ((byte)0x2F, (byte)0x80, (byte)0xED) // blue
                };
                accentColor = Color.FromRgb(aR, aG, aB);
            }

            // 悬停：深色主题向浅混合，浅色主题向深混合
            var hover = Blend(accentColor, dark ? Colors.White : Colors.Black, dark ? 0.14 : 0.10);
            var pressed = Blend(accentColor, dark ? Colors.Black : Colors.White, 0.12);
            var disabled = Blend(accentColor, Colors.Gray, 0.55);
            // 电平覆盖在强调色轨道上：深色模式提亮，浅色模式压暗。
            // 自定义色若已接近白/黑，则反向混合，避免 Peak 与音量填充同色。
            int accentBrightness = (accentColor.R * 299 + accentColor.G * 587 + accentColor.B * 114) / 1000;
            bool nearExtreme = dark ? accentBrightness >= 220 : accentBrightness <= 40;
            var peakTarget = dark
                ? (nearExtreme ? Colors.Black : Colors.White)
                : (nearExtreme ? Colors.White : Colors.Black);
            var peak = Blend(accentColor, peakTarget, nearExtreme ? 0.60 : (dark ? 0.82 : 0.68));

            void Set(string key, Color c)
            {
                var brush = GetBrush(c);
                if (!ReferenceEquals(Application.Current.Resources[key], brush))
                    Application.Current.Resources[key] = brush;
            }

            if (dark)
            {
                Set("Theme.WindowBg", Color.FromRgb(0x1E, 0x1E, 0x1E));
                Set("Theme.SurfaceBg", Color.FromRgb(0x2B, 0x2B, 0x2B));
                Set("Theme.SurfaceAlt", Color.FromRgb(0x25, 0x25, 0x25));
                Set("Theme.TextPrimary", Color.FromRgb(0xF3, 0xF4, 0xF6));
                Set("Theme.TextSecondary", Color.FromRgb(0x9C, 0xA3, 0xAF));
                Set("Theme.Border", Color.FromRgb(0x3C, 0x3C, 0x3C));
                Set("Theme.NavHover", Color.FromRgb(0x33, 0x33, 0x33));
                Set("Theme.NavSelected", Blend(accentColor, Colors.Black, 0.70));
                Set("Theme.SliderBg", Color.FromRgb(0x4B, 0x4B, 0x4B));
                Set("Theme.Overlay", Blend(accentColor, Colors.Black, 0.78));
                Set("Theme.SurfaceCard", Color.FromRgb(0x23, 0x23, 0x23));
            }
            else
            {
                Set("Theme.WindowBg", Color.FromRgb(0xF7, 0xF8, 0xFA));
                Set("Theme.SurfaceBg", Color.FromRgb(0xFF, 0xFF, 0xFF));
                Set("Theme.SurfaceAlt", Color.FromRgb(0xF1, 0xF3, 0xF6));
                Set("Theme.TextPrimary", Color.FromRgb(0x1F, 0x29, 0x37));
                Set("Theme.TextSecondary", Color.FromRgb(0x6B, 0x72, 0x80));
                Set("Theme.Border", Color.FromRgb(0xE5, 0xE7, 0xEB));
                Set("Theme.NavHover", Color.FromRgb(0xED, 0xF0, 0xF5));
                Set("Theme.NavSelected", Blend(accentColor, Colors.White, 0.86));
                Set("Theme.SliderBg", Color.FromRgb(0xD8, 0xDC, 0xE2));
                Set("Theme.Overlay", Blend(accentColor, Colors.White, 0.90));
                Set("Theme.SurfaceCard", Color.FromRgb(0xFA, 0xFB, 0xFC));
            }

            Set("Theme.Accent", accentColor);
            var osdBackground = dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White;
            var osdText = dark ? Color.FromRgb(0xF3, 0xF4, 0xF6) : Color.FromRgb(0x1F, 0x29, 0x37);
            Set("Theme.OsdValue", osdText);
            Set("Theme.OsdTitle", Blend(osdText, osdBackground, 0.18));
            Set("Theme.OsdAccent", OsdAccent(accentColor, osdBackground, dark));
            Set("Theme.AccentPeak", peak);
            Set("Theme.AccentHover", hover);
            Set("Theme.AccentPressed", pressed);
            Set("Theme.AccentDisabled", disabled);
            Set("Theme.OnAccent", Color.FromRgb(0xFF, 0xFF, 0xFF));
            Set("Theme.Success", Color.FromRgb(0x16, 0xA3, 0x4A));
            Set("Theme.Fail", Color.FromRgb(0xDC, 0x26, 0x26));

            // 透明度重新应用到最新背景色（主题切换后保持用户设置的透明度）
            ApplyBackgroundOpacity(_lastOpacity);
        }

        private static int _lastOpacity = 96;

        // 只在主题变化时计算；对比基准为主题底色，桌面透过背景时仍受用户透明度影响。
        private static Color OsdAccent(Color accent, Color background, bool dark)
        {
            double Luminance(Color color)
            {
                double Linear(byte channel)
                {
                    double value = channel / 255.0;
                    return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
                }
                return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            }
            double backgroundLight = Luminance(background);
            Color result = accent;
            for (int i = 0; i <= 10; i++)
            {
                double light = Luminance(result);
                if ((Math.Max(light, backgroundLight) + 0.05) / (Math.Min(light, backgroundLight) + 0.05) >= 3) break;
                result = Blend(accent, dark ? Colors.White : Colors.Black, Math.Min(1, (i + 1) / 10.0));
            }
            return result;
        }

        /// <summary>
        /// 按百分比（0–100）把当前窗口/卡片背景色加上 alpha 写入 Theme.WindowBgAlpha /
        /// Theme.SurfaceBgAlpha，窗口背景、导航与卡片绑定后即可整体实现背景透明度。
        /// </summary>
        public static void ApplyBackgroundOpacity(int percent)
        {
            percent = MathEx.Clamp(percent, 0, 100);
            _lastOpacity = percent;
            byte a = (byte)(255 * percent / 100);
            if (Application.Current?.Resources["Theme.WindowBg"] is SolidColorBrush bg)
            {
                var c = bg.Color;
                var brush = GetBrush(Color.FromArgb(a, c.R, c.G, c.B));
                if (!ReferenceEquals(Application.Current.Resources["Theme.WindowBgAlpha"], brush))
                    Application.Current.Resources["Theme.WindowBgAlpha"] = brush;
            }
            if (Application.Current?.Resources["Theme.SurfaceBg"] is SolidColorBrush sb)
            {
                var c = sb.Color;
                var brush = GetBrush(Color.FromArgb(a, c.R, c.G, c.B));
                if (!ReferenceEquals(Application.Current.Resources["Theme.SurfaceBgAlpha"], brush))
                    Application.Current.Resources["Theme.SurfaceBgAlpha"] = brush;
            }
        }

        /// <summary>当前强调色的 RGB 反色画刷（用于"禁用自动切换"状态点等）。</summary>
        public static System.Windows.Media.Brush GetInvertedAccentBrush()
        {
            try
            {
                if (Application.Current?.Resources["Theme.Accent"] is SolidColorBrush b)
                {
                    var c = b.Color;
                    return GetBrush(Color.FromRgb(
                        (byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B)));
                }
            }
            catch
            {
                // 取色失败回退透明
            }
            return System.Windows.Media.Brushes.Transparent;
        }

        private static Color Blend(Color a, Color b, double t)
        {
            byte C(byte x, byte y) => (byte)MathEx.Clamp((int)Math.Round(x * (1 - t) + y * t), 0, 255);
            return Color.FromRgb(C(a.R, b.R), C(a.G, b.G), C(a.B, b.B));
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SonicRoute
{
    /// <summary>应用图标：优先使用内嵌资源 SonicRoute.png（托盘 + 窗口），失败时回退到运行时绘制图标。
    /// 支持浅色版（白色重绘）：深色任务栏下黑色图标看不见，自动切换为保留原形状的白色图标。</summary>
    internal static class IconFactory
    {
        /// <summary>任务栏是否为深色模式（SystemUsesLightTheme=0）。托盘图标应跟随任务栏主题而非应用主题。</summary>
        public static bool IsTaskbarDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("SystemUsesLightTheme") is int v)
                    return v == 0;
            }
            catch { }
            return false;
        }

        private static int GetTrayIconSize()
        {
            const int SM_CXSMICON = 49;
            int px = GetSystemMetrics(SM_CXSMICON) * 2;
            return px < 32 ? 32 : (px > 64 ? 64 : px);
        }

        /// <param name="light">true = 浅色版（白色重绘，用于深色任务栏）；false = 原色图标。</param>
        public static Icon CreateAppIcon(bool light = false)
        {
            int iconPx = GetTrayIconSize();
            try
            {
                var info = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/SonicRoute.png"));
                if (info != null)
                {
                    using var stream = info.Stream;
                    // 按托盘实际小图标尺寸直接解码（32-64px），避免把 1024px 原图整张解码为约 4MB GDI+ 位图。
                    var decoded = new BitmapImage();
                    decoded.BeginInit();
                    decoded.CacheOption = BitmapCacheOption.OnLoad;
                    decoded.DecodePixelWidth = iconPx;
                    decoded.StreamSource = stream;
                    decoded.EndInit();
                    decoded.Freeze();

                    BitmapSource source = decoded.Format == PixelFormats.Bgra32
                        ? decoded
                        : new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
                    int width = source.PixelWidth;
                    int height = source.PixelHeight;
                    int stride = width * 4;
                    byte[] pixels = new byte[stride * height];
                    source.CopyPixels(pixels, stride, 0);
                    if (light)
                    {
                        // 浅色版：保留原图 alpha 通道，所有非透明像素改为白色。
                        // 原图标为透明底深色轮廓，白色重绘后深色任务栏上清晰可见。
                        for (int i = 0; i < pixels.Length; i += 4)
                        {
                            if (pixels[i + 3] > 0)
                            {
                                pixels[i] = 255;
                                pixels[i + 1] = 255;
                                pixels[i + 2] = 255;
                            }
                        }
                    }

                    using var small = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var data = small.LockBits(new Rectangle(0, 0, width, height),
                        ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        if (data.Stride == stride)
                            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                        else
                        {
                            for (int y = 0; y < height; y++)
                                Marshal.Copy(pixels, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
                        }
                    }
                    finally
                    {
                        small.UnlockBits(data);
                    }
                    return CreateOwnedIcon(small);
                }
            }
            catch
            {
                // 资源加载失败时回退到绘制图标
            }
            return CreateFallbackIcon(light, iconPx);
        }

        private static Icon CreateFallbackIcon(bool light, int size)
        {
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);

                // 圆角蓝底（浅色版用深色底，保证对比度）
                using var path = RoundedRect(new Rectangle(1, 1, size - 2, size - 2), size / 5);
                using (var bg = new SolidBrush(light
                    ? System.Drawing.Color.FromArgb(220, 220, 220)
                    : System.Drawing.Color.FromArgb(37, 99, 235)))
                    g.FillPath(bg, path);

                // 白色扬声器：喇叭锥体 + 圆
                using var white = new SolidBrush(System.Drawing.Color.White);
                float cx = size / 2f;
                float cy = size / 2f;
                float r = size * 0.16f;

                // 圆（振膜）
                g.FillEllipse(white, cx - r, cy - r, r * 2, r * 2);

                // 喇叭锥体（指向左下）
                var cone = new PointF[]
                {
                    new(cx - r * 0.6f, cy - r * 1.15f),
                    new(cx - r * 0.6f, cy + r * 1.15f),
                    new(cx - r * 3.0f, cy + r * 1.8f),
                    new(cx - r * 3.0f, cy - r * 1.8f),
                };
                g.FillPolygon(white, cone);

                // 声波弧线（右上）
                using var pen = new System.Drawing.Pen(System.Drawing.Color.White, size * 0.12f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                float arcX = cx + r * 1.3f;
                g.DrawArc(pen, arcX, cy - r * 1.6f, r * 1.4f, r * 1.4f, -55, 110);
                g.DrawArc(pen, arcX + r * 0.7f, cy - r * 2.3f, r * 2.0f, r * 2.0f, -60, 120);
            }

            return CreateOwnedIcon(bmp);
        }

        private static Icon CreateOwnedIcon(Bitmap bitmap)
        {
            IntPtr hicon = bitmap.GetHicon();
            try
            {
                // Icon.FromHandle 不拥有 HICON，Dispose 不会 DestroyIcon；Clone 后释放临时句柄避免 GDI 泄漏。
                using var borrowed = Icon.FromHandle(hicon);
                return (Icon)borrowed.Clone();
            }
            finally
            {
                DestroyIcon(hicon);
            }
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}

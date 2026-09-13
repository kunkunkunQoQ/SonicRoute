using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace SonicRoute
{
    /// <summary>应用图标：优先使用内嵌资源 SonicRoute.png（托盘 + 窗口），失败时回退到运行时绘制图标。</summary>
    internal static class IconFactory
    {
        public static Icon CreateAppIcon()
        {
            try
            {
                var info = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/SonicRoute.png"));
                if (info != null)
                {
                    using var stream = info.Stream;
                    using var bmp = new Bitmap(stream);
                    // 托盘图标只需 32px，避免把 1024px 大图整张转成 HICON 浪费 GDI/内存
                    using var small = new Bitmap(bmp, new Size(32, 32));
                    var hicon = small.GetHicon();
                    return Icon.FromHandle(hicon);
                }
            }
            catch
            {
                // 资源加载失败时回退到绘制图标
            }
            return CreateFallbackIcon();
        }

        private static Icon CreateFallbackIcon()
        {
            const int size = 32;
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // 圆角蓝底
                using var path = RoundedRect(new Rectangle(1, 1, size - 2, size - 2), size / 5);
                using (var bg = new SolidBrush(Color.FromArgb(37, 99, 235)))
                    g.FillPath(bg, path);

                // 白色扬声器：喇叭锥体 + 圆
                using var white = new SolidBrush(Color.White);
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
                using var pen = new Pen(Color.White, size * 0.12f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                float arcX = cx + r * 1.3f;
                g.DrawArc(pen, arcX, cy - r * 1.6f, r * 1.4f, r * 1.4f, -55, 110);
                g.DrawArc(pen, arcX + r * 0.7f, cy - r * 2.3f, r * 2.0f, r * 2.0f, -60, 120);
            }

            var hicon = bmp.GetHicon();
            return Icon.FromHandle(hicon);
        }

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

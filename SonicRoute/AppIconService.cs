using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SonicRoute
{
    /// <summary>按进程提取应用图标（从 exe 路径提取，带缓存）。</summary>
    public static class AppIconService
    {
        /// <summary>图标缓存上限：防止面板/完整界面反复枚举应用导致 BitmapSource 无限增长（内存优化 A1）。</summary>
        private const int MaxCacheSize = 256;

        private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();

        /// <summary>取 PID 对应进程的图标；失败时返回 null（调用方显示默认图标）。</summary>
        public static ImageSource? GetIconForPid(int pid)
        {
            string? exe = null;
            try
            {
                using var p = Process.GetProcessById(pid);
                exe = p.MainModule?.FileName;
            }
            catch { /* 系统/提升进程可能无权限 */ }

            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;
            var src = Cache.GetOrAdd(exe, path =>
            {
                try
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon == null) return null;
                    using var bmp = icon.ToBitmap();
                    var hbmp = bmp.GetHbitmap();
                    try
                    {
                        var img = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero,
                            Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24));
                        img.Freeze();
                        return img;
                    }
                    finally
                    {
                        DeleteObject(hbmp);
                    }
                }
                catch { return null; }
            });
            TrimIfNeeded();
            return src;
        }

        /// <summary>超出上限时按最旧条目（FIFO）淘汰，控制缓存常驻内存。枚举顺序不保证严格 LRU，但可有效封顶。</summary>
        private static void TrimIfNeeded()
        {
            int over = Cache.Count - MaxCacheSize;
            if (over <= 0) return;
            foreach (var kv in Cache)
            {
                if (over <= 0) break;
                if (Cache.TryRemove(kv.Key, out _)) over--;
            }
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}

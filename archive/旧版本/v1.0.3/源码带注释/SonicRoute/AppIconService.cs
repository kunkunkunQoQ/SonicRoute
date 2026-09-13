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
            return Cache.GetOrAdd(exe, path =>
            {
                try
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon == null) return null;
                    using var bmp = icon.ToBitmap();
                    var hbmp = bmp.GetHbitmap();
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero,
                            Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24));
                        src.Freeze();
                        return src;
                    }
                    finally
                    {
                        DeleteObject(hbmp);
                    }
                }
                catch { return null; }
            });
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}

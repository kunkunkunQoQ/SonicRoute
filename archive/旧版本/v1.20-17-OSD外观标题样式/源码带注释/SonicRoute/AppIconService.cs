using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
        private const int IdleCacheSize = 32;
        private static int _cacheLimit = MaxCacheSize;
        private static long _accessSequence;

        private sealed class CacheEntry
        {
            public readonly Lazy<ImageSource?> Image;
            public long LastUsed;
            public CacheEntry(string path) { Image = new Lazy<ImageSource?>(() => ExtractIcon(path)); }
        }

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

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
            return GetOrExtract(exe);
        }

        /// <summary>取指定 EXE 的图标（自动化「打开方式」候选应用使用，与进程图标共用缓存）；失败返回 null。</summary>
        public static ImageSource? GetIconForPath(string? exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;
            return GetOrExtract(exePath);
        }

        private static ImageSource? GetOrExtract(string exe)
        {
            var entry = Cache.GetOrAdd(exe, path => new CacheEntry(path));
            Interlocked.Exchange(ref entry.LastUsed, Interlocked.Increment(ref _accessSequence));
            // 相同路径的并发请求共用一次提取，避免 GetOrAdd 工厂重复提取位图。
            var src = entry.Image.Value;
            TrimIfNeeded();
            return src;
        }

        private static ImageSource? ExtractIcon(string path)
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
                finally { DeleteObject(hbmp); }
            }
            catch { return null; }
        }

        /// <summary>按最近访问顺序淘汰；UI 关闭后最多保留 32 个冻结图标，减少下次打开时的提取。</summary>
        private static void TrimIfNeeded()
        {
            int over = Cache.Count - Volatile.Read(ref _cacheLimit);
            if (over <= 0) return;
            foreach (var kv in Cache.OrderBy(pair => Interlocked.Read(ref pair.Value.LastUsed)))
            {
                if (over <= 0) break;
                if (Cache.TryRemove(kv.Key, out _)) over--;
            }
        }

        internal static void Resume() => Volatile.Write(ref _cacheLimit, MaxCacheSize);

        internal static void TrimForIdle()
        {
            Volatile.Write(ref _cacheLimit, IdleCacheSize);
            TrimIfNeeded();
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        /// <summary>显式清空全部图标缓存；通常关闭 UI 使用 TrimForIdle 保留少量常用图标。</summary>
        public static void Clear()
        {
            Cache.Clear();
        }
    }
}

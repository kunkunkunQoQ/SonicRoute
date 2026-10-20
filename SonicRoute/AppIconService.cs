using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonicRoute.Core;
using Microsoft.Win32.SafeHandles;

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
            public CacheEntry(string key)
            {
                Image = new Lazy<ImageSource?>(() => key.StartsWith("png|", StringComparison.Ordinal)
                    ? CustomAppIconStore.Load(key.Substring(4)) : ExtractIcon(key.Substring(4)));
            }
        }

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static ImageSource? GetIconForApp(int pid, string processName)
        {
            string? custom = AppIconFiles.GetFileName(ConfigService.Load(), processName);
            if (custom != null)
            {
                var image = GetOrExtract("png|" + custom);
                if (image != null) return image;
            }
            return GetIconForPid(pid);
        }

        internal static void InvalidateCustom(string? fileName)
        {
            if (fileName != null) Cache.TryRemove("png|" + fileName, out _);
        }

        /// <summary>取 PID 对应进程的图标；失败时返回 null（调用方显示默认图标）。</summary>
        public static ImageSource? GetIconForPid(int pid)
        {
            if (pid <= 0) return null;
            // PR #6: query the image path without requesting access to process modules.
            using var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle.IsInvalid) return null;
            var path = new StringBuilder(1024);
            int length = path.Capacity;
            if (QueryFullProcessImageName(handle, 0, path, ref length)) return GetIconForPath(path.ToString());
            if (Marshal.GetLastWin32Error() != 122) return null;
            path.EnsureCapacity(32768);
            length = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref length) ? GetIconForPath(path.ToString()) : null;
        }

        /// <summary>取指定 EXE 的图标（自动化「打开方式」候选应用使用，与进程图标共用缓存）；失败返回 null。</summary>
        public static ImageSource? GetIconForPath(string? exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;
            return GetOrExtract("exe|" + exePath);
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
                var image = FromIcon(icon);
                // Only automatic EXE icons are filtered; explicit user imports remain authoritative.
                var pixels = Pixels(image);
                foreach (var reference in DefaultIconPixels.Value)
                    if (pixels.SequenceEqual(reference)) return null;
                return image;
            }
            catch { return null; }
        }

        private static readonly Lazy<byte[][]> DefaultIconPixels = new(() =>
        {
            var references = new System.Collections.Generic.List<byte[]>(4);
            AddDefaultIcon(references, System.Drawing.SystemIcons.Application);
            var stock = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>() };
            if (SHGetStockIconInfo(2, 0x100, ref stock) != 0 || stock.Icon == IntPtr.Zero) return references.ToArray();
            try
            {
                using var icon = System.Drawing.Icon.FromHandle(stock.Icon);
                AddDefaultIcon(references, icon);
                return references.ToArray();
            }
            finally { DestroyIcon(stock.Icon); }
        });

        private static void AddDefaultIcon(System.Collections.Generic.List<byte[]> references, System.Drawing.Icon icon)
        {
            references.Add(Pixels(FromIcon(icon)));
            // PE/icon-file decoding may expose a legacy mask variant of the same stock icon.
            using var encoded = new MemoryStream();
            icon.Save(encoded);
            encoded.Position = 0;
            using var decoded = new System.Drawing.Icon(encoded);
            references.Add(Pixels(FromIcon(decoded)));
        }

        private static byte[] Pixels(BitmapSource image)
        {
            if (image.Format != PixelFormats.Bgra32) image = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(pixels, image.PixelWidth * 4, 0);
            return pixels;
        }

        private static BitmapSource FromIcon(System.Drawing.Icon icon)
        {
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StockIconInfo
        {
            public uint Size;
            public IntPtr Icon;
            public int ImageIndex, IconIndex;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? Path;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetStockIconInfo(uint id, uint flags, ref StockIconInfo info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const int FailedCacheMilliseconds = 5000;
        private static int _cacheLimit = MaxCacheSize;
        private static long _accessSequence;

        private sealed class CacheEntry
        {
            public readonly Lazy<ImageSource?> Image;
            public long LastUsed;
            public int FailedAt;
            public CacheEntry(string key)
            {
                Image = new Lazy<ImageSource?>(() =>
                {
                    var image = key.StartsWith("png|", StringComparison.Ordinal)
                        ? CustomAppIconStore.Load(key.Substring(4)) : ExtractIcon(key.Substring(4));
                    if (image == null) Volatile.Write(ref FailedAt, Environment.TickCount);
                    return image;
                });
            }
        }

        private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static ImageSource? GetIconForApp(int pid, string processName) => GetIconForApp(pid, processName, null);

        public static ImageSource? GetIconForApp(int pid, string processName, string? sessionIconPath)
        {
            string? custom = AppIconFiles.GetFileName(ConfigService.Load(), processName);
            if (custom != null)
            {
                var image = GetOrExtract("png|" + custom);
                if (image != null) return image;
            }
            // WASAPI can supply an icon for a protected audio service without any GUI window.
            // It is metadata, never a command; share the existing file extraction/cache.
            if (GetIconForPath(sessionIconPath) is ImageSource sessionIcon) return sessionIcon;
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
            if (!handle.IsInvalid)
            {
                var path = new StringBuilder(1024);
                int length = path.Capacity;
                bool found = QueryFullProcessImageName(handle, 0, path, ref length);
                if (!found && Marshal.GetLastWin32Error() == 122)
                {
                    path.EnsureCapacity(32768);
                    length = path.Capacity;
                    found = QueryFullProcessImageName(handle, 0, path, ref length);
                }
                if (found && GetIconForPath(path.ToString()) is ImageSource image) return image;
            }
            // Some elevated applications deny even limited process queries. Window icons do not
            // require opening their executable/process. Never guess a path from a process name.
            return GetWindowIcon(pid);
        }

        private static ImageSource? GetWindowIcon(int pid)
        {
            var windows = new List<IntPtr>();
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out uint owner);
                if (owner == (uint)pid) windows.Add(window);
                return true;
            }, IntPtr.Zero);

            // Prefer visible application windows over hidden helper windows. All work is on demand
            // on the existing icon worker; a hung window gets a bounded share of a 200 ms budget.
            var budget = Stopwatch.StartNew();
            foreach (var window in windows.OrderByDescending(IsWindowVisible))
            {
                if (budget.ElapsedMilliseconds >= 200) break;
                GetWindowThreadProcessId(window, out uint owner);
                if (owner != (uint)pid) continue;
                // A missing small class icon may be synthesized by Windows from its generic
                // large icon; prefer the original large icon rather than that generated variant.
                var classIcon = GetClassLongPtr(window, -14);
                if (classIcon == IntPtr.Zero) classIcon = GetClassLongPtr(window, -34);
                var image = CopyWindowIcon(classIcon);
                GetWindowThreadProcessId(window, out owner);
                if (owner != (uint)pid) continue;
                if (image != null) return image;
                // ICON_BIG / ICON_SMALL only. ICON_SMALL2 can synthesize a stock icon.
                for (int kind = 1; kind >= 0; kind--)
                {
                    int remaining = 200 - (int)budget.ElapsedMilliseconds;
                    if (remaining <= 0) return null;
                    // SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT; no unbounded SendMessage.
                    if (SendMessageTimeout(window, 0x007F, new IntPtr(kind), IntPtr.Zero, 0x23,
                        (uint)Math.Min(50, remaining), out var icon) == IntPtr.Zero) break;
                    GetWindowThreadProcessId(window, out owner);
                    if (owner != (uint)pid) break;
                    image = CopyWindowIcon(icon);
                    if (image != null) return image;
                }
            }
            return null;
        }

        private static ImageSource? CopyWindowIcon(IntPtr source)
        {
            if (source == IntPtr.Zero) return null;
            var copy = CopyIcon(source);
            if (copy == IntPtr.Zero) return null;
            try
            {
                var image = FromNativeIcon(copy);
                return IsDefaultIcon(image) ? null : image;
            }
            catch { return null; } // The target can exit while its window/icon is being read.
            finally { DestroyIcon(copy); } // Never destroy an icon owned by another application.
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
            // Keep misses briefly to coalesce repeated UI requests, but never poison the cache
            // for the entire session after a temporary file lock/permission failure. No timer.
            if (entry.Image.IsValueCreated && entry.Image.Value == null
                && unchecked((uint)(Environment.TickCount - Volatile.Read(ref entry.FailedAt))) >= FailedCacheMilliseconds)
            {
                var replacement = new CacheEntry(exe);
                entry = Cache.TryUpdate(exe, replacement, entry) ? replacement
                    : Cache.GetOrAdd(exe, path => new CacheEntry(path));
            }
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
                return IsDefaultIcon(image) ? null : image;
            }
            catch { return null; }
        }

        private static bool IsDefaultIcon(BitmapSource image)
        {
            var pixels = Pixels(image);
            return DefaultIconPixels.Value.Any(reference => pixels.SequenceEqual(reference));
        }

        private static readonly Lazy<byte[][]> DefaultIconPixels = new(() =>
        {
            var references = new List<byte[]>(7);
            AddDefaultIcon(references, System.Drawing.SystemIcons.Application);
            // Windows' shared IDI_APPLICATION can differ from SystemIcons.Application.
            var shared = LoadIcon(IntPtr.Zero, new IntPtr(32512));
            if (shared != IntPtr.Zero) references.Add(Pixels(FromNativeIcon(shared)));
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

        private static void AddDefaultIcon(List<byte[]> references, System.Drawing.Icon icon)
        {
            references.Add(Pixels(FromIcon(icon)));
            references.Add(Pixels(FromNativeIcon(icon.Handle)));
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

        private static BitmapSource FromNativeIcon(IntPtr icon)
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(24, 24));
            image.Freeze();
            return image;
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

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        private static extern IntPtr GetClassLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll")]
        private static extern IntPtr CopyIcon(IntPtr icon);

        [DllImport("user32.dll", EntryPoint = "LoadIconW")]
        private static extern IntPtr LoadIcon(IntPtr instance, IntPtr resource);

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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.Core;

namespace SonicRoute.Native.Ui;

/// <summary>
/// Non-blocking Win32 + WIC app icon bitmap cache producing premultiplied BGRA32 bitmaps for Direct2D.
/// Supports custom imported icons, 5-second negative caching, elevated WM_GETICON fallback, and stock-icon filtering.
/// Bounded to MaxEntries with zero GDI+/System.Drawing usage.
/// </summary>
internal static class NativeAppIconService
{
    public readonly record struct IconBitmap(byte[] BgraPixels, int Width, int Height);

    private const int MaxEntries = 96;
    private const long NegativeCacheMs = 5000;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, IconBitmap> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> NegativeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Order = new();
    private static readonly HashSet<string> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static IconBitmap? _brandIcon;
    private static int? _stockAppIconHash;

    public static IconBitmap? GetBrandIcon()
    {
        if (_brandIcon is not null) return _brandIcon;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SonicRoute.SonicRoute.png");
            if (stream is null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            byte[] png = ms.ToArray();
            if (UiBitmap.TryDecodePng(png, 48, out byte[] pixels, out int w, out int h))
            {
                _brandIcon = new IconBitmap(pixels, w, h);
                return _brandIcon;
            }
        }
        catch { }
        return null;
    }

    public static void InvalidateCustomIcon(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;
        lock (Gate)
        {
            string key = "custom:" + fileName.Trim();
            if (Cache.Remove(key))
                for (var node = Order.First; node != null; node = node.Next)
                    if (StringComparer.OrdinalIgnoreCase.Equals(node.Value, key)) { Order.Remove(node); break; }
            NegativeCache.Remove(key);
        }
    }

    public static IconBitmap? GetAppIcon(AppConfig? config, int pid, string? iconPath, string? processName, Action? onLoaded = null)
    {
        if (config != null && !string.IsNullOrWhiteSpace(processName)
            && AppIconFiles.GetFileName(config, processName!) is { } customFile)
        {
            string cKey = "custom:" + customFile;
            lock (Gate)
            {
                if (Cache.TryGetValue(cKey, out var cachedCustom)) return cachedCustom;
                if (NegativeCache.TryGetValue(cKey, out long negAt) && Environment.TickCount64 - negAt < NegativeCacheMs)
                    goto FallbackNative;
                if (InFlight.Add(cKey))
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            byte[] png = AppIconFiles.ReadPng(customFile);
                            if (UiBitmap.TryDecodePng(png, 64, out byte[] pixels, out int w, out int h))
                            {
                                PutCache(cKey, new IconBitmap(pixels, w, h));
                                onLoaded?.Invoke();
                            }
                            else PutNegative(cKey);
                        }
                        catch { PutNegative(cKey); }
                        finally { lock (Gate) InFlight.Remove(cKey); }
                    });
                }
            }
        }

    FallbackNative:
        return GetAppIcon(pid, iconPath, processName, onLoaded);
    }

    public static IconBitmap? GetAppIcon(int pid, string? processPath, string? processName, Action? onLoaded = null)
    {
        string key = !string.IsNullOrWhiteSpace(processPath) ? "path:" + processPath.Trim()
            : pid > 0 ? $"pid:{pid}:{processName}" : (!string.IsNullOrWhiteSpace(processName) ? "name:" + processName.Trim() : "");
        if (string.IsNullOrEmpty(key)) return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            if (NegativeCache.TryGetValue(key, out long failAt) && Environment.TickCount64 - failAt < NegativeCacheMs)
                return null;
            if (!InFlight.Add(key)) return null;
        }

        string? directPath = !string.IsNullOrWhiteSpace(processPath) ? processPath.Trim() : null;
        _ = Task.Run(() =>
        {
            try
            {
                string? exe = directPath ?? (pid > 0 ? TryResolveProcessPath(pid) : null);
                IconBitmap? bmp = null;
                if (!string.IsNullOrWhiteSpace(exe) && TryExtractFileIcon(exe, out var fileBmp) && !IsStockDefaultIcon(fileBmp))
                    bmp = fileBmp;
                else if (pid > 0 && TryExtractWindowIcon(pid, out var winBmp))
                    bmp = winBmp;

                if (bmp is { } resolved)
                {
                    PutCache(key, resolved);
                    onLoaded?.Invoke();
                }
                else
                {
                    PutNegative(key);
                }
            }
            catch
            {
                PutNegative(key);
            }
            finally
            {
                lock (Gate) InFlight.Remove(key);
            }
        });
        return null;
    }

    private static void PutCache(string key, IconBitmap bmp)
    {
        lock (Gate)
        {
            NegativeCache.Remove(key);
            if (Cache.ContainsKey(key)) { Cache[key] = bmp; return; }
            if (Cache.Count == MaxEntries && Order.First is { } oldest)
            {
                Cache.Remove(oldest.Value);
                Order.RemoveFirst();
            }
            Cache[key] = bmp;
            Order.AddLast(key);
        }
    }

    private static void PutNegative(string key)
    {
        lock (Gate)
        {
            if (NegativeCache.Count == MaxEntries && !NegativeCache.ContainsKey(key))
            {
                string? oldest = null;
                long oldestTime = long.MaxValue;
                foreach (var failure in NegativeCache)
                    if (failure.Value < oldestTime) { oldest = failure.Key; oldestTime = failure.Value; }
                if (oldest != null) NegativeCache.Remove(oldest);
            }
            NegativeCache[key] = Environment.TickCount64;
        }
    }

    private static string? TryResolveProcessPath(int pid)
    {
        nint hProc = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, (uint)pid);
        if (hProc == 0) return null;
        try
        {
            int cap = 512;
            while (cap <= 4096)
            {
                char[] buf = new char[cap];
                uint len = (uint)buf.Length;
                if (QueryFullProcessImageNameW(hProc, 0, buf, ref len) && len > 0)
                    return new string(buf, 0, (int)len);
                if (Marshal.GetLastWin32Error() != 122 /*ERROR_INSUFFICIENT_BUFFER*/) break;
                cap *= 2;
            }
        }
        finally
        {
            CloseHandle(hProc);
        }
        return null;
    }

    private static bool TryExtractFileIcon(string path, out IconBitmap result)
    {
        result = default;
        if (!File.Exists(path)) return false;

        var shfi = new ShFileInfo();
        const uint flags = 0x000000100 /*SHGFI_ICON*/ | 0x000000000 /*SHGFI_LARGEICON*/;
        if (SHGetFileInfoW(path, 0, ref shfi, (uint)Marshal.SizeOf<ShFileInfo>(), flags) == 0 || shfi.HIcon == 0)
            return false;

        try { return IconToPremultipliedBgra(shfi.HIcon, out result); }
        finally { DestroyIcon(shfi.HIcon); }
    }

    private static bool TryExtractWindowIcon(int pid, out IconBitmap result)
    {
        result = default;
        nint foundHwnd = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint wPid);
            if ((int)wPid == pid && IsWindowVisible(h))
            {
                foundHwnd = h;
                return false;
            }
            return true;
        }, 0);
        if (foundHwnd == 0) return false;

        nint hIcon = 0;
        if (SendMessageTimeoutW(foundHwnd, 0x007F /*WM_GETICON*/, 1 /*ICON_BIG*/, 0, 2 /*SMTO_ABORTIFHUNG*/, 120, out nint big) != 0 && big != 0)
            hIcon = big;
        else if (SendMessageTimeoutW(foundHwnd, 0x007F, 0 /*ICON_SMALL*/, 0, 2, 120, out nint small) != 0 && small != 0)
            hIcon = small;
        else
            hIcon = GetClassLongPtrW(foundHwnd, -14 /*GCLP_HICON*/);

        if (hIcon == 0) return false;
        return IconToPremultipliedBgra(hIcon, out result);
    }

    private static bool IsStockDefaultIcon(IconBitmap bmp)
    {
        int stockHash = GetStockDefaultIconHash();
        if (stockHash == 0) return false;
        return ComputePixelsHash(bmp) == stockHash;
    }

    private static int GetStockDefaultIconHash()
    {
        if (_stockAppIconHash is int cached) return cached;
        int computed = 0;
        try
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            nint[] large = new nint[1];
            if (ExtractIconExW(Path.Combine(sys, "imageres.dll"), 11, large, null, 1) > 0 && large[0] != 0)
            {
                try
                {
                    if (IconToPremultipliedBgra(large[0], out var sb))
                        computed = ComputePixelsHash(sb);
                }
                finally { DestroyIcon(large[0]); }
            }
        }
        catch { }
        _stockAppIconHash = computed;
        return computed;
    }

    private static int ComputePixelsHash(IconBitmap bmp)
    {
        var hc = new HashCode();
        hc.Add(bmp.Width);
        hc.Add(bmp.Height);
        int step = Math.Max(4, bmp.BgraPixels.Length / 128);
        for (int i = 0; i < bmp.BgraPixels.Length; i += step)
            hc.Add(bmp.BgraPixels[i]);
        return hc.ToHashCode();
    }

    private static unsafe bool IconToPremultipliedBgra(nint hIcon, out IconBitmap result)
    {
        result = default;
        if (!GetIconInfo(hIcon, out var ii)) return false;
        try
        {
            nint colorBmp = ii.HbmColor;
            if (colorBmp == 0) return false;
            var bmpInfo = new BitmapStruct();
            if (GetObjectW(colorBmp, Marshal.SizeOf<BitmapStruct>(), ref bmpInfo) == 0) return false;
            int w = bmpInfo.BmWidth;
            int h = Math.Abs(bmpInfo.BmHeight);
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return false;

            byte[] pixels = new byte[w * h * 4];
            var bmi = new BitmapInfoHeader
            {
                BiSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                BiWidth = w,
                BiHeight = -h,
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = 0,
            };

            nint hdc = GetDC(0);
            try
            {
                fixed (byte* p = pixels)
                {
                    if (GetDIBits(hdc, colorBmp, 0, (uint)h, (nint)p, ref bmi, 0) == 0)
                        return false;
                }

                bool anyAlpha = false;
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0) { anyAlpha = true; break; }
                }

                if (!anyAlpha && ii.HbmMask != 0)
                {
                    byte[] mask = new byte[w * h * 4];
                    fixed (byte* pm = mask)
                    {
                        GetDIBits(hdc, ii.HbmMask, 0, (uint)h, (nint)pm, ref bmi, 0);
                    }
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        bool transparent = mask[i] > 128 && mask[i + 1] > 128 && mask[i + 2] > 128;
                        pixels[i + 3] = transparent ? (byte)0 : (byte)255;
                    }
                }
            }
            finally
            {
                ReleaseDC(0, hdc);
            }

            UiBitmap.Premultiply(pixels);
            result = new IconBitmap(pixels, w, h);
            return true;
        }
        finally
        {
            if (ii.HbmColor != 0) DeleteObject(ii.HbmColor);
            if (ii.HbmMask != 0) DeleteObject(ii.HbmMask);
        }
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint HIcon; public int IIcon; public uint DwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string SzDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string SzTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo { public bool FIcon; public uint XHotspot, YHotspot; public nint HbmMask, HbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapStruct { public int BmType, BmWidth, BmHeight, BmWidthBytes; public ushort BmPlanes, BmBitsPixel; public nint BmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint BiSize; public int BiWidth, BiHeight; public ushort BiPlanes, BiBitCount;
        public uint BiCompression, BiSizeImage; public int BiXPelsPerMeter, BiYPelsPerMeter; public uint BiClrUsed, BiClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint SHGetFileInfoW(string path, uint attrs, ref ShFileInfo psfi, uint cbSize, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int idx, nint[]? large, nint[]? small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint hIcon);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint hIcon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nuint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] private static extern nint GetClassLongPtrW(nint hwnd, int idx);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint obj, int size, ref BitmapStruct bmp);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint lines, nint bits, ref BitmapInfoHeader bmi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] exeName, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}

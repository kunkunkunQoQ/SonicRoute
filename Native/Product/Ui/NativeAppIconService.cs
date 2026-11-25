using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SonicRoute.Native.Ui;

/// <summary>
/// Pure Win32 + WIC app icon bitmap cache producing premultiplied BGRA32 bitmaps for Direct2D.
/// Bounded to MaxEntries with zero GDI+/System.Drawing usage.
/// </summary>
internal static class NativeAppIconService
{
    public readonly record struct IconBitmap(byte[] BgraPixels, int Width, int Height);

    private const int MaxEntries = 96;
    private static readonly Dictionary<string, IconBitmap> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> PidPathCache = new();
    private static readonly Dictionary<string, string> NamePathCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Order = new();
    private static IconBitmap? _brandIcon;

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
            if (SonicRoute.Native.Graphics.WicIconLoader.TryDecodePngToBgra32(png, 48, 48, out byte[] pixels, out int w, out int h))
            {
                Premultiply(pixels);
                _brandIcon = new IconBitmap(pixels, w, h);
                return _brandIcon;
            }
        }
        catch { }
        return null;
    }

    public static IconBitmap? GetAppIcon(int pid, string? processPath, string? processName)
    {
        string? resolvedPath = !string.IsNullOrWhiteSpace(processPath) ? processPath.Trim() : null;
        if (resolvedPath is null && pid > 0)
            resolvedPath = TryResolveProcessPath(pid, processName);
        if (resolvedPath is null && !string.IsNullOrWhiteSpace(processName))
            NamePathCache.TryGetValue(processName.Trim(), out resolvedPath);

        return GetAppIcon(resolvedPath, processName);
    }

    public static IconBitmap? GetAppIcon(string? processPath, string? processName)
    {
        string key = !string.IsNullOrWhiteSpace(processPath) ? processPath.Trim() : (processName ?? "").Trim();
        if (string.IsNullOrEmpty(key)) return null;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        if (string.IsNullOrWhiteSpace(processPath) && !string.IsNullOrWhiteSpace(processName))
            NamePathCache.TryGetValue(processName.Trim(), out processPath);

        if (!string.IsNullOrWhiteSpace(processPath) && TryExtractFileIcon(processPath, out var extracted))
        {
            PutCache(key, extracted);
            if (!string.IsNullOrWhiteSpace(processName)) PutCache(processName.Trim(), extracted);
            return extracted;
        }

        return null;
    }

    private static string? TryResolveProcessPath(int pid, string? processName)
    {
        if (PidPathCache.TryGetValue(pid, out var cachedPath)) return cachedPath;
        nint hProc = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, (uint)pid);
        if (hProc == 0) return null;
        try
        {
            char[] buf = new char[512];
            uint len = (uint)buf.Length;
            if (QueryFullProcessImageNameW(hProc, 0, buf, ref len) && len > 0)
            {
                string exe = new string(buf, 0, (int)len);
                if (PidPathCache.Count > 256) PidPathCache.Clear();
                PidPathCache[pid] = exe;
                if (!string.IsNullOrWhiteSpace(processName)) NamePathCache[processName.Trim()] = exe;
                return exe;
            }
        }
        finally
        {
            CloseHandle(hProc);
        }
        return null;
    }

    private static void PutCache(string key, IconBitmap bmp)
    {
        if (Cache.ContainsKey(key)) return;
        if (Cache.Count >= MaxEntries && Order.Count > 0)
        {
            Cache.Remove(Order.Dequeue());
        }
        Cache[key] = bmp;
        Order.Enqueue(key);
    }

    private static unsafe bool TryExtractFileIcon(string path, out IconBitmap result)
    {
        result = default;
        if (!File.Exists(path)) return false;

        var shfi = new ShFileInfo();
        nint flags = 0x000000100 /*SHGFI_ICON*/ | 0x000000000 /*SHGFI_LARGEICON*/;
        if (SHGetFileInfoW(path, 0, ref shfi, (uint)Marshal.SizeOf<ShFileInfo>(), (uint)flags) == 0 || shfi.HIcon == 0)
            return false;

        try
        {
            return IconToPremultipliedBgra(shfi.HIcon, out result);
        }
        finally
        {
            DestroyIcon(shfi.HIcon);
        }
    }

    private static unsafe bool IconToPremultipliedBgra(nint hIcon, out IconBitmap result)
    {
        result = default;
        var ii = new IconInfo();
        if (!GetIconInfo(hIcon, out ii)) return false;
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
                BiHeight = -h, // top-down
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

                // Synthesize alpha from mask when icon lacks 8-bit alpha channel
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

            Premultiply(pixels);
            result = new IconBitmap(pixels, w, h);
            return true;
        }
        finally
        {
            if (ii.HbmColor != 0) DeleteObject(ii.HbmColor);
            if (ii.HbmMask != 0) DeleteObject(ii.HbmMask);
        }
    }

    private static void Premultiply(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a == 255) continue;
            if (a == 0)
            {
                pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 0;
                continue;
            }
            pixels[i] = (byte)(pixels[i] * a / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
        }
    }

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
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint hIcon);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint hIcon, out IconInfo info);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint obj, int size, ref BitmapStruct bmp);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint lines, nint bits, ref BitmapInfoHeader bmi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] exeName, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}

using System.Runtime.InteropServices;
using SonicRoute.Native.Graphics;

namespace SonicRoute.Native.Product;

internal readonly record struct ViewRect(float X, float Y, float Width, float Height)
{
    internal bool Contains(float x, float y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

internal sealed unsafe class ViewCanvas : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private record struct Color(float R, float G, float B, float A);
    [StructLayout(LayoutKind.Sequential)] private record struct Bounds(float L, float T, float R, float B);
    [StructLayout(LayoutKind.Sequential)] private record struct Round(Bounds Box, float X, float Y);
    [StructLayout(LayoutKind.Sequential)] private record struct Extent(int Width, int Height);
    [StructLayout(LayoutKind.Sequential)] private record struct BitmapProperties(uint Format, uint Alpha, float DpiX, float DpiY);
    private readonly Dictionary<object, nint> bitmaps = new();
    private readonly Queue<object> bitmapOrder = new();
    private readonly DrawingFactory factory = new();
    private readonly bool layered;
    private nint target, brush;
    private int width, height;
    private uint density;

    internal ViewCanvas(bool layered = false) => this.layered = layered;

    internal bool Begin(nint window, int pixelsX, int pixelsY, uint dpi)
    {
        try
        {
            if (target == 0)
            {
                target = factory.CreateTarget(layered ? 0 : window, (uint)pixelsX, (uint)pixelsY, dpi);
                nint colorBrush = 0;
                Color ink = new(1, 1, 1, 1);
                NativeCom.Check(((delegate* unmanaged[Stdcall]<nint, Color*, nint, nint*, int>)NativeCom.Slot(target, 8))(target, &ink, 0, &colorBrush));
                brush = colorBrush;
                (width, height, density) = (pixelsX, pixelsY, dpi);
            }
            if (!layered && (width != pixelsX || height != pixelsY))
            {
                Extent size = new(pixelsX, pixelsY);
                NativeCom.Check(((delegate* unmanaged[Stdcall]<nint, Extent*, int>)NativeCom.Slot(target, 58))(target, &size));
                (width, height) = (pixelsX, pixelsY);
            }
            if (density != dpi)
            {
                ((delegate* unmanaged[Stdcall]<nint, float, float, void>)NativeCom.Slot(target, 51))(target, dpi, dpi);
                density = dpi;
            }
            if (layered)
            {
                WinRect bounds = new(0, 0, pixelsX, pixelsY);
                NativeCom.Check(((delegate* unmanaged[Stdcall]<nint, nint, WinRect*, int>)NativeCom.Slot(target, 57))(target, window, &bounds));
            }
            ((delegate* unmanaged[Stdcall]<nint, void>)NativeCom.Slot(target, 48))(target);
            return true;
        }
        catch (COMException error) when (error.HResult == DrawingSurface.RecreateTarget)
        {
            ReleaseTarget();
            return false;
        }
    }

    internal bool End()
    {
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)NativeCom.Slot(target, 49))(target, 0, 0);
        if (hr != DrawingSurface.RecreateTarget)
        {
            NativeCom.Check(hr);
            return true;
        }
        ReleaseTarget();
        return false;
    }

    internal void Clear(uint rgb, float opacity = 1)
    {
        Color color = Ink(rgb, opacity);
        ((delegate* unmanaged[Stdcall]<nint, Color*, void>)NativeCom.Slot(target, 47))(target, &color);
    }

    internal void Clip(ViewRect box)
    {
        Bounds bounds = Rect(box);
        ((delegate* unmanaged[Stdcall]<nint, Bounds*, uint, void>)NativeCom.Slot(target, 45))(target, &bounds, 1);
    }

    internal void Unclip() => ((delegate* unmanaged[Stdcall]<nint, void>)NativeCom.Slot(target, 46))(target);

    internal void Fill(ViewRect box, uint rgb, float radius = 0, float opacity = 1)
    {
        SetColor(rgb, opacity);
        if (radius > 0)
        {
            Round round = new(Rect(box), radius, radius);
            ((delegate* unmanaged[Stdcall]<nint, Round*, nint, void>)NativeCom.Slot(target, 19))(target, &round, brush);
            return;
        }
        Bounds rect = Rect(box);
        ((delegate* unmanaged[Stdcall]<nint, Bounds*, nint, void>)NativeCom.Slot(target, 17))(target, &rect, brush);
    }

    internal void Text(string value, ViewRect box, uint rgb)
    {
        if (value.Length == 0 || box.Width <= 0 || box.Height <= 0) return;
        SetColor(rgb, 1);
        Bounds bounds = Rect(box);
        fixed (char* text = value)
        {
            ((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, Bounds*, nint, uint, uint, void>)NativeCom.Slot(target, 27))
                (target, text, (uint)value.Length, factory.Format, &bounds, brush, 2, 0);
        }
    }

    private static Bounds Rect(ViewRect box) => new(box.X, box.Y, box.X + box.Width, box.Y + box.Height);
    private static Color Ink(uint color, float alpha) => new(((color >> 16) & 255) / 255f, ((color >> 8) & 255) / 255f, (color & 255) / 255f, alpha);
    private void SetColor(uint rgb, float opacity)
    {
        Color color = Ink(rgb, opacity);
        ((delegate* unmanaged[Stdcall]<nint, Color*, void>)NativeCom.Slot(brush, 8))(brush, &color);
    }

    internal bool Icon(object key, Func<byte[]?> load, ViewRect destination)
    {
        if (!bitmaps.TryGetValue(key, out nint bitmap))
        {
            bitmap = MakeBitmap(load);
            if (bitmapOrder.Count >= 24)
            {
                var oldest = bitmapOrder.Dequeue();
                if (bitmaps.Remove(oldest, out nint previous)) NativeCom.Release(ref previous);
            }
            bitmaps.Add(key, bitmap);
            bitmapOrder.Enqueue(key);
        }
        if (bitmap == 0) return false;
        Bounds box = Rect(destination);
        ((delegate* unmanaged[Stdcall]<nint, nint, Bounds*, float, uint, nint, void>)NativeCom.Slot(target, 26))
            (target, bitmap, &box, 1f, 1, 0);
        return true;
    }

    private nint MakeBitmap(Func<byte[]?> load)
    {
        try
        {
            if (load() is not { } png || !WicIconLoader.TryDecodePngToBgra32(png, 36, 36, out var pixels, out int w, out int h))
                return 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int a = pixels[i + 3];
                for (int channel = 0; channel < 3; channel++)
                    pixels[i + channel] = (byte)((pixels[i + channel] * a + 127) / 255);
            }
            nint bitmap = 0;
            BitmapProperties props = new(87, 1, 96f, 96f);
            fixed (byte* data = pixels)
                NativeCom.Check(((delegate* unmanaged[Stdcall]<nint, Extent, byte*, uint, BitmapProperties*, nint*, int>)NativeCom.Slot(target, 4))
                    (target, new Extent(w, h), data, (uint)w * 4, &props, &bitmap));
            return bitmap;
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException) { return 0; }
    }

    private void ReleaseTarget()
    {
        foreach (nint value in bitmaps.Values) { nint bitmap = value; NativeCom.Release(ref bitmap); }
        bitmaps.Clear();
        bitmapOrder.Clear();
        NativeCom.Release(ref brush);
        NativeCom.Release(ref target);
    }
    public void Dispose() { ReleaseTarget(); factory.Dispose(); }
}

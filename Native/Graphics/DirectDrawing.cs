using System.Runtime.InteropServices;

namespace SonicRoute.Native.Graphics;

// SDK interfaces from d2d1.h and dwrite.h. These entries use the fixed IUnknown ABI.
internal static unsafe class NativeCom
{
    internal static nint Slot(nint instance, int index) => (*(nint**)instance)[index];
    internal static void Release(ref nint value)
    {
        var p = value;
        value = 0;
        if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(p, 2))(p);
    }
    internal static void Check(int result)
    {
        if (result < 0) throw new COMException($"Native call returned 0x{result:X8}", result);
    }
}

internal unsafe sealed class DrawingFactory : IDisposable
{
    private nint _d2d, _write, _format;
    internal nint Format => _format;
    private static readonly Guid D2DFactory = new("06152247-6f50-465a-9245-118bfd3b6007");
    private static readonly Guid WriteFactory = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
    [DllImport("d2d1", ExactSpelling=true)] private static extern int D2D1CreateFactory(uint type, in Guid iid, nint options, out nint factory);
    [DllImport("dwrite", ExactSpelling=true)] private static extern int DWriteCreateFactory(uint type, in Guid iid, out nint factory);

    internal DrawingFactory()
    {
        try
        {
            NativeCom.Check(D2D1CreateFactory(0, in D2DFactory, 0, out _d2d));
            NativeCom.Check(DWriteCreateFactory(0, in WriteFactory, out _write));
            nint format = 0;
            fixed (char* family = "Microsoft YaHei UI", locale = "zh-CN")
                NativeCom.Check(((delegate* unmanaged[Stdcall]<nint, char*, nint, uint, uint, uint, float, char*, nint*, int>)NativeCom.Slot(_write, 15))
                    (_write, family, 0, 500, 0, 5, 16, locale, &format));
            _format = format;
        }
        catch { Dispose(); throw; }
    }

    internal nint CreateTarget(nint window, uint width, uint height, uint dpi)
    {
        TargetProperties props = new() { DpiX = dpi, DpiY = dpi };
        nint target = 0;
        if (window == 0)
        {
            props.Format = 87; // DXGI_FORMAT_B8G8R8A8_UNORM
            props.Alpha = 1;  // D2D1_ALPHA_MODE_PREMULTIPLIED, required for the OSD DIB
            NativeCom.Check(((delegate* unmanaged[Stdcall]<nint, TargetProperties*, nint*, int>)NativeCom.Slot(_d2d, 16))(_d2d, &props, &target));
        }
        else
        {
            HwndProperties hwnd = new() { Window = window, Width = width, Height = height };
            NativeCom.Check(((delegate* unmanaged[Stdcall]<nint, TargetProperties*, HwndProperties*, nint*, int>)NativeCom.Slot(_d2d, 14))
                (_d2d, &props, &hwnd, &target));
        }
        return target;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TargetProperties { public uint Type, Format, Alpha; public float DpiX, DpiY; public uint Usage, MinLevel; }
    [StructLayout(LayoutKind.Sequential)] private struct HwndProperties { public nint Window; public uint Width, Height, Options; }
    public void Dispose() { NativeCom.Release(ref _format); NativeCom.Release(ref _write); NativeCom.Release(ref _d2d); }
}

internal unsafe sealed class DrawingSurface(DrawingFactory factory, bool overlay) : IDisposable
{
    private nint _target, _ink, _accent;
    private int _width, _height;
    private uint _dpi;
    internal int Generation { get; private set; }
    internal int Recreated { get; private set; }
    internal bool HasDeviceResources => _target!=0 || _ink!=0 || _accent!=0;
    internal const int RecreateTarget = unchecked((int)0x8899000C);
    [StructLayout(LayoutKind.Sequential)] private struct Color(float r, float g, float b, float a) { public float R=r,G=g,B=b,A=a; }
    [StructLayout(LayoutKind.Sequential)] private struct Box(float x,float y,float r,float b) { public float X=x,Y=y,R=r,B=b; }
    [StructLayout(LayoutKind.Sequential)] private struct Rounded(Box box,float x,float y) { public Box Box=box; public float RadiusX=x,RadiusY=y; }
    [StructLayout(LayoutKind.Sequential)] private struct Extent(int w,int h) { public int W=w,H=h; }

    private nint Brush(Color color)
    {
        nint brush = 0;
        NativeCom.Check(((delegate* unmanaged[Stdcall]<nint, Color*, nint, nint*, int>)NativeCom.Slot(_target,8))(_target,&color,0,&brush));
        return brush;
    }
    private void Ensure(nint window, int width, int height, uint dpi)
    {
        if (_target == 0)
        {
            try
            {
                _target = factory.CreateTarget(overlay ? 0 : window, (uint)width, (uint)height, dpi);
                _ink = Brush(new(.95f,.97f,1f,1));
                _accent = Brush(new(.14f,.38f,.62f,.93f));
                _width=width; _height=height; _dpi=dpi; Generation++;
            }
            catch { Dispose(); throw; }
        }
        if (!overlay && (width!=_width || height!=_height))
        {
            Extent size = new(width,height);
            NativeCom.Check(((delegate* unmanaged[Stdcall]<nint, Extent*, int>)NativeCom.Slot(_target,58))(_target,&size));
            _width=width; _height=height;
        }
        if (_dpi != dpi)
        {
            ((delegate* unmanaged[Stdcall]<nint,float,float,void>)NativeCom.Slot(_target,51))(_target,dpi,dpi);
            _dpi=dpi;
        }
    }
    internal bool Finish(int hr)
    {
        if (hr == RecreateTarget) { Dispose(); Recreated++; return false; }
        NativeCom.Check(hr);
        return true;
    }
    internal bool Draw(nint window, nint dc, int width, int height, uint dpi, string text)
    {
        if (width <= 0 || height <= 0) return true;
        try { Ensure(window,width,height,dpi); }
        catch(COMException error) when(error.HResult==RecreateTarget) {return Finish(error.HResult);}
        if (overlay)
        {
            WinRect bind = new(0,0,width,height);
            int bindResult = ((delegate* unmanaged[Stdcall]<nint,nint,WinRect*,int>)NativeCom.Slot(_target,57))(_target,dc,&bind);
            if (!Finish(bindResult)) return false;
        }
        ((delegate* unmanaged[Stdcall]<nint,void>)NativeCom.Slot(_target,48))(_target);
        Color color = overlay ? new(0,0,0,0) : new(.08f,.13f,.19f,1);
        ((delegate* unmanaged[Stdcall]<nint, Color*,void>)NativeCom.Slot(_target,47))(_target,&color);
        float dipWidth=width*96f/dpi, dipHeight=height*96f/dpi;
        Rounded panel=new(new(4,4,dipWidth-4,overlay?dipHeight-4:52),12,12);
        ((delegate* unmanaged[Stdcall]<nint,Rounded*,nint,void>)NativeCom.Slot(_target,19))(_target,&panel,_accent);
        // Three rounded level bars draw a small audio glyph without extra image/codec state.
        for (int i=0;i<3;i++)
        {
            Rounded bar = new(new(14+6*i,29-4*i,18+6*i,38),2,2);
            ((delegate* unmanaged[Stdcall]<nint,Rounded*,nint,void>)NativeCom.Slot(_target,19))(_target,&bar,_ink);
        }
        Box label = new(40,12,dipWidth-10,dipHeight);
        fixed (char* str = text)
            ((delegate* unmanaged[Stdcall]<nint,char*,uint,nint,Box*,nint,uint,uint,void>)NativeCom.Slot(_target,27))
                (_target,str,(uint)text.Length,factory.Format,&label,_ink,2,0);
        return Finish(((delegate* unmanaged[Stdcall]<nint,nint,nint,int>)NativeCom.Slot(_target,49))(_target,0,0));
    }
    public void Dispose() { NativeCom.Release(ref _accent); NativeCom.Release(ref _ink); NativeCom.Release(ref _target); }
}

[StructLayout(LayoutKind.Sequential)]
internal struct WinRect(int left,int top,int right,int bottom)
{
    internal int Left=left, Top=top, Right=right, Bottom=bottom;
    internal readonly int Width => Right-Left;
    internal readonly int Height => Bottom-Top;
}

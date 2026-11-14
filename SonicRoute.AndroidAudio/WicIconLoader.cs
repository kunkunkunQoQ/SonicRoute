using System.Reflection;
using System.Runtime.InteropServices;

namespace SonicRoute.AndroidAudio;

/// <summary>
/// Decodes PNG icon bytes using Windows Imaging Component (WIC / windowscodecs.dll) into 32bpp BGRA
/// bitmaps and Win32 <c>HICON</c> handles, replacing FFmpeg PNG decoding with zero external dependencies.
/// </summary>
public static class WicIconLoader
{
    private static readonly Guid ClsidWicImagingFactory2 = new("317d06e8-5f24-433d-bdf7-79ce68d8abc2");
    private static readonly Guid ClsidWicImagingFactory1 = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid IidWicImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid PixelFormat32bppBgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    private const string EmbeddedPhoneIconResource = "SonicRoute.AndroidRuntime.VirtualDisplay.phone-icon.png";
    private const int WicDecodeMetadataCacheOnDemand = 0;
    private const int WicInterpolationModeFant = 3;
    private const int WicDitherTypeNone = 0;
    private const int WicPaletteTypeCustom = 0;

    public static byte[]? LoadDefaultPhoneIconPng()
    {
        var asm = typeof(WicIconLoader).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedPhoneIconResource);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static bool TryDecodePngToBgra32(
        ReadOnlySpan<byte> pngBytes,
        int targetWidth,
        int targetHeight,
        out byte[] bgraPixels,
        out int width,
        out int height)
    {
        bgraPixels = Array.Empty<byte>();
        width = 0;
        height = 0;
        if (pngBytes.IsEmpty) return false;

        int coInit = Win32Native.CoInitializeEx(0, Win32Native.COINIT_APARTMENTTHREADED);
        bool shouldUninit = coInit is 0 or 1;
        if (coInit < 0 && coInit != Win32Native.RPC_E_CHANGED_MODE)
            return false;

        nint pngBuffer = 0;
        try
        {
            if (!TryCreateFactory(out nint factoryPtr)) return false;
            using var factory = new ComPtr(factoryPtr);

            pngBuffer = Marshal.AllocHGlobal(pngBytes.Length);
            unsafe
            {
                fixed (byte* pSrc = pngBytes)
                {
                    Buffer.MemoryCopy(pSrc, (void*)pngBuffer, pngBytes.Length, pngBytes.Length);
                }
            }

            if (ComVtbl.Get<CreateStreamFn>(factory.Handle, 14)(factory.Handle, out nint streamPtr) < 0 || streamPtr == 0)
                return false;
            using var wicStream = new ComPtr(streamPtr);

            if (ComVtbl.Get<StreamInitFromMemFn>(wicStream.Handle, 16)(wicStream.Handle, pngBuffer, (uint)pngBytes.Length) < 0)
                return false;

            if (ComVtbl.Get<CreateDecoderFromStreamFn>(factory.Handle, 4)(
                    factory.Handle, wicStream.Handle, 0, WicDecodeMetadataCacheOnDemand, out nint decoderPtr) < 0 || decoderPtr == 0)
                return false;
            using var decoder = new ComPtr(decoderPtr);

            if (ComVtbl.Get<GetFrameCountFn>(decoder.Handle, 12)(decoder.Handle, out uint frameCount) < 0 || frameCount == 0)
                return false;

            if (ComVtbl.Get<GetFrameFn>(decoder.Handle, 13)(decoder.Handle, 0, out nint framePtr) < 0 || framePtr == 0)
                return false;
            using var frame = new ComPtr(framePtr);

            if (ComVtbl.Get<GetSizeFn>(frame.Handle, 3)(frame.Handle, out uint srcW, out uint srcH) < 0 || srcW == 0 || srcH == 0)
                return false;

            nint sourceForFormat = frame.Handle;
            uint finalW = srcW;
            uint finalH = srcH;
            if (targetWidth > 0 && targetHeight > 0 && ((uint)targetWidth != srcW || (uint)targetHeight != srcH))
            {
                if (ComVtbl.Get<CreateBitmapScalerFn>(factory.Handle, 11)(factory.Handle, out nint scalerPtr) < 0 || scalerPtr == 0)
                    return false;
                using var scaler = new ComPtr(scalerPtr);
                if (ComVtbl.Get<ScalerInitFn>(scaler.Handle, 8)(
                        scaler.Handle, frame.Handle, (uint)targetWidth, (uint)targetHeight, WicInterpolationModeFant) < 0)
                    return false;
                sourceForFormat = scaler.Handle;
                finalW = (uint)targetWidth;
                finalH = (uint)targetHeight;
                return ConvertAndCopyPixels(factory.Handle, sourceForFormat, finalW, finalH, out bgraPixels, out width, out height);
            }

            return ConvertAndCopyPixels(factory.Handle, sourceForFormat, finalW, finalH, out bgraPixels, out width, out height);
        }
        finally
        {
            if (pngBuffer != 0) Marshal.FreeHGlobal(pngBuffer);
            if (shouldUninit) Win32Native.CoUninitialize();
        }
    }

    private static bool ConvertAndCopyPixels(
        nint factoryPtr, nint sourcePtr, uint w, uint h,
        out byte[] bgraPixels, out int width, out int height)
    {
        bgraPixels = Array.Empty<byte>();
        width = 0;
        height = 0;

        if (ComVtbl.Get<CreateFormatConverterFn>(factoryPtr, 10)(factoryPtr, out nint convPtr) < 0 || convPtr == 0)
            return false;
        using var converter = new ComPtr(convPtr);

        Guid fmt = PixelFormat32bppBgra;
        if (ComVtbl.Get<FormatConverterInitFn>(converter.Handle, 8)(
                converter.Handle, sourcePtr, in fmt, WicDitherTypeNone, 0, 0.0, WicPaletteTypeCustom) < 0)
            return false;

        checked
        {
            uint stride = w * 4;
            uint size = stride * h;
            byte[] pixels = new byte[size];
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                int hr = ComVtbl.Get<CopyPixelsFn>(converter.Handle, 7)(
                    converter.Handle, 0, stride, size, pin.AddrOfPinnedObject());
                if (hr < 0) return false;
            }
            finally
            {
                pin.Free();
            }

            bgraPixels = pixels;
            width = (int)w;
            height = (int)h;
            return true;
        }
    }

    public static nint TryCreateIcon(ReadOnlySpan<byte> pngBytes, int desiredSize)
    {
        if (!TryDecodePngToBgra32(pngBytes, desiredSize, desiredSize, out byte[] bgra, out int w, out int h))
            return 0;

        return CreateIconFromBgra32(bgra, w, h);
    }

    public static nint CreateIconFromBgra32(byte[] bgraPixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgraPixels.Length < width * height * 4) return 0;

        var bmi = new BitmapInfoHeader
        {
            biSize = Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = width,
            biHeight = -height, // top-down DIB matching WIC row order
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
            biSizeImage = width * height * 4
        };

        nint colorBits = 0;
        nint hColor = CreateDIBSection(0, in bmi, 0, out colorBits, 0, 0);
        if (hColor == 0 || colorBits == 0)
        {
            if (hColor != 0) DeleteObject(hColor);
            return 0;
        }

        int maskStride = ((width + 31) / 32) * 4;
        byte[] maskBytes = new byte[maskStride * height];
        nint hMask = CreateBitmap(width, height, 1, 1, maskBytes);
        if (hMask == 0)
        {
            DeleteObject(hColor);
            return 0;
        }

        try
        {
            Marshal.Copy(bgraPixels, 0, colorBits, width * height * 4);
            var info = new IconInfo
            {
                fIcon = 1,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = hMask,
                hbmColor = hColor
            };
            return CreateIconIndirect(in info);
        }
        finally
        {
            DeleteObject(hMask);
            DeleteObject(hColor);
        }
    }

    public static void DestroyIconSafe(ref nint iconHandle)
    {
        nint h = Interlocked.Exchange(ref iconHandle, 0);
        if (h != 0) DestroyIcon(h);
    }

    private static bool TryCreateFactory(out nint factory)
    {
        Guid iid = IidWicImagingFactory;
        Guid clsid2 = ClsidWicImagingFactory2;
        if (Win32Native.CoCreateInstance(in clsid2, 0, Win32Native.CLSCTX_INPROC_SERVER, in iid, out factory) >= 0 && factory != 0)
            return true;
        Guid clsid1 = ClsidWicImagingFactory1;
        return Win32Native.CoCreateInstance(in clsid1, 0, Win32Native.CLSCTX_INPROC_SERVER, in iid, out factory) >= 0 && factory != 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDecoderFromStreamFn(nint self, nint pIStream, nint pguidVendor, int metadataOptions, out nint ppIDecoder);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateFormatConverterFn(nint self, out nint ppIFormatConverter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateBitmapScalerFn(nint self, out nint ppIBitmapScaler);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateStreamFn(nint self, out nint ppIWICStream);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StreamInitFromMemFn(nint self, nint pbBuffer, uint cbBufferSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFrameCountFn(nint self, out uint pCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFrameFn(nint self, uint index, out nint ppIBitmapFrame);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSizeFn(nint self, out uint puiWidth, out uint puiHeight);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CopyPixelsFn(nint self, nint prc, uint cbStride, uint cbBufferSize, nint pbBuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ScalerInitFn(nint self, nint pISource, uint uiWidth, uint uiHeight, int mode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FormatConverterInitFn(nint self, nint pISource, in Guid dstFormat, int dither, nint pIPalette, double alphaThresholdPercent, int paletteTranslate);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, in BitmapInfoHeader pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, byte[]? lpBits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(in IconInfo piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);
}

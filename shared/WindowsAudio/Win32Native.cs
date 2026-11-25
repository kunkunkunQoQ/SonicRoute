using System.Runtime.InteropServices;

#if SONICROUTE_NATIVE
namespace SonicRoute.Native.Graphics;
#else
namespace SonicRoute.AndroidAudio;
#endif

internal static partial class Win32Native
{
    public const uint CLSCTX_INPROC_SERVER = 0x1;

    [DllImport("ole32.dll")]
    public static extern int CoIncrementMTAUsage(out nint pCookie);

    [DllImport("ole32.dll")]
    public static extern int CoDecrementMTAUsage(nint cookie);

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);
}

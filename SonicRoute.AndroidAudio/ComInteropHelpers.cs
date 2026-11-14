using System.Runtime.InteropServices;

namespace SonicRoute.AndroidAudio;

/// <summary>
/// Deterministic, zero-unsafe COM vtable invocation and lifetime wrapper for WIC, Media Foundation, and D3D11.
/// Delegates are created from verified vtable slot offsets matching Windows SDK 10.0.22621.0 headers.
/// </summary>
internal static class ComVtbl
{
    public static TDelegate Get<TDelegate>(nint comPtr, int vtableSlot) where TDelegate : Delegate
    {
        nint vtbl = Marshal.ReadIntPtr(comPtr);
        nint fnPtr = Marshal.ReadIntPtr(vtbl, vtableSlot * nint.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(fnPtr);
    }

    public static void SafeRelease(ref nint ptr)
    {
        nint p = Interlocked.Exchange(ref ptr, 0);
        if (p != 0) Marshal.Release(p);
    }

    public static int QueryInterface(nint ptr, in Guid iid, out nint result)
        => Get<QueryInterfaceFn>(ptr, 0)(ptr, in iid, out result);

    public static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr) ;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceFn(nint self, in Guid riid, out nint ppvObject);
}

internal sealed class ComPtr : IDisposable
{
    private nint _ptr;

    public ComPtr(nint ptr = 0) => _ptr = ptr;
    public nint Handle => _ptr;
    public bool IsNull => _ptr == 0;

    public nint Detach() => Interlocked.Exchange(ref _ptr, 0);

    public ComPtr QueryInterface(in Guid iid)
    {
        if (_ptr == 0) throw new ObjectDisposedException(nameof(ComPtr));
        int hr = ComVtbl.QueryInterface(_ptr, in iid, out nint next);
        ComVtbl.ThrowIfFailed(hr, "QueryInterface");
        return new ComPtr(next);
    }

    public bool TryQueryInterface(in Guid iid, out ComPtr? result)
    {
        result = null;
        if (_ptr == 0) return false;
        if (ComVtbl.QueryInterface(_ptr, in iid, out nint next) >= 0 && next != 0)
        {
            result = new ComPtr(next);
            return true;
        }
        return false;
    }

    public void Dispose() => ComVtbl.SafeRelease(ref _ptr);
}

internal static class Win32Native
{
    public const int S_OK = 0;
    public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    public const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
    public const uint COINIT_APARTMENTTHREADED = 0x2;
    public const uint COINIT_MULTITHREADED = 0x0;
    public const uint CLSCTX_INPROC_SERVER = 0x1;

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [DllImport("ole32.dll")]
    public static extern void PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public ulong value1;
        public ulong value2;
    }
}

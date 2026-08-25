using System;
using System.Runtime.InteropServices;

namespace SonicRoute.Core.Interop
{
    // ---- WASAPI 基础枚举（与 EarTrumpet Interop/MMDeviceAPI 一致）----

    public enum EDataFlow
    {
        eRender = 0,   // 播放（输出）
        eCapture = 1,  // 录音（输入）
        eAll = 2
    }

    [Flags]
    public enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    public enum DeviceState : uint
    {
        ACTIVE = 0x1,
        DISABLED = 0x2,
        NOTPRESENT = 0x4,
        UNPLUGGED = 0x8,
        ALL = 0xFFFFFFFF
    }

    public enum AudioSessionState
    {
        Inactive = 0,
        Active = 1,
        Expired = 2
    }

    // ---- COM 常量 ----
    public static class ComConstants
    {
        public const int CLSCTX_INPROC_SERVER = 0x1;
        public const int CLSCTX_ALL = 0x17;
        public const int STGM_READ = 0x0;
        public const ushort VT_LPWSTR = 31; // VarEnum.VT_LPWSTR
    }

    // ---- PROPERTYKEY（functiondiscoverykeys_devpkey.h）----
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    public static class PropertyKeys
    {
        // PKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, PID 14
        public static readonly PROPERTYKEY PKEY_Device_FriendlyName =
            new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    }

    // PROPVARIANT（x64 下固定 24 字节：8 字节头 + 16 字节联合体）
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        // VT_LPWSTR 时字符串指针位于联合体偏移 0 → 结构体偏移 8
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    // ---- 原生 P/Invoke ----
    internal static class NativeMethods
    {
        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PROPVARIANT pvar);

        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(IntPtr pv);

        // 注意：.NET 8 的 DllImport 不支持 [MarshalAs(UnmanagedType.HString)] string，
        // 因此 HSTRING 一律手动创建（WindowsCreateString）后以 IntPtr 传入。
        [DllImport("combase.dll")]
        public static extern int RoGetActivationFactory(
            IntPtr activatableClassId,
            [In] ref Guid iid,
            [Out] out IntPtr factory);

        [DllImport("combase.dll")]
        public static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string src,
            [In] uint length,
            [Out] out IntPtr hstring);

        [DllImport("combase.dll")]
        public static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll")]
        public static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    }
}

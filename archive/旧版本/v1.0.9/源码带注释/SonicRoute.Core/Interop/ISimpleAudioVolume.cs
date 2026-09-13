using System;
using System.Runtime.InteropServices;

namespace SonicRoute.Core.Interop
{
    // ISimpleAudioVolume：{87CE5498-68D6-44E5-9215-6DA47EF883D8}
    // COM 接口按公开定义重新声明（自行实现）。
    // 会话对象本身实现了该接口，可直接转换使用。
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISimpleAudioVolume
    {
        [PreserveSig]
        int SetMasterVolume(float fLevel, ref Guid EventContext);
        [PreserveSig]
        int GetMasterVolume(out float pfLevel);
        [PreserveSig]
        int SetMute(int bMute, ref Guid EventContext);
        [PreserveSig]
        int GetMute(out int pbMute);
    }
}

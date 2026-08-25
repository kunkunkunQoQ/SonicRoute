using System;
using System.Runtime.InteropServices;

namespace SonicRoute.Core.Interop
{
    // ISimpleAudioVolume：{87CE5498-68D6-44E5-9215-6DA47EF883D8}
    // 取自 EarTrumpet Interop/MMDeviceAPI/ISimpleAudioVolume.cs。
    // 会话对象本身实现了该接口（EarTrumpet AudioDeviceSession 直接 (ISimpleAudioVolume)session）。
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

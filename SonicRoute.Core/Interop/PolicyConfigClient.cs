using System;
using System.Runtime.InteropServices;

namespace SonicRoute.Core.Interop
{
    // =====================================================================
    // 系统默认设备切换（IPolicyConfig.SetDefaultEndpoint）
    // 移植自 EarTrumpet（已验证可行的实现），不要自行猜测 API：
    //   EarTrumpet/Interop/MMDeviceAPI/PolicyConfigClient.cs
    //   EarTrumpet/DataModel/WindowsAudio/Internal/AudioPolicyConfigService.cs
    //
    // 与按应用路由（AudioPolicyConfigFactory）不同：本接口修改的是
    // "系统默认播放/录音设备"（音量合成器最上方的默认设备），
    // 供"更改系统默认输出/输入设备"快捷键使用。
    // CLSID_PolicyConfigClient = {870af99c-171d-4f9e-af0d-e63df40c2bc9}
    // IID_IPolicyConfig        = {f8679f50-850a-41cf-9c72-430f290290c8}
    // =====================================================================

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    internal class PolicyConfigClient
    {
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat(IntPtr pstereoid, IntPtr ppformat);

        [PreserveSig]
        int GetDeviceFormat(IntPtr pstereoid, IntPtr pdeviceid, IntPtr ppformat);

        [PreserveSig]
        int ResetDeviceFormat(IntPtr pstereoid, IntPtr pdeviceid);

        [PreserveSig]
        int SetDeviceFormat(IntPtr pstereoid, IntPtr pdeviceid, IntPtr pformat, IntPtr pperiod);

        [PreserveSig]
        int GetProcessingPeriod(IntPtr pstereoid, IntPtr pdeviceid, IntPtr ppdefault, IntPtr ppmin);

        [PreserveSig]
        int SetProcessingPeriod(IntPtr pstereoid, IntPtr pdeviceid, IntPtr pperiod);

        [PreserveSig]
        int GetShareMode(IntPtr pstereoid, IntPtr pdeviceid, IntPtr pmode);

        [PreserveSig]
        int SetShareMode(IntPtr pstereoid, IntPtr pdeviceid, IntPtr pmode);

        [PreserveSig]
        int GetPropertyValue(IntPtr pstereoid, IntPtr pdeviceid, IntPtr pkey, IntPtr pvalue);

        [PreserveSig]
        int SetPropertyValue(IntPtr pstereoid, IntPtr pdeviceid, IntPtr pkey, IntPtr pvalue);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, EDataFlow dataFlow);

        [PreserveSig]
        int SetEndpointVisibility(IntPtr pstereoid, IntPtr pdeviceid, int bvisible);
    }

    /// <summary>系统默认设备切换器：把 Windows 系统默认播放/录音设备设为指定设备。</summary>
    public static class SystemDefaultDeviceService
    {
        /// <summary>将系统默认输出/输入设备设为指定设备（短 ID 或完整路径均可）。
        /// 返回 HRESULT 与结果。</summary>
        public static (bool Success, int HResult, string Message) SetDefault(EDataFlow flow, string deviceId)
        {
            try
            {
                var client = new PolicyConfigClient();
                // ⚠️ SetDefaultEndpoint 需要的是"短 ID"（IMMDevice::GetId 返回格式，如 {0.0.0.00000000}.{hash}），
                // 不能包成完整接口路径（\\?\SWD#MMDEVAPI#... 是按应用路由 API 的格式）——实测包成完整路径返回 E_INVALIDARG。
                // EarTrumpet 传的就是 Device.Id（IMMDevice::GetId 的短 ID 格式）。
                int hr = ((IPolicyConfig)client).SetDefaultEndpoint(deviceId, flow);
                if (hr >= 0)
                    return (true, hr, "成功");
                return (false, hr, $"HRESULT: 0x{hr:X8} ({hr})");
            }
            catch (Exception ex)
            {
                return (false, 0, ex.Message);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SonicRoute.Core.Interop;

namespace SonicRoute.Core
{
    /// <summary>
    /// 全局麦克风静音：直接对系统所有录音设备（eCapture）做设备级静音。
    /// 与"静音当前应用"不同——设备级 SetMute 会静音整条设备，所有路由到该设备的应用录音都生效，
    /// 任何使用麦克风的应用（游戏/通话/录制）都会静音。
    ///
    /// 状态语义：全部录音设备都处于静音 = 已全局静音；Toggle 在"全部静音 ↔ 全部取消静音"间切换。
    /// </summary>
    public static class GlobalMicMuteService
    {
        private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

        /// <summary>是否所有录音设备都已静音（没有任何录音设备时视为未静音）。</summary>
        public static bool IsMuted()
        {
            var vols = GetEndpointVolumes();
            try
            {
                if (vols.Count == 0) return false;
                foreach (var v in vols)
                {
                    try
                    {
                        if (v.GetMute(out int m) < 0 || m == 0) return false;
                    }
                    catch { return false; }
                }
                return true;
            }
            finally
            {
                foreach (var v in vols)
                    try { Marshal.ReleaseComObject(v); } catch { }
            }
        }

        /// <summary>静音/取消静音所有录音设备；返回切换后的全局静音状态（true=已静音）。</summary>
        public static bool Toggle()
        {
            bool m = IsMuted();
            bool ok = SetMute(!m);
            // 以写入后的真实状态为准（读回确认，与输入会话一致防止设备写入异步不生效）
            return ok ? IsMuted() : m;
        }

        /// <summary>设置所有录音设备的静音状态；任一设备写入成功即视为成功。</summary>
        public static bool SetMute(bool mute)
        {
            var vols = GetEndpointVolumes();
            if (vols.Count == 0) return false;
            var g = Guid.Empty;
            bool any = false;
            try
            {
                foreach (var v in vols)
                {
                    try
                    {
                        if (v.SetMute(mute ? 1 : 0, ref g) >= 0) any = true;
                    }
                    catch { }
                }
                return any;
            }
            finally
            {
                foreach (var v in vols)
                    try { Marshal.ReleaseComObject(v); } catch { }
            }
        }

        private static List<IAudioEndpointVolume> GetEndpointVolumes()
        {
            var list = new List<IAudioEndpointVolume>();
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                if (enumerator.EnumAudioEndpoints(EDataFlow.eCapture, DeviceState.ACTIVE, out var collection) < 0 || collection == null)
                    return list;
                try
                {
                    collection.GetCount(out uint count);
                    for (uint i = 0; i < count; i++)
                    {
                        if (collection.Item(i, out var device) < 0 || device == null) continue;
                        try
                        {
                            var iid = IID_IAudioEndpointVolume;
                            if (device.Activate(ref iid, ComConstants.CLSCTX_ALL, IntPtr.Zero, out object obj) < 0 || obj == null) continue;
                            list.Add((IAudioEndpointVolume)obj);
                        }
                        finally { Marshal.ReleaseComObject(device); }
                    }
                }
                finally { Marshal.ReleaseComObject(collection); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
            return list;
        }
    }
}

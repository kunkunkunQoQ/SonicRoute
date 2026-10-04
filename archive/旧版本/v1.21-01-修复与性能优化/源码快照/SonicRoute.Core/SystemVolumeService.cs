using System;
using System.Runtime.InteropServices;
using SonicRoute.Core.Compat;
using SonicRoute.Core.Interop;

namespace SonicRoute.Core
{
    /// <summary>
    /// 系统输出设备音量控制（设备级 IAudioEndpointVolume，eRender）。
    /// 仅控制指定设备（默认：系统默认输出设备）自身的音量/静音，**不修改系统默认设备**，
    /// 与 Per-App Routing 核心（IAudioPolicyConfigFactory）完全无关。
    /// 供简洁面板顶部"系统音频"区使用：选择设备 → 滑块/静音控制系统音量。
    /// </summary>
    public static class SystemVolumeService
    {
        private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
        private static readonly object _adjustVolumeLock = new();

        /// <summary>按设备 Id 获取 IAudioEndpointVolume；deviceId 为 null/空表示系统默认输出设备。</summary>
        private static IAudioEndpointVolume? GetVolume(string? deviceId)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                IMMDevice device;
                int hr = string.IsNullOrWhiteSpace(deviceId)
                    ? enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out device)
                    : enumerator.GetDevice(deviceId, out device);
                if (hr < 0 || device == null) return null;
                try
                {
                    var iid = IID_IAudioEndpointVolume;
                    if (device.Activate(ref iid, ComConstants.CLSCTX_ALL, IntPtr.Zero, out object obj) < 0 || obj == null) return null;
                    return (IAudioEndpointVolume)obj;
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }

        /// <summary>读取指定设备音量百分比（0–100）；取不到返回 -1。</summary>
        public static int GetVolumePercent(string? deviceId = null)
        {
            var v = GetVolume(deviceId);
            if (v == null) return -1;
            try
            {
                if (v.GetMasterVolumeLevelScalar(out float f) < 0) return -1;
                return (int)Math.Round(MathEx.Clamp(f, 0f, 1f) * 100f);
            }
            catch { return -1; }
            finally { Marshal.ReleaseComObject(v); }
        }

        /// <summary>一次激活端点读取音量和静音；读不到音量返回 -1、静音返回 false。</summary>
        public static (int Volume, bool Muted) ReadState(string? deviceId = null)
        {
            IAudioEndpointVolume? volume = null;
            try
            {
                volume = GetVolume(deviceId);
                if (volume == null) return (-1, false);
                int percent = -1;
                bool muted = false;
                try
                {
                    if (volume.GetMasterVolumeLevelScalar(out float scalar) >= 0)
                        percent = (int)Math.Round(MathEx.Clamp(scalar, 0f, 1f) * 100f);
                }
                catch { }
                try { muted = volume.GetMute(out int state) >= 0 && state != 0; } catch { }
                return (percent, muted);
            }
            catch { return (-1, false); }
            finally { if (volume != null) Marshal.ReleaseComObject(volume); }
        }

        /// <summary>设置指定设备音量百分比（0–100）。</summary>
        public static bool SetVolumePercent(string? deviceId, int percent)
        {
            var v = GetVolume(deviceId);
            if (v == null) return false;
            try
            {
                var g = Guid.Empty;
                return v.SetMasterVolumeLevelScalar(MathEx.Clamp(percent / 100f, 0f, 1f), ref g) >= 0;
            }
            catch { return false; }
            finally { Marshal.ReleaseComObject(v); }
        }

        /// <summary>同一设备接口内完成音量增减与实际值读回，避免三次创建枚举器/激活端点。
        /// 增减操作串行；接口只在本轮持有，默认设备或会话改变后仍重新获取。</summary>
        public static (int Current, int Requested, int Actual, bool Applied) AdjustVolumePercent(string? deviceId, int delta)
        {
            lock (_adjustVolumeLock)
            {
                var volume = GetVolume(deviceId);
                if (volume == null) return (-1, -1, -1, false);
                try
                {
                    int Read()
                    {
                        try
                        {
                            return volume.GetMasterVolumeLevelScalar(out float value) >= 0
                                ? (int)Math.Round(MathEx.Clamp(value, 0f, 1f) * 100f) : -1;
                        }
                        catch { return -1; }
                    }
                    int current = Read();
                    if (current < 0) return (-1, -1, -1, false);
                    int requested = MathEx.Clamp(current + delta, 0, 100);
                    bool applied;
                    try
                    {
                        var context = Guid.Empty;
                        applied = volume.SetMasterVolumeLevelScalar(requested / 100f, ref context) >= 0;
                    }
                    catch { applied = false; }
                    return (current, requested, Read(), applied);
                }
                finally { Marshal.ReleaseComObject(volume); }
            }
        }

        /// <summary>指定设备是否静音（取不到视为未静音）。</summary>
        public static bool IsMuted(string? deviceId = null)
        {
            var v = GetVolume(deviceId);
            if (v == null) return false;
            try
            {
                if (v.GetMute(out int m) < 0) return false;
                return m != 0;
            }
            catch { return false; }
            finally { Marshal.ReleaseComObject(v); }
        }

        /// <summary>静音/取消静音指定设备；返回切换后的状态。</summary>
        public static bool ToggleMute(string? deviceId = null)
        {
            bool m = IsMuted(deviceId);
            SetMute(deviceId, !m);
            return IsMuted(deviceId);
        }

        /// <summary>设置指定设备静音状态。</summary>
        public static bool SetMute(string? deviceId, bool mute)
        {
            var v = GetVolume(deviceId);
            if (v == null) return false;
            try
            {
                var g = Guid.Empty;
                return v.SetMute(mute ? 1 : 0, ref g) >= 0;
            }
            catch { return false; }
            finally { Marshal.ReleaseComObject(v); }
        }
    }
}

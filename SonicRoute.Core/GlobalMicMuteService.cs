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
        private static readonly object _changeMuteLock = new();

        public readonly struct MuteState
        {
            public MuteState(int count, int mutedCount, int unavailableCount)
            { Count = count; MutedCount = mutedCount; UnavailableCount = unavailableCount; }
            public int Count { get; }
            public int MutedCount { get; }
            public int UnavailableCount { get; }
            public bool Known => Count > 0 && UnavailableCount == 0;
            public bool AllMuted => Known && MutedCount == Count;
        }

        /// <summary>包含无设备/读取失败/部分静音的状态，不能将读取失败当作未静音。</summary>
        public static MuteState ReadState()
        {
            List<IAudioEndpointVolume>? volumes = null;
            try
            {
                volumes = GetEndpointVolumes(out int count);
                return ReadState(volumes, count);
            }
            catch { return new MuteState(0, 0, 1); }
            finally { ReleaseVolumes(volumes); }
        }

        /// <summary>本轮接口内写入全部麦克风并读回；任一失败或未到目标均不报告全部成功。</summary>
        public static (MuteState State, bool Applied) SetMuteChecked(bool mute)
            => ChangeMuteChecked(mute);

        /// <summary>初读与写入共用本轮端点；保留写后重新发现验证，状态不明时不切换。</summary>
        public static (MuteState State, bool Applied) ToggleChecked() => ChangeMuteChecked(null);

        private static (MuteState State, bool Applied) ChangeMuteChecked(bool? mute)
        {
            lock (_changeMuteLock)
            {
                List<IAudioEndpointVolume>? volumes = null;
                try
                {
                    volumes = GetEndpointVolumes(out int count);
                    bool toggle = !mute.HasValue;
                    if (toggle)
                    {
                        var before = ReadState(volumes, count);
                        if (!before.Known) return (before, false);
                        mute = !before.AllMuted;
                    }
                    bool target = mute.Value;
                    bool writesOk = count > 0 && volumes.Count == count;
                    var context = Guid.Empty;
                    foreach (var volume in volumes)
                        try { if (volume.SetMute(target ? 1 : 0, ref context) < 0) writesOk = false; }
                        catch { writesOk = false; }
                    // Keep the old toggle's final topology read: a newly available microphone
                    // must not be hidden by a successful write to the original endpoint list.
                    var state = toggle ? ReadState() : ReadState(volumes, count);
                    bool matches = state.Known && state.MutedCount == (target ? state.Count : 0);
                    return (state, writesOk && matches);
                }
                catch { return (new MuteState(0, 0, 1), false); }
                finally { ReleaseVolumes(volumes); }
            }
        }

        private static MuteState ReadState(List<IAudioEndpointVolume> volumes, int count)
        {
            int muted = 0, unavailable = count < 0 ? 1 : Math.Max(0, count - volumes.Count);
            foreach (var volume in volumes)
                try
                {
                    if (volume.GetMute(out int state) < 0) unavailable++;
                    else if (state != 0) muted++;
                }
                catch { unavailable++; }
            return new MuteState(Math.Max(count, 0), muted, unavailable);
        }

        private static void ReleaseVolumes(List<IAudioEndpointVolume>? volumes)
        {
            if (volumes == null) return;
            foreach (var volume in volumes)
                try { Marshal.ReleaseComObject(volume); } catch { }
        }

        /// <summary>是否所有录音设备都已静音（没有任何录音设备时视为未静音）。</summary>
        public static bool IsMuted()
        {
            var vols = GetEndpointVolumes();
            return AreAllMuted(vols);
        }

        private static bool AreAllMuted(List<IAudioEndpointVolume> vols)
        {
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
            => ToggleChecked().State.AllMuted;

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

        /// <summary>统一静音状态（麦克风静音 OSD 常驻用）：全局麦克风静音，
        /// 或（trackInput=true 时）系统默认输入设备静音。检测逻辑与各单项一致。</summary>
        public static bool IsAnyMuted(bool trackInput)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                // 两种状态共用一轮枚举器；保留全部静音和默认输入静音的原有语义。
                if (AreAllMuted(GetEndpointVolumes(enumerator))) return true;
                return trackInput && IsDefaultInputMuted(enumerator);
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }

        private static bool IsDefaultInputMuted(IMMDeviceEnumerator enumerator)
        {
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eMultimedia, out var device) < 0 || device == null)
                    return false;
                try
                {
                    var iid = IID_IAudioEndpointVolume;
                    if (device.Activate(ref iid, ComConstants.CLSCTX_ALL, IntPtr.Zero, out object obj) < 0 || obj == null)
                        return false;
                    var vol = (IAudioEndpointVolume)obj;
                    try { return vol.GetMute(out int m) >= 0 && m != 0; }
                    finally { Marshal.ReleaseComObject(vol); }
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            catch { return false; }
        }

        private static List<IAudioEndpointVolume> GetEndpointVolumes()
            => GetEndpointVolumes(out _);

        private static List<IAudioEndpointVolume> GetEndpointVolumes(out int count)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                return GetEndpointVolumes(enumerator, out count);
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }

        private static List<IAudioEndpointVolume> GetEndpointVolumes(IMMDeviceEnumerator enumerator)
            => GetEndpointVolumes(enumerator, out _);

        private static List<IAudioEndpointVolume> GetEndpointVolumes(IMMDeviceEnumerator enumerator, out int total)
        {
            total = -1;
            var list = new List<IAudioEndpointVolume>();
            try
            {
                if (enumerator.EnumAudioEndpoints(EDataFlow.eCapture, DeviceState.ACTIVE, out var collection) < 0 || collection == null)
                    return list;
                try
                {
                    if (collection.GetCount(out uint count) < 0) return list;
                    total = (int)count;
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
            catch
            {
                foreach (var vol in list)
                    try { Marshal.ReleaseComObject(vol); } catch { }
                throw;
            }
            return list;
        }
    }
}

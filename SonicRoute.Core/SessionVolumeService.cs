using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SonicRoute.Core.Interop;

namespace SonicRoute.Core
{
    /// <summary>
    /// 按应用的会话音量控制（独立于 Per-App Routing 核心，不动系统主音量、不影响其他应用）。
    ///
    /// 会话选择语义与 EarTrumpet 完全一致（对照 EarTrumpet.AudioDeviceSessionGroup）：
    ///   - 同一进程可能对应多个 Audio Session（Chrome 每标签页一个、一个进程可跨多个端点设备，如
    ///     哔哩哔哩在 5 个设备上各有 1 个 render 会话）。不能"找到第一个 Session 就控制"——
    ///     那可能是应用没有发声/未路由到的设备上的会话，改了没用、读回来也是错的。
    ///   - EarTrumpet 把同一进程（AppId 组）的所有会话聚合成组：读取音量取组内第一个会话
    ///     （Volume => _sessions[0].Volume），写入时遍历组内所有会话
    ///     （SetVolumeScalar / Volume setter 对 foreach _sessions 全部 SetMasterVolume）。
    ///   - 本实现据此：按 PID 收集其全部 eRender 会话，读用第一个，写遍历全部。
    ///
    /// 输出（eRender）：音量/静音控制，同 Windows 音量合成器行为。
    /// 输入（eCapture）：麦克风静音（与 EarTrumpet 输入会话静音一致），只收集会话并按
    /// 同一"读第一个/写全部"语义操作 SetMute，不提供音量调节。
    /// </summary>
    public static class SessionVolumeService
    {
        private static readonly Guid IID_IAudioSessionManager2 =
            new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        private static readonly object _lock = new();
        private static readonly object _refreshLock = new();
        private static DateTime _lastRefresh = DateTime.MinValue;
        private static DateTime _lastEnum = DateTime.MinValue;   // 实际完成枚举的时刻
        private static Dictionary<int, List<ISimpleAudioVolume>> _renderVolumes = new();
        private static Dictionary<int, List<ISimpleAudioVolume>> _captureVolumes = new();

        /// <summary>该 PID 是否有可用的（输出）会话；缓存未命中会自动刷新（受节流保护）。</summary>
        public static bool HasSession(int pid)
        {
            lock (_lock)
            {
                if (_renderVolumes.TryGetValue(pid, out var l) && l.Count > 0) return true;
            }
            Refresh();
            lock (_lock)
            {
                return _renderVolumes.TryGetValue(pid, out var l2) && l2.Count > 0;
            }
        }

        /// <summary>返回任意一个有可用输出会话的 PID；没有返回 0（托盘滚轮兜底用）。</summary>
        public static int FirstSessionPid()
        {
            Refresh(true);
            lock (_lock)
            {
                foreach (var kv in _renderVolumes)
                    if (kv.Value is { Count: > 0 }) return kv.Key;
            }
            return 0;
        }

        /// <summary>读取音量百分比（0–100）；无会话返回 -1。</summary>
        public static int GetVolumePercent(int pid)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var list = FindRenderVolumes(pid, 5);
                if (list == null || list.Count == 0) return -1;
                try
                {
                    // EarTrumpet 读组内第一个会话
                    if (list[0].GetMasterVolume(out float f) < 0)
                    {
                        if (attempt == 0) { Refresh(true); continue; }
                        return -1;
                    }
                    return (int)Math.Round(Math.Clamp(f, 0f, 1f) * 100f);
                }
                catch
                {
                    if (attempt == 0) { Refresh(true); continue; }
                    return -1;
                }
            }
            return -1;
        }

        /// <summary>设置音量百分比（0–100）；对进程全部输出会话生效。任一成功即视为成功。</summary>
        public static bool SetVolumePercent(int pid, int percent)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var list = FindRenderVolumes(pid, 5);
                if (list == null || list.Count == 0) return false;
                var g = Guid.Empty;
                float val = Math.Clamp(percent / 100f, 0f, 1f);
                bool any = false;
                try
                {
                    // EarTrumpet 写组内所有会话
                    foreach (var v in list)
                    {
                        if (v.SetMasterVolume(val, ref g) >= 0) any = true;
                    }
                    if (any) return true;
                    if (attempt == 0) { Refresh(true); continue; }
                    return false;
                }
                catch
                {
                    if (attempt == 0) { Refresh(true); continue; }
                    return false;
                }
            }
            return false;
        }

        /// <summary>是否静音（读组内第一个会话）。</summary>
        public static bool IsMuted(int pid)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var list = FindRenderVolumes(pid, 5);
                if (list == null || list.Count == 0) return false;
                try
                {
                    if (list[0].GetMute(out int m) < 0)
                    {
                        if (attempt == 0) { Refresh(true); continue; }
                        return false;
                    }
                    return m != 0;
                }
                catch
                {
                    if (attempt == 0) { Refresh(true); continue; }
                    return false;
                }
            }
            return false;
        }

        /// <summary>设置静音；对进程全部输出会话生效。</summary>
        public static bool SetMute(int pid, bool mute)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var list = FindRenderVolumes(pid, 5);
                if (list == null || list.Count == 0) return false;
                var g = Guid.Empty;
                bool any = false;
                try
                {
                    foreach (var v in list)
                    {
                        if (v.SetMute(mute ? 1 : 0, ref g) >= 0) any = true;
                    }
                    if (any) return true;
                    if (attempt == 0) { Refresh(true); continue; }
                    return false;
                }
                catch
                {
                    if (attempt == 0) { Refresh(true); continue; }
                    return false;
                }
            }
            return false;
        }

        /// <summary>切换静音，返回切换后的静音状态。</summary>
        public static bool ToggleMute(int pid)
        {
            bool m = IsMuted(pid);
            SetMute(pid, !m);
            return !m;
        }

        /// <summary>切换静音并报告是否真正生效（(新静音状态, 是否生效)）。
        /// 应用没有任何输出会话时不可静音，返回 (false, false)——避免快捷键误报"已静音"却什么都没做。</summary>
        public static (bool Muted, bool Applied) ToggleMuteChecked(int pid)
        {
            var list = FindRenderVolumes(pid, 5);
            if (list == null || list.Count == 0) return (false, false);
            bool m = IsMuted(pid);
            bool ok = SetMute(pid, !m);
            return (!m, ok);
        }

        // ------------------------------------------------------------------
        // 输入会话（麦克风静音，语义与输出一致：读组内第一个、写遍历全部）
        // ------------------------------------------------------------------

        /// <summary>该 PID 是否有可用的输入（录音）会话；缓存未命中自动刷新（受节流保护）。</summary>
        public static bool HasInputSession(int pid)
        {
            lock (_lock)
            {
                if (_captureVolumes.TryGetValue(pid, out var l) && l.Count > 0) return true;
            }
            Refresh();
            lock (_lock)
            {
                return _captureVolumes.TryGetValue(pid, out var l2) && l2.Count > 0;
            }
        }

        /// <summary>麦克风是否静音（读组内第一个输入会话）。</summary>
        public static bool IsInputMuted(int pid)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var list = FindCaptureVolumes(pid, 5);
                if (list == null || list.Count == 0) return false;
                try
                {
                    if (list[0].GetMute(out int m) < 0)
                    {
                        if (attempt == 0) { Refresh(true); continue; }
                        return false;
                    }
                    return m != 0;
                }
                catch
                {
                    if (attempt == 0) { Refresh(true); continue; }
                    return false;
                }
            }
            return false;
        }

        /// <summary>设置麦克风静音；对进程全部输入会话生效。</summary>
        public static bool SetInputMute(int pid, bool mute)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var list = FindCaptureVolumes(pid, 5);
                if (list == null || list.Count == 0) return false;
                var g = Guid.Empty;
                bool any = false;
                try
                {
                    foreach (var v in list)
                    {
                        if (v.SetMute(mute ? 1 : 0, ref g) >= 0) any = true;
                    }
                    if (any) return true;
                    if (attempt == 0) { Refresh(true); continue; }
                    return false;
                }
                catch
                {
                    if (attempt == 0) { Refresh(true); continue; }
                    return false;
                }
            }
            return false;
        }

        /// <summary>切换麦克风静音并报告是否真正生效（(新静音状态, 是否生效)）。
        /// 应用没有任何输入会话时返回 (false, false)，避免误报"已静音"却什么都没做。</summary>
        public static (bool Muted, bool Applied) ToggleInputMuteChecked(int pid)
        {
            var list = FindCaptureVolumes(pid, 5);
            if (list == null || list.Count == 0) return (false, false);
            bool m = IsInputMuted(pid);
            bool ok = SetInputMute(pid, !m);
            return (!m, ok);
        }

        /// <summary>重新枚举所有播放/录音设备的会话，按 PID 缓存其全部 eRender / eCapture 会话
        /// （旧引用释放）。带 2 秒节流：已有缓存且 2 秒内刷新过则跳过，降低高频操作
        /// （托盘滚轮/音量滑块）的 COM 枚举开销与内存/音频子系统压力。force=true 时强制执行
        /// （用于指定 PID 未命中）。</summary>
        public static void Refresh(bool force = false)
        {
            lock (_refreshLock)
            {
                var now = DateTime.UtcNow;
                bool skip = !force && (_renderVolumes.Count + _captureVolumes.Count) > 0
                            && (now - _lastRefresh).TotalMilliseconds < 2000;
                _lastRefresh = now;
                if (skip) return;
            }

            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var newRender = new Dictionary<int, List<ISimpleAudioVolume>>();
            var newCapture = new Dictionary<int, List<ISimpleAudioVolume>>();
            try
            {
                CollectInto(enumerator, EDataFlow.eRender, newRender);
                CollectInto(enumerator, EDataFlow.eCapture, newCapture);
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }

            lock (_lock)
            {
                foreach (var l in _renderVolumes.Values)
                    foreach (var v in l)
                        try { Marshal.ReleaseComObject(v); } catch { }
                foreach (var l in _captureVolumes.Values)
                    foreach (var v in l)
                        try { Marshal.ReleaseComObject(v); } catch { }
                _renderVolumes = newRender;
                _captureVolumes = newCapture;
                _lastEnum = DateTime.UtcNow;
            }
        }

        private static void CollectInto(
            IMMDeviceEnumerator enumerator, EDataFlow flow, Dictionary<int, List<ISimpleAudioVolume>> target)
        {
            if (enumerator.EnumAudioEndpoints(flow, DeviceState.ACTIVE, out var collection) < 0 || collection == null)
                return;
            try
            {
                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) < 0 || device == null) continue;
                    try
                    {
                        var iid = IID_IAudioSessionManager2;
                        if (device.Activate(ref iid, ComConstants.CLSCTX_ALL, IntPtr.Zero, out object mgrObj) < 0 || mgrObj == null)
                            continue;
                        var mgr = (IAudioSessionManager2)mgrObj;
                        try
                        {
                            if (mgr.GetSessionEnumerator(out var sessionEnum) < 0 || sessionEnum == null) continue;
                            try
                            {
                                sessionEnum.GetCount(out int sc);
                                for (int j = 0; j < sc; j++)
                                {
                                    if (sessionEnum.GetSession(j, out var session) < 0 || session == null) continue;
                                    bool owned = false;
                                    try
                                    {
                                        if (session.GetProcessId(out uint pid) < 0 || pid == 0) continue;
                                        if (session.GetState(out var st) >= 0 && st == AudioSessionState.Expired) continue;
                                        var vol = (ISimpleAudioVolume)session;
                                        int key = (int)pid;
                                        if (!target.TryGetValue(key, out var list))
                                        {
                                            list = new List<ISimpleAudioVolume>();
                                            target[key] = list;
                                        }
                                        list.Add(vol);
                                        owned = true; // 已移交 target 管理，不再释放
                                    }
                                    finally
                                    {
                                        if (!owned) Marshal.ReleaseComObject(session);
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(sessionEnum);
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(mgr);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(collection);
            }
        }

        /// <summary>按 PID 取输出会话列表；缓存超过 maxAgeSeconds 秒则强制刷新后再取。
        /// 关键原因：流媒体等应用的 Audio Session 会随播放内容变化而重建，长时间复用启动时
        /// 枚举的缓存会让 SetMute/SetMasterVolume 作用在已失效的会话上（返回成功却不生效）——
        /// 这是此前"静音/音量不生效"的根因。写操作与读操作统一用 5 秒新鲜度：5 秒内直接用缓存
        /// （支撑滑块高频拖动），超过则一次性强制刷新。</summary>
        private static List<ISimpleAudioVolume>? FindRenderVolumes(int pid, double maxAgeSeconds)
        {
            lock (_lock)
            {
                if (_renderVolumes.TryGetValue(pid, out var l))
                {
                    if ((DateTime.UtcNow - _lastEnum).TotalSeconds < maxAgeSeconds) return l;
                }
            }
            Refresh(true);
            lock (_lock)
            {
                return _renderVolumes.TryGetValue(pid, out var l2) ? l2 : null;
            }
        }

        /// <summary>按 PID 取输入（录音）会话列表，新鲜度语义与输出一致（5 秒强制刷新）。</summary>
        private static List<ISimpleAudioVolume>? FindCaptureVolumes(int pid, double maxAgeSeconds)
        {
            lock (_lock)
            {
                if (_captureVolumes.TryGetValue(pid, out var l))
                {
                    if ((DateTime.UtcNow - _lastEnum).TotalSeconds < maxAgeSeconds) return l;
                }
            }
            Refresh(true);
            lock (_lock)
            {
                return _captureVolumes.TryGetValue(pid, out var l2) ? l2 : null;
            }
        }
    }
}

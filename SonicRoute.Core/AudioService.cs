using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute.Core
{
    /// <summary>音频服务：设备枚举、应用（会话）枚举、按应用切换。</summary>
    public static class AudioService
    {
        private static readonly Guid IID_IAudioSessionManager2 =
            new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        private static IMMDeviceEnumerator CreateEnumerator() =>
            (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

        // ------------------------------------------------------------------
        // 设备枚举
        // ------------------------------------------------------------------

        /// <summary>枚举指定方向的激活设备（播放 eRender / 录音 eCapture）。</summary>
        public static List<AudioDeviceInfo> GetDevices(EDataFlow flow)
        {
            var enumerator = CreateEnumerator();
            try
            {
                return GetDevicesCore(enumerator, flow);
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        private static List<AudioDeviceInfo> GetDevicesCore(IMMDeviceEnumerator enumerator, EDataFlow flow)
        {
            var result = new List<AudioDeviceInfo>();
            int hr = enumerator.EnumAudioEndpoints(flow, DeviceState.ACTIVE, out var collection);
            if (hr < 0)
                throw new InvalidOperationException($"枚举{(flow == EDataFlow.eRender ? "播放" : "录音")}设备失败 HRESULT=0x{hr:X8} ({hr})");

            try
            {
                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    collection.Item(i, out var device);
                    if (device == null) continue;
                    try
                    {
                        int hrId = device.GetId(out string id);
                        if (hrId < 0) continue;
                        string? name = ReadFriendlyName(device);
                        result.Add(new AudioDeviceInfo
                        {
                            Id = id,
                            DisplayName = name,
                            Flow = flow
                        });
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
            return result;
        }

        /// <summary>系统默认设备完整 ID（未取到返回 null）。</summary>
        public static string? GetDefaultDeviceId(EDataFlow flow)
        {
            var enumerator = CreateEnumerator();
            try
            {
                int hr = enumerator.GetDefaultAudioEndpoint(flow, ERole.eConsole, out var device);
                if (hr < 0) return null;
                try
                {
                    device.GetId(out string id);
                    return id;
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        private static string? ReadFriendlyName(IMMDevice device)
        {
            int hr = device.OpenPropertyStore(ComConstants.STGM_READ, out var store);
            if (hr < 0 || store == null) return null;
            try
            {
                var key = PropertyKeys.PKEY_Device_FriendlyName;
                int hr2 = store.GetValue(ref key, out PROPVARIANT pv);
                if (hr2 < 0) return null;
                try
                {
                    if (pv.vt == ComConstants.VT_LPWSTR && pv.pwszVal != IntPtr.Zero)
                        return Marshal.PtrToStringUni(pv.pwszVal);
                    return null;
                }
                finally
                {
                    NativeMethods.PropVariantClear(ref pv);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        // ------------------------------------------------------------------
        // 应用（音频会话）枚举
        // ------------------------------------------------------------------

        /// <summary>枚举当前有音频会话的应用（所有播放/录音设备，按 PID 去重）。带 1 秒缓存，
        /// 避免语言切换 / 快速面板 / 托盘滚轮等场景并发重复枚举导致音频子系统卡顿。
        /// 需要强制刷新时传 refresh=true。</summary>
        public static List<AudioAppInfo> GetApps(bool refresh = false)
        {
            lock (_appsLock)
            {
                if (!refresh && _appsCache != null && (DateTime.UtcNow - _appsCacheAt).TotalMilliseconds < 1000)
                    return _appsCache;
                var apps = EnumerateApps();
                _appsCache = apps;
                _appsCacheAt = DateTime.UtcNow;
                return apps;
            }
        }

        private static readonly object _appsLock = new();
        private static List<AudioAppInfo>? _appsCache;
        private static DateTime _appsCacheAt;

        private static List<AudioAppInfo> EnumerateApps()
        {
            var apps = new Dictionary<uint, AudioAppInfo>();
            var enumerator = CreateEnumerator();
            try
            {
                foreach (var flow in new[] { EDataFlow.eRender, EDataFlow.eCapture })
                {
                    var devices = GetDevicesCore(enumerator, flow);
                    foreach (var dev in devices)
                        CollectSessions(enumerator, dev.Id, apps);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }

            return apps.Values
                .OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static void CollectSessions(IMMDeviceEnumerator enumerator, string deviceId, Dictionary<uint, AudioAppInfo> apps)
        {
            int hrGet = enumerator.GetDevice(deviceId, out var device);
            if (hrGet < 0 || device == null) return;
            try
            {
                var iidSessionManager = IID_IAudioSessionManager2;
                int hrActivate = device.Activate(ref iidSessionManager, ComConstants.CLSCTX_ALL, IntPtr.Zero, out object mgrObj);
                if (hrActivate < 0 || mgrObj == null) return;
                var mgr = (IAudioSessionManager2)mgrObj;
                try
                {
                    int hrEnum = mgr.GetSessionEnumerator(out var sessionEnum);
                    if (hrEnum < 0 || sessionEnum == null) return;
                    try
                    {
                        sessionEnum.GetCount(out int count);
                        for (int i = 0; i < count; i++)
                        {
                            int hrSess = sessionEnum.GetSession(i, out var session);
                            if (hrSess < 0 || session == null) continue;
                            try
                            {
                                int hrPid = session.GetProcessId(out uint pid);
                                if (hrPid < 0 || pid == 0) continue;

                                int hrState = session.GetState(out var state);
                                if (hrState >= 0 && state == AudioSessionState.Expired) continue;

                                string? displayName = null;
                                int hrName = session.GetDisplayName(out displayName);
                                if (hrName < 0) displayName = null;

                                if (string.IsNullOrWhiteSpace(displayName))
                                    displayName = null;

                                if (!apps.TryGetValue(pid, out var existing))
                                {
                                    apps[pid] = new AudioAppInfo
                                    {
                                        ProcessId = pid,
                                        DisplayName = displayName,
                                        ProcessName = GetProcessName(pid)
                                    };
                                }
                                else if (existing.DisplayName == null && displayName != null)
                                {
                                    existing.DisplayName = displayName;
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(session);
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

        private static string? GetProcessName(uint pid)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // 按应用切换（EarTrumpet Per-App Audio Routing 机制）
        // ------------------------------------------------------------------

        /// <summary>将进程的播放/录音设备持久化为指定设备。返回 HRESULT 与结果。</summary>
        public static (bool Success, int HResult, string Message) ApplyEndpoint(
            int processId, EDataFlow flow, string deviceId)
        {
            try
            {
                var config = new AudioPolicyConfig(flow);
                // 设备 ID 可能是短 ID（IMMDevice.GetId()），统一包装为完整接口路径再调用
                string fullDeviceId = AudioPolicyConfig.EnsureFullDeviceId(deviceId, flow);
                int hr = config.SetDefaultEndPoint(fullDeviceId, processId);
                if (hr >= 0)
                    return (true, hr, "成功");
                return (false, hr, $"HRESULT: 0x{hr:X8} ({hr})");
            }
            catch (Exception ex)
            {
                return (false, 0, ex.Message);
            }
        }

        /// <summary>读取进程当前持久化的设备完整 ID（未设置返回 null）。</summary>
        public static string? GetPersistedEndpoint(int processId, EDataFlow flow)
        {
            try
            {
                var config = new AudioPolicyConfig(flow);
                return config.GetDefaultEndPoint(processId);
            }
            catch
            {
                return null;
            }
        }
    }
}

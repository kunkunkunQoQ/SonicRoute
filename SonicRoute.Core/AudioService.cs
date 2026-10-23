using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.Core.Compat;
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

        /// <summary>枚举指定方向的激活设备（播放 eRender / 录音 eCapture）。
        /// 带极短 TTL 缓存：同一短时间内（如启动→打开面板、快捷键连续切换）避免重复 WASAPI 枚举。
        /// 返回防御性副本，调用方修改（如 IsDefault 显示标记）不影响缓存。
        /// 设备通知使缓存失效，3s TTL 作为未收到通知时的回退。</summary>
        public static List<AudioDeviceInfo> GetDevices(EDataFlow flow)
        {
            // 同方向冷缓存只枚举一次；播放与录音仍可并行。
            lock (flow == EDataFlow.eRender ? _renderEnumerationLock : _captureEnumerationLock)
            {
                lock (_deviceCacheLock)
                {
                    var cache = flow == EDataFlow.eRender ? _deviceCacheRender : _deviceCaptureRender;
                    var at = flow == EDataFlow.eRender ? _deviceCacheRenderAt : _deviceCacheCaptureAt;
                    int generation = flow == EDataFlow.eRender
                        ? Volatile.Read(ref _renderDeviceGeneration) : Volatile.Read(ref _captureDeviceGeneration);
                    int cachedGeneration = flow == EDataFlow.eRender ? _renderCacheGeneration : _captureCacheGeneration;
                    if (cache != null && cachedGeneration == generation && CompatEnv.TickCount64 - at < DeviceCacheTtlMs)
                        return CloneDevices(cache);
                }

                // 锁外枚举：不同方向可并行，不持有短时缓存锁。
                int enumerationGeneration = flow == EDataFlow.eRender
                    ? Volatile.Read(ref _renderDeviceGeneration) : Volatile.Read(ref _captureDeviceGeneration);
                var list = EnumerateDevices(flow);
                lock (_deviceCacheLock)
                {
                    if (flow == EDataFlow.eRender)
                    {
                        _deviceCacheRender = list;
                        _deviceCacheRenderAt = CompatEnv.TickCount64;
                        _renderCacheGeneration = enumerationGeneration;
                    }
                    else
                    {
                        _deviceCaptureRender = list;
                        _deviceCacheCaptureAt = CompatEnv.TickCount64;
                        _captureCacheGeneration = enumerationGeneration;
                    }
                    return CloneDevices(list);
                }
            }
        }

        private static List<AudioDeviceInfo> EnumerateDevices(EDataFlow flow)
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

        private static List<AudioDeviceInfo> CloneDevices(List<AudioDeviceInfo> src)
        {
            var list = new List<AudioDeviceInfo>(src.Count);
            for (int i = 0; i < src.Count; i++)
            {
                var d = src[i];
                list.Add(new AudioDeviceInfo { Id = d.Id, DisplayName = d.DisplayName, Flow = d.Flow });
            }
            return list;
        }

#if NET9_0_OR_GREATER
        private static readonly System.Threading.Lock _deviceCacheLock = new();
        private static readonly System.Threading.Lock _renderEnumerationLock = new(), _captureEnumerationLock = new();
#else
        private static readonly object _deviceCacheLock = new();
        private static readonly object _renderEnumerationLock = new(), _captureEnumerationLock = new();
#endif
        private static List<AudioDeviceInfo>? _deviceCacheRender;
        private static List<AudioDeviceInfo>? _deviceCaptureRender;
        private static long _deviceCacheRenderAt;
        private static long _deviceCacheCaptureAt;
        private static int _renderDeviceGeneration, _captureDeviceGeneration;
        private static int _renderCacheGeneration, _captureCacheGeneration;

        public static void InvalidateDeviceCache(EDataFlow? flow = null)
        {
            // Notification callbacks never wait on a lock held during a COM enumeration.
            if (flow != EDataFlow.eCapture) Interlocked.Increment(ref _renderDeviceGeneration);
            if (flow != EDataFlow.eRender) Interlocked.Increment(ref _captureDeviceGeneration);
        }
        // CompatEnv.TickCount64 的单位是毫秒；设备列表保留 3 秒。
        private const long DeviceCacheTtlMs = 3000;

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

        /// <summary>虚拟"系统默认"设备 ID：代表"跟随系统默认设备"（清除该应用的按应用持久化路由）。
        /// 用于保留设备筛选 / 快速切换面板 / 概览 / 全局切换的可选目标。</summary>
        public const string SystemDefaultDeviceId = "@@SYSTEM_DEFAULT@@";
        public const string SystemDefaultInputDeviceId = "@@SYSTEM_DEFAULT_IN@@"; // 输入"系统默认"虚拟项（与输出分开，便于独立显示/命名）
        public static bool IsSystemDefault(string? id) =>
            string.Equals(id, SystemDefaultDeviceId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, SystemDefaultInputDeviceId, StringComparison.OrdinalIgnoreCase);

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
                if (!refresh && _appsCache != null && _appsCacheGeneration == Volatile.Read(ref _appsGeneration)
                    && CompatEnv.TickCount64 - _appsCacheAt < 1000)
                    return _appsCache;
                int generation = Volatile.Read(ref _appsGeneration);
                bool includeIconPaths = Volatile.Read(ref _appIconMetadataEnabled) != 0;
                var apps = EnumerateApps(includeIconPaths);
                _appsCache = apps;
                _appsCacheIncludesIconPaths = includeIconPaths && Volatile.Read(ref _appIconMetadataEnabled) != 0;
                _appsCacheAt = CompatEnv.TickCount64;
                _appsCacheGeneration = generation;
                return apps;
            }
        }

        private static readonly object _appsLock = new();
        private static List<AudioAppInfo>? _appsCache;
        private static bool _appsCacheIncludesIconPaths;
        private static long _appsCacheAt;
        private static int _appsGeneration, _appsCacheGeneration;
        private static readonly object _appsReadGate = new();
        private static Task<List<AudioAppInfo>>? _appsReadTask;
        private static int _appsReadGeneration;
        private static int _appIconMetadataEnabled;

        public static void InvalidateAppsCache() => Interlocked.Increment(ref _appsGeneration);

        /// <summary>由现有窗口生命周期控制；后台目标解析不需要图标元数据。
        /// 开启时复用已有图标快照，仅基础快照或进行中的旧读取需要失效；不等待枚举锁。</summary>
        public static void SetAppIconMetadataEnabled(bool enabled)
        {
            int value = enabled ? 1 : 0;
            if (Interlocked.Exchange(ref _appIconMetadataEnabled, value) == value || !enabled) return;
            // An in-flight basic read must not be shared by the newly opened UI. TryEnter avoids
            // blocking the dispatcher on WASAPI; the existing generation check rejects that read.
            if (!Monitor.TryEnter(_appsLock)) { InvalidateAppsCache(); return; }
            try { if (!_appsCacheIncludesIconPaths) InvalidateAppsCache(); }
            finally { Monitor.Exit(_appsLock); }
        }

        /// <summary>Share ordinary reads without blocking the UI on the enumeration lock.
        /// Forced reads remain independent, and an invalidation starts a new read generation.</summary>
        public static Task<List<AudioAppInfo>> GetAppsAsync(bool refresh = false)
        {
            if (refresh) return Task.Run(() => GetApps(refresh: true));
            lock (_appsReadGate)
            {
                int generation = Volatile.Read(ref _appsGeneration);
                if (_appsReadTask != null && _appsReadGeneration == generation)
                {
                    if (!_appsReadTask.IsCompleted) return _appsReadTask;
                    if (_appsReadTask.Status == TaskStatus.RanToCompletion
                        && ReferenceEquals(_appsReadTask.Result, Volatile.Read(ref _appsCache))
                        && Volatile.Read(ref _appsCacheGeneration) == generation
                        && CompatEnv.TickCount64 - Interlocked.Read(ref _appsCacheAt) < 1000)
                        return _appsReadTask;
                }
                _appsReadGeneration = generation;
                return _appsReadTask = Task.Run(() => GetApps());
            }
        }

        private static List<AudioAppInfo> EnumerateApps(bool includeIconPaths)
        {
            var apps = new Dictionary<uint, AudioAppInfo>();
            var processes = new Dictionary<uint, (string? Name, bool Alive)>();
            var enumerator = CreateEnumerator();
            try
            {
                foreach (var flow in new[] { EDataFlow.eRender, EDataFlow.eCapture })
                {
                    var devices = GetDevicesCore(enumerator, flow);
                    foreach (var dev in devices)
                        CollectSessions(enumerator, dev.Id, apps, processes, includeIconPaths);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }

            return apps.Values
                .OrderByDescending(a => a.HasActiveSession) // 正在播放的应用优先
                .ThenBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static void CollectSessions(IMMDeviceEnumerator enumerator, string deviceId,
            Dictionary<uint, AudioAppInfo> apps, Dictionary<uint, (string? Name, bool Alive)> processes, bool includeIconPaths)
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

                                // 一个 PID 可能跨设备拥有多个会话；本轮只查一次进程名/存活状态。
                                if (!processes.TryGetValue(pid, out var process))
                                {
                                    string? name = ForegroundAppService.GetProcessNameSafe((int)pid);
                                    process = (name, name != null || IsProcessAlive(pid));
                                    processes[pid] = process;
                                }
                                string? processName = process.Name;
                                // 幽灵会话：进程已不存在（任务管理器也搜不到）但会话残留未标记 Expired。
                                // 过滤掉，避免完整界面应用列表出现无法操作的 PID 空项；权限受限的
                                // 真实进程（IsProcessAlive=true）仍保留 PID 显示。
                                if (!process.Alive) continue;

                                if (!apps.TryGetValue(pid, out var existing))
                                {
                                    existing = apps[pid] = new AudioAppInfo
                                    {
                                        ProcessId = pid,
                                        DisplayName = displayName,
                                        ProcessName = processName
                                    };
                                }
                                else if (existing.DisplayName == null && displayName != null)
                                {
                                    existing.DisplayName = displayName;
                                }
                                // Reuse the session already being read. Elevated/service processes
                                // may deny image-path queries even though their icon file is readable.
                                if (includeIconPaths && Volatile.Read(ref _appIconMetadataEnabled) != 0 && existing.IconPath == null
                                    && session.GetIconPath(out var iconPath) >= 0
                                    && !string.IsNullOrWhiteSpace(iconPath))
                                    existing.IconPath = iconPath;
                                // 存在正在播放(Active)的会话 → 标记，供列表排序优先展示
                                if (hrState >= 0 && state == AudioSessionState.Active)
                                    existing.HasActiveSession = true;
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

        /// <summary>进程是否真实存在：ArgumentException=进程已不存在（幽灵会话）；
        /// 其他异常（如权限受限）=进程存在但读不到信息，视为存活保留显示。</summary>
        private static bool IsProcessAlive(uint pid)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch
            {
                return true; // 权限受限等：进程存在，保留 PID 显示
            }
        }

        // ------------------------------------------------------------------
        // 按应用切换（Per-App Audio Routing 机制）
        // ------------------------------------------------------------------

        /// <summary>将进程的播放/录音设备持久化为指定设备；deviceId 为"系统默认"虚拟项时
        /// 清除该应用的持久化路由（SetPersistedDefaultAudioEndpoint 传 null），跟随系统默认设备。
        /// 返回 HRESULT 与结果。</summary>
        public static (bool Success, int HResult, string Message) ApplyEndpoint(
            int processId, EDataFlow flow, string deviceId)
        {
            try
            {
                using var config = new AudioPolicyConfig(flow);
                if (IsSystemDefault(deviceId))
                {
                    // 系统默认虚拟项：清除该应用持久化路由 → 跟随系统默认（与一键还原单应用方式一致）
                    int hrClear = config.SetDefaultEndPoint(null, processId);
                    if (hrClear >= 0)
                    {
                        InvalidateAppsCache();
                        return (true, hrClear, "成功");
                    }
                    return (false, hrClear, $"HRESULT: 0x{hrClear:X8} ({hrClear})");
                }
                // 设备 ID 可能是短 ID（IMMDevice.GetId()），统一包装为完整接口路径再调用
                string fullDeviceId = AudioPolicyConfig.EnsureFullDeviceId(deviceId, flow);
                int hr = config.SetDefaultEndPoint(fullDeviceId, processId);
                if (hr >= 0)
                {
                    InvalidateAppsCache();
                    return (true, hr, "成功");
                }
                return (false, hr, $"HRESULT: 0x{hr:X8} ({hr})");
            }
            catch (Exception ex)
            {
                return (false, 0, ex.Message);
            }
        }

        /// <summary>只清除端点路由，不删除可能同时保存音量/静音偏好的Windows音频属性存储。
        /// 全局接口成功即完成；计数仅用于逐进程兼容回退，不能据此确认已退出应用也已还原。</summary>
        public static (int OutOk, int InOk, int Total, bool Applied) ResetAllPersistedEndpointsChecked()
        {
            using var output = new AudioPolicyConfig(EDataFlow.eRender);
            try
            {
                if (output.ClearAllPersistedApplicationDefaultEndpoints() >= 0)
                {
                    InvalidateAppsCache();
                    return (0, 0, 0, true); // 原生全局调用不返回应用计数，不再扫描或重复写入。
                }
            }
            catch { /* 旧系统/异常时回退 */ }

            var pids = new HashSet<uint>();
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    try { if (p.Id > 0) pids.Add((uint)p.Id); }
                    catch { /* 权限不足的进程跳过 */ }
                    finally { p.Dispose(); }
                }
            }
            catch { /* 进程快照失败仍尝试可靠的音频会话目标。 */ }
            try
            {
                foreach (var app in GetApps(refresh: true))
                    if (app.ProcessId > 0 && app.ProcessId <= int.MaxValue) pids.Add(app.ProcessId);
            }
            catch { /* 会话读取失败仍处理已取得的进程。 */ }

            if (pids.Count == 0) return (0, 0, 0, false);
            using var input = new AudioPolicyConfig(EDataFlow.eCapture);
            int okOut = 0, okIn = 0, total = 0;
            foreach (var pid in pids)
            {
                total++;
                try
                {
                    if (output.SetDefaultEndPoint(null, (int)pid) >= 0) okOut++;
                }
                catch { /* 单个方向失败不阻止另一个方向与其他进程。 */ }
                try
                {
                    if (input.SetDefaultEndPoint(null, (int)pid) >= 0) okIn++;
                }
                catch { /* 单个方向失败不阻止其余目标。 */ }
            }
            if (okOut > 0 || okIn > 0) InvalidateAppsCache();
            return (okOut, okIn, total, false);
        }

        /// <summary>读取进程当前持久化的设备完整 ID（未设置返回 null）。</summary>
        public static string? GetPersistedEndpoint(int processId, EDataFlow flow)
        {
            try
            {
                using var config = new AudioPolicyConfig(flow);
                return config.GetDefaultEndPoint(processId);
            }
            catch
            {
                return null;
            }
        }
    }
}

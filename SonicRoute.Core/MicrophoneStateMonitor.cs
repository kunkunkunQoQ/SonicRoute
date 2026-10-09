using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.Core.Interop;

namespace SonicRoute.Core
{
    /// <summary>只读监听；所有 COM 枚举/读取/释放在串行化的 MTA 后台工作中执行。</summary>
    public sealed class MicrophoneStateMonitor : IDisposable
    {
        private readonly object _gate = new(), _requestGate = new();
        private readonly VolumeCallback _volumeCallback;
        private readonly DeviceCallback _deviceCallback;
        private IMMDeviceEnumerator? _enumerator;
        private readonly Dictionary<string, IAudioEndpointVolume> _volumes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _registered = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<IAudioEndpointVolume> _capture = new();
        private IAudioEndpointVolume? _default;
        private bool _deviceRegistered;
        private int _dirty = 1, _disposed;
        private Task<(bool All, bool Default)>? _read;
        public event Action? Invalidated;
        public Task Completion { get; private set; } = Task.CompletedTask;

        public MicrophoneStateMonitor()
        {
            _volumeCallback = new VolumeCallback(() => Signal(false));
            _deviceCallback = new DeviceCallback(() => Signal(true));
        }

        public Task<bool> ReadAsync(bool trackInput)
        {
            lock (_requestGate)
            {
                if (Volatile.Read(ref _disposed) != 0) return Task.FromResult(false);
                if (_read == null || _read.IsCompleted) _read = Task.Run(ReadCore);
                return SelectAsync(_read, trackInput);
            }
        }
        private static async Task<bool> SelectAsync(Task<(bool All, bool Default)> read, bool track)
        { var state = await read.ConfigureAwait(false); return state.All || (track && state.Default); }

        private (bool All, bool Default) ReadCore()
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return (false, false);
                try
                {
                    if (Interlocked.Exchange(ref _dirty, 0) != 0 || _enumerator == null) Rebuild();
                    bool all = _capture.Count > 0;
                    foreach (var volume in _capture)
                    {
                        Marshal.ThrowExceptionForHR(volume.GetMute(out int muted));
                        if (muted == 0) all = false;
                    }
                    bool input = false;
                    if (_default != null)
                    { Marshal.ThrowExceptionForHR(_default.GetMute(out int muted)); input = muted != 0; }
                    return (all, input);
                }
                catch
                {
                    Release();
                    Interlocked.Exchange(ref _dirty, 1);
                    // 驱动重启/COM 失效时保留旧状态计算方式，下次检查重建接口。
                    try { return (GlobalMicMuteService.IsAnyMuted(false), GlobalMicMuteService.IsAnyMuted(true)); }
                    catch { return (false, false); }
                }
            }
        }

        private void Rebuild()
        {
            Release();
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _deviceRegistered = _enumerator.RegisterEndpointNotificationCallback(_deviceCallback) >= 0;
            Marshal.ThrowExceptionForHR(_enumerator.EnumAudioEndpoints(EDataFlow.eCapture, DeviceState.ACTIVE, out var devices));
            try
            {
                Marshal.ThrowExceptionForHR(devices.GetCount(out uint count));
                for (uint i = 0; i < count; i++)
                {
                    if (devices.Item(i, out var device) < 0 || device == null) continue;
                    try { var volume = Activate(device); if (volume != null) _capture.Add(volume); }
                    finally { Free(device); }
                }
            }
            finally { Free(devices); }
            if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eMultimedia, out var input) >= 0 && input != null)
                try { _default = Activate(input); } finally { Free(input); }
            // 注册失败仍每 2s 校验；设备通知失败需要重建以发现新增/移除设备。
            if (!_deviceRegistered) Interlocked.Exchange(ref _dirty, 1);
        }

        private IAudioEndpointVolume? Activate(IMMDevice device)
        {
            if (device.GetId(out string id) < 0) return null;
            if (_volumes.TryGetValue(id, out var existing)) return existing;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, ComConstants.CLSCTX_ALL, IntPtr.Zero, out var obj) < 0 || obj == null) return null;
            var volume = (IAudioEndpointVolume)obj;
            _volumes.Add(id, volume);
            if (volume.RegisterControlChangeNotify(_volumeCallback) >= 0) _registered.Add(id);
            return volume;
        }

        private void Signal(bool topology)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (topology) Interlocked.Exchange(ref _dirty, 1);
            try { Invalidated?.Invoke(); } catch { }
        }
        private static void Free(object obj) { try { Marshal.ReleaseComObject(obj); } catch { } }
        private void Release()
        {
            foreach (var pair in _volumes)
            {
                if (_registered.Contains(pair.Key))
                    try { pair.Value.UnregisterControlChangeNotify(_volumeCallback); } catch { }
                Free(pair.Value);
            }
            _volumes.Clear(); _registered.Clear(); _capture.Clear(); _default = null;
            if (_enumerator != null)
            {
                if (_deviceRegistered)
                    try { _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback); } catch { }
                Free(_enumerator); _enumerator = null;
            }
            _deviceRegistered = false;
        }
        public void Dispose()
        {
            lock (_requestGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                Invalidated = null;
                Completion = Task.Run(() => { lock (_gate) Release(); });
            }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class VolumeCallback : IAudioEndpointVolumeCallback
        {
            private readonly Action _changed;
            internal VolumeCallback(Action changed) { _changed = changed; }
            public int OnNotify(IntPtr notification) { _changed(); return 0; }
        }
        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class DeviceCallback : IMMNotificationClient
        {
            private readonly Action _changed;
            internal DeviceCallback(Action changed) { _changed = changed; }
            public int OnDeviceStateChanged(string id, DeviceState state) { _changed(); return 0; }
            public int OnDeviceAdded(string id) { _changed(); return 0; }
            public int OnDeviceRemoved(string id) { _changed(); return 0; }
            public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id)
            { if (flow == EDataFlow.eCapture && role == ERole.eMultimedia) _changed(); return 0; }
            public int OnPropertyValueChanged(string id, PROPERTYKEY key) => 0;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.Core.Compat;
using SonicRoute.Core.Interop;

namespace SonicRoute.Core
{
    /// <summary>Panel-owned, read-only output notifications. No worker loop or timer.</summary>
    public sealed class OutputVolumeMonitor : IDisposable
    {
        private readonly object _gate = new();
        private sealed class ObservedVolume : IDisposable
        {
            internal readonly EndpointVolumeSubscription Subscription;
            internal int Dirty = 1;
            internal ObservedVolume(IMMDevice device, Action changed)
            {
                Subscription = new EndpointVolumeSubscription(device,
                    new MicrophoneStateMonitor.VolumeCallback(() =>
                    {
                        Interlocked.Exchange(ref Dirty, 1);
                        changed();
                    }));
            }
            public void Dispose() => Subscription.Dispose();
        }

        private readonly Dictionary<string, ObservedVolume> _volumes = new(StringComparer.OrdinalIgnoreCase);
        private readonly DeviceCallback _deviceCallback;
        private IMMDeviceEnumerator? _enumerator;
        private bool _deviceRegistered;
        private int _disposed, _needsRecovery, _needsTopologyRecovery = 1;
        public event Action<bool>? Invalidated;
        public bool NeedsRecovery => Volatile.Read(ref _needsRecovery) != 0;
        public bool NeedsTopologyRecovery => Volatile.Read(ref _needsTopologyRecovery) != 0;
        public Task Completion { get; private set; } = Task.CompletedTask;

        public OutputVolumeMonitor()
        {
            _deviceCallback = new DeviceCallback(() => Signal(true));
        }

        // Incremental reads return only new/changed endpoints. Explicit refreshes
        // still read every target, including when notification recovery is needed.
        public Task<Dictionary<string, (int Volume, bool Muted)>> ReadAsync(IEnumerable<string> deviceIds, bool refreshAll = true,
            IEnumerable<string>? refreshDeviceIds = null)
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in deviceIds)
                if (!string.IsNullOrWhiteSpace(id) && !AudioService.IsSystemDefault(id)) targets.Add(id);
            var requested = refreshDeviceIds == null ? null : new HashSet<string>(refreshDeviceIds, StringComparer.OrdinalIgnoreCase);
            return Task.Run(() => ReadCore(targets, refreshAll, requested));
        }

        private Dictionary<string, (int Volume, bool Muted)> ReadCore(HashSet<string> targets, bool refreshAll, HashSet<string>? requested)
        {
            var result = new Dictionary<string, (int Volume, bool Muted)>(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return result;
                bool recovery = false, topologyRecovery = false;
                try
                {
                    _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                    if (!_deviceRegistered)
                    {
                        try { _deviceRegistered = _enumerator.RegisterEndpointNotificationCallback(_deviceCallback) >= 0; }
                        catch { }
                    }
                    var removed = new List<string>();
                    foreach (var pair in _volumes) if (!targets.Contains(pair.Key)) removed.Add(pair.Key);
                    foreach (string id in removed) { _volumes[id].Dispose(); _volumes.Remove(id); }
                    foreach (string id in targets)
                    {
                        if (Volatile.Read(ref _disposed) != 0) break;
                        try
                        {
                            bool added = false;
                            if (!_volumes.TryGetValue(id, out var observed))
                            {
                                IMMDevice device;
                                int hr;
                                try { hr = _enumerator.GetDevice(id, out device); }
                                catch { topologyRecovery = true; throw; }
                                if (hr < 0) topologyRecovery = true;
                                try { Marshal.ThrowExceptionForHR(hr); observed = new ObservedVolume(device, () => Signal(false)); _volumes.Add(id, observed); added = true; }
                                finally { if (device != null) Marshal.ReleaseComObject(device); }
                            }
                            var subscription = observed.Subscription;
                            bool retry = !subscription.Registered;
                            if (retry && !added) subscription.TryRegister();
                            recovery |= !subscription.Registered;
                            int dirty = Interlocked.Exchange(ref observed.Dirty, 0);
                            if (!refreshAll && !retry && dirty == 0 && requested?.Contains(id) != true) continue;
                            Marshal.ThrowExceptionForHR(subscription.Volume.GetMasterVolumeLevelScalar(out float volume));
                            Marshal.ThrowExceptionForHR(subscription.Volume.GetMute(out int muted));
                            if (float.IsNaN(volume) || float.IsInfinity(volume)) throw new COMException("Invalid endpoint volume.");
                            result[id] = ((int)Math.Round(MathEx.Clamp(volume, 0f, 1f) * 100f), muted != 0);
                        }
                        catch
                        {
                            result[id] = (-1, false);
                            recovery = true;
                            if (_volumes.TryGetValue(id, out var failed)) { failed.Dispose(); _volumes.Remove(id); }
                        }
                    }
                    recovery |= !_deviceRegistered;
                }
                catch
                {
                    recovery = true;
                    Release();
                    foreach (string id in targets) result[id] = (-1, false);
                }
                Interlocked.Exchange(ref _needsTopologyRecovery, topologyRecovery || !_deviceRegistered ? 1 : 0);
                Interlocked.Exchange(ref _needsRecovery, recovery ? 1 : 0);
            }
            return result;
        }

        private void Signal(bool topology)
        {
            if (Volatile.Read(ref _disposed) == 0)
                try { Invalidated?.Invoke(topology); } catch { }
        }

        private void Release()
        {
            foreach (var subscription in _volumes.Values) subscription.Dispose();
            _volumes.Clear();
            if (_enumerator == null) return;
            if (_deviceRegistered) try { _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback); } catch { }
            try { Marshal.ReleaseComObject(_enumerator); } catch { }
            _enumerator = null;
            _deviceRegistered = false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Invalidated = null;
            Completion = Task.Run(() => { lock (_gate) Release(); });
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class DeviceCallback : IMMNotificationClient
        {
            private readonly Action _changed;
            internal DeviceCallback(Action changed) { _changed = changed; }
            private int Changed()
            {
                AudioService.InvalidateDeviceCache(EDataFlow.eRender);
                _changed();
                return 0;
            }
            public int OnDeviceAdded(string id) => Changed();
            public int OnDeviceRemoved(string id) => Changed();
            public int OnDeviceStateChanged(string id, DeviceState state) => Changed();
            public int OnPropertyValueChanged(string id, PROPERTYKEY key) => Changed();
            public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id)
                => flow == EDataFlow.eRender && role == ERole.eConsole ? Changed() : 0;
        }
    }
}

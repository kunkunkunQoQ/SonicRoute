using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.Core.Interop;

namespace SonicRoute.Core
{
    /// <summary>
    /// 快速面板专用的统一音频电平采样器。COM 对象仅在工作线程上获取、读取和释放；
    /// 面板关闭或设置关闭时 Stop 会结束线程，不留下后台轮询。
    /// </summary>
    public sealed class AudioMeterService : IDisposable
    {
        private const int SampleIntervalMs = 33;
        private const int RescanIntervalMs = 2000;
        private const float Attack = 0.65f;
        private const float Release = 0.15f;
        private static readonly Guid SessionManagerId = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        private readonly object _gate = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private HashSet<int> _targets = new HashSet<int>();
        private Thread? _worker;
        private bool _stopping;
        private bool _disposed;
        private int _targetsVersion;
        private HashSet<int>? _visibleTargets;
        private int _visibleVersion;
        private TaskCompletionSource<bool>? _completion;
        public Task Completion { get { lock (_gate) return _completion?.Task ?? Task.CompletedTask; } }

        /// <summary>来自工作线程；接收方须自行切换到 UI 线程。每个快照只包含目标 PID。</summary>
        public event Action<IReadOnlyDictionary<int, float>>? PeaksUpdated;

        public void Start(IEnumerable<int> processIds)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(AudioMeterService));
                _targets = NewTargets(processIds);
                _targetsVersion++;
                if (_worker != null) { _wake.Set(); return; }
                _stopping = false;
                _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _worker = new Thread(Run) { IsBackground = true, Name = "SonicRoute Audio Meter" };
                _worker.SetApartmentState(ApartmentState.MTA);
                _worker.Start();
            }
        }

        public void UpdateTargets(IEnumerable<int> processIds)
        {
            lock (_gate)
            {
                if (_disposed || _worker == null) return;
                var next = NewTargets(processIds);
                if (_targets.SetEquals(next)) return;
                _targets = next;
                _targetsVersion++;
                _wake.Set();
            }
        }

        /// <summary>Sampling demand is separate from subscriptions: scrolling never rebuilds the COM graph.</summary>
        public void UpdateVisibleTargets(IEnumerable<int> processIds)
        {
            var next = NewTargets(processIds);
            lock (_gate)
            {
                if (_disposed || (_visibleTargets != null && _visibleTargets.SetEquals(next))) return;
                _visibleTargets = next; _visibleVersion++;
                _wake.Set();
            }
        }

        public void Stop()
        {
            var completion = RequestStop(false);
            if (Thread.CurrentThread != _worker) completion.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            var completion = DisposeAsync();
            if (Thread.CurrentThread != _worker) completion.GetAwaiter().GetResult();
        }

        // UI owners signal immediately; native calls and COM release finish on the owning worker.
        public Task DisposeAsync() => RequestStop(true);

        private Task RequestStop(bool dispose)
        {
            lock (_gate)
            {
                if (_disposed) return _completion?.Task ?? Task.CompletedTask;
                _disposed = dispose;
                _stopping = true;
                if (dispose) PeaksUpdated = null;
                if (_worker == null)
                {
                    if (dispose) _wake.Dispose();
                    return Task.CompletedTask;
                }
                _wake.Set();
                return _completion!.Task;
            }
        }

        private static HashSet<int> NewTargets(IEnumerable<int> pids)
        {
            var result = new HashSet<int>();
            foreach (int pid in pids) if (pid > 0) result.Add(pid);
            return result;
        }

        private void Run()
        {
            MeterGraph? graph = null;
            var rawPeaks = new Dictionary<int, float>();
            var display = new Dictionary<int, (float Smoothed, float Published)>();
            var targets = new HashSet<int>();
            var watch = Stopwatch.StartNew();
            long nextScan = 0;
            int seenVersion = -1;
            int seenVisible = -1;
            HashSet<int>? visible = null;
            try
            {
                while (true)
                {
                    bool targetsChanged = false;
                    bool visibleChanged = false;
                    lock (_gate)
                    {
                        if (_stopping) break;
                        if (seenVisible != _visibleVersion)
                        {
                            visible = _visibleTargets; seenVisible = _visibleVersion; visibleChanged = true;
                        }
                        if (_targetsVersion != seenVersion)
                        {
                            // 发布后不修改目标集；更新只替换引用，工作线程可直接持有旧快照。
                            targets = _targets;
                            seenVersion = _targetsVersion;
                            targetsChanged = true;
                        }
                    }

                    if (targetsChanged || graph?.TopologyDirty == true
                        || (graph?.NeedsRecovery == true && watch.ElapsedMilliseconds >= nextScan))
                    {
                        graph?.Dispose(); graph = null;
                        if (targets.Count != 0) graph = new MeterGraph(targets, Wake, IsStopping);
                        if (targetsChanged)
                        {
                            rawPeaks.Clear();
                            foreach (int pid in targets) rawPeaks[pid] = 0f;
                            foreach (int pid in new List<int>(display.Keys))
                                if (!targets.Contains(pid)) display.Remove(pid);
                        }
                        nextScan = watch.ElapsedMilliseconds + RescanIntervalMs;
                    }

                    // 所有行都静音或列表为空时等待目标变化，不进行 30Hz 空轮询。
                    if (targets.Count == 0) { _wake.WaitOne(); continue; }

                    if (IsStopping()) break;
                    graph!.Pump();
                    bool active = false, fading = false;
                    foreach (int pid in targets) rawPeaks[pid] = 0f;
                    foreach (var handle in graph.Sessions.Values)
                    {
                        if (IsStopping()) break;
                        if (!handle.Active || (visible != null && !visible.Contains(handle.Pid))) continue;
                        active = true;
                        try
                        {
                            int hr = handle.Meter.GetPeakValue(out float peak);
                            if (hr < 0) throw new COMException("GetPeakValue failed", hr);
                            if (!float.IsNaN(peak))
                            {
#if NET8_0_OR_GREATER
                                ref float maximum = ref CollectionsMarshal.GetValueRefOrAddDefault(rawPeaks, handle.Pid, out _);
                                if (peak > maximum) maximum = Math.Min(1f, peak);
#else
                                if (peak > rawPeaks[handle.Pid]) rawPeaks[handle.Pid] = Math.Min(1f, peak);
#endif
                            }
                        }
                        catch { handle.Disconnected = 1; graph.RequireRecovery(); }
                    }
                    if (IsStopping()) break;
                    bool peaksChanged = targetsChanged || visibleChanged;
                    foreach (int pid in targets)
                    {
                        float raw = rawPeaks[pid];
#if NET8_0_OR_GREATER
                        // Worker-owned dictionaries: no resize/removal or callback while a ref is in use.
                        ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(display, pid, out bool existed);
#else
                        bool existed = display.TryGetValue(pid, out var state);
#endif
                        float previous = state.Smoothed;
                        float alpha = raw > previous ? Attack : Release;
                        float smoothed = visible != null && !visible.Contains(pid) ? 0f : previous + (raw - previous) * alpha;
                        if (smoothed < 0.001f) smoothed = 0f;
                        state.Smoothed = smoothed;
                        fading |= smoothed != 0f;
                        // 千分之一量化仅用于发布，内部平滑仍保留精度，误差最多 0.05%。
                        float next = (float)Math.Round(smoothed * 1000f) / 1000f;
                        if (!existed || next != state.Published)
                        {
                            state.Published = next;
                            peaksChanged = true;
                        }
#if !NET8_0_OR_GREATER
                        display[pid] = state;
#endif
                    }

                    // 静音段等电平不变时不生成快照，也不向 Dispatcher 投递无效帧。
                    // 任一电平变化时发送完整目标集，包含回零帧。
                    if (peaksChanged)
                    {
                        var snapshot = new Dictionary<int, float>(visible == null ? targets.Count : Math.Min(targets.Count, visible.Count));
                        foreach (int pid in targets)
                            if (visible == null || visible.Contains(pid)) snapshot[pid] = display[pid].Published;
                        try { PeaksUpdated?.Invoke(snapshot); }
                        catch { } // UI 已关闭或 Dispatcher 已停止，不影响 COM 清理
                    }
                    // 正常无活动会话并完成回零后只等通知。Active静音流仍按原33ms读取。
                    int wait = active || fading ? SampleIntervalMs : Timeout.Infinite;
                    if (graph.NeedsRecovery)
                    {
                        int recoveryWait = (int)Math.Max(1, nextScan - watch.ElapsedMilliseconds);
                        wait = wait == Timeout.Infinite ? recoveryWait : Math.Min(wait, recoveryWait);
                    }
                    _wake.WaitOne(wait);
                }
            }
            finally
            {
                try { graph?.Dispose(); }
                finally
                {
                    lock (_gate)
                    {
                        _worker = null;
                        if (_disposed) _wake.Dispose();
                        _completion!.TrySetResult(true);
                    }
                }
            }
        }

        private bool IsStopping() => Volatile.Read(ref _stopping);

        private void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        // 仅面板采样线程持有。通知只排队/标脏，不枚举、不等待、不释放最后一个COM引用。
        private sealed class MeterGraph : IDisposable
        {
            private readonly HashSet<int> _targets;
            private readonly Action _wake;
            private readonly ConcurrentQueue<(string Device, object Session)> _arrivals = new();
            private readonly List<ManagerHandle> _managers = new();
            private readonly List<string> _removed = new();
            private readonly MeterDeviceCallback _deviceCallback;
            private IMMDeviceEnumerator? _enumerator;
            private bool _deviceRegistered;
            private int _stopped, _topologyDirty, _recovery;
            private int _anonymousSession;
            internal readonly Dictionary<string, MeterHandle> Sessions = new(StringComparer.Ordinal);
            internal bool TopologyDirty => Volatile.Read(ref _topologyDirty) != 0;
            internal bool NeedsRecovery => Volatile.Read(ref _recovery) != 0;
            internal void RequireRecovery() => Interlocked.Exchange(ref _recovery, 1);

            private readonly Func<bool> _isStopping;
            internal MeterGraph(HashSet<int> targets, Action wake, Func<bool> isStopping)
            {
                _targets = targets; _wake = wake; _isStopping = isStopping;
                _deviceCallback = new MeterDeviceCallback(() =>
                {
                    if (Volatile.Read(ref _stopped) != 0) return;
                    Interlocked.Exchange(ref _topologyDirty, 1);
                    SessionVolumeService.InvalidateCache();
                    _wake();
                });
                try
                {
                    if (_isStopping()) return;
                    _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                    try { _deviceRegistered = _enumerator.RegisterEndpointNotificationCallback(_deviceCallback) >= 0; }
                    catch { RequireRecovery(); }
                    if (!_deviceRegistered) RequireRecovery();
                    Marshal.ThrowExceptionForHR(_enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.ACTIVE, out var devices));
                    try
                    {
                        Marshal.ThrowExceptionForHR(devices.GetCount(out uint count));
                        for (uint i = 0; i < count; i++)
                        {
                            if (_isStopping()) break;
                            IMMDevice? device = null;
                            try
                            {
                                Marshal.ThrowExceptionForHR(devices.Item(i, out device));
                                AddDevice(device);
                            }
                            catch { RequireRecovery(); }
                            finally { ReleaseCom(device); }
                        }
                    }
                    finally { ReleaseCom(devices); }
                }
                catch { RequireRecovery(); }
            }

            private void AddDevice(IMMDevice device)
            {
                Marshal.ThrowExceptionForHR(device.GetId(out string id));
                var iid = SessionManagerId;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, ComConstants.CLSCTX_ALL, IntPtr.Zero, out object obj));
                var manager = (IAudioSessionManager2)obj;
                var callback = new SessionCreatedCallback(session =>
                {
                    if (Volatile.Read(ref _stopped) != 0) return;
                    // RCW持有回调传入的COM引用，交给原MTA线程读取和释放。
                    _arrivals.Enqueue((id, session));
                    SessionVolumeService.InvalidateCache();
                    AudioService.InvalidateAppsCache();
                    _wake();
                });
                var handle = new ManagerHandle(manager, callback);
                _managers.Add(handle);
                try { handle.Registered = manager.RegisterSessionNotification(callback) >= 0; }
                catch { RequireRecovery(); }
                if (!handle.Registered) RequireRecovery();
                // 注册后GetCount开启通知；枚举与回调交叠按端点+会话实例身份去重。
                Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out var sessions));
                try
                {
                    Marshal.ThrowExceptionForHR(sessions.GetCount(out int count));
                    for (int i = 0; i < count; i++)
                    {
                        if (_isStopping()) break;
                        if (sessions.GetSession(i, out var session) < 0 || session == null) { RequireRecovery(); continue; }
                        AddSession(id, session);
                    }
                }
                finally { ReleaseCom(sessions); }
            }

            private void AddSession(string device, object value)
            {
                MeterHandle? handle = null;
                bool owned = false;
                try
                {
                    if (_isStopping()) return;
                    if (value is not IAudioSessionControl2 session) { RequireRecovery(); return; }
                    Marshal.ThrowExceptionForHR(session.GetProcessId(out uint pid));
                    if (pid == 0 || pid > int.MaxValue || !_targets.Contains((int)pid)) return;
                    string instance;
                    try
                    {
                        Marshal.ThrowExceptionForHR(session.GetSessionInstanceIdentifier(out instance));
                        if (string.IsNullOrEmpty(instance)) throw new COMException("Session identity unavailable.");
                        instance = "id:" + instance;
                    }
                    catch { RequireRecovery(); instance = "local:" + (++_anonymousSession); }
                    string key = device + "\n" + instance;
                    if (Sessions.ContainsKey(key)) return;
                    if (session is not IAudioMeterInformation meter) { RequireRecovery(); return; }
                    handle = new MeterHandle((int)pid, session, meter, _wake);
                    try { handle.Registered = session.RegisterAudioSessionNotification(handle.Callback) >= 0; }
                    catch { RequireRecovery(); }
                    if (!handle.Registered) RequireRecovery();
                    Sessions.Add(key, handle);
                    owned = true;
                }
                catch { RequireRecovery(); }
                finally
                {
                    if (!owned)
                    {
                        if (handle?.Registered == true)
                            try { handle.Session.UnregisterAudioSessionNotification(handle.Callback); } catch { }
                        ReleaseCom(value);
                    }
                }
            }

            internal void Pump()
            {
                while (_arrivals.TryDequeue(out var arrival)) AddSession(arrival.Device, arrival.Session);
                _removed.Clear();
                foreach (var pair in Sessions)
                {
                    var handle = pair.Value;
                    if (Volatile.Read(ref handle.Disconnected) == 0
                        && (Interlocked.Exchange(ref handle.StateDirty, 0) != 0 || !handle.Registered))
                    {
                        try
                        {
                            Marshal.ThrowExceptionForHR(handle.Session.GetState(out var state));
                            handle.State = state;
                        }
                        catch { handle.State = AudioSessionState.Active; RequireRecovery(); }
                    }
                    if (Volatile.Read(ref handle.Disconnected) != 0 || handle.State == AudioSessionState.Expired)
                    {
                        if (Volatile.Read(ref handle.Disconnected) != 0) RequireRecovery();
                        _removed.Add(pair.Key);
                        SessionVolumeService.InvalidateCache();
                        AudioService.InvalidateAppsCache();
                        handle.Dispose();
                    }
                }
                foreach (string key in _removed) Sessions.Remove(key);
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _stopped, 1);
                if (_deviceRegistered && _enumerator != null)
                    try { _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback); } catch { }
                foreach (var manager in _managers)
                {
                    if (manager.Registered)
                        try { manager.Manager.UnregisterSessionNotification(manager.Callback); } catch { }
                    ReleaseCom(manager.Manager);
                }
                foreach (var handle in Sessions.Values) handle.Dispose();
                Sessions.Clear(); _managers.Clear();
                while (_arrivals.TryDequeue(out var arrival)) ReleaseCom(arrival.Session);
                ReleaseCom(_enumerator); _enumerator = null;
            }
        }

        private sealed class ManagerHandle
        {
            internal readonly IAudioSessionManager2 Manager;
            internal readonly SessionCreatedCallback Callback;
            internal bool Registered;
            internal ManagerHandle(IAudioSessionManager2 manager, SessionCreatedCallback callback)
            { Manager = manager; Callback = callback; }
        }

        private sealed class MeterHandle : IDisposable
        {
            internal readonly int Pid;
            public readonly IAudioSessionControl2 Session;
            public readonly IAudioMeterInformation Meter;
            internal readonly SessionEventsCallback Callback;
            internal bool Registered;
            internal int StateDirty = 1, Disconnected;
            internal AudioSessionState State = AudioSessionState.Active;
            internal bool Active => Volatile.Read(ref Disconnected) == 0 && (!Registered || State == AudioSessionState.Active);
            internal MeterHandle(int pid, IAudioSessionControl2 session, IAudioMeterInformation meter, Action wake)
            {
                Pid = pid; Session = session; Meter = meter;
                Callback = new SessionEventsCallback(() => { Interlocked.Exchange(ref StateDirty, 1); wake(); },
                    () => { Interlocked.Exchange(ref Disconnected, 1); wake(); });
            }
            public void Dispose()
            {
                Callback.Stop();
                if (Registered)
                    try { Session.UnregisterAudioSessionNotification(Callback); } catch { }
                ReleaseCom(Session);
            }
        }

        private static void ReleaseCom(object? value)
        {
            if (value == null) return;
            try { Marshal.ReleaseComObject(value); } catch { }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class SessionCreatedCallback : IAudioSessionNotification
        {
            private readonly Action<object> _created;
            internal SessionCreatedCallback(Action<object> created) { _created = created; }
            public int OnSessionCreated(object session) { _created(session); return 0; }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class SessionEventsCallback : IAudioSessionEvents
        {
            private readonly Action _state, _disconnected;
            private int _stopped;
            internal SessionEventsCallback(Action state, Action disconnected) { _state = state; _disconnected = disconnected; }
            internal void Stop() => Interlocked.Exchange(ref _stopped, 1);
            public int OnStateChanged(AudioSessionState state) { if (_stopped == 0) _state(); return 0; }
            public int OnSessionDisconnected(int reason) { if (_stopped == 0) _disconnected(); return 0; }
            public int OnDisplayNameChanged(string name, ref Guid context) => 0;
            public int OnIconPathChanged(string path, ref Guid context) => 0;
            public int OnSimpleVolumeChanged(float volume, bool muted, ref Guid context) => 0;
            public int OnChannelVolumeChanged(uint count, IntPtr volumes, uint changed, ref Guid context) => 0;
            public int OnGroupingParamChanged(ref Guid grouping, ref Guid context) => 0;
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class MeterDeviceCallback : IMMNotificationClient
        {
            private readonly Action _changed;
            internal MeterDeviceCallback(Action changed) { _changed = changed; }
            public int OnDeviceStateChanged(string id, DeviceState state) { _changed(); return 0; }
            public int OnDeviceAdded(string id) { _changed(); return 0; }
            public int OnDeviceRemoved(string id) { _changed(); return 0; }
            public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id) => 0;
            public int OnPropertyValueChanged(string id, PROPERTYKEY key) => 0;
        }
    }
}

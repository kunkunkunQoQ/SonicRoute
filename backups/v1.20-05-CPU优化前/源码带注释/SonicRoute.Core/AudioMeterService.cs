using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SonicRoute.Core.Interop;

namespace SonicRoute.Core
{
    /// <summary>
    /// 简洁面板专用的统一音频电平采样器。COM 对象仅在工作线程上获取、读取和释放；
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

        public void Stop()
        {
            Thread? worker;
            lock (_gate)
            {
                worker = _worker;
                if (worker == null) return;
                _stopping = true;
                _wake.Set();
            }
            if (worker != Thread.CurrentThread) worker.Join();
            lock (_gate)
            {
                if (_worker == worker) _worker = null;
            }
        }

        public void Dispose()
        {
            Stop();
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _wake.Dispose();
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
            var meters = new Dictionary<int, List<MeterHandle>>();
            var display = new Dictionary<int, float>();
            var targets = new HashSet<int>();
            var watch = Stopwatch.StartNew();
            long nextScan = 0;
            int seenVersion = -1;
            try
            {
                while (true)
                {
                    bool targetsChanged = false;
                    lock (_gate)
                    {
                        if (_stopping) break;
                        if (_targetsVersion != seenVersion)
                        {
                            // 目标只在面板行或静音状态改变时复制，避免 30Hz 空转分配。
                            targets = new HashSet<int>(_targets);
                            seenVersion = _targetsVersion;
                            targetsChanged = true;
                        }
                    }

                    if (targetsChanged || watch.ElapsedMilliseconds >= nextScan)
                    {
                        // 旧 RCW 先释放，避免刷新时新旧引用指向同一会话对象。
                        ReleaseMeters(meters);
                        try { EnumerateMeters(targets, meters); }
                        catch { ReleaseMeters(meters); } // 设备切换时等待下次扫描
                        if (targetsChanged)
                        {
                            foreach (int pid in new List<int>(display.Keys))
                                if (!targets.Contains(pid)) display.Remove(pid);
                        }
                        nextScan = watch.ElapsedMilliseconds + RescanIntervalMs;
                    }

                    // 所有行都静音或列表为空时等待目标变化，不进行 30Hz 空轮询。
                    if (targets.Count == 0) { _wake.WaitOne(); continue; }

                    bool peaksChanged = targetsChanged;
                    foreach (int pid in targets)
                    {
                        float raw = 0f;
                        if (meters.TryGetValue(pid, out var sessions))
                        {
                            for (int i = sessions.Count - 1; i >= 0; i--)
                            {
                                try
                                {
                                    int hr = sessions[i].Meter.GetPeakValue(out float peak);
                                    if (hr < 0) throw new COMException("GetPeakValue failed", hr);
                                    if (!float.IsNaN(peak) && peak > raw) raw = Math.Min(1f, peak);
                                }
                                catch
                                {
                                    ReleaseCom(sessions[i].Session);
                                    sessions.RemoveAt(i);
                                }
                            }
                        }
                        display.TryGetValue(pid, out float previous);
                        float alpha = raw > previous ? Attack : Release;
                        float smoothed = previous + (raw - previous) * alpha;
                        if (smoothed < 0.001f) smoothed = 0f;
                        if (smoothed != previous) peaksChanged = true;
                        display[pid] = smoothed;
                    }

                    // 静音段等电平不变时不生成快照，也不向 Dispatcher 投递无效帧。
                    // 任一电平变化时发送完整目标集，包含回零帧。
                    if (peaksChanged)
                    {
                        var snapshot = new Dictionary<int, float>(targets.Count);
                        foreach (int pid in targets) snapshot[pid] = display[pid];
                        try { PeaksUpdated?.Invoke(snapshot); }
                        catch { } // UI 已关闭或 Dispatcher 已停止，不影响 COM 清理
                    }
                    if (_wake.WaitOne(SampleIntervalMs)) continue;
                }
            }
            finally { ReleaseMeters(meters); }
        }

        private static void EnumerateMeters(HashSet<int> targets, Dictionary<int, List<MeterHandle>> result)
        {
            if (targets.Count == 0) return;
            IMMDeviceEnumerator? enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.ACTIVE, out var devices) < 0 || devices == null) return;
                try
                {
                    if (devices.GetCount(out uint count) < 0) return;
                    for (uint i = 0; i < count; i++)
                    {
                        if (devices.Item(i, out var device) < 0 || device == null) continue;
                        try { EnumerateDevice(device, targets, result); }
                        catch { } // 单个设备断开不影响其他设备
                        finally { ReleaseCom(device); }
                    }
                }
                finally { ReleaseCom(devices); }
            }
            finally { ReleaseCom(enumerator); }
        }

        private static void EnumerateDevice(IMMDevice device, HashSet<int> targets, Dictionary<int, List<MeterHandle>> result)
        {
            var iid = SessionManagerId;
            if (device.Activate(ref iid, ComConstants.CLSCTX_ALL, IntPtr.Zero, out object managerObject) < 0 || managerObject == null) return;
            var manager = (IAudioSessionManager2)managerObject;
            try
            {
                if (manager.GetSessionEnumerator(out var sessionEnumerator) < 0 || sessionEnumerator == null) return;
                try
                {
                    if (sessionEnumerator.GetCount(out int count) < 0) return;
                    for (int i = 0; i < count; i++)
                    {
                        if (sessionEnumerator.GetSession(i, out var session) < 0 || session == null) continue;
                        bool owned = false;
                        try
                        {
                            if (session.GetProcessId(out uint processId) < 0 || processId == 0 || processId > int.MaxValue) continue;
                            int pid = (int)processId;
                            if (!targets.Contains(pid)) continue;
                            if (session.GetState(out var state) < 0 || state == AudioSessionState.Expired) continue;
                            if (session is not IAudioMeterInformation meter) continue;
                            if (!result.TryGetValue(pid, out var list)) result[pid] = list = new List<MeterHandle>();
                            list.Add(new MeterHandle(session, meter));
                            owned = true;
                        }
                        catch { } // 一条会话损坏时继续枚举
                        finally { if (!owned) ReleaseCom(session); }
                    }
                }
                finally { ReleaseCom(sessionEnumerator); }
            }
            finally { ReleaseCom(manager); }
        }

        private static void ReleaseMeters(Dictionary<int, List<MeterHandle>> meters)
        {
            foreach (var list in meters.Values)
                foreach (var handle in list) ReleaseCom(handle.Session);
            meters.Clear();
        }

        private static void ReleaseCom(object? value)
        {
            if (value == null) return;
            try { Marshal.ReleaseComObject(value); } catch { }
        }

        private sealed class MeterHandle
        {
            public readonly IAudioSessionControl2 Session;
            public readonly IAudioMeterInformation Meter;
            public MeterHandle(IAudioSessionControl2 session, IAudioMeterInformation meter)
            {
                Session = session;
                Meter = meter;
            }
        }
    }
}

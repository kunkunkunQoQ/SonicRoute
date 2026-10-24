using System;
using System.IO;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace SonicRoute
{
    /// <summary>WMI 原生进程事件，无 WITHIN / 内置轮询；按需订阅，注册和释放不占用 UI 线程。</summary>
    internal sealed class ProcessChangeSource : IDisposable
    {
        private readonly object _gate = new();
        private readonly Action<string?> _changed;
        private ManagementEventWatcher? _started, _stopped;
        private int _disposed, _available, _subscriptionFailed;
        internal bool IsAvailable => Volatile.Read(ref _available) != 0;
        internal Task Completion { get; private set; } = Task.CompletedTask;
        internal Task Startup { get; private set; } = Task.CompletedTask;

        internal ProcessChangeSource(Action<string?> changed) { _changed = changed; }

        internal Task StartAsync() => Startup = Task.Run(() =>
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                try
                {
                    _started = Create("Win32_ProcessStartTrace");
                    _stopped = Create("Win32_ProcessStopTrace");
                    Volatile.Write(ref _available, Volatile.Read(ref _subscriptionFailed) == 0 ? 1 : 0);
                }
                catch (Exception error)
                {
                    Release();
                    // 普通用户/系统策略可能禁止 Trace；不提权，不改成 WMI WITHIN 隐藏轮询。
                    AutoRuleScheduler.Log("进程事件不可用，应用启停规则使用兼容检测: " + error.Message);
                }
            }
            Signal(null); // 注册结果交给原 Dispatcher，决定是否需要兼容检测。
        });

        private ManagementEventWatcher Create(string eventClass)
        {
            var watcher = new ManagementEventWatcher("SELECT * FROM " + eventClass);
            watcher.EventArrived += EventArrived;
            watcher.Stopped += WatcherStopped;
            try { watcher.Start(); return watcher; }
            catch { watcher.Dispose(); throw; }
        }

        private void EventArrived(object sender, EventArrivedEventArgs e)
        {
            using (e.NewEvent)
            {
                try { Signal(Path.GetFileNameWithoutExtension(e.NewEvent["ProcessName"] as string)); }
                catch { Signal(null); }
            }
        }

        private void WatcherStopped(object sender, StoppedEventArgs e)
        {
            Volatile.Write(ref _subscriptionFailed, 1);
            Volatile.Write(ref _available, 0);
            Signal(null);
        }

        private void Signal(string? name)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _changed(name); } catch { }
        }

        private void Release()
        {
            Volatile.Write(ref _available, 0);
            foreach (var watcher in new[] { _started, _stopped })
            {
                if (watcher == null) continue;
                watcher.EventArrived -= EventArrived;
                watcher.Stopped -= WatcherStopped;
                try { watcher.Dispose(); } catch { }
            }
            _started = _stopped = null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Completion = Task.Run(() => { lock (_gate) Release(); });
        }
    }
}

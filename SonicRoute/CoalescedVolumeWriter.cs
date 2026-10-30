using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace SonicRoute
{
    // UI-owned batching; committed writes have a lifetime independent of the window's reads.
    internal sealed class CoalescedVolumeWriter : IDisposable
    {
        private sealed class Request
        {
            internal int Pid, Percent, Version;
            internal Action<int, int, bool, int> Readback = null!;
        }
        private readonly Dispatcher _dispatcher;
        private readonly Func<int, int, (bool Applied, int Actual)> _write;
        private readonly Dictionary<int, Request> _pending = new();
        private readonly ConcurrentDictionary<int, int> _latest = new();
        private readonly ConcurrentDictionary<int, int> _settled = new();
        private readonly object _queueGate = new();
        private readonly Dictionary<int, Request> _queued = new();
        private readonly DispatcherTimer _timer;
        private Task _tail = Task.CompletedTask;
        private int _version;
        private bool _throttled;
        private volatile bool _disposed;
        internal Task Completion { get { lock (_queueGate) return _tail; } }
        internal bool HasPending(int pid) => _latest.TryGetValue(pid, out int latest)
            && (!_settled.TryGetValue(pid, out int settled) || settled != latest);

        internal CoalescedVolumeWriter(Dispatcher dispatcher, Func<int, int, (bool Applied, int Actual)> write)
        {
            _dispatcher = dispatcher; _write = write;
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            { Interval = TimeSpan.FromMilliseconds(90) };
            _timer.Tick += Tick;
        }

        internal int Enqueue(int pid, int percent, Action<int, int, bool, int> readback)
        {
            if (_disposed) return -1;
            int version = ++_version;
            _latest[pid] = version;
            _pending[pid] = new Request { Pid = pid, Percent = percent, Version = version, Readback = readback };
            if (!_throttled) { Flush(); _throttled = true; }
            if (!_timer.IsEnabled) _timer.Start();
            return version;
        }

        private void Tick(object? sender, EventArgs e) { _timer.Stop(); Flush(); _throttled = false; }

        internal void Flush()
        {
            _timer.Stop();
            _throttled = false;
            if (_pending.Count == 0) return;
            var requests = new List<Request>(_pending.Values);
            _pending.Clear();
            lock (_queueGate)
            {
                foreach (var request in requests) _queued[request.Pid] = request;
                if (_tail.IsCompleted) _tail = Task.Run(Drain);
            }
        }

        private void Drain()
        {
            while (true)
            {
                List<Request> requests;
                lock (_queueGate)
                {
                    if (_queued.Count == 0) { _tail = Task.CompletedTask; return; }
                    requests = new List<Request>(_queued.Values);
                    _queued.Clear();
                }
                foreach (var request in requests)
                {
                    if (!_latest.TryGetValue(request.Pid, out int current) || current != request.Version) continue;
                    (bool Applied, int Actual) result;
                    try { result = _write(request.Pid, request.Percent); }
                    catch { result = (false, -1); }
                    _settled[request.Pid] = request.Version;
                    if (_disposed || _dispatcher.HasShutdownStarted) continue;
                    try
                    {
                        _dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!_disposed && _latest.TryGetValue(request.Pid, out int latest) && latest == request.Version)
                                request.Readback(request.Pid, request.Version, result.Applied, result.Actual);
                        }));
                    }
                    catch (InvalidOperationException) { }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Flush();
            _disposed = true;
            _timer.Stop(); _timer.Tick -= Tick;
        }
    }
}

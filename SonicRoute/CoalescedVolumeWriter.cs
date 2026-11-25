using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
#if !SONICROUTE_NATIVE
using System.Windows.Threading;
#endif

namespace SonicRoute
{
    // UI-owned batching; committed writes have a lifetime independent of the window's reads.
    internal sealed class CoalescedVolumeWriter : CoalescedVolumeWriter<int>
    {
#if !SONICROUTE_NATIVE
        internal CoalescedVolumeWriter(Dispatcher dispatcher, Func<int, int, (bool Applied, int Actual)> write)
            : this(action =>
            {
                if (dispatcher.HasShutdownStarted) return false;
                dispatcher.BeginInvoke(action); return true;
            }, (delay, action) => new DesktopDeadline(dispatcher, delay, action), write, pending => App.ProtectAudioWriteFromTrim(pending)) { }

        private sealed class DesktopDeadline : IDisposable
        {
            private readonly DispatcherTimer _timer;
            internal DesktopDeadline(Dispatcher dispatcher, TimeSpan delay, Action action)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = delay };
                _timer.Tick += (_, _) => { Dispose(); action(); };
                _timer.Start();
            }
            public void Dispose() => _timer.Stop();
        }
#endif
        internal CoalescedVolumeWriter(Func<Action, bool> post, Func<TimeSpan, Action, IDisposable> schedule,
            Func<int, int, (bool Applied, int Actual)> write, Action<Task>? protect = null)
            : base(post, schedule, write, protect) { }
    }

    internal class CoalescedVolumeWriter<TKey> : IDisposable where TKey : notnull
    {
        private sealed class Request
        {
            internal TKey Target = default!;
            internal int Percent, Version;
            internal volatile bool Settled;
            internal Action<TKey, int, bool, int> Readback = null!;
        }
        private readonly Func<Action, bool> _post;
        private readonly Func<TimeSpan, Action, IDisposable> _schedule;
        private readonly Action<Task>? _protect;
        private readonly Func<TKey, int, (bool Applied, int Actual)> _write;
        private readonly Dictionary<TKey, Request> _pending = new();
        private readonly ConcurrentDictionary<TKey, Request> _latest = new();
        private readonly object _queueGate = new();
        private readonly Dictionary<TKey, Request> _queued = new();
        private IDisposable? _timer;
        private Task _tail = Task.CompletedTask;
        private int _version;
        private bool _throttled;
        private volatile bool _disposed;
        internal Task Completion { get { lock (_queueGate) return _tail; } }
        internal bool HasPending(TKey target) => _latest.TryGetValue(target, out var request) && !request.Settled;

        private bool Retire(Request request) =>
            ((ICollection<KeyValuePair<TKey, Request>>)_latest).Remove(new(request.Target, request));

        internal CoalescedVolumeWriter(Func<Action, bool> post, Func<TimeSpan, Action, IDisposable> schedule,
            Func<TKey, int, (bool Applied, int Actual)> write, Action<Task>? protect = null)
        {
            _post = post; _schedule = schedule; _write = write; _protect = protect;
        }

        internal int Enqueue(TKey target, int percent, Action<TKey, int, bool, int> readback)
        {
            if (_disposed || (_latest.Count >= 128 && !_latest.ContainsKey(target))) return -1;
            int version = ++_version;
            var request = new Request { Target = target, Percent = percent, Version = version, Readback = readback };
            _latest[target] = _pending[target] = request;
            if (!_throttled) { Flush(); _throttled = true; }
            _timer ??= _schedule(TimeSpan.FromMilliseconds(90), () => { Flush(); _throttled = false; });
            return version;
        }



        internal void Cancel(TKey target)
        {
            if (_disposed) return;
            _pending.Remove(target);
            _latest.TryRemove(target, out _);
        }

        internal void Flush()
        {
            _timer?.Dispose(); _timer = null;
            _throttled = false;
            if (_pending.Count == 0) return;
            var requests = new List<Request>(_pending.Values);
            _pending.Clear();
            lock (_queueGate)
            {
                foreach (var request in requests) _queued[request.Target] = request;
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
                    if (!_latest.TryGetValue(request.Target, out var current) || current != request) continue;
                    (bool Applied, int Actual) result;
                    try { result = _write(request.Target, request.Percent); }
                    catch { result = (false, -1); }
                    request.Settled = true;
                    if (_disposed) { Retire(request); continue; }
                    try
                    {
                        if (!_post(() =>
                        {
                            if (Retire(request) && !_disposed)
                                request.Readback(request.Target, request.Version, result.Applied, result.Actual);
                        })) Retire(request);
                    }
                    catch (InvalidOperationException) { Retire(request); }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Flush();
            _protect?.Invoke(Completion);
            _disposed = true;
            _timer?.Dispose(); _timer = null;
        }
    }
}

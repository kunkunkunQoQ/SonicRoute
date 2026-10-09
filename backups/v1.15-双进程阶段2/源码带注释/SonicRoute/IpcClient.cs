using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SonicRoute
{
    /// <summary>
    /// UI 进程的 Named Pipe 客户端。
    /// - 请求管道：长期复用，串行写 + 单后台读循环（按消息 Id 分发到等待的请求），保证并发请求响应不串号。
    /// - 事件管道：长期连接，EvtHello 注册后由后台读循环触发 EventReceived；断开触发 Disconnected（App 负责重连）。
    /// 所有 IO 均在后台线程完成（ConfigureAwait(false)）；Request&lt;T&gt; 同步等待带超时兜底，不会死锁 UI 线程。
    /// </summary>
    public sealed class IpcClient : IDisposable
    {
        public string UiId { get; } = Guid.NewGuid().ToString("N");

        private const int RequestTimeoutMs = 5000;

        private NamedPipeClientStream? _req;
        private NamedPipeClientStream? _evt;
        private readonly SemaphoreSlim _reqWriteLock = new(1, 1);
        private readonly object _pendingLock = new();
        private readonly Dictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
        private int _nextId;
        private volatile bool _disposed;
        private Task? _reqReaderTask;

        /// <summary>事件接收（后台线程触发，UI 侧必须切 Dispatcher 处理）。</summary>
        public event Action<string, JsonElement?>? EventReceived;

        /// <summary>事件管道断开（Backend 退出/崩溃），App 决定重连或退出。</summary>
        public event Action? Disconnected;

        public bool Connected => _req != null && _evt != null && !_disposed;

        /// <summary>连接两条管道并注册事件连接；失败返回 false（内部清理）。</summary>
        public bool TryConnect(int timeoutMs = 3000)
        {
            if (_disposed) return false;
            NamedPipeClientStream? req = null, evt = null;
            try
            {
                req = new NamedPipeClientStream(".", IpcProtocol.ReqPipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                evt = new NamedPipeClientStream(".", IpcProtocol.EvtPipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                req.Connect(timeoutMs);
                evt.Connect(timeoutMs);
                _req = req;
                _evt = evt;
                // 事件连接注册（必须先于请求 Hello，保证服务端能绑定事件流）
                IpcProtocol.WriteAsync(evt, new IpcMessage { T = IpcProtocol.EvtHello, P = IpcProtocol.ToElement(new HelloDto { UiId = UiId }) })
                    .GetAwaiter().GetResult();
                _ = Task.Run(EvtLoopAsync);
                _reqReaderTask = Task.Run(ReqLoopAsync); // 请求管道单读循环
                return true;
            }
            catch
            {
                try { req?.Dispose(); } catch { }
                try { evt?.Dispose(); } catch { }
                _req = null;
                _evt = null;
                return false;
            }
        }

        private async Task EvtLoopAsync()
        {
            try
            {
                var evt = _evt;
                while (!_disposed && evt != null)
                {
                    var msg = await IpcProtocol.ReadAsync(evt, CancellationToken.None);
                    if (msg == null) break;
                    try { EventReceived?.Invoke(msg.T, msg.P); } catch { }
                }
            }
            catch { }
            finally
            {
                try { _evt?.Dispose(); } catch { }
                _evt = null;
                if (!_disposed)
                {
                    try { Disconnected?.Invoke(); } catch { }
                }
            }
        }

        /// <summary>请求管道单读循环：读取所有响应，按 Id 分发到等待中的请求（防止并发请求响应串号导致永久挂起）。</summary>
        private async Task ReqLoopAsync()
        {
            try
            {
                while (!_disposed)
                {
                    var req = _req;
                    if (req == null) break;
                    var msg = await IpcProtocol.ReadAsync(req, CancellationToken.None);
                    if (msg == null) break;
                    TaskCompletionSource<JsonElement?>? tcs = null;
                    lock (_pendingLock)
                    {
                        if (msg.Id.HasValue && _pending.TryGetValue(msg.Id.Value, out tcs))
                            _pending.Remove(msg.Id.Value);
                    }
                    tcs?.TrySetResult(msg.P);
                }
            }
            catch { }
            finally
            {
                // 读循环退出：让所有等待中的请求失败（避免永久挂起）
                lock (_pendingLock)
                {
                    foreach (var tcs in _pending.Values) tcs.TrySetResult(null);
                    _pending.Clear();
                }
            }
        }

        /// <summary>发送请求并等待响应（后台完成；返回响应 payload 的 JsonElement）。</summary>
        public async Task<JsonElement?> RequestAsync(string type, object? payload, CancellationToken ct = default)
        {
            if (_disposed || _req == null) return null;
            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock) _pending[id] = tcs;
            try
            {
                await _reqWriteLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_disposed || _req == null) return null;
                    var msg = new IpcMessage { T = type, Id = id, P = IpcProtocol.ToElement(payload) };
                    await IpcProtocol.WriteAsync(_req, msg).ConfigureAwait(false);
                }
                finally
                {
                    _reqWriteLock.Release();
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(RequestTimeoutMs);
                var done = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
                if (done != tcs.Task) return null; // 超时
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_pendingLock) _pending.Remove(id);
            }
        }

        /// <summary>同步等待响应（UI 线程可用；本地管道延迟 &lt;1ms，5s 超时兜底防挂起）。</summary>
        public T? Request<T>(string type, object? payload)
        {
            var r = RequestAsync(type, payload).GetAwaiter().GetResult();
            return r == null ? default : IpcProtocol.Get<T>(r);
        }

        /// <summary>即发即忘（SaveConfig 等高频写；请求管道串行写保证到达顺序，单读循环按 Id 分发）。</summary>
        public void RequestVoid(string type, object? payload)
        {
            _ = RequestAsync(type, payload);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _req?.Dispose(); } catch { }
            try { _evt?.Dispose(); } catch { }
            _req = null;
            _evt = null;
            lock (_pendingLock)
            {
                foreach (var tcs in _pending.Values) tcs.TrySetResult(null);
                _pending.Clear();
            }
        }
    }
}

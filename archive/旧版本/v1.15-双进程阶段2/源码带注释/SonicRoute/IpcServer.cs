using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SonicRoute
{
    /// <summary>
    /// 后台进程（Backend）的 Named Pipe 服务端。
    /// - 请求管道（Req）：UI 建立长期连接，串行请求/响应（Hello / 状态查询 / OSD / 配置等）。
    /// - 事件管道（Evt）：UI 建立长期连接并先发 EvtHello 注册，Backend 单向推送事件（OpenMainWindow / CurrentChanged / ConfigChanged 等）。
    /// UI 会话管理：单 UI 实例。二次启动的 UI 发 Hello 时若已有活动 UI → 通知旧 UI 激活窗口，并响应 ExistsUi=true 让新 UI 退出。
    /// </summary>
    public sealed class IpcServer : IDisposable
    {
        private readonly Func<string, JsonElement?, object?> _handler;
        private readonly object _lock = new();
        private readonly List<NamedPipeServerStream> _reqStreams = new();
        private readonly Dictionary<string, NamedPipeServerStream> _pendingEvt = new();
        private readonly SemaphoreSlim _evtWriteLock = new(1, 1);

        private NamedPipeServerStream? _activeEvt;
        private string? _activeUiId;
        private volatile bool _running;
        private CancellationTokenSource _cts = new();

        public IpcServer(Func<string, JsonElement?, object?> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>当前是否有活动 UI（事件连接存活）。</summary>
        public bool HasUi
        {
            get { lock (_lock) return _activeEvt != null; }
        }

        public void Start()
        {
            _running = true;
            _ = Task.Run(AcceptReqLoopAsync);
            _ = Task.Run(AcceptEvtLoopAsync);
        }

        // ---------- 请求管道 ----------

        private async Task AcceptReqLoopAsync()
        {
            while (_running && !_cts.IsCancellationRequested)
            {
                var s = new NamedPipeServerStream(IpcProtocol.ReqPipe, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await s.WaitForConnectionAsync(_cts.Token);
                    lock (_lock) _reqStreams.Add(s);
                    _ = HandleReqAsync(s);
                }
                catch
                {
                    try { s.Dispose(); } catch { }
                    break;
                }
            }
        }

        private async Task HandleReqAsync(NamedPipeServerStream s)
        {
            try
            {
                while (_running && !_cts.IsCancellationRequested)
                {
                    var msg = await IpcProtocol.ReadAsync(s, _cts.Token);
                    if (msg == null) break;

                    if (msg.T == IpcReq.Hello)
                    {
                        var hello = IpcProtocol.Get<HelloDto>(msg.P);
                        var resp = RegisterUi(hello?.UiId ?? "", hello?.Replace ?? false);
                        await IpcProtocol.WriteAsync(s, IpcProtocol.RespMsg(msg.Id, resp));
                        continue;
                    }

                    object? result = null;
                    try { result = _handler(msg.T, msg.P); }
                    catch { result = null; }
                    await IpcProtocol.WriteAsync(s, IpcProtocol.RespMsg(msg.Id, result));
                }
            }
            catch { }
            finally
            {
                lock (_lock) _reqStreams.Remove(s);
                try { s.Dispose(); } catch { }
            }
        }

        // ---------- 事件管道 ----------

        private async Task AcceptEvtLoopAsync()
        {
            while (_running && !_cts.IsCancellationRequested)
            {
                var s = new NamedPipeServerStream(IpcProtocol.EvtPipe, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await s.WaitForConnectionAsync(_cts.Token);
                    _ = HandleEvtAsync(s);
                }
                catch
                {
                    try { s.Dispose(); } catch { }
                    break;
                }
            }
        }

        private async Task HandleEvtAsync(NamedPipeServerStream s)
        {
            string? uiId = null;
            try
            {
                // 事件连接的第一条消息必须是 EvtHello（携带 UiId 注册）
                var msg = await IpcProtocol.ReadAsync(s, _cts.Token);
                if (msg == null || msg.T != IpcProtocol.EvtHello) return;
                uiId = IpcProtocol.Get<HelloDto>(msg.P)?.UiId;
                if (string.IsNullOrEmpty(uiId)) return;
                RegisterEvt(uiId, s);
                // 事件连接长驻：持续读取以感知断开（客户端退出/被杀 → ReadAsync 返回 null → 清理会话）
                // 服务端写事件走 SendEvent（_activeEvt），此循环只负责保持连接存活与断开检测
                while (!_cts.IsCancellationRequested)
                {
                    var m = await IpcProtocol.ReadAsync(s, _cts.Token);
                    if (m == null) break;
                }
            }
            catch { }
            finally
            {
                UnregisterEvt(uiId, s);
                try { s.Dispose(); } catch { }
            }
        }

        // ---------- UI 会话管理 ----------

        /// <summary>Hello 请求处理：
        /// - 已有活动 UI 且非替换（重复启动）→ 通知旧 UI 激活窗口，返回 ExistsUi=true 让新 UI 退出。
        /// - 已有活动 UI 且替换（--restart 重启应用，如清理/导入配置后）→ 通知旧 UI 退出，新 UI 接管（ExistsUi=false）。
        /// - 无活动 UI → 注册新 UI。</summary>
        private object? RegisterUi(string uiId, bool replace)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(uiId)) return new HelloRespDto { ExistsUi = false };
                bool exists = _activeUiId != null && !string.Equals(_activeUiId, uiId, StringComparison.Ordinal);
                if (exists)
                {
                    var old = _activeEvt;
                    if (old != null)
                        FireWrite(old, replace ? IpcProtocol.EventMsg(IpcEvt.Quit, null) : IpcProtocol.EventMsg(IpcEvt.ActivateWindow, null));
                    if (!replace)
                        return new HelloRespDto { ExistsUi = true };
                    // 替换：旧 UI 将退出，本 UI 接管活动会话
                    _activeUiId = uiId;
                    if (_pendingEvt.TryGetValue(uiId, out var st))
                    {
                        _activeEvt = st;
                        _pendingEvt.Remove(uiId);
                    }
                    return new HelloRespDto { ExistsUi = false };
                }
                _activeUiId = uiId;
                if (_pendingEvt.TryGetValue(uiId, out var st2))
                {
                    _activeEvt = st2;
                    _pendingEvt.Remove(uiId);
                }
                return new HelloRespDto { ExistsUi = false };
            }
        }

        private void RegisterEvt(string uiId, NamedPipeServerStream s)
        {
            lock (_lock)
            {
                if (string.Equals(uiId, _activeUiId, StringComparison.Ordinal)) _activeEvt = s;
                else _pendingEvt[uiId] = s; // 事件连接先到（Hello 未到）：挂起等待注册
            }
        }

        private void UnregisterEvt(string? uiId, NamedPipeServerStream s)
        {
            lock (_lock)
            {
                if (uiId != null && _pendingEvt.TryGetValue(uiId, out var p) && ReferenceEquals(p, s))
                    _pendingEvt.Remove(uiId);
                if (ReferenceEquals(s, _activeEvt))
                {
                    _activeEvt = null;
                    _activeUiId = null;
                }
            }
        }

        // ---------- 事件推送 ----------

        /// <summary>推送事件到活动 UI；无 UI 返回 false。写入 fire-and-forget + 全局写锁串行化（不阻塞 Backend UI 线程）。</summary>
        public bool SendEvent(string type, object? payload)
        {
            NamedPipeServerStream? s;
            lock (_lock) s = _activeEvt;
            if (s == null) return false;
            var msg = IpcProtocol.EventMsg(type, payload);
            _ = Task.Run(async () =>
            {
                await _evtWriteLock.WaitAsync();
                try { await IpcProtocol.WriteAsync(s, msg); }
                catch { } // UI 已断开：静默
                finally { _evtWriteLock.Release(); }
            });
            return true;
        }

        private void FireWrite(NamedPipeServerStream s, IpcMessage msg)
        {
            _ = Task.Run(async () =>
            {
                await _evtWriteLock.WaitAsync();
                try { await IpcProtocol.WriteAsync(s, msg); }
                catch { }
                finally { _evtWriteLock.Release(); }
            });
        }

        public void Dispose()
        {
            if (!_running) return;
            _running = false;
            try { _cts.Cancel(); } catch { }
            lock (_lock)
            {
                foreach (var s in _reqStreams.ToArray())
                {
                    try { s.Dispose(); } catch { }
                }
                _reqStreams.Clear();
                try { _activeEvt?.Dispose(); } catch { }
                _activeEvt = null;
                _activeUiId = null;
                foreach (var kv in _pendingEvt.ToArray())
                {
                    try { kv.Value.Dispose(); } catch { }
                }
                _pendingEvt.Clear();
            }
            try { _cts.Dispose(); } catch { }
        }
    }
}

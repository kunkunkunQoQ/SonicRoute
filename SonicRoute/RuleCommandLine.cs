using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
#if NET48
using System.Security.AccessControl;
#endif
using SonicRoute.Core;

namespace SonicRoute
{
    /// <summary>命令行直接执行保存的规则；复用手动执行语义，输出一行 JSON 和退出码。</summary>
    internal static partial class RuleCommandLine
    {
        internal static Result Error(int exitCode, string code) => new() { ExitCode = exitCode, Code = code };

        /// <summary>只匹配 Id / 完整名称；名称不唯一时拒绝执行，避免误执行另一条规则。</summary>
        internal static async Task<Result> ExecuteAsync(string selector)
        {
            try
            {
                var rules = await Task.Run(() =>
                {
                    AutoRuleStore.Invalidate(); // 显式请求读取最新磁盘规则，不增加文件监听。
                    return AutoRuleStore.LoadAll();
                });
                var rule = SelectRule(selector, rules, out var failure);
                if (rule == null) return failure!;
                var execution = await AutoRuleService.ExecuteWithResultAsync(rule, "command");
                // A temporary command host may exit immediately after returning its result.
                await AutoRuleService.FlushExecutionHistoryAsync();
                return new Result
                {
                    ExitCode = execution.Busy ? 5 : execution.Canceled ? 7 : execution.Failed > 0 ? 1 : 0,
                    Code = execution.Busy ? "rule_busy" : execution.Canceled ? "canceled"
                        : execution.Failed > 0 ? "step_failed" : "ok",
                    RuleId = rule.Id, RuleName = rule.Name,
                    Succeeded = execution.Succeeded, Failed = execution.Failed,
                    Stopped = execution.Stopped, Skipped = execution.Skipped
                };
            }
            catch (Exception ex)
            {
                AutoRuleScheduler.Log("CLI 执行失败: " + ex.Message);
                return Error(8, "internal_error");
            }
        }

        internal static string PipeName
        {
            get
            {
                using var identity = WindowsIdentity.GetCurrent();
                using var process = Process.GetCurrentProcess();
                string edition = AppRuntimeIdentity.Current.CommandEdition;
                return "SonicRoute.RuleCommand." + edition + "." + process.SessionId + "." + identity.User!.Value;
            }
        }

        internal static async Task<Result> SendAsync(string selector)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                // 等待正在启动的首实例创建端点；失败不另起执行进程，避免重复执行。
                await Task.Run(() => pipe.Connect(5000));
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (timeout.Token.Register(() => pipe.Dispose()))
                    await WriteFrameAsync(pipe, selector, timeout.Token);
                // 规则可包含长延时，不对执行结果施加固定超时，也不自动重试。
                string json = await ReadFrameAsync(pipe, CancellationToken.None);
                return ReadResult(json) ?? Error(6, "server_unavailable");
            }
            catch { return Error(6, "server_unavailable"); }
        }

        internal const int MaxFrameBytes = RuleCommandFrame.MaxFrameBytes;
        internal static Task WriteFrameAsync(Stream stream, string text, CancellationToken token) => RuleCommandFrame.WriteFrameAsync(stream, text, token);
        internal static Task<string> ReadFrameAsync(Stream stream, CancellationToken token) => RuleCommandFrame.ReadFrameAsync(stream, token);

        internal static void WriteResult(Result result) => CommandOutput.Write(EncodeResult(result));

        internal static void WriteHelp() => CommandOutput.Write(L10n.T("Auto.CliHelp"));


    }

    /// <summary>同一用户、登录会话和版本的按需命令通道；异步等待连接，没有轮询。</summary>
    internal sealed class RuleCommandServer : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly Task _audioWarmup;
        private readonly object _gate = new();
        private readonly HashSet<NamedPipeServerStream> _clients = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource<bool> _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private NamedPipeServerStream? _listener;
        private volatile bool _stopping;
        private volatile bool _disposed;

        internal RuleCommandServer(Dispatcher dispatcher, Task audioWarmup)
        {
            _dispatcher = dispatcher;
            _audioWarmup = audioWarmup;
            _listener = CreatePipe();
            _ = AcceptAsync();
        }

        private static NamedPipeServerStream CreatePipe()
        {
#if NET48
            using var identity = WindowsIdentity.GetCurrent();
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(RuleCommandLine.PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, security);
#else
            return new NamedPipeServerStream(RuleCommandLine.PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#endif
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    NamedPipeServerStream listener;
                    lock (_gate)
                    {
                        if (_stopping || _listener == null) return;
                        listener = _listener;
                    }
                    await listener.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_stopping) return;
                        _listener = null;
                        _clients.Add(listener);
                    }
                    _ = HandleAsync(listener);
                    lock (_gate)
                    {
                        if (_stopping) return;
                        _listener = CreatePipe();
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_stopping) AutoRuleScheduler.Log("CLI 通道停止: " + ex.Message);
            }
        }

        private async Task HandleAsync(NamedPipeServerStream pipe)
        {
            try
            {
                string selector;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    using (timeout.Token.Register(() => pipe.Dispose()))
                        selector = await RuleCommandLine.ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
                }
                var result = string.IsNullOrWhiteSpace(selector)
                    ? RuleCommandLine.Error(2, "invalid_arguments") : await ExecuteAsync(selector).ConfigureAwait(false);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    using (timeout.Token.Register(() => pipe.Dispose()))
                        await RuleCommandLine.WriteFrameAsync(pipe, RuleCommandLine.EncodeResult(result), timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (!_disposed) AutoRuleScheduler.Log("CLI 请求失败: " + ex.Message);
            }
            finally
            {
                pipe.Dispose();
                lock (_gate)
                {
                    _clients.Remove(pipe);
                    if (_stopping && _clients.Count == 0) _idle.TrySetResult(true);
                }
            }
        }

        internal async Task<RuleCommandLine.Result> ExecuteAsync(string selector)
        {
            await _audioWarmup.ConfigureAwait(false);
            if (_dispatcher.HasShutdownStarted) return RuleCommandLine.Error(7, "canceled");
            return await _dispatcher.InvokeAsync(() => RuleCommandLine.ExecuteAsync(selector))
                .Task.Unwrap().ConfigureAwait(false);
        }

        /// <summary>临时执行进程收尾：停止接受请求，已接受的其他请求返回结果后再退出。</summary>
        internal Task StopAcceptingAsync()
        {
            lock (_gate)
            {
                _stopping = true;
                _listener?.Dispose(); _listener = null;
                if (_clients.Count == 0) _idle.TrySetResult(true);
                return _idle.Task;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _stopping = true;
                _lifetime.Cancel();
                _listener?.Dispose(); _listener = null;
                foreach (var pipe in _clients) pipe.Dispose();
                if (_clients.Count == 0) _idle.TrySetResult(true);
            }
        }
    }
}

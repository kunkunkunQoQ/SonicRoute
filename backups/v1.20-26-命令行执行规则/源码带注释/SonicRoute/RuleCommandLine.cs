using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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
    internal static class RuleCommandLine
    {
        internal enum Mode { None, Run, Help, Invalid }

        internal sealed class Result
        {
            public Result() { } // System.Text.Json 在两个 TFM 下均使用公共无参构造。
            public int ExitCode { get; set; }
            public string Code { get; set; } = "";
            public string RuleId { get; set; } = "";
            public string RuleName { get; set; } = "";
            public int Succeeded { get; set; }
            public int Failed { get; set; }
        }

        internal static Mode Parse(string[] args, out string selector)
        {
            selector = "";
            if (args.Length == 1 && (EqualsArg(args[0], "--help") || EqualsArg(args[0], "-h")))
                return Mode.Help;
            if (args.Length == 2 && EqualsArg(args[0], "--run-rule") && !string.IsNullOrWhiteSpace(args[1]))
            {
                selector = args[1].Trim();
                return Encoding.UTF8.GetByteCount(selector) <= MaxFrameBytes ? Mode.Run : Mode.Invalid;
            }
            // 保留原 --main/--panel 启动行为；混用规则调用和界面参数视为参数错误。
            return args.Any(a => EqualsArg(a, "--run-rule") || EqualsArg(a, "--help") || EqualsArg(a, "-h")
                || a.StartsWith("--run-rule=", StringComparison.OrdinalIgnoreCase)) ? Mode.Invalid : Mode.None;
        }

        private static bool EqualsArg(string value, string expected) =>
            string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

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
                bool isGuid = Guid.TryParse(selector, out var requestedId);
                var matches = rules.Where(r => string.Equals(r.Id, selector, StringComparison.OrdinalIgnoreCase)
                    || (isGuid && Guid.TryParse(r.Id, out var id) && id == requestedId)).ToList();
                if (matches.Count == 0)
                    matches = rules.Where(r => string.Equals(r.Name, selector, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0) return Error(3, "rule_not_found");
                if (matches.Count != 1) return Error(4, "ambiguous_rule");
                var rule = matches[0];
                var execution = await AutoRuleService.ExecuteWithResultAsync(rule);
                return new Result
                {
                    ExitCode = execution.Busy ? 5 : execution.Canceled ? 7 : execution.Failed > 0 ? 1 : 0,
                    Code = execution.Busy ? "rule_busy" : execution.Canceled ? "canceled"
                        : execution.Failed > 0 ? "step_failed" : "ok",
                    RuleId = rule.Id, RuleName = rule.Name,
                    Succeeded = execution.Succeeded, Failed = execution.Failed
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
#if NET48
                const string edition = "Legacy";
#else
                const string edition = "Lite";
#endif
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
                return JsonSerializer.Deserialize<Result>(json) ?? Error(6, "server_unavailable");
            }
            catch { return Error(6, "server_unavailable"); }
        }

        internal const int MaxFrameBytes = 16 * 1024;

        internal static async Task WriteFrameAsync(Stream stream, string text, CancellationToken token)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            if (body.Length == 0 || body.Length > MaxFrameBytes) throw new InvalidDataException();
            byte[] header = BitConverter.GetBytes(body.Length);
            await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        internal static async Task<string> ReadFrameAsync(Stream stream, CancellationToken token)
        {
            byte[] header = new byte[4];
            await ReadFullAsync(stream, header, token).ConfigureAwait(false);
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException();
            byte[] body = new byte[length];
            await ReadFullAsync(stream, body, token).ConfigureAwait(false);
            return new UTF8Encoding(false, true).GetString(body);
        }

        private static async Task ReadFullAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }

        internal static void WriteResult(Result result) => WriteOutput(JsonSerializer.Serialize(result));

        internal static void WriteHelp() => WriteOutput(L10n.T("Auto.CliHelp"));

        private static void WriteOutput(string output)
        {
            try
            {
                // GUI 子系统没有默认控制台；保留调用方已有重定向，否则附着父控制台。
                if (GetFileType(GetStdHandle(-11)) == 0) AttachConsole(uint.MaxValue);
                var handle = GetStdHandle(-11);
                if (GetFileType(handle) == 2)
                {
                    string line = output + Environment.NewLine;
                    if (WriteConsole(handle, line, (uint)line.Length, out _, IntPtr.Zero)) return;
                }
                using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                writer.WriteLine(output);
                writer.Flush();
            }
            catch { } // 由快捷方式启动时可无控制台；退出码仍可由调用者读取。
        }

        [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint processId);
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
        [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
        [DllImport("kernel32.dll", EntryPoint = "WriteConsoleW", CharSet = CharSet.Unicode)]
        private static extern bool WriteConsole(IntPtr handle, string text, uint length, out uint written, IntPtr reserved);
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
                        await RuleCommandLine.WriteFrameAsync(pipe, JsonSerializer.Serialize(result), timeout.Token).ConfigureAwait(false);
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

using System.IO.Pipes;
using SonicRoute.Core;

namespace SonicRoute.Native.Product;

internal sealed record ControlReply(string Text, Action? AfterReply = null);

/// <summary>The owner of the instance mutex lives on the host STA, from startup through final shutdown.</summary>
internal sealed class ProductInstance : IDisposable
{
    private readonly CandidateEnvironment _profile;
    private Mutex? _owner;
    private PipeListener? _rules;
    private PipeListener? _control;

    internal ProductInstance(CandidateEnvironment profile)
    {
        _profile = profile;
        var mutex = new Mutex(true, profile.MutexName, out bool created);
        if (created) _owner = mutex;
        else mutex.Dispose();
    }
    internal bool IsPrimary => _owner != null;

    internal void Listen(Func<string, Task<string>> executeRule, Func<string, Task<ControlReply>> executeControl)
    {
        if (!IsPrimary || _rules != null) throw new InvalidOperationException("Listener requires the primary instance.");
        _rules = new PipeListener(_profile.RulePipeName, async s => new ControlReply(await executeRule(s).ConfigureAwait(false)));
        try { _control = new PipeListener(_profile.ControlPipeName, executeControl); }
        catch { _rules.Dispose(); _rules = null; throw; }
    }

    internal async Task<string> SendRuleAsync(string selector)
    {
        try { return await SendAsync(_profile.RulePipeName, selector).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return RuleCommandLine.EncodeResult(new() { ExitCode = 6, Code = "server_unavailable" });
        }
    }
    internal Task<string> SendControlAsync(string command) => SendAsync(_profile.ControlPipeName, command);

    private static async Task<string> SendAsync(string name, string command)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000).ConfigureAwait(false);
        using (var writing = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await RuleCommandFrame.WriteFrameAsync(pipe, command, writing.Token).ConfigureAwait(false);
        // Saved rules can intentionally run for minutes. A submitted rule is never resent.
        return await RuleCommandFrame.ReadFrameAsync(pipe, CancellationToken.None).ConfigureAwait(false);
    }

    internal Task DrainTemporaryClientsAsync() => Task.WhenAll(
        _rules?.StopAcceptingAsync() ?? Task.CompletedTask, _control?.StopAcceptingAsync() ?? Task.CompletedTask);

    public void Dispose()
    {
        _rules?.Dispose();
        _control?.Dispose();
        if (_owner == null) return;
        _owner.ReleaseMutex();
        _owner.Dispose();
        _owner = null;
    }

    private sealed class PipeListener : IDisposable
    {
        private const int Capacity = 8;
        private readonly string _name;
        private readonly Func<string, Task<ControlReply>> _execute;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationTokenSource _acceptStop = new();
        private readonly object _gate = new();
        private readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _clients;
        private bool _draining;
        private readonly CancellationToken _token;
        private readonly SemaphoreSlim _capacity = new(Capacity, Capacity);
        private readonly Task _accept;
        private int _disposing;

        internal PipeListener(string name, Func<string, Task<ControlReply>> execute)
        {
            _name = name;
            _execute = execute;
            _token = _stop.Token;
            // Creating the first server synchronously makes Listen a real readiness boundary.
            _accept = AcceptAsync(Create());
        }

        private NamedPipeServerStream Create() => new(_name, PipeDirection.InOut, Capacity,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        private async Task AcceptAsync(NamedPipeServerStream first)
        {
            NamedPipeServerStream? next = first;
            try
            {
                while (true)
                {
                    await _capacity.WaitAsync(_acceptStop.Token).ConfigureAwait(false);
                    next ??= Create();
                    await next.WaitForConnectionAsync(_acceptStop.Token).ConfigureAwait(false);
                    var connected = next;
                    next = null;
                    lock (_gate) ++_clients;
                    _ = HandleAsync(connected);
                }
            }
            catch (OperationCanceledException) when (_acceptStop.IsCancellationRequested) { }
            catch (Exception e) { ExceptionDiagnostics.Record("IPC.Listen", e); }
            finally { next?.Dispose(); }
        }

        private async Task HandleAsync(NamedPipeServerStream pipe)
        {
            ControlReply? reply = null;
            try
            {
                string command;
                using (var reading = CancellationTokenSource.CreateLinkedTokenSource(_token))
                {
                    reading.CancelAfter(TimeSpan.FromSeconds(10));
                    command = await RuleCommandFrame.ReadFrameAsync(pipe, reading.Token).ConfigureAwait(false);
                }
                reply = await _execute(command).ConfigureAwait(false);
                using var writing = CancellationTokenSource.CreateLinkedTokenSource(_token);
                writing.CancelAfter(TimeSpan.FromSeconds(10));
                await RuleCommandFrame.WriteFrameAsync(pipe, reply.Text, writing.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            catch (Exception e) { ExceptionDiagnostics.Record("IPC.Command", e); }
            finally
            {
                pipe.Dispose();
                _capacity.Release();
                lock (_gate)
                {
                    if (--_clients == 0 && _draining) _idle.TrySetResult();
                }
                // Once acknowledged by a worker, shutdown must proceed even if its caller disconnected.
                try { reply?.AfterReply?.Invoke(); }
                catch (Exception e) { ExceptionDiagnostics.Record("IPC.AfterReply", e); }
            }
        }

        internal async Task StopAcceptingAsync()
        {
            _acceptStop.Cancel();
            await _accept.ConfigureAwait(false);
            lock (_gate)
            {
                _draining = true;
                if (_clients == 0) _idle.TrySetResult();
            }
            await _idle.Task.ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposing, 1) != 0) return;
            _acceptStop.Cancel();
            _stop.Cancel();
            // Only the overlapped accept is synchronously drained. Already submitted business
            // commands belong to the application lifetime and may still be completing on its owner.
            _accept.GetAwaiter().GetResult();
            _acceptStop.Dispose();
            _stop.Dispose();
        }
    }
}

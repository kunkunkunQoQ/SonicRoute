using SonicRoute.Native.Monitoring;
namespace SonicRoute;

internal sealed class ProcessChangeSource(Action<string?> changed) : IDisposable
{
    // A slow WMI call may outlive a rule revision. One registration can be created or
    // retired at a time. A newer owner uses snapshot/backoff until that call releases COM.
    private static readonly object registrationGate = new();
    private static TaskCompletionSource? active;
    private TaskCompletionSource? owned;
    private ProcessTraceSubscription? registration;
    private int available, stopped, disposed;
    internal bool IsAvailable => Volatile.Read(ref available) == 1;
    internal Task Startup { get; private set; } = Task.CompletedTask;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal Task StartAsync() => Startup = StartCore();

    private async Task StartCore()
    {
        lock (registrationGate)
        {
            if (active != null) return;
            active = owned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            registration = await ProcessTraceSubscription.StartAsync(
                item => Signal(Path.GetFileNameWithoutExtension(item.ProcessName)),
                hr => { Interlocked.Exchange(ref stopped, 1); Volatile.Write(ref available, 0); Signal(null); }).ConfigureAwait(false);
            Volatile.Write(ref available, Volatile.Read(ref stopped) == 0 && Volatile.Read(ref disposed) == 0 ? 1 : 0);
        }
        catch (Exception error)
        {
            ReleaseOwnership();
            AutoRuleScheduler.Log($"WMI process trace unavailable 0x{error.HResult:X8}; using required process snapshot fallback.");
        }
        finally { Signal(null); }
    }

    private void Signal(string? name)
    {
        if (Volatile.Read(ref disposed) == 0) changed(name);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Volatile.Write(ref available, 0);
        Completion = ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        try
        {
            await Startup.ConfigureAwait(false);
            if (registration is not null) await registration.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) { AutoRuleScheduler.Log($"WMI unregister: 0x{error.HResult:X8}"); }
        finally { ReleaseOwnership(); }
    }

    private void ReleaseOwnership()
    {
        lock (registrationGate)
        {
            if (owned == null) return;
            active = null;
            owned.TrySetResult();
            owned = null;
        }
    }

    internal static Task WhenReleased()
    {
        lock (registrationGate) return active?.Task ?? Task.CompletedTask;
    }
}

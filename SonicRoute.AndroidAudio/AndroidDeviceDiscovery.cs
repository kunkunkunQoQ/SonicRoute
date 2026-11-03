namespace SonicRoute.AndroidAudio;

/// <summary>Shares an on-demand scan without sharing caller cancellation or publishing an obsolete scan.</summary>
public sealed class AndroidDeviceDiscovery
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<AndroidDevice>>> _read;
    private IReadOnlyList<AndroidDevice>? _devices;
    private long _revision;
    private Scan? _scan;
    private sealed class Scan(long revision)
    {
        internal readonly long Revision = revision;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<IReadOnlyList<AndroidDevice>> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Readers;
    }
    public AndroidDeviceDiscovery(Func<CancellationToken, Task<IReadOnlyList<AndroidDevice>>> read) => _read = read;
    public event Action? Changed;
    public IReadOnlyList<AndroidDevice>? Devices { get { lock (_gate) return _devices; } }
    public void Publish(IReadOnlyList<AndroidDevice> devices)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_devices, devices)) return;
            _devices = devices; ++_revision;
        }
        Changed?.Invoke();
    }
    public void Invalidate() { lock (_gate) ++_revision; }
    public Task<IReadOnlyList<AndroidDevice>> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Scan scan; bool start;
        lock (_gate)
        {
            start = _scan == null || _scan.Completion.Task.IsCompleted || _scan.Revision != _revision;
            if (start) _scan = new Scan(_revision);
            scan = _scan!; ++scan.Readers;
        }
        if (start) _ = ReadCoreAsync(scan);
        return WaitAsync(scan, token);
    }
    private async Task ReadCoreAsync(Scan scan)
    {
        try
        {
            var devices = await _read(scan.Cancellation.Token).ConfigureAwait(false);
            scan.Cancellation.Token.ThrowIfCancellationRequested();
            bool changed;
            lock (_gate)
            {
                changed = scan.Revision == _revision;
                if (changed) { _devices = devices; ++_revision; }
                devices = _devices ?? devices;
            }
            if (changed) Changed?.Invoke();
            scan.Completion.TrySetResult(devices);
        }
        catch (OperationCanceledException) { scan.Completion.TrySetCanceled(); }
        catch (Exception ex) { scan.Completion.TrySetException(ex); }
    }
    private async Task<IReadOnlyList<AndroidDevice>> WaitAsync(Scan scan, CancellationToken token)
    {
        try { return await scan.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            bool last;
            lock (_gate)
            {
                last = --scan.Readers == 0;
                if (last && ReferenceEquals(_scan, scan)) _scan = null;
            }
            if (last)
            {
                if (!scan.Completion.Task.IsCompleted) scan.Cancellation.Cancel();
                _ = scan.Completion.Task.ContinueWith(_ => scan.Cancellation.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}

namespace SonicRoute.AndroidAudio;

public enum VirtualDisplayState { Starting, Active, OnPhone, Recovering, Missing, Unknown }

/// <summary>Video/control only. Audio stays in AndroidAudioManager's in-process sources.</summary>
public interface IVirtualDisplayClient : IAsyncDisposable
{
    Task StartAsync(AndroidDevice device, AndroidApp app, int x, int y, CancellationToken token);
    Task Completion { get; }
    int ProcessId => 0;
    VirtualDisplayState State => VirtualDisplayState.Active;
    event Action<VirtualDisplayState>? StateChanged { add { } remove { } }
    void Activate();
    void Restore() => Activate();
}

public sealed class AndroidVirtualDisplayManager : IAsyncDisposable
{
    private sealed class Entry(IVirtualDisplayClient client, AndroidDevice device, AndroidApp app)
    {
        internal readonly IVirtualDisplayClient Client = client;
        internal readonly AndroidDevice Device = device;
        internal readonly AndroidApp App = app;
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly TaskCompletionSource<IVirtualDisplayClient> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<IVirtualDisplayClient> _factory;
    private bool _disposed;
    public int Count { get { lock (_gate) return _entries.Count; } }
    public event Action<string>? Failed;
    public AndroidVirtualDisplayManager(Func<IVirtualDisplayClient> factory) => _factory = factory;

    public Task<IVirtualDisplayClient> OpenAsync(AndroidDevice device, AndroidApp app, int x, int y)
    {
        Entry entry; string id = AndroidDeviceIdentity.Key(device) + ":" + app.Package;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(id, out var existing)) return existing.Ready.Task;
            entry = new Entry(_factory(), device, app); _entries.Add(id, entry);
        }
        _ = RunAsync(id, entry, device, app, x, y);
        return entry.Ready.Task;
    }
    public (AndroidDevice Device, AndroidApp App)? FindApplication(int processId)
    {
        if (processId <= 0) return null;
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
                if (entry.Client.ProcessId == processId) return (entry.Device, entry.App);
        }
        return null;
    }
    public Task CloseDeviceAsync(string serial) => CloseDevicesAsync(device => device.Serial == serial);
    public Task CloseDeviceAsync(AndroidDevice device) => CloseDevicesAsync(candidate => AndroidDeviceIdentity.Matches(candidate, device));
    private Task CloseDevicesAsync(Func<AndroidDevice, bool> matches) => CloseEntriesAsync(entry => matches(entry.Device));
    public Task CloseApplicationAsync(AndroidDevice device, string package) => CloseEntriesAsync(entry =>
        AndroidDeviceIdentity.Matches(entry.Device, device) && entry.App.Package == package);
    private async Task CloseEntriesAsync(Func<Entry, bool> matches)
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = _entries.Values.Where(matches).ToArray();
            foreach (var entry in entries) entry.Lifetime.Cancel();
        }
        await Task.WhenAll(entries.Select(e => e.Closed.Task)).ConfigureAwait(false);
    }
    private async Task RunAsync(string id, Entry entry, AndroidDevice device, AndroidApp app, int x, int y)
    {
        try
        {
            await entry.Client.StartAsync(device, app, x, y, entry.Lifetime.Token).ConfigureAwait(false);
            entry.Ready.TrySetResult(entry.Client);
            await entry.Client.Completion.WaitAsync(entry.Lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { entry.Ready.TrySetCanceled(); }
        catch (Exception ex)
        {
            // A startup error is delivered to the opener; subsequent failures use the event.
            if (!entry.Ready.TrySetException(ex))
                try { Failed?.Invoke(app.Name + "：" + ex.Message); } catch { }
        }
        finally
        {
            try { await entry.Client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { try { Failed?.Invoke(ex.Message); } catch { } }
            lock (_gate) _entries.Remove(id);
            entry.Lifetime.Dispose(); entry.Closed.TrySetResult();
        }
    }
    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            _disposed = true; entries = _entries.Values.ToArray();
            foreach (var entry in entries) entry.Lifetime.Cancel();
        }
        await Task.WhenAll(entries.Select(e => e.Closed.Task)).ConfigureAwait(false);
    }
}

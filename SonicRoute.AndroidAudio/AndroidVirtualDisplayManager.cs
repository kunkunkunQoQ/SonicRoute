namespace SonicRoute.AndroidAudio;

public enum VirtualDisplayState { Starting, Active, OnPhone, Recovering, Missing, Unknown }

/// <summary>Video/control only. Audio stays in AndroidAudioManager's in-process sources.</summary>
public interface IVirtualDisplayClient : IAsyncDisposable
{
    Task StartAsync(AndroidDevice device, AndroidApp app, int x, int y, CancellationToken token);
    Task Completion { get; }
    int ProcessId => 0;
    nint WindowHandle => 0;
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
    private readonly HashSet<Entry> _retiring = new();
    private readonly Func<IVirtualDisplayClient> _factory;
    private readonly TimeSpan _closeTimeout;
    private bool _disposed;

    public int Count { get { lock (_gate) return _entries.Count + _retiring.Count; } }
    public event Action<string>? Failed;
    public event Action<AndroidApp?, Exception>? FailedError;
    public event Action? ResourcesRetired;
    public AndroidVirtualDisplayManager(Func<IVirtualDisplayClient> factory) : this(factory, TimeSpan.FromSeconds(8)) { }
    internal AndroidVirtualDisplayManager(Func<IVirtualDisplayClient> factory, TimeSpan closeTimeout)
    {
        if (closeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(closeTimeout));
        _factory = factory; _closeTimeout = closeTimeout;
    }

    public Task<IVirtualDisplayClient> OpenAsync(AndroidDevice device, AndroidApp app, int x, int y)
    {
        if (device.IsApp) throw new SrapException("unsupported");
        Entry entry;
        string id = AndroidDeviceIdentity.Key(device) + ":" + app.Package;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(id, out var existing)) return existing.Ready.Task;
            entry = new Entry(_factory(), device, app);
            _entries.Add(id, entry);
        }
        _ = RunAsync(id, entry, device, app, x, y);
        return entry.Ready.Task;
    }

    public (AndroidDevice Device, AndroidApp App)? FindApplication(int processId)
        => FindApplication(0, processId);

    public (AndroidDevice Device, AndroidApp App)? FindApplication(nint windowHandle, int processId)
    {
        lock (_gate)
        {
            if (windowHandle != 0)
            {
                foreach (var entry in _entries.Values)
                    if (entry.Client.WindowHandle == windowHandle) return (entry.Device, entry.App);
            }
            if (processId > 0 && (windowHandle == 0 || processId != Environment.ProcessId))
            {
                foreach (var entry in _entries.Values)
                    if (entry.Client.ProcessId == processId && (processId != Environment.ProcessId || entry.Client.WindowHandle == 0))
                        return (entry.Device, entry.App);
            }
        }
        return null;
    }

    public (AndroidDevice Device, AndroidApp App)? FindByPackage(string package)
    {
        if (string.IsNullOrEmpty(package)) return null;
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
                if (string.Equals(entry.App.Package, package, StringComparison.OrdinalIgnoreCase))
                    return (entry.Device, entry.App);
        }
        return null;
    }

    public bool OwnsWindow(nint windowHandle)
    {
        if (windowHandle == 0) return false;
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
                if (entry.Client.WindowHandle == windowHandle) return true;
        }
        return false;
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
            entries = _entries.Values.Concat(_retiring).Where(matches).Distinct().ToArray();
            foreach (var pair in _entries.Where(pair => matches(pair.Value)).ToArray())
            {
                _entries.Remove(pair.Key);
                _retiring.Add(pair.Value);
            }
        }
        await Task.WhenAll(CancelEntries(entries), Task.WhenAll(entries.Select(e => e.Closed.Task)))
            .WaitAsync(_closeTimeout).ConfigureAwait(false);
    }

    private static Task CancelEntries(Entry[] entries)
    {
        foreach (var entry in entries) entry.Ready.TrySetCanceled();
        return Task.WhenAll(entries.Select(async entry =>
        {
            // Cancellation can invoke client callbacks; never do it while owning the registry lock.
            try { await entry.Lifetime.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (AggregateException ex) { System.Diagnostics.Trace.WriteLine("Display cancellation callback: " + ex.Message); }
        }));
    }

    private async Task RunAsync(string id, Entry entry, AndroidDevice device, AndroidApp app, int x, int y)
    {
        try
        {
            entry.Lifetime.Token.ThrowIfCancellationRequested();
            await entry.Client.StartAsync(device, app, x, y, entry.Lifetime.Token).ConfigureAwait(false);
            entry.Lifetime.Token.ThrowIfCancellationRequested();
            entry.Ready.TrySetResult(entry.Client);
            await entry.Client.Completion.WaitAsync(entry.Lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { entry.Ready.TrySetCanceled(); }
        catch (Exception ex)
        {
            if (!entry.Ready.TrySetException(ex))
                try { Failed?.Invoke(app.Name + "：" + ex.Message); FailedError?.Invoke(app, ex); } catch { }
        }
        finally
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(id, out var current) && ReferenceEquals(current, entry))
                    _entries.Remove(id);
                _retiring.Add(entry);
            }
            try { await entry.Client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { try { Failed?.Invoke(ex.Message); FailedError?.Invoke(null, ex); } catch { } }
            lock (_gate) _retiring.Remove(entry);
            entry.Lifetime.Dispose();
            entry.Closed.TrySetResult();
            try { ResourcesRetired?.Invoke(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Display retirement notification: " + ex.Message); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            _disposed = true;
            entries = _entries.Values.Concat(_retiring).Distinct().ToArray();
            _entries.Clear();
            foreach (var entry in entries) _retiring.Add(entry);
        }
        await Task.WhenAll(CancelEntries(entries), Task.WhenAll(entries.Select(e => e.Closed.Task)))
            .WaitAsync(_closeTimeout).ConfigureAwait(false);
    }
}

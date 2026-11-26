namespace SonicRoute.AndroidAudio;

/// <summary>Shares an on-demand scan without sharing caller cancellation or publishing an obsolete scan.</summary>
public sealed class AndroidDeviceDiscovery
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<AndroidDevice>>> _read;
    private IReadOnlyList<AndroidDevice>? _devices;
    private bool _current;
    private long _revision;
    private Scan? _scan;
    private sealed class Scan(long revision)
    {
        internal long Revision = revision;
        internal HashSet<string>? LostTargets;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<IReadOnlyList<AndroidDevice>> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Readers;
    }
    public AndroidDeviceDiscovery(Func<CancellationToken, Task<IReadOnlyList<AndroidDevice>>> read) => _read = read;
    public event Action? Changed;
    public IReadOnlyList<AndroidDevice>? Devices { get { lock (_gate) return _devices; } }
    // Retain the last list for presentation, but don't start new work on an explicitly
    // invalidated transport until a newer discovery or connection result arrives.
    internal IReadOnlyList<AndroidDevice>? CurrentDevices { get { lock (_gate) return _current ? _devices : null; } }
    internal long Revision { get { lock (_gate) return _revision; } }
    internal long RevisionOf(IReadOnlyList<AndroidDevice> snapshot)
    { lock (_gate) return _current && ReferenceEquals(_devices, snapshot) ? _revision : -1; }

    // A handshake can finish after another scan or a removal. Use the newer
    // observation only if it independently contains exactly this live instance.
    internal bool PublishConnection(AndroidConnectionResult result, long revision, Func<AndroidConnectionResult, bool> accept)
        => PublishConnection(result, revision, accept, out _);

    private bool PublishConnection(AndroidConnectionResult result, long revision, Func<AndroidConnectionResult, bool> accept,
        out bool superseded)
    {
        bool published = false;
        superseded = false;
        lock (_gate)
        {
            if (revision != _revision)
            {
                if (!_current || _devices == null || !_devices.Contains(result.Device))
                {
                    superseded = true;
                    return false;
                }
                result = result with { Devices = _devices };
            }
            else if (!_current || !ReferenceEquals(_devices, result.Devices))
            {
                _devices = result.Devices; _current = true; ++_revision; published = true;
            }
        }
        bool accepted = false;
        try { return accepted = accept(result); }
        finally
        {
            // Admission may change even when discovery returned the same list.
            // Dispatch after the durable store transaction and outside the lock.
            if (published || accepted) Changed?.Invoke();
        }
    }

    // Connecting can itself trigger track-devices before the handshake returns. Recheck
    // that specific live attachment once without repeating the wireless connection.
    internal async Task<bool> PublishReconnectAsync(AndroidConnectionResult result, long revision,
        Func<AndroidConnectionResult, bool> accept, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (PublishConnection(result, revision, accept, out bool superseded)) return true;
        if (!superseded) return false;

        var devices = await ReadAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var confirmed = devices.FirstOrDefault(d => d.State == "device" && (d == result.Device
            || AndroidDeviceIdentity.SameAttachment(result.Device, d)
                && AndroidDeviceIdentity.SamePhysicalDevice(result.Device, d)
                && (result.Device.IdentityFingerprint == null || result.Device.IdentityFingerprint == d.IdentityFingerprint)));
        return confirmed != null && PublishConnection(result with { Device = confirmed, Devices = devices }, RevisionOf(devices), accept);
    }
    public void Publish(IReadOnlyList<AndroidDevice> devices)
    {
        lock (_gate)
        {
            if (_current && ReferenceEquals(_devices, devices)) return;
            _devices = devices; _current = true; ++_revision;
        }
        Changed?.Invoke();
    }
    public void Invalidate() { lock (_gate) { _current = false; ++_revision; } Changed?.Invoke(); }
    // Lifecycle changes supersede in-flight scans without discarding verified transports
    // of other phones. The management filter immediately hides the removed profile.
    internal void RetirePendingScan() { lock (_gate) ++_revision; }
    internal void RemoveTransport(string target)
    {
        bool changed;
        lock (_gate)
        {
            // A pre-loss scan may still contain this generation, along with valid
            // observations of other phones. Retain those instead of invalidating all.
            if (_scan is { } scan && scan.Revision == _revision && !scan.Completion.Task.IsCompleted)
            {
                (scan.LostTargets ??= new(StringComparer.Ordinal)).Add(target);
                scan.Revision = _revision + 1;
            }
            ++_revision;
            changed = _devices?.Any(d => d.ConnectionTarget == target) == true;
            if (changed) _devices = _devices!.Where(d => d.ConnectionTarget != target).ToArray();
        }
        if (changed) Changed?.Invoke();
    }
    internal void RemoveAbsentUsbDevices(IReadOnlyList<AndroidDevice>? observed, IReadOnlySet<string>? present,
        IReadOnlyCollection<AndroidDevice> removed)
    {
        lock (_gate)
        {
            if (_devices == null || observed == null) return;
            bool StillAttached(AndroidDevice device)
            {
                if (device.IsWifi || device.UsbInterfacePath == null && device.Serial.StartsWith("emulator-", StringComparison.Ordinal)
                    && ushort.TryParse(device.Serial.AsSpan("emulator-".Length), out _)) return true;
                // A concurrent loss on another phone advances the revision. Apply this
                // probe only to the original attachment, never to a replug at the same path.
                if (!ReferenceEquals(observed, _devices) && !observed.Any(old => AndroidDeviceIdentity.SameAttachment(old, device))) return true;
                if (present?.Count == 0) return false;
                if (device.UsbInterfacePath == null) return true; // A shared daemon need not expose the Windows path.
                // A current path list also handles removals while all panels were closed.
                // If Windows enumeration itself failed, trust only a removal for this exact attachment.
                return present?.Contains(device.UsbInterfacePath) ?? !removed.Any(old => AndroidDeviceIdentity.SameAttachment(old, device));
            }
            var remaining = _devices.Where(StillAttached).ToArray();
            if (remaining.Length == _devices.Count) return;
            _devices = remaining;
            ++_revision;
        }
        Changed?.Invoke();
    }
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
                if (changed)
                {
                    _devices = scan.LostTargets is { } lost
                        ? devices.Where(d => d.ConnectionTarget == null || !lost.Contains(d.ConnectionTarget)).ToArray()
                        : devices;
                    _current = true;
                    ++_revision;
                }
                devices = _current ? _devices ?? [] : [];
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

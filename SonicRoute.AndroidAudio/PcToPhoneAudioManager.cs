using System.ComponentModel;
using System.Diagnostics;

namespace SonicRoute.AndroidAudio;

public interface IPcProcessSilenceLease : IDisposable
{
    void Release(bool restorePreviousEndpoint);
    CancellationToken Invalidated => CancellationToken.None;
}

/// <summary>Streams per-process Windows 11 loopback audio to an Android phone's speaker or Bluetooth output sink.</summary>
public sealed class PcToPhoneAudioManager(Func<AdbClient> adbFactory, Func<int, IPcProcessSilenceLease?>? silenceLocalProcess = null) : IAsyncDisposable
{
    public PcToPhoneAudioManager(AdbClient adb, Func<int, IPcProcessSilenceLease?>? silenceLocalProcess = null)
        : this(() => adb, silenceLocalProcess) { }

    private readonly object _gate = new();
    private readonly Dictionary<int, ActiveRoute> _routes = new();
    private readonly Dictionary<Task, ActiveRoute> _retiring = new();
    private bool _disposed;
    public bool EnableLiveStreaming { get; set; } = true;
    internal Func<AndroidDevice, string, CancellationToken, Action<string, int, string>, Task<IPhonePcmConnection>>? OpenAppPlayback { get; set; }
    public event Action? Changed;
    public event Action<int>? RouteChanged;
    private void NotifyRoute(int pid)
    {
        RouteChanged?.Invoke(pid);
        Changed?.Invoke();
    }
    public event Action? ResourcesRetired;
    public event Action<AndroidDevice, IReadOnlyList<AndroidOutputSink>>? OutputsObserved;
    public event Action<int, string, int, string>? RoutedDeviceObserved;
    public event Action<AndroidDevice>? DeviceDisconnected;

    public int ActiveCount { get { lock (_gate) return _routes.Count; } }
    public bool HasActiveWork { get { lock (_gate) return _routes.Count > 0 || _retiring.Count > 0; } }

    public (long BytesStreamed, long NonSilentBytesStreamed, string? RoutedKey, int RoutedType, string? RoutedName)? GetDiagnostics(int processId)
    {
        lock (_gate)
        {
            if (!_routes.TryGetValue(processId, out var route)) return null;
            return (Interlocked.Read(ref route.BytesStreamed), Interlocked.Read(ref route.NonSilentBytesStreamed),
                route.RoutedKey, route.RoutedType, route.RoutedName);
        }
    }

    public string? GetPersistedEndpoint(int processId) => GetPersistedEndpoint(processId, out _);

    internal string? GetPersistedEndpoint(int processId, out bool exclusive)
    {
        lock (_gate)
        {
            _routes.TryGetValue(processId, out var route);
            exclusive = route?.SilenceLocal == true;
            return route?.TargetDeviceId;
        }
    }

    public IReadOnlyList<(int ProcessId, string TargetDeviceId, string PhoneKey, string SinkKey)> GetActiveRoutes()
    {
        lock (_gate)
            return _routes.Values.Select(r => (r.ProcessId, r.TargetDeviceId, r.PhoneKey, r.SinkKey)).ToArray();
    }

    public (bool Success, int HResult, string Message) RouteProcess(int processId, string targetDeviceId, AndroidDevice? device)
        => RouteProcess(processId, targetDeviceId, device, silenceLocal: true);

    internal (bool Success, int HResult, string Message) RouteProcess(int processId, string targetDeviceId, AndroidDevice? device, bool silenceLocal)
    {
        if (processId <= 0) return (false, -1, "无效的应用进程 ID。");
        if (!AndroidOutputIdentity.TryParseDeviceId(targetDeviceId, out string phoneKey, out string sinkKey))
            return (false, -1, "无效的手机播放设备标识。");
        if (device?.IsApp == true && (OpenAppPlayback == null || device.State != "device"))
            return (false, -1, "手机 App 音频播放不可用。");

        ActiveRoute? previous = null;
        ActiveRoute route;
        bool samePhoneSwitch = false;

        lock (_gate)
        {
            if (_disposed) return (false, -1, "手机音频服务已停止。");
            if (_routes.TryGetValue(processId, out var existing) && !existing.IsStopped
                && string.Equals(existing.PhoneKey, phoneKey, StringComparison.OrdinalIgnoreCase)
                && (device?.IsApp != true && existing.Device?.IsApp != true || device != null && existing.Device != null
                    && AndroidDeviceIdentity.SameAttachment(device, existing.Device)))
            {
                existing.TargetDeviceId = targetDeviceId;
                if (device != null) existing.Device = device;
                existing.ChangeSilenceMode(silenceLocal, () => SafeAcquireSilenceLease(processId));
                route = existing;
                samePhoneSwitch = true;
            }
            else
            {
                IPcProcessSilenceLease? carriedLease = null;
                if (_routes.TryGetValue(processId, out previous))
                {
                    carriedLease = previous.DetachSilenceLease();
                    _routes.Remove(processId);
                }
                bool canSilence = device is { State: "device" }
                    && !string.Equals(device.Serial, "offline-fixture", StringComparison.Ordinal);
                if (!silenceLocal || device?.IsApp == true)
                { carriedLease?.Release(restorePreviousEndpoint: device?.IsApp == true); carriedLease = null; }
                var lease = carriedLease ?? (silenceLocal && canSilence && device?.IsApp != true ? SafeAcquireSilenceLease(processId) : null);
                route = new ActiveRoute(processId, targetDeviceId, phoneKey, sinkKey, device, lease, silenceLocal);
                _routes[processId] = route;
                if (previous != null) _ = Retire(previous, restorePreviousEndpoint: false);
            }
        }

        if (samePhoneSwitch)
        {
            route.RequestSinkSwitch(sinkKey, () => SafeAcquireSilenceLease(processId));
            if (!route.IsStreamingStarted && EnableLiveStreaming
                && !string.Equals(route.Device?.Serial, "offline-fixture", StringComparison.Ordinal))
            {
                route.StartStreaming(this);
            }
            NotifyRoute(processId);
            return (true, 0, "成功");
        }

        if (EnableLiveStreaming
            && !string.Equals(device?.Serial, "offline-fixture", StringComparison.Ordinal))
        {
            route.StartStreaming(this);
        }

        NotifyRoute(processId);
        return (true, 0, "成功");
    }

    public bool StopProcess(int processId, bool restorePreviousEndpoint = true)
    {
        ActiveRoute? removed;
        lock (_gate)
        {
            if (!_routes.TryGetValue(processId, out removed)) return false;
            _routes.Remove(processId);
            _ = Retire(removed, restorePreviousEndpoint);
        }
        NotifyRoute(processId);
        return true;
    }

    public async Task<bool> StopProcessAsync(int processId, bool restorePreviousEndpoint = true)
    {
        ActiveRoute? removed;
        Task retiring;
        lock (_gate)
        {
            if (!_routes.TryGetValue(processId, out removed)) return false;
            _routes.Remove(processId);
            retiring = Retire(removed, restorePreviousEndpoint);
        }
        await retiring.ConfigureAwait(false);
        NotifyRoute(processId);
        return true;
    }

    public async Task StopPhoneAsync(AndroidDevice device, bool restorePreviousEndpoint = true)
    {
        string phoneKey = AndroidDeviceIdentity.Key(device);
        ActiveRoute[] matching;
        Task[] retiring;
        lock (_gate)
        {
            bool Matches(ActiveRoute r) => r.Device != null ? AndroidDeviceIdentity.Matches(r.Device, device)
                : string.Equals(r.PhoneKey, phoneKey, StringComparison.OrdinalIgnoreCase);
            matching = _routes.Values.Where(Matches).ToArray();
            var previous = _retiring.Where(pair => Matches(pair.Value)).Select(pair => pair.Key).ToArray();
            foreach (var r in matching) _routes.Remove(r.ProcessId);
            retiring = previous.Concat(matching.Select(r => Retire(r, restorePreviousEndpoint))).ToArray();
        }
        await Task.WhenAll(retiring).ConfigureAwait(false);
        if (matching.Length > 0) NotifyRoute(matching.Length == 1 ? matching[0].ProcessId : 0);
    }

    public int StopAll(bool restorePreviousEndpoint = true)
    {
        ActiveRoute[] all;
        lock (_gate)
        {
            all = _routes.Values.ToArray();
            _routes.Clear();
            foreach (var route in all) _ = Retire(route, restorePreviousEndpoint);
        }
        if (all.Length > 0) NotifyRoute(all.Length == 1 ? all[0].ProcessId : 0);
        return all.Length;
    }

    public async ValueTask DisposeAsync()
    {
        ActiveRoute[] all;
        Task[] retiring;
        lock (_gate)
        {
            _disposed = true;
            all = _routes.Values.ToArray();
            // Register before detaching: another stop cannot escape the shutdown join.
            foreach (var route in all) Retire(route, restorePreviousEndpoint: true);
            _routes.Clear();
            retiring = _retiring.Keys.ToArray();
        }
        await Task.WhenAll(retiring).ConfigureAwait(false);
    }

    private Task Retire(ActiveRoute route, bool restorePreviousEndpoint)
    {
        Task task;
        lock (_gate)
        {
            task = route.StopAsync(restorePreviousEndpoint);
            _retiring[task] = route;
        }
        _ = task.ContinueWith(done =>
        {
            lock (_gate) _retiring.Remove(done);
            try { ResourcesRetired?.Invoke(); }
            catch (Exception ex) { Trace.WriteLine("PC audio retirement notification: " + ex.Message); }
        },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return task;
    }

    private IPcProcessSilenceLease? SafeAcquireSilenceLease(int processId)
    {
        try { return silenceLocalProcess?.Invoke(processId); }
        catch (Exception ex) { Trace.WriteLine("PcToPhone silence lease: " + ex.Message); return null; }
    }

    private bool StopOwnedRoute(ActiveRoute route)
    {
        lock (_gate)
        {
            if (!_routes.TryGetValue(route.ProcessId, out var current) || !ReferenceEquals(current, route)) return false;
            _routes.Remove(route.ProcessId);
            _ = Retire(route, restorePreviousEndpoint: true);
        }
        NotifyRoute(route.ProcessId);
        return true;
    }

    private async Task RunStreamAsync(ActiveRoute route, AndroidDevice? device, bool watchingExit, CancellationToken token)
    {
        bool unexpectedExit = false;
        try
        {
            if (device is not { State: "device" })
            {
                if (device is { ConnectionTarget: not null } || device?.IsApp == true || route.PhoneKey.StartsWith("app:", StringComparison.Ordinal))
                    throw AndroidError.WithCode(new IOException("手机连接已改变，请刷新设备。"), AndroidErrorCode.PhoneOffline);
                device = (await adbFactory().DevicesAsync(token).ConfigureAwait(false)).FirstOrDefault(d => d.State == "device"
                    && string.Equals(AndroidDeviceIdentity.Key(d), route.PhoneKey, StringComparison.OrdinalIgnoreCase))
                    ?? throw AndroidError.WithCode(new IOException("手机未连接或离线。"), AndroidErrorCode.PhoneOffline);
            }
            void Routed(string key, int type, string name)
            {
                route.RoutedKey = key; route.RoutedType = type; route.RoutedName = name;
                RoutedDeviceObserved?.Invoke(route.ProcessId, key, type, name);
            }
            await using IPhonePcmConnection output = device.IsApp
                ? await (OpenAppPlayback ?? throw new SrapException("unsupported"))(device, route.SinkKey, token, Routed).ConfigureAwait(false)
                : await AndroidAudioOutputConnection.OpenAsync(adbFactory(), device, route.SinkKey, token,
                    (phone, sinks) => OutputsObserved?.Invoke(phone, sinks), Routed).ConfigureAwait(false);
            route.Device = device = output.Device;
            using var capture = CancellationTokenSource.CreateLinkedTokenSource(token);
            void Failed(Exception _) { try { capture.Cancel(); } catch (ObjectDisposedException) { } }
            output.Failed += Failed;
            try
            {
                await route.AttachOutputAsync(output).ConfigureAwait(false);
                if (output.Completion.IsCompleted) throw await output.Completion.ConfigureAwait(false);
                route.EnsureSilenceLease(() => SafeAcquireSilenceLease(route.ProcessId));
                await PcProcessLoopbackCapture.RunMtaCaptureLoopAsync(route.ProcessId, output.Stream, capture.Token,
                    watchingExit ? null : () => StopOwnedRoute(route), (bytes, nonSilent) =>
                    {
                        Interlocked.Add(ref route.BytesStreamed, bytes);
                        if (nonSilent > 0) Interlocked.Add(ref route.NonSilentBytesStreamed, nonSilent);
                    }).ConfigureAwait(false);
                if (output.Completion.IsCompleted) throw await output.Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && output.Completion.IsCompleted)
            { throw await output.Completion.ConfigureAwait(false); }
            finally { output.Failed -= Failed; route.DetachOutput(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            unexpectedExit = !token.IsCancellationRequested;
            Trace.WriteLine($"PcToPhone stream ({route.ProcessId}): {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested && !route.IsStopped)
            {
                bool removed = StopOwnedRoute(route);
                if (removed && unexpectedExit && device != null)
                    try { DeviceDisconnected?.Invoke(device); } catch { }
            }
        }
    }

    private sealed class ActiveRoute(
        int processId,
        string targetDeviceId,
        string phoneKey,
        string sinkKey,
        AndroidDevice? device,
        IPcProcessSilenceLease? silenceLease,
        bool silenceLocal)
    {
        private readonly object _routeLock = new();
        private readonly CancellationTokenSource _cts = new();
        private IPcProcessSilenceLease? _silenceLease = silenceLease;
        private IPhonePcmConnection? _output;
        private string? _confirmedAppSink;
        private Task _streamTask = Task.CompletedTask;
        private Task? _stopTask;
        public int ProcessId { get; } = processId;
        public string TargetDeviceId { get; set; } = targetDeviceId;
        public string PhoneKey { get; } = phoneKey;
        public string SinkKey { get; set; } = sinkKey;
        public AndroidDevice? Device { get; set; } = device;
        public bool SilenceLocal { get; private set; } = silenceLocal;
        public bool IsStopped { get; private set; }
        public bool IsStreamingStarted { get; private set; }
        public long BytesStreamed;
        public long NonSilentBytesStreamed;
        public string? RoutedKey;
        public int RoutedType;
        public string? RoutedName;

        public void StartStreaming(PcToPhoneAudioManager owner)
        {
            lock (_routeLock)
            {
                if (IsStopped || IsStreamingStarted) return;
                IsStreamingStarted = true;
                // Pin the selected process before asynchronous phone setup. An already-signalled
                // process handle also delivers Exited, including when it exits during registration.
                Process? source = null;
                bool alreadyExited = false;
                try
                {
                    source = Process.GetProcessById(ProcessId);
                    source.Exited += (_, _) =>
                    {
                        try { owner.StopOwnedRoute(this); }
                        catch (Exception ex) { Trace.WriteLine("PC audio process exit notification: " + ex.Message); }
                    };
                    source.EnableRaisingEvents = true;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    // Some sources deny a synchronizable handle. Keep the capture loop's
                    // bounded liveness check for those, while a missing PID can retire immediately.
                    alreadyExited = ex is ArgumentException;
                    source?.Dispose();
                    source = null;
                }
                _streamTask = Task.Run(async () =>
                {
                    using (source)
                    {
                        if (alreadyExited) { owner.StopOwnedRoute(this); return; }
                        await owner.RunStreamAsync(this, Device, source != null, _cts.Token).ConfigureAwait(false);
                    }
                });
            }
        }

        public async Task AttachOutputAsync(IPhonePcmConnection output)
        {
            string sink;
            lock (_routeLock)
            {
                _output = output;
                sink = SinkKey;
            }
            await output.SwitchTargetAsync(sink, _cts.Token).ConfigureAwait(false);
            lock (_routeLock) { if (Device?.IsApp == true && SinkKey == sink) _confirmedAppSink = sink; }
        }

        public void DetachOutput() { lock (_routeLock) _output = null; }

        public void RequestSinkSwitch(string sinkKey, Func<IPcProcessSilenceLease?> acquire)
        {
            lock (_routeLock)
            {
                if (IsStopped) return;
                if (Device?.IsApp == true && SinkKey != sinkKey)
                {
                    _confirmedAppSink = null;
                    var lease = _silenceLease;
                    _silenceLease = null;
                    lease?.Release(restorePreviousEndpoint: true);
                }
                SinkKey = sinkKey;
                if (_output != null) _ = SwitchOutputAsync(_output, sinkKey, acquire);
            }
        }

        private async Task SwitchOutputAsync(IPhonePcmConnection output, string sinkKey, Func<IPcProcessSilenceLease?> acquire)
        {
            try
            {
                await output.SwitchTargetAsync(sinkKey, _cts.Token).ConfigureAwait(false);
                lock (_routeLock)
                {
                    if (IsStopped || Device?.IsApp != true || SinkKey != sinkKey || _output != output) return;
                    _confirmedAppSink = sinkKey;
                    EnsureSilenceLease(acquire);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Trace.WriteLine("Phone output switch: " + ex.Message);
                // Fail this route rather than play through an unrequested default device.
                output.Abort(ex);
            }
        }

        // The Windows endpoint has already been applied before an exclusive route becomes mixed.
        public void ChangeSilenceMode(bool exclusive, Func<IPcProcessSilenceLease?> factory)
        {
            lock (_routeLock)
            {
                if (SilenceLocal == exclusive || IsStopped) return;
                SilenceLocal = exclusive;
                if (exclusive)
                {
                    if (Device is { State: "device" } && Device.Serial != "offline-fixture" && (!Device.IsApp || _confirmedAppSink == SinkKey))
                        _silenceLease ??= factory();
                }
                else
                {
                    var previous = _silenceLease;
                    _silenceLease = null;
                    previous?.Release(restorePreviousEndpoint: false);
                }
            }
        }

        public void EnsureSilenceLease(Func<IPcProcessSilenceLease?> factory)
        {
            lock (_routeLock)
            {
                if (IsStopped || !SilenceLocal || _silenceLease != null || Device?.IsApp == true && _confirmedAppSink != SinkKey) return;
                _silenceLease = factory();
            }
        }

        public IPcProcessSilenceLease? DetachSilenceLease()
        {
            lock (_routeLock)
            {
                var lease = _silenceLease;
                _silenceLease = null;
                return lease;
            }
        }

        public Task StopAsync(bool restorePreviousEndpoint)
        {
            IPcProcessSilenceLease? lease;
            Task stream;
            TaskCompletionSource stopped;
            lock (_routeLock)
            {
                if (_stopTask != null) return _stopTask;
                stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopTask = stopped.Task;
                IsStopped = true;
                lease = _silenceLease;
                _silenceLease = null;
                stream = _streamTask;
            }
            _ = CompleteStopAsync();
            return stopped.Task;

            async Task CompleteStopAsync()
            {
                try
                {
                    try { _cts.Cancel(); } catch { }
                    try { lease?.Release(restorePreviousEndpoint); } catch { }
                    try { await stream.ConfigureAwait(false); } catch { }
                }
                finally { _cts.Dispose(); stopped.TrySetResult(); }
            }
        }
    }
}

namespace SonicRoute.AndroidAudio;

/// <summary>One independent UID transport, decoder, PCM queue and renderer per application.</summary>
public sealed class AndroidAudioSource
{
    public AndroidDevice Device { get; }
    public AndroidApp App { get; }
    public string Id { get; }
    public AndroidAudioSession Session { get; }
    public bool KeepPhoneAudio { get; }
    internal readonly CancellationTokenSource Lifetime = new();
    internal readonly TaskCompletionSource<AndroidAudioSource> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int RetirementStarted;
    internal bool DisplayOwned;
    internal Action<AndroidAudioState>? Handler;
    internal AndroidAudioSource(AndroidDevice device, AndroidApp app, AndroidAudioSession session, bool keepPhoneAudio)
    {
        Device = device; App = app; Session = session; KeepPhoneAudio = keepPhoneAudio;
        Id = $"android:{device.Serial}:{app.Package}:{app.Uid}";
    }
}

/// <summary>Registry lifetime is independent of the quick panel; no timers or discovery loops.</summary>
public sealed class AndroidAudioManager : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<AndroidAudioSource> _sources = new();
    // Publish order and identity lookup together; readers never rebuild or lock the registry.
    private sealed class SourceSnapshot
    {
        internal static readonly SourceSnapshot Empty = new(Array.Empty<AndroidAudioSource>());
        internal readonly IReadOnlyList<AndroidAudioSource> Sources;
        internal readonly Dictionary<string, AndroidAudioSource> ById;
        internal SourceSnapshot(AndroidAudioSource[] sources)
        {
            Sources = Array.AsReadOnly(sources);
            ById = sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        }
    }
    private SourceSnapshot _snapshot = SourceSnapshot.Empty;
    private readonly HashSet<AndroidAudioSource> _retiring = new();
    private readonly Queue<string> _history = new();
    private readonly Func<AndroidAudioSession> _factory;
    private readonly AndroidAudioSettingsStore? _settings;
    private readonly Func<AndroidDevice, string, bool>? _smartVolume;
    private bool _disposed;
    public event Action? Changed;
    public IReadOnlyList<AndroidAudioSource> Sources => Volatile.Read(ref _snapshot).Sources;
    public AndroidAudioSource? FindSource(string id) =>
        Volatile.Read(ref _snapshot).ById.TryGetValue(id, out var source) ? source : null;
    private void PublishSources() => Volatile.Write(ref _snapshot, _sources.Count == 0
        ? SourceSnapshot.Empty : new SourceSnapshot(_sources.ToArray()));
    public AndroidAudioManager(AdbClient adb, AndroidAudioSettingsStore? settings = null, Func<AndroidDevice, string, bool>? smartVolume = null)
        : this(() => new AndroidAudioSession(adb, settings == null ? null : settings.GetStreaming), settings, smartVolume) { }
    public AndroidAudioManager(Func<AndroidAudioSession> factory, AndroidAudioSettingsStore? settings = null, Func<AndroidDevice, string, bool>? smartVolume = null)
    { _factory = factory; _settings = settings; _smartVolume = smartVolume; }

    public Task<AndroidAudioSource> AddAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio = true, string? outputId = null, bool displayOwned = false)
    {
        AndroidAudioSource source;
        AndroidAudioSource? replaced;
        AndroidAudioSource[] releaseBeforeStart;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A display may use an existing device capture, but must not replace an
            // explicit whole-phone choice with a second capture of the same audio.
            if (displayOwned && _sources.FirstOrDefault(s => AndroidDeviceIdentity.Matches(s.Device, device)
                && s.App.IsDeviceAudio && s.Session.State.State != CaptureState.Failed) is { } wholePhone)
                return wholePhone.Started.Task;
            replaced = _sources.FirstOrDefault(s => AndroidDeviceIdentity.Matches(s.Device, device) && s.App.Package == app.Package);
            if (replaced != null && !displayOwned) replaced.DisplayOwned = false; // an explicit playback choice always wins
            if (displayOwned && replaced != null && replaced.App.Uid == app.Uid && replaced.Session.State.State != CaptureState.Failed)
                return replaced.Started.Task; // opening a picture never changes an existing playback-bar source
            if (replaced != null && replaced.App.Uid == app.Uid && replaced.KeepPhoneAudio == keepPhoneAudio
                && replaced.Session.State.State != CaptureState.Failed)
                return replaced.Started.Task; // a repeated drop shares the exact in-flight start or live source
            source = new AndroidAudioSource(device, app, _factory(), keepPhoneAudio) { DisplayOwned = displayOwned };
            var saved = _settings?.Get(device, app.Package) ?? new AndroidAppAudioSettings();
            source.Session.SetVolume(saved.Volume);
            source.Session.SetSmartVolume(_smartVolume?.Invoke(device, app.Package) ?? true);
            outputId ??= saved.OutputId;
            bool Conflicts(AndroidAudioSource other) => AndroidDeviceIdentity.Matches(other.Device, device)
                && (other.App.Package == app.Package || other.App.IsDeviceAudio || app.IsDeviceAudio);
            var conflicts = _sources.Where(Conflicts).ToArray();
            // Include already-retiring captures: a rapid device -> app -> second app
            // switch must finish releasing the device policy before either app starts.
            releaseBeforeStart = _retiring.Where(Conflicts).Concat(conflicts).Distinct().ToArray();
            int index = conflicts.Length == 0 ? _sources.Count : _sources.IndexOf(conflicts[0]);
            if (replaced != null && replaced.App.Uid == app.Uid)
            {
                var previous = replaced.Session.State;
                source.Session.SetVolume(previous.Volume);
                source.Session.SetMute(previous.Muted);
                if (_smartVolume == null) source.Session.SetSmartVolume(previous.SmartVolume);
                outputId = previous.OutputId;
            }
            foreach (var conflict in conflicts) Detach(conflict);
            source.Handler = _ =>
            {
                lock (_gate)
                {
                    if (!_sources.Contains(source)) return;
                    // Another update may have completed before this subscriber runs.
                    var current = source.Session.State;
                    _settings?.Remember(device, app.Package, current.Volume, current.OutputId);
                }
                Notify();
            };
            source.Session.Changed += source.Handler;
            _sources.Insert(Math.Min(index, _sources.Count), source);
            PublishSources();
        }
        Notify();
        _ = StartAsync(source, releaseBeforeStart, keepPhoneAudio, outputId);
        return source.Started.Task;
    }

    public void SyncSmartVolume(Func<AndroidDevice, string, bool> resolve)
    {
        AndroidAudioSource[] snapshot;
        lock (_gate) snapshot = _sources.ToArray();
        foreach (var source in snapshot)
            source.Session.SetSmartVolume(resolve(source.Device, source.App.Package));
    }

    private async Task StartAsync(AndroidAudioSource source, AndroidAudioSource[] releaseBeforeStart, bool keepPhoneAudio, string? outputId)
    {
        try
        {
            await Task.WhenAll(releaseBeforeStart.Select(RetireAsync)).ConfigureAwait(false);
            source.Lifetime.Token.ThrowIfCancellationRequested();
            await source.Session.SetOutputAsync(outputId).ConfigureAwait(false);
            await source.Session.StartAsync(source.Device, source.App, keepPhoneAudio, source.Lifetime.Token).ConfigureAwait(false);
            source.Started.TrySetResult(source);
        }
        catch (OperationCanceledException) { source.Started.TrySetCanceled(); }
        catch (Exception ex) { source.Started.TrySetException(ex); }
    }

    public async Task RemoveAsync(string id)
    {
        AndroidAudioSource? source;
        lock (_gate)
        {
            source = _sources.FirstOrDefault(s => s.Id == id);
            if (source == null) return;
            Detach(source);
            PublishSources();
        }
        var release = RetireAsync(source);
        Notify();
        await release.ConfigureAwait(false);
    }
    public async Task ReleaseDisplayAudioAsync(AndroidAudioSource source, string? ownerPackage = null)
    {
        lock (_gate)
        {
            if (!source.DisplayOwned || !_sources.Contains(source)
                || (ownerPackage != null && ownerPackage != source.App.Package)) return;
            Detach(source); PublishSources();
        }
        var release = RetireAsync(source); Notify(); await release.ConfigureAwait(false);
    }

    private void Detach(AndroidAudioSource source)
    {
        _sources.Remove(source);
        _retiring.Add(source);
        source.Session.Changed -= source.Handler;
        source.Lifetime.Cancel();
    }

    private Task RetireAsync(AndroidAudioSource source)
    {
        if (Interlocked.Exchange(ref source.RetirementStarted, 1) == 0) _ = CompleteRetirementAsync(source);
        return source.Released.Task;
    }
    private async Task CompleteRetirementAsync(AndroidAudioSource source)
    {
        try { await ReleaseAsync(source).ConfigureAwait(false); source.Released.TrySetResult(); }
        catch (Exception ex) { source.Released.TrySetException(ex); }
        finally { lock (_gate) _retiring.Remove(source); }
    }

    private async Task ReleaseAsync(AndroidAudioSource source)
    {
        try { await source.Session.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            // Observe starts that were cancelled while creating a transport before retiring their CTS.
            try { await source.Started.Task.ConfigureAwait(false); } catch { }
            string log = source.Session.GetLog();
            lock (_gate)
            {
                foreach (string line in (source.App.Name + Environment.NewLine + log).Split('\n'))
                {
                    while (_history.Count >= 300) _history.Dequeue();
                    _history.Enqueue(line.TrimEnd('\r'));
                }
            }
            source.Lifetime.Dispose();
        }
    }

    public async Task StopAllAsync()
    {
        AndroidAudioSource[] sources;
        lock (_gate)
        {
            sources = _sources.ToArray();
            foreach (var source in sources) Detach(source);
            PublishSources();
        }
        var releases = sources.Select(RetireAsync).ToArray();
        Notify();
        await Task.WhenAll(releases).ConfigureAwait(false);
    }

    public string GetLog()
    {
        string history; AndroidAudioSource[] sources;
        lock (_gate) { history = string.Join(Environment.NewLine, _history); sources = _sources.ToArray(); }
        return history + Environment.NewLine + string.Join(Environment.NewLine, sources.Select(s => s.App.Name + Environment.NewLine + s.Session.GetLog()));
    }
    private void Notify()
    {
        var listeners = Changed;
        if (listeners != null) foreach (Action listener in listeners.GetInvocationList())
            try { listener(); } catch { /* one closed UI subscriber must not stop another source */ }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await StopAllAsync().ConfigureAwait(false);
        Task[] retiring; lock (_gate) retiring = _retiring.Select(s => s.Released.Task).ToArray();
        await Task.WhenAll(retiring).ConfigureAwait(false);
    }
}

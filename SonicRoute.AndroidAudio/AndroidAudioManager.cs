namespace SonicRoute.AndroidAudio;

/// <summary>One independent UID transport, decoder, PCM queue and renderer per application.</summary>
public sealed class AndroidAudioSource
{
    public AndroidDevice Device { get; }
    public AndroidApp App { get; }
    public string Id { get; }
    public AndroidAudioSession Session { get; }
    public bool KeepPhoneAudio { get; }
    internal readonly CancellationTokenSource Lifetime;
    internal readonly TaskCompletionSource<AndroidAudioSource> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int RetirementStarted;
    internal object? DisplayOwner;
    internal string? InitialOutputId;
    internal string?[]? InitialOutputs;
    internal bool RememberRestoredSelection;
    internal bool Resuming;
    internal bool SuppressRetention;
    internal Action<AndroidAudioState>? Handler;
    internal AndroidAudioSource(AndroidDevice device, AndroidApp app, AndroidAudioSession session, bool keepPhoneAudio, CancellationToken lifetime = default)
    {
        Device = device; App = app; Session = session; KeepPhoneAudio = keepPhoneAudio;
        Id = $"android:{device.Serial}:{app.Package}:{app.Uid}";
        Lifetime = lifetime.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(lifetime) : new();
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
    private sealed record SavedCapture(string OriginalId, AndroidDevice Device, AndroidApp App,
        bool KeepPhoneAudio, object? DisplayOwner, AndroidAudioState State, string? OutputId, string?[]? Outputs, int Index);
    private sealed record PendingStart(AndroidAudioSource Source, AndroidAudioSource[] Predecessors);
    private readonly Dictionary<AndroidAudioSource, List<SavedCapture>> _resumeAfterDevice = new();
    private readonly Queue<string> _history = new();
    private readonly Func<AndroidAudioSession> _factory;
    private readonly AndroidAudioSettingsStore? _settings;
    private readonly Func<AndroidDevice, string, bool>? _smartVolume;
    private readonly Func<AndroidDevice, bool>? _mayCapture;
    private readonly Func<AndroidDevice, AppCapturePermission?>? _appCapture;
    private readonly CancellationToken _lifetime;
    private bool _disposed, _shuttingDown;
    internal void BeginShutdown() { lock (_gate) _shuttingDown = true; }
    public event Action? Changed;
    public event Action<string?>? SourceChanged;
    public event Action? ResourcesRetired;
    internal event Action<AndroidDevice>? CaptureInterrupted;
    public IReadOnlyList<AndroidAudioSource> Sources => Volatile.Read(ref _snapshot).Sources;
    public bool HasActiveWork { get { lock (_gate) return _sources.Count > 0 || _retiring.Count > 0; } }
    public AndroidAudioSource? FindSource(string id) =>
        Volatile.Read(ref _snapshot).ById.TryGetValue(id, out var source) ? source : null;
    private void PublishSources() => Volatile.Write(ref _snapshot, _sources.Count == 0
        ? SourceSnapshot.Empty : new SourceSnapshot(_sources.ToArray()));
    public AndroidAudioManager(AdbClient adb, AndroidAudioSettingsStore? settings = null, Func<AndroidDevice, string, bool>? smartVolume = null)
        : this(() => new AndroidAudioSession(adb, settings == null ? null : settings.GetStreaming), settings, smartVolume) { }
    public AndroidAudioManager(Func<AndroidAudioSession> factory, AndroidAudioSettingsStore? settings = null, Func<AndroidDevice, string, bool>? smartVolume = null)
        : this(factory, settings, smartVolume, null) { }

    internal AndroidAudioManager(Func<AndroidAudioSession> factory, AndroidAudioSettingsStore? settings,
        Func<AndroidDevice, string, bool>? smartVolume, Func<AndroidDevice, bool>? mayCapture, CancellationToken lifetime = default,
        Func<AndroidDevice, AppCapturePermission?>? appCapture = null)
    { _factory = factory; _settings = settings; _smartVolume = smartVolume; _mayCapture = mayCapture; _lifetime = lifetime; _appCapture = appCapture; }

    public Task<AndroidAudioSource> AddAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio = true, string? outputId = null, bool displayOwned = false) =>
        AddCore(device, app, keepPhoneAudio, outputId, displayOwned, null);

    internal Task<AndroidAudioSource> ResumeAsync(AndroidDevice device, AndroidApp app, string?[] outputs) =>
        AddCore(device, app, outputs.Contains(AndroidAudioSession.SourcePhoneOutputId), outputs[0], false, outputs);

    internal Task<AndroidAudioSource> PlayPhoneRequestAsync(AndroidDevice device, AndroidApp app, string?[] computerOutputs) =>
        AddCore(device, app, true, computerOutputs[0], false,
            AppCapturePermission.WithLocalPlayback(device, computerOutputs), phoneRequest: true);

    private Task<AndroidAudioSource> AddCore(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, string? outputId,
        bool displayOwned, string?[]? initialOutputs, bool phoneRequest = false)
    {
        if (_mayCapture?.Invoke(device) == false) throw new OperationCanceledException();
        if (device.IsApp) AppCapturePermission.Require(_appCapture?.Invoke(device), app, keepPhoneAudio);
        AndroidAudioSource source;
        var starts = new List<PendingStart>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lifetime.ThrowIfCancellationRequested();
            // A display may use an existing device capture, but must not replace an
            // explicit whole-phone choice with a second capture of the same audio.
            if (displayOwned && _sources.FirstOrDefault(s => AndroidDeviceIdentity.Matches(s.Device, device)
                && s.App.IsDeviceAudio && s.Session.State.State != CaptureState.Failed) is { } wholePhone)
                return wholePhone.Started.Task;
            var replaced = _sources.FirstOrDefault(s => AndroidDeviceIdentity.Matches(s.Device, device) && s.App.Package == app.Package);
            if (initialOutputs != null && !phoneRequest)
            {
                if (replaced != null) return replaced.Started.Task;
                var retained = _settings?.FindExternalAudio(ExternalAudioPreference.AndroidKey(device, app.Package));
                if ((retained != null ? !retained.Resume : _settings?.Get(device, app.Package).ResumeOutputs == null) || _sources.Any(s =>
                    AndroidDeviceIdentity.Matches(s.Device, device) && (app.IsDeviceAudio || s.App.IsDeviceAudio)))
                    throw new OperationCanceledException("The user changed this phone's playback selection.");
            }
            if (replaced != null && !displayOwned) replaced.DisplayOwner = null; // an explicit playback choice always wins
            if (displayOwned && replaced != null && replaced.App.Uid == app.Uid && replaced.Session.State.State != CaptureState.Failed)
                return replaced.Started.Task; // opening a picture never changes an existing playback-bar source
            if (replaced != null && replaced.App.Uid == app.Uid && replaced.KeepPhoneAudio == keepPhoneAudio
                && replaced.Session.State.State != CaptureState.Failed)
                return replaced.Started.Task; // a repeated drop shares the exact in-flight start or live source
            bool Conflicts(AndroidAudioSource other) => AndroidDeviceIdentity.Matches(other.Device, device)
                && (other.App.Package == app.Package || other.App.IsDeviceAudio || app.IsDeviceAudio);
            var conflicts = _sources.Where(Conflicts).ToArray();
            // Include already-retiring captures: a rapid device -> app -> second app
            // switch must finish releasing the device policy before either app starts.
            var releaseBeforeStart = _retiring.Where(Conflicts).Concat(conflicts).Distinct().ToArray();
            int index = conflicts.Length == 0 ? _sources.Count : _sources.IndexOf(conflicts[0]);
            var previousApps = new List<SavedCapture>();
            foreach (var conflict in conflicts)
            {
                if (_resumeAfterDevice.Remove(conflict, out var retained)) previousApps.AddRange(retained);
                if (app.IsDeviceAudio && !conflict.App.IsDeviceAudio && conflict.Session.State.State != CaptureState.Failed)
                    previousApps.Add(SaveCapture(conflict));
            }
            AndroidAudioState? previous = null;
            if (replaced != null && replaced.App.Uid == app.Uid)
            {
                previous = replaced.Session.State;
                outputId = SaveCapture(replaced).OutputId;
            }
            else if (previousApps.FirstOrDefault(p => p.App.Package == app.Package && p.App.Uid == app.Uid) is { } retainedApp)
            { previous = retainedApp.State; outputId = retainedApp.OutputId; }
            if (initialOutputs == null)
                _settings?.ForgetSourceRoutes(device, pkg => app.IsDeviceAudio || pkg == AndroidApp.DeviceAudioPackage || pkg == app.Package);
            foreach (var conflict in conflicts) Detach(conflict);
            source = CreateSource(device, app, keepPhoneAudio, outputId, displayOwned ? new object() : null, previous, index);
            source.InitialOutputs = initialOutputs;
            source.Resuming = initialOutputs != null;
            starts.Add(new PendingStart(source, releaseBeforeStart));
            if (app.IsDeviceAudio && previousApps.Count != 0) _resumeAfterDevice[source] = previousApps;
            else RestoreCaptures(previousApps, device, starts);
            PublishSources();
        }
        Notify();
        StartPending(starts);
        return source.Started.Task;
    }

    // These snapshots retain intent only: the superseded capture is fully released.
    private SavedCapture SaveCapture(AndroidAudioSource source)
    {
        var state = source.Session.State;
        string? output = state.State == CaptureState.Idle && !source.Started.Task.IsCompleted
            ? source.InitialOutputId : state.OutputId;
        var outputs = (_mayCapture == null ? null : _settings?.FindExternalAudio(
            ExternalAudioPreference.AndroidKey(source.Device, source.App.Package))?.Outputs)
            ?? _settings?.Get(source.Device, source.App.Package).ResumeOutputs;
        return new(source.Id, source.Device, source.App, outputs?.Contains(AndroidAudioSession.SourcePhoneOutputId) ?? source.KeepPhoneAudio,
            source.DisplayOwner, state, output, outputs, _sources.IndexOf(source));
    }

    private AndroidAudioSource CreateSource(AndroidDevice device, AndroidApp app, bool keepPhoneAudio,
        string? outputId, object? displayOwner, AndroidAudioState? previous, int index)
    {
        var saved = _settings?.Get(device, app.Package) ?? new AndroidAppAudioSettings();
        var source = new AndroidAudioSource(device, app, _factory(), keepPhoneAudio, _lifetime)
        { DisplayOwner = displayOwner,
          InitialOutputId = previous != null ? outputId : outputId ?? saved.OutputId };
        string? key = _mayCapture == null ? null : ExternalAudioPreference.AndroidKey(device, app.Package);
        var retained = _settings?.FindExternalAudio(key);
        source.Session.SetVolume(previous?.Volume ?? retained?.Volume ?? saved.Volume);
        source.Session.SetMute(previous?.Muted ?? retained?.Muted ?? false);
        source.Session.SetSmartVolume(_smartVolume?.Invoke(device, app.Package) ?? previous?.SmartVolume ?? !device.IsApp);
        var previousStage = source.Session.State.State;
        source.Handler = changedState =>
        {
            bool restore, stageChanged;
            lock (_gate)
            {
                if (!_sources.Contains(source)) return;
                var current = source.Session.State;
                stageChanged = previousStage != current.State;
                previousStage = current.State;
                _settings?.Remember(device, app.Package, current.Volume, current.OutputId);
                if (current.State == CaptureState.Receiving && _settings?.FindExternalAudio(key) is { Visible: true } choice)
                    _settings.RememberExternalAudio(choice with { Volume = current.Volume, Muted = current.Muted });
                restore = current.State == CaptureState.Failed && source.Started.Task.IsCompletedSuccessfully;
            }
            NotifySource(stageChanged ? null : source.Id);
            if (restore) _ = HandleFailureAsync(source, confirm: true);
        };
        source.Session.OutputSelectionCommitted += outputs =>
        {
            lock (_gate)
                if (_sources.Contains(source)) _settings?.RememberSourceOutputs(device, app.Package, outputs);
        };
        if (key != null)
            source.Session.PlaybackSelectionCommitted += () =>
            {
                lock (_gate)
                    if (_sources.Contains(source) && !source.SuppressRetention)
                        _settings?.UpdateExternalAudio(key, choice => choice.Resume
                            ? choice with { Outputs = source.Session.CurrentSelection() } : choice);
            };
        source.Session.Changed += source.Handler;
        _sources.Insert(Math.Clamp(index, 0, _sources.Count), source);
        return source;
    }

    private void RestoreCaptures(List<SavedCapture> captures, AndroidDevice device, List<PendingStart> starts)
    {
        if (_disposed) return;
        foreach (var capture in captures.OrderBy(c => c.Index))
        {
            if (_sources.Any(s => AndroidDeviceIdentity.Matches(s.Device, device)
                && (s.App.IsDeviceAudio || s.App.Package == capture.App.Package))) continue;
            var restored = CreateSource(device, capture.App, capture.KeepPhoneAudio,
                capture.OutputId, capture.DisplayOwner, capture.State, capture.Index);
            restored.InitialOutputs = capture.Outputs;
            restored.RememberRestoredSelection = capture.Outputs != null;
            var predecessors = _retiring.Where(s => AndroidDeviceIdentity.Matches(s.Device, device)
                && (s.App.IsDeviceAudio || s.App.Package == capture.App.Package)).ToArray();
            starts.Add(new PendingStart(restored, predecessors));
        }
    }

    private void StartPending(List<PendingStart> starts)
    {
        foreach (var start in starts)
            _ = StartAsync(start.Source, start.Predecessors, start.Source.KeepPhoneAudio, start.Source.InitialOutputId);
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
            if (_mayCapture?.Invoke(source.Device) == false) throw new OperationCanceledException();
            if (source.InitialOutputs != null)
                await ((IMultiOutputAudioSession)source.Session).SetOutputsAsync(source.InitialOutputs).ConfigureAwait(false);
            else await source.Session.SetOutputAsync(outputId).ConfigureAwait(false);
            await source.Session.StartAsync(source.Device, source.App, keepPhoneAudio, source.Lifetime.Token).ConfigureAwait(false);
            if (source.RememberRestoredSelection)
            {
                lock (_gate)
                    if (_sources.Contains(source)) _settings?.RememberSourceOutputs(source.Device, source.App.Package, source.InitialOutputs);
            }
            lock (_gate)
            {
                if (_sources.Contains(source) && !_shuttingDown && source.Session.State.State == CaptureState.Receiving)
                    RememberExternalSource(source, updateRoute: !source.Resuming);
                source.Started.TrySetResult(source);
            }
            if (_mayCapture != null) NotifySource(source.Id);
        }
        catch (OperationCanceledException)
        {
            source.Started.TrySetCanceled();
            await HandleFailureAsync(source).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            source.Started.TrySetException(ex);
            await HandleFailureAsync(source).ConfigureAwait(false);
        }
    }

    private void RememberExternalSource(AndroidAudioSource source, bool updateRoute)
    {
        if (_mayCapture == null || _settings == null || source.SuppressRetention || !_mayCapture(source.Device)
            || ExternalAudioPreference.AndroidKey(source.Device, source.App.Package) is not { } key) return;
        var state = source.Session.State;
        var previous = _settings.FindExternalAudio(key);
        if (source.Resuming && previous is { Resume: false }) return;
        var outputs = !updateRoute && previous != null ? previous.Outputs : source.Session.CurrentSelection();
        if (!source.Resuming && _resumeAfterDevice.TryGetValue(source, out var replaced))
            foreach (var capture in replaced)
                _settings.StopExternalAudio(ExternalAudioPreference.AndroidKey(capture.Device, capture.App.Package));
        _settings.RememberExternalAudio(new(key, source.App.Name, source.App, state.Volume, state.Muted, outputs));
    }

    private async Task HandleFailureAsync(AndroidAudioSource source, bool confirm = false)
    {
        // A route transaction may report a transient failure before rolling back.
        if (confirm && !await source.Session.IsFailedAsync().ConfigureAwait(false)) return;
        bool current = _mayCapture?.Invoke(source.Device) != false;
        lock (_gate)
            if (_shuttingDown || !_sources.Contains(source) || _mayCapture == null && !_resumeAfterDevice.ContainsKey(source)) return;
        try
        {
            if (confirm && !source.SuppressRetention) CaptureInterrupted?.Invoke(source.Device);
            await RemoveSourceAsync(source, restorePrevious: current).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                while (_history.Count >= 300) _history.Dequeue();
                _history.Enqueue("Previous capture restore: " + error.Message);
            }
        }
    }

    public Task RemoveAsync(string id, bool restorePrevious = true)
    {
        AndroidAudioSource? source;
        lock (_gate)
        {
            source = _sources.FirstOrDefault(s => s.Id == id);
            if (source != null)
            {
                source.SuppressRetention = true;
                _settings?.RememberSourceOutputs(source.Device, source.App.Package, null);
                if (_mayCapture != null) _settings?.StopExternalAudio(ExternalAudioPreference.AndroidKey(source.Device, source.App.Package));
            }
            if (source == null)
            {
                foreach (var captures in _resumeAfterDevice.Values) captures.RemoveAll(c => c.OriginalId == id);
                return Task.CompletedTask;
            }
        }
        return RemoveSourceAsync(source, restorePrevious);
    }

    public Task RemoveApplicationAsync(AndroidDevice device, string package)
    {
        AndroidAudioSource? source;
        lock (_gate)
        {
            _settings?.RememberSourceOutputs(device, package, null);
            if (_mayCapture != null) _settings?.StopExternalAudio(ExternalAudioPreference.AndroidKey(device, package));
            foreach (var captures in _resumeAfterDevice.Values)
                captures.RemoveAll(c => c.App.Package == package && AndroidDeviceIdentity.Matches(c.Device, device));
            source = _sources.FirstOrDefault(s => s.App.Package == package && AndroidDeviceIdentity.Matches(s.Device, device));
            if (source != null) source.SuppressRetention = true;
        }
        return source == null ? Task.CompletedTask : RemoveSourceAsync(source, restorePrevious: true);
    }

    internal async Task SuspendUnavailableAsync(Func<AndroidDevice, bool> isCurrent)
    {
        AndroidAudioSource[] offline;
        lock (_gate)
        {
            offline = _sources.Where(source => !isCurrent(source.Device)).ToArray();
            if (offline.Length == 0) return;
            foreach (var source in offline)
            {
                _resumeAfterDevice.Remove(source);
                Detach(source);
            }
            PublishSources();
        }
        Notify();
        await Task.WhenAll(offline.Select(RetireAsync)).ConfigureAwait(false);
    }

    internal async Task RemovePhoneAsync(AndroidDevice device)
    {
        AndroidAudioSource[] owned, other;
        lock (_gate)
        {
            owned = _sources.Concat(_retiring).Where(s => AndroidDeviceIdentity.Matches(s.Device, device)).Distinct().ToArray();
            string removedKey = AndroidDeviceIdentity.Key(device);
            bool RoutesToRemovedPhone(string? id) => AndroidOutputIdentity.TryParseDeviceId(id, out string key, out _) && key == removedKey;
            foreach (var captures in _resumeAfterDevice.Values)
            {
                captures.RemoveAll(c => AndroidDeviceIdentity.Matches(c.Device, device));
                for (int i = 0; i < captures.Count; ++i)
                {
                    var capture = captures[i];
                    if (!RoutesToRemovedPhone(capture.OutputId) && capture.Outputs?.Any(RoutesToRemovedPhone) != true) continue;
                    var remaining = capture.Outputs?.Where(id => !RoutesToRemovedPhone(id)).ToArray();
                    captures[i] = capture with { Outputs = remaining is { Length: > 0 } ? remaining : null,
                        OutputId = RoutesToRemovedPhone(capture.OutputId) ? remaining?.FirstOrDefault() : capture.OutputId };
                }
            }
            foreach (var source in owned.Where(_sources.Contains))
            {
                _resumeAfterDevice.Remove(source);
                Detach(source);
            }
            other = _sources.ToArray();
            PublishSources();
        }
        Notify();
        await Task.WhenAll(owned.Select(RetireAsync).Concat(other.Select(s => s.Session.PhoneRemovedAsync(device)))).ConfigureAwait(false);
    }

    private async Task RemoveSourceAsync(AndroidAudioSource source, bool restorePrevious)
    {
        var starts = new List<PendingStart>();
        lock (_gate)
        {
            if (!_sources.Contains(source)) return;
            _resumeAfterDevice.Remove(source, out var captures);
            Detach(source);
            if (restorePrevious && captures != null) RestoreCaptures(captures, source.Device, starts);
            PublishSources();
        }
        var release = RetireAsync(source);
        Notify();
        StartPending(starts);
        await release.ConfigureAwait(false);
        // A failed restored app remains visible as failed; do not strand its siblings.
        foreach (var start in starts)
            try { await start.Source.Started.Task.ConfigureAwait(false); } catch { }
    }
    public async Task ReleaseDisplayAudioAsync(AndroidAudioSource source, string? ownerPackage = null)
    {
        lock (_gate)
        {
            if (source.DisplayOwner == null
                || (ownerPackage != null && ownerPackage != source.App.Package)) return;
            if (!_sources.Contains(source))
            {
                bool pending = false;
                foreach (var captures in _resumeAfterDevice.Values)
                    pending |= captures.RemoveAll(c => c.DisplayOwner == source.DisplayOwner) != 0;
                var restored = _sources.FirstOrDefault(s => s.DisplayOwner == source.DisplayOwner);
                if (restored == null)
                {
                    if (pending && !_disposed && !_shuttingDown && _mayCapture?.Invoke(source.Device) != false)
                        _settings?.RememberSourceOutputs(source.Device, source.App.Package, null);
                    return;
                }
                source = restored;
            }
            if (!_disposed && !_shuttingDown && _mayCapture?.Invoke(source.Device) != false)
            {
                source.SuppressRetention = true;
                _settings?.RememberSourceOutputs(source.Device, source.App.Package, null);
                if (_mayCapture != null) _settings?.StopExternalAudio(ExternalAudioPreference.AndroidKey(source.Device, source.App.Package));
            }
        }
        await RemoveSourceAsync(source, restorePrevious: true).ConfigureAwait(false);
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
        finally
        {
            lock (_gate) _retiring.Remove(source);
            try { ResourcesRetired?.Invoke(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Audio retirement notification: " + ex.Message); }
        }
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
            _resumeAfterDevice.Clear();
            sources = _sources.ToArray();
            if (!_shuttingDown && _mayCapture != null)
                foreach (var source in sources)
                {
                    _settings?.RememberSourceOutputs(source.Device, source.App.Package, null);
                    _settings?.StopExternalAudio(ExternalAudioPreference.AndroidKey(source.Device, source.App.Package));
                }
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
    private void Notify() => NotifySource(null);
    private void NotifySource(string? id)
    {
        if (SourceChanged is { } targets) foreach (Action<string?> listener in targets.GetInvocationList())
            try { listener(id); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex.Message); }
        var listeners = Changed;
        if (listeners != null) foreach (Action listener in listeners.GetInvocationList())
            try { listener(); } catch { /* one closed UI subscriber must not stop another source */ }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) _shuttingDown = _disposed = true;
        await StopAllAsync().ConfigureAwait(false);
        Task[] retiring; lock (_gate) retiring = _retiring.Select(s => s.Released.Task).ToArray();
        await Task.WhenAll(retiring).ConfigureAwait(false);
    }
}

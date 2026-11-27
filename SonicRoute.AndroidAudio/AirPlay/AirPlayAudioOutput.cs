using SonicRoute.AndroidAudio;

namespace SonicRoute.AirPlay;

internal interface IPhoneAudioSession
{
    AndroidAudioState State { get; }
    AndroidPcmSource? Pcm { get; }
    void SetVolume(int percent);
    void SetMute(bool muted);
    Task SetOutputAsync(string? endpoint);
}

/// <summary>One AirPlay stream using the shared Windows and Android PCM renderers.</summary>
internal sealed class AirPlayAudioOutput : IPhoneAudioSession, IMultiOutputAudioSession, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _routes = new(1, 1);
    private readonly Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer> _createPlayer;
    private readonly CancellationToken _lifetime;
    private readonly Func<Action, IDisposable> _watchDefault;
    private readonly Action _changed;
    private readonly Action _failed;
    private readonly AndroidAudioSettingsStore? _preferences;
    private readonly Func<string?[], string?[]>? _resolveOutputs;
    private readonly ExternalAudioPreference? _initial;
    private readonly AndroidPcmSource _pcm = new();
    private readonly PcmOutputMirrors _mirrors;
    private AndroidAudioState _state = new(CaptureState.Starting);
    private string[] _extraIds = [];
    private IPcmPlayer? _player, _failedPlayer;
    private IDisposable? _defaultWatch;
    private volatile bool _disposed;
    private int _defaultQueued, _defaultVersion, _outputLost;
    private int _resamplePhase;
    private float _previousLeft, _previousRight;
    private float[]? _converted;

    internal AirPlayAudioOutput(string name, Action changed, Action failed,
        Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer> createPlayer,
        Func<Action, IDisposable> watchDefault, CancellationToken lifetime,
        AndroidAudioSettingsStore? preferences = null, string? preferenceKey = null, Func<string?[], string?[]>? resolveOutputs = null)
    {
        Name = name;
        _changed = changed; _failed = failed; _createPlayer = createPlayer; _watchDefault = watchDefault;
        _lifetime = lifetime;
        _mirrors = new(RemoveFailedOutputsAsync);
        _preferences = preferences;
        _resolveOutputs = resolveOutputs;
        PreferenceKey = preferences == null ? null : preferenceKey;
        // A display name is enough for a card, but two senders may share it.
        if (preferenceKey?.StartsWith(ExternalAudioPreference.AirPlayPrefix + "id:", StringComparison.Ordinal) == true)
            _initial = preferences?.FindExternalAudio(preferenceKey);
        if (_initial is { } saved)
        {
            _state = _state with { Volume = saved.Volume, Muted = saved.Muted };
            _pcm.Volume = saved.Muted ? 0 : saved.Volume / 100f;
        }
    }

    internal string Id { get; } = "airplay:" + Guid.NewGuid().ToString("N");
    internal string Name { get; }
    internal string? PreferenceKey { get; }
    public AndroidAudioState State => Volatile.Read(ref _state);
    public AndroidPcmSource? Pcm => State.State == CaptureState.Receiving ? _pcm : null;
    internal int BufferedSamples => _pcm.BufferedSamples;
    internal double RemoteVolume { get; private set; }
    internal bool CanChangeActiveOutputs => !_disposed && State.State == CaptureState.Receiving;
    internal bool HasSavedOutputs => _initial?.Outputs.Any(id => id != null) == true;

    // The sender owns playback. Open the remembered renderers on the first media
    // packet, before any samples can reach a temporary default output.
    internal async Task PreparePlaybackAsync(CancellationToken token)
    {
        await _routes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_disposed || State.State != CaptureState.Starting || _player != null || !HasSavedOutputs) return;
            var outputs = _resolveOutputs?.Invoke(_initial!.Outputs) ?? _initial!.Outputs;
            var (primary, extras) = PcmOutputMirrors.Select(outputs, null);
            if (primary == null && extras.Length == 0) return;
            _mirrors.SetLevels(_pcm.Volume, false);
            _mirrors.SetHeaderGain(_pcm.HeaderGain);
            try
            {
                await _mirrors.ApplyAsync(extras, (pcm, id, cancel) => _createPlayer(pcm, id, cancel), async () =>
                {
                    var player = _createPlayer(_pcm, primary, token);
                    try
                    {
                        if (player is AndroidPhonePcmPlayer phone) await phone.VerifyBeforePlaybackAsync(token).ConfigureAwait(false);
                        if (player is WasapiPcmPlayer wasapi) await wasapi.PlayAndConfirmAsync(token).ConfigureAwait(false);
                        else { token.ThrowIfCancellationRequested(); player.Play(); }
                        if (primary == null) _defaultWatch ??= _watchDefault(DefaultChanged);
                        SetPlayer(player);
                    }
                    catch { DisposeQuietly(player); throw; }
                }, token).ConfigureAwait(false);
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                System.Diagnostics.Trace.WriteLine("AirPlay output restoration: " + error.GetType().Name);
                return;
            }
            Volatile.Write(ref _extraIds, extras);
            _pcm.Mirrors = _mirrors;
            Volatile.Write(ref _state, _state with { State = CaptureState.Receiving, OutputId = primary });
            PublishStarted();
        }
        finally { _routes.Release(); }
    }

    private void PublishStarted()
    {
        if (PreferenceKey is { } key)
        {
            var state = State;
            _preferences!.RememberExternalAudio(new(key, Name, null, state.Volume, state.Muted, _initial?.Outputs ?? [null]));
        }
        _changed();
    }

    internal void Write(ReadOnlySpan<float> samples, int sampleRate)
    {
        // Media has one producer. UI edits use the PCM provider's independent volume fields.
        if (State.State == CaptureState.Starting)
        {
            bool started = false;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_player == null)
                {
                    _defaultWatch ??= _watchDefault(DefaultChanged);
                    var player = _createPlayer(_pcm, null, _lifetime);
                    try { player.Play(); }
                    catch { DisposeQuietly(player); throw; }
                    SetPlayer(player);
                    _pcm.Mirrors = _mirrors;
                    Volatile.Write(ref _state, _state with { State = CaptureState.Receiving });
                    started = true;
                }
            }
            if (started)
            {
                _mirrors.SetLevels(_pcm.Volume, false);
                _mirrors.SetHeaderGain(_pcm.HeaderGain);
                PublishStarted();
            }
        }
        if (sampleRate == AndroidPcmSource.SampleRate) _pcm.Write(samples);
        else
        {
            // Continuous 44.1 -> 48 kHz interpolation with fractional phase across packet boundaries.
            var result = _converted ??= new float[2 * (4096 * 160 / 147 + 4)];
            int count = 0;
            for (int i = 0; i < samples.Length; i += 2)
            {
                while (_resamplePhase < 160)
                {
                    float fraction = _resamplePhase / 160f;
                    result[count++] = _previousLeft + (samples[i] - _previousLeft) * fraction;
                    result[count++] = _previousRight + (samples[i + 1] - _previousRight) * fraction;
                    _resamplePhase += 147;
                }
                _resamplePhase -= 160;
                _previousLeft = samples[i]; _previousRight = samples[i + 1];
            }
            _pcm.Write(result.AsSpan(0, count));
        }
    }

    internal void Flush()
    {
        _resamplePhase = 0; _previousLeft = _previousRight = 0;
        _pcm.Clear();
        _mirrors.ClearBuffers();
    }
    internal void SetRemoteVolume(double db)
    {
        if (!double.IsFinite(db)) return;
        RemoteVolume = Math.Clamp(db, -144, 0);
        float gain = db <= -144 ? 0 : (float)Math.Pow(10, RemoteVolume / 20);
        _pcm.HeaderGain = gain;
        _mirrors.SetHeaderGain(gain);
    }

    public void SetVolume(int percent) => SetLevel(Math.Clamp(percent, 0, 100), null);
    public void SetMute(bool muted) => SetLevel(null, muted);
    private void SetLevel(int? volume, bool? muted)
    {
        float level;
        lock (_gate)
        {
            if (_disposed || (volume ?? _state.Volume) == _state.Volume && (muted ?? _state.Muted) == _state.Muted) return;
            var next = _state with { Volume = volume ?? _state.Volume, Muted = muted ?? _state.Muted };
            level = next.Muted ? 0 : next.Volume / 100f;
            _pcm.Volume = level;
            Volatile.Write(ref _state, next);
            if (next.State == CaptureState.Receiving)
                _preferences?.UpdateExternalAudio(PreferenceKey, choice => choice with { Volume = next.Volume, Muted = next.Muted });
        }
        _mirrors.SetLevels(level, false);
        _changed();
    }

    string?[] IMultiOutputAudioSession.GetOutputs() => [State.OutputId, .. Volatile.Read(ref _extraIds)];
    Task IMultiOutputAudioSession.SetOutputsAsync(IReadOnlyList<string?> outputs) => Task.Run(() => ChangeOutputsAsync(outputs.ToArray()));
    (int VolumePercent, bool Muted) IMultiOutputAudioSession.GetMirrorGain(string id) => _mirrors.GetGain(id);
    (int VolumePercent, bool Muted) IMultiOutputAudioSession.SetMirrorGain(string id, int? volumePercent, bool? muted, bool toggleMute) =>
        _mirrors.SetGain(id, volumePercent, muted, toggleMute);
    public Task SetOutputAsync(string? endpoint) => Task.Run(() => ChangeOutputsAsync([endpoint]));
    internal Task SetActiveOutputsAsync(string?[] outputs, Func<bool> stillActive) => Task.Run(() => ChangeOutputsAsync(outputs, stillActive));
    internal Task RestoreOutputsAsync(string?[] outputs, Func<bool> stillActive) => ChangeOutputsAsync(outputs, stillActive, remember: false);

    private async Task ChangeOutputsAsync(string?[] desired, Func<bool>? stillActive = null, bool remember = true)
    {
        bool changed;
        bool notifyFailed = false;
        await _routes.WaitAsync(_lifetime).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (stillActive != null && !stillActive()) throw new OperationCanceledException();
                if (_disposed || _state.State != CaptureState.Receiving || _player == null)
                    throw new InvalidOperationException("AirPlay audio has stopped.");
            }
            changed = await ChangeOutputsLockedAsync(desired).ConfigureAwait(false);
            if (remember)
                _preferences?.UpdateExternalAudio(PreferenceKey, choice => choice with { Outputs = ((IMultiOutputAudioSession)this).GetOutputs() });
        }
        catch
        {
            notifyFailed = Interlocked.Exchange(ref _outputLost, 0) != 0;
            throw;
        }
        finally
        {
            _routes.Release();
            if (notifyFailed) _failed();
        }
        if (changed) _changed();
    }

    private async Task<bool> ChangeOutputsLockedAsync(string?[] desired)
    {
        string? currentPrimary;
        string[] currentExtras;
        lock (_gate)
        {
            currentPrimary = _state.OutputId;
            currentExtras = Volatile.Read(ref _extraIds);
        }
        var (primary, extras) = PcmOutputMirrors.Select(desired, currentPrimary);
        if (string.Equals(primary, currentPrimary, StringComparison.OrdinalIgnoreCase)
            && extras.Length == currentExtras.Length
            && extras.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(currentExtras))
            return false;

        bool needsWatch = primary == null;
        bool addedWatch = needsWatch && _defaultWatch == null;
        if (addedWatch) _defaultWatch = _watchDefault(DefaultChanged);
        try
        {
            await _mirrors.ApplyAsync(extras, (pcm, id, token) => _createPlayer(pcm, id, token), async () =>
            {
                if (!string.Equals(primary, currentPrimary, StringComparison.OrdinalIgnoreCase))
                    await ReplacePlayerAsync(Volatile.Read(ref _player)!, primary).ConfigureAwait(false);
            }, _lifetime).ConfigureAwait(false);
        }
        catch
        {
            if (addedWatch) StopWatch();
            throw;
        }
        lock (_gate)
        {
            Volatile.Write(ref _state, _state with { OutputId = primary });
            Volatile.Write(ref _extraIds, extras);
            _pcm.Mirrors = _mirrors;
        }
        if (!needsWatch) StopWatch();
        return true;
    }

    private async Task ReplacePlayerAsync(IPcmPlayer previous, string? endpoint)
    {
        var next = _createPlayer(_pcm, endpoint, _lifetime);
        bool switching = false;
        try
        {
            if (next is AndroidPhonePcmPlayer phone)
                await phone.VerifyBeforePlaybackAsync(_lifetime).ConfigureAwait(false);
            _lifetime.ThrowIfCancellationRequested();
            switching = true;
            try { previous.Stop(); }
            catch (Exception ex) when (previous is AndroidPhonePcmPlayer or WasapiPcmPlayer)
            { System.Diagnostics.Trace.WriteLine("AirPlay previous output: " + ex.GetType().Name); }
            next.Play();
        }
        catch
        {
            DisposeQuietly(next);
            if (switching && !_lifetime.IsCancellationRequested
                && previous is not AndroidPhonePcmPlayer { Failure: not null }
                && !ReferenceEquals(previous, Volatile.Read(ref _failedPlayer)))
            {
                try { previous.Play(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("AirPlay output recovery: " + ex.GetType().Name);
                    Interlocked.Exchange(ref _outputLost, 1);
                }
            }
            throw;
        }
        SetPlayer(next);
        DisposeQuietly(previous);
    }

    private void SetPlayer(IPcmPlayer next)
    {
        Volatile.Write(ref _player, next);
        Volatile.Write(ref _failedPlayer, null);
        _mirrors.SetPrimary(next);
        if (next is WasapiPcmPlayer wasapi)
            wasapi.Failed += _ => QueuePrimaryFallback(next, allowWindowsFallback: false);
        if (next is AndroidPhonePcmPlayer phone)
        {
            phone.Failed += _ => QueuePrimaryFallback(phone, allowWindowsFallback: true);
            if (phone.Failure != null) QueuePrimaryFallback(phone, allowWindowsFallback: true);
        }
    }

    private async Task RemoveFailedOutputsAsync(Predicate<IPcmPlayer> match)
    {
        bool changed = false;
        await _routes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed && _mirrors.Remove(match))
            {
                Volatile.Write(ref _extraIds, _mirrors.IdsSnapshot());
                changed = true;
            }
        }
        finally { _routes.Release(); }
        if (changed) _changed();
    }

    private void QueuePrimaryFallback(IPcmPlayer failedPlayer, bool allowWindowsFallback)
    {
        if (!ReferenceEquals(Volatile.Read(ref _player), failedPlayer) || _lifetime.IsCancellationRequested) return;
        Volatile.Write(ref _failedPlayer, failedPlayer);
        // Output failure callbacks may run on the renderer's own pump thread. Retire it on a separate worker.
        _ = Task.Run(async () =>
        {
            bool changed = false;
            bool failed = false;
            bool cancelled = false;
            try
            {
                await _routes.WaitAsync(_lifetime).ConfigureAwait(false);
                try
                {
                    bool active;
                    lock (_gate)
                        active = !_disposed && _state.State == CaptureState.Receiving && ReferenceEquals(_player, failedPlayer);
                    if (!active) return;
                    (changed, failed, cancelled) = await FallbackFromPrimaryLockedAsync(allowWindowsFallback, "AirPlay output fallback").ConfigureAwait(false);
                }
                finally { _routes.Release(); }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { cancelled = true; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("AirPlay primary fallback: " + ex.GetType().Name);
                if (ReferenceEquals(Volatile.Read(ref _player), failedPlayer) && !_lifetime.IsCancellationRequested)
                    failed = true;
            }
            if (cancelled) return;
            if (failed) _failed();
            else if (changed) _changed();
        });
    }

    internal Task PhoneRemovedAsync(AndroidDevice device) =>
        Task.Run(async () =>
        {
            bool changed = false;
            bool failed = false;
            bool cancelled = false;
            try
            {
                await _routes.WaitAsync(_lifetime).ConfigureAwait(false);
                try
                {
                    IPcmPlayer? current;
                    lock (_gate)
                    {
                        if (_disposed || _state.State != CaptureState.Receiving) return;
                        current = _player;
                    }
                    if (_mirrors.Remove(p => p is AndroidPhonePcmPlayer phone && AndroidDeviceIdentity.Matches(device, phone.Device)))
                    {
                        Volatile.Write(ref _extraIds, _mirrors.IdsSnapshot());
                        changed = true;
                    }
                    if (current is AndroidPhonePcmPlayer active && AndroidDeviceIdentity.Matches(device, active.Device))
                    {
                        Volatile.Write(ref _failedPlayer, active);
                        var fallback = await FallbackFromPrimaryLockedAsync(allowWindowsFallback: true, "AirPlay phone output fallback").ConfigureAwait(false);
                        changed |= fallback.Changed;
                        failed = fallback.Failed;
                        cancelled = fallback.Cancelled;
                    }
                }
                finally { _routes.Release(); }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { cancelled = true; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("AirPlay phone output fallback: " + ex.GetType().Name);
                if (Volatile.Read(ref _player) is AndroidPhonePcmPlayer current && AndroidDeviceIdentity.Matches(device, current.Device)
                    && !_lifetime.IsCancellationRequested)
                    failed = true;
            }
            if (cancelled) return;
            if (failed) _failed();
            else if (changed) _changed();
        });

    private async Task<(bool Changed, bool Failed, bool Cancelled)> FallbackFromPrimaryLockedAsync(bool allowWindowsFallback, string tracePrefix)
    {
        bool changed = false;
        string[] remaining = _mirrors.IdsSnapshot();
        for (int i = 0; i < remaining.Length; i++)
        {
            try
            {
                if (await ChangeOutputsLockedAsync(remaining[i..]).ConfigureAwait(false)) changed = true;
                return (changed, false, false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return (changed, false, true); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine(tracePrefix + ": " + ex.GetType().Name); }
        }
        if (allowWindowsFallback)
        {
            try
            {
                if (await ChangeOutputsLockedAsync([null]).ConfigureAwait(false)) changed = true;
                return (changed, false, false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return (changed, false, true); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine(tracePrefix + ": " + ex.GetType().Name); }
        }
        return (changed, true, false);
    }

    private void DefaultChanged()
    {
        Interlocked.Increment(ref _defaultVersion);
        QueueDefaultRefresh();
    }

    private void QueueDefaultRefresh()
    {
        if (Interlocked.CompareExchange(ref _defaultQueued, 1, 0) == 0)
            _ = Task.Run(RefreshDefaultOutputAsync);
    }

    private async Task RefreshDefaultOutputAsync()
    {
        int handled = 0;
        bool notifyFailed = false;
        bool stop = false;
        try
        {
            do
            {
                handled = Volatile.Read(ref _defaultVersion);
                await _routes.WaitAsync(_lifetime).ConfigureAwait(false);
                try
                {
                    IPcmPlayer? player;
                    lock (_gate)
                    {
                        if (_disposed || _state.State != CaptureState.Receiving || _player == null || _state.OutputId != null)
                        {
                            stop = true;
                            return;
                        }
                        player = _player;
                    }
                    await ReplacePlayerAsync(player, null).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { stop = true; return; }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("AirPlay default output: " + ex.GetType().Name);
                    notifyFailed = Interlocked.Exchange(ref _outputLost, 0) != 0;
                    if (notifyFailed) { stop = true; return; }
                }
                finally { _routes.Release(); }
            } while (handled != Volatile.Read(ref _defaultVersion) && !_lifetime.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { stop = true; }
        finally
        {
            Interlocked.Exchange(ref _defaultQueued, 0);
            if (!stop && !_lifetime.IsCancellationRequested && handled != Volatile.Read(ref _defaultVersion))
                QueueDefaultRefresh();
            if (notifyFailed) _failed();
        }
    }

    private void StopWatch() { var watch = _defaultWatch; _defaultWatch = null; DisposeQuietly(watch); }
    private static void DisposeQuietly(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("AirPlay audio cleanup: " + ex.GetType().Name); }
    }

    public async ValueTask DisposeAsync()
    {
        await _routes.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                Volatile.Write(ref _state, _state with { State = CaptureState.Idle });
            }
            StopWatch();
            _pcm.Mirrors = null;
            _mirrors.Clear();
            Volatile.Write(ref _extraIds, []);
            Volatile.Write(ref _failedPlayer, null);
            DisposeQuietly(Interlocked.Exchange(ref _player, null));
            _pcm.Clear();
            _converted = null;
        }
        finally { _routes.Release(); }
    }
}

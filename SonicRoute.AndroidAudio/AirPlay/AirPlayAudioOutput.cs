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

/// <summary>One AirPlay stream on the same PCM/WASAPI path as incoming Android audio.</summary>
internal sealed class AirPlayAudioOutput : IPhoneAudioSession, IDisposable
{
    private readonly object _gate = new();
    private readonly Func<AndroidPcmSource, string?, IPcmPlayer> _createPlayer;
    private readonly Func<Action, IDisposable> _watchDefault;
    private readonly Action _changed;
    private readonly Action _failed;
    private readonly AndroidPcmSource _pcm = new();
    private AndroidAudioState _state = new(CaptureState.Starting);
    private IPcmPlayer? _player;
    private IDisposable? _defaultWatch;
    private bool _disposed;
    private int _defaultQueued, _defaultVersion;
    private int _resamplePhase;
    private float _previousLeft, _previousRight;
    private float[]? _converted;

    internal AirPlayAudioOutput(string name, Action changed, Action failed,
        Func<AndroidPcmSource, string?, IPcmPlayer> createPlayer, Func<Action, IDisposable> watchDefault)
    {
        Name = name;
        _changed = changed; _failed = failed; _createPlayer = createPlayer; _watchDefault = watchDefault;
    }

    internal string Id { get; } = "airplay:" + Guid.NewGuid().ToString("N");
    internal string Name { get; }
    public AndroidAudioState State => Volatile.Read(ref _state);
    public AndroidPcmSource? Pcm => State.State == CaptureState.Receiving ? _pcm : null;
    internal int BufferedSamples => _pcm.BufferedSamples;
    internal double RemoteVolume { get; private set; }

    internal void Write(ReadOnlySpan<float> samples, int sampleRate)
    {
        // Media has one producer. UI edits use the PCM provider's independent volume fields.
        if (State.State == CaptureState.Starting)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_player == null)
                {
                    _defaultWatch ??= _watchDefault(DefaultChanged);
                    ReplacePlayer(null);
                    Volatile.Write(ref _state, _state with { State = CaptureState.Receiving });
                }
            }
            _changed();
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
    }
    internal void SetRemoteVolume(double db)
    {
        if (!double.IsFinite(db)) return;
        RemoteVolume = Math.Clamp(db, -144, 0);
        _pcm.HeaderGain = db <= -144 ? 0 : (float)Math.Pow(10, RemoteVolume / 20);
    }

    public void SetVolume(int percent) => SetLevel(Math.Clamp(percent, 0, 100), null);
    public void SetMute(bool muted) => SetLevel(null, muted);
    private void SetLevel(int? volume, bool? muted)
    {
        lock (_gate)
        {
            if (_disposed || (volume ?? _state.Volume) == _state.Volume && (muted ?? _state.Muted) == _state.Muted) return;
            var next = _state with { Volume = volume ?? _state.Volume, Muted = muted ?? _state.Muted };
            _pcm.Volume = next.Muted ? 0 : next.Volume / 100f;
            Volatile.Write(ref _state, next);
        }
        _changed();
    }

    public Task SetOutputAsync(string? endpoint) => Task.Run(() =>
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_player == null) throw new InvalidOperationException("AirPlay audio has stopped.");
            if (string.Equals(endpoint, _state.OutputId, StringComparison.OrdinalIgnoreCase)) return;
            bool addedWatch = endpoint == null && _defaultWatch == null;
            if (addedWatch) _defaultWatch = _watchDefault(DefaultChanged);
            try { ReplacePlayer(endpoint); }
            catch
            {
                if (addedWatch) StopWatch();
                throw;
            }
            Volatile.Write(ref _state, _state with { OutputId = endpoint });
            if (endpoint != null) StopWatch();
        }
        _changed();
    });

    private void ReplacePlayer(string? endpoint)
    {
        var next = _createPlayer(_pcm, endpoint);
        var old = _player;
        try
        {
            old?.Stop();
            next.Play();
        }
        catch
        {
            DisposeQuietly(next);
            old?.Play();
            throw;
        }
        Volatile.Write(ref _player, next);
        if (next is WasapiPcmPlayer wasapi) wasapi.Failed += _ =>
        {
            if (ReferenceEquals(Volatile.Read(ref _player), next)) _failed();
        };
        DisposeQuietly(old);
    }

    private void DefaultChanged()
    {
        Interlocked.Increment(ref _defaultVersion);
        QueueDefaultRefresh();
    }

    private void QueueDefaultRefresh()
    {
        if (Interlocked.Exchange(ref _defaultQueued, 1) != 0) return;
        _ = Task.Run(() =>
        {
            int version = Volatile.Read(ref _defaultVersion);
            try
            {
                lock (_gate)
                {
                    if (!_disposed && _state.OutputId == null && _player != null) ReplacePlayer(null);
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("AirPlay default output: " + ex.GetType().Name); }
            finally
            {
                Interlocked.Exchange(ref _defaultQueued, 0);
                if (version != Volatile.Read(ref _defaultVersion)) QueueDefaultRefresh();
            }
        });
    }

    private void StopWatch() { var watch = _defaultWatch; _defaultWatch = null; DisposeQuietly(watch); }
    private static void DisposeQuietly(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("AirPlay audio cleanup: " + ex.GetType().Name); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Volatile.Write(ref _state, _state with { State = CaptureState.Idle });
            StopWatch();
            DisposeQuietly(Interlocked.Exchange(ref _player, null));
            _pcm.Clear();
            _converted = null;
        }
    }
}

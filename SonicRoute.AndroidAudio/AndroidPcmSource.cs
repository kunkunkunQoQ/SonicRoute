using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Numerics;

namespace SonicRoute.AndroidAudio;

/// <summary>One producer and one WASAPI consumer; bounded float PCM, 48 kHz stereo.</summary>
public sealed class AndroidPcmSource : IWasapiPlaybackSource
{
    public const int SampleRate = 48000, Channels = 2;
    // The phone profile includes headroom. PC-to-PC playback already includes the source mixer level.
    internal bool PreserveQuietLevel { get; init; }

    private const int CrossfadeFrames = 48; // ~1 ms @ 48 kHz
    private const int CrossfadeSamples = CrossfadeFrames * Channels;
    private static readonly double TimestampToMs = 1000.0 / Stopwatch.Frequency;

    private readonly object _gate = new();
    private readonly float[] _samples;
    private readonly Func<double>? _clockMs;
    private AudioLatencyProfile _profile;
    private int _latencyMode;
    private double _targetBufferMs;
    private double _estimatedJitterMs;
    private double _lastArrivalMs = double.NaN;
    private double _lastPacketDurationMs;
    private double _lastJitterAdjustMs = double.NaN;
    private double _lastUnderrunAdjustMs = double.NaN;
    private double _stableSinceMs = double.NaN;
    private double _lastDecayMs = double.NaN;
    private double _primeWaitStartMs = double.NaN;
    private bool _hasReceivedAudio;
    private bool _primingEnabled;
    private bool _primed;
    private int _start, _count;
    private float _volume = 1, _gain = 1;
    private bool _smartVolume;
    private float _smartEnvelope;
    private long _dropped, _underflow;
    private long _written, _nonSilent, _read;
    private float _lastPeak;
    private float _meterUnreadPeak = -1f, _meterActivePeak;
    private long _lastWriteTick = -1000, _lastNonSilentWriteTick = -1000;
    private int _meterWaiting;

    public AndroidPcmSource() : this(AudioLatencyMode.Balanced) { }

    public AndroidPcmSource(AudioLatencyMode latencyMode) : this(latencyMode, null) { }

    internal AndroidPcmSource(AudioLatencyMode latencyMode, Func<double>? clockMs = null, bool packetCounterOnly = false)
    {
        _samples = packetCounterOnly ? [] : new float[SampleRate * Channels / 2]; // 500 ms maximum for float renderers
        _clockMs = clockMs;
        _profile = AudioLatencyProfile.For(latencyMode);
        _latencyMode = (int)_profile.Mode;
        _targetBufferMs = _profile.InitialTargetMs;
    }

    public static event Action? PeakActivity;
    // Only a phone renderer subscribes; silence still carries timing without polling.
    internal event Action? DataAvailable;
    private event Action? PlaybackBufferRequested;
    event Action? IWasapiPlaybackSource.PlaybackBufferRequested
    {
        add => PlaybackBufferRequested += value;
        remove => PlaybackBufferRequested -= value;
    }
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    public AudioLatencyMode LatencyMode
    {
        get => (AudioLatencyMode)Volatile.Read(ref _latencyMode);
        set
        {
            var next = AudioLatencyProfile.For(value);
            lock (_gate)
            {
                if (_profile.Mode == value) return;
                ApplyLatencyProfileLocked(next);
            }
            Mirrors?.SetLatencyMode(value);
        }
    }

    public double TargetBufferMs { get { lock (_gate) return _targetBufferMs; } }
    public double EstimatedJitterMs { get { lock (_gate) return _estimatedJitterMs; } }
    public double BufferedDurationMs { get { lock (_gate) return _count * 1000.0 / (SampleRate * Channels); } }

    public int BufferedSamples { get { lock (_gate) return _count; } }
    public long DroppedSamples => Interlocked.Read(ref _dropped);
    public long UnderflowSamples => Interlocked.Read(ref _underflow);
    public long SamplesWritten => Interlocked.Read(ref _written);
    public long NonSilentSamplesWritten => Interlocked.Read(ref _nonSilent);
    public long SamplesConsumed => Interlocked.Read(ref _read);
    public float LastPeak => Volatile.Read(ref _lastPeak);
    public float Volume { get => Volatile.Read(ref _volume); set => Volatile.Write(ref _volume, Math.Clamp(value, 0, 1)); }
    public float HeaderGain { get => Volatile.Read(ref _gain); set => Volatile.Write(ref _gain, value); }
    public bool SmartVolume { get => Volatile.Read(ref _smartVolume); set => Volatile.Write(ref _smartVolume, value); }
    public float EffectiveGain => ComputeEffectiveGain();

    internal volatile PcmOutputMirrors? Mirrors;
    public void Write(ReadOnlySpan<float> samples)
    {
        var metrics = Measure(samples);
        WriteMeasured(samples, metrics);
        Mirrors?.Write(samples, metrics, this);
    }

    internal static (int NonSilent, float Peak) Measure(ReadOnlySpan<float> samples)
    {
        if (samples.Length % Channels != 0) throw new ArgumentException("PCM frames must contain both channels.");
        int nonSilent = 0; float peak = 0;
        int offset = 0;
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
        {
            var peaks = Vector<float>.Zero;
            var counts = Vector<int>.Zero;
            var threshold = new Vector<float>(0.0001f);
            bool hasNaN = false;
            for (; offset <= samples.Length - Vector<float>.Count; offset += Vector<float>.Count)
            {
                var absolute = Vector.Abs(new Vector<float>(samples[offset..]));
                peaks = Vector.Max(peaks, absolute);
                counts -= Vector.GreaterThan(absolute, threshold);
                hasNaN |= !Vector.EqualsAll(absolute, absolute);
            }
            for (int lane = 0; lane < Vector<float>.Count; lane++)
            { peak = Math.Max(peak, peaks[lane]); nonSilent += counts[lane]; }
            if (hasNaN) peak = float.NaN;
        }
        foreach (float sample in samples[offset..])
        { float absolute = Math.Abs(sample); peak = Math.Max(peak, absolute); if (absolute > 0.0001f) nonSilent++; }
        return (nonSilent, peak);
    }

    internal void BeginPlaybackPriming()
    {
        lock (_gate)
        {
            _primingEnabled = true;
            if (_count >= _profile.PrimeBufferSamples)
            {
                _primed = true;
                _primeWaitStartMs = double.NaN;
            }
            else
            {
                _primed = false;
                _primeWaitStartMs = _count > 0 ? NowMs() : double.NaN;
            }
        }
    }

    internal void WriteMeasured(ReadOnlySpan<float> samples, (int NonSilent, float Peak) metrics)
    {
        var (nonSilent, peak) = metrics;
        Interlocked.Add(ref _written, samples.Length);
        // An encoded-packet mirror only needs to know whether this frame reached its committed output.
        if (_samples.Length == 0) return;
        Interlocked.Add(ref _nonSilent, nonSilent);
        Volatile.Write(ref _lastPeak, peak);
        if (!float.IsNaN(peak) && peak >= 0f)
        {
            float prev = Volatile.Read(ref _smartEnvelope);
            float alpha = peak > prev ? 0.35f : 0.04f;
            Volatile.Write(ref _smartEnvelope, prev + (peak - prev) * alpha);
        }
        float gain = Volatile.Read(ref _gain);
        float meterPeak = float.IsNaN(peak) || float.IsNaN(gain) || peak <= 0.0001f || gain <= 0f
            ? 0f : Math.Clamp(peak * gain, 0f, 1f);
        bool wake = false;
        lock (_gate)
        {
            double nowMs = NowMs();
            if (samples.Length > 0)
            {
                _hasReceivedAudio = true;
                UpdateArrivalJitterLocked(samples.Length, nowMs);
                int targetSamples = Math.Clamp(AudioLatencyProfile.MsToAlignedSamples(_targetBufferMs), Channels, _samples.Length);
                int highWatermarkSamples = Math.Clamp(
                    AudioLatencyProfile.MsToAlignedSamples(_profile.GetTrimHighWatermarkMs(_targetBufferMs)),
                    targetSamples,
                    _samples.Length);

                Span<float> fadeTail = stackalloc float[CrossfadeSamples];
                int fadeTailLen = 0;
                if (_count + samples.Length > highWatermarkSamples)
                {
                    int keepFromQueue = Math.Max(0, targetSamples - samples.Length) & ~1;
                    int dropFromQueue = Math.Max(0, _count - keepFromQueue) & ~1;
                    int dropFromIncoming = samples.Length > targetSamples ? ((samples.Length - targetSamples) & ~1) : 0;
                    int totalDrop = dropFromQueue + dropFromIncoming;
                    if (totalDrop > 0)
                    {
                        fadeTailLen = CaptureDroppedTailLocked(samples, dropFromQueue, dropFromIncoming, fadeTail);
                        if (dropFromQueue > 0)
                        {
                            _start = (_start + dropFromQueue) % _samples.Length;
                            _count -= dropFromQueue;
                        }
                        if (dropFromIncoming > 0) samples = samples[dropFromIncoming..];
                        Interlocked.Add(ref _dropped, totalDrop);
                        _stableSinceMs = nowMs;
                    }
                }
                if (samples.Length > _samples.Length)
                {
                    int excess = (samples.Length - _samples.Length) & ~1;
                    Interlocked.Add(ref _dropped, excess);
                    samples = samples[excess..];
                }
                int end = (_start + _count) % _samples.Length;
                int first = Math.Min(samples.Length, _samples.Length - end);
                samples[..first].CopyTo(_samples.AsSpan(end));
                samples[first..].CopyTo(_samples);
                _count += samples.Length;
                if (fadeTailLen >= Channels && _count >= Channels)
                    ApplySpliceCrossfadeLocked(fadeTail[..fadeTailLen]);

                if (_primingEnabled && !_primed)
                {
                    if (_count >= _profile.PrimeBufferSamples)
                    {
                        _primed = true;
                        _primeWaitStartMs = double.NaN;
                    }
                    else if (_count > 0 && double.IsNaN(_primeWaitStartMs))
                    {
                        _primeWaitStartMs = nowMs;
                    }
                }
            }
            long tickNow = Environment.TickCount64;
            if (meterPeak > _meterUnreadPeak) _meterUnreadPeak = meterPeak;
            _meterActivePeak = meterPeak;
            _lastWriteTick = tickNow;
            if (meterPeak > 0f)
            {
                _lastNonSilentWriteTick = tickNow;
                wake = Interlocked.Exchange(ref _meterWaiting, 0) != 0;
            }
        }
        if (wake && PeakActivity is { } listeners)
        {
            foreach (Action listener in listeners.GetInvocationList())
                try { listener(); } catch { }
        }
        DataAvailable?.Invoke();
    }

    public (bool Active, float Peak) ReadMeterPeak()
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            float peak;
            if (_meterUnreadPeak >= 0f && now - _lastWriteTick <= 150)
            {
                peak = _meterUnreadPeak;
                _meterUnreadPeak = -1f;
            }
            else if (_meterActivePeak > 0f && now - _lastNonSilentWriteTick <= 80)
            {
                peak = _meterActivePeak;
            }
            else
            {
                _meterUnreadPeak = -1f;
                peak = _meterActivePeak = 0f;
            }
            bool active = peak > 0f || now - _lastNonSilentWriteTick <= 80;
            Volatile.Write(ref _meterWaiting, active ? 0 : 1);
            return (active, peak);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (count % WaveFormat.BlockAlign != 0) throw new ArgumentException("Unaligned PCM read.");
        Span<float> destination = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
        float gain = ComputeEffectiveGain();
        int take = 0;
        lock (_gate)
        {
            double nowMs = NowMs();
            bool releasedByTimeout = false;
            if (_primingEnabled && !_primed)
            {
                if (_count >= _profile.PrimeBufferSamples)
                {
                    _primed = true;
                    _primeWaitStartMs = double.NaN;
                }
                else if (_count > 0)
                {
                    if (double.IsNaN(_primeWaitStartMs))
                        _primeWaitStartMs = nowMs;
                    else if (nowMs - _primeWaitStartMs >= _profile.PrimeMaxWaitMs)
                    {
                        _primed = true;
                        _primeWaitStartMs = double.NaN;
                        releasedByTimeout = true;
                    }
                }
            }

            if (!_primingEnabled || _primed)
            {
                take = Math.Min(destination.Length, _count);
                int first = Math.Min(take, _samples.Length - _start);
                ApplyGain(_samples.AsSpan(_start, first), destination, gain);
                ApplyGain(_samples.AsSpan(0, take - first), destination[first..], gain);
                if (take > 0) { _start = (_start + take) % _samples.Length; _count -= take; }
                if (_hasReceivedAudio && destination.Length > 0)
                {
                    if (take < destination.Length)
                    {
                        if (!releasedByTimeout) RecordUnderrunLocked(destination.Length - take, nowMs);
                        else _stableSinceMs = nowMs;
                    }
                    else
                    {
                        MaybeDecayTargetLocked(nowMs);
                    }
                }
            }
            destination[take..].Clear();
            Interlocked.Add(ref _read, take);
            Interlocked.Add(ref _underflow, destination.Length - take);
        }
        PlaybackBufferRequested?.Invoke();
        return count; // live source: an underrun is silence, not EOF
    }

    // A Windows Link relay sends the original 16-bit packet and has no need to copy its float playback queue.
    internal bool ConsumeRelayPacket(ref long lastWritten)
    {
        lock (_gate)
        {
            long written = SamplesWritten;
            if (written == lastWritten) return false;
            lastWritten = written;
            Interlocked.Add(ref _read, _count);
            _start = _count = 0;
            return true;
        }
    }

    private double NowMs() => _clockMs != null ? _clockMs() : Stopwatch.GetTimestamp() * TimestampToMs;

    private void ApplyLatencyProfileLocked(AudioLatencyProfile next)
    {
        _profile = next;
        Volatile.Write(ref _latencyMode, (int)next.Mode);
        _targetBufferMs = next.InitialTargetMs;
        ResetSessionAdaptationLocked();
        if (_primingEnabled && !_primed && _count >= next.PrimeBufferSamples)
        {
            _primed = true;
            _primeWaitStartMs = double.NaN;
        }
    }

    private void ResetSessionAdaptationLocked()
    {
        _estimatedJitterMs = 0;
        _lastArrivalMs = double.NaN;
        _lastPacketDurationMs = 0;
        _lastJitterAdjustMs = double.NaN;
        _lastUnderrunAdjustMs = double.NaN;
        _stableSinceMs = double.NaN;
        _lastDecayMs = double.NaN;
    }

    private double ComputeJitterTargetMs() =>
        Math.Clamp(_profile.PrimeMs + _estimatedJitterMs * 2.0, _profile.InitialTargetMs, _profile.MaxTargetMs);

    private void UpdateArrivalJitterLocked(int sampleCount, double nowMs)
    {
        double packetDurationMs = sampleCount * 1000.0 / (SampleRate * Channels);
        if (double.IsNaN(_lastArrivalMs))
        {
            _lastArrivalMs = nowMs;
            _lastPacketDurationMs = packetDurationMs;
            _stableSinceMs = nowMs;
            _lastJitterAdjustMs = nowMs;
            _lastDecayMs = nowMs;
            return;
        }

        double elapsedMs = Math.Max(0.0, nowMs - _lastArrivalMs);
        _lastArrivalMs = nowMs;
        double discontinuityThresholdMs = Math.Max(250.0, _lastPacketDurationMs * 6.0);
        if (elapsedMs <= discontinuityThresholdMs)
        {
            double instantJitterMs = Math.Min(200.0, Math.Abs(elapsedMs - _lastPacketDurationMs));
            double alpha = instantJitterMs > _estimatedJitterMs ? 0.125 : 0.05;
            _estimatedJitterMs += (instantJitterMs - _estimatedJitterMs) * alpha;
            double jitterTarget = ComputeJitterTargetMs();
            if (jitterTarget > _targetBufferMs)
            {
                _stableSinceMs = nowMs;
                if (double.IsNaN(_lastJitterAdjustMs) || nowMs - _lastJitterAdjustMs >= _profile.UnderrunCooldownMs)
                {
                    double step = Math.Min(_profile.MaxStepUpMs, jitterTarget - _targetBufferMs);
                    _targetBufferMs = Math.Min(_profile.MaxTargetMs, _targetBufferMs + step);
                    _lastJitterAdjustMs = nowMs;
                    _lastDecayMs = nowMs;
                }
            }
        }
        _lastPacketDurationMs = packetDurationMs;
    }

    private void RecordUnderrunLocked(int missingSamples, double nowMs)
    {
        _stableSinceMs = nowMs;
        if (!double.IsNaN(_lastUnderrunAdjustMs) && nowMs - _lastUnderrunAdjustMs < _profile.UnderrunCooldownMs)
            return;
        double missingMs = missingSamples * 1000.0 / (SampleRate * Channels);
        double step = Math.Clamp(missingMs * 0.5, _profile.MinStepUpMs, _profile.MaxStepUpMs);
        _targetBufferMs = Math.Min(_profile.AdaptiveCeilingMs, _targetBufferMs + step);
        _lastUnderrunAdjustMs = nowMs;
        _lastDecayMs = nowMs;
    }

    private void MaybeDecayTargetLocked(double nowMs)
    {
        if (double.IsNaN(_stableSinceMs))
        {
            _stableSinceMs = nowMs;
            _lastDecayMs = nowMs;
            return;
        }
        if (nowMs - _stableSinceMs < _profile.StabilityRecoveryDelayMs) return;
        if (!double.IsNaN(_lastDecayMs) && nowMs - _lastDecayMs < _profile.DecayIntervalMs) return;
        double floorMs = Math.Max(_profile.InitialTargetMs, ComputeJitterTargetMs());
        if (_targetBufferMs > floorMs)
        {
            _targetBufferMs = Math.Max(floorMs, _targetBufferMs - _profile.DecayStepMs);
            _lastDecayMs = nowMs;
        }
    }

    private int CaptureDroppedTailLocked(ReadOnlySpan<float> incoming, int dropFromQueue, int dropFromIncoming, Span<float> fadeTail)
    {
        int fromIncoming = Math.Min(CrossfadeSamples, dropFromIncoming) & ~1;
        int fromQueue = Math.Min(CrossfadeSamples - fromIncoming, dropFromQueue) & ~1;
        if (fromQueue > 0)
        {
            int index = (_start + dropFromQueue - fromQueue) % _samples.Length;
            int first = Math.Min(fromQueue, _samples.Length - index);
            _samples.AsSpan(index, first).CopyTo(fadeTail);
            _samples.AsSpan(0, fromQueue - first).CopyTo(fadeTail[first..]);
        }
        if (fromIncoming > 0)
            incoming.Slice(dropFromIncoming - fromIncoming, fromIncoming).CopyTo(fadeTail.Slice(fromQueue, fromIncoming));
        return fromQueue + fromIncoming;
    }

    private void ApplySpliceCrossfadeLocked(ReadOnlySpan<float> fadeTail)
    {
        int actualFade = Math.Min(fadeTail.Length, _count) & ~1;
        if (actualFade < Channels) return;
        int fadeFrames = actualFade / Channels;
        int tailOffset = fadeTail.Length - actualFade;
        for (int f = 0; f < fadeFrames; f++)
        {
            float t = (f + 1f) / (fadeFrames + 1f);
            float inv = 1f - t;
            int sampleOffset = f * Channels;
            int idx0 = (_start + sampleOffset) % _samples.Length;
            int idx1 = (idx0 + 1) % _samples.Length;
            _samples[idx0] = fadeTail[tailOffset + sampleOffset] * inv + _samples[idx0] * t;
            _samples[idx1] = fadeTail[tailOffset + sampleOffset + 1] * inv + _samples[idx1] * t;
        }
    }

    private float ComputeEffectiveGain()
    {
        float volume = Volume;
        float headerGain = HeaderGain;
        if (!SmartVolume) return volume * headerGain;
        if (volume <= 0f || headerGain <= 0f || float.IsNaN(headerGain)) return 0f;
        float curved = volume * (0.12f + 0.88f * volume);
        float env = Math.Max(Volatile.Read(ref _smartEnvelope), Volatile.Read(ref _lastPeak));
        float rangeFactor = float.IsNaN(env) || env <= 0.15f
            ? 0.25f
            : Math.Clamp(0.25f * MathF.Pow(0.15f / env, 0.70f), 0.08f, 0.25f);
        return curved * headerGain * (PreserveQuietLevel ? rangeFactor / 0.25f : rangeFactor);
    }

    private static void ApplyGain(ReadOnlySpan<float> samples, Span<float> destination, float gain)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var factor = new Vector<float>(gain);
            var lower = new Vector<float>(-1);
            var upper = new Vector<float>(1);
            for (; i <= samples.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var value = new Vector<float>(samples.Slice(i)) * factor;
                // Conditional comparisons preserve Math.Clamp's NaN behavior as well as clipping.
                value = Vector.ConditionalSelect(Vector.LessThan(value, lower), lower, value);
                value = Vector.ConditionalSelect(Vector.GreaterThan(value, upper), upper, value);
                value.CopyTo(destination[i..]);
            }
        }
        for (; i < samples.Length; i++) destination[i] = Math.Clamp(samples[i] * gain, -1, 1);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _start = _count = 0;
            _smartEnvelope = 0f;
            _meterUnreadPeak = -1f;
            _meterActivePeak = 0f;
            _lastWriteTick = _lastNonSilentWriteTick = -1000;
            Volatile.Write(ref _meterWaiting, 0);
            _hasReceivedAudio = false;
            _primed = false;
            _primeWaitStartMs = double.NaN;
            _targetBufferMs = _profile.InitialTargetMs;
            ResetSessionAdaptationLocked();
        }
        Mirrors?.ClearBuffers();
    }
}

public sealed partial class WasapiPcmPlayer
{
    public WasapiPcmPlayer(AndroidPcmSource source, string? endpointId) : this((IWasapiPlaybackSource)source, endpointId) { }
}

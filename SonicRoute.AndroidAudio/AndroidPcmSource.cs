using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;
using System.Numerics;

namespace SonicRoute.AndroidAudio;

/// <summary>One producer and one WASAPI consumer; bounded float PCM, 48 kHz stereo.</summary>
public sealed class AndroidPcmSource : IWaveProvider
{
    public const int SampleRate = 48000, Channels = 2;
    private readonly object _gate = new();
    private readonly float[] _samples = new float[SampleRate * Channels / 2]; // 500 ms maximum
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
    public static event Action? PeakActivity;
    // Only a phone renderer subscribes; silence still carries timing without polling.
    internal event Action? DataAvailable;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
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

    public void Write(ReadOnlySpan<float> samples)
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
        Interlocked.Add(ref _written, samples.Length); Interlocked.Add(ref _nonSilent, nonSilent);
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
            // Trim accumulated delay to 150 ms after a 250 ms burst; never grow the queue.
            if (_count + samples.Length > SampleRate * Channels / 4)
            {
                int keep = Math.Max(0, SampleRate * Channels * 150 / 1000 - samples.Length);
                int drop = Math.Max(0, _count - keep);
                _start = (_start + drop) % _samples.Length; _count -= drop;
                Interlocked.Add(ref _dropped, drop);
            }
            if (samples.Length > _samples.Length)
            {
                Interlocked.Add(ref _dropped, samples.Length - _samples.Length);
                samples = samples[^_samples.Length..];
            }
            int end = (_start + _count) % _samples.Length;
            int first = Math.Min(samples.Length, _samples.Length - end);
            samples[..first].CopyTo(_samples.AsSpan(end));
            samples[first..].CopyTo(_samples);
            long now = Environment.TickCount64;
            if (meterPeak > _meterUnreadPeak) _meterUnreadPeak = meterPeak;
            _meterActivePeak = meterPeak;
            _lastWriteTick = now;
            if (meterPeak > 0f)
            {
                _lastNonSilentWriteTick = now;
                wake = Interlocked.Exchange(ref _meterWaiting, 0) != 0;
            }
            _count += samples.Length;
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
        lock (_gate)
        {
            int take = Math.Min(destination.Length, _count);
            int first = Math.Min(take, _samples.Length - _start);
            ApplyGain(_samples.AsSpan(_start, first), destination, gain);
            ApplyGain(_samples.AsSpan(0, take - first), destination[first..], gain);
            destination[take..].Clear();
            _start = (_start + take) % _samples.Length; _count -= take;
            Interlocked.Add(ref _read, take);
            Interlocked.Add(ref _underflow, destination.Length - take);
        }
        return count; // live source: an underrun is silence, not EOF
    }

    private float ComputeEffectiveGain()
    {
        float volume = Volume;
        float headerGain = HeaderGain;
        if (!SmartVolume) return volume * headerGain;
        if (volume <= 0f || headerGain <= 0f || float.IsNaN(headerGain)) return 0f;
        float curved = volume * (0.5f + 0.5f * volume);
        float env = Math.Max(Volatile.Read(ref _smartEnvelope), Volatile.Read(ref _lastPeak));
        float rangeFactor = float.IsNaN(env) || env <= 0.0005f
            ? 0.35f
            : Math.Clamp(0.35f / MathF.Pow(Math.Max(env, 0.02f), 0.70f), 0.20f, 1.40f);
        return curved * headerGain * rangeFactor;
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
        }
    }
}

public sealed class WasapiPcmPlayer : IPcmPlayer
{
    private readonly WasapiDeviceLease _device;
    private readonly WasapiOut _output;
    public event Action<Exception>? Failed;

    public WasapiPcmPlayer(AndroidPcmSource source, string? endpointId)
    {
        _device = WasapiDeviceLease.Open(endpointId);
        try
        {
            _output = new WasapiOut(_device.Device, AudioClientShareMode.Shared, true, 100);
            _output.Init(source); // shared-mode conversion adapts to the endpoint's mix format
            _output.PlaybackStopped += (_, e) => { if (e.Exception != null) Failed?.Invoke(e.Exception); };
        }
        catch { _output?.Dispose(); _device.Dispose(); throw; }
    }
    public void Play() => _output.Play();
    public void Stop() => _output.Stop();
    public void Dispose() { _output.Dispose(); _device.Dispose(); }
}

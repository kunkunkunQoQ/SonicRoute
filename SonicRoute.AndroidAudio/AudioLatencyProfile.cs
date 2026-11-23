namespace SonicRoute.AndroidAudio;

/// <summary>Selectable audio buffering and WASAPI shared-mode latency profiles.</summary>
public enum AudioLatencyMode
{
    Balanced = 0,
    UltraLow = 1,
    Stable = 2
}

/// <summary>Mode-specific buffering targets, trimming watermarks, and WASAPI latency negotiation rules.</summary>
public sealed class AudioLatencyProfile
{
    private readonly int[] _fallbackLatenciesMs;

    private AudioLatencyProfile(
        AudioLatencyMode mode,
        double minTargetMs,
        double initialTargetMs,
        double maxTargetMs,
        double adaptiveCeilingMs,
        double primeMs,
        double trimMarginMs,
        double minTrimHighWatermarkMs,
        int wasapiPeriodMultiplier,
        int minWasapiLatencyMs,
        int maxWasapiLatencyMs,
        int[] fallbackLatenciesMs,
        double underrunCooldownMs,
        double minStepUpMs,
        double maxStepUpMs,
        double decayStepMs)
    {
        Mode = mode;
        MinTargetMs = minTargetMs;
        InitialTargetMs = initialTargetMs;
        MaxTargetMs = maxTargetMs;
        AdaptiveCeilingMs = adaptiveCeilingMs;
        PrimeMs = primeMs;
        PrimeBufferSamples = MsToAlignedSamples(primeMs);
        TrimMarginMs = trimMarginMs;
        MinTrimHighWatermarkMs = minTrimHighWatermarkMs;
        WasapiPeriodMultiplier = wasapiPeriodMultiplier;
        MinWasapiLatencyMs = minWasapiLatencyMs;
        MaxWasapiLatencyMs = maxWasapiLatencyMs;
        _fallbackLatenciesMs = fallbackLatenciesMs;
        UnderrunCooldownMs = underrunCooldownMs;
        MinStepUpMs = minStepUpMs;
        MaxStepUpMs = maxStepUpMs;
        DecayStepMs = decayStepMs;
    }

    public static AudioLatencyProfile Balanced { get; } = new(
        mode: AudioLatencyMode.Balanced,
        minTargetMs: 40,
        initialTargetMs: 56,
        maxTargetMs: 80,
        adaptiveCeilingMs: 95,
        primeMs: 40,
        trimMarginMs: 48,
        minTrimHighWatermarkMs: 110,
        wasapiPeriodMultiplier: 4,
        minWasapiLatencyMs: 35,
        maxWasapiLatencyMs: 50,
        fallbackLatenciesMs: [100],
        underrunCooldownMs: 450,
        minStepUpMs: 3,
        maxStepUpMs: 6,
        decayStepMs: 1.0);

    public static AudioLatencyProfile UltraLow { get; } = new(
        mode: AudioLatencyMode.UltraLow,
        minTargetMs: 20,
        initialTargetMs: 28,
        maxTargetMs: 40,
        adaptiveCeilingMs: 50,
        primeMs: 20,
        trimMarginMs: 25,
        minTrimHighWatermarkMs: 0,
        wasapiPeriodMultiplier: 2,
        minWasapiLatencyMs: 16,
        maxWasapiLatencyMs: 25,
        fallbackLatenciesMs: [40, 100],
        underrunCooldownMs: 450,
        minStepUpMs: 2,
        maxStepUpMs: 4,
        decayStepMs: 0.5);

    public static AudioLatencyProfile Stable { get; } = new(
        mode: AudioLatencyMode.Stable,
        minTargetMs: 80,
        initialTargetMs: 110,
        maxTargetMs: 150,
        adaptiveCeilingMs: 170,
        primeMs: 80,
        trimMarginMs: 65,
        minTrimHighWatermarkMs: 0,
        wasapiPeriodMultiplier: 6,
        minWasapiLatencyMs: 60,
        maxWasapiLatencyMs: 80,
        fallbackLatenciesMs: [100],
        underrunCooldownMs: 450,
        minStepUpMs: 5,
        maxStepUpMs: 10,
        decayStepMs: 1.5);

    public AudioLatencyMode Mode { get; }
    public double MinTargetMs { get; }
    public double InitialTargetMs { get; }
    public double MaxTargetMs { get; }
    public double AdaptiveCeilingMs { get; }
    public double PrimeMs { get; }
    public int PrimeBufferSamples { get; }
    public double TrimMarginMs { get; }
    public double MinTrimHighWatermarkMs { get; }
    public int WasapiPeriodMultiplier { get; }
    public int MinWasapiLatencyMs { get; }
    public int MaxWasapiLatencyMs { get; }
    public double UnderrunCooldownMs { get; }
    public double MinStepUpMs { get; }
    public double MaxStepUpMs { get; }
    public double DecayStepMs { get; }
    public double StabilityRecoveryDelayMs => 2500.0;
    public double DecayIntervalMs => 500.0;
    public double PrimeMaxWaitMs => 120.0;

    public static AudioLatencyProfile For(AudioLatencyMode mode) => mode switch
    {
        AudioLatencyMode.Balanced => Balanced,
        AudioLatencyMode.UltraLow => UltraLow,
        AudioLatencyMode.Stable => Stable,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported audio latency mode.")
    };

    /// <summary>Returns the burst backlog watermark above which queued delay is trimmed back to <paramref name="targetBufferMs"/>.</summary>
    public double GetTrimHighWatermarkMs(double targetBufferMs) =>
        Math.Max(targetBufferMs + TrimMarginMs, MinTrimHighWatermarkMs);

    /// <summary>Computes the preferred WASAPI shared-mode latency in milliseconds for a device period.</summary>
    public int ComputeWasapiLatencyMs(double devicePeriodMs)
    {
        double effectivePeriod = double.IsFinite(devicePeriodMs) && devicePeriodMs > 0 ? devicePeriodMs : 10.0;
        int raw = (int)Math.Ceiling(effectivePeriod * WasapiPeriodMultiplier);
        return Math.Clamp(raw, MinWasapiLatencyMs, MaxWasapiLatencyMs);
    }

    /// <summary>Returns ordered WASAPI shared-mode latency candidates including safe fallbacks.</summary>
    public int[] GetWasapiLatencyCandidates(double devicePeriodMs)
    {
        int primary = ComputeWasapiLatencyMs(devicePeriodMs);
        var candidates = new List<int>(_fallbackLatenciesMs.Length + 1) { primary };
        foreach (int fallback in _fallbackLatenciesMs)
        {
            if (fallback > candidates[^1])
                candidates.Add(fallback);
        }
        return candidates.ToArray();
    }

    internal static int MsToAlignedSamples(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds <= 0) return 0;
        int samples = (int)Math.Round(milliseconds * AndroidPcmSource.SampleRate * AndroidPcmSource.Channels / 1000.0);
        return Math.Max(0, samples) & ~1;
    }
}

using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task AudioLatencyChecksAsync(string output)
    {
        Directory.CreateDirectory(output);
        int startChecks = _checks;

        CheckLatencyProfilesAndWasapiFormulas();
        CheckModeWatermarksAndBurstTrimming();
        CheckJitterAndUnderrunAdaptationAndDecay();
        CheckStereoAlignedCrossfadeTrim();
        await CheckPlaybackPrimingAndTimeoutAsync();
        await CheckGainsAndMirrorsAcrossModesAsync();
        var wasapiDiag = CheckRealWasapiLatencyNegotiation();

        await File.WriteAllTextAsync(Path.Combine(output, "audio-latency-checks.json"), JsonSerializer.Serialize(new
        {
            Success = true,
            Assertions = _checks - startChecks,
            TotalAssertions = _checks,
            Wasapi = wasapiDiag
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks - startChecks} audio latency assertions.");
    }

    private static void CheckLatencyProfilesAndWasapiFormulas()
    {
        var defaultPcm = new AndroidPcmSource();
        Check(defaultPcm.LatencyMode == AudioLatencyMode.Balanced
            && Math.Abs(defaultPcm.TargetBufferMs - 56.0) < 0.001,
            "default AndroidPcmSource starts in Balanced mode at 56ms target");

        var ultra = AudioLatencyProfile.For(AudioLatencyMode.UltraLow);
        var balanced = AudioLatencyProfile.For(AudioLatencyMode.Balanced);
        var stable = AudioLatencyProfile.For(AudioLatencyMode.Stable);

        Check(ultra.MinTargetMs == 20 && ultra.InitialTargetMs == 28 && ultra.MaxTargetMs == 40
            && ultra.AdaptiveCeilingMs == 50 && ultra.PrimeMs == 20
            && Math.Abs(ultra.GetTrimHighWatermarkMs(ultra.InitialTargetMs) - 53.0) < 0.001,
            "UltraLow profile matches 20-40ms target, 28ms initial, 20ms prime, target+25ms trim watermark");
        Check(balanced.MinTargetMs == 40 && balanced.InitialTargetMs == 56 && balanced.MaxTargetMs == 80
            && balanced.AdaptiveCeilingMs == 95 && balanced.PrimeMs == 40
            && Math.Abs(balanced.GetTrimHighWatermarkMs(balanced.InitialTargetMs) - 110.0) < 0.001
            && Math.Abs(balanced.GetTrimHighWatermarkMs(70.0) - 118.0) < 0.001,
            "Balanced profile matches 40-80ms target, 56ms initial, 40ms prime, max(target+48, 110)ms watermark");
        Check(stable.MinTargetMs == 80 && stable.InitialTargetMs == 110 && stable.MaxTargetMs == 150
            && stable.AdaptiveCeilingMs == 170 && stable.PrimeMs == 80
            && Math.Abs(stable.GetTrimHighWatermarkMs(stable.InitialTargetMs) - 175.0) < 0.001,
            "Stable profile matches 80-150ms target, 110ms initial, 80ms prime, target+65ms trim watermark");

        Check(ultra.GetWasapiLatencyCandidates(10.0).SequenceEqual([20, 40, 100])
            && ultra.GetWasapiLatencyCandidates(3.0).SequenceEqual([16, 40, 100])
            && ultra.GetWasapiLatencyCandidates(20.0).SequenceEqual([25, 40, 100]),
            "UltraLow WASAPI latency clamps period*2 to [16, 25]ms with 40ms and 100ms fallbacks");
        Check(balanced.GetWasapiLatencyCandidates(10.0).SequenceEqual([40, 100])
            && balanced.GetWasapiLatencyCandidates(3.0).SequenceEqual([35, 100])
            && balanced.GetWasapiLatencyCandidates(20.0).SequenceEqual([50, 100]),
            "Balanced WASAPI latency clamps period*4 to [35, 50]ms with 100ms fallback");
        Check(stable.GetWasapiLatencyCandidates(10.0).SequenceEqual([60, 100])
            && stable.GetWasapiLatencyCandidates(3.0).SequenceEqual([60, 100])
            && stable.GetWasapiLatencyCandidates(20.0).SequenceEqual([80, 100]),
            "Stable WASAPI latency clamps period*6 to [60, 80]ms with 100ms fallback");

        bool rejectedInvalid = false;
        try { _ = new AndroidPcmSource((AudioLatencyMode)99); } catch (ArgumentOutOfRangeException) { rejectedInvalid = true; }
        Check(rejectedInvalid, "undefined AudioLatencyMode is rejected cleanly");
    }

    private static void CheckModeWatermarksAndBurstTrimming()
    {
        float[] frame20ms = new float[1920];
        Array.Fill(frame20ms, 0.1f);

        // Balanced allows up to 110ms without drops (preserving DecoderSettings 93.5ms & AndroidReceiveMirrors 60ms/40ms).
        var balanced = new AndroidPcmSource(AudioLatencyMode.Balanced);
        for (int i = 0; i < 5; i++) balanced.Write(frame20ms); // 100ms <= 110ms
        Check(balanced.BufferedSamples == 9600 && balanced.DroppedSamples == 0
            && Math.Abs(balanced.BufferedDurationMs - 100.0) < 0.001,
            "Balanced retains 100ms burst without dropping below 110ms high watermark");
        balanced.Write(frame20ms); // 120ms > 110ms -> trims back to 56ms (5376 samples)
        Check(balanced.BufferedSamples == 5376 && balanced.DroppedSamples == 6144
            && Math.Abs(balanced.BufferedDurationMs - 56.0) < 0.001,
            "Balanced trims burst above 110ms back to 56ms target instead of 500ms ring limit");

        // Program.cs line 126 compatibility: 24000 + 24000 samples.
        var compat = new AndroidPcmSource();
        compat.Write(new float[24000]);
        compat.Write(new float[24000]);
        Check(compat.BufferedSamples <= 24000 && compat.DroppedSamples > 0,
            "Balanced satisfies 24000+24000 bounded queue invariant with low target latency");

        // UltraLow keeps <= 53ms, trims > 53ms back to 28ms (2688 samples).
        var ultra = new AndroidPcmSource(AudioLatencyMode.UltraLow);
        ultra.Write(frame20ms);
        ultra.Write(frame20ms); // 40ms <= 53ms
        Check(ultra.BufferedSamples == 3840 && ultra.DroppedSamples == 0,
            "UltraLow retains 40ms within its 53ms watermark");
        ultra.Write(frame20ms); // 60ms > 53ms -> trims back to 28ms (2688 samples)
        Check(ultra.BufferedSamples == 2688 && ultra.DroppedSamples == 3072
            && Math.Abs(ultra.BufferedDurationMs - 28.0) < 0.001,
            "UltraLow trims 60ms burst back to 28ms target");

        // Stable keeps <= 175ms, trims > 175ms back to 110ms (10560 samples).
        var stable = new AndroidPcmSource(AudioLatencyMode.Stable);
        for (int i = 0; i < 8; i++) stable.Write(frame20ms); // 160ms <= 175ms
        Check(stable.BufferedSamples == 15360 && stable.DroppedSamples == 0,
            "Stable retains 160ms within its 175ms watermark");
        stable.Write(frame20ms); // 180ms > 175ms -> trims back to 110ms (10560 samples)
        Check(stable.BufferedSamples == 10560 && stable.DroppedSamples == 6720
            && Math.Abs(stable.BufferedDurationMs - 110.0) < 0.001,
            "Stable trims 180ms burst back to 110ms target");
    }

    private static void CheckJitterAndUnderrunAdaptationAndDecay()
    {
        double nowMs = 1000.0;
        var pcm = new AndroidPcmSource(AudioLatencyMode.UltraLow, () => nowMs);
        byte[] read20ms = new byte[1920 * sizeof(float)];
        float[] frame20ms = new float[1920];
        Array.Fill(frame20ms, 0.2f);

        // Pre-audio reads must not count as active-stream underruns.
        for (int i = 0; i < 5; i++)
        {
            nowMs += 500;
            pcm.Read(read20ms, 0, read20ms.Length);
        }
        Check(Math.Abs(pcm.TargetBufferMs - 28.0) < 0.001 && pcm.EstimatedJitterMs == 0,
            "pre-stream silence reads never inflate target buffer or jitter");

        // Trigger active underrun #1 then rapid underruns within cooldown (< 400ms).
        nowMs += 20;
        pcm.Write(frame20ms); // 20ms received
        nowMs += 10;
        pcm.Read(read20ms, 0, read20ms.Length); // consumes 20ms cleanly
        nowMs += 20;
        pcm.Read(read20ms, 0, read20ms.Length); // underrun #1 (20ms missing -> +4ms => 32ms)
        double afterFirstUnderrun = pcm.TargetBufferMs;
        for (int i = 0; i < 15; i++)
        {
            nowMs += 20; // 300ms total < 450ms cooldown
            pcm.Read(read20ms, 0, read20ms.Length);
        }
        Check(afterFirstUnderrun > 28.0 && afterFirstUnderrun <= 32.0 && pcm.TargetBufferMs == afterFirstUnderrun,
            "underrun step-up is bounded and cooldown blocks oscillation during rapid underruns");

        // Spaced underruns climb smoothly and stay capped at AdaptiveCeilingMs (50ms).
        for (int i = 0; i < 12; i++)
        {
            nowMs += 500;
            pcm.Read(read20ms, 0, read20ms.Length);
        }
        Check(Math.Abs(pcm.TargetBufferMs - 50.0) < 0.001,
            "repeated spaced underruns clamp smoothly at UltraLow adaptive ceiling (50ms)");

        // Sustained clean playback must hold for >= 2500ms before slowly recovering towards 28ms.
        pcm.Clear();
        Check(Math.Abs(pcm.TargetBufferMs - 28.0) < 0.001 && pcm.EstimatedJitterMs == 0,
            "Clear resets target buffer and session jitter diagnostics");

        // Jittery 5ms / 35ms arrivals raise EstimatedJitterMs and smoothly lift TargetBufferMs inside [28, 40].
        nowMs += 20;
        pcm.Write(frame20ms);
        for (int i = 0; i < 40; i++)
        {
            nowMs += (i & 1) == 0 ? 5.0 : 35.0;
            pcm.Write(frame20ms);
            pcm.Read(read20ms, 0, read20ms.Length);
        }
        Check(pcm.EstimatedJitterMs > 8.0 && pcm.TargetBufferMs > 28.0 && pcm.TargetBufferMs <= 40.0,
            "alternating arrival jitter increases EWMA jitter and raises target smoothly within mode band");

        // Long pause gap (> 250ms) does not spike EstimatedJitterMs.
        double jitterBeforePause = pcm.EstimatedJitterMs;
        nowMs += 2000.0;
        pcm.Write(frame20ms);
        Check(pcm.EstimatedJitterMs <= jitterBeforePause + 0.001,
            "long idle pause gap does not falsely spike jitter estimate");

        // Fresh session: push target up via two spaced underruns, then verify >= 2500ms stability hold & gradual decay.
        pcm.Clear();
        nowMs += 20; pcm.Write(frame20ms);
        pcm.Read(read20ms, 0, read20ms.Length);
        nowMs += 500; pcm.Read(read20ms, 0, read20ms.Length);
        nowMs += 500; pcm.Read(read20ms, 0, read20ms.Length);
        double elevated = pcm.TargetBufferMs;
        Check(elevated > 28.0 && elevated <= 36.0,
            "two spaced underruns smoothly raise UltraLow target before stability window");

        // Pre-fill 40ms so steady reads never underrun, then run 100 clean cycles (2000ms < 2500ms).
        nowMs += 20; pcm.Write(frame20ms);
        nowMs += 20; pcm.Write(frame20ms);
        for (int i = 0; i < 100; i++)
        {
            nowMs += 20;
            pcm.Write(frame20ms);
            pcm.Read(read20ms, 0, read20ms.Length);
        }
        Check(pcm.TargetBufferMs == elevated,
            "target buffer does not prematurely decay before 2500ms stability window");

        // Continue steady playback for another 15 seconds to verify gradual decay back to 28ms.
        for (int i = 0; i < 750; i++)
        {
            nowMs += 20;
            pcm.Write(frame20ms);
            pcm.Read(read20ms, 0, read20ms.Length);
        }
        Check(Math.Abs(pcm.TargetBufferMs - 28.0) < 0.001,
            "sustained stable stream >= 2500ms gradually recovers back to initial target");
    }

    private static void CheckStereoAlignedCrossfadeTrim()
    {
        var pcm = new AndroidPcmSource(AudioLatencyMode.UltraLow);

        // Partial-queue trim: Phase A (40ms, L=+0.70f, R=+0.15f) + Phase B (20ms, L=-0.70f, R=-0.15f) -> 60ms > 53ms -> trims to 28ms (2688 samples).
        float[] phaseA = new float[1920];
        for (int i = 0; i < 1920; i += 2) { phaseA[i] = 0.70f; phaseA[i + 1] = 0.15f; }
        pcm.Write(phaseA);
        pcm.Write(phaseA);

        float[] phaseB = new float[1920];
        for (int i = 0; i < 1920; i += 2) { phaseB[i] = -0.70f; phaseB[i + 1] = -0.15f; }
        pcm.Write(phaseB);

        Check(pcm.DroppedSamples % 2 == 0 && pcm.BufferedSamples % 2 == 0 && pcm.BufferedSamples == 2688,
            "trimming drops and preserves even stereo-aligned sample counts");

        byte[] partialBytes = new byte[pcm.BufferedSamples * sizeof(float)];
        pcm.Read(partialBytes, 0, partialBytes.Length);
        var partialSamples = MemoryMarshal.Cast<byte, float>(partialBytes);
        // Kept 768 samples (384 frames) of Phase A followed by 1920 samples (960 frames) of Phase B; L and R stay unswapped.
        Check(Math.Abs(partialSamples[0] - 0.70f) < 0.0001f && Math.Abs(partialSamples[1] - 0.15f) < 0.0001f
            && Math.Abs(partialSamples[768] - (-0.70f)) < 0.0001f && Math.Abs(partialSamples[769] - (-0.15f)) < 0.0001f,
            "partial-queue trim preserves left/right stereo channel alignment");

        // Exact splice across dropped tail -> kept head: 30ms Phase A + 28ms Phase B = 58ms > 53ms.
        // Drops all 2880 samples of Phase A, retains 2688 samples of Phase B, and crossfades the first 48 frames (1ms).
        var splicePcm = new AndroidPcmSource(AudioLatencyMode.UltraLow);
        float[] phaseA30 = new float[2880];
        for (int i = 0; i < 2880; i += 2) { phaseA30[i] = 0.70f; phaseA30[i + 1] = 0.15f; }
        float[] phaseB28 = new float[2688];
        for (int i = 0; i < 2688; i += 2) { phaseB28[i] = -0.70f; phaseB28[i + 1] = -0.15f; }
        splicePcm.Write(phaseA30);
        splicePcm.Write(phaseB28);

        Check(splicePcm.DroppedSamples == 2880 && splicePcm.BufferedSamples == 2688,
            "exact splice drops Phase A (2880 samples) and retains Phase B (2688 samples)");

        byte[] outBytes = new byte[splicePcm.BufferedSamples * sizeof(float)];
        splicePcm.Read(outBytes, 0, outBytes.Length);
        var samples = MemoryMarshal.Cast<byte, float>(outBytes);

        float maxStepL = 0f, maxStepR = 0f;
        for (int f = 1; f < 64; f++)
        {
            maxStepL = Math.Max(maxStepL, Math.Abs(samples[f * 2] - samples[(f - 1) * 2]));
            maxStepR = Math.Max(maxStepR, Math.Abs(samples[f * 2 + 1] - samples[(f - 1) * 2 + 1]));
        }
        // Raw step would be 1.40f (L) and 0.30f (R). With 48-frame linear crossfade, max step is ~1.40/49 = 0.0286f.
        bool channelsIntact = true;
        for (int f = 48; f < samples.Length / 2; f++)
        {
            if (Math.Abs(samples[f * 2] - (-0.70f)) > 0.0001f || Math.Abs(samples[f * 2 + 1] - (-0.15f)) > 0.0001f)
            { channelsIntact = false; break; }
        }
        Check(samples[0] > 0.60f && maxStepL < 0.05f && maxStepR < 0.02f && channelsIntact,
            "48-frame crossfade eliminates splice step click and preserves left/right channel separation");
    }

    private static async Task CheckPlaybackPrimingAndTimeoutAsync()
    {
        float[] frame20ms = new float[1920];
        Array.Fill(frame20ms, 0.25f);
        byte[] readBuf = new byte[1920 * sizeof(float)];

        // Test Balanced priming (40ms = 3840 samples) and Clear() re-priming.
        var balanced = new AndroidPcmSource(AudioLatencyMode.Balanced);
        balanced.BeginPlaybackPriming();
        balanced.Write(frame20ms); // 20ms < 40ms
        balanced.Read(readBuf, 0, readBuf.Length);
        Check(MemoryMarshal.Cast<byte, float>(readBuf).ToArray().All(s => s == 0f) && balanced.BufferedSamples == 1920,
            "Balanced priming holds playback while buffered audio is below 40ms");

        balanced.Write(frame20ms); // 40ms >= 40ms
        balanced.Read(readBuf, 0, readBuf.Length);
        Check(Math.Abs(MemoryMarshal.Cast<byte, float>(readBuf)[0] - 0.25f) < 0.0001f && balanced.BufferedSamples == 1920,
            "Balanced priming releases immediately once 40ms accumulates");

        balanced.Clear();
        balanced.Write(frame20ms); // 20ms after Clear must prime again
        balanced.Read(readBuf, 0, readBuf.Length);
        Check(MemoryMarshal.Cast<byte, float>(readBuf).ToArray().All(s => s == 0f) && balanced.BufferedSamples == 1920,
            "Clear resets priming state when playback priming is enabled");

        // Test real-clock 120ms safety timeout so short chimes are never swallowed forever.
        var stable = new AndroidPcmSource(AudioLatencyMode.Stable); // PrimeMs = 80ms
        stable.BeginPlaybackPriming();
        stable.Write(frame20ms); // Only 20ms arrives (e.g. short notification chime)
        stable.Read(readBuf, 0, readBuf.Length);
        Check(MemoryMarshal.Cast<byte, float>(readBuf).ToArray().All(s => s == 0f) && stable.BufferedSamples == 1920,
            "Stable priming holds initial 20ms short chime before timeout");

        await Task.Delay(145);
        stable.Read(readBuf, 0, readBuf.Length);
        Check(Math.Abs(MemoryMarshal.Cast<byte, float>(readBuf)[0] - 0.25f) < 0.0001f && stable.BufferedSamples == 0,
            "priming protection timeout (~120ms) releases short notification chime safely");
    }

    private static async Task CheckGainsAndMirrorsAcrossModesAsync()
    {
        foreach (var mode in new[] { AudioLatencyMode.UltraLow, AudioLatencyMode.Balanced, AudioLatencyMode.Stable })
        {
            var pcm = new AndroidPcmSource(mode) { Volume = 0.25f, HeaderGain = 1.6f, SmartVolume = false };
            float[] loud = new float[1920];
            Array.Fill(loud, 0.5f);
            pcm.Write(loud);
            byte[] buf = new byte[1920 * sizeof(float)];
            pcm.Read(buf, 0, buf.Length);
            float expectedLinear = 0.5f * 0.25f * 1.6f; // 0.20f
            var meter = pcm.ReadMeterPeak();
            Check(Math.Abs(MemoryMarshal.Cast<byte, float>(buf)[0] - expectedLinear) < 0.0001f
                && meter.Active && Math.Abs(meter.Peak - 0.8f) < 0.001f,
                $"{mode}: linear Volume/HeaderGain and ReadMeterPeak work identically");

            pcm.SmartVolume = true;
            pcm.Volume = 0.1f;
            pcm.HeaderGain = 1f;
            Array.Fill(loud, 0.9f);
            pcm.Write(loud);
            Check(pcm.EffectiveGain < 0.025f, $"{mode}: SmartVolume attenuates full-scale signal at low volume");
        }

        // Verify PcmOutputMirrors synchronizes LatencyMode across new and existing mirror queues.
        var mirrors = new PcmOutputMirrors(_ => Task.CompletedTask);
        mirrors.SetLatencyMode(AudioLatencyMode.UltraLow);
        mirrors.SetLevels(0.4f, true);
        var created = new Dictionary<string, AndroidPcmSource>();
        await mirrors.ApplyAsync(["m1", "m2"], (pcm, id, _) =>
        {
            created[id] = pcm;
            return new LatencyTestPlayer();
        }, () => Task.CompletedTask, CancellationToken.None);

        Check(created["m1"].LatencyMode == AudioLatencyMode.UltraLow
            && created["m2"].LatencyMode == AudioLatencyMode.UltraLow
            && Math.Abs(created["m1"].TargetBufferMs - 28.0) < 0.001
            && Math.Abs(created["m2"].Volume - 0.4f) < 0.001
            && created["m2"].SmartVolume,
            "PcmOutputMirrors creates new mirror queues with the configured LatencyMode and audio levels");

        mirrors.SetLatencyMode(AudioLatencyMode.Stable);
        Check(created["m1"].LatencyMode == AudioLatencyMode.Stable
            && created["m2"].LatencyMode == AudioLatencyMode.Stable
            && Math.Abs(created["m1"].TargetBufferMs - 110.0) < 0.001,
            "PcmOutputMirrors updates LatencyMode on all existing mirror queues");

        var primary = new AndroidPcmSource(AudioLatencyMode.Stable) { Mirrors = mirrors, HeaderGain = 1.25f };
        primary.LatencyMode = AudioLatencyMode.Balanced;
        Check(created["m1"].LatencyMode == AudioLatencyMode.Balanced
            && created["m2"].LatencyMode == AudioLatencyMode.Balanced,
            "changing primary AndroidPcmSource.LatencyMode propagates to mirrors");

        float[] tone = new float[1920];
        Array.Fill(tone, 0.3f);
        primary.Write(tone);
        Check(created["m1"].BufferedSamples == 1920 && created["m2"].BufferedSamples == 1920
            && Math.Abs(created["m1"].HeaderGain - 1.25f) < 0.0001f,
            "primary Write forwards measured PCM and HeaderGain to mirrors cleanly");

        mirrors.Clear();
    }

    private static object CheckRealWasapiLatencyNegotiation()
    {
        bool hasDevice = false;
        try
        {
            using var enumerator = new WasapiDeviceLease.Enumerator();
            using var defaultLease = enumerator.TryDefault(Role.Multimedia);
            hasDevice = defaultLease != null;
        }
        catch (COMException)
        {
            hasDevice = false;
        }

        if (!hasDevice)
        {
            Console.WriteLine("SKIP real WASAPI latency negotiation (no active default multimedia render endpoint)");
            return new { WasapiDeviceAvailable = false, Skipped = true };
        }

        var negotiated = new Dictionary<string, object>();
        foreach (var mode in new[] { AudioLatencyMode.UltraLow, AudioLatencyMode.Balanced, AudioLatencyMode.Stable })
        {
            var pcm = new AndroidPcmSource(mode);
            using var player = new WasapiPcmPlayer(pcm, null);
            var profile = AudioLatencyProfile.For(mode);
            int[] validCandidates = profile.GetWasapiLatencyCandidates(player.DeviceDefaultPeriodMs);
            Check(player.DeviceDefaultPeriodMs > 0 && validCandidates.Contains(player.ActualRequestedLatencyMs),
                $"WASAPI negotiates valid shared latency for {mode} (period={player.DeviceDefaultPeriodMs:F2}ms, requested={player.ActualRequestedLatencyMs}ms)");

            // Verify WasapiPcmPlayer constructor enabled playback priming on pcm without emitting sound.
            byte[] silentRead = new byte[1920 * sizeof(float)];
            pcm.Write(new float[960]); // 10ms < PrimeMs (20/40/80ms)
            pcm.Read(silentRead, 0, silentRead.Length);
            Check(pcm.BufferedSamples == 960,
                $"WasapiPcmPlayer enables playback priming on {mode} source");

            negotiated[mode.ToString()] = new
            {
                player.DeviceDefaultPeriodMs,
                player.ActualRequestedLatencyMs,
                ValidCandidates = validCandidates
            };
        }

        return new { WasapiDeviceAvailable = true, Skipped = false, Modes = negotiated };
    }

    private sealed class LatencyTestPlayer : IPcmPlayer
    {
        public void Play() { }
        public void Stop() { }
        public void Dispose() { }
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Concentus;
using Concentus.Enums;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task RawPcmChecksAsync(string output)
    {
        if (!string.IsNullOrWhiteSpace(output))
            Directory.CreateDirectory(output);

        Check(ScrcpyAudioProtocol.CodecOpus == 0x6f707573
            && ScrcpyAudioProtocol.CodecRaw == 0x72617720
            && ScrcpyAudioProtocol.RawPcmDecoderId == "raw-pcm",
            "scrcpy audio protocol constants match wire fourCCs and raw-pcm identifier");
        Check(ScrcpyAudioProtocol.IsRawPcmDecoder("raw-pcm")
            && !ScrcpyAudioProtocol.IsRawPcmDecoder(null)
            && !ScrcpyAudioProtocol.IsRawPcmDecoder(AndroidStreamSettings.ConcentusDecoder)
            && !ScrcpyAudioProtocol.IsRawPcmDecoder("RAW-PCM"),
            "IsRawPcmDecoder matches exact raw-pcm identifier");

        await CheckProtocolDispatchAsync();
        CheckPcm16LeToFloatAccuracyAndSimd();
        await CheckRealRawPcmStreamAndFragmentedReadAsync();
        await CheckNoOpusDecoderAndLowAllocationAsync();
        await CheckRawPcmValidationAndRejectionsAsync();
        await CheckRawPcmRebaseAndVolumeControlsAsync();
        await CheckRawPcmTransportCommandsAsync(output);
        var perf = await BenchmarkRawPcmVsOpusAsync();

        if (!string.IsNullOrWhiteSpace(output))
        {
            await File.WriteAllTextAsync(Path.Combine(output, "raw-pcm-summary.json"), JsonSerializer.Serialize(new
            {
                Success = true,
                Assertions = _checks,
                Vector.IsHardwareAccelerated,
                VectorShortLanes = Vector<short>.Count,
                Benchmark4sStereo = perf,
                RealPhoneAccess = false,
                RealAudioPlayback = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        Console.WriteLine($"PASS {_checks} raw PCM assertions; no phone access or speaker playback.");
    }

    private static async Task CheckProtocolDispatchAsync()
    {
        byte[] rawHeader = Encoding.ASCII.GetBytes("raw ");
        uint rawCodec = await ScrcpyAudioProtocol.ReadCodecIdAsync(new FragmentedStream(rawHeader), default);
        Check(rawCodec == ScrcpyAudioProtocol.CodecRaw, "ReadCodecIdAsync decodes big-endian 'raw ' codec id");

        foreach (string? pref in new[] { null, ScrcpyAudioProtocol.RawPcmDecoderId, AndroidStreamSettings.ConcentusDecoder })
        {
            IAndroidAudioReceiver rx = ScrcpyAudioProtocol.CreateReceiver(rawCodec, pref);
            Check(rx is ScrcpyRawPcmReceiver && rx.DecoderId == ScrcpyAudioProtocol.RawPcmDecoderId && rx.CodecId == ScrcpyAudioProtocol.CodecRaw,
                $"CreateReceiver selects ScrcpyRawPcmReceiver for raw stream (preferred={pref ?? "auto"})");
        }

        byte[] opusHeader = Encoding.ASCII.GetBytes("opus");
        uint opusCodec = await ScrcpyAudioProtocol.ReadCodecIdAsync(new FragmentedStream(opusHeader), default);
        Check(opusCodec == ScrcpyAudioProtocol.CodecOpus, "ReadCodecIdAsync decodes big-endian 'opus' codec id");

        foreach (string? pref in new[] { null, AndroidStreamSettings.ConcentusDecoder, ScrcpyAudioProtocol.RawPcmDecoderId })
        {
            IAndroidAudioReceiver rx = ScrcpyAudioProtocol.CreateReceiver(opusCodec, pref);
            Check(rx is ScrcpyOpusReceiver && rx.DecoderId == AndroidStreamSettings.ConcentusDecoder && rx.CodecId == ScrcpyAudioProtocol.CodecOpus,
                $"CreateReceiver selects ScrcpyOpusReceiver for opus stream (preferred={pref ?? "auto"})");
        }

        foreach (uint disabledMarker in new[] { 0u, 1u })
        {
            byte[] markerBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(markerBytes, disabledMarker);
            InvalidDataException? ex = null;
            try { await ScrcpyAudioProtocol.ReadCodecIdAsync(new MemoryStream(markerBytes), default); }
            catch (InvalidDataException caught) { ex = caught; }
            Check(ex != null && AndroidError.TryGetCode(ex, out var code) && code == AndroidErrorCode.AudioCaptureNotEstablished,
                $"ReadCodecIdAsync maps disabled marker {disabledMarker} to AudioCaptureNotEstablished");
        }

        foreach (uint unknownCodec in new[] { 0x61616320u, 0x666c6163u, 0xDEADBEEFu })
        {
            bool rejected = false;
            try { _ = ScrcpyAudioProtocol.CreateReceiver(unknownCodec); }
            catch (InvalidDataException ex) { rejected = ex.Message.Contains($"0x{unknownCodec:X8}", StringComparison.Ordinal); }
            Check(rejected, $"CreateReceiver rejects unknown codec 0x{unknownCodec:X8}");
        }

        foreach (uint codec in new[] { ScrcpyAudioProtocol.CodecOpus, ScrcpyAudioProtocol.CodecRaw })
        {
            bool rejected = false;
            try { _ = ScrcpyAudioProtocol.CreateReceiver(codec, "unsupported-decoder"); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, $"CreateReceiver rejects unknown preferred decoder for 0x{codec:X8}");
        }

        bool ctorRejected = false;
        try { _ = new ScrcpyRawPcmReceiver("unsupported-decoder"); }
        catch (ArgumentException) { ctorRejected = true; }
        Check(ctorRejected, "ScrcpyRawPcmReceiver constructor rejects unsupported decoder id");
    }

    private static void CheckPcm16LeToFloatAccuracyAndSimd()
    {
        short[] boundaries = [short.MinValue, -1, 0, 1, short.MaxValue];
        byte[] boundaryBytes = new byte[boundaries.Length * sizeof(short)];
        for (int i = 0; i < boundaries.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(boundaryBytes.AsSpan(i * 2, 2), boundaries[i]);

        float[] boundaryFloats = new float[boundaries.Length];
        ScrcpyRawPcmReceiver.ConvertPcm16LeToFloat(boundaryBytes, boundaryFloats);
        Check(boundaryFloats[0] == -1.0f
            && boundaryFloats[1] == -1f / 32768f
            && boundaryFloats[2] == 0.0f
            && boundaryFloats[3] == 1f / 32768f
            && boundaryFloats[4] == 32767f / 32768f
            && boundaryFloats[4] < 1.0f,
            "S16LE boundary values map exactly into [-1.0f, 1.0f)");

        var rng = new Random(20261123);
        int[] sampleCounts = [0, 1, 2, 3, 4, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 960, 1024, 1920, 2048, 4096];
        foreach (int count in sampleCounts)
        {
            byte[] src = new byte[count * sizeof(short)];
            for (int i = 0; i < count; i++)
            {
                short value = i switch
                {
                    0 when count >= 3 => short.MinValue,
                    1 when count >= 3 => 0,
                    2 when count >= 3 => short.MaxValue,
                    _ => (short)rng.Next(short.MinValue, short.MaxValue + 1)
                };
                BinaryPrimitives.WriteInt16LittleEndian(src.AsSpan(i * 2, 2), value);
            }

            float[] actual = Enumerable.Repeat(float.NaN, count + 8).ToArray();
            float[] expected = new float[count];
            ScrcpyRawPcmReceiver.ConvertPcm16LeToFloat(src, actual.AsSpan(0, count));
            ScalarConvertPcm16LeToFloat(src, expected);

            bool bitExact = true;
            bool inRange = true;
            for (int i = 0; i < count; i++)
            {
                bitExact &= BitConverter.SingleToInt32Bits(actual[i]) == BitConverter.SingleToInt32Bits(expected[i]);
                inRange &= float.IsFinite(actual[i]) && actual[i] >= -1.0f && actual[i] < 1.0f;
            }
            bool tailUntouched = true;
            for (int i = count; i < actual.Length; i++)
                tailUntouched &= float.IsNaN(actual[i]);

            Check(bitExact && inRange && tailUntouched,
                $"ConvertPcm16LeToFloat SIMD matches scalar bit-for-bit ({count} samples)");
        }

        bool oddRejected = false;
        try { ScrcpyRawPcmReceiver.ConvertPcm16LeToFloat(new byte[3], new float[2]); }
        catch (ArgumentException) { oddRejected = true; }
        bool smallDstRejected = false;
        try { ScrcpyRawPcmReceiver.ConvertPcm16LeToFloat(new byte[8], new float[3]); }
        catch (ArgumentException) { smallDstRejected = true; }
        Check(oddRejected && smallDstRejected, "ConvertPcm16LeToFloat validates even byte count and destination capacity");
    }

    private static async Task CheckRealRawPcmStreamAndFragmentedReadAsync()
    {
        var (wire, expectedSamples, lastPts) = BuildRawPcmWire(
            includeEmptyConfig: true,
            includeLargePacket: true,
            includeKeyFrameFlagOnLast: true);

        byte[] wireStreamBytes = wire;
        await using var fragmented = new FragmentedStream(wireStreamBytes);
        uint codecId = await ScrcpyAudioProtocol.ReadCodecIdAsync(fragmented, default);
        IAndroidAudioReceiver receiver = ScrcpyAudioProtocol.CreateReceiver(codecId, ScrcpyAudioProtocol.RawPcmDecoderId);
        var pcm = new AndroidPcmSource { SmartVolume = false, HeaderGain = 2.5f };
        int firstFrames = 0;
        try { await receiver.ReceivePacketsAsync(fragmented, pcm, () => firstFrames++, default); }
        catch (EndOfStreamException) { }

        Check(receiver is ScrcpyRawPcmReceiver && firstFrames == 1 && receiver.PacketsDecoded == 5
            && receiver.LastTimestampUs == lastPts && receiver.TimestampDiscontinuities == 0,
            "fragmented scrcpy raw PCM stream dispatches and decodes all 5 packets including >4KiB packet and empty config");
        Check(pcm.HeaderGain == 1f && pcm.SamplesWritten == expectedSamples.Length && pcm.NonSilentSamplesWritten > 0,
            "raw PCM stream resets HeaderGain to 1.0 and writes non-silent stereo samples");

        byte[] actualBytes = new byte[expectedSamples.Length * sizeof(float)];
        pcm.Read(actualBytes, 0, actualBytes.Length);
        ReadOnlySpan<float> actualFloats = MemoryMarshal.Cast<byte, float>(actualBytes);

        bool samplesExact = true;
        for (int i = 0; i < expectedSamples.Length; i++)
            samplesExact &= BitConverter.SingleToInt32Bits(actualFloats[i]) == BitConverter.SingleToInt32Bits(expectedSamples[i]);
        Check(samplesExact
            && actualFloats[0] == -1.0f && actualFloats[1] == 32767f / 32768f
            && actualFloats[2] == 0.0f && actualFloats[3] == -1f / 32768f,
            "raw PCM preserves stereo L/R channels, boundary amplitudes, and exact S16LE conversion through FragmentedStream");

        // Direct ReceiveAsync entry point and cross-codec rejection
        var direct = new ScrcpyRawPcmReceiver();
        var directPcm = new AndroidPcmSource { SmartVolume = false };
        int directFirst = 0;
        try { await direct.ReceiveAsync(new MemoryStream(wireStreamBytes), directPcm, () => directFirst++, default); }
        catch (EndOfStreamException) { }
        Check(directFirst == 1 && direct.PacketsDecoded == 5 && directPcm.SamplesWritten == expectedSamples.Length,
            "ScrcpyRawPcmReceiver.ReceiveAsync verifies 'raw ' header and decodes full stream");

        bool wrongCodecRejected = false;
        try { await new ScrcpyRawPcmReceiver().ReceiveAsync(new MemoryStream(BuildWire()), new(), () => { }, default); }
        catch (InvalidDataException) { wrongCodecRejected = true; }
        Check(wrongCodecRejected, "ScrcpyRawPcmReceiver.ReceiveAsync rejects non-raw Opus stream");
    }

    private static async Task CheckNoOpusDecoderAndLowAllocationAsync()
    {
        Type receiverType = typeof(ScrcpyRawPcmReceiver);
        const BindingFlags allMembers = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        bool usesConcentusField = receiverType.GetFields(allMembers)
            .Any(f => f.FieldType.Assembly == typeof(IOpusDecoder).Assembly);
        bool usesConcentusLocal = receiverType.GetMethods(allMembers)
            .Select(m => m.GetMethodBody())
            .Where(b => b != null)
            .SelectMany(b => b!.LocalVariables)
            .Any(v => v.LocalType.Assembly == typeof(IOpusDecoder).Assembly);
        Check(!usesConcentusField && !usesConcentusLocal,
            "ScrcpyRawPcmReceiver carries zero Concentus/OpusDecoder fields or local variables");

        // Steady-state allocation check: 200 packets x 4096 B = 800 KiB raw PCM over MemoryStream
        using var steadyWire = new MemoryStream();
        steadyWire.Write(Encoding.ASCII.GetBytes("raw "));
        byte[] packet = new byte[4096];
        for (int i = 0; i < 1024; i++)
        {
            short v = (short)(Math.Sin(i * 0.05) * 16000);
            BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(i * 4, 2), v);
            BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(i * 4 + 2, 2), v);
        }
        for (int p = 0; p < 200; p++)
            WritePacket(steadyWire, (ulong)(p * 21333), packet);
        byte[] steadyBytes = steadyWire.ToArray();

        // Warm JIT + ArrayPool
        var warm = new ScrcpyRawPcmReceiver();
        try { await warm.ReceiveAsync(new MemoryStream(steadyBytes), new(), () => { }, default); }
        catch (EndOfStreamException) { }

        var steadyRx = new ScrcpyRawPcmReceiver();
        var steadyPcm = new AndroidPcmSource { SmartVolume = false };
        using var steadyStream = new MemoryStream(steadyBytes, writable: false);
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        try { await steadyRx.ReceiveAsync(steadyStream, steadyPcm, () => { }, default); }
        catch (EndOfStreamException) { }
        long steadyAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        Check(steadyRx.PacketsDecoded == 200 && steadyAlloc < 4096,
            $"raw PCM steady-state decode allocates no per-packet buffers ({steadyAlloc} B across 200 x 4KiB packets)");
    }

    private static async Task CheckRawPcmValidationAndRejectionsAsync()
    {
        foreach (int badSize in new[] { 1, 2, 3, 5, 4095 })
        {
            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("raw "));
            WritePacket(ms, 0, new byte[badSize]);
            ms.Position = 0;
            var pcm = new AndroidPcmSource();
            InvalidDataException? ex = null;
            try { await new ScrcpyRawPcmReceiver().ReceiveAsync(ms, pcm, () => { }, default); }
            catch (InvalidDataException caught) { ex = caught; }
            Check(ex != null && ex.Message.Contains("Unaligned", StringComparison.Ordinal) && pcm.SamplesWritten == 0,
                $"rejects unaligned raw PCM packet size={badSize} before writing audio");
        }

        // Zero-length non-config packet
        using (var zeroMs = new MemoryStream())
        {
            zeroMs.Write(Encoding.ASCII.GetBytes("raw "));
            WritePacket(zeroMs, 0, []);
            zeroMs.Position = 0;
            bool failed = false;
            try { await new ScrcpyRawPcmReceiver().ReceiveAsync(zeroMs, new(), () => { }, default); }
            catch (InvalidDataException) { failed = true; }
            Check(failed, "rejects zero-length non-config raw PCM packet");
        }

        // Oversized packet > 262144
        using (var bigMs = new MemoryStream())
        {
            bigMs.Write(Encoding.ASCII.GetBytes("raw "));
            byte[] header = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(header, 1000);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 262145);
            bigMs.Write(header);
            bigMs.Position = 0;
            bool failed = false;
            try { await new ScrcpyRawPcmReceiver().ReceiveAsync(bigMs, new(), () => { }, default); }
            catch (InvalidDataException) { failed = true; }
            Check(failed, "rejects oversized raw PCM packet > 262144 bytes");
        }

        // Non-empty unknown config packet (must not interpret metadata/OpusHead as PCM)
        foreach (byte[] badConfig in new[] { Encoding.ASCII.GetBytes("OpusHead\x01\x02\x00\x00"), Encoding.ASCII.GetBytes("METADATA") })
        {
            using var cfgMs = new MemoryStream();
            cfgMs.Write(Encoding.ASCII.GetBytes("raw "));
            WritePacket(cfgMs, 1UL << 63, badConfig);
            cfgMs.Position = 0;
            var pcm = new AndroidPcmSource();
            bool failed = false;
            try { await new ScrcpyRawPcmReceiver().ReceiveAsync(cfgMs, pcm, () => { }, default); }
            catch (InvalidDataException) { failed = true; }
            Check(failed && pcm.SamplesWritten == 0,
                $"rejects non-empty config packet ({badConfig.Length} bytes) without feeding samples to PCM");
        }
    }

    private static async Task CheckRawPcmRebaseAndVolumeControlsAsync()
    {
        // Timestamp rebase test: 3 packets increasing, then PTS drops -> pcm.Clear() + TimestampDiscontinuities++
        using var rebaseMs = new MemoryStream();
        rebaseMs.Write(Encoding.ASCII.GetBytes("raw "));
        long[] ptsSeries = [0, 21333, 42666, 5000, 26333];
        byte[] frameBytes = new byte[960 * 2 * sizeof(short)]; // 20ms per packet
        for (int p = 0; p < ptsSeries.Length; p++)
        {
            short val = (short)((p + 1) * 2000);
            for (int i = 0; i < 960 * 2; i++)
                BinaryPrimitives.WriteInt16LittleEndian(frameBytes.AsSpan(i * 2, 2), val);
            WritePacket(rebaseMs, (ulong)ptsSeries[p], frameBytes);
        }

        var rx = new ScrcpyRawPcmReceiver();
        var pcm = new AndroidPcmSource { SmartVolume = false };
        try { await rx.ReceiveAsync(new MemoryStream(rebaseMs.ToArray()), pcm, () => { }, default); }
        catch (EndOfStreamException) { }
        Check(rx.PacketsDecoded == 5 && rx.TimestampDiscontinuities == 1 && rx.LastTimestampUs == 26333
            && pcm.BufferedSamples == 2 * 960 * 2 && pcm.SamplesWritten == 5 * 960 * 2,
            "raw PCM timestamp rebase clears stale queued audio and keeps receiving");

        // Volume / mute verification on AndroidPcmSource
        byte[] readBuf = new byte[64 * sizeof(float)];
        pcm.Volume = 0.5f;
        pcm.Read(readBuf, 0, readBuf.Length);
        float expectedRebased = (4 * 2000) * (1f / 32768f) * 0.5f;
        Check(MemoryMarshal.Cast<byte, float>(readBuf).ToArray().All(s => MathF.Abs(s - expectedRebased) < 1e-6f),
            "AndroidPcmSource applies Volume scaling cleanly to raw PCM");

        pcm.Volume = 0f;
        pcm.Read(readBuf, 0, readBuf.Length);
        Check(MemoryMarshal.Cast<byte, float>(readBuf).ToArray().All(s => s == 0f),
            "AndroidPcmSource mute produces exact zero PCM for raw PCM");

        // SmartVolume verification on loud raw PCM sine stream
        var (loudWire, _, _) = BuildRawPcmWire(includeEmptyConfig: false, includeLargePacket: false, includeKeyFrameFlagOnLast: false, amplitude: 0.9f);
        var smartPcm = new AndroidPcmSource { SmartVolume = true, Volume = 1f };
        var smartRx = new ScrcpyRawPcmReceiver();
        try { await smartRx.ReceiveAsync(new MemoryStream(loudWire), smartPcm, () => { }, default); }
        catch (EndOfStreamException) { }
        float gain = smartPcm.EffectiveGain;
        byte[] smartOut = new byte[Math.Min(smartPcm.BufferedSamples, 1920) * sizeof(float)];
        smartPcm.Read(smartOut, 0, smartOut.Length);
        float[] smartFloats = MemoryMarshal.Cast<byte, float>(smartOut).ToArray();
        Check(gain is > 0f and < 1f && smartFloats.Any(s => MathF.Abs(s) > 0.001f) && smartFloats.All(s => s is >= -1f and <= 1f),
            "AndroidPcmSource SmartVolume compresses loud raw PCM without clipping");
    }

    private static async Task CheckRawPcmTransportCommandsAsync(string output)
    {
        var app = new AndroidApp("com.example.music", "Music", 10042, "media");
        var withEncoder = new AndroidStreamSettings("c2.android.opus.encoder", 192000, AudioDecoder: ScrcpyAudioProtocol.RawPcmDecoderId);

        string rawCmd = ScrcpyAdbTransport.BuildServerShellCommand("/data/local/tmp/sonicroute.jar", "abcd1234", app, false, withEncoder);
        Check(rawCmd.Contains("audio_codec=raw ", StringComparison.Ordinal)
            && !rawCmd.Contains("audio_codec=opus", StringComparison.Ordinal)
            && !rawCmd.Contains("audio_bit_rate=", StringComparison.Ordinal)
            && !rawCmd.Contains("audio_encoder=", StringComparison.Ordinal)
            && rawCmd.Contains("audio_source=playback audio_dup=false audio_target_packages=com.example.music audio_mix_mode=uid", StringComparison.Ordinal),
            "ScrcpyAdbTransport emits audio_codec=raw without audio_bit_rate or audio_encoder when raw-pcm is selected");

        foreach (string? opusDecoder in new[] { null, AndroidStreamSettings.ConcentusDecoder })
        {
            var opusSettings = (withEncoder with { AudioDecoder = opusDecoder }).Normalize();
            string opusCmd = ScrcpyAdbTransport.BuildServerShellCommand("/data/local/tmp/sonicroute.jar", "abcd1234", app, true, opusSettings);
            Check(opusCmd.Contains("audio_codec=opus ", StringComparison.Ordinal)
                && opusCmd.Contains("audio_bit_rate=192000 ", StringComparison.Ordinal)
                && opusCmd.Contains("audio_encoder=c2.android.opus.encoder ", StringComparison.Ordinal)
                && !opusCmd.Contains("audio_codec=raw", StringComparison.Ordinal),
                $"ScrcpyAdbTransport preserves exact opus command options ({opusDecoder ?? "auto"})");
        }

        bool cleanTempRuntime = string.IsNullOrWhiteSpace(output);
        string tempRuntime = cleanTempRuntime
            ? Path.Combine(Path.GetTempPath(), "raw-pcm-adb-fixture-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(output, "raw-pcm-adb-fixture");
        Directory.CreateDirectory(tempRuntime);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRuntime, "sonicroute-catalog.jar"), "test-jar");
            var transport = new RawPcmTransportFixture();
            await using var adb = new AdbClient(tempRuntime, transport);
            var phone = new AndroidDevice("raw-phone", "RawPhone", "device", false);
            await using var scrcpy = new ScrcpyAdbTransport(adb, streaming: _ => new AndroidStreamSettings(AudioBitRate: 160000, AudioDecoder: AndroidStreamSettings.ConcentusDecoder));
            using Stream stream = await scrcpy.ConnectAsync(phone, app, keepPhoneAudio: false, default);
            Check(transport.OpenedShellCommands.Count == 1
                && transport.OpenedShellCommands[0].Contains("audio_codec=opus", StringComparison.Ordinal)
                && transport.OpenedShellCommands[0].Contains("audio_bit_rate=160000", StringComparison.Ordinal),
                "ScrcpyAdbTransport.ConnectAsync passes normalized stream command and completes dummy handshake");
        }
        finally
        {
            if (cleanTempRuntime)
                try { Directory.Delete(tempRuntime, recursive: true); } catch { }
        }
    }

    private static async Task<object> BenchmarkRawPcmVsOpusAsync()
    {
        const int frames = 200; // 4.0 seconds @ 48 kHz stereo (20 ms / frame)
        const int samplesPerFrame = 960 * 2;
        using var rawStream = new MemoryStream();
        rawStream.Write(Encoding.ASCII.GetBytes("raw "));

        using var opusStream = new MemoryStream();
        opusStream.Write(Encoding.ASCII.GetBytes("opus"));
        byte[] opusHead = new byte[19];
        Encoding.ASCII.GetBytes("OpusHead").CopyTo(opusHead, 0);
        opusHead[8] = 1;
        opusHead[9] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(opusHead.AsSpan(10), 312);
        BinaryPrimitives.WriteInt32LittleEndian(opusHead.AsSpan(12), 48000);
        WritePacket(opusStream, 1UL << 63, opusHead);

        using IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
        float[] floatFrame = new float[samplesPerFrame];
        byte[] rawFrame = new byte[samplesPerFrame * sizeof(short)];
        byte[] opusEncoded = new byte[4000];

        for (int f = 0; f < frames; f++)
        {
            for (int s = 0; s < 960; s++)
            {
                double t = (f * 960 + s) / 48000.0;
                float l = (float)(0.35 * Math.Sin(2 * Math.PI * 440.0 * t));
                float r = (float)(0.35 * Math.Sin(2 * Math.PI * 660.0 * t));
                short sl = (short)Math.Clamp((int)Math.Round(l * 32767.0), short.MinValue, short.MaxValue);
                short sr = (short)Math.Clamp((int)Math.Round(r * 32767.0), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(rawFrame.AsSpan(s * 4, 2), sl);
                BinaryPrimitives.WriteInt16LittleEndian(rawFrame.AsSpan(s * 4 + 2, 2), sr);
                floatFrame[s * 2] = sl * (1f / 32768f);
                floatFrame[s * 2 + 1] = sr * (1f / 32768f);
            }
            ulong pts = (ulong)(f * 20000);
            WritePacket(rawStream, pts, rawFrame);
            int encLen = encoder.Encode(floatFrame, 960, opusEncoded, opusEncoded.Length);
            WritePacket(opusStream, pts, opusEncoded[..encLen]);
        }

        byte[] rawWire = rawStream.ToArray();
        byte[] opusWire = opusStream.ToArray();

        async Task<(double Ms, long Bytes, long Packets)> RunOnceAsync(Func<IAndroidAudioReceiver> factory, byte[] wire)
        {
            IAndroidAudioReceiver rx = factory();
            var pcm = new AndroidPcmSource { SmartVolume = false };
            using var ms = new MemoryStream(wire, writable: false);
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            try { await rx.ReceiveAsync(ms, pcm, static () => { }, default); }
            catch (EndOfStreamException) { }
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            return (elapsedMs, allocated, rx.PacketsDecoded);
        }

        // Warm JIT
        await RunOnceAsync(() => new ScrcpyRawPcmReceiver(), rawWire);
        await RunOnceAsync(() => new ScrcpyOpusReceiver(), opusWire);

        var rawTrials = new (double Ms, long Bytes)[5];
        var opusTrials = new (double Ms, long Bytes)[5];
        for (int i = 0; i < 5; i++)
        {
            if ((i & 1) == 0)
            {
                var r = await RunOnceAsync(() => new ScrcpyRawPcmReceiver(), rawWire);
                var o = await RunOnceAsync(() => new ScrcpyOpusReceiver(), opusWire);
                Check(r.Packets == frames && o.Packets == frames, $"benchmark decoded {frames} packets in trial {i}");
                rawTrials[i] = (r.Ms, r.Bytes);
                opusTrials[i] = (o.Ms, o.Bytes);
            }
            else
            {
                var o = await RunOnceAsync(() => new ScrcpyOpusReceiver(), opusWire);
                var r = await RunOnceAsync(() => new ScrcpyRawPcmReceiver(), rawWire);
                Check(r.Packets == frames && o.Packets == frames, $"benchmark decoded {frames} packets in trial {i}");
                rawTrials[i] = (r.Ms, r.Bytes);
                opusTrials[i] = (o.Ms, o.Bytes);
            }
        }

        double rawMedianMs = rawTrials.Select(t => t.Ms).Order().ElementAt(2);
        long rawMedianBytes = rawTrials.Select(t => t.Bytes).Order().ElementAt(2);
        double opusMedianMs = opusTrials.Select(t => t.Ms).Order().ElementAt(2);
        long opusMedianBytes = opusTrials.Select(t => t.Bytes).Order().ElementAt(2);

        Console.WriteLine($"METRIC raw-pcm-vs-opus-4s: opus {opusMedianMs:F3} ms / {opusMedianBytes} B; raw-pcm {rawMedianMs:F3} ms / {rawMedianBytes} B ({frames} frames, median of 5).");
        Check(rawMedianMs < opusMedianMs && rawMedianBytes < opusMedianBytes,
            "raw PCM decode path is faster and allocates less than Concentus Opus decode");

        return new
        {
            Frames = frames,
            OpusMedianMs = opusMedianMs,
            OpusMedianAllocatedBytes = opusMedianBytes,
            RawPcmMedianMs = rawMedianMs,
            RawPcmMedianAllocatedBytes = rawMedianBytes
        };
    }

    private static (byte[] Wire, float[] ExpectedSamples, long LastTimestampUs) BuildRawPcmWire(
        bool includeEmptyConfig,
        bool includeLargePacket,
        bool includeKeyFrameFlagOnLast,
        float amplitude = 0.4f)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("raw "));
        if (includeEmptyConfig)
            WritePacket(ms, 1UL << 63, []);

        int[] frameCounts = includeLargePacket ? [1024, 480, 480, 480, 2048] : [960, 960, 960, 960, 960];
        var expected = new List<float>(frameCounts.Sum() * 2);
        long lastPts = 0;
        int totalFramesSoFar = 0;

        for (int p = 0; p < frameCounts.Length; p++)
        {
            int frames = frameCounts[p];
            byte[] payload = new byte[frames * 4];
            for (int f = 0; f < frames; f++)
            {
                short left, right;
                if (p == 0 && f == 0)
                {
                    left = short.MinValue;
                    right = short.MaxValue;
                }
                else if (p == 0 && f == 1)
                {
                    left = 0;
                    right = -1;
                }
                else
                {
                    double t = (totalFramesSoFar + f) / 48000.0;
                    left = (short)Math.Clamp((int)Math.Round(amplitude * Math.Sin(2 * Math.PI * 440.0 * t) * 32767.0), short.MinValue, short.MaxValue);
                    right = (short)Math.Clamp((int)Math.Round(amplitude * Math.Sin(2 * Math.PI * 880.0 * t) * 32767.0), short.MinValue, short.MaxValue);
                }
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(f * 4, 2), left);
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(f * 4 + 2, 2), right);
                expected.Add(left * (1f / 32768f));
                expected.Add(right * (1f / 32768f));
            }

            long pts = p * 21333L;
            ulong flags = (ulong)pts;
            if (includeKeyFrameFlagOnLast && p == frameCounts.Length - 1)
                flags |= 1UL << 62;
            WritePacket(ms, flags, payload);
            lastPts = pts;
            totalFramesSoFar += frames;
        }

        return (ms.ToArray(), expected.ToArray(), lastPts);
    }

    private static void ScalarConvertPcm16LeToFloat(ReadOnlySpan<byte> sourceBytes, Span<float> destinationFloats)
    {
        const float scale = 1f / 32768f;
        int count = sourceBytes.Length >> 1;
        for (int i = 0; i < count; i++)
            destinationFloats[i] = BinaryPrimitives.ReadInt16LittleEndian(sourceBytes.Slice(i * 2, 2)) * scale;
    }

    private sealed class RawPcmTransportFixture : PairingFixtureTransport, IAdbTransport
    {
        private readonly Dictionary<string, long> _jars = new(StringComparer.Ordinal);
        internal readonly List<string> OpenedShellCommands = [];

        Task IAdbTransport.PushAsync(string serial, string local, string remote, CancellationToken token)
        {
            _jars[serial + "\0" + remote] = new FileInfo(local).Length;
            return Task.CompletedTask;
        }

        Task<AdbShellResult> IAdbTransport.RunShellAsync(string serial, string command, CancellationToken token)
        {
            if (command.StartsWith("ls -l ", StringComparison.Ordinal))
            {
                string path = command["ls -l ".Length..].Split(' ', 2)[0];
                return Task.FromResult(_jars.TryGetValue(serial + "\0" + path, out long len)
                    ? new AdbShellResult($"-rw-r--r-- 1 shell shell {len} 2026-01-01 00:00 {path}\n", 0)
                    : new AdbShellResult("MISSING\n", 0));
            }
            if (command.StartsWith("chmod 0644 ", StringComparison.Ordinal))
            {
                int mv = command.IndexOf(" && mv -f ", StringComparison.Ordinal);
                if (mv > 0)
                {
                    string tmp = command["chmod 0644 ".Length..mv].Trim();
                    string[] mvArgs = command[(mv + " && mv -f ".Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (mvArgs.Length >= 2 && _jars.Remove(serial + "\0" + tmp, out long len))
                        _jars[serial + "\0" + mvArgs[1]] = len;
                }
                return Task.FromResult(new AdbShellResult("", 0));
            }
            if (command == "pm list packages -U")
                return Task.FromResult(new AdbShellResult("package:com.example.music uid:10042\n", 0));
            if (command == "getprop ro.build.version.sdk")
                return Task.FromResult(new AdbShellResult("36\n", 0));
            return Task.FromResult(new AdbShellResult("", 0));
        }

        Task<IAdbShellSession> IAdbTransport.OpenShellAsync(string serial, string command, bool redirectInput, CancellationToken token)
        {
            OpenedShellCommands.Add(command);
            return Task.FromResult<IAdbShellSession>(new CompletedAudioShell());
        }

        Task<Stream> IAdbTransport.OpenLocalAbstractAsync(string serial, string name, CancellationToken token) =>
            Task.FromResult<Stream>(new MemoryStream([0x00]));

        private sealed class CompletedAudioShell : IAdbShellSession
        {
            public Stream OutputStream => new MemoryStream(Encoding.UTF8.GetBytes("SR_PID:7777\n"));
            public Stream ErrorStream => Stream.Null;
            public Stream InputStream => Stream.Null;
            public bool HasExited => false;
            public int? ExitCode => null;
            public Task<int> WaitForExitAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task CloseInputAsync(CancellationToken token = default) => Task.CompletedTask;
            public void Kill() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

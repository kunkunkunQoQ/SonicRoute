using System.Reflection;
using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task DecoderSettingsAsync(string output)
    {
        Directory.CreateDirectory(output);
        var phone = new AndroidDevice("decoder-usb", "Fixture", "device", false, "decoder-phone");
        var wifi = phone with { Serial = "127.0.0.1:5555", IsWifi = true };
        var defaults = new AndroidStreamSettings();
        Check(JsonSerializer.Deserialize<AndroidStreamSettings>("{\"AudioBitRate\":128000}")!.Normalize() == defaults,
            "older phone settings keep automatic decoding and unchanged defaults");
        Check((defaults with { AudioDecoder = "invalid", VideoDecoder = "bad;command" }).Normalize() == defaults,
            "unsupported persisted decoders fall back to automatic without shell arguments");
        var selected = defaults with { AudioDecoder = AndroidStreamSettings.ConcentusDecoder, VideoDecoder = AndroidStreamSettings.SoftwareVideoDecoder };
        string path = Path.Combine(output, "decoder-settings.json");
        var settings = new AndroidAudioSettingsStore(path);
        settings.RememberStreaming(phone, selected); await settings.FlushAsync();
        var restored = new AndroidAudioSettingsStore(path);
        Check(restored.GetStreaming(wifi) == selected && restored.GetStreaming(phone with { HardwareId = "other" }) == defaults,
            "audio and video decoders persist per physical phone across USB and Wi-Fi");
        Check(selected.AudioOptions() == defaults.AudioOptions(), "PC decoder choices never alter the Android capture command");

        var outputs = new List<byte[]>();
        foreach (string? mode in new[] { null, AndroidStreamSettings.ConcentusDecoder })
        {
            var receiver = new ScrcpyOpusReceiver(mode);
            var pcm = new AndroidPcmSource { SmartVolume = false };
            try { await receiver.ReceiveAsync(new MemoryStream(BuildWire()), pcm, () => { }, default); }
            catch (EndOfStreamException) { }
            byte[] samples = new byte[pcm.BufferedSamples * sizeof(float)]; pcm.Read(samples, 0, samples.Length); outputs.Add(samples);
            Check(receiver.DecoderId == AndroidStreamSettings.ConcentusDecoder && receiver.PacketsDecoded > 0 && pcm.NonSilentSamplesWritten > 0,
                $"{mode ?? "automatic"} decodes real Opus packets into non-silent managed PCM");
        }
        Check(outputs[0].SequenceEqual(outputs[1]), "explicit Concentus selection is sample-identical to automatic decoding");
        bool rejected = false;
        try { _ = new ScrcpyOpusReceiver("not-supported"); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "unsupported direct decoder requests fail before acquiring resources");

        await using (var session = new AndroidAudioSession(() => new FakeTransport(BuildWire(), () => { }), (_, _) => new FakePlayer(),
            streaming: _ => selected))
        {
            await session.StartAsync(phone, AndroidApp.DeviceAudio);
            var receiver = (ScrcpyOpusReceiver?)typeof(AndroidAudioSession).GetField("_receiver", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session);
            Check(receiver?.DecoderId == AndroidStreamSettings.ConcentusDecoder, "audio session passes the saved decoder into its real receive path");
            await session.StopAsync();
        }

        var (rawSessionWire, _, _) = BuildRawPcmWire(includeEmptyConfig: true, includeLargePacket: false, includeKeyFrameFlagOnLast: false);
        var rawSelected = defaults with
        {
            AudioDecoder = AndroidStreamSettings.RawPcmDecoder,
            AudioLatencyMode = AndroidStreamSettings.LatencyModeUltraLow
        };
        await using (var rawSession = new AndroidAudioSession(() => new FakeTransport(rawSessionWire, () => { }), (_, _) => new FakePlayer(),
            streaming: _ => rawSelected))
        {
            await rawSession.StartAsync(phone, AndroidApp.DeviceAudio);
            var receiver = (IAndroidAudioReceiver?)typeof(AndroidAudioSession).GetField("_receiver", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rawSession);
            Check(receiver is ScrcpyRawPcmReceiver
                && receiver.DecoderId == AndroidStreamSettings.RawPcmDecoder
                && receiver.CodecId == ScrcpyAudioProtocol.CodecRaw
                && rawSession.Pcm is { LatencyMode: AudioLatencyMode.UltraLow, NonSilentSamplesWritten: > 0 },
                "audio session selects ScrcpyRawPcmReceiver and applies saved latency mode on raw PCM stream");
            await rawSession.StopAsync();
        }

        await using (var fallbackSession = new AndroidAudioSession(() => new FakeTransport(BuildWire(), () => { }), (_, _) => new FakePlayer(),
            streaming: _ => rawSelected))
        {
            await fallbackSession.StartAsync(phone, AndroidApp.DeviceAudio);
            var receiver = (IAndroidAudioReceiver?)typeof(AndroidAudioSession).GetField("_receiver", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fallbackSession);
            Check(receiver is ScrcpyOpusReceiver && receiver.CodecId == ScrcpyAudioProtocol.CodecOpus,
                "audio session falls back to Concentus Opus decoder when server streams opus despite raw-pcm preference");
            await fallbackSession.StopAsync();
        }

        byte[] unknownWire = [.. System.Text.Encoding.ASCII.GetBytes("flac")];
        await using (var invalidCodecSession = new AndroidAudioSession(() => new FakeTransport(unknownWire, () => { }), (_, _) => new FakePlayer(),
            streaming: _ => rawSelected))
        {
            bool rejectedUnknownCodec = false;
            try { await invalidCodecSession.StartAsync(phone, AndroidApp.DeviceAudio); }
            catch (InvalidDataException) { rejectedUnknownCodec = true; }
            Check(rejectedUnknownCodec && invalidCodecSession.State.State == CaptureState.Failed,
                "audio session safely rejects unknown wire codec IDs without playing garbage");
        }

        string runtime = Path.Combine(output, "fake-runtime"); Directory.CreateDirectory(runtime);
        await File.WriteAllTextAsync(Path.Combine(runtime, "sonicroute-catalog.jar"), "test-only transport marker");
        var transport = new EncoderSettingsTransport();
        await using (var adb = new AdbClient(runtime, transport))
        {
            var first = await adb.EncodersAsync(phone, default);
            var cached = await adb.EncodersAsync(wifi, default);
            Check(ReferenceEquals(first, cached) && transport.Queries == 1 && transport.Pushes == 1,
                "reopening one physical phone reuses its encoder catalog without another push or shell request");
            await adb.EncodersAsync(phone with { HardwareId = "other" }, default);
            Check(transport.Queries == 2 && transport.Pushes == 1, "another phone cannot reuse the first phone's encoder capabilities while reusing the cached remote jar");
            adb.InvalidateEncoders(phone); await adb.EncodersAsync(phone, default);
            Check(transport.Queries == 3, "an explicit refresh queries that phone again");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            bool canceledHit = false;
            try { await adb.EncodersAsync(phone, canceled.Token); } catch (OperationCanceledException) { canceledHit = true; }
            Check(canceledHit && transport.Queries == 3, "canceled views cannot start work even on a cache hit");

            adb.InvalidateEncoders(phone);
            transport.Query = _ => throw new IOException("Injected capability failure");
            try { await adb.EncodersAsync(phone, default); } catch (IOException) { }
            transport.Query = _ => Task.FromResult(EncoderSettingsTransport.Json);
            await adb.EncodersAsync(phone, default);
            Check(transport.Queries == 5 && transport.Pushes == 1, "failed queries are retried instead of cached and reuse the cached jar");

            adb.InvalidateEncoders(phone);
            var held = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.Query = _ => held.Task;
            var pending = adb.EncodersAsync(phone, default);
            adb.InvalidateEncoders(phone);
            held.SetResult(EncoderSettingsTransport.Json); await pending;
            transport.Query = _ => Task.FromResult(EncoderSettingsTransport.Json);
            await adb.EncodersAsync(phone, default);
            Check(transport.Queries == 7, "an older in-flight query cannot repopulate an explicitly invalidated cache");
            for (int i = 0; i < 5; i++) await adb.EncodersAsync(phone with { HardwareId = "bounded-" + i }, default);
            int before = transport.Queries;
            await adb.EncodersAsync(phone, default);
            Check(transport.Queries == before + 1, "the capability cache evicts old phones rather than retaining unbounded entries");
        }

        await using (var window = new NativeVideoWindow("Hidden decoder selection check", false, true, null, 0, 0, 240, 240))
        {
            nint hwnd = await window.CreateAsync(default);
            using var renderer = new D3D11VideoSurfaceRenderer(hwnd, 240, 240);
            foreach (string? mode in new[] { null, AndroidStreamSettings.WindowsVideoDecoder, AndroidStreamSettings.SoftwareVideoDecoder })
            {
                using var decoder = new MediaFoundationH264Decoder(renderer.DeviceHandle, 64, 64, mode);
                var (config, idr) = BuildTestBaselineH264_64x64();
                var merger = new ScrcpyPacketMerger(); merger.Process(config, true);
                int hardware = 0, software = 0;
                void Hardware(nint texture, uint slice, int w, int h, int x, int y, int cw, int ch, VideoColorMatrix matrix)
                { renderer.PresentHardwareTexture(texture, slice, w, h, x, y, cw, ch, matrix); hardware++; }
                void Software(ReadOnlySpan<byte> frame, int stride, int h, int x, int y, int cw, int ch, VideoColorMatrix matrix)
                { renderer.PresentSoftwareNv12(frame, stride, h, x, y, cw, ch, matrix); software++; }
                byte[] packet = merger.Process(idr, false).ToArray();
                for (int i = 0; i < 4; i++) decoder.DecodePacket(i == 0 ? packet : idr, i * 33333L, Hardware, Software);
                if (hardware + software == 0) decoder.Drain(Hardware, Software);
                Check(hardware + software > 0 && renderer.HasPresentedFrame, $"{mode ?? "automatic"} presents real synthetic H.264 frames");
                if (mode == AndroidStreamSettings.SoftwareVideoDecoder)
                    Check(!decoder.IsHardwareAccelerated && hardware == 0 && software > 0,
                        "explicit software decoding stays on CPU even with a live D3D11 renderer device");
            }
            Check(!IsWindowVisibleForStreamTest(hwnd), "decoder probes keep their owned native window hidden");
        }
        Check(MediaFoundationH264Decoder.ActiveInstanceCount == 0 && D3D11VideoSurfaceRenderer.ActiveInstanceCount == 0,
            "all selected decoder modes release their native resources");
        await File.WriteAllTextAsync(Path.Combine(output, "decoder-settings-summary.json"), JsonSerializer.Serialize(new
        { Assertions = _checks, RealPhone = false, Playback = false, Scope = "decoder persistence, real Opus/MFT modes, bounded encoder cache and refresh/cancellation" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} decoder/capability assertions; no real phone or speaker playback.");
    }

    private sealed class EncoderSettingsTransport : PairingFixtureTransport, IAdbTransport
    {
        internal const string Json = "{\"audio\":[\"c2.android.opus.encoder\"],\"video\":[\"c2.android.avc.encoder\"]}";
        internal int Queries, Pushes;
        private readonly Dictionary<string, long> _jars = new(StringComparer.Ordinal);
        internal Func<CancellationToken, Task<string>> Query = _ => Task.FromResult(Json);
        Task IAdbTransport.PushAsync(string serial, string local, string remote, CancellationToken token)
        { Pushes++; _jars[serial + "\0" + remote] = new FileInfo(local).Length; return Task.CompletedTask; }
        async Task<AdbShellResult> IAdbTransport.RunShellAsync(string serial, string command, CancellationToken token)
        {
            if (command.StartsWith("ls -l ", StringComparison.Ordinal))
            {
                string path = command["ls -l ".Length..].Split(' ', 2)[0];
                return _jars.TryGetValue(serial + "\0" + path, out long len)
                    ? new($"-rw-r--r-- 1 shell shell {len} 2026-01-01 00:00 {path}\n", 0)
                    : new("MISSING\n", 0);
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
                return new("", 0);
            }
            if (command.Contains("org.sonicroute.android.EncoderCatalog", StringComparison.Ordinal))
            { Queries++; return new(await Query(token), 0); }
            return new("", 0);
        }
    }

    private static async Task AndroidHevcStartAsync(string output, bool checkResize = false)
    {
        Check(MediaFoundationH264Decoder.SupportsHevc, "Windows can open the HEVC decoder for actual frame output");
        var device = new AndroidDevice("hevc-fixture", "Fixture", "device", false, "hevc-fixture");
        var app = new AndroidApp("cn.fixture.video", "Decoder fixture", 1001, "media");
        var settings = new AndroidAudioSettingsStore(Path.Combine(output, "android-hevc-preferences.json"));
        settings.RememberStreaming(device, new(VideoDecoder: AndroidStreamSettings.WindowsHevcDecoder,
            VideoEncoder: "c2.test.avc.encoder", HevcEncoder: "c2.test.hevc.encoder"));
        await settings.FlushAsync();
        await using var adb = new AdbClient(output, new HevcStartTransport());
        foreach (string codec in checkResize ? new[] { "h265-resize" } : new[] { "h265", "h264" })
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(output, codec + "-android-video.bin"), cancel.Token);
            int opened = 0, disposed = 0;
            var transport = new DelegateVideoStreamTransport((request, _) =>
            {
                opened++;
                Check(request.Options is { NewDisplay: true, StartAppOnServer: true, VideoCodec: "h265", VideoEncoder: "c2.test.hevc.encoder" },
                    "the app-window transport requests the saved H.265 encoder");
                var stream = new MemoryStream(bytes, writable: false);
                return Task.FromResult(new VideoStreamSession(stream, null, Task.CompletedTask, null, () =>
                { disposed++; return stream.DisposeAsync(); }));
            });
            await using var client = new InProcessVirtualDisplayClient(adb, transport, () => settings);
            await client.StartAsync(device, app, -10000, -10000, cancel.Token);
            await client.Completion.WaitAsync(cancel.Token);
            Check(opened == 1 && disposed == 1, $"the negotiated {codec} app video renders a frame and retires its only session");
        }
        Check(settings.GetStreaming(device).VideoDecoder == AndroidStreamSettings.WindowsHevcDecoder,
            "a fallback frame never rewrites the saved decoder preference");
        AndroidVirtualDisplaySize? size = settings.Get(device, app.Package).DisplaySize;
        if (checkResize)
        {
            Check(size == new AndroidVirtualDisplaySize(848, 640),
                "the HEVC decoder renders the final dimensions after two encoder restarts");
        }
        else
        {
            Check(size == null, "a small decoded picture renders without persisting an unsupported virtual-display size");
        }
        string summary = checkResize ? "android-hevc-recovery-summary.json" : "android-hevc-start-summary.json";
        await File.WriteAllTextAsync(Path.Combine(output, summary),
            JsonSerializer.Serialize(new { Assertions = _checks, DeviceContact = false, WindowsDecodedFrames = true, FinalSize = size }));
    }

    private sealed class HevcStartTransport : PairingFixtureTransport, IAdbTransport
    {
        Task<AdbShellResult> IAdbTransport.RunShellAsync(string serial, string command, CancellationToken token)
        {
            if (command is "pm list packages -U --user 0" or "pm list packages -U --user current")
                return Task.FromResult(new AdbShellResult("package:cn.fixture.video uid:1001\n", 0));
            throw new InvalidOperationException("Unexpected simulated Android command.");
        }
    }
}

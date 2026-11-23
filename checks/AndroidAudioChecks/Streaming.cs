using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task EncoderCatalogAsync(string runtime, string output)
    {
        Directory.CreateDirectory(output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var adb = new AdbClient(runtime);
        var devices = await adb.DevicesAsync(deadline.Token);
        var device = devices.First(x => x.State == "device");
        var encoders = await adb.EncodersAsync(device, deadline.Token);
        Check(encoders.Audio.Count > 0 && encoders.Video.Count > 0
            && encoders.Audio.Concat(encoders.Video).All(AndroidStreamSettings.IsEncoderName),
            "one-shot device query returns selectable Opus and H.264 encoder names");
        await File.WriteAllTextAsync(Path.Combine(output, "encoder-catalog.json"), JsonSerializer.Serialize(new
        { Device = device.Name, Encoders = encoders, RealAdb = true, CaptureStarted = false, ContinuousWork = false },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task StreamSettingsAsync(string output)
    {
        Directory.CreateDirectory(output);
        var phone = new AndroidDevice("usb", "Fixture", "device", false, "streaming-fixture");
        var wifi = phone with { Serial = "127.0.0.1:12345", IsWifi = true };
        var other = phone with { HardwareId = "another-phone", Serial = "usb2" };
        var defaults = new AndroidStreamSettings();
        Check(defaults.AudioOptions() == "audio_bit_rate=128000"
            && defaults.AudioCodec == "opus"
            && defaults.AudioLatencyMode == AndroidStreamSettings.LatencyModeBalanced
            && defaults.EffectiveAudioLatencyMode == AudioLatencyMode.Balanced,
            "default audio preserves Opus bitrate, automatic encoder, and balanced latency mode");
        var invalid = new AndroidStreamSettings("bad;touch", -1, "codec\nvalue", int.MaxValue, 0,
            AudioLatencyMode: "unknown-mode").Normalize();
        Check(invalid == defaults, "corrupt values and shell metacharacters fall back to safe defaults");
        var highBounds = new AndroidStreamSettings("c2.android.opus.encoder", 320000, "c2.qti.avc.encoder", 50000000, 120).Normalize();
        Check(highBounds is { VideoBitRate: 50000000, MaxFps: 120 }, "upper-bound 50 Mbps and 120 fps pass normalization");
        var parsedDetails = AdbClient.ParseEncoders(
            "{\"audio\":[\"c2.android.opus.encoder\"],\"video\":[\"c2.android.avc.encoder\",\"c2.qti.avc.encoder\"],\"details\":{\"c2.android.opus.encoder\":{\"hw\":false,\"sw\":true},\"c2.android.avc.encoder\":{\"hw\":false,\"sw\":true},\"c2.qti.avc.encoder\":{\"hw\":true,\"sw\":false},\"bad;name\":{\"hw\":true,\"sw\":false}}}");
        Check(parsedDetails.Kinds != null
            && parsedDetails.Kinds["c2.qti.avc.encoder"] == AndroidEncoderKind.Hardware
            && parsedDetails.Kinds["c2.android.avc.encoder"] == AndroidEncoderKind.Software
            && !parsedDetails.Kinds.ContainsKey("bad;name"),
            "encoder catalog parses safe hardware and software capabilities");
        var parsedLegacy = AdbClient.ParseEncoders("{\"audio\":[\"c2.android.opus.encoder\"],\"video\":[\"c2.android.avc.encoder\"]}");
        Check(parsedLegacy.Kinds == null && parsedLegacy.Hevc is { Count: 0 } && parsedLegacy.Video.SequenceEqual(["c2.android.avc.encoder"]),
            "legacy catalog JSON without details remains backward-compatible");
        var hevcCatalog = AdbClient.ParseEncoders("""{"audio":[],"video":["c2.android.avc.encoder"],"hevc":["c2.qti.hevc.encoder","bad;codec","c2.qti.hevc.encoder"]}""");
        Check(hevcCatalog.Hevc!.SequenceEqual(["c2.qti.hevc.encoder"]) && hevcCatalog.Video.SequenceEqual(["c2.android.avc.encoder"]),
            "HEVC encoder IDs remain distinct from AVC and are validated by the same rules");
        string path = Path.Combine(output, "preferences-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{\"Version\":2,\"Apps\":{\"cn.test.music\":{\"Volume\":37,\"OutputId\":\"speakers\"}}}");
        var store = new AndroidAudioSettingsStore(path);
        Check(store.GetStreaming(phone) == defaults && store.Get(phone, "cn.test.music").Volume == 37,
            "old per-app preferences load with unchanged streaming defaults");
        var selected = new AndroidStreamSettings("c2.android.opus.encoder", 192000, "c2.vendor.avc.encoder", 32000000, 120);
        store.RememberStreaming(phone, selected);
        await store.FlushAsync();
        var restored = new AndroidAudioSettingsStore(path);
        Check(restored.GetStreaming(wifi) == selected && restored.GetStreaming(other) == defaults,
            "streaming choices persist per physical phone across USB and Wi-Fi");
        Check(restored.Get(phone, "cn.test.music").Volume == 37 && restored.Get(phone, "cn.test.music").OutputId == "speakers",
            "saving streaming preferences preserves migrated application volume and output");
        Check(selected.AudioOptions() == "audio_bit_rate=192000 audio_encoder=c2.android.opus.encoder",
            "audio transport receives bitrate and selected Opus encoder");
        var normSel = selected.Normalize();
        var normHigh = highBounds.Normalize();
        var normInvalid = invalid.Normalize();
        string mirrorCmd = AdbVideoStreamTransport.BuildServerShellCommand("/data/local/tmp/sonicroute-video-server.jar",
            new VideoStreamOpenRequest(phone, AndroidApp.DeviceAudio, "11223344", new VideoServerOptions(
                NewDisplay: false, MirrorDeviceDisplay: true, MaxFps: normSel.MaxFps,
                VideoBitRate: normSel.VideoBitRate, VideoEncoder: normSel.VideoEncoder, StartAppOnServer: false)));
        string appCmd = AdbVideoStreamTransport.BuildServerShellCommand("/data/local/tmp/sonicroute-video-server.jar",
            new VideoStreamOpenRequest(phone, new("cn.test.music", "Music", 1001, "media"), "11223344", new VideoServerOptions(
                MaxFps: normHigh.MaxFps, VideoBitRate: normHigh.VideoBitRate, VideoEncoder: normHigh.VideoEncoder, IsWifi: wifi.IsWifi)));
        Check(mirrorCmd.EndsWith(" display_id=0", StringComparison.Ordinal) && mirrorCmd.Contains(" video_encoder=c2.vendor.avc.encoder ")
            && mirrorCmd.Contains(" video_bit_rate=32000000 ") && mirrorCmd.Contains(" max_fps=120 ")
            && mirrorCmd.Contains(" video_transport=usb video_abr=true "),
            "phone mirror receives 32 Mbps, 120 fps, USB ABR transport without creating a desktop");
        Check(appCmd.Contains(" new_display ") && appCmd.Contains(" start_app=cn.test.music ")
            && appCmd.Contains(" video_bit_rate=50000000 ") && appCmd.Contains(" max_fps=120 ")
            && appCmd.Contains(" video_transport=wifi video_abr=true ")
            && appCmd.Contains(" audio=false "),
            "ordinary app screens share 50 Mbps, 120 fps, and Wi-Fi ABR transport while audio stays managed separately");
        string safeCmd = AdbVideoStreamTransport.BuildServerShellCommand("/data/local/tmp/sonicroute-video-server.jar",
            new VideoStreamOpenRequest(phone, AndroidApp.DeviceAudio, "11223344", new VideoServerOptions(
                NewDisplay: false, MirrorDeviceDisplay: true, MaxFps: normInvalid.MaxFps,
                VideoBitRate: normInvalid.VideoBitRate, VideoEncoder: normInvalid.VideoEncoder, StartAppOnServer: false)));
        Check(!safeCmd.Contains("video_encoder=")
            && safeCmd.Contains(" max_fps=30 ") && safeCmd.Contains(" video_bit_rate=4000000 "),
            "invalid persisted video values never reach the server command line");
        var hevc = selected with { VideoDecoder = AndroidStreamSettings.WindowsHevcDecoder, HevcEncoder = "c2.qti.hevc.encoder" };
        restored.RememberStreaming(phone, hevc);
        await restored.FlushAsync();
        Check(new AndroidAudioSettingsStore(path).GetStreaming(wifi) == hevc,
            "per-device HEVC preference reloads without dropping the saved AVC encoder");

        var rawAndLatency = (hevc with
        {
            AudioDecoder = AndroidStreamSettings.RawPcmDecoder,
            AudioLatencyMode = AndroidStreamSettings.LatencyModeUltraLow
        }).Normalize();
        Check(rawAndLatency.AudioCodec == "raw"
            && rawAndLatency.AudioOptions().Length == 0
            && rawAndLatency.EffectiveAudioLatencyMode == AudioLatencyMode.UltraLow,
            "raw PCM mode reports raw audio codec, omits Opus CLI args, and keeps ultra-low latency enum");
        restored.RememberStreaming(phone, rawAndLatency);
        await restored.FlushAsync();
        var reloadedRaw = new AndroidAudioSettingsStore(path).GetStreaming(wifi);
        Check(reloadedRaw == rawAndLatency
            && reloadedRaw.AudioEncoder == "c2.android.opus.encoder"
            && reloadedRaw.AudioBitRate == 192000,
            "raw PCM and latency mode persist across USB/Wi-Fi without erasing saved Opus encoder or bitrate");
        string rawAudioCmd = ScrcpyAdbTransport.BuildServerShellCommand("/data/local/tmp/sonicroute-catalog.jar", "55667788",
            new("cn.test.music", "Music", 1001, "media"), false, reloadedRaw);
        Check(rawAudioCmd.Contains(" audio_codec=raw ", StringComparison.Ordinal)
            && !rawAudioCmd.Contains("audio_bit_rate=", StringComparison.Ordinal)
            && !rawAudioCmd.Contains("audio_encoder=", StringComparison.Ordinal),
            "raw PCM audio server command switches to audio_codec=raw without Opus options");
        var stableMode = (reloadedRaw with
        {
            AudioDecoder = AndroidStreamSettings.ConcentusDecoder,
            AudioLatencyMode = AndroidStreamSettings.LatencyModeStable
        }).Normalize();
        Check(stableMode.AudioCodec == "opus"
            && stableMode.EffectiveAudioLatencyMode == AudioLatencyMode.Stable
            && stableMode.AudioOptions() == "audio_bit_rate=192000 audio_encoder=c2.android.opus.encoder",
            "switching back from raw PCM restores Opus options and preserves stable latency mode");
        string opusAudioCmd = ScrcpyAdbTransport.BuildServerShellCommand("/data/local/tmp/sonicroute-catalog.jar", "55667788",
            new("cn.test.music", "Music", 1001, "media"), false, stableMode);
        Check(opusAudioCmd.Contains(" audio_codec=opus ", StringComparison.Ordinal)
            && opusAudioCmd.Contains(" audio_bit_rate=192000 audio_encoder=c2.android.opus.encoder ", StringComparison.Ordinal),
            "Opus audio server command remains intact alongside new latency mode");
        await File.WriteAllTextAsync(Path.Combine(output, "stream-settings-summary.json"), JsonSerializer.Serialize(new
        { Assertions = _checks, RealAdb = false, ProductStarted = false, Scope = "preferences migration, isolation, capability parsing, 24/32/50 Mbps, 120 fps, ABR transport flags, audio latency modes, raw PCM, and both video paths" },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}

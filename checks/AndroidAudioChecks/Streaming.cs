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
        Check(defaults.AudioOptions() == "audio_bit_rate=128000", "default audio preserves Opus bitrate and automatic encoder");
        var invalid = new AndroidStreamSettings("bad;touch", -1, "codec\nvalue", int.MaxValue, 0).Normalize();
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
        Check(parsedLegacy.Kinds == null && parsedLegacy.Video.SequenceEqual(["c2.android.avc.encoder"]),
            "legacy catalog JSON without details remains backward-compatible");
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
                MaxFps: normHigh.MaxFps, VideoBitRate: normHigh.VideoBitRate, VideoEncoder: normHigh.VideoEncoder)));
        Check(mirrorCmd.EndsWith(" display_id=0", StringComparison.Ordinal) && mirrorCmd.Contains(" video_encoder=c2.vendor.avc.encoder ")
            && mirrorCmd.Contains(" video_bit_rate=32000000 ") && mirrorCmd.Contains(" max_fps=120 "),
            "phone mirror receives 32 Mbps and 120 fps without creating a desktop");
        Check(appCmd.Contains(" new_display ") && appCmd.Contains(" start_app=cn.test.music ")
            && appCmd.Contains(" video_bit_rate=50000000 ") && appCmd.Contains(" max_fps=120 ")
            && appCmd.Contains(" audio=false "),
            "ordinary app screens share 50 Mbps and 120 fps phone video choices while audio stays managed separately");
        string safeCmd = AdbVideoStreamTransport.BuildServerShellCommand("/data/local/tmp/sonicroute-video-server.jar",
            new VideoStreamOpenRequest(phone, AndroidApp.DeviceAudio, "11223344", new VideoServerOptions(
                NewDisplay: false, MirrorDeviceDisplay: true, MaxFps: normInvalid.MaxFps,
                VideoBitRate: normInvalid.VideoBitRate, VideoEncoder: normInvalid.VideoEncoder, StartAppOnServer: false)));
        Check(!safeCmd.Contains("video_encoder=")
            && safeCmd.Contains(" max_fps=30 ") && safeCmd.Contains(" video_bit_rate=4000000 "),
            "invalid persisted video values never reach the server command line");
        await File.WriteAllTextAsync(Path.Combine(output, "stream-settings-summary.json"), JsonSerializer.Serialize(new
        { Assertions = _checks, RealAdb = false, ProductStarted = false, Scope = "preferences migration, isolation, capability parsing, 24/32/50 Mbps, 120 fps, and both video paths" },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}

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
        var adb = new AdbClient(Path.Combine(output, "offline-runtime"));
        var mirror = VirtualDisplayClient.CreateStartInfo(adb, phone, AndroidApp.DeviceAudio, 0, 0, streaming: selected);
        var app = VirtualDisplayClient.CreateStartInfo(adb, phone, new("cn.test.music", "Music", 1001, "media"), 0, 0, streaming: highBounds);
        Check(mirror.ArgumentList.Contains("--display-id=0") && mirror.ArgumentList.Contains("--video-encoder=c2.vendor.avc.encoder")
            && mirror.ArgumentList.Contains("--video-bit-rate=32000000") && mirror.ArgumentList.Contains("--max-fps=120"),
            "phone mirror receives 32 Mbps and 120 fps without creating a desktop");
        Check(app.ArgumentList.Contains("--new-display") && app.ArgumentList.Contains("--start-app=cn.test.music")
            && app.ArgumentList.Contains("--video-bit-rate=50000000") && app.ArgumentList.Contains("--max-fps=120")
            && app.ArgumentList.Contains("--no-audio"),
            "ordinary app screens share 50 Mbps and 120 fps phone video choices while audio stays managed separately");
        var safe = VirtualDisplayClient.CreateStartInfo(adb, phone, AndroidApp.DeviceAudio, 0, 0, streaming: invalid);
        Check(!safe.ArgumentList.Any(x => x.StartsWith("--video-encoder"))
            && safe.ArgumentList.Contains("--max-fps=30") && safe.ArgumentList.Contains("--video-bit-rate=4000000"),
            "invalid persisted video values never reach the command line");
        await File.WriteAllTextAsync(Path.Combine(output, "stream-settings-summary.json"), JsonSerializer.Serialize(new
        { Assertions = _checks, RealAdb = false, ProductStarted = false, Scope = "preferences migration, isolation, capability parsing, 24/32/50 Mbps, 120 fps, and both video paths" },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}

using System.Globalization;
using System.Text.Json;

namespace SonicRoute.AndroidAudio;

/// <summary>Per-phone streaming choices. Applied only when opening a new audio or video connection.</summary>
public sealed record AndroidStreamSettings(string? AudioEncoder = null, int AudioBitRate = 128000,
    string? VideoEncoder = null, int VideoBitRate = 4000000, int MaxFps = 30,
    string? AudioDecoder = null, string? VideoDecoder = null, string? HevcEncoder = null,
    string? AudioLatencyMode = AndroidStreamSettings.LatencyModeBalanced)
{
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalFields { get; set; }

    public const string ConcentusDecoder = "concentus-opus";
    public const string RawPcmDecoder = ScrcpyAudioProtocol.RawPcmDecoderId;
    public const string WindowsVideoDecoder = "windows-h264-d3d11";
    public const string SoftwareVideoDecoder = "windows-h264-software";
    public const string WindowsHevcDecoder = "windows-h265";
    public const string LatencyModeUltraLow = "ultra-low";
    public const string LatencyModeBalanced = "balanced";
    public const string LatencyModeStable = "stable";

    public AndroidStreamSettings Normalize() => this with
    {
        AudioEncoder = IsEncoderName(AudioEncoder) ? AudioEncoder : null,
        VideoEncoder = IsEncoderName(VideoEncoder) ? VideoEncoder : null,
        HevcEncoder = IsEncoderName(HevcEncoder) ? HevcEncoder : null,
        AudioDecoder = AudioDecoder is ConcentusDecoder or RawPcmDecoder ? AudioDecoder : null,
        VideoDecoder = VideoDecoder is WindowsVideoDecoder or SoftwareVideoDecoder or WindowsHevcDecoder ? VideoDecoder : null,
        AudioBitRate = AudioBitRate is >= 16000 and <= 512000 ? AudioBitRate : 128000,
        VideoBitRate = VideoBitRate is >= 100000 and <= 50000000 ? VideoBitRate : 4000000,
        MaxFps = MaxFps is >= 1 and <= 120 ? MaxFps : 30,
        AudioLatencyMode = AudioLatencyMode is LatencyModeUltraLow or LatencyModeBalanced or LatencyModeStable
            ? AudioLatencyMode
            : LatencyModeBalanced
    };

    public string AudioCodec => AudioDecoder == RawPcmDecoder ? "raw" : "opus";

    public AudioLatencyMode EffectiveAudioLatencyMode => AudioLatencyMode switch
    {
        LatencyModeUltraLow => SonicRoute.AndroidAudio.AudioLatencyMode.UltraLow,
        LatencyModeStable => SonicRoute.AndroidAudio.AudioLatencyMode.Stable,
        _ => SonicRoute.AndroidAudio.AudioLatencyMode.Balanced
    };

    // Encoder names become Android shell arguments as well as desktop ArgumentList
    // values. Keep persisted preferences and device-reported names in the same safe set.
    public static bool IsEncoderName(string? name) => name is { Length: > 0 and <= 128 }
        && name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    public string AudioOptions()
    {
        var value = Normalize();
        if (value.AudioDecoder == RawPcmDecoder)
            return string.Empty;
        return "audio_bit_rate=" + value.AudioBitRate.ToString(CultureInfo.InvariantCulture)
            + (value.AudioEncoder == null ? "" : " audio_encoder=" + value.AudioEncoder);
    }
}

public enum AndroidEncoderKind { Unknown, Hardware, Software }

public sealed record AndroidEncoderCatalog(
    IReadOnlyList<string> Audio,
    IReadOnlyList<string> Video,
    IReadOnlyDictionary<string, AndroidEncoderKind>? Kinds = null,
    IReadOnlyList<string>? Hevc = null);

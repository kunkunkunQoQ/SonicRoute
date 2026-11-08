using System.Globalization;

namespace SonicRoute.AndroidAudio;

/// <summary>Per-phone streaming choices. Applied only when opening a new audio or video connection.</summary>
public sealed record AndroidStreamSettings(string? AudioEncoder = null, int AudioBitRate = 128000,
    string? VideoEncoder = null, int VideoBitRate = 4000000, int MaxFps = 30)
{
    public AndroidStreamSettings Normalize() => this with
    {
        AudioEncoder = IsEncoderName(AudioEncoder) ? AudioEncoder : null,
        VideoEncoder = IsEncoderName(VideoEncoder) ? VideoEncoder : null,
        AudioBitRate = AudioBitRate is >= 16000 and <= 512000 ? AudioBitRate : 128000,
        VideoBitRate = VideoBitRate is >= 100000 and <= 50000000 ? VideoBitRate : 4000000,
        MaxFps = MaxFps is >= 1 and <= 120 ? MaxFps : 30
    };

    // Encoder names become Android shell arguments as well as desktop ArgumentList
    // values. Keep persisted preferences and device-reported names in the same safe set.
    public static bool IsEncoderName(string? name) => name is { Length: > 0 and <= 128 }
        && name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    public string AudioOptions()
    {
        var value = Normalize();
        return "audio_bit_rate=" + value.AudioBitRate.ToString(CultureInfo.InvariantCulture)
            + (value.AudioEncoder == null ? "" : " audio_encoder=" + value.AudioEncoder);
    }
}

public enum AndroidEncoderKind { Unknown, Hardware, Software }

public sealed record AndroidEncoderCatalog(
    IReadOnlyList<string> Audio,
    IReadOnlyList<string> Video,
    IReadOnlyDictionary<string, AndroidEncoderKind>? Kinds = null);

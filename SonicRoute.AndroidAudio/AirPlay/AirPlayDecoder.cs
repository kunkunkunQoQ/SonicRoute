using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SonicRoute.AirPlay;

internal readonly record struct AirPlayFormat(int Codec, int SampleRate, int Bits, int Frames)
{
    internal const int Pcm = 1, Alac = 2, Aac = 4, Eld = 8;
    internal const long OutputFormats = (1L << 11) | (1L << 15) | (15L << 18);
    internal const long AacFormats = 3L << 22;
    internal const long EldFormats = 3L << 24;

    internal static AirPlayFormat Parse(Dictionary<string, object?> stream, bool legacy = false)
    {
        long format = AirPlayPlist.Int(stream, "audioFormat");
        long ct = AirPlayPlist.Int(stream, "ct");
        var (codec, rate, bits, frames) = format switch
        {
            0 when legacy && ct == Pcm => (Pcm, 44100, 16, 352),
            0 when legacy && ct == Alac => (Alac, 44100, 16, 352),
            0 when legacy && ct == Aac => (Aac, 44100, 16, 1024),
            0 when ct == Eld => (Eld, 44100, 16, 480),
            1L << 11 => (Pcm, 44100, 16, 352),
            1L << 15 => (Pcm, 48000, 16, 352),
            1L << 18 => (Alac, 44100, 16, 352),
            1L << 19 => (Alac, 44100, 24, 352),
            1L << 20 => (Alac, 48000, 16, 352),
            1L << 21 => (Alac, 48000, 24, 352),
            1L << 22 => (Aac, 44100, 16, 1024),
            1L << 23 => (Aac, 48000, 16, 1024),
            1L << 24 => (Eld, 44100, 16, 480),
            1L << 25 => (Eld, 48000, 16, 480),
            _ => throw new NotSupportedException("Unsupported AirPlay audio format.")
        };
        long spf = AirPlayPlist.Int(stream, "spf", frames);
        if ((ct != 0 && ct != codec) || spf is < 1 or > 4096) throw new NotSupportedException("Unsupported AirPlay frame format.");
        if (codec == Eld && (spf is not (480 or 512) || !SupportsEldConfig(stream, rate, (int)spf)))
            throw new NotSupportedException("Unsupported AAC-ELD configuration.");
        return new(codec, rate, bits, (int)spf);
    }

    private static bool SupportsEldConfig(Dictionary<string, object?> stream, int rate, int frames)
    {
        if (!stream.TryGetValue("audioConfig", out var config)) return true;
        // Audio object type 39, a stereo pair, and the selected ELD block length.
        // The remaining flags must signal no LD-SBR, resilience, or ELD extensions.
        return config is byte[] { Length: 4 } asc
            && asc[0] == 0xf8 && asc[1] == (rate == 44100 ? 0xe8 : 0xe6)
            && asc[2] == (frames == 480 ? 0x50 : 0x40) && asc[3] == 0;
    }
}

/// <summary>Decodes AirPlay packets with Windows Media Foundation and keeps one pair of reusable samples.</summary>
internal sealed class AirPlayDecoder : IDisposable
{
    internal const int MaxPacket = 24 * 1024;
    private readonly AacEldDecoder? _eld;
    private readonly AirPlayAudioTransform? _transform;

    internal AirPlayDecoder(AirPlayFormat format)
    {
        if (format.Codec == AirPlayFormat.Eld) _eld = new AacEldDecoder(format);
        if (format.Codec is AirPlayFormat.Aac or AirPlayFormat.Alac)
            _transform = new AirPlayAudioTransform(format, format.Codec == AirPlayFormat.Aac ? AacConfig(format.SampleRate) : AlacConfig(format));
    }

    internal int Decode(ReadOnlySpan<byte> packet, Span<float> pcm)
    {
        if (packet.Length is 0 or > MaxPacket) return 0;
        if (_eld != null) return _eld.Decode(packet, pcm);
        if (_transform != null) return _transform.Decode(packet, pcm);
        return DecodePcm(packet, pcm, 16, bigEndian: true);
    }

    internal static int DecodePcm(ReadOnlySpan<byte> input, Span<float> output, int bits, bool bigEndian)
    {
        int width = bits == 0 ? 4 : bits / 8;
        int samples = Math.Min(input.Length / width, output.Length) & ~1;
        if (bits == 0)
        {
            MemoryMarshal.Cast<byte, float>(input[..(samples * 4)]).CopyTo(output);
            return samples;
        }
        for (int i = 0; i < samples; ++i)
        {
            var bytes = input[(i * width)..];
            output[i] = (bits, bigEndian) switch
            {
                (16, true) => BinaryPrimitives.ReadInt16BigEndian(bytes) / 32768f,
                (16, false) => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
                (24, _) => (bytes[0] << 8 | bytes[1] << 16 | bytes[2] << 24) / 2147483648f,
                _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f
            };
        }
        return samples;
    }

    internal void Flush()
    {
        _eld?.Flush();
        _transform?.Flush();
    }

    private static byte[] AacConfig(int rate)
    {
        // HEAACWAVEINFO extension followed by the two-byte stereo AAC-LC AudioSpecificConfig.
        return [0, 0, 0xfe, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            rate == 44100 ? (byte)0x12 : (byte)0x11, rate == 44100 ? (byte)0x10 : (byte)0x90];
    }

    private static byte[] AlacConfig(AirPlayFormat format)
    {
        // The Windows decoder consumes ALACSpecificConfig directly, without the enclosing atom.
        byte[] bytes = new byte[24];
        BinaryPrimitives.WriteInt32BigEndian(bytes, format.Frames);
        bytes[5] = (byte)format.Bits;
        bytes[6] = 40; bytes[7] = 10; bytes[8] = 14; bytes[9] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), 255);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), format.SampleRate);
        return bytes;
    }

    public void Dispose() => _transform?.Dispose();
}

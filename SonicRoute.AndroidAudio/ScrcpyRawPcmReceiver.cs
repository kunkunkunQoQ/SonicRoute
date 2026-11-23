using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SonicRoute.AndroidAudio;

public interface IAndroidAudioReceiver
{
    string DecoderId { get; }
    uint CodecId { get; }
    long PacketsDecoded { get; }
    long LastTimestampUs { get; }
    long TimestampDiscontinuities { get; }
    Task ReceiveAsync(Stream stream, AndroidPcmSource pcm, Action firstFrame, CancellationToken token);
    internal Task ReceivePacketsAsync(Stream stream, AndroidPcmSource pcm, Action firstFrame, CancellationToken token);
}

public static class ScrcpyAudioProtocol
{
    public const uint CodecOpus = 0x6f707573; // "opus"
    public const uint CodecRaw = 0x72617720;  // "raw "
    public const string RawPcmDecoderId = "raw-pcm";

    public static async Task<uint> ReadCodecIdAsync(Stream stream, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] codec = new byte[4];
        await stream.ReadExactlyAsync(codec, token).ConfigureAwait(false);
        uint codecId = BinaryPrimitives.ReadUInt32BigEndian(codec);
        if (codecId is 0 or 1)
            throw AndroidError.WithCode(
                new InvalidDataException("手机未能建立应用音频捕获，请检查 Android 版本及应用支持情况。"),
                AndroidErrorCode.AudioCaptureNotEstablished);
        return codecId;
    }

    public static IAndroidAudioReceiver CreateReceiver(uint codecId, string? preferredDecoder = null)
    {
        return codecId switch
        {
            CodecOpus => preferredDecoder switch
            {
                // If the server fell back to Opus while raw-pcm was preferred, decode using Opus
                // rather than misinterpreting compressed frames as raw PCM samples.
                null or RawPcmDecoderId => new ScrcpyOpusReceiver(),
                AndroidStreamSettings.ConcentusDecoder => new ScrcpyOpusReceiver(AndroidStreamSettings.ConcentusDecoder),
                _ => throw new ArgumentException("Unsupported audio decoder.", nameof(preferredDecoder))
            },
            CodecRaw => preferredDecoder switch
            {
                null or RawPcmDecoderId or AndroidStreamSettings.ConcentusDecoder => new ScrcpyRawPcmReceiver(RawPcmDecoderId),
                _ => throw new ArgumentException("Unsupported audio decoder.", nameof(preferredDecoder))
            },
            _ => throw new InvalidDataException($"Unsupported scrcpy audio codec: 0x{codecId:X8}.")
        };
    }

    public static bool IsRawPcmDecoder(string? decoder) =>
        string.Equals(decoder, RawPcmDecoderId, StringComparison.Ordinal);
}

/// <summary>Receives scrcpy 48 kHz stereo S16LE raw PCM packets without instantiating any codec.</summary>
public sealed class ScrcpyRawPcmReceiver : IAndroidAudioReceiver
{
    private const ulong PacketFlagConfig = 1UL << 63;
    private const ulong PtsMask = (1UL << 62) - 1;
    private const int MaxPacketBytes = 262144;
    private const int InitialPacketBufferBytes = 4096;
    private readonly string? _decoder;

    public ScrcpyRawPcmReceiver(string? decoder = null)
    {
        if (decoder is not (null or ScrcpyAudioProtocol.RawPcmDecoderId))
            throw new ArgumentException("Unsupported raw PCM decoder.", nameof(decoder));
        _decoder = decoder;
    }

    public string DecoderId => _decoder ?? ScrcpyAudioProtocol.RawPcmDecoderId;
    public uint CodecId => ScrcpyAudioProtocol.CodecRaw;
    public long PacketsDecoded { get; private set; }
    public long LastTimestampUs { get; private set; } = -1;
    public long TimestampDiscontinuities { get; private set; }

    public async Task ReceiveAsync(Stream stream, AndroidPcmSource pcm, Action firstFrame, CancellationToken token)
    {
        uint codecId = await ScrcpyAudioProtocol.ReadCodecIdAsync(stream, token).ConfigureAwait(false);
        if (codecId != ScrcpyAudioProtocol.CodecRaw)
            throw new InvalidDataException("Expected scrcpy raw PCM audio stream.");
        await ReceivePacketsAsync(stream, pcm, firstFrame, token).ConfigureAwait(false);
    }

    internal async Task ReceivePacketsAsync(Stream stream, AndroidPcmSource pcm, Action firstFrame, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentNullException.ThrowIfNull(firstFrame);

        pcm.HeaderGain = 1f;
        byte[] header = new byte[12];
        byte[] payload = ArrayPool<byte>.Shared.Rent(InitialPacketBufferBytes);
        float[] decoded = ArrayPool<float>.Shared.Rent(InitialPacketBufferBytes / sizeof(short));
        try
        {
            bool first = true;
            while (true)
            {
                await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                ulong flags = BinaryPrimitives.ReadUInt64BigEndian(header);
                uint size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
                if (size > MaxPacketBytes)
                    throw new InvalidDataException("Invalid scrcpy audio packet length.");

                if ((flags & PacketFlagConfig) != 0)
                {
                    if (size == 0)
                        continue;
                    throw new InvalidDataException("Unsupported non-empty configuration packet in raw PCM audio stream.");
                }

                if (size == 0)
                    throw new InvalidDataException("Invalid scrcpy audio packet length.");
                if ((size & 3) != 0)
                    throw new InvalidDataException("Unaligned raw PCM audio packet length.");

                int byteCount = (int)size;
                if (byteCount > payload.Length)
                {
                    byte[] largerPayload = ArrayPool<byte>.Shared.Rent(byteCount);
                    ArrayPool<byte>.Shared.Return(payload);
                    payload = largerPayload;
                }

                await stream.ReadExactlyAsync(payload.AsMemory(0, byteCount), token).ConfigureAwait(false);

                int sampleCount = byteCount >> 1;
                if (sampleCount > decoded.Length)
                {
                    float[] largerDecoded = ArrayPool<float>.Shared.Rent(sampleCount);
                    ArrayPool<float>.Shared.Return(decoded);
                    decoded = largerDecoded;
                }

                long timestamp = (long)(flags & PtsMask);
                if (LastTimestampUs >= 0 && timestamp < LastTimestampUs)
                {
                    pcm.Clear();
                    TimestampDiscontinuities++;
                }
                LastTimestampUs = timestamp;

                Span<float> floatSpan = decoded.AsSpan(0, sampleCount);
                ConvertPcm16LeToFloat(payload.AsSpan(0, byteCount), floatSpan);
                pcm.Write(floatSpan);
                PacketsDecoded++;
                if (first)
                {
                    first = false;
                    firstFrame();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
            ArrayPool<float>.Shared.Return(decoded);
        }
    }

    Task IAndroidAudioReceiver.ReceivePacketsAsync(Stream stream, AndroidPcmSource pcm, Action firstFrame, CancellationToken token) =>
        ReceivePacketsAsync(stream, pcm, firstFrame, token);

    internal static void ConvertPcm16LeToFloat(ReadOnlySpan<byte> sourceBytes, Span<float> destinationFloats)
    {
        if ((sourceBytes.Length & 1) != 0)
            throw new ArgumentException("Raw PCM byte length must be a multiple of 2.", nameof(sourceBytes));
        int sampleCount = sourceBytes.Length >> 1;
        if (destinationFloats.Length < sampleCount)
            throw new ArgumentException("Destination span is too small for decoded PCM samples.", nameof(destinationFloats));
        if (sampleCount == 0)
            return;

        const float scale = 1f / 32768f;
        int i = 0;

        if (BitConverter.IsLittleEndian && Vector.IsHardwareAccelerated && sampleCount >= Vector<short>.Count)
        {
            ReadOnlySpan<short> sourceShorts = MemoryMarshal.Cast<byte, short>(sourceBytes);
            int shortWidth = Vector<short>.Count;
            int floatWidth = Vector<float>.Count;
            var scaleVector = new Vector<float>(scale);

            for (; i <= sampleCount - shortWidth; i += shortWidth)
            {
                var samples = new Vector<short>(sourceShorts.Slice(i));
                Vector.Widen(samples, out Vector<int> lowInt, out Vector<int> highInt);
                (Vector.ConvertToSingle(lowInt) * scaleVector).CopyTo(destinationFloats.Slice(i));
                (Vector.ConvertToSingle(highInt) * scaleVector).CopyTo(destinationFloats.Slice(i + floatWidth));
            }
        }

        for (; i < sampleCount; i++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(sourceBytes.Slice(i * 2, 2));
            destinationFloats[i] = sample * scale;
        }
    }
}

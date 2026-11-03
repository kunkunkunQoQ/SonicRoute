using System.Buffers.Binary;
using System.Buffers;
using System.Text;
using Concentus;
using Concentus.Structs;

namespace SonicRoute.AndroidAudio;

/// <summary>scrcpy 3.3.4 audio protocol, matching demuxer.c and device/Streamer.java.</summary>
public sealed class ScrcpyOpusReceiver
{
    public long PacketsDecoded { get; private set; }
    public long LastTimestampUs { get; private set; } = -1;
    public long TimestampDiscontinuities { get; private set; }

    public async Task ReceiveAsync(Stream stream, AndroidPcmSource pcm, Action firstFrame, CancellationToken token)
    {
        byte[] codec = new byte[4];
        await stream.ReadExactlyAsync(codec, token).ConfigureAwait(false);
        uint codecId = BinaryPrimitives.ReadUInt32BigEndian(codec);
        if (codecId is 0 or 1) throw new InvalidDataException("手机未能建立应用音频捕获，请检查 Android 版本及应用支持情况。");
        if (codecId != 0x6f707573) throw new InvalidDataException("Expected scrcpy Opus audio stream.");
        byte[] header = new byte[12], payload = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            float[] decoded = new float[5760 * AndroidPcmSource.Channels];
            // Explicit managed decoder: do not load arbitrary native Opus/FFmpeg libraries from PATH.
#pragma warning disable CS0618
            using IOpusDecoder decoder = new OpusDecoder(AndroidPcmSource.SampleRate, AndroidPcmSource.Channels);
#pragma warning restore CS0618
            bool configured = false, first = true;
            int skip = 0;
            while (true)
            {
                await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                ulong flags = BinaryPrimitives.ReadUInt64BigEndian(header);
                uint size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
                if (size is 0 or > 262144) throw new InvalidDataException("Invalid scrcpy audio packet length.");
                if (size > payload.Length)
                {
                    byte[] larger = ArrayPool<byte>.Shared.Rent((int)size);
                    ArrayPool<byte>.Shared.Return(payload);
                    payload = larger;
                }
                await stream.ReadExactlyAsync(payload.AsMemory(0, (int)size), token).ConfigureAwait(false);
                if ((flags & (1UL << 63)) != 0)
                {
                    if (configured) throw new InvalidDataException("Unexpected mid-stream Opus reconfiguration.");
                    if (size < 19 || Encoding.ASCII.GetString(payload, 0, 8) != "OpusHead"
                        || payload[8] > 15 || payload[9] is < 1 or > 2 || payload[18] != 0)
                        throw new InvalidDataException("Unsupported OpusHead.");
                    skip = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(10));
                    short gain = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(16));
                    pcm.HeaderGain = (float)Math.Pow(10, gain / (256.0 * 20));
                    configured = true;
                    continue;
                }
                if (!configured) throw new InvalidDataException("Missing Opus configuration.");
                long timestamp = (long)(flags & ((1UL << 62) - 1));
                // TCP preserves packet order. AudioRecord may rebase its clock while capture starts;
                // discard queued latency at that boundary and keep decoding the ordered Opus stream.
                if (LastTimestampUs >= 0 && timestamp < LastTimestampUs) { pcm.Clear(); TimestampDiscontinuities++; }
                LastTimestampUs = timestamp;
                int frames = decoder.Decode(payload.AsSpan(0, (int)size), decoded, 5760);
                int discarded = Math.Min(skip, frames); skip -= discarded;
                pcm.Write(decoded.AsSpan(discarded * 2, (frames - discarded) * 2));
                PacketsDecoded++;
                if (first) { first = false; firstFrame(); }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(payload); }
    }
}

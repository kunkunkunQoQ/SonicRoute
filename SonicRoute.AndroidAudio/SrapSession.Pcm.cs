using System.Buffers;
using System.Text.Json;

namespace SonicRoute.AndroidAudio;

internal sealed partial class SrapSession
{
    internal const string PcmEncoding = "s16le-48000-stereo-v1";
    private SrapPcmOutput? pcmOutput;
    internal bool SupportsPcm
    {
        get
        {
            var snapshot = Capabilities;
            return snapshot.Allows("audio.route.owned") && snapshot.Entries.TryGetValue("audio.play", out var play)
                && play is { State: "available", Scope: "owned_track" } && play.Limits?.GetValueOrDefault("pcm") == PcmEncoding;
        }
    }

    internal uint AllocateStreamId()
    {
        long id = Interlocked.Add(ref nextStream, 2);
        return id is > 0 and <= uint.MaxValue ? (uint)id : throw new SrapException("busy");
    }

    internal void RegisterPcm(SrapPcmOutput output)
    {
        if (!SupportsPcm) throw new SrapException("unsupported");
        if (Interlocked.CompareExchange(ref pcmOutput, output, null) != null) throw new SrapException("busy");
    }
    internal void RetirePcm(SrapPcmOutput output) => Interlocked.CompareExchange(ref pcmOutput, null, output);

    internal async Task StartPcmAsync(uint id, string target, CancellationToken token)
    {
        if (!SupportsPcm) throw new SrapException("unsupported");
        var response = await RequestAsync("audio.play.start", "audio.play", new() { ["streamId"] = id,
            ["targetKey"] = target, ["encoding"] = PcmEncoding }, token).ConfigureAwait(false);
        if (!response.TryGetProperty("streamId", out var streamField) || !streamField.TryGetUInt32(out uint echoed) || echoed != id
            || SrapClient.Text(response, "encoding") != PcmEncoding || !response.TryGetProperty("credit", out var credit)
            || !credit.TryGetInt32(out int count) || count != SrapPcmOutput.Window) throw new SrapException("invalid_argument");
    }

    internal async Task StopPcmAsync(uint id, CancellationToken token)
    {
        var result = await RequestAsync("stream.stop", null, new() { ["streamId"] = id }, token).ConfigureAwait(false);
        if (!result.TryGetProperty("streamId", out var number) || !number.TryGetUInt32(out uint value) || value != id
            || !result.TryGetProperty("stopped", out var stopped) || stopped.ValueKind != JsonValueKind.True)
            throw new SrapException("io_failure");
    }

    internal async Task WriteDataFrameAsync(uint streamId, ReadOnlyMemory<byte> payload, CancellationToken cancel)
    {
        if (payload.Length is < 1 or > SrapProtocol.MaxPayload) throw new SrapException("invalid_argument");
        int length = SrapProtocol.HeaderSize + payload.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            SrapProtocol.WriteHeader(buffer, SrapFrameType.Data, streamId, payload.Length);
            payload.CopyTo(buffer.AsMemory(SrapProtocol.HeaderSize));
            await writeGate.WaitAsync(cancel).ConfigureAwait(false);
            try { await stream.WriteAsync(buffer.AsMemory(0, length), cancel).ConfigureAwait(false); }
            catch { Dispose(); throw; }
            finally { writeGate.Release(); }
        }
        finally
        {
            buffer.AsSpan(0, length).Clear();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

using System.Buffers;
using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;

namespace SonicRoute.WindowsLink;

// The owning Android session decodes once. Committed destinations receive the same 48 kHz PCM packet.
internal sealed class PhoneLinkAudioPlayer(WindowsLinkService service, LinkExternalAudioDescriptor source,
    string peerId, CancellationToken lifetime) : IPcmPlayer, IFailedPcmPlayer
{
    private WindowsLinkService.ExternalAudioOutput? _output;
    private int _playing;
    private Exception? _failure;
    public event Action<Exception>? Failed;
    internal Exception? Failure => Volatile.Read(ref _failure);

    public void Play()
    {
        _output ??= service.OpenExternalAudioOutputAsync(source, peerId, error =>
        {
            Volatile.Write(ref _failure, error);
            Failed?.Invoke(error);
        }, lifetime).GetAwaiter().GetResult();
        if (Failure is { } error) throw new IOException("The selected computer has disconnected.", error);
        Volatile.Write(ref _playing, 1);
    }
    public void Stop() => Volatile.Write(ref _playing, 0);
    public void Dispose()
    {
        Stop();
        Interlocked.Exchange(ref _output, null)?.Dispose();
    }
    internal static void Send(PhoneLinkAudioPlayer[] players, ReadOnlySpan<float> samples, float gain)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(samples.Length * sizeof(short));
        try
        {
            var bytes = buffer.AsSpan(0, samples.Length * sizeof(short));
            var values = MemoryMarshal.Cast<byte, short>(bytes);
            for (int i = 0; i < samples.Length; ++i)
                values[i] = (short)Math.Clamp(MathF.Round(samples[i] * gain * 32768f), short.MinValue, short.MaxValue);
            foreach (var player in players)
                if (Volatile.Read(ref player._playing) != 0) Volatile.Read(ref player._output)?.Push(bytes);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}

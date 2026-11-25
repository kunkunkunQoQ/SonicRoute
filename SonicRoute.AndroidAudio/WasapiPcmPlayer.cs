using System.IO;
namespace SonicRoute.AndroidAudio;

public interface IPcmPlayer : IDisposable
{
    void Play();
    void Stop();
}

internal interface IWasapiPlaybackSource
{
    PcmWaveFormat WaveFormat { get; }
    int Read(byte[] buffer, int offset, int count);
    event Action? PlaybackBufferRequested;
}

public sealed partial class WasapiPcmPlayer : IPcmPlayer
{
    private readonly AndroidWasapiOutput _output;
    private readonly IWasapiPlaybackSource _source;
    public event Action<Exception>? Failed;
    internal string EndpointId => _output.EndpointId;
    internal int ActualRequestedLatencyMs => _output.LatencyMs;
    internal double DeviceDefaultPeriodMs => _output.DefaultPeriodMs;

    internal WasapiPcmPlayer(IWasapiPlaybackSource source, string? endpointId)
    {
        _source = source;
        _output = new AndroidWasapiOutput(source, endpointId);
        _output.Failed += ex => Failed?.Invoke(ex);
        if (source is AndroidPcmSource pcm) pcm.BeginPlaybackPriming();
    }

    public void Play() => _output.SetPlaying(true);
    internal async Task PlayAndConfirmAsync(CancellationToken token)
    {
        var playing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        // WASAPI primes its first buffer before starting. A second request confirms the output runs even while the source app is paused.
        void Consumed()
        {
            if (++reads == 2) playing.TrySetResult(true);
        }
        void PlaybackFailed(Exception error) => playing.TrySetException(error);
        _source.PlaybackBufferRequested += Consumed;
        Failed += PlaybackFailed;
        try
        {
            Play();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            using var registration = deadline.Token.Register(() => playing.TrySetCanceled());
            try { await playing.Task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new IOException("WASAPI did not start reading playback buffers.", new TimeoutException()); }
        }
        finally
        {
            Failed -= PlaybackFailed;
            _source.PlaybackBufferRequested -= Consumed;
        }
    }
    public void Stop() => _output.SetPlaying(false);
    public void Dispose() => _output.Dispose();
}

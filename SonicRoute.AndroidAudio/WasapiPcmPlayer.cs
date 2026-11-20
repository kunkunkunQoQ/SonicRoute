using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SonicRoute.AndroidAudio;

public interface IPcmPlayer : IDisposable
{
    void Play();
    void Stop();
}

internal interface IWasapiPlaybackSource : IWaveProvider
{
    event Action? PlaybackBufferRequested;
}

public sealed partial class WasapiPcmPlayer : IPcmPlayer
{
    private readonly WasapiDeviceLease _device;
    private readonly WasapiOut _output;
    private readonly IWasapiPlaybackSource _source;
    private int _disposed;
    public event Action<Exception>? Failed;

    internal string EndpointId => _device.Device.ID;

    internal WasapiPcmPlayer(IWasapiPlaybackSource source, string? endpointId)
    {
        _source = source;
        _device = WasapiDeviceLease.Open(endpointId);
        try
        {
            _output = new WasapiOut(_device.Device, AudioClientShareMode.Shared, true, 100);
            _output.Init(source); // shared-mode conversion adapts to the endpoint's mix format
            _output.PlaybackStopped += (_, e) => { if (e.Exception != null) Failed?.Invoke(e.Exception); };
        }
        catch
        {
            try { _output?.Dispose(); } catch { }
            try { _device.Dispose(); } catch { }
            throw;
        }
    }
    public void Play() => _output.Play();
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
    public void Stop() => _output.Stop();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _output.Dispose(); }
        finally { _device.Dispose(); }
    }
}

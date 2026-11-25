#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
using SonicRoute.AirPlay;
using SonicRoute.Core;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

/// <summary>On-demand owner for the AirPlay receiver used by the native Full view.</summary>
internal sealed class AirPlayPreview : IDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private AirPlayReceiver? _receiver;
    private int _disposed;

    internal event Action? Changed;
    internal AirPlayStatus Status => Volatile.Read(ref _receiver)?.Status ?? new(AirPlayStage.Off);
    internal AirPlayAudioOutput? Source => Volatile.Read(ref _receiver)?.Source;
    internal int Port => Volatile.Read(ref _receiver)?.Port ?? 0;

    internal async Task SetReceivingAsync(AppConfig config, bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (enabled)
            {
                if (_receiver is { Status.Stage: not AirPlayStage.Off }) return;
                if (_receiver is { } ended)
                {
                    _receiver = null;
                    await CloseAsync(ended).ConfigureAwait(false);
                }
                string name = AirPlayIdentity.NormalizeName(config.AirPlayReceiverName);
                if (name.Length == 0) name = DefaultName;
                var receiver = new AirPlayReceiver(name, player: static (pcm, endpoint, _) => new WasapiPcmPlayer(pcm, endpoint),
                    preferHevc: config.AirPlayVideoDecoder == 2, requireCode: config.AirPlayRequireCode);
                receiver.Changed += ReceiverChanged;
                receiver.DisplayChanged += ReceiverChanged;
                Volatile.Write(ref _receiver, receiver);
                _ = ObserveAsync(receiver);
            }
            else if (Interlocked.Exchange(ref _receiver, null) is { } receiver)
                await CloseAsync(receiver).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
            NotifyChanged();
        }
    }

    internal void ShowMirror() => Volatile.Read(ref _receiver)?.ShowMirror();
    internal void SetMirrorTopmost(bool value) => Volatile.Read(ref _receiver)?.SetMirrorTopmost(value);
    internal void SetMirrorFullscreen(bool value) => Volatile.Read(ref _receiver)?.SetMirrorFullscreen(value);
    internal void Disconnect() { if (Source is { } source) Volatile.Read(ref _receiver)?.Disconnect(source.Id); }

    private static string DefaultName
    {
        get
        {
            string host = string.Concat(Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(32));
            return host.Length == 0 ? "SonicRoute Preview" : "SonicRoute Preview - " + host;
        }
    }

    private void ReceiverChanged() => NotifyChanged();
    private void NotifyChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch { }
    }

    private async Task ObserveAsync(AirPlayReceiver receiver)
    {
        try { await receiver.Completion.ConfigureAwait(false); }
        catch { }
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(receiver, Volatile.Read(ref _receiver))) return;
            Volatile.Write(ref _receiver, null);
            await CloseAsync(receiver).ConfigureAwait(false);
        }
        finally { _operations.Release(); NotifyChanged(); }
    }

    private async Task CloseAsync(AirPlayReceiver receiver)
    {
        receiver.Changed -= ReceiverChanged;
        receiver.DisplayChanged -= ReceiverChanged;
        await receiver.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _operations.Wait();
        try
        {
            if (Interlocked.Exchange(ref _receiver, null) is { } receiver)
                CloseAsync(receiver).GetAwaiter().GetResult();
        }
        finally { _operations.Release(); }
    }
}
#endif

#if SONICROUTE_PHONE
using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

/// <summary>On-demand owner for the AirPlay receiver used by the native Full view.</summary>
internal sealed class AirPlayPreview : IDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly bool _isPackaged;
    private readonly Func<AndroidPcmSource, string, CancellationToken, IPcmPlayer>? _phonePlayerFactory;
    private AirPlayReceiver? _receiver;
    private AirPlayStatus _inactive = new(AirPlayStage.Off);
    private AirPlayStatus? _transition;
    private int _disposed;

    internal AirPlayPreview(bool isPackaged = false, Func<AndroidPcmSource, string, CancellationToken, IPcmPlayer>? phonePlayerFactory = null)
    {
        _isPackaged = isPackaged;
        _phonePlayerFactory = phonePlayerFactory;
    }

    internal event Action? Changed;
    internal AirPlayStatus Status => Volatile.Read(ref _transition) ?? Volatile.Read(ref _receiver)?.Status ?? Volatile.Read(ref _inactive);
    internal AirPlayAudioOutput? Source => Volatile.Read(ref _receiver)?.Source;
    internal int Port => Volatile.Read(ref _receiver)?.Port ?? 0;

    internal async Task SetReceivingAsync(AppConfig config, bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var stage = Status.Stage;
        if (stage is AirPlayStage.Starting or AirPlayStage.Stopping || enabled == (stage is AirPlayStage.Waiting or AirPlayStage.Connected))
            return;

        var transition = new AirPlayStatus(enabled ? AirPlayStage.Starting : AirPlayStage.Stopping);
        if (Interlocked.CompareExchange(ref _transition, transition, null) != null)
            return;

        try
        {
            NotifyChanged();
            await _operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;

                if (enabled)
                {
                    if (_receiver is { } existing)
                    {
                        if (existing.Status.Stage != AirPlayStage.Off)
                            return;
                        Volatile.Write(ref _receiver, null);
                        await CloseAsync(existing).ConfigureAwait(false);
                    }

                    Volatile.Write(ref _inactive, new(AirPlayStage.Starting));
                    NotifyChanged();

                    try
                    {
                        string name = AirPlayIdentity.NormalizeName(config.AirPlayReceiverName);
                        if (name.Length == 0) name = DefaultName;
                        var receiver = new AirPlayReceiver(
                            name,
                            player: CreatePlayer,
                            preferHevc: config.AirPlayVideoDecoder == 2,
                            requireCode: config.AirPlayRequireCode,
                            useStorePorts: _isPackaged);
                        receiver.Changed += ReceiverChanged;
                        receiver.DisplayChanged += ReceiverChanged;
                        Volatile.Write(ref _receiver, receiver);
                        _ = ObserveAsync(receiver);
                    }
                    catch (Exception ex)
                    {
                        int? networkError = ex switch
                        {
                            SocketException socket => socket.NativeErrorCode,
                            NetworkInformationException network => network.NativeErrorCode,
                            _ => null
                        };
                        System.Diagnostics.Trace.WriteLine($"AirPlay start: {ex.GetType().Name} ({networkError}).");
                        Volatile.Write(ref _inactive, new(
                            AirPlayStage.Failed,
                            ErrorKey: ex is AirPlayNoNetworkException ? "Phone.AirPlayNoIpv4" : "Phone.AirPlayStartError",
                            NetworkError: networkError));
                    }
                }
                else
                {
                    Volatile.Write(ref _inactive, new(AirPlayStage.Off));
                    if (Interlocked.Exchange(ref _receiver, null) is { } receiver)
                        await CloseAsync(receiver).ConfigureAwait(false);
                }
            }
            finally
            {
                _operations.Release();
            }
        }
        finally
        {
            Volatile.Write(ref _transition, null);
            NotifyChanged();
        }
    }

    internal void ShowMirror() => Volatile.Read(ref _receiver)?.ShowMirror();
    internal void SetMirrorTopmost(bool value) => Volatile.Read(ref _receiver)?.SetMirrorTopmost(value);
    internal void SetMirrorFullscreen(bool value) => Volatile.Read(ref _receiver)?.SetMirrorFullscreen(value);
    internal void Disconnect() { if (Source is { } source) Volatile.Read(ref _receiver)?.Disconnect(source.Id); }

    internal static string DefaultName
    {
        get
        {
            string host = string.Concat(Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(32));
            return host.Length == 0 ? "SonicRoute" : "SonicRoute - " + host;
        }
    }

    private IPcmPlayer CreatePlayer(AndroidPcmSource pcm, string? endpoint, CancellationToken token) =>
        AndroidOutputIdentity.IsPhoneOutput(endpoint) && _phonePlayerFactory != null && endpoint != null
            ? _phonePlayerFactory(pcm, endpoint, token)
            : new WasapiPcmPlayer(pcm, endpoint);

    private void ReceiverChanged() => NotifyChanged();
    private void NotifyChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex.GetType().Name); }
    }

    private async Task ObserveAsync(AirPlayReceiver receiver)
    {
        try { await receiver.Completion.ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("AirPlay stopped: " + ex.GetType().Name); }
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(receiver, Volatile.Read(ref _receiver))) return;
            Volatile.Write(ref _inactive, new(AirPlayStage.Off));
            Volatile.Write(ref _receiver, null);
            await CloseAsync(receiver).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
            NotifyChanged();
        }
    }

    private async Task CloseAsync(AirPlayReceiver receiver)
    {
        receiver.Changed -= ReceiverChanged;
        receiver.DisplayChanged -= ReceiverChanged;
        try { await receiver.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("AirPlay stop: " + ex.GetType().Name); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _operations.Wait();
        try
        {
            Volatile.Write(ref _inactive, new(AirPlayStage.Off));
            if (Interlocked.Exchange(ref _receiver, null) is { } receiver)
                CloseAsync(receiver).GetAwaiter().GetResult();
        }
        finally { _operations.Release(); }
    }
}
#endif

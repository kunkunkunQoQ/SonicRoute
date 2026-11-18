#if SONICROUTE_PHONE
using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

internal enum AirPlayVideoPreference { Automatic, H264, PreferHevc }

internal static class AirPlayHost
{
    internal const string AppKey = "airplay:audio";
    private static readonly SemaphoreSlim Operations = new(1, 1);
    private static AirPlayReceiver? _receiver;
    private static AirPlayStatus _inactive = new(AirPlayStage.Off);
    private static int _videoPreference;
    private static int _exiting;
    internal static event Action? Changed;
    internal static event Action? VideoPreferenceChanged;
    internal static AirPlayStatus Status => Volatile.Read(ref _receiver)?.Status ?? Volatile.Read(ref _inactive);
    internal static AirPlayAudioOutput? Source => Volatile.Read(ref _receiver)?.Source;

    internal static AirPlayAudioOutput? Find(string? id) => id != null && Source is { } source && source.Id == id ? source : null;
    internal static void Disconnect(string id) => Volatile.Read(ref _receiver)?.Disconnect(id);
    internal static void ShowMirror() => Volatile.Read(ref _receiver)?.ShowMirror();

    internal static AirPlayVideoPreference VideoPreference
    {
        get => (AirPlayVideoPreference)Volatile.Read(ref _videoPreference);
        set { if (Interlocked.Exchange(ref _videoPreference, (int)value) != (int)value) VideoPreferenceChanged?.Invoke(); }
    }

    internal static Task StartAsync() => Task.Run(async () =>
    {
        await Operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _exiting) != 0 || _receiver != null) return;
            Volatile.Write(ref _inactive, new(AirPlayStage.Starting));
            Publish();
            try
            {
                string host = string.Concat(Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(32));
                string name = host.Length == 0 ? "SonicRoute" : "SonicRoute - " + host;
                var receiver = new AirPlayReceiver(name, player: CreatePlayer,
                    preferHevc: VideoPreference == AirPlayVideoPreference.PreferHevc);
                receiver.Changed += Publish;
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
                Volatile.Write(ref _inactive, new(AirPlayStage.Failed,
                    ErrorKey: ex is AirPlayNoNetworkException ? "Phone.AirPlayNoIpv4" : "Phone.AirPlayStartError", NetworkError: networkError));
            }
            Publish();
        }
        finally { Operations.Release(); }
    });

    private static IPcmPlayer CreatePlayer(AndroidPcmSource pcm, string? endpoint, CancellationToken token) =>
        AndroidOutputIdentity.IsPhoneOutput(endpoint) ? AndroidAudioHost.CreatePhonePcmPlayer(pcm, endpoint!, token)
            : new WasapiPcmPlayer(pcm, endpoint);

    internal static Task StopAsync() => Task.Run(async () =>
    {
        await Operations.WaitAsync().ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _inactive, new(AirPlayStage.Off));
            var receiver = Interlocked.Exchange(ref _receiver, null);
            if (receiver != null) await CloseAsync(receiver).ConfigureAwait(false);
            Publish();
        }
        finally { Operations.Release(); }
    });

    private static async Task ObserveAsync(AirPlayReceiver receiver)
    {
        try { await receiver.Completion.ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("AirPlay stopped: " + ex.GetType().Name); }
        await Operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(receiver, _receiver)) return;
            Volatile.Write(ref _inactive, new(AirPlayStage.Off));
            Volatile.Write(ref _receiver, null);
            await CloseAsync(receiver).ConfigureAwait(false);
            Publish();
        }
        finally { Operations.Release(); }
    }

    private static async Task CloseAsync(AirPlayReceiver receiver)
    {
        receiver.Changed -= Publish;
        try { await receiver.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("AirPlay stop: " + ex.GetType().Name); }
    }

    private static void Publish()
    {
        if (Changed is { } handlers)
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex.GetType().Name); }
        ApplicationAudio.NotifyChanged();
    }

    internal static void Shutdown()
    {
        Interlocked.Exchange(ref _exiting, 1);
        if (_receiver == null && Status.Stage != AirPlayStage.Starting) return;
        _ = StopAsync().Wait(TimeSpan.FromSeconds(2));
    }
}
#endif

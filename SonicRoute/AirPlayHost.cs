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

namespace SonicRoute;

internal enum AirPlayVideoPreference { Automatic, H264, PreferHevc }

internal static class AirPlayHost
{
    internal const string AppKey = "airplay:audio";
    internal const string WindowsDragFormat = "SonicRoute.AirPlayToWindows.v1";
    private static readonly SemaphoreSlim Operations = new(1, 1);
    private static AirPlayReceiver? _receiver;
    private static AirPlayStatus _inactive = new(AirPlayStage.Off);
    private static AirPlayStatus? _transition;
    private static int _exiting;
    private static AirPlayAudioOutput? _publishedSource;
    internal static event Action? Changed;
    internal static event Action? PreferencesChanged;
    internal static AirPlayStatus Status => Volatile.Read(ref _transition) ?? Volatile.Read(ref _receiver)?.Status ?? Volatile.Read(ref _inactive);
    internal static AirPlayAudioOutput? Source => Volatile.Read(ref _receiver)?.Source;

    internal static AirPlayAudioOutput? Find(string? id) => id != null && Source is { } source && source.Id == id ? source : null;
    internal static void Disconnect(string id) => Volatile.Read(ref _receiver)?.Disconnect(id);
    internal static void ShowMirror() => Volatile.Read(ref _receiver)?.ShowMirror();
    internal static void SetMirrorTopmost(bool value) => Volatile.Read(ref _receiver)?.SetMirrorTopmost(value);
    internal static void SetMirrorFullscreen(bool value) => Volatile.Read(ref _receiver)?.SetMirrorFullscreen(value);

    internal static string DefaultName { get; } = GetDefaultName();
    internal static (string Name, AirPlayVideoPreference Video, bool RequireCode) Preferences
    {
        get
        {
            var config = ConfigService.Load();
            var video = config.AirPlayVideoDecoder is >= 0 and <= (int)AirPlayVideoPreference.PreferHevc
                ? (AirPlayVideoPreference)config.AirPlayVideoDecoder : AirPlayVideoPreference.Automatic;
            return (AirPlayIdentity.NormalizeName(config.AirPlayReceiverName), video, config.AirPlayRequireCode);
        }
    }

    internal static void SavePreferences(string name, AirPlayVideoPreference video, bool requireCode)
    {
        var config = ConfigService.Load();
        name = AirPlayIdentity.NormalizeName(name);
        if (config.AirPlayReceiverName == name && config.AirPlayVideoDecoder == (int)video && config.AirPlayRequireCode == requireCode) return;
        config.AirPlayReceiverName = name;
        config.AirPlayVideoDecoder = (int)video;
        config.AirPlayRequireCode = requireCode;
        ConfigService.QueueSave(config);
        PreferencesChanged?.Invoke();
    }

    private static string GetDefaultName()
    {
        string host = string.Concat(Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(32));
        return host.Length == 0 ? "SonicRoute" : "SonicRoute - " + host;
    }

    internal static async Task SetReceivingAsync(bool enabled)
    {
        var stage = Status.Stage;
        if (stage is AirPlayStage.Starting or AirPlayStage.Stopping || enabled == (stage is AirPlayStage.Waiting or AirPlayStage.Connected)) return;
        var transition = new AirPlayStatus(enabled ? AirPlayStage.Starting : AirPlayStage.Stopping);
        if (Interlocked.CompareExchange(ref _transition, transition, null) != null) return;
        try
        {
            // Publish the intent before scheduling network work so every open switch disables together.
            PublishDisplay();
            await (enabled ? StartAsync() : StopAsync()).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _transition, null);
            PublishDisplay();
        }
    }

    internal static Task StartAsync()
    {
        var (configuredName, video, requireCode) = Preferences;
        return Task.Run(async () =>
        {
            await Operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _exiting) != 0) return;
                if (_receiver is { } previous)
                {
                    if (previous.Status.Stage != AirPlayStage.Off) return;
                    // A timed-out receiver may have published Off just before its observer acquires Operations.
                    Volatile.Write(ref _receiver, null);
                    await CloseAsync(previous).ConfigureAwait(false);
                }
                Volatile.Write(ref _inactive, new(AirPlayStage.Starting));
                Publish();
                try
                {
                    string name = configuredName.Length == 0 ? DefaultName : configuredName;
                    var receiver = new AirPlayReceiver(name, player: CreatePlayer,
                        preferHevc: video == AirPlayVideoPreference.PreferHevc, requireCode: requireCode,
                        useStorePorts: AppRuntimeIdentity.Current.IsPackaged);
                    receiver.Changed += Publish;
                    receiver.DisplayChanged += PublishDisplay;
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
    }

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
        receiver.DisplayChanged -= PublishDisplay;
        try { await receiver.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("AirPlay stop: " + ex.GetType().Name); }
    }

    private static void Publish()
    {
        PublishDisplay();
        var source = Source;
        var previous = Interlocked.Exchange(ref _publishedSource, source);
        ApplicationAudio.NotifySourceChanged(ReferenceEquals(previous, source) ? source?.Id : null);
    }

    private static void PublishDisplay()
    {
        if (Changed is { } handlers)
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex.GetType().Name); }
    }

    internal static void Shutdown()
    {
        Interlocked.Exchange(ref _exiting, 1);
        if (_receiver == null && Status.Stage != AirPlayStage.Starting) return;
        _ = StopAsync().Wait(TimeSpan.FromSeconds(2));
    }
}
#endif

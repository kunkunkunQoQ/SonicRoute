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
    private readonly AndroidAudioSettingsStore? _preferences;
    private readonly AppConfig? _config;
    private readonly Func<Action, bool>? _tryPost;
    private AirPlayReceiver? _receiver;
    private AirPlayStatus _inactive = new(AirPlayStage.Off);
    private AirPlayStatus? _transition;
    private int _disposed;
    private bool _watchingNetwork;
    private int _networkQueued;
    private int _checkNetworkOnRelease;
    private readonly object _recoveryGate = new();
    private Task _outputRecovery = Task.CompletedTask;
    private bool _recoveringOutputs, _outputRecoveryPending;

    internal AirPlayPreview(bool isPackaged = false, Func<AndroidPcmSource, string, CancellationToken, IPcmPlayer>? phonePlayerFactory = null,
        AndroidAudioSettingsStore? preferences = null, AppConfig? config = null, Func<Action, bool>? tryPost = null)
    {
        _isPackaged = isPackaged;
        _phonePlayerFactory = phonePlayerFactory;
        _preferences = preferences;
        _config = config;
        _tryPost = tryPost;
    }

    internal event Action? Changed;
    internal AirPlayStatus Status => Volatile.Read(ref _transition) ?? Volatile.Read(ref _receiver)?.Status ?? Volatile.Read(ref _inactive);
    internal AirPlayAudioOutput? Source => Volatile.Read(ref _receiver)?.Source;
    internal int Port => Volatile.Read(ref _receiver)?.Port ?? 0;
    internal bool Enabled => _preferences?.AirPlayEnabled ?? Status.Stage is AirPlayStage.Waiting or AirPlayStage.Connected;

    internal async Task SetReceivingAsync(AppConfig config, bool enabled, bool restart = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var stage = Status.Stage;
        if (stage is AirPlayStage.Starting or AirPlayStage.Stopping)
            return;
        if (!restart && enabled == (stage is AirPlayStage.Waiting or AirPlayStage.Connected) && enabled == Enabled)
            return;

        var transition = new AirPlayStatus(enabled ? AirPlayStage.Starting : AirPlayStage.Stopping);
        if (Interlocked.CompareExchange(ref _transition, transition, null) != null)
            return;

        try
        {
            _preferences?.SetAirPlayEnabled(enabled);
            WatchNetwork(enabled);
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
                        if (existing.Status.Stage != AirPlayStage.Off && (!restart || existing.Status.Stage == AirPlayStage.Connected))
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
                            useStorePorts: _isPackaged,
                            preferences: _preferences,
                            resolveOutputs: ExternalAudioBridge.ResolveAvailableOutputs);
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
    internal void Disconnect(string? expectedSource = null)
    {
        if (Source is { } source && (expectedSource == null || expectedSource == source.Id))
            Volatile.Read(ref _receiver)?.Disconnect(source.Id);
    }

    private void WatchNetwork(bool enabled)
    {
        if (_config == null || _tryPost == null || _watchingNetwork == enabled) return;
        if (enabled) NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        else NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _watchingNetwork = enabled;
    }

    private void OnNetworkChanged(object? sender, EventArgs args)
    {
        if (_tryPost == null || Interlocked.Exchange(ref _networkQueued, 1) != 0) return;
        if (!_tryPost(() =>
        {
            Volatile.Write(ref _networkQueued, 0);
            if (Volatile.Read(ref _disposed) != 0 || !Enabled || _config == null) return;
            var stage = Status.Stage;
            if (stage == AirPlayStage.Connected)
            {
                Volatile.Write(ref _checkNetworkOnRelease, 1);
                return;
            }
            if (stage is AirPlayStage.Off or AirPlayStage.Failed
                || stage == AirPlayStage.Waiting && Volatile.Read(ref _receiver)?.MatchesInterfaces() == false)
                _ = SetReceivingAsync(_config, enabled: true, restart: true);
        })) Volatile.Write(ref _networkQueued, 0);
    }

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

    private void ReceiverChanged()
    {
        if (Status.Stage == AirPlayStage.Waiting && Interlocked.Exchange(ref _checkNetworkOnRelease, 0) != 0)
            OnNetworkChanged(null, EventArgs.Empty);
        NotifyChanged();
    }

    internal void QueueOutputRecovery()
    {
        if (Source?.PreferenceKey?.StartsWith(ExternalAudioPreference.AirPlayPrefix + "id:", StringComparison.Ordinal) != true) return;
        lock (_recoveryGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _outputRecoveryPending = true;
            if (_recoveringOutputs) return;
            _recoveringOutputs = true;
            _outputRecovery = Task.Run(RestoreOutputsAsync);
        }
    }

    private async Task RestoreOutputsAsync()
    {
        while (true)
        {
            lock (_recoveryGate)
            {
                if (!_outputRecoveryPending || Volatile.Read(ref _disposed) != 0)
                {
                    _recoveringOutputs = false;
                    return;
                }
                _outputRecoveryPending = false;
            }
            try
            {
                if (Source is not { } source || _preferences?.FindExternalAudio(source.PreferenceKey) is not { Visible: true } choice) continue;
                var available = ExternalAudioBridge.ResolveAvailableOutputs(choice.Outputs);
                if (!((IMultiOutputAudioSession)source).GetOutputs().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(available))
                    await source.RestoreOutputsAsync(available, () => ReferenceEquals(Source, source)
                        && ReferenceEquals(_preferences.FindExternalAudio(choice.Key)?.Outputs, choice.Outputs)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { ExceptionDiagnostics.Record("AirPlay.RecoverOutputs", error); }
        }
    }

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
        WatchNetwork(false);
        _operations.Wait();
        try
        {
            Volatile.Write(ref _inactive, new(AirPlayStage.Off));
            if (Interlocked.Exchange(ref _receiver, null) is { } receiver)
                CloseAsync(receiver).GetAwaiter().GetResult();
        }
        finally { _operations.Release(); }
        _outputRecovery.GetAwaiter().GetResult();
    }
}
#endif

namespace SonicRoute.AndroidAudio;

/// <summary>Reads attachments and listens for changes without admitting them to a profile.</summary>
internal interface IPhoneTransportDiscovery
{
    Task<IReadOnlyList<AndroidDevice>> ReadAsync(CancellationToken token);
    Task TrackAsync(Action<IReadOnlyList<AndroidDevice>> changed, CancellationToken token);
}

internal sealed class AdbTransportDiscovery(AdbClient adb) : IPhoneTransportDiscovery
{
    private readonly AndroidWirelessConnection _wireless = new(adb);

    public Task<IReadOnlyList<AndroidDevice>> ReadAsync(CancellationToken token) => adb.DevicesAsync(token, deduplicate: false);
    public Task TrackAsync(Action<IReadOnlyList<AndroidDevice>> changed, CancellationToken token) => adb.TrackDevicesAsync(changed, token);
    internal Task<AndroidConnectionResult> ConnectAsync(string address, CancellationToken token) => _wireless.ConnectAsync(address, token);
    internal Task<AndroidConnectionResult> ReconnectAsync(AndroidDevice device, string? address, CancellationToken token,
        IReadOnlyList<AndroidDevice>? devices = null, bool retryMissingIdentity = true) =>
        _wireless.ReconnectAsync(device, address, token, devices, retryMissingIdentity);
    internal Task<AndroidPairingResult> PairCodeAsync(string address, string code, CancellationToken token) => _wireless.PairCodeAsync(address, code, token);
    internal Task<AndroidPairingResult> PairQrAsync(AndroidQrPairing qr, CancellationToken token) => _wireless.PairQrAsync(qr, token);
    internal Task<AndroidConnectionResult> FinishPairingAsync(AndroidPairingResult pairing, CancellationToken token) =>
        _wireless.FinishPairingAsync(pairing, token);
    internal Task<AndroidConnectionResult> EnrichReadyResultAsync(AndroidConnectionResult result, bool retryMissingIdentity, CancellationToken token) =>
        _wireless.EnrichReadyResultAsync(result, retryMissingIdentity, token);
    internal Task UnpairAsync(AndroidDevice device, CancellationToken token) => _wireless.UnpairAsync(device, token);
    internal Task DisconnectAsync(AndroidDevice device, CancellationToken token) => _wireless.DisconnectAsync(device, token);
    internal Task<IReadOnlyList<AndroidMdnsService>> DiscoverSavedAsync(CancellationToken token) =>
        AndroidMdnsDiscovery.DiscoverConnectionsAsync(token);
    internal Task WatchSavedAsync(Func<AndroidMdnsService, Task> discovered, CancellationToken token) =>
        AndroidMdnsDiscovery.WatchConnectionsAsync(discovered, token);
}

/// <summary>Product operations consume an attachment resolved by the connection manager.</summary>
internal interface IPhoneOperationPort
{
    Task<IReadOnlyList<AndroidApp>> AppsAsync(AndroidDevice device, CancellationToken token, IReadOnlyList<string>? packages = null);
    Task<IReadOnlyList<AndroidOutputSink>> OutputsAsync(AndroidDevice device, CancellationToken token);
    Task<AndroidEncoderCatalog> EncodersAsync(AndroidDevice device, CancellationToken token);
    void InvalidateEncoders(AndroidDevice device);
    bool CanWatchMedia(AndroidDevice device);
    IPhoneMediaSession OpenMediaSession(AndroidDevice device);
    bool CanSyncClipboard(AndroidDevice device);
    IAndroidClipboardClient OpenClipboard(AndroidDevice device);
    IPhoneFileOperations OpenFiles(AndroidDevice device);
    string? FileRoot(AndroidDevice device);
    bool CanWriteFiles(AndroidDevice device);
    AndroidAudioSession CreateCaptureSession(Func<AndroidDevice, AndroidStreamSettings> settings, Func<string, AndroidDevice?> resolvePhone);
    IVirtualDisplayClient CreateDisplay(Func<AndroidAudioSettingsStore> settings);
    AndroidPhonePcmPlayer CreatePlayback(AndroidDevice device, AndroidPcmSource pcm, string sink, CancellationToken token,
        Action<AndroidDevice, IReadOnlyList<AndroidOutputSink>> outputsObserved, CancellationToken attachment);
    bool CanListApps(AndroidDevice device);
    bool CanListOutputs(AndroidDevice device);
    bool CanPlayPcm(AndroidDevice device);
    AppCapturePermission? AppCapture(AndroidDevice device) => null;
    bool CanViewSharedScreen(AndroidDevice device) => false;
    bool NeedsScreenConsent(AndroidDevice device) => false;
    Task ViewSharedScreenAsync(AndroidDevice device, string title, CancellationToken token) => throw new SrapException("unsupported");
    bool CanControlVolume(AndroidDevice device);
    bool CanTestRoute(AndroidDevice device);
    Task<int> VolumeAsync(AndroidDevice device, int? percent, CancellationToken token);
    Task<string> TestRouteAsync(AndroidDevice device, string target, CancellationToken token);
}

internal interface IPhoneMediaSession
{
    event Action<AndroidConnectionQuality>? ConnectionQualityChanged;
    Task WatchAsync(AndroidDevice device, Action<AndroidMediaState> changed, CancellationToken token, bool measureLatency = true);
    Task SendAsync(string command, CancellationToken token);
    Task SeekAsync(string package, long positionMs, CancellationToken token);
}

internal interface IPhoneFileOperations
{
    Task<List<AndroidStorageFile>> ListAsync(string folder, CancellationToken token);
    Task<string> SendAsync(string path, string destination, CancellationToken token);
    Task<string> ReceiveAsync(string folder, string name, string destination, CancellationToken token);
}

internal sealed class AdbOperationAdapter(AdbClient adb) : IPhoneOperationPort, IAsyncDisposable
{
    private readonly VirtualDisplayRuntime _displayRuntime = new(adb);

    public Task<IReadOnlyList<AndroidApp>> AppsAsync(AndroidDevice device, CancellationToken token, IReadOnlyList<string>? packages = null) =>
        adb.AppsAsync(device, token, packages);
    public Task<IReadOnlyList<AndroidOutputSink>> OutputsAsync(AndroidDevice device, CancellationToken token) => adb.OutputsAsync(device, token);
    public Task<AndroidEncoderCatalog> EncodersAsync(AndroidDevice device, CancellationToken token) => adb.EncodersAsync(device, token);
    public void InvalidateEncoders(AndroidDevice device) => adb.InvalidateEncoders(device);
    public bool CanWatchMedia(AndroidDevice device) => !device.IsApp;
    public IPhoneMediaSession OpenMediaSession(AndroidDevice device) => new AndroidMediaSessionClient(adb);
    public bool CanSyncClipboard(AndroidDevice device) => !device.IsApp;
    public IAndroidClipboardClient OpenClipboard(AndroidDevice device) => OpenClipboard(device, null);
    internal IAndroidClipboardClient OpenClipboard(AndroidDevice device, Action? requireCurrent) => device.IsApp
        ? throw new SrapException("unsupported") : new AndroidClipboardClient(adb) { RequireCurrent = requireCurrent };
    public IPhoneFileOperations OpenFiles(AndroidDevice device) => OpenFiles(device, null);
    internal IPhoneFileOperations OpenFiles(AndroidDevice device, Action? requireCurrent) => device.IsApp
        ? throw new SrapException("unsupported") : new AndroidFileTransfer(adb, device.CommandTarget, requireCurrent);
    public string? FileRoot(AndroidDevice device) => device.IsApp ? null : AndroidFileTransfer.Storage;
    public bool CanWriteFiles(AndroidDevice device) => !device.IsApp;
    public AndroidAudioSession CreateCaptureSession(Func<AndroidDevice, AndroidStreamSettings> settings, Func<string, AndroidDevice?> resolvePhone) =>
        new(adb, settings, resolvePhone);
    public IVirtualDisplayClient CreateDisplay(Func<AndroidAudioSettingsStore> settings) => new InProcessVirtualDisplayClient(adb, _displayRuntime, settings);
    public AndroidPhonePcmPlayer CreatePlayback(AndroidDevice device, AndroidPcmSource pcm, string sink, CancellationToken token,
        Action<AndroidDevice, IReadOnlyList<AndroidOutputSink>> outputsObserved, CancellationToken attachment) =>
        new(adb, device, pcm, sink, token, outputsObserved, attachment);
    internal PcToPhoneAudioManager CreatePcPlayback(Func<int, IPcProcessSilenceLease?>? silenceLocalProcess) => new(adb, silenceLocalProcess);
    public bool CanListApps(AndroidDevice device) => !device.IsApp;
    public bool CanListOutputs(AndroidDevice device) => !device.IsApp;
    public bool CanPlayPcm(AndroidDevice device) => !device.IsApp;
    public bool CanControlVolume(AndroidDevice device) => false;
    public bool CanTestRoute(AndroidDevice device) => false;
    public Task<int> VolumeAsync(AndroidDevice device, int? percent, CancellationToken token) => throw new SrapException("unsupported");
    public Task<string> TestRouteAsync(AndroidDevice device, string target, CancellationToken token) => throw new SrapException("unsupported");
    public ValueTask DisposeAsync() => adb.DisposeAsync();
}

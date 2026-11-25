#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute.Native.Product;

internal sealed class PhonePreview : IDisposable
{
    private readonly DirectAdbTransport transport;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken token;
    private readonly SemaphoreSlim scans = new(1, 1);
    private Task? tracking;
    private int closed;
    internal readonly AdbClient Adb;
    internal readonly AndroidPhoneStore Store;
    internal readonly AndroidAudioSettingsStore Settings;
    internal readonly AndroidDeviceDiscovery Discovery;
    internal readonly AndroidConnectionManager Connections;
    internal readonly AndroidWirelessConnection Wireless;
    internal readonly AndroidAudioManager Sources;
    internal readonly PcToPhoneAudioManager Pc;
    internal readonly AndroidVirtualDisplayManager Displays;
    internal readonly AndroidFileTransfers Transfers = new();
    internal readonly PhoneClipboard Clipboard;
    internal event Action? Changed;
    internal string? Error { get; private set; }

    internal PhonePreview(string root, NativeApplicationWindow shell, IMainThread owner)
    {
        token = lifetime.Token;
        transport = new DirectAdbTransport(new AdbKeyStore(Path.Combine(root, "AdbIdentity")), ownsKeyStore: true);
        Adb = new(new AndroidRuntimeBundle(Path.Combine(root, "PhoneRuntime")), transport);
        Store = new(Path.Combine(root, "android-phones.json"), () => L10n.T("Phone.DefaultName"));
        Settings = new(Path.Combine(root, "android-audio.json"));
        Discovery = new(cancel => Adb.DevicesAsync(cancel, deduplicate: false));
        Connections = new(Store, () => Discovery, Settings.MigrateConnectionPreferences);
        Wireless = new(Adb);
        Sources = new(() => new AndroidAudioSession(Adb, Settings.GetStreaming, Connections.ResolveKey), Settings,
            (phone, app) => Store.IsSmartVolumeEnabled(phone, app));
        Pc = new(() => Adb);
        Pc.OutputsObserved += (device, outputs) => Store.UpdateOutputs(device, outputs);
        var runtime = new VirtualDisplayRuntime(Adb);
        Displays = new(() => new InProcessVirtualDisplayClient(Adb, runtime, () => Settings));
        Clipboard = new(Adb, shell, owner);
        Sources.Changed += Notify;
        Discovery.Changed += Observe;
        Store.Changed += Notify;
        Pc.Changed += Notify;
        Transfers.Changed += _ => Notify();
        Displays.ResourcesRetired += Notify;
    }

    internal CancellationToken Lifetime => token;

    internal void StartTracking()
    {
        if (tracking == null || tracking.IsCompleted) tracking = Task.Run(TrackAsync);
    }

    private async Task TrackAsync()
    {
        try
        {
            await Adb.TrackDevicesAsync(devices =>
            {
                Discovery.RetirePendingScan();
                _ = RefreshAfterDeviceChangeAsync();
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Error = error.Message; Notify(); }
    }

    private async Task RefreshAfterDeviceChangeAsync()
    {
        try { await RefreshAsync(restore: true).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Error = error.Message; Notify(); }
    }

    internal async Task RefreshAsync(bool restore = false)
    {
        await scans.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var devices = await Discovery.ReadAsync(deadline.Token).ConfigureAwait(false);
            Error = DirectAdbTransport.UsbAccessError(devices, null) is { } accessError ? accessError.ToString() : null;
            if (!restore) return;

            foreach (var route in Settings.GetSavedSources())
            {
                var online = Connections.ResolveKey(route.DeviceKey);
                if (online == null || Sources.Sources.Any(s => s.App.Package == route.Package && AndroidDeviceIdentity.Matches(s.Device, online))) continue;
                var app = Store.FindProfile(online)?.RetainedApps.FirstOrDefault(a => a.Package == route.Package)
                    ?? new AndroidApp(route.Package, route.Package, -1, "");
                try { await Sources.ResumeAsync(online, app, route.Outputs).ConfigureAwait(false); }
                catch (Exception error) { ExceptionDiagnostics.Record("NativePhone.Resume", error); }
            }
        }
        finally { scans.Release(); Notify(); }
    }

    internal async Task DiscoverAndConnectAsync(string address, CancellationToken cancel)
    {
        long revision = Connections.Revision;
        var result = await Wireless.ConnectAsync(address, cancel).ConfigureAwait(false);
        Accept(result, revision);
    }

    internal async Task PairAsync(string address, string code, CancellationToken cancel)
    {
        long revision = Connections.Revision;
        var paired = await Wireless.PairCodeAsync(address, code, cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        Accept(await Wireless.FinishPairingAsync(paired, cancel).ConfigureAwait(false), revision);
    }

    internal async Task PairAsync(AndroidQrPairing qr, CancellationToken cancel)
    {
        long revision = Connections.Revision;
        var paired = await Wireless.PairQrAsync(qr, cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        Accept(await Wireless.FinishPairingAsync(paired, cancel).ConfigureAwait(false), revision);
    }

    internal async Task ReconnectAsync(AndroidDevice device)
    {
        long revision = Connections.Revision;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var result = await Wireless.ReconnectAsync(device, deadline.Token).ConfigureAwait(false);
        Accept(result, revision);
    }

    private void Accept(AndroidConnectionResult result, long revision)
    {
        if (token.IsCancellationRequested || !Connections.Admit(result, revision))
            throw new InvalidOperationException("The connection changed while this request was running.");
        Discovery.Publish(result.Devices);
        Notify();
    }

    internal async Task DisconnectAsync(AndroidDevice device, bool unpair)
    {
        Connections.SetDisconnected(device, true);
        await Task.WhenAll(Pc.StopPhoneAsync(device), Sources.RemovePhoneAsync(device), Displays.CloseDeviceAsync(device)).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        if (unpair) await Wireless.UnpairAsync(device, deadline.Token).ConfigureAwait(false);
        else if (device.IsWifi) await Wireless.DisconnectAsync(device, deadline.Token).ConfigureAwait(false);
        Discovery.RemoveTransport(device.CommandTarget);
        Notify();
    }

    internal bool StartTransfer(AndroidDevice device, IReadOnlyList<string> paths, string destination, string? remoteFolder)
    {
        var current = Connections.Get(device);
        return current.Profile != null && current.Device is { } confirmed &&
            Transfers.TryStart(current.Profile.Id, new AndroidFileTransfer(Adb, confirmed.CommandTarget),
                paths, destination, remoteFolder, () => Connections.IsCurrent(confirmed), Connections.OperationToken(confirmed), out _);
    }

    internal async Task<IVirtualDisplayClient> OpenDisplayAsync(AndroidDevice device, AndroidApp app, int x, int y)
    {
        var display = await Displays.OpenAsync(device, app, x, y).ConfigureAwait(false);
        if (Sources.Sources.Any(s => s.App.Package == app.Package && AndroidDeviceIdentity.Matches(s.Device, device))) return display;
        var source = await Sources.AddAsync(device, app, keepPhoneAudio: false, displayOwned: true).ConfigureAwait(false);
        _ = ReleaseDisplaySourceAsync(display, source);
        return display;
    }

    private async Task ReleaseDisplaySourceAsync(IVirtualDisplayClient display, AndroidAudioSource source)
    {
        try { await display.Completion.ConfigureAwait(false); }
        catch (Exception error) { ExceptionDiagnostics.Record("NativePhone.Display", error); }
        finally { await Sources.ReleaseDisplayAudioAsync(source).ConfigureAwait(false); }
    }

    private void Observe()
    {
        if (token.IsCancellationRequested) return;
        try { Connections.ObserveCurrent(); } catch (Exception error) { Error = error.Message; }
        Notify();
    }

    private void Notify()
    {
        if (Volatile.Read(ref closed) == 0) Changed?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        Clipboard.Dispose();
        lifetime.Cancel();
        Task.Run(async () =>
        {
            await Transfers.CloseAsync().ConfigureAwait(false);
            if (tracking != null) await tracking.ConfigureAwait(false);
            Sources.BeginShutdown();
            await Task.WhenAll(Displays.DisposeAsync().AsTask(), Sources.DisposeAsync().AsTask(), Pc.DisposeAsync().AsTask()).ConfigureAwait(false);
            await Store.FlushAsync().ConfigureAwait(false);
            await Settings.FlushAsync().ConfigureAwait(false);
            await Adb.DisposeAsync().ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
        }).GetAwaiter().GetResult();
        lifetime.Dispose();
    }
}

internal sealed partial class ApplicationHost
{
    private PhonePreview? phone;
    internal bool HasPhone => phone != null;
    internal PhonePreview Phone => phone ??= new(_environment.DataDirectory, _window, this);
    internal AirPlayPreview AirPlay => airPlay ??= new();
    private AirPlayPreview? airPlay;

    private void RestoreFull()
    {
        string settingsPath = Path.Combine(_environment.DataDirectory, "android-audio.json");
        if (!File.Exists(settingsPath)) return;
        var settings = new AndroidAudioSettingsStore(settingsPath);
        if (settings.GetSavedSources().Length != 0) Phone.StartTracking();
    }

    private void StopFull()
    {
        phone?.Dispose();
        airPlay?.Dispose();
    }

    private void FullSignal(ShellSignal signal)
    {
        if (signal.Kind == ShellSignalKind.Clipboard) phone?.Clipboard.OnClipboardChanged();
        if (signal.Kind is ShellSignalKind.Power or ShellSignalKind.Session) phone?.StartTracking();
    }
}
#endif

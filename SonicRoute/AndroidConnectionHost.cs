#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

// Application-facing adapter for the independent AndroidConnectionManager.
// It owns only user-triggered refresh and bounded recovery, not phone UI state.
internal static class PhoneAudioWindowCache
{
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static readonly Dictionary<string, CancellationTokenSource> Recovering = new(StringComparer.Ordinal);
    private static int _visibleUsbPanels;
    private static CancellationTokenSource? _usbPanelScan;
    private static CancellationTokenSource? _panelWireless;
    private static AndroidDeviceDiscovery _discovery = new(token => AndroidAudioHost.Adb.DevicesAsync(token, deduplicate: false));
    private static readonly Lazy<AndroidConnectionManager> LazyManager = new(() => new(AndroidAudioHost.Phone,
        () => Discovery, (oldDevice, newDevice) => AndroidAudioHost.PrepareStreamingAsync(default).GetAwaiter().GetResult()
            .MigrateConnectionPreferences(oldDevice, newDevice)));
    static PhoneAudioWindowCache() => _discovery.Changed += DiscoveryChanged;
    internal static AndroidConnectionManager Manager => LazyManager.Value;
    internal static AndroidDeviceDiscovery Discovery
    {
        get => _discovery;
        set
        {
            _discovery.Changed -= DiscoveryChanged;
            _discovery = value;
            _discovery.Changed += DiscoveryChanged;
            if (value.CurrentDevices != null) DiscoveryChanged();
        }
    }
    internal static Func<AndroidWirelessConnection> CreateWirelessConnection { get; set; } = () => new(AndroidAudioHost.Adb);
    internal static Func<Action<string>, Action, CancellationToken, Task> WatchPanelWireless { get; set; } =
        (lost, changed, token) => AndroidAudioHost.Adb.WatchPanelWirelessAsync(lost, changed, token);
    internal static Func<IReadOnlySet<string>?> ReadUsbInterfacePaths { get; set; } = AdbDevicePresence.QueryUsbInterfacePaths;
    internal static IReadOnlyList<AndroidDevice>? Devices => Discovery.Devices;
    internal static AndroidDevice? GetOnlineDevice(AndroidDevice? device) => device == null ? null : Manager.Resolve(device);
    internal static IReadOnlyList<AndroidDevice> OnlineDevices => Manager.OnlineDevices;
    internal static string? SelectedSerial;
    internal static bool AirPlaySelected;
    internal static bool WindowsLinkSelected;
    internal static string? WindowsLinkPeerId;
    internal static Task<IReadOnlyList<AndroidDevice>> ReadDevicesAsync(CancellationToken token) => Discovery.ReadAsync(token);
    internal static event Action? DevicesChanged;
    private static void DiscoveryChanged()
    {
        try { if (Discovery.CurrentDevices != null) Manager.ObserveCurrent(); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Android connection observation: " + ex.Message); }
        DevicesChanged?.Invoke();
    }
    internal static void SetDevices(IReadOnlyList<AndroidDevice> devices) => Discovery.Publish(devices);

    // Called on the WPF dispatcher. The sidebar and device panel share one listener
    // and one baseline read, including when USB changed while both were closed.
    internal static void SetUsbPanelVisible(ref bool watching, bool visible)
    {
        if (watching == visible || System.Windows.Application.Current is not App app) return;
        if (visible)
        {
            if (!app.AndroidUsbNotificationsAvailable) return;
            watching = true;
            if (++_visibleUsbPanels != 1) return;
            app.AndroidUsbInterfaceChanged += OnUsbInterfaceChanged;
            ObservePanelWireless(app);
            OnUsbInterfaceChanged([]);
        }
        else
        {
            watching = false;
            if (--_visibleUsbPanels != 0) return;
            app.AndroidUsbInterfaceChanged -= OnUsbInterfaceChanged;
            _panelWireless?.Cancel();
            _usbPanelScan?.Cancel();
        }
    }

    private static async void ObservePanelWireless(App app)
    {
        using var listener = new CancellationTokenSource();
        _panelWireless = listener;
        var discovery = Discovery;
        var token = listener.Token;
        void Post(Action update)
        {
            if (token.IsCancellationRequested) return;
            _ = app.Dispatcher.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && ReferenceEquals(Discovery, discovery)) update();
            });
        }
        async void ReadChange()
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(token);
            read.CancelAfter(TimeSpan.FromSeconds(5));
            discovery.RetirePendingScan();
            try { await Task.Run(() => discovery.ReadAsync(read.Token), read.Token); }
            catch (OperationCanceledException) when (read.IsCancellationRequested) { }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("ADB panel discovery: " + ex.Message); }
        }
        try
        {
            await WatchPanelWireless(target => Post(() => discovery.RemoveTransport(target)), () => Post(ReadChange), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("ADB panel notifications: " + ex.Message); }
        finally { if (ReferenceEquals(_panelWireless, listener)) _panelWireless = null; }
    }

    private static void OnUsbInterfaceChanged(IReadOnlyCollection<AndroidDevice> removed)
    {
        // A scan started before this edge may still return the removed transport.
        // Supersede it without invalidating verified sessions on other phones.
        Discovery.RetirePendingScan();
        ScanForVisiblePanels(removed);
    }

    private static async void ScanForVisiblePanels(IReadOnlyCollection<AndroidDevice> removed)
    {
        _usbPanelScan?.Cancel();
        using var scan = new CancellationTokenSource();
        _usbPanelScan = scan;
        var discovery = Discovery;
        var observed = discovery.CurrentDevices;
        bool IsCurrent() => !scan.IsCancellationRequested && ReferenceEquals(discovery, Discovery);
        async Task<bool> ReadAsync()
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(scan.Token);
            attempt.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await Task.Run(() => discovery.ReadAsync(attempt.Token), attempt.Token);
                return true;
            }
            catch (OperationCanceledException) when (!scan.IsCancellationRequested) { return false; }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
            {
                System.Diagnostics.Trace.WriteLine("USB panel discovery: " + ex.Message);
                return false;
            }
        }
        try
        {
            if (await ReadAsync() || !IsCurrent()) return;
            var present = await Task.Run(ReadUsbInterfacePaths, scan.Token);
            if (!IsCurrent()) return;
            discovery.RemoveAbsentUsbDevices(observed, present, removed);
            if (present?.Count != 0 && !scan.IsCancellationRequested) await ReadAsync();
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("USB panel discovery: " + ex.Message); }
        finally { if (ReferenceEquals(scan, _usbPanelScan)) _usbPanelScan = null; }
    }

    // Resolve a captured UI/drag record just before opening a stream. The common
    // cached path performs no ADB work; an invalid or missing snapshot gets one
    // bounded scan, then the existing opt-in saved wireless recovery path.
    internal static async Task<AndroidDevice?> ResolveOnlineDeviceAsync(AndroidDevice device, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (GetOnlineDevice(device) is { } cached) return cached;
        using (var scan = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            scan.CancelAfter(TimeSpan.FromSeconds(5));
            try { await ReadDevicesAsync(scan.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { return GetOnlineDevice(device); }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                if (GetOnlineDevice(device) is { } published) return published;
                throw;
            }
        }
        token.ThrowIfCancellationRequested();
        var devices = Discovery.CurrentDevices;
        if (GetOnlineDevice(device) is { } online) return online;
        if (devices != null) await RecoverMissingWirelessAsync(device, devices, token).ConfigureAwait(false);
        return GetOnlineDevice(device);
    }

    internal static void SetAutomaticRecovery(AndroidDevice device, bool enabled)
    {
        Manager.SetDisconnected(device, !enabled);
        if (!enabled) CancelRecovery(device);
        DevicesChanged?.Invoke();
    }

    internal static void NotifyManagementChanged() => DevicesChanged?.Invoke();
    internal static void CancelRecovery(AndroidDevice device)
    {
        string key = AndroidDeviceIdentity.Key(device);
        lock (Recovering)
        {
            if (Recovering.TryGetValue(key, out var active)) active.Cancel();
        }
    }

    // Only run on demand (opening the phone panel or losing a live helper). Direct
    // wireless ADB sessions belong to the process and need restoring after a restart.
    internal static async Task<bool> RecoverMissingWirelessAsync(AndroidDevice? device, IReadOnlyList<AndroidDevice> devices, CancellationToken token)
    {
        if (device == null || AndroidDeviceIdentity.StableKey(device) is not { } key
            || GetOnlineDevice(device) != null) return false;
        var store = AndroidAudioHost.Phone;
        var profile = store.FindProfile(device);
        if (profile == null || profile.ManuallyDisconnected || store.IsRemoved(device)
            || profile.WirelessReconnectTarget(discoverFromUsb: false) is not { } target) return false;
        long revision = Manager.Revision;
        long discoveryRevision = Discovery.RevisionOf(devices);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (Recovering)
        {
            if (!Recovering.TryAdd(key, attempt)) return false;
        }
        try
        {
            var result = await CreateWirelessConnection().ReconnectAsync(target, profile.LastWirelessAddress, attempt.Token, devices,
                retryMissingIdentity: !AndroidAudioHost.HasActivePlaybackConnections()).ConfigureAwait(false);
            attempt.Token.ThrowIfCancellationRequested();
            if (Manager.Revision != revision || store.IsRemoved(device) || store.IsRemoved(result.Device)
                || AndroidConnectionManager.IsAmbiguous(result.Device, result.Devices)
                || store.FindProfile(device)?.ManuallyDisconnected != false) return false;
            attempt.Token.ThrowIfCancellationRequested();
            if (!Discovery.PublishConnection(result, discoveryRevision, current => Manager.AcceptRecovery(current, profile.Id, revision))) return false;
            await AndroidAudioHost.ProbeOutputsIfConnectedAsync([result.Device], token: attempt.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Phone wireless recovery: " + ex.Message); }
        finally { lock (Recovering) Recovering.Remove(key); }
        return false;
    }

    internal static async Task<IReadOnlyList<AndroidDevice>> ReconnectSelectedDeviceAsync(CancellationToken token,
        AndroidWirelessConnection? wireless = null)
    {
        var store = AndroidAudioHost.Phone;
        var selected = store.Profile;
        var devices = await RefreshSelectedDeviceAsync(token, wireless).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (selected.LastDevice == null) return devices;

        var current = store.Profile;
        if (current.Id != selected.Id) throw new OperationCanceledException(token);
        if (GetOnlineDevice(current.LastDevice) == null)
            throw AndroidError.WithCode(new IOException("未发现设备，请开启无线调试或更新连接地址。"), AndroidErrorCode.ReconnectNoDeviceFound);
        return devices;
    }

    internal static async Task<IReadOnlyList<AndroidDevice>> RefreshSelectedDeviceAsync(CancellationToken token,
        AndroidWirelessConnection? wireless = null)
    {
        var store = AndroidAudioHost.Phone;
        var selected = store.Profile;
        if (selected.LastDevice is not { } saved)
            return await ReadDevicesAsync(token).ConfigureAwait(false);

        long revision = Manager.Revision;
        bool StillSelected() => store.Profile.Id == selected.Id && revision == Manager.Revision;
        await RefreshGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!StillSelected()) return Devices ?? [];
            IReadOnlyList<AndroidDevice> devices;
            long discoveryRevision = Discovery.Revision;
            try
            {
                devices = await ReadDevicesAsync(token).ConfigureAwait(false);
                discoveryRevision = Discovery.RevisionOf(devices);
            }
            catch (Exception ex) when (ex is IOException or SocketException) { devices = []; }
            token.ThrowIfCancellationRequested();
            if (!StillSelected()) return devices;

            AndroidConnectionResult result;
            var ready = AndroidDeviceIdentity.FindOnlineDevice(saved, devices);
            var connection = wireless ?? CreateWirelessConnection();
            if (ready != null && (!saved.IsWifi || !ready.IsWifi && AndroidDeviceIdentity.SamePhysicalDevice(saved, ready)))
            {
                result = await connection.EnrichReadyResultAsync(new(ready, devices),
                    !AndroidAudioHost.HasActivePlaybackConnections(), token).ConfigureAwait(false);
            }
            else if (selected.WirelessReconnectTarget(discoverFromUsb: true) is { } target)
                result = await connection.ReconnectAsync(target, selected.LastWirelessAddress, token, devices,
                    retryMissingIdentity: !AndroidAudioHost.HasActivePlaybackConnections()).ConfigureAwait(false);
            else return devices;
            token.ThrowIfCancellationRequested();
            if (!StillSelected()) return Devices ?? devices;

            // A user-requested reconnect may repair an active legacy profile that
            // conflicts with its own archival removal record. Passive scans cannot.
            if (!Discovery.PublishConnection(result, discoveryRevision, current => Manager.Admit(current, revision, selected.Id, requireCurrent: true)))
                throw AndroidError.WithCode(new IOException("设备身份无法确认，请重新添加或连接正确的设备。"), AndroidErrorCode.ReconnectNoDeviceFound);
            return Devices ?? result.Devices;
        }
        finally { RefreshGate.Release(); }
    }
}
#endif

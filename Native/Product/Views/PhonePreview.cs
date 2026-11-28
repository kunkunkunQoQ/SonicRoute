#if SONICROUTE_PHONE
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute.Native.Product;

internal sealed partial class PhonePreview : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken token;
    private readonly SemaphoreSlim scans = new(1, 1);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly object operationsGate = new();
    private readonly Dictionary<string, CancellationTokenSource> recovering = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> probeTimestamps = new(StringComparer.Ordinal);
    private readonly Dictionary<IVirtualDisplayClient, DisplaySessionWatch> displayWatches = new();
    private readonly Func<AirPlayPreview?>? tryGetAirPlay;
    private readonly IMainThread owner;
    private readonly AppConfig config;
    private Task? tracking;
    private CancellationTokenSource? trackingLifetime;
    private bool keepTracking;
    private int temporaryTrackers;
    private IReadOnlyList<AndroidDevice> lastOnline = [];
    private int closed;

    private readonly AdbTransportDiscovery adbDiscovery;
    private readonly AdbOperationAdapter adbOperations;
    private readonly SrapPhoneTransport appTransport;
    internal readonly SrapPcListening PcListening;
    internal IPhoneOperationPort Operations { get; }
    internal readonly AndroidPhoneStore Store;
    internal readonly AndroidAudioSettingsStore Settings;
    internal readonly AndroidDeviceDiscovery Discovery;
    internal readonly AndroidConnectionManager Connections;
    internal readonly AndroidAudioManager Sources;
    internal readonly PcToPhoneAudioManager Pc;
    internal readonly AndroidVirtualDisplayManager Displays;
    internal readonly AndroidFileTransfers Transfers = new();
    private readonly Dictionary<string, Queue<(string[] Paths, string Destination, string? PullFrom, CancellationToken Attachment)>> pendingTransfers = new(StringComparer.Ordinal);
    internal readonly PhoneClipboard Clipboard;
    internal event Action? Changed;
    internal string? Error { get; private set; }

    internal PhonePreview(string root, NativeApplicationWindow shell, IMainThread owner, AppConfig config,
        Func<int, IPcProcessSilenceLease?>? silenceLocalProcess = null,
        Func<AirPlayPreview?>? tryGetAirPlay = null, AndroidAudioSettingsStore? settings = null,
        IAdbTransport? adbFixture = null, Action<string>? openListeningPermission = null)
    {
        this.owner = owner;
        this.config = config;
        this.tryGetAirPlay = tryGetAirPlay;
        token = lifetime.Token;

        // ArbitratedAdbTransport is AOT-clean in SonicRoute.AndroidAudio and delegates to external adb.exe on 127.0.0.1:5037
        // when active (matching WPF AndroidAudioHost), falling back to DirectAdbTransport when no server is running.
        var adb = new AdbClient(new AndroidRuntimeBundle(Path.Combine(root, "PhoneRuntime")), adbFixture);
        adbDiscovery = new(adb);
        adbOperations = new(adb);

        string storePath = Path.Combine(root, "android-phone.json");
        MigrateLegacyPhoneStore(root, storePath);
        Store = new(storePath, () => L10n.T("Phone.DefaultName"));
        appTransport = new(Store, root);
        PcListening = new(Store, appTransport);
        if (openListeningPermission != null) PcListening.PermissionRequested += request => owner.TryPost(() =>
        {
            if (Volatile.Read(ref closed) == 0 && PcListening.Pending(request.ProfileId) == request)
                openListeningPermission(request.ProfileId);
        });
        Settings = settings ?? new(Path.Combine(root, "android-audio.json"));
        Discovery = new(adbDiscovery.ReadAsync);
        Connections = new(Store, () => Discovery, Settings.MigrateConnectionPreferences, appTransport, adb, Settings.MigrateConnectionPreferences);
        var directOperations = new SrapOperationAdapter(adbOperations, appTransport, Connections.IsCurrent);
        Operations = directOperations;
        Sources = new(() => Operations.CreateCaptureSession(Settings.GetStreaming, Connections.ResolveKey), Settings,
            (phone, app) => Store.IsSmartVolumeEnabled(phone, app), Connections.IsCurrent, token, Operations.AppCapture);
        Pc = adbOperations.CreatePcPlayback(silenceLocalProcess);
        Pc.OpenAppPlayback = directOperations.OpenAppPlaybackAsync;
        Pc.OutputsObserved += (device, outputs) => Store.UpdateOutputs(device, outputs);
        Pc.DeviceDisconnected += OnPcPlaybackDisconnected;
        Displays = new(() => Operations.CreateDisplay(() => Settings));
        Clipboard = new(Operations.OpenClipboard, shell, owner, Path.Combine(root, "phone-clipboard.json"),
            d => Store.IsRemoved(d), d => Connections.Resolve(d) is { } live && Operations.CanSyncClipboard(live) ? live : null,
            d => Store.FindProfile(d)?.LastDevice);

        Sources.Changed += Notify;
        Sources.ResourcesRetired += QueueSourceReconciliation;
        Sources.CaptureInterrupted += OnPcPlaybackDisconnected;
        Discovery.Changed += Observe;
        Store.Changed += OnStoreChanged;
        Store.OutputsChanged += OnPlaybackDevicesChanged;
        Pc.Changed += Notify;
        Transfers.Changed += OnTransferChanged;
        Displays.ResourcesRetired += Notify;
        Clipboard.Changed += Notify;
        appTransport.Changed += OnAppTransportChanged;
        appTransport.Start();
    }

    internal CancellationToken Lifetime => token;

    internal Task<IReadOnlyList<AndroidMdnsService>> DiscoverSavedConnectionsAsync(CancellationToken cancel) =>
        adbDiscovery.DiscoverSavedAsync(cancel);

    internal static Task<IReadOnlyList<AndroidMdnsService>> DiscoverAppConnectionsAsync(CancellationToken cancel) =>
        SrapPhoneTransport.DiscoverAsync(cancel);

    internal async Task PairAppAsync(string invite, System.Net.IPEndPoint? selected, Action pending, CancellationToken cancel)
    {
        var device = await appTransport.PairAsync(invite, selected, pending, cancel).ConfigureAwait(false);
        await ProbeOutputsIfConnectedAsync([device], force: true, token: cancel).ConfigureAwait(false);
    }

    internal async Task PairNearbyAppAsync(AndroidMdnsService service,
        Func<string, CancellationToken, Task<bool>> confirmCode, CancellationToken cancel)
    {
        var device = await appTransport.PairNearbyAsync(service, confirmCode, cancel).ConfigureAwait(false);
        await ProbeOutputsIfConnectedAsync([device], force: true, token: cancel).ConfigureAwait(false);
    }

    private void OnAppTransportChanged()
    {
        if (token.IsCancellationRequested) return;
        QueueSourceReconciliation();
        PcListening.Reconcile();
        owner.TryPost(() => { if (Volatile.Read(ref closed) == 0 && Clipboard.Enabled) Clipboard.Reconcile(); });
        UpdatePlaybackConnections();
        Notify();
    }

    internal bool HasActivePlaybackConnections =>
        LocalAppOutputMirrors.HasPhoneOutputs || Sources.HasActiveWork || Displays.Count > 0 || Pc.HasActiveWork || PcListening.HasActiveWork ||
        tryGetAirPlay?.Invoke()?.Source != null;

    private static void MigrateLegacyPhoneStore(string root, string targetPath)
    {
        string legacyPath = Path.Combine(root, "android-phones.json");
        if (File.Exists(targetPath) || !File.Exists(legacyPath)) return;
        try
        {
            Directory.CreateDirectory(root);
            File.Copy(legacyPath, targetPath, overwrite: false);
            if (!File.Exists(targetPath + ".bak") && File.Exists(legacyPath + ".bak"))
                File.Copy(legacyPath + ".bak", targetPath + ".bak", overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExceptionDiagnostics.Record("NativePhone.MigrateStore", ex);
        }
    }

    internal static string TranslateError(Exception? error)
    {
        if (error == null) return "";
        if (error is SrapException srap)
        {
            string key = "Phone.Srap." + srap.Code;
            string message = L10n.T(key);
            return message == key ? L10n.T("Phone.SrapFailed") : message;
        }
        if (error is System.Security.Authentication.AuthenticationException) return L10n.T("Phone.SrapTlsRejected");
        if (error is FormatException) return L10n.T("Phone.SrapInvalidInvite");
        if (AndroidError.TryGetCode(error, out var code))
        {
            string key = "Phone.Err." + code;
            string translated = L10n.T(key);
            if (!string.Equals(translated, key, StringComparison.Ordinal)) return translated;
        }
        return error.Message ?? "";
    }

    internal void OnDeviceBroadcast()
    {
        if (Volatile.Read(ref closed) != 0) return;
        Discovery.RetirePendingScan();
        _ = RefreshInBackgroundAsync();
    }

    internal void StartTracking()
    {
        lock (operationsGate)
        {
            keepTracking = true;
            EnsureTracking();
        }
    }

    private void EnsureTracking()
    {
        if (Volatile.Read(ref closed) != 0 || trackingLifetime != null) return;
        var lifetime = trackingLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var prior = tracking;
        tracking = Task.Run(async () =>
        {
            try
            {
                if (prior != null) await prior.ConfigureAwait(false);
                if (!lifetime.IsCancellationRequested) await TrackAsync(adbDiscovery, lifetime.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (operationsGate)
                {
                    if (ReferenceEquals(trackingLifetime, lifetime)) trackingLifetime = null;
                    lifetime.Dispose();
                }
            }
        });
    }

    private async Task TrackAsync(IPhoneTransportDiscovery transport, CancellationToken cancel)
    {
        try
        {
            await transport.TrackAsync(devices =>
            {
                Discovery.RetirePendingScan();
                _ = Volatile.Read(ref keepTracking) ? RefreshInBackgroundAsync() : RefreshSavedTransportsAsync(cancel);
            }, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) { Error = TranslateError(error); Notify(); }
    }

    internal async Task RefreshInBackgroundAsync(string? reconnectProfileId = null, CancellationToken reconnectToken = default)
    {
        try { await RefreshAsync(restore: true, reconnectProfileId, reconnectToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Error = TranslateError(error); Notify(); }
    }

    internal async Task RefreshAsync(bool restore = false, string? reconnectProfileId = null, CancellationToken reconnectToken = default)
    {
        await scans.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var devices = await Discovery.ReadAsync(deadline.Token).ConfigureAwait(false);
            Error = DirectAdbTransport.UsbAccessError(devices, null) is { } accessError
                ? TranslateError(AndroidError.WithCode(new IOException(accessError.ToString()), accessError))
                : null;
            _ = ProbeOutputsIfConnectedAsync(devices, force: false, token: token);
            if (!restore) return;

            string? attemptedPhone = null;
            if (reconnectProfileId != null && !reconnectToken.IsCancellationRequested &&
                Store.Profile is { LastDevice: { IsApp: false } saved } selected && selected.Id == reconnectProfileId &&
                Connections.Get(saved).State == AndroidManagedConnectionState.Offline)
            {
                using var opening = CancellationTokenSource.CreateLinkedTokenSource(reconnectToken, deadline.Token);
                await RecoverMissingWirelessAsync(saved, devices, opening.Token, discoverFromUsb: true).ConfigureAwait(false);
                if (!reconnectToken.IsCancellationRequested) attemptedPhone = AndroidDeviceIdentity.StableKey(saved);
            }

            var restoreKeys = Settings.ExternalAudio.Where(p => p.Resume && p.App != null).Select(p => p.DeviceKey!)
                .Concat(Settings.GetSavedSources().Select(r => r.DeviceKey)).Distinct();
            foreach (string key in restoreKeys)
            {
                if (key == attemptedPhone || Connections.ResolveKey(key) != null) continue;
                var profile = Store.Profiles.FirstOrDefault(p => AndroidDeviceIdentity.StableKey(p.LastDevice) == key);
                if (profile?.LastDevice is { } lastDev)
                    await RecoverMissingWirelessAsync(lastDev, Discovery.Devices ?? devices, deadline.Token).ConfigureAwait(false);
            }
            QueueSourceReconciliation();
        }
        finally
        {
            scans.Release();
            owner.TryPost(() => { if (Volatile.Read(ref closed) == 0) Clipboard.Reconcile(); });
            Notify();
        }
    }

    internal async Task<bool> RecoverMissingWirelessAsync(AndroidDevice? device, IReadOnlyList<AndroidDevice> devices, CancellationToken cancel,
        bool discoverFromUsb = false, string? advertisedAddress = null)
    {
        if (cancel.IsCancellationRequested || device == null || device.IsApp || AndroidDeviceIdentity.StableKey(device) is not { } key
            || Connections.Resolve(device) != null)
            return false;

        var profile = Store.FindProfile(device);
        if (profile == null || profile.ManuallyDisconnected || Store.IsRemoved(device)
            || profile.WirelessReconnectTarget(discoverFromUsb) is not { } target)
            return false;

        long revision = Connections.Revision;
        long discoveryRevision = Discovery.RevisionOf(devices);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, cancel);
        lock (recovering)
        {
            if (!recovering.TryAdd(key, attempt)) return false;
        }
        try
        {
            var result = await adbDiscovery.ReconnectAsync(target, advertisedAddress ?? profile.LastWirelessAddress, attempt.Token, devices,
                retryMissingIdentity: !HasActivePlaybackConnections).ConfigureAwait(false);
            attempt.Token.ThrowIfCancellationRequested();
            if (Connections.Revision != revision || Store.IsRemoved(device) || Store.IsRemoved(result.Device)
                || AndroidConnectionManager.IsAmbiguous(result.Device, result.Devices)
                || Store.FindProfile(device)?.ManuallyDisconnected != false)
                return false;
            attempt.Token.ThrowIfCancellationRequested();
            if (!Discovery.PublishConnection(result, discoveryRevision,
                current => !attempt.IsCancellationRequested && Connections.AcceptRecovery(current, profile.Id, revision)))
                return false;
            await ProbeOutputsIfConnectedAsync([result.Device], token: attempt.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested) { return false; }
        catch (Exception ex) { ExceptionDiagnostics.Record("NativePhone.WirelessRecovery", ex); return false; }
        finally { lock (recovering) recovering.Remove(key); }
    }

    internal void CancelRecovery(AndroidDevice device)
    {
        string key = AndroidDeviceIdentity.Key(device);
        lock (recovering)
        {
            if (recovering.TryGetValue(key, out var active)) active.Cancel();
        }
    }

    internal async Task<AndroidDevice?> ResolveOnlineDeviceAsync(AndroidDevice device, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (Connections.Resolve(device) is { } cached) return cached;
        if (device.IsApp) return null;
        using (var scan = CancellationTokenSource.CreateLinkedTokenSource(token, cancel))
        {
            scan.CancelAfter(TimeSpan.FromSeconds(5));
            try { await Discovery.ReadAsync(scan.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested && !token.IsCancellationRequested)
            { return Connections.Resolve(device); }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                if (Connections.Resolve(device) is { } published) return published;
                throw;
            }
        }
        cancel.ThrowIfCancellationRequested();
        var devices = Discovery.CurrentDevices;
        if (Connections.Resolve(device) is { } online) return online;
        if (devices != null) await RecoverMissingWirelessAsync(device, devices, cancel).ConfigureAwait(false);
        return Connections.Resolve(device);
    }

    internal async Task<AndroidDevice> ResolveOperationDeviceAsync(AndroidDevice device, AndroidErrorCode offlineCode, CancellationToken cancel = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancel);
        var initial = Connections.Get(device);
        if (initial.State is AndroidManagedConnectionState.Removed or AndroidManagedConnectionState.ManuallyDisconnected
            or AndroidManagedConnectionState.IdentityConflict)
            throw ConnectionError(initial.State, offlineCode);
        var live = await ResolveOnlineDeviceAsync(device, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        if (live == null) throw ConnectionError(Connections.Get(device).State, offlineCode);
        if (!Connections.IsCurrent(live)) throw ConnectionError(Connections.Get(live).State, offlineCode);
        return live;
    }

    private static IOException ConnectionError(AndroidManagedConnectionState state, AndroidErrorCode offlineCode) =>
        state == AndroidManagedConnectionState.Removed
            ? AndroidError.WithCode(new IOException("This phone was removed."), AndroidErrorCode.PhoneRemovedAddFirst)
            : AndroidError.WithCode(new IOException("Device is offline or unverified."), offlineCode);

    internal async Task ProbeOutputsIfConnectedAsync(IEnumerable<AndroidDevice>? devices, bool force = false, CancellationToken token = default)
    {
        if (devices == null) return;
        long now = Environment.TickCount64;
        foreach (var device in devices.Select(d => Connections.Resolve(d)).OfType<AndroidDevice>().DistinctBy(AndroidDeviceIdentity.Key))
        {
            if (device.State != "device" || !Operations.CanListOutputs(device)
                || string.Equals(device.Serial, "offline-fixture", StringComparison.Ordinal)) continue;
            lock (probeTimestamps)
            {
                if (!force && probeTimestamps.TryGetValue(device.Serial, out long last) && now - last < 3000) continue;
                probeTimestamps[device.Serial] = now;
            }
            try
            {
                using var read = CancellationTokenSource.CreateLinkedTokenSource(this.token, token, Connections.OperationToken(device));
                var outputs = await Operations.OutputsAsync(device, read.Token).ConfigureAwait(false);
                if (Connections.IsCurrent(device)) Store.UpdateOutputs(device, outputs);
            }
            catch
            {
                lock (probeTimestamps) probeTimestamps.Remove(device.Serial);
            }
        }
    }

    internal void OnPcPlaybackDisconnected(AndroidDevice device)
    {
        if (token.IsCancellationRequested || Store.IsRemoved(device)) return;
        if (device.IsApp)
        {
            Notify();
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var devices = await Discovery.ReadAsync(token).ConfigureAwait(false);
                if (Connections.Resolve(device) is { } online)
                    await ProbeOutputsIfConnectedAsync([online], token: token).ConfigureAwait(false);
                else
                    await RecoverMissingWirelessAsync(device, devices, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { ExceptionDiagnostics.Record("NativePhone.PlaybackRecover", ex); }
        });
    }

    internal async Task DiscoverAndConnectAsync(string address, CancellationToken cancel, AndroidSavedConnection? saved = null)
    {
        long revision = Connections.Revision;
        var result = saved == null ? await adbDiscovery.ConnectAsync(address, cancel).ConfigureAwait(false)
            : await adbDiscovery.ReconnectAsync(saved.Device, saved.Address, cancel).ConfigureAwait(false);
        Accept(result, revision);
    }

    internal async Task RestoreUsbAsync(AndroidDevice chosen, CancellationToken cancel)
    {
        long revision = Connections.Revision;
        var devices = await Discovery.ReadAsync(cancel).ConfigureAwait(false);
        var live = devices.FirstOrDefault(d => d.State == "device" && !d.IsWifi && AndroidDeviceIdentity.Matches(chosen, d)
            && !AndroidConnectionManager.IsAmbiguous(d, devices) && Connections.Resolve(d) == null);
        cancel.ThrowIfCancellationRequested();
        if (live == null) throw AndroidError.WithCode(new IOException("USB device is unavailable."), AndroidErrorCode.UsbReconnectHint);
        Accept(new(live, devices), revision);
    }

    internal async Task PairAsync(string address, string code, CancellationToken cancel)
    {
        long revision = Connections.Revision;
        var paired = await adbDiscovery.PairCodeAsync(address, code, cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        Accept(await adbDiscovery.FinishPairingAsync(paired, cancel).ConfigureAwait(false), revision);
    }

    internal async Task PairAsync(AndroidQrPairing qr, CancellationToken cancel)
    {
        long revision = Connections.Revision;
        var paired = await adbDiscovery.PairQrAsync(qr, cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        Accept(await adbDiscovery.FinishPairingAsync(paired, cancel).ConfigureAwait(false), revision);
    }

    internal async Task ReconnectAsync(AndroidDevice device)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await ReconnectSelectedDeviceAsync(deadline.Token).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<AndroidDevice>> ReconnectSelectedDeviceAsync(CancellationToken cancel)
    {
        var selected = Store.Profile;
        long revision = Connections.Revision;
        if (selected.LastDevice?.IsApp == true)
            return [await appTransport.ReconnectAsync(selected, cancel).ConfigureAwait(false)];
        if (selected.AppPublicKeySha256 != null && !selected.AppTrustRevoked && (selected.PreferAppTransport
            || Connections.Resolve(selected.LastDevice)?.IsApp == true))
        {
            try { return [await appTransport.ReconnectAsync(selected, cancel).ConfigureAwait(false)]; }
            catch (SrapException ex) when (ex.Code is "unavailable" or "not_paired") { revision = Connections.Revision; }
        }
        var devices = await Task.Run(() => RefreshSelectedDeviceAsync(selected, revision, cancel), cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        if (selected.LastDevice == null) return devices;

        var current = Store.Profile;
        if (current.Id != selected.Id) throw new OperationCanceledException(cancel);
        if (Connections.Resolve(current.LastDevice) == null)
            throw AndroidError.WithCode(new IOException("Device not found."), AndroidErrorCode.ReconnectNoDeviceFound);
        return devices;
    }

    internal Task<IReadOnlyList<AndroidDevice>> RefreshSelectedDeviceAsync(CancellationToken cancel) =>
        Store.Profile.LastDevice?.IsApp == true || Connections.Resolve(Store.Profile.LastDevice)?.IsApp == true ? ReconnectSelectedDeviceAsync(cancel) :
            RefreshSelectedDeviceAsync(Store.Profile, Connections.Revision, cancel);

    private async Task<IReadOnlyList<AndroidDevice>> RefreshSelectedDeviceAsync(AndroidPhoneProfile selected, long revision, CancellationToken cancel)
    {
        if (selected.LastDevice is not { } saved)
            return await Discovery.ReadAsync(cancel).ConfigureAwait(false);

        bool StillSelected() => Store.Profile.Id == selected.Id && revision == Connections.Revision;
        await refreshGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (!StillSelected()) return Discovery.Devices ?? [];
            IReadOnlyList<AndroidDevice> devices;
            long discoveryRevision = Discovery.Revision;
            try
            {
                devices = await Discovery.ReadAsync(cancel).ConfigureAwait(false);
                discoveryRevision = Discovery.RevisionOf(devices);
            }
            catch (Exception ex) when (ex is IOException or SocketException) { devices = []; }
            cancel.ThrowIfCancellationRequested();
            if (!StillSelected()) return devices;

            AndroidConnectionResult result;
            var ready = AndroidDeviceIdentity.FindOnlineDevice(saved, devices);
            if (ready is { IsWifi: false } && (!saved.IsWifi || AndroidDeviceIdentity.SamePhysicalDevice(saved, ready)))
            {
                result = await adbDiscovery.EnrichReadyResultAsync(new(ready, devices), !HasActivePlaybackConnections, cancel).ConfigureAwait(false);
            }
            else if (selected.WirelessReconnectTarget(discoverFromUsb: true) is { } target)
            {
                result = await adbDiscovery.ReconnectAsync(target, selected.LastWirelessAddress, cancel, devices,
                    retryMissingIdentity: !HasActivePlaybackConnections).ConfigureAwait(false);
            }
            else return devices;
            cancel.ThrowIfCancellationRequested();
            if (!StillSelected()) return Discovery.Devices ?? devices;

            if (!await Discovery.PublishReconnectAsync(result, discoveryRevision,
                current => !cancel.IsCancellationRequested && Connections.Admit(current, revision, selected.Id, requireCurrent: true), cancel).ConfigureAwait(false))
                throw AndroidError.WithCode(new IOException("Device identity could not be verified."), AndroidErrorCode.ReconnectNoDeviceFound);
            _ = ProbeOutputsIfConnectedAsync([result.Device], force: true, token: token);
            return Discovery.Devices ?? result.Devices;
        }
        finally
        {
            refreshGate.Release();
            Notify();
        }
    }

    private void Accept(AndroidConnectionResult result, long revision)
    {
        if (token.IsCancellationRequested || !Connections.Admit(result, revision))
            throw AndroidError.WithCode(new IOException("The connection changed while this request was running."), AndroidErrorCode.ReconnectNoDeviceFound);
        Discovery.Publish(result.Devices);
        _ = ProbeOutputsIfConnectedAsync([result.Device], force: true, token: token);
        Notify();
    }

    internal async Task DisconnectAsync(AndroidDevice device, bool unpair)
    {
        if (device.IsApp)
        {
            if (Store.FindProfile(device) is not { AppPublicKeySha256: { } fingerprint } appProfile) return;
            if (unpair) appTransport.RevokeTrust(appProfile.Id, fingerprint);
            else
            {
                Connections.SetDisconnected(device, true);
                appTransport.Close(appProfile.Id);
            }
            await Store.FlushAsync().ConfigureAwait(false);
            return;
        }
        if (unpair && device.State != "device")
            throw AndroidError.WithCode(new IOException("Device is offline."), AndroidErrorCode.UnpairPhoneOffline);

        var aliases = (Discovery.CurrentDevices ?? [device])
            .Where(d => AndroidDeviceIdentity.Matches(device, d))
            .Append(device)
            .Distinct()
            .ToArray();

        lock (operationsGate)
        {
            Connections.BeginRetirement(aliases);
            try
            {
                Connections.SetDisconnected(device, true);
                foreach (var alias in aliases) CancelRecovery(alias);
            }
            catch
            {
                Connections.CompleteRetirement(aliases);
                throw;
            }
        }

        try
        {
            await CleanupManagedPhoneAsync(aliases, removed: false).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            if (unpair)
            {
                await adbDiscovery.UnpairAsync(device, deadline.Token).ConfigureAwait(false);
            }
            else
            {
                foreach (var alias in aliases.Where(d => d.IsWifi).DistinctBy(d => d.CommandTarget))
                    await adbDiscovery.DisconnectAsync(alias, deadline.Token).ConfigureAwait(false);
            }
            Store.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker]);
            foreach (var alias in aliases)
                Discovery.RemoveTransport(alias.CommandTarget);
        }
        finally
        {
            Connections.CompleteRetirement(aliases);
            Notify();
        }
    }

    internal async Task RemovePhoneAsync(string profileId)
    {
        IReadOnlyList<AndroidDevice> aliases;
        lock (operationsGate)
        {
            if (!Connections.Remove(profileId, out aliases)) return;
        }
        foreach (var alias in aliases) CancelRecovery(alias);
        Notify();
        try
        {
            await CleanupManagedPhoneAsync(aliases, removed: true).ConfigureAwait(false);
        }
        finally
        {
            Connections.CompleteRetirement(aliases);
            Notify();
        }
        await Store.FlushAsync().ConfigureAwait(false);
    }

    private async Task CleanupManagedPhoneAsync(IReadOnlyList<AndroidDevice> aliases, bool removed)
    {
        foreach (var alias in aliases.DistinctBy(AndroidDeviceIdentity.Key))
        {
            Settings.RetireExternalPhone(alias, removed);
            if (removed) Settings.ForgetPhoneRoutes(alias);
            else Settings.ForgetSourceRoutes(alias);
            await Task.WhenAll(
                LocalAppOutputMirrors.PhoneRemovedAsync(alias),
                Sources.RemovePhoneAsync(alias),
                Displays.CloseDeviceAsync(alias),
                Pc.StopPhoneAsync(alias),
                tryGetAirPlay?.Invoke()?.Source?.PhoneRemovedAsync(alias) ?? Task.CompletedTask
            ).ConfigureAwait(false);
        }
        await Settings.FlushAsync().ConfigureAwait(false);
    }

    internal AndroidPhonePcmPlayer CreatePhonePcmPlayer(AndroidPcmSource pcm, string endpoint, CancellationToken cancel)
    {
        if (!AndroidOutputIdentity.TryParseDeviceId(endpoint, out string phoneKey, out string sink))
            throw AndroidError.WithCode(new IOException("Invalid device output."), AndroidErrorCode.InvalidPhoneOutputDevice);

        var device = Connections.ResolveKey(phoneKey)
            ?? throw AndroidError.WithCode(new IOException("Device is offline."), AndroidErrorCode.PhoneOffline);
        var profile = Store.FindProfile(device);
        var sinks = profile?.OutputSinks is { Count: > 0 } configured
            ? AndroidOutputSink.NormalizeList(configured)
            : [AndroidOutputSink.DefaultSpeaker];
        bool valid = string.Equals(sink, AndroidOutputSink.Current.Key, StringComparison.OrdinalIgnoreCase)
            || sinks.Any(s => string.Equals(s.Key, sink, StringComparison.OrdinalIgnoreCase));
        if (!valid)
            throw AndroidError.WithCode(new IOException("Selected device output is disconnected."), AndroidErrorCode.SelectedPhoneOutputDisconnected);

        cancel.ThrowIfCancellationRequested();
        return Operations.CreatePlayback(device, pcm, sink, cancel,
            (connected, outputs) => { if (Connections.IsCurrent(connected)) Store.UpdateOutputs(connected, outputs); },
            Connections.OperationToken(device));
    }

    internal int QueuedTransferCount(string profileId) => pendingTransfers.GetValueOrDefault(profileId)?.Count ?? 0;
    internal void ClearQueuedTransfers(string profileId) => pendingTransfers.Remove(profileId);

    // Enqueue and advance on the host owner. A closed view does not own accepted transfers.
    internal bool EnqueueTransfer(AndroidDevice device, string profileId, string[] paths, string destination, string? remoteFolder)
    {
        if (paths.Length == 0 || Connections.Get(device).Profile?.Id != profileId || Operations.FileRoot(device) == null ||
            remoteFolder == null && !Operations.CanWriteFiles(device)) return false;
        if (Transfers.Get(profileId)?.Phase != AndroidFilePhase.Running && QueuedTransferCount(profileId) == 0)
            return StartTransfer(device, paths, destination, remoteFolder);
        if (!pendingTransfers.TryGetValue(profileId, out var queue)) pendingTransfers.Add(profileId, queue = new());
        queue.Enqueue((paths, destination, remoteFolder, Connections.OperationToken(device)));
        return true;
    }

    private void OnTransferChanged(string profileId)
    {
        var phase = Transfers.Get(profileId)?.Phase;
        if (phase != AndroidFilePhase.Running)
            owner.PostContinuation(() =>
            {
                if (closed != 0 || !pendingTransfers.TryGetValue(profileId, out var queue)) return;
                if (phase == AndroidFilePhase.Cancelled) { ClearQueuedTransfers(profileId); return; }
                if (Transfers.Get(profileId)?.Phase == AndroidFilePhase.Running) return;
                var last = Store.Profiles.FirstOrDefault(p => p.Id == profileId)?.LastDevice;
                var current = last == null ? null : Connections.Get(last);
                var next = queue.Dequeue();
                if (queue.Count == 0) ClearQueuedTransfers(profileId);
                if (next.Attachment.IsCancellationRequested || current?.Device is not { } device || current.Profile?.Id != profileId ||
                    !StartTransfer(device, next.Paths, next.Destination, next.PullFrom))
                {
                    ClearQueuedTransfers(profileId);
                    Error = L10n.T("Tray.AudioActionFailed");
                    Notify();
                }
            });
        Notify();
    }

    internal bool StartTransfer(AndroidDevice device, IReadOnlyList<string> paths, string destination, string? remoteFolder)
    {
        var current = Connections.Get(device);
        if (current.Profile == null || current.Device is not { } confirmed || !AndroidDeviceIdentity.SameTransport(device, confirmed)
            || Operations.FileRoot(confirmed) == null ||
            remoteFolder == null && !Operations.CanWriteFiles(confirmed)) return false;
        try
        {
            return Transfers.TryStart(current.Profile.Id, Operations.OpenFiles(confirmed), paths, destination, remoteFolder,
                () => Connections.IsCurrent(confirmed), Connections.OperationToken(confirmed), out _);
        }
        catch (SrapException) { return false; }
    }

    internal async Task<IVirtualDisplayClient> OpenDisplayAsync(AndroidDevice device, AndroidApp app, int x, int y)
    {
        if (device.IsApp) throw new SrapException("unsupported");
        if (app.IsDeviceAudio)
            app = app with { Name = Store.GetDisplayName(device) };
        var resolved = await ResolveOperationDeviceAsync(device, AndroidErrorCode.DisplayPhoneNotConnected, token).ConfigureAwait(false);
        if (!AndroidConnectionManager.IsRequestedAdbDisplayAttachment(device, resolved)) throw new SrapException("stale_session");
        device = resolved;
        app = await Connections.ResolveRetainedDisplayAppAsync(device, app,
            (selected, package, ct) => Operations.AppsAsync(selected, ct, [package]), token).ConfigureAwait(false);

        var (clampedX, clampedY) = ClampWindowOriginToWorkArea(x, y);
        IVirtualDisplayClient display;
        lock (operationsGate)
        {
            if (!Connections.IsCurrent(device))
                throw ConnectionError(Connections.Get(device).State, AndroidErrorCode.DisplayPhoneNotConnected);
        }
        display = await Displays.OpenAsync(device, app, clampedX, clampedY).ConfigureAwait(false);
        display.Activate();

        DisplaySessionWatch watch;
        bool created = false;
        lock (displayWatches)
        {
            if (!displayWatches.TryGetValue(display, out watch!))
            {
                watch = new DisplaySessionWatch(this, display, device, app);
                displayWatches.Add(display, watch);
                created = true;
            }
        }
        if (created) _ = WatchDisplayAsync(display, watch);
        return display;
    }

    private async Task WatchDisplayAsync(IVirtualDisplayClient display, DisplaySessionWatch watch)
    {
        try { await watch.WatchAsync().ConfigureAwait(false); }
        catch (Exception error) { ExceptionDiagnostics.Record("NativePhone.Display", error); }
        finally { lock (displayWatches) displayWatches.Remove(display); }
    }

    internal async Task ReturnAndroidToPhoneAsync(AndroidDevice device, string package)
    {
        DisplaySessionWatch[] matching;
        lock (displayWatches)
            matching = displayWatches.Values.Where(w => w.Matches(device, package)).ToArray();
        await Task.WhenAll(matching.Select(w => w.StopReceivingAsync())).ConfigureAwait(false);
        await Displays.CloseApplicationAsync(device, package).ConfigureAwait(false);
        await Sources.RemoveApplicationAsync(device, package).ConfigureAwait(false);
    }

    private sealed class DisplaySessionWatch(PhonePreview owner, IVirtualDisplayClient client, AndroidDevice device, AndroidApp app)
    {
        private readonly object gate = new();
        private AndroidAudioSource? source;
        private Views.VirtualDisplayStatusWindow? statusWindow;
        private bool statusUpdateQueued;
        private VirtualDisplayState desired = client.State;
        private bool closed, dirty, running;
        private Task reconciliation = Task.CompletedTask;

        internal bool Matches(AndroidDevice targetDevice, string package) =>
            AndroidDeviceIdentity.Matches(device, targetDevice) && app.Package == package;

        internal async Task StopReceivingAsync()
        {
            Task pending;
            lock (gate)
            {
                closed = true;
                QueueStatusUpdateLocked();
                pending = QueueLocked();
            }
            await pending.ConfigureAwait(false);
        }

        internal async Task WatchAsync()
        {
            client.StateChanged += OnStateChanged;
            OnStateChanged(client.State);
            try { await client.Completion.ConfigureAwait(false); }
            catch (Exception error) { ExceptionDiagnostics.Record("NativePhone.DisplayClient", error); }
            finally
            {
                client.StateChanged -= OnStateChanged;
                Task pending;
                lock (gate)
                {
                    closed = true;
                    QueueStatusUpdateLocked();
                    pending = QueueLocked();
                }
                await pending.ConfigureAwait(false);
            }
        }

        private void OnStateChanged(VirtualDisplayState state)
        {
            lock (gate)
            {
                if (closed) return;
                desired = state;
                QueueStatusUpdateLocked();
                _ = QueueLocked();
            }
        }

        private void QueueStatusUpdateLocked()
        {
            if (!statusUpdateQueued) statusUpdateQueued = owner.owner.TryPost(UpdateStatusOnUi);
        }

        private void UpdateStatusOnUi()
        {
            VirtualDisplayState state;
            bool finished;
            lock (gate)
            {
                statusUpdateQueued = false;
                state = desired;
                finished = closed || Volatile.Read(ref owner.closed) != 0;
            }
            if (finished || state is not (VirtualDisplayState.OnPhone or VirtualDisplayState.Recovering or VirtualDisplayState.Missing))
            {
                CloseStatusOnUi();
                return;
            }
            if (client.WindowHandle == 0) return;
            if (statusWindow == null || statusWindow.IsDisposed)
                statusWindow = new Views.VirtualDisplayStatusWindow(client, owner.config, owner.owner);
            statusWindow.UpdateState(state);
        }

        internal void CloseStatusOnUi()
        {
            statusWindow?.Dispose();
            statusWindow = null;
        }

        private Task QueueLocked()
        {
            dirty = true;
            if (!running)
            {
                running = true;
                reconciliation = Task.Run(ReconcileAsync);
            }
            return reconciliation;
        }

        private async Task ReconcileAsync()
        {
            try
            {
                while (true)
                {
                    bool receive;
                    AndroidAudioSource? current;
                    lock (gate)
                    {
                        if (!dirty) { running = false; return; }
                        dirty = false;
                        receive = !closed && desired is VirtualDisplayState.Active or VirtualDisplayState.Starting or VirtualDisplayState.Unknown;
                        current = source;
                    }
                    try
                    {
                        if (!receive && current != null)
                        {
                            lock (gate) { if (ReferenceEquals(source, current)) source = null; }
                            await owner.Sources.ReleaseDisplayAudioAsync(current, app.Package).ConfigureAwait(false);
                        }
                        else if (receive && current == null)
                        {
                            var live = await owner.ResolveOperationDeviceAsync(device, AndroidErrorCode.PhoneOffline, owner.token).ConfigureAwait(false);
                            if (!AndroidConnectionManager.IsRequestedAdbDisplayAttachment(device, live)) throw new SrapException("stale_session");
                            var added = await owner.Sources.AddAsync(live, app, keepPhoneAudio: false, displayOwned: true).ConfigureAwait(false);
                            bool shouldReleaseImmediately;
                            lock (gate)
                            {
                                shouldReleaseImmediately = closed || desired is not (VirtualDisplayState.Active or VirtualDisplayState.Starting or VirtualDisplayState.Unknown);
                                if (!shouldReleaseImmediately) source = added;
                            }
                            if (shouldReleaseImmediately)
                                await owner.Sources.ReleaseDisplayAudioAsync(added, app.Package).ConfigureAwait(false);
                        }
                    }
                    catch (Exception error)
                    {
                        ExceptionDiagnostics.Record("NativePhone.DisplayAudio", error);
                    }
                }
            }
            catch
            {
                lock (gate) running = false;
            }
        }
    }

    private static (int X, int Y) ClampWindowOriginToWorkArea(int screenX, int screenY)
    {
        nint monitor = MonitorFromPoint(new NativePt { X = screenX, Y = screenY }, 2 /*MONITOR_DEFAULTTONEAREST*/);
        if (monitor != 0)
        {
            var mi = new NativeMonitorInfo { CbSize = (uint)Marshal.SizeOf<NativeMonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref mi))
            {
                int left = mi.RcWork.Left;
                int top = mi.RcWork.Top;
                int right = mi.RcWork.Right;
                int bottom = mi.RcWork.Bottom;
                int x = Math.Max(left, Math.Min(screenX - 210, right - 440));
                int y = Math.Max(top, Math.Min(screenY - 30, bottom - 790));
                return (Math.Max(left, x), Math.Max(top, y));
            }
        }
        return (screenX, screenY);
    }

    private void OnStoreChanged()
    {
        PcListening.Reconcile();
        Sources.SyncSmartVolume((d, p) => Store.IsSmartVolumeEnabled(d, p));
        QueueSourceReconciliation();
        owner.TryPost(() => { if (Volatile.Read(ref closed) == 0) Clipboard.Reconcile(); });
        Notify();
    }

    private void Observe()
    {
        if (token.IsCancellationRequested) return;
        try
        {
            Connections.ObserveCurrent();
            var cached = Discovery.CurrentDevices;
            if (cached != null)
            {
                UpdatePlaybackConnections();
                QueueSourceReconciliation();
                foreach (var profile in Store.Profiles)
                {
                    if (profile.LastDevice is { } dev && Connections.Resolve(dev) == null && profile.OutputSinks.Count > 1)
                        Store.UpdateOutputs(dev, [AndroidOutputSink.DefaultSpeaker]);
                }
                foreach (var route in Pc.GetActiveRoutes())
                {
                    if (Connections.ResolveKey(route.PhoneKey) == null)
                        Pc.StopProcess(route.ProcessId, restorePreviousEndpoint: true);
                }
            }
        }
        catch (Exception error) { Error = TranslateError(error); }
        owner.TryPost(() => { if (Volatile.Read(ref closed) == 0) Clipboard.Reconcile(); });
        Notify();
    }

    private void Notify()
    {
        if (Volatile.Read(ref closed) == 0) Changed?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        Store.OutputsChanged -= OnPlaybackDevicesChanged;
        appTransport.Changed -= OnAppTransportChanged;
        Sources.ResourcesRetired -= QueueSourceReconciliation;
        Sources.CaptureInterrupted -= OnPcPlaybackDisconnected;
        Sources.BeginShutdown();
        Transfers.Changed -= OnTransferChanged;
        pendingTransfers.Clear();
        lock (displayWatches)
            foreach (var watch in displayWatches.Values) watch.CloseStatusOnUi();
        Clipboard.Dispose();
        StopWirelessWatch();
        lifetime.Cancel();
        Task.Run(async () =>
        {
            await PcListening.DisposeAsync().ConfigureAwait(false);
            await appTransport.DisposeAsync().ConfigureAwait(false);
            await Transfers.CloseAsync().ConfigureAwait(false);
            if (tracking != null) await tracking.ConfigureAwait(false);
            await WaitForSourceReconciliationAsync().ConfigureAwait(false);
            await Task.WhenAll(Displays.DisposeAsync().AsTask(), Sources.DisposeAsync().AsTask(), Pc.DisposeAsync().AsTask()).ConfigureAwait(false);
            await Store.FlushAsync().ConfigureAwait(false);
            await Settings.FlushAsync().ConfigureAwait(false);
            await adbOperations.DisposeAsync().ConfigureAwait(false);
        }).GetAwaiter().GetResult();
        lifetime.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePt { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRc { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public uint CbSize;
        public NativeRc RcMonitor, RcWork;
        public uint DwFlags;
    }

    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePt pt, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint hMonitor, ref NativeMonitorInfo lpmi);
}

internal sealed partial class ApplicationHost
{
    private PhonePreview? phone;
    private AndroidAudioSettingsStore? externalAudioSettings;
    private AndroidAudioSettingsStore ExternalAudioSettings => externalAudioSettings ??=
        new(Path.Combine(_environment.DataDirectory, "android-audio.json"));
    internal bool HasPhone => phone != null;
    internal bool HasAirPlay => airPlay != null;
    internal PhonePreview Phone
    {
        get
        {
            if (phone != null) return phone;
            var created = new PhonePreview(_environment.DataDirectory, _window, this, _config,
                _audio.ExternalAudio != null ? _audio.ExternalAudio.AcquireLocalSilenceLease : null,
                () => HasAirPlay ? AirPlay : null, ExternalAudioSettings,
                _environment.IsolatedAdbFixturePort() is { } port ? new AdbSocketClient(port) : null,
                profile => OpenPhoneManagement(profile, airPlay: false));
            created.Changed += OnExternalSourcesChanged;
            phone = created;
            EnsureExternalOutputTopology();
            return created;
        }
    }
    internal AirPlayPreview AirPlay
    {
        get
        {
            if (airPlay != null) return airPlay;
            var created = new AirPlayPreview(_environment.IsPackaged,
                (pcm, endpoint, cancel) => PcRoutingApi.CreatePhonePcmPlayer(pcm, endpoint, cancel),
                ExternalAudioSettings, _config, TryPost);
            created.Changed += OnExternalSourcesChanged;
            airPlay = created;
            EnsureExternalOutputTopology();
            return created;
        }
    }
    private AirPlayPreview? airPlay;

    private void OnExternalSourcesChanged()
    {
        NotifyAudioVisualChanged();
    }

    private IDisposable? externalOutputTopology;
    private void EnsureExternalOutputTopology()
    {
        if (!_resident || externalOutputTopology != null) return;
        try { externalOutputTopology = ProductAudio.WatchDevices(OnExternalOutputTopologyChanged); }
        catch (Exception error) { ExceptionDiagnostics.Record("ExternalAudio.WatchOutputs", error); }
    }

    private void OnExternalOutputTopologyChanged()
    {
        phone?.QueueSourceReconciliation(refreshRoutes: true);
        airPlay?.QueueOutputRecovery();
    }

    private void RestoreFull()
    {
        var settings = ExternalAudioSettings;
        bool hasSavedRoutes = settings.GetSavedSources().Length != 0 || settings.ExternalAudio.Any(p =>
            p.App != null && p.Resume || p.App == null && settings.AirPlayEnabled && p.Outputs.Any(AndroidOutputIdentity.IsPhoneOutput));

        string clipboardPath = Path.Combine(_environment.DataDirectory, "phone-clipboard.json");
        var preferences = File.Exists(clipboardPath) ? ClipboardPreferences.TryLoad(clipboardPath) : null;
        string phonePath = Path.Combine(_environment.DataDirectory, "android-phone.json");
        if (File.Exists(phonePath) || File.Exists(phonePath + ".bak"))
            _ = Task.Run(() =>
            {
                if (new AndroidPhoneStore(phonePath).Profiles.Any(p => p.AppPublicKeySha256 != null && !p.ManuallyDisconnected && !p.AppTrustRevoked))
                    _window.TryPost(() => _ = Phone);
            });

        if (settings.ExternalAudio.Any(choice => choice.App != null && choice.Visible)) _ = Phone;
        if (hasSavedRoutes || preferences is { Enabled.Length: > 0 } or { Archived.Length: > 0 })
        {
            var p = Phone;
            _window.TryPost(() =>
            {
                if (preferences != null) p.Clipboard.Initialize(preferences);
                if (hasSavedRoutes || p.Clipboard.Enabled)
                {
                    p.StartTracking();
                    _ = p.RefreshInBackgroundAsync();
                }
            });
        }
        if (settings.AirPlayEnabled)
            _window.TryPost(() => _ = AirPlay.SetReceivingAsync(_config, enabled: true));
    }

    private void StopFull()
    {
        externalOutputTopology?.Dispose();
        if (phone != null) phone.Changed -= OnExternalSourcesChanged;
        if (airPlay != null) airPlay.Changed -= OnExternalSourcesChanged;
        airPlay?.Dispose();
        phone?.Dispose();
        externalAudioSettings?.FlushAsync().GetAwaiter().GetResult();
        _audio.ExternalAudio?.Dispose();
        _audio.ExternalAudio = null;
    }

    private void FullSignal(ShellSignal signal)
    {
        if (signal.Kind == ShellSignalKind.Clipboard) phone?.Clipboard.OnClipboardChanged();
        if (signal.Kind is ShellSignalKind.Power or ShellSignalKind.Session) phone?.StartTracking();
    }
}
#endif

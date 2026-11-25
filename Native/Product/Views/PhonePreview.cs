#if SONICROUTE_PHONE
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute.Native.Product;

internal sealed class PhonePreview : IDisposable
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

    internal PhonePreview(string root, NativeApplicationWindow shell, IMainThread owner,
        Func<int, IPcProcessSilenceLease?>? silenceLocalProcess = null,
        Func<AirPlayPreview?>? tryGetAirPlay = null)
    {
        this.owner = owner;
        this.tryGetAirPlay = tryGetAirPlay;
        token = lifetime.Token;

        // ArbitratedAdbTransport is AOT-clean in SonicRoute.AndroidAudio and delegates to external adb.exe on 127.0.0.1:5037
        // when active (matching WPF AndroidAudioHost), falling back to DirectAdbTransport when no server is running.
        Adb = new(new AndroidRuntimeBundle(Path.Combine(root, "PhoneRuntime")));

        string storePath = Path.Combine(root, "android-phone.json");
        MigrateLegacyPhoneStore(root, storePath);
        Store = new(storePath, () => L10n.T("Phone.DefaultName"));
        Settings = new(Path.Combine(root, "android-audio.json"));
        Discovery = new(cancel => Adb.DevicesAsync(cancel, deduplicate: false));
        Connections = new(Store, () => Discovery, Settings.MigrateConnectionPreferences);
        Wireless = new(Adb);
        Sources = new(() => new AndroidAudioSession(Adb, Settings.GetStreaming, Connections.ResolveKey), Settings,
            (phone, app) => Store.IsSmartVolumeEnabled(phone, app));
        Pc = new(() => Adb, silenceLocalProcess);
        Pc.OutputsObserved += (device, outputs) => Store.UpdateOutputs(device, outputs);
        Pc.DeviceDisconnected += OnPcPlaybackDisconnected;
        var runtime = new VirtualDisplayRuntime(Adb);
        Displays = new(() => new InProcessVirtualDisplayClient(Adb, runtime, () => Settings));
        Clipboard = new(Adb, shell, owner, Path.Combine(root, "phone-clipboard.json"),
            d => Store.IsRemoved(d), d => Connections.Resolve(d));

        Sources.Changed += Notify;
        Discovery.Changed += Observe;
        Store.Changed += OnStoreChanged;
        Pc.Changed += Notify;
        Transfers.Changed += _ => Notify();
        Displays.ResourcesRetired += Notify;
        Clipboard.Changed += Notify;
    }

    internal CancellationToken Lifetime => token;

    internal bool HasActivePlaybackConnections =>
        Sources.HasActiveWork || Displays.Count > 0 || Pc.HasActiveWork || tryGetAirPlay?.Invoke()?.Source != null;

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
        _ = RefreshAfterDeviceChangeAsync();
    }

    internal void StartTracking()
    {
        if (Volatile.Read(ref closed) != 0) return;
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
        catch (Exception error) { Error = TranslateError(error); Notify(); }
    }

    private async Task RefreshAfterDeviceChangeAsync()
    {
        try { await RefreshAsync(restore: true).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Error = TranslateError(error); Notify(); }
    }

    internal async Task RefreshAsync(bool restore = false)
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

            var savedRoutes = Settings.GetSavedSources();
            if (savedRoutes.Length > 0)
            {
                // Recover saved wireless phones that have not re-appeared yet.
                foreach (var route in savedRoutes)
                {
                    if (Connections.ResolveKey(route.DeviceKey) != null) continue;
                    var savedProfile = Store.Profiles.FirstOrDefault(p => AndroidDeviceIdentity.StableKey(p.LastDevice) == route.DeviceKey);
                    if (savedProfile?.LastDevice is { } lastDev)
                    {
                        await RecoverMissingWirelessAsync(lastDev, Discovery.Devices ?? devices, deadline.Token).ConfigureAwait(false);
                    }
                }

                foreach (var route in savedRoutes)
                {
                    var online = Connections.ResolveKey(route.DeviceKey);
                    if (online == null || Sources.Sources.Any(s => s.App.Package == route.Package && AndroidDeviceIdentity.Matches(s.Device, online)))
                        continue;
                    var profile = Store.FindProfile(online);
                    if (profile == null || profile.ManuallyDisconnected) continue;
                    var app = route.Package == AndroidApp.DeviceAudioPackage
                        ? AndroidApp.DeviceAudio
                        : profile.RetainedApps.FirstOrDefault(a => a.Package == route.Package)
                            ?? new AndroidApp(route.Package, route.Package, -1, "");
                    try { await Sources.ResumeAsync(online, app, route.Outputs).ConfigureAwait(false); }
                    catch (Exception error) { ExceptionDiagnostics.Record("NativePhone.Resume", error); }
                }
            }
        }
        finally
        {
            scans.Release();
            owner.TryPost(() => { if (Volatile.Read(ref closed) == 0) Clipboard.Reconcile(); });
            Notify();
        }
    }

    internal async Task<bool> RecoverMissingWirelessAsync(AndroidDevice? device, IReadOnlyList<AndroidDevice> devices, CancellationToken cancel)
    {
        if (device == null || AndroidDeviceIdentity.StableKey(device) is not { } key
            || Connections.Resolve(device) != null)
            return false;

        var profile = Store.FindProfile(device);
        if (profile == null || profile.ManuallyDisconnected || Store.IsRemoved(device)
            || profile.WirelessReconnectTarget(discoverFromUsb: false) is not { } target)
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
            var result = await Wireless.ReconnectAsync(target, profile.LastWirelessAddress, attempt.Token, devices,
                retryMissingIdentity: !HasActivePlaybackConnections).ConfigureAwait(false);
            attempt.Token.ThrowIfCancellationRequested();
            if (Connections.Revision != revision || Store.IsRemoved(device) || Store.IsRemoved(result.Device)
                || AndroidConnectionManager.IsAmbiguous(result.Device, result.Devices)
                || Store.FindProfile(device)?.ManuallyDisconnected != false)
                return false;
            attempt.Token.ThrowIfCancellationRequested();
            if (!Discovery.PublishConnection(result, discoveryRevision, current => Connections.AcceptRecovery(current, profile.Id, revision)))
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
            if (device.State != "device" || string.Equals(device.Serial, "offline-fixture", StringComparison.Ordinal)) continue;
            lock (probeTimestamps)
            {
                if (!force && probeTimestamps.TryGetValue(device.Serial, out long last) && now - last < 3000) continue;
                probeTimestamps[device.Serial] = now;
            }
            try
            {
                using var read = CancellationTokenSource.CreateLinkedTokenSource(this.token, token, Connections.OperationToken(device));
                var outputs = await Adb.OutputsAsync(device, read.Token).ConfigureAwait(false);
                if (Connections.IsCurrent(device)) Store.UpdateOutputs(device, outputs);
            }
            catch
            {
                lock (probeTimestamps) probeTimestamps.Remove(device.Serial);
            }
        }
    }

    private void OnPcPlaybackDisconnected(AndroidDevice device)
    {
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await ReconnectSelectedDeviceAsync(deadline.Token).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<AndroidDevice>> ReconnectSelectedDeviceAsync(CancellationToken cancel)
    {
        var selected = Store.Profile;
        var devices = await RefreshSelectedDeviceAsync(cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        if (selected.LastDevice == null) return devices;

        var current = Store.Profile;
        if (current.Id != selected.Id) throw new OperationCanceledException(cancel);
        if (Connections.Resolve(current.LastDevice) == null)
            throw AndroidError.WithCode(new IOException("Device not found."), AndroidErrorCode.ReconnectNoDeviceFound);
        return devices;
    }

    internal async Task<IReadOnlyList<AndroidDevice>> RefreshSelectedDeviceAsync(CancellationToken cancel)
    {
        var selected = Store.Profile;
        if (selected.LastDevice is not { } saved)
            return await Discovery.ReadAsync(cancel).ConfigureAwait(false);

        long revision = Connections.Revision;
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
            if (ready != null && (!saved.IsWifi || !ready.IsWifi && AndroidDeviceIdentity.SamePhysicalDevice(saved, ready)))
            {
                result = await Wireless.EnrichReadyResultAsync(new(ready, devices), !HasActivePlaybackConnections, cancel).ConfigureAwait(false);
            }
            else if (selected.WirelessReconnectTarget(discoverFromUsb: true) is { } target)
            {
                result = await Wireless.ReconnectAsync(target, selected.LastWirelessAddress, cancel, devices,
                    retryMissingIdentity: !HasActivePlaybackConnections).ConfigureAwait(false);
            }
            else return devices;
            cancel.ThrowIfCancellationRequested();
            if (!StillSelected()) return Discovery.Devices ?? devices;

            if (!Discovery.PublishConnection(result, discoveryRevision, current => Connections.Admit(current, revision, selected.Id, requireCurrent: true)))
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
            await CleanupManagedPhoneAsync(aliases).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            if (unpair)
            {
                await Wireless.UnpairAsync(device, deadline.Token).ConfigureAwait(false);
            }
            else
            {
                foreach (var alias in aliases.Where(d => d.IsWifi).DistinctBy(d => d.CommandTarget))
                    await Wireless.DisconnectAsync(alias, deadline.Token).ConfigureAwait(false);
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
            await CleanupManagedPhoneAsync(aliases).ConfigureAwait(false);
        }
        finally
        {
            Connections.CompleteRetirement(aliases);
            Notify();
        }
        await Store.FlushAsync().ConfigureAwait(false);
    }

    private async Task CleanupManagedPhoneAsync(IReadOnlyList<AndroidDevice> aliases)
    {
        foreach (var alias in aliases.DistinctBy(AndroidDeviceIdentity.Key))
        {
            Settings.ForgetPhoneRoutes(alias);
            owner.TryPost(() => { if (Volatile.Read(ref closed) == 0) Clipboard.DisableForDevice(alias); });
            await Task.WhenAll(
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
        return new AndroidPhonePcmPlayer(Adb, device, pcm, sink, cancel,
            (connected, outputs) => { if (Connections.IsCurrent(connected)) Store.UpdateOutputs(connected, outputs); },
            Connections.OperationToken(device));
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
        if (app.IsDeviceAudio)
            app = app with { Name = Store.GetDisplayName(device) };
        device = await ResolveOperationDeviceAsync(device, AndroidErrorCode.DisplayPhoneNotConnected, token).ConfigureAwait(false);

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
                _ = QueueLocked();
            }
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
        Sources.SyncSmartVolume((d, p) => Store.IsSmartVolumeEnabled(d, p));
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
    internal bool HasPhone => phone != null;
    internal bool HasAirPlay => airPlay != null;
    internal PhonePreview Phone
    {
        get
        {
            if (phone != null) return phone;
            var created = new PhonePreview(_environment.DataDirectory, _window, this,
                _audio.ExternalAudio != null ? _audio.ExternalAudio.AcquireLocalSilenceLease : null,
                () => HasAirPlay ? AirPlay : null);
            created.Changed += OnExternalSourcesChanged;
            phone = created;
            return created;
        }
    }
    internal AirPlayPreview AirPlay
    {
        get
        {
            if (airPlay != null) return airPlay;
            var created = new AirPlayPreview(_environment.IsPackaged,
                (pcm, endpoint, cancel) => Phone.CreatePhonePcmPlayer(pcm, endpoint, cancel));
            created.Changed += OnExternalSourcesChanged;
            airPlay = created;
            return created;
        }
    }
    private AirPlayPreview? airPlay;

    private void OnExternalSourcesChanged()
    {
        _audio.InvalidateAppsCache();
        NotifyAudioVisualChanged();
    }

    private void RestoreFull()
    {
        string settingsPath = Path.Combine(_environment.DataDirectory, "android-audio.json");
        bool hasSavedRoutes = false;
        if (File.Exists(settingsPath))
        {
            var settings = new AndroidAudioSettingsStore(settingsPath);
            hasSavedRoutes = settings.GetSavedSources().Length != 0;
        }

        string clipboardPath = Path.Combine(_environment.DataDirectory, "phone-clipboard.json");
        bool hasClipboard = File.Exists(clipboardPath) && PhoneClipboard.HasConfiguredDevices(clipboardPath);

        if (hasSavedRoutes || hasClipboard)
        {
            var p = Phone;
            p.StartTracking();
            if (hasClipboard) _ = p.Clipboard.InitializeAsync();
            _ = p.RefreshAsync(restore: hasSavedRoutes);
        }
    }

    private void StopFull()
    {
        if (phone != null) phone.Changed -= OnExternalSourcesChanged;
        if (airPlay != null) airPlay.Changed -= OnExternalSourcesChanged;
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

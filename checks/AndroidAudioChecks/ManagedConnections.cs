using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task ManagedConnectionsAsync(string output)
    {
        string root = Path.Combine(output, "managed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AndroidDevice usb = new("serial-A", "Same model", "device", false, "serial-A", "usb-1")
            { IdentityFingerprint = "proof-A", ConnectionTarget = "test-usb-1" };
        AndroidDevice wifi = usb with { Serial = "192.0.2.10:40001", IsWifi = true, TransportId = "wifi-2", ConnectionTarget = "test-wifi-2" };
        AndroidDevice other = usb with { Serial = "serial-B", HardwareId = "serial-B", IdentityFingerprint = "proof-B",
            TransportId = "usb-3", ConnectionTarget = "test-usb-3" };
        AndroidApp app = new("cn.fixture.active", "Active app", 1001, "media");
        AndroidApp archivedApp = new("cn.fixture.archived", "Archived app", 1002, "media");
        await CheckServiceAddressPersistenceAsync(root, usb, wifi);

        var store = new AndroidPhoneStore(Path.Combine(root, "dual.json"));
        var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult<IReadOnlyList<AndroidDevice>>([]));
        var manager = new AndroidConnectionManager(store, () => discovery);
        discovery.Publish([usb, wifi]); manager.ObserveCurrent();
        Check(store.Profiles.Count == 1 && manager.OnlineDevices.Count == 1 && manager.IsCurrent(usb) && manager.IsCurrent(wifi),
            "verified USB and Wi-Fi aliases produce one managed profile and two legitimate transports");
        string id = store.Profile.Id;
        var legacyUsbTarget = store.Profile.WirelessReconnectTarget(discoverFromUsb: true);
        Check(store.Profile.WirelessReconnectTarget(discoverFromUsb: false) == null && legacyUsbTarget is { IsWifi: true, ConnectionTarget: null }
            && legacyUsbTarget.HardwareId == usb.HardwareId && legacyUsbTarget.IdentityFingerprint == usb.IdentityFingerprint
            && AndroidDeviceIdentity.MatchesWirelessService(legacyUsbTarget, new("adb-serial-A-ABC", AndroidWirelessConnection.ConnectType, wifi.Serial)),
            "a USB-only profile can discover its own wireless service on explicit reconnect without reusing the USB selector");
        Check(manager.Admit(new(wifi, [usb, wifi]), manager.Revision) && store.Profile.Id == id
            && store.Profiles.Count == 1, "explicit Wi-Fi admission reuses USB's profile ID");
        Check(manager.Resolve(usb)?.CommandTarget == wifi.CommandTarget,
            "all new operations use the canonical selected live transport, not an old USB object");
        discovery.Publish([usb]); manager.ObserveCurrent();
        await store.FlushAsync();
        var afterUnplug = new AndroidPhoneStore(store.FilePath).Profile;
        var wirelessTarget = afterUnplug.WirelessReconnectTarget(discoverFromUsb: false);
        Check(afterUnplug.Id == id && afterUnplug.LastDevice?.IsWifi == false && wirelessTarget?.Serial == wifi.Serial
            && wirelessTarget.IdentityFingerprint == usb.IdentityFingerprint && wirelessTarget.ConnectionTarget == null,
            "an observed switch to USB preserves the previous Wi-Fi endpoint and identity across a process restart");
        discovery.Publish([usb, wifi, other]); manager.ObserveCurrent();
        Check(store.Profiles.Count == 2 && manager.Resolve(other)?.CommandTarget == other.CommandTarget,
            "same-model phones with distinct physical identities retain separate profiles and targets");
        var duplicate = other with { HardwareId = usb.HardwareId, Serial = usb.Serial, TransportId = "usb-4", ConnectionTarget = "test-usb-4" };
        discovery.Publish([usb, wifi, duplicate]); manager.ObserveCurrent();
        Check(manager.Resolve(usb) == null && !manager.Admit(new(duplicate, [usb, duplicate]), manager.Revision)
            && store.Profiles.Count == 2, "duplicate hardware ID with conflicting evidence cannot route, admit or merge");
        discovery.Publish([duplicate]); manager.ObserveCurrent();
        Check(manager.Resolve(usb) == null && !manager.Admit(new(duplicate, [duplicate]), manager.Revision),
            "a later device reusing the same serial cannot overwrite the saved fingerprint");
        var missingProof = wifi with { IdentityFingerprint = null };
        discovery.Publish([missingProof]);
        Check(!manager.Admit(new(missingProof, [missingProof]), manager.Revision, id) && manager.Resolve(wifi) == null,
            "reconnecting cannot erase a previously verified fingerprint when the new identity query fails");
        var unknown = wifi with { HardwareId = null, IdentityFingerprint = null, Serial = "192.0.2.10:40002", ConnectionTarget = "test-unknown" };
        discovery.Publish([usb, unknown]); manager.ObserveCurrent();
        Check(store.Profiles.Count == 2 && manager.Resolve(unknown) == null && !manager.Admit(new(unknown, [usb, unknown]), manager.Revision),
            "failed identity lookup is not merged by model, address or a guess about USB/Wi-Fi aliasing");
        discovery.Publish([usb, wifi]); manager.ObserveCurrent();
        AndroidDevice[] disconnecting = [usb, wifi];
        AndroidDevice[] removing = [wifi];
        manager.BeginRetirement(disconnecting);
        manager.BeginRetirement(removing);
        manager.CompleteRetirement(disconnecting);
        Check(manager.Resolve(usb) == null && manager.OperationToken(wifi).IsCancellationRequested
            && !manager.Admit(new(wifi, [usb, wifi]), manager.Revision, id),
            "a completed disconnect cannot release a concurrent removal of the same phone");
        manager.CompleteRetirement(removing);
        Check(manager.Admit(new(wifi, [usb, wifi]), manager.Revision, id) && manager.IsCurrent(usb),
            "the final cleanup releases both verified aliases for explicit admission");

        // Drop USB at successive reads during token acquisition. The Wi-Fi stream
        // already in use must keep its lifetime through the discovery change.
        var wifiOnly = new AndroidDeviceDiscovery(_ => Task.FromResult<IReadOnlyList<AndroidDevice>>([wifi]));
        wifiOnly.Publish([wifi]);
        bool operationsSurviveRescan = true;
        for (int cutover = 1; cutover <= 8; ++cutover)
        {
            bool changingSnapshot = false;
            int reads = 0;
            var rescanManager = new AndroidConnectionManager(store, () =>
                changingSnapshot && ++reads >= cutover ? wifiOnly : discovery);
            var alreadyRunning = rescanManager.OperationToken(wifi);
            changingSnapshot = true;
            _ = rescanManager.OperationToken(usb);
            changingSnapshot = false;
            operationsSurviveRescan &= !alreadyRunning.IsCancellationRequested
                && !rescanManager.OperationToken(wifi).IsCancellationRequested;
        }
        Check(operationsSurviveRescan,
            "a transient discovery change while acquiring a USB token preserves the active Wi-Fi token and later operations");
        var lifetime = manager.OperationToken(wifi);
        manager.SetDisconnected(wifi, true);
        Check(lifetime.IsCancellationRequested && manager.Get(wifi).State == AndroidManagedConnectionState.ManuallyDisconnected,
            "manual disconnect revokes the operation lifetime even with ADB still reporting device");
        await store.FlushAsync();
        store = new AndroidPhoneStore(store.FilePath);
        discovery = new AndroidDeviceDiscovery(_ => Task.FromResult<IReadOnlyList<AndroidDevice>>([]));
        manager = new AndroidConnectionManager(store, () => discovery);
        Check(manager.Resolve(wifi) == null && manager.Get(wifi).State == AndroidManagedConnectionState.ManuallyDisconnected,
            "restart preserves explicit disconnect; persisted device state grants no live access");
        discovery.Publish([usb, wifi]); manager.ObserveCurrent();
        Check(manager.Resolve(wifi) == null && manager.Admit(new(wifi, [usb, wifi]), manager.Revision, id)
            && manager.Resolve(wifi) != null, "only explicit reconnect re-enables a manually stopped phone");
        var pending = new TaskCompletionSource<IReadOnlyList<AndroidDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery = new AndroidDeviceDiscovery(_ => pending.Task);
        discovery.Publish([usb, wifi]);
        var active = manager.OperationToken(wifi);
        long oldRevision = manager.Revision;
        var scanning = discovery.ReadAsync(default);
        Check(manager.Remove(id, out var aliases) && aliases.Any(d => d.IsWifi) && aliases.Any(d => !d.IsWifi)
            && active.IsCancellationRequested, "removing an active phone revokes operations and enumerates both transport aliases for cleanup");
        pending.SetResult([usb, wifi]); await scanning; manager.ObserveCurrent();
        Check(store.Profiles.All(p => p.Id != id) && manager.Resolve(wifi) == null
            && !manager.Admit(new(wifi, [usb, wifi]), oldRevision, id), "a concurrent scan or stale explicit operation cannot resurrect a removed profile");
        await store.FlushAsync(); store = new AndroidPhoneStore(store.FilePath);
        manager = new AndroidConnectionManager(store, () => discovery); discovery.Publish([usb, wifi]); manager.ObserveCurrent();
        Check(store.IsRemoved(wifi) && manager.Resolve(wifi) == null && manager.OperationToken(wifi).IsCancellationRequested,
            "removal persists across restart and blocks stale operation objects");

        var routes = new AndroidAudioSettingsStore(Path.Combine(root, "cleanup-audio.json"));
        string?[] localOutputs = [null, "speakers-A"];
        byte[] wire = BuildWire();
        await using (var sources = new AndroidAudioManager(() => new AndroidAudioSession(
            () => new FakeTransport(wire, () => { }), (_, _) => new FakePlayer()), routes))
        {
            var oldDisplay = await sources.AddAsync(wifi, app, false, displayOwned: true);
            await sources.RemovePhoneAsync(wifi);
            var newDisplay = await sources.AddAsync(wifi, app, false, displayOwned: true);
            await ((IMultiOutputAudioSession)newDisplay.Session).SetOutputsAsync(localOutputs);
            await sources.ReleaseDisplayAudioAsync(oldDisplay, app.Package);
            Check(ReferenceEquals(sources.FindSource(newDisplay.Id), newDisplay)
                && newDisplay.Session.State.State == CaptureState.Receiving
                && routes.Get(wifi, app.Package).ResumeOutputs?.SequenceEqual(localOutputs) == true,
                "a late close from before removal preserves the new display's capture and saved outputs after re-add");
        }

        string target = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(other), AndroidOutputSink.Current.Key);
        routes.RememberSourceOutputs(wifi, app.Package, [AndroidAudioSession.SourcePhoneOutputId, target]);
        routes.RememberSourceOutputs(wifi, archivedApp.Package, [null, target, "speakers-A"]);
        routes.RememberSourceOutputs(other, app.Package, localOutputs);
        routes.ForgetPhoneRoutes(other);
        await routes.FlushAsync();
        var cleaned = new AndroidAudioSettingsStore(routes.FilePath);
        Check(cleaned.Get(wifi, app.Package).ResumeOutputs == null && cleaned.Get(other, app.Package).ResumeOutputs == null
            && cleaned.Get(wifi, archivedApp.Package).ResumeOutputs?.SequenceEqual(localOutputs) == true,
            "removing an inactive target clears its own and single remaining playback intents while preserving the other two outputs across restart");

        async Task LegacyConflict(bool fail)
        {
            string suffix = fail ? "failure" : "success";
            var library = new AndroidPhoneStore(Path.Combine(root, "legacy-" + suffix + ".json"));
            var settings = new AndroidAudioSettingsStore(Path.Combine(root, "audio-" + suffix + ".json"));
            var archived = wifi with { Serial = "192.0.2.10:40009", TransportId = "old-WiFi", ConnectionTarget = null, IdentityFingerprint = null };
            var endpoint = wifi with { HardwareId = null, IdentityFingerprint = null, ConnectionTarget = null };
            library.RememberDevice(archived); library.Rename("Archived Xiaomi"); library.Retain(archivedApp);
            library.RemoveDevice(library.Profile.Id);
            library.RememberDevice(endpoint); library.Rename("Current Xiaomi"); library.Retain(app);
            string primary = library.Profile.Id;
            settings.Remember(archived, app.Package, 17, null);
            settings.RememberWindow(archived, app.Package, new(901, 701));
            settings.Remember(endpoint, app.Package, 63, null);
            settings.RememberDisplay(endpoint, app.Package, new(720, 1280));
            settings.RememberStreaming(endpoint, new AndroidStreamSettings { MaxFps = 30 });
            await library.FlushAsync(); await settings.FlushAsync();
            var oldLibrary = File.ReadAllText(library.FilePath);
            var currentDiscovery = new AndroidDeviceDiscovery(_ => Task.FromResult<IReadOnlyList<AndroidDevice>>([wifi, usb]));
            var connections = new AndroidConnectionManager(library, () => currentDiscovery, settings.MigrateConnectionPreferences);
            currentDiscovery.Publish([wifi, usb]); connections.ObserveCurrent();
            Check(library.Profiles.Count == 1 && library.Profile.Id == primary && connections.Resolve(endpoint) == null,
                "legacy endpoint profile and archived physical tombstone do not passively merge or display as connected (" + suffix + ")");
            if (fail)
            {
                // Hold only the isolated fixture's destination file against atomic replacement.
                bool failed = false;
                using (var held = new FileStream(library.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    try { connections.Admit(new(wifi, [wifi, usb]), connections.Revision, primary); }
                    catch (IOException) { failed = true; }
                Check(failed && File.ReadAllText(library.FilePath) == oldLibrary && library.Profile.LastDevice == endpoint,
                    "injected library write failure preserves the original profile and file");
                Check(settings.Get(endpoint, app.Package).Volume == 63 && settings.Get(endpoint, app.Package).DisplaySize == new AndroidVirtualDisplaySize(720, 1280)
                    && settings.Get(archived, app.Package).Volume == 17 && settings.Get(archived, app.Package).WindowSize == new AndroidWindowSize(901, 701),
                    "failed cross-file migration rolls back stable preferences while retaining endpoint volume and layout");
                Check(Directory.GetFiles(root, "audio-failure.json.pre-rebind-*.bak").Length == 1,
                    "failed migration retains a durable recovery copy");
            }
            Check(connections.Admit(new(wifi, [wifi, usb]), connections.Revision, primary), "explicit rebind repairs historic removal conflict (" + suffix + ")");
            Check(library.Profile.Id == primary && library.Profile.Name == "Current Xiaomi" && library.Profiles.Count == 1
                && library.Profile.RetainedApps.Select(a => a.Package).Order().SequenceEqual(new[] { app.Package, archivedApp.Package }.Order())
                && !library.IsRemoved(wifi) && connections.Resolve(wifi) == wifi,
                "rebind preserves active ID/name/apps, restores archived apps and clears only matching tombstones");
            var migrated = settings.Get(wifi, app.Package);
            Check(migrated.Volume == 63 && migrated.DisplaySize == new AndroidVirtualDisplaySize(720, 1280)
                && migrated.WindowSize == new AndroidWindowSize(901, 701) && settings.GetStreaming(wifi).MaxFps == 30
                && settings.Get(endpoint, app.Package).Volume == 63,
                "active volume, display settings, archived window size and stream preferences survive rebind");
            var loaded = new AndroidPhoneStore(library.FilePath);
            var reloadedSettings = new AndroidAudioSettingsStore(settings.FilePath);
            var empty = new AndroidDeviceDiscovery(_ => Task.FromResult<IReadOnlyList<AndroidDevice>>([]));
            var restarted = new AndroidConnectionManager(loaded, () => empty);
            Check(loaded.Profile.Id == primary && !loaded.IsRemoved(wifi) && reloadedSettings.Get(wifi, app.Package).Volume == 63
                && restarted.Resolve(wifi) == null, "successful migration persists, but restart requires current discovery");
        }
        await LegacyConflict(false); await LegacyConflict(true);
        string selector = AdbTransportTarget.CreateSocket("42", "fixture-daemon")!;
        Check(AdbTransportTarget.SocketSelectCommand(selector) == "host:transport-id:42"
            && AdbTransportTarget.SocketHostCommand(selector, "features") == "host-transport-id:42:features",
            "ADB socket commands use transport IDs rather than reusable addresses");
        Console.WriteLine($"PASS {_checks} managed connection assertions; isolated preferences and mocked transports only.");
    }

    private static async Task CheckServiceAddressPersistenceAsync(string root, AndroidDevice usb, AndroidDevice wifi)
    {
        var store = new AndroidPhoneStore(Path.Combine(root, "service-endpoint.json"));
        var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult<IReadOnlyList<AndroidDevice>>([]));
        var manager = new AndroidConnectionManager(store, () => discovery);
        var alias = wifi with { Serial = "adb-serial-A-current._adb-tls-connect._tcp" };
        AndroidConnectionResult current = new(alias, [alias]);
        discovery.Publish(current.Devices);
        Check(manager.Admit(current, manager.Revision) && store.Profile.LastWirelessAddress == null,
            "a live service name alone cannot invent a saved numeric endpoint");
        var id = store.Profile.Id;
        Check(manager.Admit(current with { WirelessAddress = wifi.Serial }, manager.Revision, id, requireCurrent: true)
            && store.Profile.LastWirelessAddress == wifi.Serial,
            "explicit admission stores a newly proven endpoint even when the selected live alias is unchanged");
        const string newAddress = "192.0.2.10:40003";
        var recovered = current with { WirelessAddress = newAddress };
        Check(manager.AcceptRecovery(recovered, id, manager.Revision) && store.Profile.LastWirelessAddress == newAddress,
            "recovery updates only the already-admitted device's endpoint");
        var stale = current with { WirelessAddress = "192.0.2.11:40005" };
        Check(!manager.AcceptRecovery(stale, id, manager.Revision - 1) && !manager.AcceptRecovery(stale, "unrelated", manager.Revision)
            && store.Profile.LastWirelessAddress == newAddress, "a stale recovery or another profile cannot overwrite the last address");
        discovery.Publish([usb]); manager.ObserveCurrent();
        await store.FlushAsync();
        var loaded = new AndroidPhoneStore(store.FilePath).Profile;
        Check(loaded.LastDevice == (usb with { ConnectionTarget = null }) && loaded.LastWirelessAddress == newAddress
            && loaded.Id == id, "a service-name endpoint survives a passive switch to USB and a process restart");
        manager.Remove(id, out _);
        Check(!manager.AcceptRecovery(stale, id, manager.Revision) && store.IsRemoved(alias),
            "late address updates cannot restore a removed library");
    }
}

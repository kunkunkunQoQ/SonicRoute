using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task PhoneLibraryAsync(string[] args)
    {
        // A unique fixture root is intentionally retained; this check never cleans or reuses user files.
        string directory = Path.Combine(args.Length > 1 ? Path.GetDirectoryName(Path.GetFullPath(args[1]))! : AppContext.BaseDirectory,
            "phone-library-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string legacyPath = Path.Combine(directory, "legacy.json");
        var legacyDevice = new AndroidDevice("legacy-usb", "Legacy phone", "device", false);
        var legacyApp = new AndroidApp("cn.test.shared", "Legacy name", 101, "media");
        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new
        {
            Version = 1, Name = "Legacy", Device = legacyDevice, Apps = new[] { legacyApp }, HiddenPackages = Array.Empty<string>()
        }));
        var legacy = new AndroidPhoneStore(legacyPath);
        Check(legacy.Profile.RetainedApps.Count == 1 && legacy.Profile.QuickPanelApps.Count == 1
            && legacy.Profile.QuickPanelApps[0].Package == legacyApp.Package
            && legacy.IsSmartVolumeEnabled(legacyDevice, legacyApp.Package),
            "v1 phone library migration defaults every retained app to visible and SmartVolume enabled");

        int legacyChanges = 0; legacy.Changed += () => ++legacyChanges;
        legacy.SetSmartVolume(legacyApp.Package, false);
        Check(legacy.Profile.DisabledSmartVolumeApps.SequenceEqual(new[] { legacyApp.Package })
            && !legacy.IsSmartVolumeEnabled(legacyDevice, legacyApp.Package)
            && legacy.Profile.QuickPanelApps.Count == 1, "SmartVolume toggle updates the app's SmartVolume setting while keeping pinned apps in QuickPanelApps");
        await legacy.FlushAsync();
        var hiddenReload = new AndroidPhoneStore(legacyPath);
        Check(hiddenReload.Profile.DisabledSmartVolumeApps.SequenceEqual(new[] { legacyApp.Package })
            && !hiddenReload.IsSmartVolumeEnabled(legacyDevice, legacyApp.Package)
            && hiddenReload.Profile.QuickPanelApps.Count == 1, "disabled SmartVolume state persists across reload");
        int afterHide = legacyChanges;
        legacy.SetSmartVolume(legacyApp.Package, false);
        Check(legacyChanges == afterHide, "repeating an unchanged SmartVolume request is a no-op");
        legacy.SetSmartVolume(legacyApp.Package, true);
        Check(legacy.Profile.DisabledSmartVolumeApps.Count == 0 && legacy.IsSmartVolumeEnabled(legacyDevice, legacyApp.Package)
            && ReferenceEquals(legacy.Profile.QuickPanelApps, legacy.Profile.RetainedApps),
            "QuickPanelApps always reuses the retained-app list and SmartVolume restores to default on");
        legacy.SetSmartVolume(legacyApp.Package, false);
        legacy.Remove(legacyApp.Package);
        await legacy.FlushAsync();
        var legacyReload = new AndroidPhoneStore(legacyPath);
        Check(legacyReload.Profile.RetainedApps.Count == 0 && legacyReload.Profile.QuickPanelApps.Count == 0
            && legacyReload.Profile.DisabledSmartVolumeApps.Count == 0,
            "unpinning clears retained, quick-panel and SmartVolume state and persists across reload");

        string file = Path.Combine(directory, "multi.json");
        var store = new AndroidPhoneStore(file);
        var phoneAUsb = new AndroidDevice("usb-A", "A USB", "device", false, "hw-A", "1");
        var phoneAWifi = new AndroidDevice("192.0.2.10:4000", "A mDNS", "device", true, "hw-A", "2");
        var phoneAIp = new AndroidDevice("192.0.2.10:5555", "A IP", "device", true, "hw-A", "3");
        var phoneB = new AndroidDevice("usb-B", "B USB", "device", false, "hw-B", "4");
        var appA = new AndroidApp("cn.test.shared", "App on A", 201, "media");
        var appB = new AndroidApp("cn.test.shared", "App on B", 302, "video");

        store.RememberDevice(phoneAUsb);
        store.Retain(appA);
        store.SetSmartVolume(appA.Package, false);
        store.RememberDevice(phoneB);
        store.Retain(appB);
        Check(store.Profiles.Count == 2 && store.Profile.LastDevice == phoneB,
            "explicitly remembering a second phone creates and selects a separate library");
        Check(store.Profiles.Single(p => p.LastDevice?.HardwareId == "hw-A").RetainedApps.Single().Name == appA.Name
            && !store.IsSmartVolumeEnabled(phoneAUsb, appA.Package)
            && store.IsSmartVolumeEnabled(phoneB, appB.Package)
            && store.Profile.RetainedApps.Single().Uid == appB.Uid && store.Profile.QuickPanelApps.Count == 1,
            "same package keeps independent names, UIDs and SmartVolume state across phones");

        store.ObserveDevices(new[] { phoneAUsb with { Name = "A scanned" }, phoneB with { Name = "B scanned" } });
        Check(store.Profile.LastDevice?.HardwareId == "hw-B" && store.Profile.RetainedApps.Single().Name == appB.Name,
            "device discovery updates device records without changing selection or app metadata");
        store.RememberDevice(phoneAWifi);
        Check(store.Profiles.Count == 2 && store.Profile.LastDevice == phoneAWifi,
            "USB and mDNS transports with the same hardware identity merge into one selected profile");
        store.RememberDevice(phoneAIp);
        Check(store.Profiles.Count == 2 && store.Profile.LastDevice == phoneAIp,
            "USB and IP transports with the same hardware identity continue to merge");
        string phoneBId = store.Profiles.Single(p => p.LastDevice?.HardwareId == "hw-B").Id;
        Check(store.FindProfile(phoneAWifi)?.Id == store.Profile.Id && !store.SelectProfile("missing-profile")
            && store.SelectProfile(phoneBId) && store.Profile.Id == phoneBId,
            "identity lookup resolves alternate transports and profile switching rejects unknown IDs");

        store.UpdateMetadata(phoneAUsb, new[] { appA with { Name = "A refreshed", Uid = 211 } });
        Check(store.Profiles.Single(p => p.LastDevice?.HardwareId == "hw-A").RetainedApps.Single().Name == "A refreshed"
            && store.Profile.Id == phoneBId,
            "delayed metadata refresh targets its device profile without switching the user's selection");
        store.SelectProfile(store.Profiles.Single(p => p.LastDevice?.HardwareId == "hw-B").Id);
        store.ObserveDevices(new[] { phoneAWifi, phoneB });
        Check(store.Profile.LastDevice?.HardwareId == "hw-B" && store.Profile.RetainedApps.Single().Name == appB.Name,
            "switching libraries and scanning another phone never overwrites the selected library contents");

        int changes = 0; store.Changed += () => ++changes;
        var selectedList = store.Profile.QuickPanelApps;
        store.SetSmartVolume(appB.Package, true);
        store.Retain(appB);
        store.UpdateMetadata(phoneB, new[] { appB });
        Check(changes == 0 && ReferenceEquals(selectedList, store.Profile.QuickPanelApps),
            "unchanged SmartVolume, retain and metadata events emit no change and preserve list references");
        store.Rename("Phone B");
        await store.FlushAsync();
        var restored = new AndroidPhoneStore(file);
        Check(restored.Profiles.Count == 2 && restored.Profile.Name == "Phone B"
            && restored.Profile.RetainedApps.Single().Name == appB.Name
            && restored.Profiles.Single(p => p.LastDevice?.HardwareId == "hw-A").RetainedApps.Single().Name == "A refreshed",
            "reload restores selected library and both independent retained-app lists");

        string legacyIpPath = Path.Combine(directory, "legacy-ip.json");
        var legacyIp = new AndroidDevice("192.0.2.11:5555", "Saved IP", "device", true);
        var legacyIpApp = new AndroidApp("cn.test.legacyip", "Saved IP app", 611, "media");
        await File.WriteAllTextAsync(legacyIpPath, JsonSerializer.Serialize(new
        {
            Version = 1, Name = "Saved IP phone", Device = legacyIp, Apps = new[] { legacyIpApp }, HiddenPackages = Array.Empty<string>()
        }));
        var legacyIpStore = new AndroidPhoneStore(legacyIpPath);
        var legacyMdns = new AndroidDevice("adb-hw-ip._adb-tls-connect._tcp", "mDNS", "device", true, "hw-legacy-ip");
        var identifiedIp = legacyIp with { HardwareId = "hw-legacy-ip", Name = "IP transport" };
        legacyIpStore.ObserveDevices(new[] { legacyMdns, identifiedIp });
        Check(legacyIpStore.Profiles.Count == 1 && legacyIpStore.Profile.Name == "Saved IP phone"
            && legacyIpStore.Profile.RetainedApps.Single() == legacyIpApp,
            "legacy IP-only library is upgraded before mDNS-first transport deduplication and keeps its saved data");

        var mergeStore = new AndroidPhoneStore(Path.Combine(directory, "stale-endpoint-profiles.json"));
        var mergeUsb = new AndroidDevice("usb-merge", "USB phone", "device", false, "hw-merge", "11");
        var mergeIp = new AndroidDevice("192.0.2.12:5555", "Old IP", "device", true);
        var usbSavedApp = new AndroidApp("cn.test.usbmember", "USB member", 701, "media");
        var ipSavedApp = new AndroidApp("cn.test.ipmember", "IP member", 702, "other");
        mergeStore.RememberDevice(mergeUsb);
        mergeStore.Rename("Saved USB name");
        mergeStore.Retain(usbSavedApp);
        mergeStore.SetSmartVolume(usbSavedApp.Package, false);
        mergeStore.RememberDevice(mergeIp);
        mergeStore.Rename("Saved IP name");
        mergeStore.Retain(ipSavedApp);
        mergeStore.SetSmartVolume(ipSavedApp.Package, false);
        string selectedIpId = mergeStore.Profile.Id;
        var identifiedMergeIp = mergeIp with { HardwareId = "hw-merge", Name = "IP resolved" };
        var mergeMdns = new AndroidDevice("adb-merge._adb-tls-connect._tcp", "mDNS resolved", "device", true, "hw-merge");
        mergeStore.ObserveDevices(new[] { mergeMdns, identifiedMergeIp });
        Check(mergeStore.Profiles.Count == 1 && mergeStore.Profile.Id == selectedIpId
            && mergeStore.Profile.Name == "Saved IP name" && mergeStore.SelectProfile(selectedIpId)
            && mergeStore.Profile.RetainedApps.Select(a => a.Package).ToHashSet(StringComparer.Ordinal)
                .SetEquals(new[] { usbSavedApp.Package, ipSavedApp.Package })
            && mergeStore.Profile.DisabledSmartVolumeApps.ToHashSet(StringComparer.Ordinal)
                .SetEquals(new[] { usbSavedApp.Package, ipSavedApp.Package })
            && mergeStore.Profile.QuickPanelApps.Count == 2,
            "raw scan merges stale USB and IP libraries, preserves selected identity, membership and SmartVolume settings");

        string settingsPath = Path.Combine(directory, "audio-settings.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new
        {
            Version = 1,
            Apps = new Dictionary<string, AndroidAppAudioSettings>
            {
                [appA.Package] = new(37, "legacy-output")
            }
        }));
        var settings = new AndroidAudioSettingsStore(settingsPath);
        Check(settings.Get(appA.Package) is { Volume: 37, OutputId: "legacy-output" }
            && settings.Get(phoneAUsb, appA.Package) is { Volume: 37, OutputId: "legacy-output" },
            "legacy v1 app volume remains readable through generic and device-aware lookup");
        settings.Remember(phoneAUsb, appA.Package, 41, "output-A");
        settings.Remember(phoneB, appB.Package, 73, "output-B");
        await settings.FlushAsync();
        var settingsReload = new AndroidAudioSettingsStore(settingsPath);
        Check(settingsReload.Get(phoneAUsb, appA.Package) is { Volume: 41, OutputId: "output-A" }
            && settingsReload.Get(phoneAWifi, appA.Package) is { Volume: 41, OutputId: "output-A" }
            && settingsReload.Get(phoneB, appB.Package) is { Volume: 73, OutputId: "output-B" }
            && settingsReload.Get(appA.Package).Volume == 37,
            "same-package audio settings persist independently per phone while preserving the legacy fallback");

        string catalogPath = Path.Combine(directory, "catalog.json");
        var catalogFetches = new Dictionary<string, int>(StringComparer.Ordinal);
        var catalog = new AndroidAppCatalogCache(catalogPath, (device, token) =>
        {
            token.ThrowIfCancellationRequested();
            string key = AndroidDeviceIdentity.Key(device);
            catalogFetches[key] = catalogFetches.GetValueOrDefault(key) + 1;
            var app = new AndroidApp(appA.Package, key == "device:hw-A" ? "Catalog A" : "Catalog B",
                key == "device:hw-A" ? 401 : 502, "media");
            return Task.FromResult<IReadOnlyList<AndroidApp>>(new[] { app });
        });
        var catalogA = await catalog.GetAsync(phoneAUsb, false, default);
        var catalogB = await catalog.GetAsync(phoneB, false, default);
        var catalogAAlternateTransport = await catalog.GetAsync(phoneAWifi, false, default);
        Check(catalogFetches.Count == 2 && catalogFetches.GetValueOrDefault("device:hw-A") == 1
            && catalogFetches.GetValueOrDefault("device:hw-B") == 1
            && catalogAAlternateTransport.Single().Name == catalogA.Single().Name
            && catalogB.Single().Name == "Catalog B",
            "on-demand catalogues fetch once per phone, switch independently, and share snapshots across transports");
        int unexpectedFetches = 0;
        var catalogReload = new AndroidAppCatalogCache(catalogPath, (device, token) =>
        {
            ++unexpectedFetches;
            return Task.FromResult<IReadOnlyList<AndroidApp>>(Array.Empty<AndroidApp>());
        });
        var diskA = await catalogReload.GetAsync(phoneAWifi, false, default);
        var diskB = await catalogReload.GetAsync(phoneB, false, default);
        var diskAUsb = await catalogReload.GetAsync(phoneAUsb, false, default);
        Check(unexpectedFetches == 0 && diskA.Single().Name == "Catalog A" && diskB.Single().Name == "Catalog B"
            && diskAUsb.Single().Name == "Catalog A",
            "restart reloads each phone's own disk catalogue and alternate transports hit the same saved identity");

        if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
            await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new
            {
                Success = true, Assertions = _checks, FixtureDirectory = directory,
                DeviceAccess = false, CleanupPerformed = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} phone-library assertions; no ADB or device access.");
    }
}

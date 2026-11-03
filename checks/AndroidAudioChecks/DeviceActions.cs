using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task DeviceActionsAsync(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var one = new AndroidDevice("10.0.0.1:4000", "Same model", "offline", true, "fixture-one");
        var alias = one with { Serial = "adb-fixture-one-changing._adb-tls-connect._tcp", State = "device" };
        var usb = one with { Serial = "fixture-one", State = "device", IsWifi = false };
        var two = one with { Serial = "10.0.0.1:5000", HardwareId = "fixture-two", State = "device" };
        var app = new AndroidApp("cn.test.music", "Music", 1001, "media");
        var store = new AndroidPhoneStore(Path.Combine(output, "phones.json"));
        store.RememberDevice(one); store.Rename("First"); store.Retain(app); string first = store.Profile.Id;
        store.RememberDevice(two); store.Rename("Second"); store.Retain(app with { Package = "cn.test.second" });
        string second = store.Profile.Id; store.SelectProfile(first);
        Check(store.RemoveDevice(first, [alias, usb, two]) && store.Profiles.Count == 1 && store.Profile.Id == second
            && store.Profile.Name == "Second" && store.Profile.RetainedApps[0].Package == "cn.test.second",
            "offline removal selects the remaining phone and preserves its name and library");
        Check(store.IsRemoved(one) && store.IsRemoved(alias) && store.IsRemoved(usb) && !store.IsRemoved(two),
            "removed identities cover known USB and wireless aliases without matching by model or IP");
        store.ObserveDevices([alias, usb, two]);
        Check(store.Profiles.Count == 1 && store.Profile.Id == second,
            "stale live discovery cannot re-add a removed phone");
        await store.FlushAsync(); store = new AndroidPhoneStore(store.FilePath);
        store.ObserveDevices([alias with { Serial = "adb-fixture-one-new._adb-tls-connect._tcp", HardwareId = null }, two]);
        Check(store.Profiles.Count == 1 && store.IsRemoved(alias), "removal suppression survives restart and changing mDNS suffix");
        int changed = 0; store.Changed += () => changed++;
        Check(!store.RemoveDevice(first) && changed == 0, "repeated removal is a no-op without persistence notifications");
        store.RememberDevice(alias);
        Check(store.Profiles.Count == 2 && !store.IsRemoved(one) && !store.IsRemoved(usb)
            && store.Profile.RetainedApps.Count == 0 && store.Profile.Name == "我的手机",
            "explicit adding restores a fresh device profile and clears its removed aliases");
        string added = store.Profile.Id; store.SelectProfile(second); store.RemoveDevice(added);
        Check(store.Profile.Id == second, "removing another phone retains the selected phone");
        Check(store.RemoveDevice(second) && store.Profiles.Count == 1 && store.Profile.LastDevice == null
            && store.Profile.RetainedApps.Count == 0, "last removal leaves only the Add Device draft");
        await store.FlushAsync();
        store = new AndroidPhoneStore(store.FilePath); store.ObserveDevices([alias, usb, two]);
        Check(store.Profiles.Count == 1 && store.Profile.LastDevice == null, "last removal remains empty after restart and refresh");

        string prefs = Path.Combine(output, "app-preferences.json");
        var settings = new AndroidAudioSettingsStore(prefs);
        var landscape = new AndroidVirtualDisplaySize(1920, 1080);
        var portrait = new AndroidVirtualDisplaySize(1080, 2400);
        Check(settings.Get(one, app.Package) == new AndroidAppAudioSettings(), "new apps retain system output and phone-native display defaults");
        settings.RememberDisplay(one, app.Package, landscape);
        settings.Remember(one, app.Package, 37, "fixture-output");
        Check(settings.Get(one, app.Package) == new AndroidAppAudioSettings(37, "fixture-output", landscape),
            "volume and output updates preserve the saved virtual display size");
        settings.RememberDisplay(one, app.Package, portrait);
        Check(settings.Get(one, app.Package) == new AndroidAppAudioSettings(37, "fixture-output", portrait),
            "display changes preserve volume and output preferences");
        settings.RememberDisplay(two, app.Package, landscape);
        settings.RememberDisplay(one, "cn.test.other", new(1600, 900));
        await settings.FlushAsync(); settings = new AndroidAudioSettingsStore(prefs);
        Check(settings.Get(alias, app.Package).DisplaySize == portrait && settings.Get(two, app.Package).DisplaySize == landscape
            && settings.Get(one, "cn.test.other").DisplaySize == new AndroidVirtualDisplaySize(1600, 900),
            "display sizes survive restart separately per phone and app, including transport aliases");
        Check(settings.Get(one, app.Package).Volume == 37 && settings.Get(one, app.Package).OutputId == "fixture-output",
            "audio preferences still load alongside display settings");
        var before = File.GetLastWriteTimeUtc(prefs); settings.RememberDisplay(one, app.Package, portrait); await settings.FlushAsync();
        Check(File.GetLastWriteTimeUtc(prefs) == before, "identical framebuffer events do not rewrite configuration");
        bool rejected = false; try { settings.RememberDisplay(one, app.Package, new(0, 900)); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected && settings.Get(one, app.Package).DisplaySize == portrait, "invalid live sizes cannot replace a valid preference");
        string legacy = Path.Combine(output, "legacy.json");
        await File.WriteAllTextAsync(legacy, """{"Version":1,"Apps":{"cn.test.music":{"Volume":42,"OutputId":"legacy","DisplaySize":{"Width":99999,"Height":100}}}}""");
        var migrated = new AndroidAudioSettingsStore(legacy);
        Check(migrated.Get(one, app.Package) == new AndroidAppAudioSettings(42, "legacy"),
            "invalid persisted sizes are ignored without losing legacy audio preferences");
        migrated.RememberDisplay(one, app.Package, landscape); await migrated.FlushAsync();
        Check(migrated.Get(one, app.Package) == new AndroidAppAudioSettings(42, "legacy", landscape)
            && migrated.Get(two, app.Package).DisplaySize == null, "legacy audio migration cannot copy a display size to another phone");
        foreach (string line in new[] { "INFO: Texture: 1080x2400", "INFO: Texture: 1920x1080" })
            Check(VirtualDisplayClient.TryReadDisplaySize(line)?.IsValid() == true, "native framebuffer event accepted: " + line);
        Check(new[] { "[server] INFO: Texture: 1080x2400", "INFO: Texture: 0x0", "INFO: Texture: 8193x1000",
            "INFO: Texture: 1080x2400 garbage", "INFO: Texture: -1080x2400" }.All(s => VirtualDisplayClient.TryReadDisplaySize(s) == null),
            "only valid client framebuffer dimensions drive persistence");
        var adb = new AdbClient(Path.Combine(output, "unused-runtime"));
        var defaults = VirtualDisplayClient.CreateStartInfo(adb, alias, app, 0, 0);
        var restored = VirtualDisplayClient.CreateStartInfo(adb, alias, app, 0, 0, displaySize: landscape);
        Check(defaults.ArgumentList.Contains("--new-display") && restored.ArgumentList.Contains("--new-display=1920x1080")
            && !Directory.Exists(adb.RuntimeDirectory), "native launch restores saved dimensions without eager tool extraction");

        var clients = new List<FakeDisplay>();
        await using var displays = new AndroidVirtualDisplayManager(() => { var item = new FakeDisplay(); clients.Add(item); return item; });
        await displays.OpenAsync(alias, app, 0, 0); await displays.OpenAsync(two, app, 0, 0);
        await displays.CloseDeviceAsync(one);
        Check(displays.Count == 1 && clients[0].Disposed && !clients[1].Disposed,
            "device-scoped virtual display cleanup preserves another phone's window");
        await displays.OpenAsync(alias, app, 0, 0); await displays.OpenAsync(alias, app with { Package = "cn.test.keep" }, 0, 0);
        await displays.CloseApplicationAsync(one, app.Package);
        Check(displays.Count == 2 && clients[2].Disposed && !clients[3].Disposed && !clients[1].Disposed,
            "returning one app closes only its virtual screen and preserves all other apps and phones");
        var batch = new AndroidPhoneStore(Path.Combine(output, "batch-phones.json"));
        batch.RememberDevice(one); batch.Retain(app); batch.SetQuickPanelVisible(app.Package, false);
        int notifications = 0; batch.Changed += () => notifications++;
        var extra = app with { Package = "cn.test.extra" };
        batch.RetainMany([app, extra, extra, app with { Package = "cn.test.third" }]);
        Check(notifications == 1 && batch.Profile.RetainedApps.Count == 3 && batch.Profile.HiddenQuickPanelApps.Contains(app.Package),
            "batch add deduplicates packages, preserves existing switches and notifies once");
        batch.RetainMany([app, extra]); batch.RetainMany([]);
        Check(notifications == 1, "empty and already-added batches do not refresh or save");
        bool invalidBatch = false;
        try { batch.RetainMany([app with { Package = "cn.test.fourth" }, app with { Package = "invalid;package" }]); }
        catch (ArgumentException) { invalidBatch = true; }
        Check(invalidBatch && batch.Profile.RetainedApps.Count == 3 && notifications == 1,
            "batch validates every item before making changes");
        await batch.FlushAsync();
        Check(new AndroidPhoneStore(batch.FilePath).Profile.QuickPanelApps.Count == 2,
            "one persisted batch restores added apps and preserves the hidden existing app");
        settings.RememberWindow(one, app.Package, new(1003, 613));
        settings.Remember(one, app.Package, 37, "fixture-output");
        settings.RememberDisplay(one, app.Package, landscape); await settings.FlushAsync();
        var windowPrefs = new AndroidAudioSettingsStore(prefs);
        Check(windowPrefs.Get(one, app.Package) == new AndroidAppAudioSettings(37, "fixture-output", landscape, new(1003, 613))
            && windowPrefs.Get(two, app.Package).WindowSize == null, "window size survives display/audio writes and restart separately per phone");
        var windowInfo = VirtualDisplayClient.CreateStartInfo(adb, alias, app, 0, 0, displaySize: landscape, windowSize: new(1003, 613));
        Check(windowInfo.ArgumentList.Contains("--window-width=1003") && windowInfo.ArgumentList.Contains("--window-height=613")
            && VirtualDisplayClient.TryReadWindowSize("INFO: SonicRoute window-size: 1003x613") == new AndroidWindowSize(1003, 613)
            && VirtualDisplayClient.TryReadWindowSize("INFO: SonicRoute window-size: 0x0") == null,
            "exact non-aligned window size is restored and minimized events are rejected");

        string spkId = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(one), "speaker");
        string btKey = "bt:8:AA:BB:CC:DD:EE:FF";
        string btId = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(one), btKey);
        Check(AndroidOutputIdentity.IsPhoneOutput(spkId) && !AndroidOutputIdentity.IsPhoneBluetoothOutput(spkId)
            && AndroidOutputIdentity.IsPhoneOutput(btId) && AndroidOutputIdentity.IsPhoneBluetoothOutput(btId)
            && AndroidOutputIdentity.TryParseDeviceId(spkId, out string parsedPhone, out string parsedSink)
            && parsedPhone == AndroidDeviceIdentity.Key(one) && parsedSink == "speaker"
            && AndroidOutputIdentity.TryParseDeviceId(btId, out parsedPhone, out parsedSink)
            && parsedPhone == AndroidDeviceIdentity.Key(one) && parsedSink == btKey,
            "phone speaker and Bluetooth output device IDs round-trip phone and sink keys");

        var mediaWithOutputs = AndroidMediaState.Parse("""{"package":"cn.test.music","title":"T","artist":"A","playing":true,"actions":4,"outputs":[{"key":"bt:AUSDOM_A3_Pro","kind":"bluetooth","id":16,"type":7,"name":"AUSDOM A3 Pro","address":"00:00:00:00:00:00"},{"key":"bt:8:AA:BB","kind":"bluetooth","id":15,"type":8,"name":"AUSDOM A3 Pro","address":"AA:BB"},{"key":"speaker","kind":"speaker","id":2,"type":2,"name":"Built-in Speaker"}]}""");
        Check(mediaWithOutputs.Outputs is { Count: 2 }
            && mediaWithOutputs.Outputs[0].Kind == AndroidOutputSinkKind.Speaker && mediaWithOutputs.Outputs[0].Key == "speaker"
            && mediaWithOutputs.Outputs[1].Kind == AndroidOutputSinkKind.Bluetooth && mediaWithOutputs.Outputs[1].Key == "bt:8:AA:BB"
            && mediaWithOutputs.Outputs[1].AndroidDeviceType == 8 && mediaWithOutputs.Outputs[1].Name == "AUSDOM A3 Pro",
            "media state output list normalizes speaker first and deduplicates SCO + A2DP entries for the same Bluetooth headset");
        var mediaDelta = AndroidMediaState.Parse("""{"package":"cn.test.music","title":"T2","artist":"A","playing":true,"actions":4}""", mediaWithOutputs);
        Check(ReferenceEquals(mediaDelta.Outputs, mediaWithOutputs.Outputs),
            "delta media state without outputs preserves previously observed phone output sinks");

        var outputStore = new AndroidPhoneStore(Path.Combine(output, "output-phones.json"));
        outputStore.RememberDevice(one); outputStore.RememberDevice(two);
        int outputEvents = 0; outputStore.OutputsChanged += () => outputEvents++;
        Check(outputStore.FindProfile(one)!.OutputSinks.Count == 1 && outputStore.FindProfile(one)!.OutputSinks[0].Kind == AndroidOutputSinkKind.Speaker,
            "remembered phone defaults to a single speaker sink before live Bluetooth discovery");
        var btSink = new AndroidOutputSink(btKey, AndroidOutputSinkKind.Bluetooth, 15, 8, "Xiaomi Buds 4 Pro", "AA:BB:CC:DD:EE:FF");
        Check(outputStore.UpdateOutputs(alias, [AndroidOutputSink.DefaultSpeaker, btSink]) && outputEvents == 1
            && outputStore.FindProfile(one)!.OutputSinks.Count == 2 && outputStore.FindProfile(one)!.OutputSinks[1].Name == "Xiaomi Buds 4 Pro"
            && !outputStore.UpdateOutputs(alias, [AndroidOutputSink.DefaultSpeaker, btSink]) && outputEvents == 1,
            "updating phone output sinks matches transport aliases and suppresses duplicate notifications");

        int leaseCount = 0, releasedCount = 0, restoredCount = 0;
        await using var pcToPhone = new PcToPhoneAudioManager(
            () => throw new InvalidOperationException("Offline test must not start ADB"),
            _ => { leaseCount++; return new FakeSilenceLease(() => releasedCount++, () => restoredCount++); })
        { EnableLiveStreaming = false };
        var r1 = pcToPhone.RouteProcess(4242, spkId, alias);
        Check(r1.Success && pcToPhone.GetPersistedEndpoint(4242) == spkId && leaseCount == 1 && releasedCount == 0,
            "routing a PC app to phone speaker records its endpoint and acquires one local silence lease");
        var r2 = pcToPhone.RouteProcess(4242, btId, alias);
        Check(r2.Success && pcToPhone.GetPersistedEndpoint(4242) == btId && leaseCount == 1 && releasedCount == 0,
            "switching a routed PC app between speaker and Bluetooth on the same phone reuses the silence lease");
        string twoSpkId = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(two), "speaker");
        pcToPhone.RouteProcess(5252, twoSpkId, two);
        await pcToPhone.StopPhoneAsync(one);
        Check(pcToPhone.GetPersistedEndpoint(4242) == null && pcToPhone.GetPersistedEndpoint(5252) == twoSpkId
            && releasedCount == 1 && restoredCount == 1,
            "stopping one phone releases and restores only its routed PC apps while another phone continues");
        Check(pcToPhone.StopProcess(5252, restorePreviousEndpoint: false) && pcToPhone.ActiveCount == 0
            && releasedCount == 2 && restoredCount == 1,
            "explicitly rerouting a PC app to a PC endpoint releases its silence lease without overwriting the new endpoint");

        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        { Passed = true, Assertions = _checks, RealDeviceAccess = false,
            Scope = "Device removal, discovery suppression, scoped video cleanup, resolution persistence, phone output sinks and PC-to-phone routing" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} device action assertions");
    }

    private sealed class FakeSilenceLease(Action onRelease, Action onRestore) : IPcProcessSilenceLease
    {
        private int _disposed;
        public void Release(bool restorePreviousEndpoint)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            onRelease();
            if (restorePreviousEndpoint) onRestore();
        }
        public void Dispose() => Release(restorePreviousEndpoint: true);
    }

    private static async Task PcToPhoneLiveAsync(string[] args)
    {
        string runtime = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var adb = new AdbClient(runtime);
        var devices = await adb.DevicesAsync(cts.Token);
        var device = devices.FirstOrDefault(d => d.State == "device")
            ?? throw new IOException("No authorized Android phone connected for live PC-to-Phone verification.");

        // 1. Live AudioOutputBridge list via AdbClient.OutputsAsync
        var outputs = await adb.OutputsAsync(device, cts.Token);
        Check(outputs.Count >= 1 && outputs[0].Kind == AndroidOutputSinkKind.Speaker
            && outputs[0].Key == "speaker" && outputs[0].AndroidDeviceType == 2,
            "live phone AudioOutputBridge list reports the built-in speaker sink first");
        string leftoverOutputs = (await adb.RunAsync(cts.Token, "-s", device.Serial, "shell", "ls /data/local/tmp")).Trim();
        Check(!leftoverOutputs.Contains("sonicroute_outputs_"),
            "live OutputsAsync removes its temporary helper JARs from /data/local/tmp");

        // 2. Live MediaSessionBridge outputs field verification
        var firstMedia = new TaskCompletionSource<AndroidMediaState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mediaClient = new AndroidMediaSessionClient(adb);
        using var mediaStop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var mediaWatch = mediaClient.WatchAsync(device, state => firstMedia.TrySetResult(state), mediaStop.Token);
        AndroidMediaState liveMediaState;
        try
        {
            var winner = await Task.WhenAny(firstMedia.Task, mediaWatch);
            if (winner == mediaWatch) await mediaWatch;
            liveMediaState = await firstMedia.Task.WaitAsync(cts.Token);
            Check(liveMediaState.Outputs is { Count: >= 1 } && liveMediaState.Outputs[0].Kind == AndroidOutputSinkKind.Speaker,
                "live MediaSessionBridge snapshot includes the phone's playable output sinks");
        }
        finally
        {
            mediaStop.Cancel();
            try { await mediaWatch; } catch (OperationCanceledException) { }
        }

        // 3. Live end-to-end PC process loopback -> ADB TCP -> Android AudioOutputBridge play (AudioTrack.setPreferredDevice)
        int leaseAcquired = 0, leaseReleased = 0, leaseRestored = 0;
        var observedOutputsTcs = new TaskCompletionSource<IReadOnlyList<AndroidOutputSink>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var routedTcs = new TaskCompletionSource<(string Key, int Type, string Name)>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pcToPhone = new PcToPhoneAudioManager(
            adb,
            _ => { leaseAcquired++; return new FakeSilenceLease(() => leaseReleased++, () => leaseRestored++); });
        pcToPhone.OutputsObserved += (_, sinks) => observedOutputsTcs.TrySetResult(sinks);
        pcToPhone.RoutedDeviceObserved += (pid, key, type, name) =>
        {
            if (pid == Environment.ProcessId && !string.IsNullOrEmpty(key))
                routedTcs.TrySetResult((key, type, name));
        };

        // Start a synthetic low-volume WASAPI tone in this process so VAD\Process_Loopback captures non-silent frames
        var toneSource = new AndroidPcmSource { Volume = 0.02f };
        using var toneCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var feedTask = Task.Run(async () =>
        {
            float[] samples = new float[960 * 2];
            for (int i = 0; i < 960; i++)
            {
                float v = MathF.Sin(2f * MathF.PI * 440f * i / 48000f) * 0.25f;
                samples[i * 2] = v;
                samples[i * 2 + 1] = v;
            }
            while (!toneCts.IsCancellationRequested)
            {
                if (toneSource.BufferedSamples < 9600)
                    toneSource.Write(samples);
                await Task.Delay(15, toneCts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        });

        using var toneWaveOut = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 40);
        toneWaveOut.Init(toneSource);
        toneWaveOut.Play();

        string phoneKey = AndroidDeviceIdentity.Key(device);
        string targetSpkId = AndroidOutputIdentity.FormatDeviceId(phoneKey, "speaker");
        (long BytesStreamed, long NonSilentBytesStreamed, string? RoutedKey, int RoutedType, string? RoutedName)? diag = null;
        (string Key, int Type, string Name) routedInfo = default;
        try
        {
            var routeResult = pcToPhone.RouteProcess(Environment.ProcessId, targetSpkId, device);
            Check(routeResult.Success && leaseAcquired == 1,
                "live RouteProcess starts streaming for the target process and acquires its silence lease");

            var observedSinks = await observedOutputsTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            routedInfo = await routedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            Check(observedSinks.Count >= 1 && routedInfo.Key == "speaker" && routedInfo.Type == 2,
                "live AudioOutputBridge play reports speaker routing via AudioTrack.setPreferredDevice/getRoutedDevice");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                diag = pcToPhone.GetDiagnostics(Environment.ProcessId);
                if (diag is { BytesStreamed: >= 48000 * 4, NonSilentBytesStreamed: > 0 })
                    break;
                await Task.Delay(100, cts.Token);
            }
            Check(diag is { BytesStreamed: >= 48000 * 4, NonSilentBytesStreamed: > 0 },
                "live Windows 11 Process Loopback captures and streams non-silent 48kHz stereo PCM to the phone AudioTrack");
        }
        finally
        {
            toneCts.Cancel();
            try { toneWaveOut.Stop(); } catch { }
            await feedTask;
            await pcToPhone.StopProcessAsync(Environment.ProcessId, restorePreviousEndpoint: true);
        }

        string psOut = await adb.RunAsync(cts.Token, "-s", device.Serial, "shell", "ps", "-A", "-o", "ARGS");
        string tmpFiles = await adb.RunAsync(cts.Token, "-s", device.Serial, "shell", "ls", "/data/local/tmp");
        string forwards = await adb.RunAsync(cts.Token, "-s", device.Serial, "forward", "--list");
        Check(!psOut.Contains("org.sonicroute.android.AudioOutputBridge")
            && !tmpFiles.Contains("sonicroute_pc_out_")
            && !forwards.Contains("sonicroute_pc_out_")
            && leaseReleased == 1 && leaseRestored == 1,
            "stopping live PC-to-Phone playback terminates the remote AudioOutputBridge helper, removes ADB forwards/temp files and restores the silence lease");

        string resultPath = Path.Combine(output, "result.json");
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new
        {
            Passed = true,
            Assertions = _checks,
            RealDeviceAccess = true,
            Device = device,
            DetectedOutputs = outputs,
            RoutedDevice = new { routedInfo.Key, routedInfo.Type, routedInfo.Name },
            StreamedBytes = diag?.BytesStreamed ?? 0,
            NonSilentStreamedBytes = diag?.NonSilentBytesStreamed ?? 0,
            RemoteProcessCleaned = !psOut.Contains("org.sonicroute.android.AudioOutputBridge"),
            TempFilesCleaned = !tmpFiles.Contains("sonicroute_pc_out_"),
            ForwardsCleaned = !forwards.Contains("sonicroute_pc_out_"),
            Scope = "Live Xiaomi 13 AudioOutputBridge list/play, MediaSessionBridge outputs, Windows 11 Process Loopback non-silent PCM streaming and clean teardown"
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} live PC-to-Phone assertions; evidence: {resultPath}");
    }
}

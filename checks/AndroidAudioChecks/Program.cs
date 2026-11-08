using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Concentus;
using Concentus.Enums;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static int _checks;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); _checks++; Console.WriteLine("PASS " + name); }
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--stream-settings") { await StreamSettingsAsync(args[1]); return 0; }
            if (args.Length > 0 && args[0] == "--encoder-catalog") { await EncoderCatalogAsync(args[1], args[2]); return 0; }
            if (args.Length > 0 && args[0] == "--device-audio") { await DeviceAudioAsync(args[1]); return 0; }
            if (args.Length > 0 && args[0] == "--media-log-tail") { await MediaLogTailAsync(args[1]); return 0; }
            if (args.Length > 0 && args[0] == "--output-latency-live") { await OutputLatencyLiveAsync(args[1]); return 0; }
            if (args.Length > 0 && args[0] == "--output-startup") { await OutputStartupAsync(args[1]); return 0; }
            if (args.Length > 0 && args[0] == "--explicit-output") { await ExplicitOutputAsync(args[1]); return 0; }
            if (args.Length > 0 && args[0] == "--explicit-output-live") { await ExplicitOutputLiveAsync(args[1], args[2]); return 0; }
            if (args.Length > 0 && args[0] == "--output-switch-latency-live") { await ExplicitOutputLiveAsync(args[1], args[2], allowSilence: true); return 0; }
            if (args.Length > 0 && args[0] == "--device-actions") { await DeviceActionsAsync(args[1]); return 0; }
            if (args.Length > 0 && args[0] == "--pc-to-phone-live") { await PcToPhoneLiveAsync(args); return 0; }
            if (args.Length > 0 && args[0] == "--phone-demand") { await PhoneDemandAsync(args[1]); return 0; }
            if (args.Length > 0 && args[0] == "--current-integration") { await CurrentIntegrationAsync(args[1]); return 0; }
            if (args.Length > 0 && args[0] == "--package") { await PackageAsync(args); return 0; }
            if (args.Length > 0 && args[0] == "--media") { await MediaDeviceAsync(args); return 0; }
            if (args.Length > 0 && args[0] == "--catalog-retained") { await RetainedCatalogAsync(args); return 0; }
            if (args.Length > 0 && args[0] == "--connection-state") { await ConnectionStateAsync(args); return 0; }
            if (args.Length > 0 && args[0] == "--phone-library") { await PhoneLibraryAsync(args); return 0; }
            if (args.Length > 0 && args[0] is "--catalog" or "--live") { await DeviceAsync(args); return 0; }
            await ModulesAsync(); Console.WriteLine($"PASS {_checks} focused assertions"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task PackageAsync(string[] args)
    {
        var adb = new AdbClient(Path.Combine(args[1], "AndroidRuntime"));
        new VirtualDisplayRuntime(adb).Validate();
        Check(true, "packaged audio server and isolated video server/client dependencies match the compiled runtime manifest");
        var info = VirtualDisplayClient.CreateStartInfo(adb, new AndroidDevice("offline-package-check", "Offline", "device", false),
            new AndroidApp("cn.test.app", "Offline", 123, "other"), 0, 0);
        info.ArgumentList.Add("--help"); // parse the actual launch options and load DLLs, without contacting a phone
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        string help = await stdout + await stderr;
        Check(process.ExitCode == 0 && help.Contains("--new-display") && help.Contains("--start-app"),
            "packaged native client loads its video DLLs and accepts the video-only launch options without starting ADB");
        await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(new { Package = Path.GetFullPath(args[1]), Success = true,
            ManifestFiles = 8, NativeExitCode = process.ExitCode, DeviceAccess = false, Assertions = _checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} package assertions; no phone access or capture.");
    }
    private static async Task ModulesAsync()
    {
        var devices = AdbClient.ParseDevices("List of devices attached\nadb-a._adb-tls-connect._tcp device model:Test_Phone\nusb unauthorized\n");
        await CheckPhoneManagementAsync();
        Check(devices.Count == 2 && devices[0].IsWifi && !devices[1].IsWifi && devices[1].State == "unauthorized", "ADB Wi-Fi and unauthorized states");
        bool rejected = false; try { AdbClient.ValidatePackage("cn.test.app;id"); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "reject shell metacharacters in package");
        var pcm = new AndroidPcmSource();
        pcm.Write(new float[] { 1, -1, 0.5f, -0.5f }); pcm.Volume = 0.25f;
        byte[] read = new byte[16]; pcm.Read(read, 0, read.Length);
        Check(MemoryMarshal.Cast<byte, float>(read).SequenceEqual(new float[] { 0.25f, -0.25f, 0.125f, -0.125f }), "PCM applies only source volume");
        pcm.Write(new float[] { 1, 1 }); pcm.Volume = 0; pcm.Read(read, 0, 16);
        Check(read.All(b => b == 0), "source mute and underrun produce zero PCM");
        pcm.Volume = 1; pcm.Write(new float[24000]); pcm.Write(new float[24000]);
        Check(pcm.BufferedSamples <= 24000 && pcm.DroppedSamples > 0, "bounded queue trims stale latency");
        pcm.Clear(); Check(pcm.BufferedSamples == 0, "clear releases queued audio");
        CheckPcmWrapAndGain();
        byte[] wire = BuildWire();
        var receiver = new ScrcpyOpusReceiver(); var decoded = new AndroidPcmSource(); int firstFrames = 0;
        try { await receiver.ReceiveAsync(new FragmentedStream(wire), decoded, () => firstFrames++, default); } catch (EndOfStreamException) { }
        Check(receiver.PacketsDecoded == 5 && firstFrames == 1, "fragmented scrcpy stream decodes five real Opus frames");
        Check(decoded.NonSilentSamplesWritten > 0 && decoded.SamplesWritten == (960 * 5 - 312) * 2, "Opus pre-skip and audible PCM");
        Check(receiver.LastTimestampUs == 80000, "microsecond timestamps preserved");
        var largeConfig = new byte[wire.Length + 8192];
        wire.AsSpan(0, 35).CopyTo(largeConfig);
        BinaryPrimitives.WriteUInt32BigEndian(largeConfig.AsSpan(12), 19 + 8192);
        wire.AsSpan(35).CopyTo(largeConfig.AsSpan(35 + 8192));
        var grown = new ScrcpyOpusReceiver();
        try { await grown.ReceiveAsync(new FragmentedStream(largeConfig), new(), () => { }, default); } catch (EndOfStreamException) { }
        Check(grown.PacketsDecoded == 5, "pooled packet storage grows for a valid large configuration and preserves decoding");
        var rebased = new ScrcpyOpusReceiver();
        try { await rebased.ReceiveAsync(new MemoryStream(BuildWire(true)), new(), () => { }, default); } catch (EndOfStreamException) { }
        Check(rebased.PacketsDecoded == 5 && rebased.TimestampDiscontinuities == 1, "Android clock rebase clears stale PCM without terminating capture");
        byte[] badSize = wire[..35].ToArray(); // codec + config header + 19-byte OpusHead
        using (var malformed = new MemoryStream())
        {
            malformed.Write(badSize); WritePacket(malformed, 100000, new byte[262145]);
            malformed.Position = 0; bool failed = false;
            try { await new ScrcpyOpusReceiver().ReceiveAsync(malformed, new(), () => { }, default); } catch (InvalidDataException) { failed = true; }
            Check(failed, "reject oversized packet before decode");
        }
        bool disabled = false;
        try { await new ScrcpyOpusReceiver().ReceiveAsync(new MemoryStream(new byte[4]), new(), () => { }, default); } catch (InvalidDataException) { disabled = true; }
        Check(disabled, "explicit server-disabled stream is a failure");
        int transports = 0, disposed = 0, players = 0;
        var device = new AndroidDevice("fake", "Test", "device", true); var app = new AndroidApp("cn.test.app", "Test", 123, "media");
        await using var session = new AndroidAudioSession(() => { transports++; return new FakeTransport(wire, () => disposed++); }, (_, _) => { players++; return new FakePlayer(); });
        await session.StartAsync(device, app);
        Check(session.State.State == CaptureState.Receiving && session.State.SourceId == "android:fake:cn.test.app:123", "external source identity is independent of PID");
        await session.StartAsync(device, app); Check(transports == 1, "repeated drop does not duplicate capture");
        session.SetVolume(24); Check(Math.Abs(session.Pcm!.Volume - 0.24f) < 0.001, "session gain updates PCM adapter");
        session.SetMute(true); Check(session.Pcm!.Volume == 0 && session.State.Muted, "mute preserves volume preference");
        session.SetMute(false); Check(Math.Abs(session.Pcm!.Volume - 0.24f) < 0.001, "unmute restores volume");
        await session.SetOutputAsync("test-endpoint"); Check(players == 2 && session.State.OutputId == "test-endpoint", "routing replaces only the external source renderer");
        await session.StopAsync(); Check(disposed == 1 && session.State.State == CaptureState.Idle && session.Pcm == null, "stop cancels stream and releases owned resources");
        await session.StartAsync(device, app); await session.StopAsync(); Check(transports == 2 && disposed == 2, "start-stop can be repeated without leaked transport");
        await session.StartAsync(device, app);
        await session.StartAsync(device with { Name = "Renamed phone" }, app);
        Check(transports == 3, "device identity uses serial rather than changing display metadata");
        await session.StartAsync(device, app with { Uid = 456 });
        Check(transports == 4 && disposed == 3 && session.State.SourceId == "android:fake:cn.test.app:456", "changed UID never reuses the previous capture");
        await session.StopAsync();
        await using var cleanup = new AndroidAudioSession(() => new FailingCleanupTransport(wire), (_, _) => new FakePlayer());
        await cleanup.StartAsync(device, app); await cleanup.StopAsync();
        Check(cleanup.State.State == CaptureState.Idle && cleanup.Pcm == null && cleanup.GetLog().Contains("Transport cleanup"), "transport cleanup failure still releases the PCM and session lifetime");
        int multiStarts = 0, multiStops = 0;
        var multiModes = new List<bool>();
        await using var multi = new AndroidAudioManager(() => new AndroidAudioSession(
            () => { multiStarts++; return new FakeTransport(wire, () => multiStops++, mode => multiModes.Add(mode)); }, (_, _) => new FakePlayer()));
        var first = await multi.AddAsync(device, app);
        var second = await multi.AddAsync(device, app with { Package = "cn.test.second", Uid = 124 });
        Check(ReferenceEquals(multi.FindSource(first.Id), first) && ReferenceEquals(multi.FindSource(second.Id), second)
            && multi.FindSource("missing-source") == null, "source identity lookup preserves independent targets and unknown identities");
        var stableSources = multi.Sources;
        first.Session.SetVolume(60);
        Check(ReferenceEquals(stableSources, multi.Sources) && stableSources[0].Session.State.Volume == 60,
            "source state updates reuse the registry snapshot and expose current state");
        int duplicateEvents = 0;
        void CountChange(AndroidAudioState _) => duplicateEvents++;
        first.Session.Changed += CountChange;
        first.Session.SetVolume(60); first.Session.SetMute(false);
        Check(duplicateEvents == 0, "unchanged gain controls do not notify UI or persistence subscribers");
        first.Session.SetVolume(100);
        first.Session.Changed -= CountChange;
        Check(multi.Sources.Count == 2 && multiStarts == 2 && multiStops == 0 && first.Session.State.State == CaptureState.Receiving,
            "adding a second application preserves the first independent capture");
        Check(!ReferenceEquals(first.Session.Pcm, second.Session.Pcm), "each application owns a distinct PCM queue");
        var duplicate = await multi.AddAsync(device, app, true, "another-output");
        Check(ReferenceEquals(first, duplicate) && multiStarts == 2 && first.Session.State.OutputId == null,
            "repeated multi-source drop preserves the existing stream and its route");
        first.Session.SetVolume(17); first.Session.SetMute(true);
        await first.Session.SetOutputAsync("first-output");
        Check(second.Session.Pcm!.Volume == 1 && !second.Session.State.Muted && second.Session.State.OutputId == null,
            "one application's volume mute and route do not change another");
        var switched = await multi.AddAsync(device, app, false, "ignored-new-output");
        Check(ReferenceEquals(multi.FindSource(first.Id), switched) && ReferenceEquals(multi.FindSource(second.Id), second),
            "replacing an audio mode publishes the new source to identity lookup without disturbing another app");
        Check(!ReferenceEquals(first, switched) && !switched.KeepPhoneAudio && multiModes.SequenceEqual(new[] { true, true, false })
            && multiStarts == 3 && multiStops == 1 && ReferenceEquals(multi.Sources[0], switched),
            "changing the drop zone reconnects only that app with the requested phone audio mode and stable position");
        Check(switched.Session.State.Volume == 17 && switched.Session.State.Muted && switched.Session.State.OutputId == "first-output"
            && second.Session.State.State == CaptureState.Receiving && ReferenceEquals(multi.Sources[1], second),
            "mode changes preserve the app's volume mute and output while another source continues");
        Check(ReferenceEquals(switched, await multi.AddAsync(device, app, false)) && multiStarts == 3,
            "repeating the same drop mode shares the existing connection");
        await multi.RemoveAsync(first.Id);
        Check(multi.FindSource(first.Id) == null && ReferenceEquals(multi.FindSource(second.Id), second),
            "removed identities disappear before their owned transport is released");
        Check(stableSources.Count == 2 && multi.Sources.Count == 1 && !ReferenceEquals(stableSources, multi.Sources),
            "source membership changes publish a new snapshot without mutating a previous reader's view");
        Check(multiStops == 2 && multi.Sources.Count == 1 && second.Session.State.State == CaptureState.Receiving,
            "exit releases only its selected source");
        var uidReplacement = await multi.AddAsync(device, second.App with { Uid = 456 });
        Check(multi.FindSource(second.Id) == null && ReferenceEquals(multi.FindSource(uidReplacement.Id), uidReplacement),
            "UID changes retire the old identity and publish the replacement");
        Check(multiStarts == 4 && multiStops == 3 && multi.Sources.Single().App.Uid == 456,
            "UID replacement affects only the matching package");
        await multi.StopAllAsync();
        Check(multi.FindSource(uidReplacement.Id) == null && multi.FindSource(first.Id) == null,
            "stop all clears the identity index together with source membership");
        Check(multi.Sources.Count == 0 && multiStops == 4, "stop all releases every independent source");
        await multi.StopAllAsync(); Check(multiStops == 4, "repeated stop all is idempotent");
        await using var pending = new AndroidAudioManager(() => new AndroidAudioSession(() => new PendingTransport(), (_, _) => new FakePlayer()));
        var pendingStart = pending.AddAsync(device, app);
        await pending.RemoveAsync(pending.Sources.Single().Id);
        bool cancelled = false; try { await pendingStart; } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && pending.Sources.Count == 0, "exit during startup cancels and joins only that source");
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = new AndroidAudioManager(() => new AndroidAudioSession(() => new DelayedCleanupTransport(wire, releaseGate.Task), (_, _) => new FakePlayer()));
        var closingSource = await closing.AddAsync(device, app);
        var individualExit = closing.RemoveAsync(closingSource.Id);
        var applicationExit = closing.DisposeAsync().AsTask();
        Check(!applicationExit.IsCompleted, "application exit joins a source already removed from the visible registry");
        releaseGate.TrySetResult(); await Task.WhenAll(individualExit, applicationExit);
        Check(applicationExit.IsCompletedSuccessfully, "retiring sources finish cleanup before manager shutdown");
        int attempts = 0;
        await using var isolated = new AndroidAudioManager(() => new AndroidAudioSession(
            () => ++attempts == 1 ? new FakeTransport(wire, () => { }) : new RejectedTransport(), (_, _) => new FakePlayer()));
        var healthy = await isolated.AddAsync(device, app);
        bool newAppFailed = false; try { await isolated.AddAsync(device, app with { Package = "cn.test.rejected", Uid = 200 }); } catch (IOException) { newAppFailed = true; }
        Check(newAppFailed && healthy.Session.State.State == CaptureState.Receiving && isolated.Sources.Count == 2,
            "a failed new application leaves the existing capture running and exposes its own failed row");
        await VirtualDisplaysAsync(device, app);
        await PhoneSettingsAsync(device, app, wire);
        var playing = AndroidMediaState.Parse("{\"package\":\"cn.test.app\",\"title\":\"Track\",\"artist\":\"Artist\",\"playing\":true,\"actions\":50}");
        Check(playing.Playing && playing.CanToggle && playing.CanNext && playing.CanPrevious, "phone media actions expose the playing session controls");
        var concurrent = AndroidMediaState.Parse("{\"package\":\"cn.test.app\",\"title\":\"Track\",\"artist\":\"\",\"playing\":true,\"actions\":50,\"playingPackages\":[\"cn.test.app\",\"cn.test.second\",\"cn.test.app\"]}");
        Check(concurrent.PlayingPackages.SequenceEqual(new[] { "cn.test.app", "cn.test.second" }), "media protocol preserves distinct concurrently playing applications");
        Check(new AndroidConnectionQuality(20).SignalLevel == 4 && new AndroidConnectionQuality(60).SignalLevel == 4
            && new AndroidConnectionQuality(61).SignalLevel == 3 && new AndroidConnectionQuality(150).SignalLevel == 3
            && new AndroidConnectionQuality(151).SignalLevel == 2 && new AndroidConnectionQuality(300).SignalLevel == 2
            && new AndroidConnectionQuality(301).SignalLevel == 1 && new AndroidConnectionQuality(null).SignalLevel == 0,
            "connection signal decreases monotonically with measured RTT and clears on timeout");
        var paused = AndroidMediaState.Parse("{\"package\":\"cn.test.app\",\"title\":\"Track\",\"artist\":\"\",\"playing\":false,\"actions\":4}");
        Check(paused.CanToggle && !paused.CanNext && !paused.CanPrevious, "paused media enables only controls advertised by the phone");
        var timeline = AndroidMediaState.Parse("{\"package\":\"cn.test.app\",\"title\":\"Track\",\"artist\":\"Artist\",\"playing\":false,\"actions\":260,\"artwork\":\"AQID\",\"position\":211000,\"duration\":238000,\"speed\":1}");
        Check(timeline.CanSeek && timeline.CurrentPositionMs == 211000 && timeline.DurationMs == 238000
            && timeline.ArtworkPng!.SequenceEqual(new byte[] { 1, 2, 3 }), "media snapshot preserves artwork and paused playback timeline for seeking");
        string deltaLine = "{\"package\":\"cn.test.app\",\"title\":\"Track\",\"artist\":\"Artist\",\"playing\":true,\"actions\":260,\"position\":212000,\"duration\":238000}";
        var delta = AndroidMediaState.Parse(deltaLine, timeline);
        Check(ReferenceEquals(delta.ArtworkPng, timeline.ArtworkPng) && delta.PositionMs == 212000 && delta.Playing,
            "metadata-only events reuse the exact thumbnail while updating playback state");
        Check(AndroidMediaState.Parse(deltaLine.Replace("cn.test.app", "cn.test.other"), timeline).ArtworkPng == null
            && AndroidMediaState.Parse(deltaLine.Replace("\"position\"", "\"artwork\":\"\",\"position\""), timeline).ArtworkPng == null,
            "changing apps or explicitly clearing artwork cannot retain another track's thumbnail");
        Check(!(timeline with { DurationMs = 0 }).CanSeek, "unknown-duration media cannot offer a fabricated seek range");
        bool mediaError = false; try { AndroidMediaState.Parse("{\"error\":\"permission denied\"}"); } catch (IOException) { mediaError = true; }
        Check(mediaError, "media permission failures surface without fabricated track data");
        string mediaId = new string('a', 32);
        Check(AndroidMediaSessionClient.IsOwnedCommand("app_process / org.sonicroute.android.MediaSessionBridge " + mediaId, mediaId)
            && !AndroidMediaSessionClient.IsOwnedCommand("app_process / other.Bridge " + mediaId, mediaId)
            && !AndroidMediaSessionClient.IsOwnedCommand("org.sonicroute.android.MediaSessionBridge " + mediaId + "1", mediaId),
            "media cleanup matches only its exact helper and session id");
    }
    private static void CheckPcmWrapAndGain()
    {
        var pcm = new AndroidPcmSource();
        var expected = new Queue<float>();
        var random = new Random(311);
        long written = 0, consumed = 0, underflow = 0, nonSilent = 0;
        bool correct = true;
        for (int block = 0; block < 300; block++)
        {
            pcm.Volume = block % 3 == 0 ? 0 : block % 3 == 1 ? 0.35f : 1;
            pcm.HeaderGain = 2.3f;
            if (expected.Count < 4096)
            {
                float[] samples = new float[random.Next(32, 1200) * 2];
                for (int i = 0; i < samples.Length; i++)
                {
                    float value = i % 97 == 0 ? float.NaN : i % 101 == 0 ? float.PositiveInfinity
                        : i % 103 == 0 ? float.NegativeInfinity : (float)(random.NextDouble() * 4 - 2);
                    samples[i] = value; expected.Enqueue(value);
                    if (Math.Abs(value) > 0.0001f) nonSilent++;
                }
                written += samples.Length; pcm.Write(samples);
            }
            int requested = random.Next(32, 1600) * 2;
            byte[] read = Enumerable.Repeat((byte)0xcd, requested * sizeof(float) + 16).ToArray();
            int take = Math.Min(requested, expected.Count);
            pcm.Read(read, 8, requested * sizeof(float)); consumed += take; underflow += requested - take;
            var actual = MemoryMarshal.Cast<byte, float>(read.AsSpan(8, requested * sizeof(float)));
            for (int i = 0; i < requested; i++)
            {
                float value = i < take ? Math.Clamp(expected.Dequeue() * (pcm.Volume * pcm.HeaderGain), -1, 1) : 0;
                correct &= actual[i].Equals(value);
            }
            correct &= read.AsSpan(0, 8).ToArray().All(b => b == 0xcd) && read.AsSpan(read.Length - 8).ToArray().All(b => b == 0xcd);
        }
        Check(correct && consumed > AndroidPcmSource.SampleRate * AndroidPcmSource.Channels,
            "PCM wraparound gain clipping mute NaN infinities offsets and underruns match scalar sample results");
        Check(pcm.SamplesWritten == written && pcm.SamplesConsumed == consumed && pcm.UnderflowSamples == underflow
            && pcm.NonSilentSamplesWritten == nonSilent && pcm.DroppedSamples == 0,
            "optimized PCM keeps sample diagnostics exact across multiple ring wraps");
    }
    private static async Task PhoneSettingsAsync(AndroidDevice device, AndroidApp app, byte[] wire)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "settings-fixture-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "android-audio.json");
        var settings = new AndroidAudioSettingsStore(path);
        Check(settings.Get(app.Package) == new AndroidAppAudioSettings() && !File.Exists(path), "new phone settings start at full volume on system default without writing during load");
        Func<AndroidAudioSession> factory = () => new AndroidAudioSession(() => new FakeTransport(wire, () => { }), (_, _) => new FakePlayer());
        await using (var manager = new AndroidAudioManager(factory, settings))
        {
            var first = await manager.AddAsync(device, app, false);
            Check(first.Session.State.OutputId == null && first.Session.State.Volume == 100 && !first.KeepPhoneAudio,
                "new desktop-only phone source starts on system default independently of master output");
            first.Session.SetVolume(37); await first.Session.SetOutputAsync("saved-output");
            var second = await manager.AddAsync(device, app with { Package = "cn.test.settingssecond", Uid = 124 });
            Check(second.Session.State.Volume == 100 && second.Session.State.OutputId == null, "phone app preferences do not leak into another application");
        }
        await settings.FlushAsync();
        var restored = new AndroidAudioSettingsStore(path);
        Check(restored.Get(device, app.Package) == new AndroidAppAudioSettings(37, "saved-output"), "separate phone settings file survives a new store instance");
        await using (var manager = new AndroidAudioManager(factory, restored))
        {
            var first = await manager.AddAsync(device, app);
            Check(first.Session.State.Volume == 37 && first.Session.State.OutputId == "saved-output" && Math.Abs(first.Session.Pcm!.Volume - 0.37f) < 0.001,
                "newly created phone stream restores saved route and gain before reception");
            await first.Session.SetOutputAsync(null); first.Session.SetVolume(0);
            await manager.RemoveAsync(first.Id);
            var again = await manager.AddAsync(device, app);
            Check(again.Session.State.OutputId == null && again.Session.State.Volume == 0 && again.Session.Pcm!.Volume == 0,
                "explicit system-default selection and zero volume survive remove and re-add");
        }
        await restored.FlushAsync();
        Check(new AndroidAudioSettingsStore(path).Get(device, app.Package) == new AndroidAppAudioSettings(0, null), "system-default preference stays null after writing and reloading");
        string snapshot = File.ReadAllText(path);
        await restored.FlushAsync();
        Check(snapshot == File.ReadAllText(path) && !Directory.GetFiles(root, "*.tmp").Any(), "completed preference saves leave no temporary files or idle writes");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var orderedSettings = new AndroidAudioSettingsStore(Path.Combine(root, "concurrent.json"));
        await using var concurrent = new AndroidAudioManager(() =>
        {
            var session = new AndroidAudioSession(() => new FakeTransport(wire, () => { }), (_, _) => new FakePlayer());
            session.Changed += state =>
            {
                if (state.State == CaptureState.Receiving && state.Volume == 45)
                { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Fixture callback was not released."); }
            };
            return session;
        }, orderedSettings);
        var orderedSource = await concurrent.AddAsync(device, app);
        var older = Task.Run(() => orderedSource.Session.SetVolume(45));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            orderedSource.Session.SetVolume(67);
        }
        finally { release.Set(); }
        await older; await orderedSettings.FlushAsync();
        Check(orderedSource.Session.State.Volume == 67 && Math.Abs(orderedSource.Session.Pcm!.Volume - 0.67f) < 0.001f
            && new AndroidAudioSettingsStore(orderedSettings.FilePath).Get(device, app.Package).Volume == 67,
            "out-of-order source callbacks preserve current PCM gain and saved volume");
    }

    private static async Task MediaDeviceAsync(string[] args)
    {
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Error)); Trace.AutoFlush = true;
        var adb = new AdbClient(args[1]);
        var device = (await adb.DevicesAsync(default)).FirstOrDefault(d => d.State == "device")
            ?? throw new IOException("No authorized phone; media device check not run.");
        var first = new TaskCompletionSource<AndroidMediaState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new AndroidMediaSessionClient(adb);
        var latency = new TaskCompletionSource<AndroidConnectionQuality>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionQualityChanged += quality => latency.TrySetResult(quality);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var watch = client.WatchAsync(device, state => first.TrySetResult(state), stop.Token);
        try
        {
            Task winner = await Task.WhenAny(first.Task, watch);
            if (winner == watch) await watch;
            var state = await first.Task.WaitAsync(stop.Token);
            var quality = device.IsWifi ? await latency.Task.WaitAsync(stop.Token) : null;
            if (quality != null)
            {
                Check(quality.RoundTripMs >= 0 && quality.SignalLevel is >= 1 and <= 4,
                    "existing phone connection responds to the matched latency ping without playback commands");
                Console.WriteLine($"CONNECTION RTT={quality.RoundTripMs:0.00}ms strength={quality.SignalLevel}/4");
            }
            Console.WriteLine($"MEDIA package={state.Package} title={state.Title} playing={state.Playing} actions={state.Actions} artwork={state.ArtworkPng?.Length ?? 0} position={state.PositionMs}/{state.DurationMs}");
            stop.Cancel();
            try { await watch; } catch (OperationCanceledException) { }
            string processes = await adb.RunAsync(default, "-s", device.Serial, "shell", "ps", "-A", "-o", "ARGS");
            Check(!processes.Contains("org.sonicroute.android.MediaSessionBridge"), "visible-panel metadata subscription ends and releases its remote helper");
            await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(new { Device = device, State = state, ConnectionQuality = quality, ControlsSent = 0,
                Scope = "Read metadata and callback connection; no playback or media key injected", Checks = _checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { stop.Cancel(); try { await watch; } catch (OperationCanceledException) { } }
    }
    private static async Task VirtualDisplaysAsync(AndroidDevice device, AndroidApp app)
    {
        var clients = new List<FakeDisplay>();
        var displays = new AndroidVirtualDisplayManager(() => { var client = new FakeDisplay { ProcessId = clients.Count + 10000 }; clients.Add(client); return client; });
        var first = await displays.OpenAsync(device, app, -200, 50);
        Check(ReferenceEquals(first, await displays.OpenAsync(device, app, 900, 900)) && clients.Count == 1 && clients[0].X == -200,
            "repeated desktop drops reuse one independent window per app instead of spawning duplicates");
        await displays.OpenAsync(device, app with { Package = "cn.test.second", Uid = 124 }, 20, 30);
        Check(displays.Count == 2 && clients[1].X == 20 && clients[1].Y == 30, "distinct desktop apps keep separate windows at their requested location");
        Check(displays.FindApplication(10000)?.App.Package == app.Package
            && displays.FindApplication(10001)?.App.Package == "cn.test.second" && displays.FindApplication(9999) == null,
            "foreground viewer processes map to the exact phone application without process-name scans");
        clients[0].End.TrySetResult();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!clients[0].Disposed && DateTime.UtcNow < deadline) await Task.Delay(5);
        Check(clients[0].Disposed && !clients[1].Disposed, "closing one virtual window releases only its own video client");
        await displays.DisposeAsync();
        Check(clients[1].Disposed && displays.Count == 0, "application shutdown joins every virtual display client");
        var pendingClient = new FakeDisplay { Pending = true };
        var pending = new AndroidVirtualDisplayManager(() => pendingClient);
        var starting = pending.OpenAsync(device, app, 0, 0);
        await pending.DisposeAsync();
        bool cancelled = false; try { await starting; } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && pendingClient.Disposed, "shutdown during virtual display startup cancels and cleans up its pending client");
        var adb = new AdbClient(Path.Combine(Path.GetTempPath(), "SonicRoute-VirtualDisplay-ArgumentFixture"));
        var info = VirtualDisplayClient.CreateStartInfo(adb, device, app with { Name = "测试 Window \"title\"" }, -400, 50);
        Check(info.ArgumentList.Contains("--no-audio") && info.ArgumentList.Contains("--new-display")
            && !info.ArgumentList.Any(a => a.StartsWith("--max-size=") || a.StartsWith("--window-width="))
            && info.Environment["SONICROUTE_FLEX_DISPLAY"] == "1"
            && !info.ArgumentList.Contains("--no-vd-destroy-content")
            && info.ArgumentList.Contains("--start-app=" + app.Package) && info.ArgumentList.Contains("--no-clipboard-autosync")
            && !info.UseShellExecute && info.Environment["ADB"] == adb.Executable
            && info.Environment["SCRCPY_SERVER_PATH"] == new VirtualDisplayRuntime(adb).ServerFile
            && info.Environment["SCRCPY_SERVER_PATH"] != adb.ServerFile,
            "virtual windows isolate video, default to phone geometry and destroy VD content when closed");
        string cache = Path.Combine(AppContext.BaseDirectory, "icon-fixture-" + Guid.NewGuid().ToString("N"));
        byte[] iconBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a7b0AAAAASUVORK5CYII=");
        var iconApp = app with { IconPng = iconBytes };
        string iconPath = VirtualDisplayClient.CacheIcon(iconApp, cache)!;
        DateTime written = File.GetLastWriteTimeUtc(iconPath);
        Check(File.ReadAllBytes(iconPath).SequenceEqual(iconBytes) && VirtualDisplayClient.CacheIcon(iconApp, cache) == iconPath
            && File.GetLastWriteTimeUtc(iconPath) == written
            && VirtualDisplayClient.CreateStartInfo(adb, device, iconApp, 0, 0, iconPath).Environment["SCRCPY_ICON_PATH"] == iconPath,
            "virtual client receives the exact app PNG through its own cached icon path without repeated writes");
        Check(VirtualDisplayClient.IsOwnedServerCommand("app_process / com.genymobile.scrcpy.Server 3.3.4 scid=1234abcd video=true", "1234abcd")
            && !VirtualDisplayClient.IsOwnedServerCommand("app_process / com.genymobile.scrcpy.Server 3.3.4 scid=1234abce", "1234abcd")
            && !VirtualDisplayClient.IsOwnedServerCommand("other.Process scid=1234abcd", "1234abcd")
            && !VirtualDisplayClient.IsOwnedServerCommand("com.genymobile.scrcpy.Server scid=1234abcd0", "1234abcd"),
            "forced cleanup requires the exact owned server identity and cannot match another stream");
    }
    private sealed class FakeDisplay : IVirtualDisplayClient
    {
        internal readonly TaskCompletionSource End = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Pending, Disposed;
        internal int X, Y;
        public int ProcessId { get; init; }
        public Task Completion => End.Task;
        public async Task StartAsync(AndroidDevice device, AndroidApp app, int x, int y, CancellationToken token)
        { X = x; Y = y; if (Pending) await Task.Delay(Timeout.Infinite, token); }
        public void Activate() { }
        public ValueTask DisposeAsync() { Disposed = true; End.TrySetResult(); return ValueTask.CompletedTask; }
    }
    private static async Task DeviceAsync(string[] args)
    {
        string runtime = args[1], output = args[2];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var adb = new AdbClient(runtime); var devices = await adb.DevicesAsync(default);
        var device = devices.FirstOrDefault(d => d.State == "device") ?? throw new Exception("NOT TESTED: no authorized phone");
        var apps = await adb.AppsAsync(device, default);
        if (args[0] == "--catalog")
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Device = device, Apps = apps }));
            Console.WriteLine($"CATALOG {apps.Count} apps, {apps.Count(a => a.IconPng?.Length > 0)} icons");
            foreach (var app in apps.Where(a => a.Package.Contains("bodian") || a.Package.Contains("cloudmusic") || a.Package.Contains("bili"))) Console.WriteLine($"{app.Name} {app.Package} UID={app.Uid}");
            return;
        }
        string package = args.Length > 3 ? args[3] : "cn.wenyu.bodian";
        int seconds = args.Length > 4 ? int.Parse(args[4]) : 120;
        var selected = apps.Single(a => a.Package == package);
        await using var session = new AndroidAudioSession(adb);
        session.Changed += state => Console.WriteLine($"STATE {state.State}: {state.Error}");
        session.SetVolume(30);
        var before = Process.GetCurrentProcess(); before.Refresh(); long startMemory = before.PrivateMemorySize64;
        var clock = Stopwatch.StartNew();
        AndroidPcmSource? pcm = null;
        Exception? failure = null;
        long packets = 0, clockRebases = 0;
        try
        {
        await session.StartAsync(device, selected, true);
        pcm = session.Pcm!;
        Console.WriteLine($"LIVE receiving {selected.Name}, UID={selected.Uid}, phone audio retained");
        for (int i = 0; i < seconds; i++)
        {
            await Task.Delay(1000);
            if (session.State.State != CaptureState.Receiving)
            { await Task.Delay(500); throw new Exception(session.State.Error ?? "Stream stopped: " + session.State.State); }
            if (i == 3) { session.SetVolume(20); session.SetMute(true); }
            if (i == 4) session.SetMute(false);
            if (i % 20 == 19) Console.WriteLine($"{i + 1}s packets={session.PacketsDecoded} nonSilent={pcm.NonSilentSamplesWritten} buffered={pcm.BufferedSamples}");
        }
        packets = session.PacketsDecoded;
        clockRebases = session.TimestampDiscontinuities;
        }
        catch (Exception ex) { failure = ex; }
        await session.StopAsync(); before.Refresh();
        var result = new { Device = device, Package = selected.Package, Uid = selected.Uid, ElapsedSeconds = clock.Elapsed.TotalSeconds,
            Success = failure == null, Error = failure?.Message, PacketsDecoded = packets, TimestampDiscontinuities = clockRebases, SamplesWritten = pcm?.SamplesWritten, NonSilentSamplesWritten = pcm?.NonSilentSamplesWritten,
            SamplesConsumed = pcm?.SamplesConsumed, DroppedSamples = pcm?.DroppedSamples, UnderflowSamples = pcm?.UnderflowSamples,
            MemoryStart = startMemory, MemoryEnd = before.PrivateMemorySize64, StateAfterStop = session.State.State.ToString(),
            Evidence = "Real phone -> scrcpy protocol -> managed Opus -> in-process PCM -> event-driven WASAPI consumption. Physical audibility and isolation against a second playing app not established by this probe.", Log = session.GetLog() };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(session.GetLog());
        if (failure != null) throw failure;
        Console.WriteLine($"LIVE COMPLETE {packets} packets, {pcm?.NonSilentSamplesWritten} non-silent decoded samples, {pcm?.SamplesConsumed} samples consumed by WASAPI");
    }
    private static byte[] BuildWire(bool rebase = false)
    {
        using var output = new MemoryStream(); output.Write(Encoding.ASCII.GetBytes("opus"));
        byte[] header = new byte[19]; Encoding.ASCII.GetBytes("OpusHead").CopyTo(header, 0); header[8] = 1; header[9] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 312); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 48000);
        WritePacket(output, 1UL << 63, header);
        using IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
        float[] samples = new float[1920]; byte[] encoded = new byte[4000];
        for (int frame = 0; frame < 5; frame++)
        {
            for (int i = 0; i < 960; i++) { float value = (float)(0.2 * Math.Sin((frame * 960 + i) * 440 * 2 * Math.PI / 48000)); samples[i * 2] = samples[i * 2 + 1] = value; }
            int length = encoder.Encode(samples, 960, encoded, encoded.Length); WritePacket(output, (ulong)(rebase && frame == 3 ? 0 : frame * 20000), encoded[..length]);
        }
        return output.ToArray();
    }
    private static void WritePacket(Stream stream, ulong flags, byte[] payload)
    { byte[] header = new byte[12]; BinaryPrimitives.WriteUInt64BigEndian(header, flags); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), payload.Length); stream.Write(header); stream.Write(payload); }
    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken); }
    private sealed class HoldingStream(byte[] data) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { if (Position < Length) return await base.ReadAsync(buffer, cancellationToken); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
    private sealed class FakeTransport(byte[] data, Action disposed, Action<bool>? connected = null) : IAudioTransport
    {
        private Stream? _stream;
        public Task<Stream> ConnectAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, CancellationToken token)
        { connected?.Invoke(keepPhoneAudio); _stream = new HoldingStream(data); return Task.FromResult(_stream); }
        public ValueTask DisposeAsync() { _stream?.Dispose(); disposed(); return ValueTask.CompletedTask; }
    }
    private sealed class FakePlayer : IPcmPlayer { public void Play() { } public void Stop() { } public void Dispose() { } }
    private sealed class PendingTransport : IAudioTransport
    {
        public async Task<Stream> ConnectAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, CancellationToken token)
        { await Task.Delay(Timeout.Infinite, token); return Stream.Null; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class DelayedCleanupTransport(byte[] data, Task release) : IAudioTransport
    {
        private readonly Stream _stream = new HoldingStream(data);
        public Task<Stream> ConnectAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, CancellationToken token) => Task.FromResult(_stream);
        public async ValueTask DisposeAsync() { await release; _stream.Dispose(); }
    }
    private sealed class RejectedTransport : IAudioTransport
    {
        public Task<Stream> ConnectAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, CancellationToken token) => throw new IOException("simulated app rejection");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FailingCleanupTransport(byte[] data) : IAudioTransport
    {
        private readonly Stream _stream = new HoldingStream(data);
        public Task<Stream> ConnectAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, CancellationToken token) => Task.FromResult(_stream);
        public ValueTask DisposeAsync() { _stream.Dispose(); throw new IOException("simulated cleanup failure"); }
    }
}

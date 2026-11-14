using System.Reflection;
using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task DeviceAudioAsync(string output)
    {
        Directory.CreateDirectory(output);
        var device = new AndroidDevice("scope-one", "One", "device", false, "hardware-one");
        var other = new AndroidDevice("scope-two", "Two", "device", false, "hardware-two");
        var music = new AndroidApp("cn.test.music", "Music", 1001, "media");
        var video = music with { Package = "cn.test.video", Uid = 1002 };
        var whole = AndroidApp.DeviceAudio;
        Check(whole.IsDeviceAudio && !(music with { Uid = -1 }).IsDeviceAudio
            && !(whole with { Uid = 1001 }).IsDeviceAudio,
            "whole-phone capture requires the exact reserved identity, never a failed UID lookup");
        var options = typeof(ScrcpyAdbTransport).GetMethod("CaptureOptions", BindingFlags.Static | BindingFlags.NonPublic)!;
        string Options(AndroidApp app, bool keep) => (string)options.Invoke(null, [app, keep])!;
        Check(!Options(whole, false).Contains("audio_target_packages") && Options(whole, false).Contains("audio_dup=false")
            && Options(whole, true).Contains("audio_dup=true"),
            "only explicit device capture omits the UID filter and supports both playback zones");
        Check(Options(music, true).Contains("audio_target_packages=cn.test.music")
            && Options(music with { Uid = -1 }, false).Contains("audio_target_packages=cn.test.music"),
            "ordinary and unresolved application captures retain their package filter");
        bool invalidRejected = false;
        try { Options(whole with { Uid = 0 }, false); }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { invalidRejected = true; }
        Check(invalidRejected, "malformed device identity cannot become an unfiltered capture");

        var adb = new AdbClient(Path.Combine(output, "offline-runtime"));
        string mirrorCmd = AdbVideoStreamTransport.BuildServerShellCommand("/data/local/tmp/sonicroute-video-server.jar",
            new VideoStreamOpenRequest(device, whole, "aabbccdd", new VideoServerOptions(
                NewDisplay: false, MirrorDeviceDisplay: true, InitialDisplaySize: new(1200, 900), StartAppOnServer: false)));
        Check(mirrorCmd.Contains(" display_id=0")
            && !mirrorCmd.Contains("new_display") && !mirrorCmd.Contains("start_app=")
            && !mirrorCmd.Contains("vd_system_decorations"),
            "device entry mirrors physical display zero and ignores old virtual display dimensions");
        Check(mirrorCmd.Contains(" audio=false ") && mirrorCmd.Contains(" max_fps=30 ")
            && mirrorCmd.Contains(" video_bit_rate=4000000 "),
            "mirror reuses bounded video settings and leaves audio to exactly one managed capture");
        byte[]? defaultIcon = WicIconLoader.LoadDefaultPhoneIconPng();
        Check(defaultIcon is { Length: > 100 } && defaultIcon[0] == 0x89 && defaultIcon[1] == 0x50,
            "device mirror keeps embedded WIC phone icon resource without on-disk runtime extraction");
        string appCmd = AdbVideoStreamTransport.BuildServerShellCommand("/data/local/tmp/sonicroute-video-server.jar",
            new VideoStreamOpenRequest(device, music, "aabbccdd", new VideoServerOptions()));
        Check(appCmd.Contains(" new_display") && appCmd.Contains(" start_app=cn.test.music")
            && appCmd.Contains("org.sonicroute.android.SonicRouteServer") && appCmd.Contains("socket_prefix=sonicroute"),
            "ordinary application displays retain their independent-display contract on SonicRouteServer");
        Check(VirtualDisplayClient.IsFirstFrame("INFO: SonicRoute first-frame")
            && !VirtualDisplayClient.IsFirstFrame("INFO: Texture: 1080x2400")
            && !VirtualDisplayClient.IsFirstFrame("no frame"),
            "mirror first-frame readiness cannot be satisfied by texture allocation alone");
        var desktopClient = new VirtualDisplayClient(adb, new VirtualDisplayRuntime(adb));
        var inner = (InProcessVirtualDisplayClient)typeof(VirtualDisplayClient).GetField("_inner", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(desktopClient)!;
        typeof(InProcessVirtualDisplayClient).GetField("_isDeviceAudio", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(inner, true);
        var onLog = typeof(InProcessVirtualDisplayClient).GetMethod("OnServerLogLine", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ready = (TaskCompletionSource)typeof(InProcessVirtualDisplayClient).GetField("_ready", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(inner)!;
        onLog.Invoke(inner, ["[server] INFO: SonicRoute app-state: display=12 state=active"]);
        Check(!ready.Task.IsCompleted && desktopClient.State == VirtualDisplayState.Starting,
            "a mirror does not become active before first frame presentation");
        typeof(InProcessVirtualDisplayClient).GetField("_firstFramePresented", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(inner, true);
        onLog.Invoke(inner, ["[server] INFO: SonicRoute app-state: display=12 state=active"]);
        ready.TrySetResult();
        Check(ready.Task.IsCompletedSuccessfully && desktopClient.State == VirtualDisplayState.Active,
            "a presented first frame activates the phone mirror cleanly");
        await desktopClient.DisposeAsync();
        Check((int)AndroidErrorCode.NoAudioRouteOrEnded == 57 && Enum.GetValues<AndroidErrorCode>().Length == 58,
            "removing the desktop-only error preserves the original error identifiers");

        byte[] wire = BuildWire();
        int starts = 0, stops = 0;
        var settings = new AndroidAudioSettingsStore(Path.Combine(output, "audio.json"));
        await using (var manager = new AndroidAudioManager(() => new AndroidAudioSession(
            () => { starts++; return new FakeTransport(wire, () => stops++); }, (_, _) => new FakePlayer()), settings))
        {
            var one = await manager.AddAsync(device, music);
            var two = await manager.AddAsync(device, video);
            var neighbour = await manager.AddAsync(other, music);
            var all = await manager.AddAsync(device, whole, false);
            Check(starts == 4 && stops == 2 && manager.Sources.Count == 2 && all.Session.State.State == CaptureState.Receiving
                && ReferenceEquals(manager.FindSource(neighbour.Id), neighbour),
                "device capture releases both same-phone app streams while the other phone continues");
            Check(one.Session.State.State == CaptureState.Idle && two.Session.State.State == CaptureState.Idle,
                "retired application transports are stopped before device capture is ready");
            Check(ReferenceEquals(all, await manager.AddAsync(device, whole, false)) && starts == 4,
                "repeated whole-phone drops reuse the same live capture");
            var displayAudio = await manager.AddAsync(device, music, false, displayOwned: true);
            await manager.ReleaseDisplayAudioAsync(displayAudio);
            Check(ReferenceEquals(displayAudio, all) && manager.FindSource(all.Id) == all && starts == 4,
                "opening or closing an app display neither duplicates nor stops explicit whole-phone audio");
            all.Session.SetVolume(24); all.Session.SetMute(true);
            var changedMode = await manager.AddAsync(device, whole, true);
            Check(changedMode.Session.State.Volume == 24 && changedMode.Session.State.Muted
                && changedMode.KeepPhoneAudio && starts == 5 && stops == 3,
                "changing whole-phone playback mode preserves volume and mute with one replacement");
            var appAgain = await manager.AddAsync(device, music, false);
            Check(stops == 4 && manager.Sources.Count == 3 && manager.FindSource(changedMode.Id) == null
                && manager.FindSource(appAgain.Id) == appAgain && neighbour.Session.State.State == CaptureState.Receiving,
                "explicit app capture leaves global mode and restores the phone's other prior apps");
        }
        await settings.FlushAsync();
        var restored = new AndroidAudioSettingsStore(settings.FilePath);
        Check(restored.Get(device, whole.Package).Volume == 24 && restored.Get(other, whole.Package).Volume == 100
            && restored.Get(device, music.Package).Volume == 100,
            "device preferences survive reload independently of ordinary apps and other phones");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int rapidStarts = 0;
        await using (var rapid = new AndroidAudioManager(() => new AndroidAudioSession(() =>
        {
            int attempt = Interlocked.Increment(ref rapidStarts);
            return attempt == 1 ? new DelayedCleanupTransport(wire, release.Task)
                : new FakeTransport(wire, () => { });
        }, (_, _) => new FakePlayer())))
        {
            await rapid.AddAsync(device, whole, false);
            var firstApp = rapid.AddAsync(device, music, false);
            var secondApp = rapid.AddAsync(device, video, false);
            bool bothWaiting = !firstApp.IsCompleted && !secondApp.IsCompleted && rapidStarts == 1;
            release.SetResult();
            Check(bothWaiting, "rapid app switches wait for an already-retiring global audio policy");
            await Task.WhenAll(firstApp, secondApp).WaitAsync(TimeSpan.FromSeconds(8));
            Check(rapidStarts == 3 && rapid.Sources.Count == 2 && rapid.Sources.All(s => !s.App.IsDeviceAudio),
                "both independent app streams start after the global policy finishes releasing");
        }
        await using (var owned = new AndroidAudioManager(() => new AndroidAudioSession(
            () => new FakeTransport(wire, () => { }), (_, _) => new FakePlayer())))
        {
            var screenAudio = await owned.AddAsync(device, whole, false, displayOwned: true);
            var ordinaryDisplayAudio = await owned.AddAsync(device, music, false, displayOwned: true);
            await owned.ReleaseDisplayAudioAsync(ordinaryDisplayAudio, music.Package);
            Check(owned.FindSource(screenAudio.Id) == screenAudio,
                "an app display borrowing mirror audio cannot release the mirror's owned capture");
            await owned.ReleaseDisplayAudioAsync(screenAudio);
            Check(owned.Sources.Count == 0, "closing a mirror releases its own audio when not explicitly retained");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "device-audio-summary.json"), JsonSerializer.Serialize(new
        { Assertions = _checks, RealAdb = false, ProductStarted = false,
            Scope = "capture options, mirror arguments, per-phone exclusivity, rapid release ordering, settings and display ownership" },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} device audio assertions; no phone access.");
    }
}

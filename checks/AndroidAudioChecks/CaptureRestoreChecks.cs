using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task CheckCaptureRestoreAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var phone = new AndroidDevice("restore-one", "Phone", "device", false, "restore-one");
        var other = new AndroidDevice("restore-two", "Other", "device", false, "restore-two");
        var music = new AndroidApp("cn.test.restoremusic", "Music", 2101, "media");
        var video = new AndroidApp("cn.test.restorevideo", "Video", 2102, "media");
        byte[] wire = BuildWire();
        AndroidAudioManager Manager(Func<IAudioTransport>? factory = null) => new(() =>
            new AndroidAudioSession(factory ?? (() => new FakeTransport(wire, () => { })), (_, _) => new FakePlayer()));
        await using (var manager = Manager())
        {
            var first = await manager.AddAsync(phone, music, false, "speaker-one");
            first.Session.SetVolume(23); first.Session.SetMute(true); first.Session.SetSmartVolume(false);
            var second = await manager.AddAsync(phone, video, true, "speaker-two");
            second.Session.SetVolume(71);
            var neighbour = await manager.AddAsync(other, music);
            var whole = await manager.AddAsync(phone, AndroidApp.DeviceAudio, false);
            Check(manager.Sources.Count == 2 && first.Session.State.State == CaptureState.Idle
                && second.Session.State.State == CaptureState.Idle, "whole-phone capture releases prior per-app policies while retaining their intent");
            var changed = await manager.AddAsync(phone, AndroidApp.DeviceAudio, true);
            await manager.RemoveAsync(changed.Id);
            var restoredFirst = manager.FindSource(first.Id)!;
            var restoredSecond = manager.FindSource(second.Id)!;
            Check(manager.Sources.Count == 3 && restoredFirst.Session.State.State == CaptureState.Receiving
                && restoredSecond.Session.State.State == CaptureState.Receiving && manager.FindSource(neighbour.Id) == neighbour,
                "leaving whole-phone mode restores both prior apps without touching the other phone");
            Check(restoredFirst.Session.State is { Volume: 23, Muted: true, SmartVolume: false, OutputId: "speaker-one" }
                && !restoredFirst.KeepPhoneAudio && restoredSecond.Session.State is { Volume: 71, OutputId: "speaker-two" },
                "restored applications retain volume, mute, smart-volume, output and phone-audio preference");
            var allAgain = await manager.AddAsync(phone, AndroidApp.DeviceAudio);
            await manager.RemoveApplicationAsync(phone, music.Package);
            await manager.RemoveAsync(allAgain.Id);
            Check(manager.FindSource(first.Id) == null && manager.FindSource(second.Id)?.Session.State.State == CaptureState.Receiving,
                "an app explicitly removed during whole-phone mode is not resurrected");
            var lastWhole = await manager.AddAsync(phone, AndroidApp.DeviceAudio);
            await manager.RemoveAsync(lastWhole.Id, restorePrevious: false);
            Check(manager.Sources.Count == 1 && manager.FindSource(neighbour.Id) == neighbour,
                "device disconnect/removal suppresses previous-app restoration");
        }

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int rapidStarts = 0;
        await using (var manager = Manager(() => Interlocked.Increment(ref rapidStarts) == 2
            ? new DelayedCleanupTransport(wire, release.Task) : new FakeTransport(wire, () => { })))
        {
            var first = await manager.AddAsync(phone, music);
            first.Session.SetVolume(31);
            var whole = await manager.AddAsync(phone, AndroidApp.DeviceAudio);
            var leaving = manager.RemoveAsync(whole.Id);
            var reentering = manager.AddAsync(phone, AndroidApp.DeviceAudio);
            Check(!leaving.IsCompleted && !reentering.IsCompleted && rapidStarts == 2,
                "quick re-entry waits for the old whole-phone policy instead of duplicating capture");
            release.TrySetResult();
            await Task.WhenAll(leaving, reentering).WaitAsync(TimeSpan.FromSeconds(3));
            await manager.RemoveAsync((await reentering).Id);
            Check(manager.FindSource(first.Id)?.Session.State is { State: CaptureState.Receiving, Volume: 31 }
                && manager.Sources.Count == 1 && rapidStarts == 4,
                "prior capture intent survives a quick exit and re-entry while cleanup is pending");
        }

        await using (var manager = Manager())
        {
            var owned = await manager.AddAsync(phone, music, false, displayOwned: true);
            var explicitApp = await manager.AddAsync(phone, video, false);
            var whole = await manager.AddAsync(phone, AndroidApp.DeviceAudio);
            await manager.ReleaseDisplayAudioAsync(owned, music.Package);
            await manager.RemoveAsync(whole.Id);
            Check(manager.FindSource(owned.Id) == null && manager.FindSource(explicitApp.Id)?.Session.State.State == CaptureState.Receiving,
                "closing a suspended app display removes only its own saved capture");
            var ownedAgain = await manager.AddAsync(phone, music, false, displayOwned: true);
            whole = await manager.AddAsync(phone, AndroidApp.DeviceAudio);
            await manager.RemoveAsync(whole.Id);
            await manager.ReleaseDisplayAudioAsync(ownedAgain, music.Package);
            Check(manager.FindSource(ownedAgain.Id) == null && manager.FindSource(explicitApp.Id) != null,
                "display ownership still releases the restored source after whole-phone mode");
            await manager.AddAsync(phone, AndroidApp.DeviceAudio);
            await manager.StopAllAsync();
            Check(manager.Sources.Count == 0, "stop-all discards saved captures and starts nothing again");
        }

        int attempts = 0;
        await using (var manager = Manager(() => Interlocked.Increment(ref attempts) == 2
            ? new RejectedTransport() : new FakeTransport(wire, () => { })))
        {
            var first = await manager.AddAsync(phone, music);
            var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.Changed += () =>
            {
                if (manager.FindSource(first.Id)?.Session.State.State == CaptureState.Receiving && attempts >= 3)
                    restored.TrySetResult();
            };
            bool failed = false;
            try { await manager.AddAsync(phone, AndroidApp.DeviceAudio); } catch (IOException) { failed = true; }
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(failed && attempts == 3 && manager.Sources.Count == 1,
                "failed whole-phone startup restores prior apps instead of losing their takeover");
        }
    }
}

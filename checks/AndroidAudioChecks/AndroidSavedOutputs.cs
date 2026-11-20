using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task CheckSavedPhoneSelectionAsync(string directory)
    {
        string path = Path.Combine(directory, "saved-phone-" + Guid.NewGuid().ToString("N") + ".json");
        var phone = new AndroidDevice("10.0.0.10:30001", "Fixture phone", "device", true, "hardware-a");
        var targetPhone = new AndroidDevice("usb-b", "Another phone", "device", false);
        string targetCurrent = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(targetPhone), AndroidOutputSink.Current.Key);
        var music = new AndroidApp("com.fixture.music", "Fixture music", 10203, "media");
        var outputs = new string?[] { null, "speakers-b" };
        var modes = new List<bool>();
        AndroidAudioManager Create(AndroidAudioSettingsStore store) => new(() => new AndroidAudioSession(
            () => new FakeTransport(BuildWire(rebase: true), () => { }, keep => modes.Add(keep)),
            (_, _) => new StartupPlayer(() => { }, () => { }), resolvePhone: key => key == AndroidDeviceIdentity.Key(targetPhone) ? targetPhone : null), store);
        var store = new AndroidAudioSettingsStore(path);
        await using (var manager = Create(store))
        {
            var source = await manager.AddAsync(phone, music);
            Check(store.GetSavedSources().Length == 0, "an ordinary phone capture does not start itself at login");
            await ((IMultiOutputAudioSession)source.Session).SetOutputsAsync(outputs);
            var whole = await manager.AddAsync(phone, AndroidApp.DeviceAudio);
            Check(store.GetSavedSources().Length == 0, "a replaced app is not captured over an active whole-phone source");
            await manager.RemoveAsync(whole.Id);
            var restored = manager.Sources.Single();
            Check(((IMultiOutputAudioSession)restored.Session).GetOutputs().SequenceEqual(outputs)
                && store.GetSavedSources().Single().Outputs.SequenceEqual(outputs), "returning an entire-phone source restores the previous app's multi-output choice");
        }
        await store.FlushAsync();
        phone = phone with { Serial = "10.0.0.11:30999" };
        store = new AndroidAudioSettingsStore(path);
        var saved = store.GetSavedSources().Single();
        Check(saved.DeviceKey == "device:hardware-a" && saved.Package == music.Package && saved.Outputs.SequenceEqual(outputs)
            && store.Get(phone with { HardwareId = "other" }, music.Package).ResumeOutputs == null,
            "a restarted application matches the hardware and package across a new wireless port, never the previous IP");
        await using (var manager = Create(store))
        {
            var source = await manager.ResumeAsync(phone, music, [null]);
            var session = (IMultiOutputAudioSession)source.Session;
            Check(modes.Last() == false && session.GetOutputs().SequenceEqual([null]) && store.GetSavedSources().Single().Outputs.SequenceEqual(outputs),
                "a temporarily missing output allows playback on the available endpoint and preserves the complete group");
            await source.Session.RestoreOutputsAsync(outputs, () => true);
            Check(session.GetOutputs().SequenceEqual(outputs), "a returned output rejoins the captured app's saved group");
            await source.Session.SetOutputAsync("speakers-b");
            Check(store.GetSavedSources().Length == 0, "explicit single selection cancels capture on the next launch");
            await source.Session.RestoreOutputsAsync(outputs, () => store.Get(phone, music.Package).ResumeOutputs != null);
            Check(session.GetOutputs().SequenceEqual(["speakers-b"]), "queued automatic recovery respects a newer single selection");
            var whole = await manager.AddAsync(phone, AndroidApp.DeviceAudio);
            await ((IMultiOutputAudioSession)whole.Session).SetOutputsAsync([targetCurrent, AndroidAudioSession.SourcePhoneOutputId]);
        }
        await store.FlushAsync();
        store = new AndroidAudioSettingsStore(path);
        saved = store.GetSavedSources().Single();
        await using (var manager = Create(store))
        {
            var whole = await manager.ResumeAsync(phone, AndroidApp.DeviceAudio, saved.Outputs);
            Check(saved.Package == AndroidApp.DeviceAudioPackage && saved.Outputs.Contains(targetCurrent) && modes.Last() && whole.Session.UsesSourcePhonePlayback
                && ((IMultiOutputAudioSession)whole.Session).GetOutputs().Contains(targetCurrent),
                "whole-phone audio simultaneously restores source native playback and the current output of the other phone");
            await manager.RemoveApplicationAsync(phone, AndroidApp.DeviceAudioPackage);
            Check(store.GetSavedSources().Length == 0, "explicitly returning an entire phone prevents it from opening again");
        }
        await store.FlushAsync();
    }
}

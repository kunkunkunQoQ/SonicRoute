using System.Collections;
using System.Reflection;
using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task IdleRetirementAsync(string output)
    {
        Directory.CreateDirectory(output);
        var device = new AndroidDevice("offline-fixture", "Fixture", "device", false, "retirement-fixture");
        var app = new AndroidApp("fixture.retirement", "Fixture", 1001, "media");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var sources = new AndroidAudioManager(() => new AndroidAudioSession(
            () => new DelayedCleanupTransport(BuildWire(), release.Task), (_, _) => new FakePlayer())))
        {
            sources.ResourcesRetired += () => retired.TrySetResult();
            var source = await sources.AddAsync(device, app);
            Check(sources.HasActiveWork, "a published or starting phone capture blocks idle cleanup");
            var stop = sources.RemoveAsync(source.Id);
            Check(sources.Sources.Count == 0 && sources.HasActiveWork && !stop.IsCompleted,
                "a removed capture remains active for reclamation until its transport has retired");
            release.SetResult(); await stop; await retired.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!sources.HasActiveWork, "finished phone capture cleanup emits an idle-resource event outside its registry");
        }

        var startingRetired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var sources = new AndroidAudioManager(() => new AndroidAudioSession(
            () => new PendingTransport(), (_, _) => new FakePlayer())))
        {
            sources.ResourcesRetired += () => startingRetired.TrySetResult();
            var start = sources.AddAsync(device, app);
            Check(sources.HasActiveWork && !start.IsCompleted, "an unfinished capture start is counted before playback is ready");
            await sources.RemoveAsync(sources.Sources.Single().Id);
            try { await start; } catch (OperationCanceledException) { }
            await startingRetired.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!sources.HasActiveWork, "cancelling an unfinished capture releases its activity guard");
        }

        var pcRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pcRetired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var pc = new PcToPhoneAudioManager(() => throw new InvalidOperationException("No ADB in retirement fixture"))
            { EnableLiveStreaming = false })
        {
            pc.ResourcesRetired += () => pcRetired.TrySetResult();
            string endpoint = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "speaker");
            Check(pc.RouteProcess(12345, endpoint, device).Success && pc.HasActiveWork, "a PC-to-phone route blocks idle cleanup");
            var routes = (IDictionary)typeof(PcToPhoneAudioManager).GetField("_routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pc)!;
            var route = routes[12345]!;
            route.GetType().GetField("_streamTask", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(route, pcRelease.Task);
            var stop = pc.StopProcessAsync(12345);
            Check(pc.ActiveCount == 0 && pc.HasActiveWork && !stop.IsCompleted,
                "PC route retirement still blocks cleanup after the visible route has disappeared");
            pcRelease.SetResult(); await stop; await pcRetired.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!pc.HasActiveWork, "PC route cleanup emits retirement only after its stream has completed");
        }

        var displayRetired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var displays = new AndroidVirtualDisplayManager(() => new FakeDisplay()))
        {
            displays.ResourcesRetired += () => displayRetired.TrySetResult();
            await displays.OpenAsync(device, app, 0, 0);
            Check(displays.Count == 1, "a starting or active display stays in the protected registry");
            await displays.CloseApplicationAsync(device, app.Package);
            await displayRetired.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(displays.Count == 0, "display resources notify retirement after closing their client");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "retirement.json"), JsonSerializer.Serialize(new
        { Success = true, Assertions = _checks, PhoneAccess = false, EndpointWrites = false, AdbAccess = false }));
        Console.WriteLine($"PASS {_checks} idle-retirement assertions; fixture transports only.");
    }
}

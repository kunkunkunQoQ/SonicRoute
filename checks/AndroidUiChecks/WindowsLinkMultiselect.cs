using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task CheckMultipleWindowsOutputsAsync()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = limit.Token;
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "AndroidUiChecks.exe"), "--silent-route-child")
        { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true })!;
        string root = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!;
        int acquired = 0, released = 0;
        bool grant = true;
        await using var sender = new WindowsLinkService(Path.Combine(root, "source"), "Source", port: 0, discovery: false,
            acquireLocalSilence: pid =>
            {
                if (pid != child.Id || !grant || acquired != released) return null;
                Interlocked.Increment(ref acquired);
                return new PushSpeakerLease(() => Interlocked.Increment(ref released));
            }) { ProbeAudioCapture = _ => true, ProbeExclusiveCapture = () => true };
        WindowsLinkService Receiver(string name) => new(Path.Combine(root, name), name, port: 0, discovery: false,
            audioPlayerFactory: (_, _, _) => new AirPlayFixtureOutput());
        await using var a = Receiver("A");
        await using var b = Receiver("B");
        foreach (var device in new[] { sender, a, b }) device.PairingRequested += p => device.AcceptPairing(p.Id, true);
        const BindingFlags bindings = BindingFlags.Static | BindingFlags.NonPublic;
        var assembly = typeof(App).Assembly;
        var hub = assembly.GetType("SonicRoute.WindowsLinkHub")!;
        var serviceField = hub.GetField("_service", bindings)!;
        var previous = serviceField.GetValue(null);
        var routes = assembly.GetType("SonicRoute.AppPlaybackOutputs")!;
        var app = new AudioAppInfo { ProcessId = (uint)child.Id, DisplayName = "Silent route fixture" };
        const string local = AudioService.SystemDefaultDeviceId;
        Task<(bool Success, int HResult, string Message)> Set(params string[] ids) =>
            (Task<(bool, int, string)>)routes.GetMethod("SetAsync", bindings)!.Invoke(null, [app, ids, ids.FirstOrDefault(), false])!;
        HashSet<string> Selected() => (HashSet<string>)routes.GetMethod("GetSelected", bindings)!.Invoke(null, [child.Id, local])!;
        try
        {
            await Task.WhenAll(sender.EnableAsync(token), a.EnableAsync(token), b.EnableAsync(token));
            await sender.ConnectAsync(a.LocalAddress, token: token);
            await sender.ConnectAsync(b.LocalAddress, token: token);
            while (sender.Peers.Count(p => p.Connected) != 2) await Task.Delay(15, token);
            serviceField.SetValue(null, sender);
            var serviceChanged = hub.GetMethod("ServiceChanged", bindings)!.CreateDelegate<Action>();
            sender.Changed += serviceChanged;
            serviceChanged();
            string aId = sender.Peers.Single(p => p.DisplayName == "A").Id;
            string bId = sender.Peers.Single(p => p.DisplayName == "B").Id;
            string aOut = WindowsLinkOutputIdentity.FormatDeviceId(aId), bOut = WindowsLinkOutputIdentity.FormatDeviceId(bId);
            string absent = WindowsLinkOutputIdentity.FormatDeviceId(Guid.NewGuid().ToString("N"));
            Check((await Set(local, aOut)).Success && Selected().SetEquals([local, aOut]), "local and A are shared");
            Guid originalA = sender.Streams.Single().Id;
            Check((await Set(local, aOut, bOut)).Success && Selected().SetEquals([local, aOut, bOut])
                && sender.Streams.Any(s => s.Id == originalA), "adding B keeps A's current stream");
            Check(!(await Set(aOut, absent)).Success && Selected().SetEquals([local, aOut, bOut])
                && acquired == released, "a failed group change restores the original shared outputs and releases its mute");
            Check((await Set(bOut, aOut)).Success && Selected().SetEquals([aOut, bOut])
                && sender.FindOutgoingAppAudio(child.Id) is { DestinationOnly: true } owner && owner.PeerId == bId,
                "only the confirmed exclusive owner suppresses the local selection; lookup returns it ahead of a shared stream");
            int reservations = acquired;
            var captures = (IDictionary)typeof(LinkAudioCapture).Assembly.GetType("SonicRoute.WindowsLink.SharedProcessCapture")!
                .GetField("Active", bindings)!.GetValue(null)!;
            Check(captures.Count == 1, "two receivers share one native process capture");
            Check((await Set(aOut)).Success && Selected().SetEquals([aOut]) && sender.Streams.Single().DestinationOnly
                && acquired == reservations && released == acquired - 1, "changing exclusive owner keeps the existing mute lease throughout");
            Guid isolated = sender.Streams.Single().Id;
            string offlinePhone = AndroidOutputIdentity.FormatDeviceId("isolated-missing-phone", "speaker");
            Check(!(await Set(offlinePhone)).Success && Selected().SetEquals([aOut]) && sender.Streams.Single().Id == isolated,
                "an unavailable Android output fails before disturbing the confirmed Windows source lease");
            Check(!(await Set(bOut, absent)).Success && Selected().SetEquals([aOut]) && sender.Streams.Single().Id == isolated,
                "a failed replacement keeps A's confirmed stream and mute lease");
            Check((await Set()).Success && Selected().SetEquals([local]) && released == acquired && captures.Count == 0,
                "empty selection restores System, releases the mute and closes the capture");
            grant = false;
            Check(!(await Set(aOut)).Success && Selected().SetEquals([local]) && sender.Streams.Length == 0,
                "a single receiver never silently becomes a shared output when isolation fails");
            grant = true;
            async Task WaitAsync(Func<bool> ready)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                while (!ready()) await Task.Delay(20, timeout.Token);
            }
            async Task ReconnectB()
            {
                await sender.ConnectAsync(b.LocalAddress, token: token);
                await WaitAsync(() => sender.Peers.Any(p => p.Id == bId && p.Connected));
            }
            void DropB()
            {
                var connections = (IDictionary)typeof(WindowsLinkService).GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
                ((LinkConnection)connections[bId]!).Abort();
            }
            Check((await Set(local, aOut, bOut)).Success, "three-output history starts with both live receivers");
            var surviving = sender.GetOutgoingAppStreams(child.Id).Single(s => s.PeerId == aId).Id;
            DropB();
            await WaitAsync(() => Selected().SetEquals([local, aOut]));
            Check(sender.GetOutgoingAppStreams(child.Id).Single().Id == surviving, "network loss removes only B and continues the same A stream and Windows output");
            await ReconnectB();
            Check((await Set(local, aOut)).Success && (await Set(bOut)).Success, "B can be selected alone after the local-and-A combination");
            DropB();
            await WaitAsync(() => Selected().SetEquals([local, aOut]));
            Check(sender.GetOutgoingAppStreams(child.Id).Single() is { DestinationOnly: false }, "loss of the sole B restores the last successful online combination, including A");
            await ReconnectB();
            Check((await Set(bOut)).Success, "the single-output fixture can be rearmed");
            await sender.StopStreamAsync(sender.GetOutgoingAppStreams(child.Id).Single().Id);
            await WaitAsync(() => !(bool)routes.GetMethod("IsManaged", bindings)!.Invoke(null, [child.Id])!);
            Check(sender.Streams.Length == 0, "manual stream stop clears the remembered choice without starting the prior receiver");
            Check((await Set(local, aOut)).Success && (await Set()).Success &&
                !(bool)routes.GetMethod("IsManaged", bindings)!.Invoke(null, [child.Id])!, "reset forgets fallback history");
            // Replay the same endpoint-ended notification emitted after the Android player's real
            // transport has been retired. The peer selected as fallback still uses real paired TLS.
            var remember = routes.GetMethod("Remember", bindings)!;
            var ended = routes.GetMethod("OutputEnded", bindings)!;
            foreach (var sink in new[] { "speaker", "bt:aa" })
            {
                string output = AndroidOutputIdentity.FormatDeviceId("isolated-missing-phone", sink);
                Check((await Set(aOut)).Success, "A is the previous output for the offline Android " + sink);
                await sender.StopStreamAsync(sender.GetOutgoingAppStreams(child.Id).Single().Id);
                await WaitAsync(() => !(bool)routes.GetMethod("IsManaged", bindings)!.Invoke(null, [child.Id])!);
                remember.Invoke(null, [child.Id, new[] { aOut }, new[] { output }, output, false]);
                ended.Invoke(null, [child.Id, output, true]);
                await WaitAsync(() => Selected().SetEquals([aOut]) && sender.GetOutgoingAppStreams(child.Id).Length == 1);
                Check(sender.GetOutgoingAppStreams(child.Id).Single().DestinationOnly, "losing the only Android " + sink + " reopens the last Windows receiver without adding local playback");
            }
            var beforeNativeChange = sender.GetOutgoingAppStreams(child.Id).Single().Id;
            ended.Invoke(null, [child.Id, local, true]);
            Check(sender.GetOutgoingAppStreams(child.Id).Single().Id == beforeNativeChange, "a local Windows hardware notification cannot replace the chosen cross-device route");
            child.StandardInput.Close();
            await child.WaitForExitAsync(token);
            await WaitAsync(() => !(bool)routes.GetMethod("IsManaged", bindings)!.Invoke(null, [child.Id])!
                && sender.GetOutgoingAppStreams(child.Id).Length == 0);
            Check(true, "exiting the source process retires the fallback history and its audio stream");
            sender.Changed -= serviceChanged;
        }
        finally
        {
            serviceField.SetValue(null, previous);
            child.StandardInput.Close();
            await child.WaitForExitAsync(token);
        }
    }
}

using SonicRoute.AndroidAudio;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task CheckPhoneSinkHardwareAsync(string runtime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = timeout.Token;
        // Use the user's already-running ADB server. Each AudioTrack carries only silence.
        await using var adb = new AdbClient(runtime, new AdbSocketClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 5037)));
        var device = (await adb.DevicesAsync(token)).Single(d => d.State == "device");
        var sinks = await adb.OutputsAsync(device, token);
        var candidates = sinks.Where(s => s.Kind == AndroidOutputSinkKind.Speaker ||
            s.Kind == AndroidOutputSinkKind.Bluetooth && s.AndroidDeviceType != 7).ToArray();
        Check(candidates.Any(s => s.Kind == AndroidOutputSinkKind.Speaker), "the connected phone reports its speaker");
        Console.WriteLine($"Connected Bluetooth media outputs: {candidates.Count(s => s.Kind == AndroidOutputSinkKind.Bluetooth)}");
        foreach (var sink in candidates)
        {
            var received = new AndroidPcmSource();
            using var player = new AndroidPhonePcmPlayer(adb, device, received, sink.Key, token);
            await player.VerifyBeforePlaybackAsync(token);
            player.Play();
            received.Write(new float[1920]);
            while (player.Diagnostics.Bytes < 3840) await Task.Delay(15, token);
            Check(player.Diagnostics.RoutedType == sink.AndroidDeviceType && player.Diagnostics.NonSilentBytes == 0,
                $"{sink.Kind}: Android confirmed hardware type {sink.AndroidDeviceType} and consumed silent PCM");
        }
    }

    private static async Task CheckAndroidLinkForwardingAsync(string directory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        string root = Path.Combine(directory, "pair-" + Guid.NewGuid().ToString("N"));
        async Task Until(Func<bool> done) { while (!done()) await Task.Delay(10, token); }
        WindowsLinkService Node(string name)
        {
            var node = new WindowsLinkService(Path.Combine(root, name), name, 0, discovery: false,
                audioPlayerFactory: (_, _, _) => new StartupPlayer(() => { }, () => { }));
            node.PairingRequested += p => node.AcceptPairing(p.Id, true);
            return node;
        }
        await using var bridge = Node("bridge");
        await using var kun = Node("KUN");
        await bridge.EnableAsync(token);
        await kun.EnableAsync(token);
        await kun.ConnectAsync(bridge.LocalAddress, token: token);
        var remote = WindowsLinkOutputIdentity.FormatDeviceId(bridge.Peers.Single(p => p.Connected).Id);
        var device = new AndroidDevice("fixture-phone", "Fixture", "device", true);
        var modes = new List<bool>();
        int active = 0, peak = 0;
        await using var session = new AndroidAudioSession(() => new FakeTransport(BuildWire(),
            () => active--, mode => { modes.Add(mode); peak = Math.Max(peak, ++active); }),
            (_, _) => new StartupPlayer(() => { }, () => { })) { LinkService = () => bridge };
        var route = (IMultiOutputAudioSession)session;
        foreach (var app in new[] { new AndroidApp("example.audio", "Fixture music", 123, "media"), AndroidApp.DeviceAudio })
        {
            await session.SetOutputAsync(remote);
            await session.StartAsync(device, app, true, token);
            await Until(() => kun.IncomingAudioSources.SingleOrDefault()?.Session.Pcm?.NonSilentSamplesWritten > 0);
            var received = kun.IncomingAudioSources.Single();
            Check(received.IsRelayed && received.AppKey == (app.IsDeviceAudio ? LinkRemoteApp.SystemAudioKey : app.Package)
                && !session.UsesSourcePhonePlayback && modes.Last() == false && peak == 1,
                "single KUN playback receives decoded Opus over TLS with the correct Android source identity and one exclusive capture");
            await route.SetOutputsAsync([remote, AndroidAudioSession.SourcePhoneOutputId]);
            Check(session.UsesSourcePhonePlayback && modes.Last() && peak == 1, "adding native phone playback keeps one phone capture");
            await route.SetOutputsAsync(["fixture-speaker"]);
            await Until(() => kun.IncomingAudioSources.Length == 0);
            await route.SetOutputsAsync([remote, "fixture-speaker"]);
            var stream = kun.IncomingAudioSources.Single();
            await kun.StopStreamAsync(stream.StreamId);
            await Until(() => route.GetOutputs().SequenceEqual(["fixture-speaker"]));
            Check(session.State.State == CaptureState.Receiving, "losing a remote mirror keeps the selected speaker active");
            await route.SetOutputsAsync([remote]);
            await kun.StopStreamAsync(kun.IncomingAudioSources.Single().StreamId);
            await Until(() => route.GetOutputs().SequenceEqual(["fixture-speaker"]));
            Check(session.State.State == CaptureState.Receiving && !session.UsesSourcePhonePlayback,
                "losing the only remote output restores the previous speaker");
            await session.StopAsync();
        }
    }
}

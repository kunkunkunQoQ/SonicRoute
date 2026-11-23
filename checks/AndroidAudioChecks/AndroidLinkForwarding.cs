using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task CheckPhoneSinkHardwareAsync(string runtime, string? savedProfile = null, string? address = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(savedProfile == null ? 35 : 90));
        var token = timeout.Token;
        // Each AudioTrack carries only silence. With a saved profile use the same verified
        // wireless reconnect path as the app, including when no external ADB server is running.
        await using var adb = new AdbClient(runtime, savedProfile == null
            ? new AdbSocketClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 5037)) : null);
        var device = savedProfile == null ? (await adb.DevicesAsync(token)).Single(d => d.State == "device")
            : (await new AndroidWirelessConnection(adb).ReconnectAsync(new AndroidPhoneStore(savedProfile).Profile.LastDevice
                ?? throw new InvalidOperationException("No saved phone"), address, token)).Device;
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
            if (savedProfile != null)
            {
                var frame = new float[1920];
                using var cadence = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
                for (int i = 0; i < 500; ++i)
                {
                    await cadence.WaitForNextTickAsync(token);
                    received.Write(frame);
                }
                while (player.Diagnostics.Bytes < 501L * frame.Length * sizeof(short))
                {
                    if (player.Failure is { } error) throw error;
                    await Task.Delay(10, token);
                }
                Check(received.DroppedSamples == 0 && player.Failure == null
                    && (await adb.DevicesAsync(token)).Any(d => d.State == "device" && AndroidDeviceIdentity.MatchesSavedDevice(device, d)),
                    $"{sink.Kind}: all 10 seconds of PCM sent, zero queue drops, same phone still online");
            }
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
        var raw = BuildRawPcmWire(includeEmptyConfig: true, includeLargePacket: false, includeKeyFrameFlagOnLast: false);
        byte[] wire = BuildWire();
        await using var session = new AndroidAudioSession(() => new FakeTransport(wire,
            () => active--, mode => { modes.Add(mode); peak = Math.Max(peak, ++active); }),
            (_, _) => new StartupPlayer(() => { }, () => { })) { LinkService = () => bridge };
        session.SetSmartVolume(false);
        var route = (IMultiOutputAudioSession)session;
        foreach (var app in new[] { new AndroidApp("example.audio", "Fixture music", 123, "media"), AndroidApp.DeviceAudio })
        {
            string codec = app.IsDeviceAudio ? "Raw PCM" : "Opus";
            if (app.IsDeviceAudio) wire = raw.Wire;
            await session.SetOutputAsync(remote);
            await session.StartAsync(device, app, true, token);
            await Until(() => session.PacketsDecoded == 5 &&
                kun.IncomingAudioSources.SingleOrDefault()?.Session.Pcm?.BufferedSamples == session.Pcm!.SamplesWritten);
            var received = kun.IncomingAudioSources.Single();
            Check(received.IsRelayed && received.AppKey == (app.IsDeviceAudio ? LinkRemoteApp.SystemAudioKey : app.Package)
                && !session.UsesSourcePhonePlayback && modes.Last() == false && peak == 1,
                $"{codec} reaches KUN over TLS with the correct Android source identity and one exclusive capture");
            received.Session.SetSmartVolume(false);
            float[] expected = app.IsDeviceAudio ? raw.ExpectedSamples : ReadBufferedSamples(session.Pcm!);
            float[] actual = ReadBufferedSamples(received.Session.Pcm!);
            Check(expected.Length > 0 && actual.Length == expected.Length && actual.Any(sample => Math.Abs(sample) > 0.1f)
                && (app.IsDeviceAudio ? actual.SequenceEqual(expected)
                    : actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) <= 0.5f / 32768f)),
                $"{codec} preserves the decoded stereo samples through Windows Link PCM16 forwarding");
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
                $"{codec} restores the previous speaker after losing the only remote output");
            await session.StopAsync();
        }

        static float[] ReadBufferedSamples(AndroidPcmSource pcm)
        {
            byte[] bytes = new byte[pcm.BufferedSamples * sizeof(float)];
            pcm.Read(bytes, 0, bytes.Length);
            return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        }
    }
}

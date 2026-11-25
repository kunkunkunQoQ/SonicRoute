using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Net;
using System.Text.Json;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static void AirPlayEldFixture(string input, string output)
    {
        byte[] fixture = File.ReadAllBytes(input);
        var asc = fixture.AsSpan(1, fixture[0]);
        int frames = asc[2] == 0x50 ? 480 : 512;
        var format = AirPlayFormat.Parse(new()
        {
            ["ct"] = 8L, ["audioFormat"] = 1L << (asc[1] == 0xe8 ? 24 : 25), ["spf"] = (long)frames,
            ["audioConfig"] = asc.ToArray()
        });
        using var decoder = new AirPlayDecoder(format);
        using var file = File.Create(output);
        var pcm = new float[frames * 2];
        int position = 1 + asc.Length, count = 0;
        bool complete = true;
        while (position < fixture.Length)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(fixture.AsSpan(position));
            position += 2;
            complete &= decoder.Decode(fixture.AsSpan(position, length), pcm) == pcm.Length;
            file.Write(MemoryMarshal.AsBytes(pcm.AsSpan()));
            position += length;
            ++count;
        }
        Check(count >= 12 && complete && pcm.Any(value => Math.Abs(value) > 0.02f), "all AAC-ELD access units decode to stereo PCM");
        Check(decoder.Decode([0xff], pcm) == 0 && decoder.Decode([0, 0, 0, 0], pcm) == pcm.Length
              && pcm.All(value => value == 0), "a malformed ELD frame clears overlap and the next packet can play");
    }

    private static async Task AirPlayLoopbackAsync(bool preferHevc, bool requireCode)
    {
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
        System.Diagnostics.Trace.AutoFlush = true;
        var created = new ConcurrentQueue<(string? Endpoint, SimulatedOutput Player)>();
        Action? defaultChanged = null;
        int detached = 0;
        string name = requireCode ? "投屏.测试 🎵" : "SonicRoute Simulation";
        await using var receiver = new AirPlayReceiver(name, loopback: true,
            player: (pcm, endpoint, _) =>
            {
                var player = new SimulatedOutput(pcm);
                created.Enqueue((endpoint, player));
                return player;
            }, watchDefault: action =>
            {
                defaultChanged = action;
                return new AirPlayWatch(() => Interlocked.Increment(ref detached));
            }, preferHevc: preferHevc, requireCode: requireCode);
        int port = receiver.Port;
        byte[] mdns = AirPlayDiscovery.WriteResponse(new AirPlayIdentity(name, requireCode: requireCode), IPAddress.Loopback, port, 120);
        Console.WriteLine(JsonSerializer.Serialize(new { Port = port, Mdns = Convert.ToBase64String(mdns) }));
        while (await Console.In.ReadLineAsync() is { } command && command != "stop")
        {
            var source = receiver.Source;
            if (command == "controls" && source != null)
            {
                source.SetVolume(37);
                source.SetMute(true);
                await source.SetOutputAsync("simulated-endpoint");
            }
            if (command == "system" && source != null) await source.SetOutputAsync(null);
            if (command == "default") defaultChanged?.Invoke();
            if (command == "disconnect" && source != null) receiver.Disconnect(source.Id);
            var outputs = created.ToArray();
            var status = receiver.Status;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Stage = status.Stage.ToString(), status.Sender, status.Mirroring, status.Code,
                receiver.MirrorFrames, status.ErrorKey, status.MirrorAudioUnavailable,
                Track = status.Metadata is { } track ? new { track.Title, track.Artist, ArtworkBytes = track.Artwork?.Length } : null,
                source?.Id, Volume = source?.State.Volume, Muted = source?.State.Muted,
                Endpoint = source?.State.OutputId, SourceSamples = source?.Pcm?.SamplesWritten,
                NonSilent = source?.Pcm?.NonSilentSamplesWritten, Pending = source?.Pcm?.BufferedSamples,
                Outputs = outputs.Length, Disposed = outputs.Count(o => o.Player.Disposed), Detached = detached
            }));
        }
        await receiver.DisposeAsync();
        bool remains = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Any(l => l.Address.Equals(IPAddress.Loopback) && l.Port == port);
        Console.WriteLine(JsonSerializer.Serialize(new { Closed = receiver.Completion.IsCompletedSuccessfully && !remains }));
    }

    private static async Task AirPlayPhoneOutputAsync(string output)
    {
        string runtime = Path.Combine(output, "simulated-runtime");
        Directory.CreateDirectory(runtime);
        await File.WriteAllTextAsync(Path.Combine(runtime, "sonicroute-catalog.jar"), "FAKE-JAR-CONTENT");
        await using var server = new FakeAdbServer();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var adb = new AdbClient(runtime, new AdbSocketClient(server.EndPoint));
        var device = (await adb.DevicesAsync(lifetime.Token)).Single();
        string phoneKey = AndroidDeviceIdentity.Key(device);
        string speaker = AndroidOutputIdentity.FormatDeviceId(phoneKey, "speaker");
        string unavailable = AndroidOutputIdentity.FormatDeviceId(phoneKey, "bt:unavailable");
        var windowsPlayers = new List<SimulatedOutput>();
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AirPlayAudioOutput? tracked = null;
        bool expectFallback = false;
        int defaultWatches = 0, errors = 0;
        await using var source = new AirPlayAudioOutput("Apple Fixture", () =>
        {
            if (Volatile.Read(ref expectFallback) && tracked?.State.OutputId == null) restored.TrySetResult();
        }, () => Interlocked.Increment(ref errors), (pcm, endpoint, token) =>
        {
            if (AndroidOutputIdentity.TryParseDeviceId(endpoint, out _, out string sink))
                return new AndroidPhonePcmPlayer(adb, device, pcm, sink, token);
            var player = new SimulatedOutput(pcm);
            windowsPlayers.Add(player);
            return player;
        }, _ =>
        {
            Interlocked.Increment(ref defaultWatches);
            return new AirPlayWatch(() => Interlocked.Decrement(ref defaultWatches));
        }, lifetime.Token);
        tracked = source;
        source.Write([0.25f, -0.25f], AndroidPcmSource.SampleRate);
        source.Flush();
        source.SetVolume(37);
        var pcmSource = source.Pcm;

        bool rejected = false;
        try { await source.SetOutputAsync(unavailable).WaitAsync(lifetime.Token); }
        catch (IOException ex) when (AndroidError.TryGetCode(ex, out var code) && code == AndroidErrorCode.PhoneAudioOutputFailed)
        { rejected = true; }
        Check(rejected && source.State.OutputId == null && !windowsPlayers.Single().Disposed && defaultWatches == 1,
            "a rejected Android output leaves the prior playing endpoint and default watch intact");

        await source.SetOutputAsync(speaker).WaitAsync(lifetime.Token);
        const int silentBytes = 2 * 960 * 2 * sizeof(short); // One priming chunk per attempt.
        byte[] primed = await server.WaitForLocalAbstractBytesAsync(silentBytes, TimeSpan.FromSeconds(2));
        Check(primed.All(value => value == 0) && source.State.OutputId == speaker
            && ReferenceEquals(source.Pcm, pcmSource) && source.State.Volume == 37
            && windowsPlayers[0].Disposed && defaultWatches == 0,
            "the speaker route verifies with silence while the sender is paused and keeps the same PCM source and volume");
        source.Write(Enumerable.Repeat(0.35f, 1920).ToArray(), AndroidPcmSource.SampleRate);
        byte[] playback = await server.WaitForLocalAbstractBytesAsync(silentBytes + 3840, TimeSpan.FromSeconds(2));
        Check(playback.Skip(silentBytes).Any(value => value != 0), "AirPlay PCM reaches the selected Android speaker bridge");
        source.SetMute(true);
        source.Write(Enumerable.Repeat(0.35f, 1920).ToArray(), AndroidPcmSource.SampleRate);
        byte[] muted = await server.WaitForLocalAbstractBytesAsync(silentBytes + 7680, TimeSpan.FromSeconds(2));
        Check(muted.Skip(silentBytes + 3840).All(value => value == 0), "the speaker route honors the AirPlay row's mute");

        await source.SetOutputAsync(null).WaitAsync(lifetime.Token);
        Check(source.State.OutputId == null && defaultWatches == 1 && source.State.Muted
            && ReferenceEquals(source.Pcm, pcmSource), "the same AirPlay session can return to the Windows default output");
        var current = AndroidOutputIdentity.FormatDeviceId(phoneKey, "current");
        await source.SetOutputAsync(current).WaitAsync(lifetime.Token);
        Check(source.State.OutputId == current && source.State.State == CaptureState.Receiving,
            "AirPlay can select Android's current media route without ending its input stream");
        await source.PhoneRemovedAsync(device).WaitAsync(lifetime.Token);
        Check(source.State.State == CaptureState.Receiving && source.State.OutputId == null && defaultWatches == 1,
            "removing the selected Android device releases its renderer and resumes Windows output");

        var multi = (IMultiOutputAudioSession)source;
        source.SetMute(false);
        source.SetVolume(55);
        source.SetRemoteVolume(-6.0);
        int winCountBefore = windowsPlayers.Count;
        var primaryWin = windowsPlayers[^1];
        int speakerBefore = server.ReceivedFor("speaker").Length;
        await multi.SetOutputsAsync([null, "win-extra", speaker, "WIN-EXTRA"]).WaitAsync(lifetime.Token);
        await multi.SetOutputsAsync([null, "win-extra", speaker]).WaitAsync(lifetime.Token);
        var mirrorWin = windowsPlayers[^1];
        Check(!primaryWin.Disposed && windowsPlayers.Count == winCountBefore + 1 && defaultWatches == 1
            && multi.GetOutputs().SequenceEqual([null, "win-extra", speaker])
            && Math.Abs(mirrorWin.Pcm.Volume - 0.55f) < 0.001f
            && Math.Abs(mirrorWin.Pcm.HeaderGain - pcmSource!.HeaderGain) < 0.0001f,
            "AirPlay multi-output adds mirrors without recreating primary Windows output and suppresses duplicates");
        source.SetRemoteVolume(0.0);
        source.SetMute(true);
        Check(mirrorWin.Pcm.Volume == 0 && Math.Abs(mirrorWin.Pcm.HeaderGain - 1f) < 0.0001f,
            "AirPlay mute and remote volume sync across mirror outputs");
        source.SetMute(false);
        long primaryNonSilentBefore = pcmSource!.NonSilentSamplesWritten;
        long mirrorNonSilentBefore = mirrorWin.Pcm.NonSilentSamplesWritten;
        source.Write(Enumerable.Repeat(0.35f, 1920).ToArray(), AndroidPcmSource.SampleRate);
        await server.WaitForLocalAbstractBytesAsync(silentBytes + 11520 + 7680, TimeSpan.FromSeconds(2));
        Check(pcmSource.NonSilentSamplesWritten == primaryNonSilentBefore + 1920
            && mirrorWin.Pcm.NonSilentSamplesWritten == mirrorNonSilentBefore + 1920
            && server.ReceivedFor("speaker").Skip(speakerBefore + 3840).Any(v => v != 0),
            "one AirPlay Write fans out non-zero PCM simultaneously to primary, Windows mirror, and Android speaker");
        source.Write(Enumerable.Repeat(0.25f, 4800).ToArray(), AndroidPcmSource.SampleRate);
        source.Flush();
        Check(source.BufferedSamples == 0 && mirrorWin.Pcm.BufferedSamples == 0,
            "AirPlay Flush clears both primary and mirror PCM buffers");
        await source.PhoneRemovedAsync(device).WaitAsync(lifetime.Token);
        Check(source.State.State == CaptureState.Receiving && !primaryWin.Disposed && !mirrorWin.Disposed
            && multi.GetOutputs().SequenceEqual([null, "win-extra"]),
            "removing a mirrored phone drops only the phone mirror while keeping Windows outputs active");
        await multi.SetOutputsAsync([speaker, "win-extra"]).WaitAsync(lifetime.Token);
        Check(source.State.OutputId == speaker && defaultWatches == 0 && !mirrorWin.Disposed
            && multi.GetOutputs().SequenceEqual([speaker, "win-extra"]),
            "switching primary to phone preserves the Windows mirror and stops default-output watch");
        await source.PhoneRemovedAsync(device).WaitAsync(lifetime.Token);
        var promotedWin = windowsPlayers[^1];
        Check(source.State.State == CaptureState.Receiving && source.State.OutputId == "win-extra"
            && multi.GetOutputs().SequenceEqual(["win-extra"]) && !promotedWin.Disposed && mirrorWin.Disposed,
            "removing a primary phone promotes the surviving Windows mirror without ending AirPlay");
        await multi.SetOutputsAsync(["win-extra", speaker]).WaitAsync(lifetime.Token);
        await source.SetOutputAsync(speaker).WaitAsync(lifetime.Token);
        Check(promotedWin.Disposed && windowsPlayers[^1].Disposed,
            "rapid multi-output add/remove retires unused mirrors cleanly");

        Volatile.Write(ref expectFallback, true);
        server.Disconnect();
        await restored.Task.WaitAsync(lifetime.Token);
        Check(source.State.State == CaptureState.Receiving && source.State.OutputId == null && errors == 0 && defaultWatches == 1,
            "loss of an active Android speaker falls back to Windows without ending AirPlay");
        await source.DisposeAsync();
        Check(source.Pcm == null && windowsPlayers.All(player => player.Disposed) && defaultWatches == 0,
            "closing the source retires the final player and its default-output subscription");
        await File.WriteAllTextAsync(Path.Combine(output, "airplay-phone-output.json"),
            JsonSerializer.Serialize(new { Assertions = _checks, RealDevices = false }));
    }

    private sealed class AirPlayWatch(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    // Pulls from the actual PCM queue exactly as the player would. No device is opened.
    private sealed class SimulatedOutput(AndroidPcmSource pcm) : IPcmPlayer
    {
        private CancellationTokenSource? _stop;
        private Task? _consume;
        internal AndroidPcmSource Pcm => pcm;
        internal bool Disposed { get; private set; }
        public void Play()
        {
            if (_stop != null) return;
            _stop = new();
            var token = _stop.Token;
            _consume = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
                byte[] bytes = new byte[480 * 2 * sizeof(float)];
                while (await timer.WaitForNextTickAsync(token)) pcm.Read(bytes, 0, bytes.Length);
            });
        }
        public void Stop()
        {
            if (_stop == null) return;
            _stop.Cancel();
            try { _consume?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            _stop.Dispose(); _stop = null; _consume = null;
        }
        public void Dispose() { Stop(); Disposed = true; }
    }
}

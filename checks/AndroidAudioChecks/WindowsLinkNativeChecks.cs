using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SonicRoute.AndroidAudio;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task WindowsLinkNativeChecksAsync(string output, bool audioOnly)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("This check requires a new output directory.");
        Directory.CreateDirectory(output);
        string fixturePath = Path.Combine(output, "fixture");
        Directory.CreateDirectory(fixturePath);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("--windows-link-window-fixture");
        start.ArgumentList.Add(fixturePath);
        using var fixture = Process.Start(start)!;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            string handlePath = Path.Combine(fixturePath, "window.txt");
            await WaitUntilAsync(() => File.Exists(handlePath) || fixture.HasExited, limit.Token);
            if (fixture.HasExited) throw new IOException("The Win32 capture fixture did not start.");
            nint hwnd = (nint)long.Parse(await File.ReadAllTextAsync(handlePath, limit.Token), CultureInfo.InvariantCulture);
            Check(WindowsLinkService.GetWindows().Any(window => window.Handle == hwnd && window.ProcessId == fixture.Id),
                "Windows Link enumerates the known user-mode capture fixture window");
            string fixtureName = fixture.ProcessName;
            var remoteApp = new LinkRemoteApp(fixtureName, "Silent fixture A", fixtureName, fixture.Id, true, true, hwnd, null, null);
            NativeSilenceFixture? silence = null;
            bool refuseOutput = false;
            await using var b = new WindowsLinkService(Path.Combine(output, "b"), "Fixture B", port: 0, discovery: false,
                audioPlayerFactory: (pcm, endpoint, _) => refuseOutput ? throw new IOException("Fixture playback failed.")
                    : new WasapiPcmPlayer(pcm, endpoint));
            await using var a = new WindowsLinkService(Path.Combine(output, "a"), "Fixture A", port: 0, discovery: false,
                localCatalogProvider: (_, _) => Task.FromResult(new[] { remoteApp }),
                acquireLocalSilence: pid =>
                {
                    if (pid != fixture.Id || !b.IncomingAudioSources.Any(s => s.Session.State.State == CaptureState.Receiving))
                        throw new InvalidOperationException("Silence must follow receiver confirmation.");
                    return silence = new NativeSilenceFixture();
                });
            a.PairingRequested += p => a.AcceptPairing(p.Id, true);
            b.PairingRequested += p => b.AcceptPairing(p.Id, true);
            await a.EnableAsync(limit.Token);
            await b.EnableAsync(limit.Token);
            await a.ConnectAsync(b.LocalAddress, token: limit.Token);
            await WaitUntilAsync(() => a.Peers.Any(p => p.Connected) && b.Peers.Any(p => p.Connected), limit.Token);
            string peerA = b.Peers.Single(p => p.Connected).Id;
            string peerB = a.Peers.Single(p => p.Connected).Id;
            int initialRenderers = D3D11VideoSurfaceRenderer.ActiveInstanceCount;
            var sendAudio = Task.WhenAll(
                b.RequestPullAudioAsync(peerA, remoteApp, limit.Token),
                b.SendAudioAsync(peerA, fixture.Id, "Silent fixture B", limit.Token));
            if (audioOnly)
            {
                Guid[] streams = await sendAudio;
                Check(a.IncomingAudioSources.Any(s => s.StreamId == streams[1] && s.Session.State.State == CaptureState.Receiving)
                    && b.IncomingAudioSources.Any(s => s.StreamId == streams[0] && s.Session.State.State == CaptureState.Receiving),
                    "both probed senders complete push and pull with native PCM capture and confirmed WASAPI playback");
                Guid exclusiveId = await b.RequestPullAudioAsync(peerA, remoteApp, limit.Token, destinationOnly: true);
                Check(silence is { Disposed: false } && b.Streams.Any(s => s.Id == exclusiveId && s.Incoming && s.DestinationOnly),
                    "the destination-only drop switches the stream after receiver confirmation");
                await Task.WhenAll(b.StopStreamAsync(exclusiveId), b.StopStreamAsync(streams[1]));
                await WaitUntilAsync(() => a.Streams.Length == 0 && b.Streams.Length == 0 && silence is { Disposed: true }, limit.Token);
                Check(true, "returning both streams releases the source route and both receivers");
                return;
            }
            var startAt = Stopwatch.GetTimestamp();
            Guid video = await a.ShareWindowAsync(peerB, hwnd, "Windows Link fixture", false, limit.Token);
            var openedAt = Stopwatch.GetElapsedTime(startAt);
            await WaitUntilAsync(() => D3D11VideoSurfaceRenderer.ActiveInstanceCount > initialRenderers
                && a.IncomingAudioSources.Any(s => s.Session.State.State == CaptureState.Receiving)
                && b.IncomingAudioSources.Any(s => s.Session.State.State == CaptureState.Receiving), limit.Token);
            var rendererAt = Stopwatch.GetElapsedTime(startAt);
            Guid[] audio = await sendAudio;
            Check(b.Streams.Any(s => s.Id == video && s.Incoming && s.Kind == LinkMediaKind.Window),
                "the WGC and Media Foundation window stream arrives through TLS and creates a D3D viewer");

            using var process = Process.GetCurrentProcess();
            TimeSpan cpu = process.TotalProcessorTime;
            var initialA = a.ReadTraffic(peerB);
            var initialB = b.ReadTraffic(peerA);
            var active = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromSeconds(4), limit.Token);
            var elapsed = Stopwatch.GetElapsedTime(active);
            process.Refresh();
            var finalA = a.ReadTraffic(peerB);
            var finalB = b.ReadTraffic(peerA);
            long bytesA = finalA.Sent - initialA.Sent;
            long bytesB = finalB.Sent - initialB.Sent;
            Check(bytesA > 32_000 && bytesB > 32_000 && fixture.HasExited == false,
                "both native audio directions deliver PCM and start WASAPI playback during video sharing");
            var data = new
            {
                Kind = "Windows Graphics Capture and silent WASAPI application PCM over IPv4 loopback",
                SenderWindow = "640x360", SampleSeconds = elapsed.TotalSeconds,
                OpenedMs = openedAt.TotalMilliseconds, ReceiverCreatedMs = rendererAt.TotalMilliseconds,
                ReferenceClock = "one process; viewer creation only",
                LinkABytes = bytesA, LinkBBytes = bytesB,
                LinkAMbps = bytesA * 8 / elapsed.TotalSeconds / 1_000_000,
                LinkBMbps = bytesB * 8 / elapsed.TotalSeconds / 1_000_000,
                CpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                PrivateBytes = process.PrivateMemorySize64, WorkingSetBytes = process.WorkingSet64,
                TestProcessId = process.Id, PhysicalPeers = false
            };
            var json = JsonSerializer.Serialize(data);
            Console.WriteLine(json);
            await File.WriteAllTextAsync(Path.Combine(output, "native-load.json"), json, limit.Token);
            Guid exclusive = await b.RequestPullAudioAsync(peerA, remoteApp, limit.Token, destinationOnly: true);
            Check(exclusive != audio[0] && silence is { Disposed: false }
                && a.Streams.Any(s => s.Id == exclusive && s.DestinationOnly)
                && b.Streams.Any(s => s.Id == video), "exclusive audio waits for receiver confirmation and leaves window sharing intact");
            await b.StopPeerAppStreamsAsync(peerA, remoteApp.Key);
            await WaitUntilAsync(() => silence is { Disposed: true }, limit.Token);
            Check(b.Streams.Any(s => s.Id == video), "returning app audio restores the source route and leaves the shared window open");
            var previousSilence = silence;
            refuseOutput = true;
            bool rejected = false;
            try { await a.SendAudioAsync(peerB, fixture.Id, "Refused playback", limit.Token, destinationOnly: true); }
            catch (IOException ex) when (ex.Message.Contains("Fixture playback failed", StringComparison.Ordinal)) { rejected = true; }
            Check(rejected && ReferenceEquals(previousSilence, silence), "receiver playback failure reaches the sender before any source route change");
            await Task.WhenAll(b.StopStreamAsync(video), a.StopStreamAsync(audio[0]), b.StopStreamAsync(audio[1]));
            await WaitUntilAsync(() => a.Streams.Length == 0 && b.Streams.Length == 0
                && D3D11VideoSurfaceRenderer.ActiveInstanceCount == initialRenderers, limit.Token);
            Check(true, "stopping video and the two audio streams releases the receiver and all tracks");
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(fixturePath, "stop"), "");
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await fixture.WaitForExitAsync(exit.Token);
        }
    }

    private sealed class NativeSilenceFixture : IPcProcessSilenceLease
    {
        private int _disposed;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public void Release(bool restorePreviousEndpoint) => Dispose();
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private static async Task WindowsLinkWindowFixtureAsync(string directory)
    {
        const int width = 640, height = 360;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var window = new NativeVideoWindow("Windows Link owned capture fixture", true, false, null,
            20, 60, width, height, acceptsGamepad: false);
        nint handle = await window.CreateAsync(deadline.Token);
        using var renderer = new D3D11VideoSurfaceRenderer(handle, width, height, isMirrorMode: true);
        using var player = new WasapiPcmPlayer(new AndroidPcmSource { SmartVolume = false, Volume = 0 }, null);
        player.Play();
        byte[] pixels = new byte[width * height * 3 / 2];
        pixels.AsSpan(width * height).Fill(128);
        window.EnsureVisible();
        string pendingHandle = Path.Combine(directory, "window.tmp");
        await File.WriteAllTextAsync(pendingHandle, handle.ToInt64().ToString(CultureInfo.InvariantCulture));
        File.Move(pendingHandle, Path.Combine(directory, "window.txt"));
        using var frameTick = new PeriodicTimer(TimeSpan.FromMilliseconds(67));
        for (int i = 0; !File.Exists(Path.Combine(directory, "stop"))
            && await frameTick.WaitForNextTickAsync(deadline.Token); i++)
        {
            for (int y = 0; y < height; y++)
                pixels.AsSpan(y * width, width).Fill((byte)(24 + ((y + i * 8) % 200)));
            renderer.PresentSoftwareNv12(pixels, width, height, 0, 0, width, height, default);
        }
    }
}

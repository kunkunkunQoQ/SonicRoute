using System.Text.Json;
using SonicRoute.AndroidAudio;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static partial class WindowInputChecks
    {
        private static readonly byte[] WindowCatalogIcon = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAGklEQVR4nGOQnXLgPyWYYdSAUQNGDRguBgAAZT5wH+buOfUAAAAASUVORK5CYII=");

        // Only the fixture renders these samples. The receiver drains PCM in memory to avoid a loopback echo.
        private static async Task PlayWindowFixtureToneAsync()
        {
            var pcm = new AndroidPcmSource { SmartVolume = false, Volume = 1 };
            using var player = new WasapiPcmPlayer(pcm, null);
            player.Play();
            float[] block = new float[4800 * 2];
            for (int i = 0; i < block.Length / 2; i++)
            {
                float envelope = Math.Min(1f, Math.Min(i, 4799 - i) / 240f);
                block[2 * i] = block[2 * i + 1] = 0.025f * envelope * (float)Math.Sin(2 * Math.PI * 480 * i / 48000);
            }
            for (int i = 0; i < 7; i++)
            {
                pcm.Write(block);
                await Task.Delay(100);
            }
            await Task.Delay(250);
        }

        private sealed record FixturePeer(string Address, int Pid, string Process, long Started, FixtureWindows Windows, string Signal);

        internal static async Task WindowAudioSenderAsync(string root)
        {
            using var fixture = StartMonitorFixture(renderVideo: true);
            string prefix = "SonicRoute-Companion-" + Guid.NewGuid().ToString("N");
            using var tone = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "-tone");
            using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-stop");
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var token = limit.Token;
            nint first = 0;
            try
            {
                var handles = JsonSerializer.Deserialize<FixtureWindows>(
                    await fixture.StandardOutput.ReadLineAsync(token) ?? throw new IOException("The remote-window fixture exited."))!;
                first = (nint)handles.First;
                var owner = LinkWindowCapture.ReadProcess(fixture.Id);
                var process = new LinkRemoteApp(owner.Name!, "Companion audio fixture", owner.Name!, fixture.Id, false, false, 0, null, null)
                    { HasAudioSession = true, InstanceId = LinkRemoteApp.AudioInstanceId(fixture.Id, owner.StartTicks) };
                await using var sender = new WindowsLinkService(Path.Combine(root, "sender"), "Fixture windows", port: 0, discovery: false)
                {
                    CanShareWindow = w => w.ProcessId == fixture.Id && (w.Handle == handles.First || w.Handle == handles.Second),
                    ScopedCatalogProvider = (scope, icons, _) => Task.FromResult(scope == LinkCatalogScope.Audio ? new[] { process } :
                        new[] { handles.First, handles.Second }.Select(id => LinkWindowCapture.ReadWindow((nint)id))
                            .OfType<LinkWindow>().Where(w => w.ProcessId == fixture.Id).Select(w =>
                            new LinkRemoteApp(owner.Name!, w.Name, owner.Name!, fixture.Id, false, true, w.Handle, w.Name, icons ? WindowCatalogIcon : null)
                                { InstanceId = w.InstanceId, HasAudioSession = false }).ToArray())
                };
                sender.PairingRequested += request => sender.AcceptPairing(request.Id, true);
                sender.Error += error => Console.Error.WriteLine($"Source stream: {error}");
                await sender.EnableAsync(token);
                var descriptor = new FixturePeer(sender.LocalAddress, fixture.Id, owner.Name!, owner.StartTicks, handles, prefix);
                await File.WriteAllTextAsync(Path.Combine(root, "source.tmp"), JsonSerializer.Serialize(descriptor), token);
                File.Move(Path.Combine(root, "source.tmp"), Path.Combine(root, "source.json"));
                while (WaitHandle.WaitAny([tone, stop, token.WaitHandle]) == 0)
                    if (!PostMessageW(first, ToneMessage, 0, 0)) throw new IOException("The test tone request failed.");
                token.ThrowIfCancellationRequested();
                await WaitUntilAsync(() => sender.Streams.Length == 0, token);
                Check(true, "window sender retired all outgoing tracks");
            }
            finally
            {
                if (first != 0) PostMessageW(first, StopMessage, 0, 0);
                if (!fixture.WaitForExit(3000)) fixture.Kill();
            }
        }

        internal static async Task WindowAudioAsync(string root)
        {
            RequirePrivateDesktop();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(38));
            var token = limit.Token;
            var fixture = JsonSerializer.Deserialize<FixturePeer>(await File.ReadAllTextAsync(Path.Combine(root, "source.json"), token))!;
            using var tone = EventWaitHandle.OpenExisting(fixture.Signal + "-tone");
            using var stop = EventWaitHandle.OpenExisting(fixture.Signal + "-stop");
            try
            {
                var process = new LinkRemoteApp(fixture.Process, "Companion audio fixture", fixture.Process, fixture.Pid, false, false, 0, null, null)
                    { HasAudioSession = true, InstanceId = LinkRemoteApp.AudioInstanceId(fixture.Pid, fixture.Started) };
                await using var receiver = new WindowsLinkService(Path.Combine(root, "receiver"), "Viewer", port: 0, discovery: false,
                    audioPlayerFactory: (pcm, _, _) => new SimulatedOutput(pcm));
                receiver.PairingRequested += request => receiver.AcceptPairing(request.Id, true);
                receiver.Error += error => Console.WriteLine($"Receiver stream: {error}");
                await receiver.EnableAsync(token);
                await receiver.ConnectAsync(fixture.Address, token: token);
                await WaitUntilAsync(() => receiver.Peers.Any(p => p.Connected), token);
                var peer = receiver.Peers.Single(p => p.Connected);
                Check(peer.Capabilities.HasFlag(LinkCapabilities.WindowAudio), "the real source advertises window companion audio");
                var catalog = await receiver.RequestCatalogAsync(peer.Id, true, token, windowsOnly: true);
                var windowA = catalog.Single(w => w.WindowHandle == fixture.Windows.First);
                var windowB = catalog.Single(w => w.WindowHandle == fixture.Windows.Second);
                async Task<Guid> Open(LinkRemoteApp app)
                {
                    var (video, available) = await receiver.OpenWindowWithAudioAsync(peer.Id, app, 80, 80, token);
                    Check(available, app.IsMonitor ? "the display viewer negotiated companion audio" :
                        $"the {app.WindowTitle} viewer negotiated companion audio");
                    return video;
                }
                Task Settle(int videos, int audios) => WaitUntilAsync(
                    () => receiver.Streams.Count(s => s.Kind == LinkMediaKind.Window) == videos
                        && receiver.IncomingAudioSources.Length == audios && receiver.Streams.Length == videos + audios, token);
                Guid[] videos = await Task.WhenAll(Open(windowA), Open(windowB));
                await Settle(2, 1);
                var audio = receiver.IncomingAudioSources.Single();
                Check(videos[0] != videos[1] && receiver.Streams.Any(s => s.Id == audio.StreamId && s.Kind == LinkMediaKind.AppAudio),
                    "two different captured windows from the same application share one actual process-audio stream");
                tone.Set();
                await WaitUntilAsync(() => audio.Session.Pcm is { NonSilentSamplesWritten: > 2000, SamplesConsumed: > 0 }, token);
                Check(true, "the quiet tone from the captured application arrived as non-silent PCM at the receiver");
                async Task<nint> VisibleViewer(Guid stream)
                {
                    string title = receiver.Streams.Single(s => s.Id == stream).Name;
                    nint Handle() => FindMonitorViewerWindow("SonicRoute_VirtualDisplay_WindowClass", title);
                    try { await WaitUntilAsync(() => IsMonitorViewerVisible(Handle()), TimeSpan.FromSeconds(10)); }
                    catch (TimeoutException ex)
                    {
                        throw new TimeoutException($"No visible viewer for '{title}', HWND={Handle()}, stream active={receiver.Streams.Any(s => s.Id == stream)}.", ex);
                    }
                    return Handle();
                }
                var viewers = await Task.WhenAll(videos.Select(VisibleViewer));
                Check(viewers[0] != viewers[1], "both independently captured windows become visible after their first frame, even when the receiver starts hidden");
                await receiver.StopStreamAsync(videos[0]);
                await Settle(1, 1);
                Check(receiver.IncomingAudioSources.Single().StreamId == audio.StreamId,
                    "closing one application viewer keeps its sibling's audio stream");
                nint viewer = viewers[1];
                Check(windowB.IconPng?.SequenceEqual(WindowCatalogIcon) == true
                    && receiver.Streams.Single(s => s.Id == videos[1]).IconPng?.SequenceEqual(WindowCatalogIcon) == true
                    && SendMessageW(viewer, 0x007f /* WM_GETICON */, 0, 0) != 0
                    && SendMessageW(viewer, 0x007f, 1, 0) != 0,
                    "the paired viewer carries its remote app icon from the catalog into the real title bar and taskbar");
                Console.WriteLine($"Private viewer HWND {viewer}, visible={IsMonitorViewerVisible(viewer)}, window thread={GetMonitorViewerProcessId(viewer, out var owner)}, owning test PID match={owner == Environment.ProcessId}.");
                if (GetMonitorViewerProcessId(viewer, out uint pid) == 0 || pid != Environment.ProcessId
                    || !PostMessageW(viewer, 0x0010, 0, 0)) throw new IOException("Could not close our private test viewer.");
                Console.WriteLine("The test delivered WM_CLOSE to the presentation HWND.");
                await Settle(0, 0);
                Check(true, "closing the last application viewer with its native close action removes its automatic mixer row");

                Guid manual = await receiver.RequestPullAudioAsync(peer.Id, process, token);
                Guid appViewer = await Open(windowA);
                await Settle(1, 1);
                await receiver.StopStreamAsync(appViewer);
                await Settle(0, 1);
                Check(receiver.IncomingAudioSources.Single().StreamId == manual, "a manual app stream opened before the viewer survives its closure");
                await receiver.StopStreamAsync(manual);
                await Settle(0, 0);
                appViewer = await Open(windowB);
                Guid automatic = receiver.IncomingAudioSources.Single().StreamId;
                Guid adopted = await receiver.RequestPullAudioAsync(peer.Id, process, token);
                await receiver.StopStreamAsync(appViewer);
                await Settle(0, 1);
                Check(adopted == automatic && receiver.IncomingAudioSources.Single().StreamId == adopted,
                    "an explicit app audio pull adopts an automatic stream and survives the last window closing");
                await receiver.StopStreamAsync(adopted);
                await Settle(0, 0);

                // The monitor sender belongs to the same private desktop as its viewer.
                await using var monitorSender = new WindowsLinkService(Path.Combine(root, "monitor-sender"), "Private desktop", 0, discovery: false,
                    localCatalogProvider: (_, _) => Task.FromResult(new[] { process }));
                monitorSender.PairingRequested += request => monitorSender.AcceptPairing(request.Id, true);
                await monitorSender.EnableAsync(token);
                await receiver.ConnectAsync(monitorSender.LocalAddress, token: token);
                await WaitUntilAsync(() => receiver.Peers.Count(p => p.Connected) == 2, token);
                peer = receiver.Peers.Single(p => p.Connected && p.Name == "Private desktop");
                var displays = await receiver.RequestCatalogAsync(peer.Id, false, token, windowsOnly: true);
                var monitor = displays.First(w => w.Display?.IsPrimary == true);
                manual = await receiver.RequestPullAudioAsync(peer.Id, process, token);
                Guid screen = await Open(monitor);
                await Settle(1, 2);
                Check(receiver.Streams.Any(s => s.Incoming && s.Kind == LinkMediaKind.SystemAudio)
                    && receiver.Streams.Any(s => s.Id == manual && s.Kind == LinkMediaKind.AppAudio),
                    "sharing a monitor adds the system mix alongside an existing manual app stream");
                await receiver.StopStreamAsync(screen);
                await Settle(0, 1);
                Check(receiver.IncomingAudioSources.Single().StreamId == manual,
                    "closing the monitor removes only its automatic system mix");
                await receiver.StopStreamAsync(manual);
                await Settle(0, 0);

                Guid manualSystem = await receiver.RequestPullAudioAsync(peer.Id, LinkRemoteApp.SystemAudio(peer.DisplayName), token);
                screen = await Open(monitor);
                await Settle(1, 1);
                await receiver.StopStreamAsync(screen);
                await Settle(0, 1);
                Check(receiver.IncomingAudioSources.Single().StreamId == manualSystem
                    && receiver.Streams.Single().Kind == LinkMediaKind.SystemAudio,
                    "a manually pulled system mix opened before its monitor survives closing the viewer");
                await receiver.StopStreamAsync(manualSystem);
                await Settle(0, 0);
                await WaitUntilAsync(() => monitorSender.Streams.Length == 0, token);
            }
            finally { stop.Set(); }
        }
    }
}

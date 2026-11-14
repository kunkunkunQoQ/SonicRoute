using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task CheckAdbSocketClientAsync()
    {
        await using var server = new FakeAdbServer();
        var client = new AdbSocketClient(server.EndPoint);

        Check(await AdbSocketClient.IsServerReachableAsync(server.EndPoint), "socket probe detects live 5037 server");
        using (var probeStop = new TcpListener(IPAddress.Loopback, 0))
        {
            probeStop.Start();
            var unusedEp = (IPEndPoint)probeStop.LocalEndpoint;
            probeStop.Stop();
            Check(!await AdbSocketClient.IsServerReachableAsync(unusedEp), "socket probe returns false when 5037 port is closed");
        }

        string ver = await client.VersionAsync(default);
        Check(ver.Contains("41", StringComparison.Ordinal), "host:version decodes server protocol version");

        // track-devices-l push updates without polling
        var snapshots = new List<IReadOnlyList<AndroidDevice>>();
        using (var trackCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var firstSnap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondSnap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task tracking = client.TrackDevicesAsync(list =>
            {
                snapshots.Add(list);
                if (snapshots.Count == 1) firstSnap.TrySetResult();
                else if (snapshots.Count >= 2) secondSnap.TrySetResult();
            }, trackCts.Token);
            await firstSnap.Task.WaitAsync(TimeSpan.FromSeconds(2));
            server.EnqueueTrackUpdate("fake-usb-1\tdevice model:Pixel_Test transport_id:7\n10.0.0.5:5555\tdevice model:Wifi_Test transport_id:8\n");
            await secondSnap.Task.WaitAsync(TimeSpan.FromSeconds(2));
            trackCts.Cancel();
            bool canceled = false;
            try { await tracking; } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && snapshots.Count == 2 && snapshots[1].Count == 2 && snapshots[1][1].IsWifi,
                "host:track-devices-l receives push snapshots and exits cleanly on cancellation");
        }

        // Pair / Connect / Disconnect / Mdns / SerialNo / Forward
        Check(await client.PairAsync("192.168.1.20:37000", "123456", default) == "Successfully paired to 192.168.1.20:37000 [guid=adb-abc]",
            "host:pair uses password-first wire order and reads two-stage OKAY response");
        Check(server.Requests.Contains("host:pair:123456:192.168.1.20:37000"), "host:pair exact wire string verified");

        server.FailNextPair = "Wrong pairing code";
        bool pairFailed = false;
        try { await client.PairAsync("192.168.1.20:37000", "000000", default); } catch (IOException ex) { pairFailed = ex.Message.Contains("Wrong pairing code"); }
        Check(pairFailed, "host:pair propagates second-stage FAIL message");

        Check((await client.ConnectAsync("192.168.1.20:41234", default)).Contains("connected to 192.168.1.20:41234"),
            "host:connect returns daemon message");
        Check(await client.DisconnectAsync("192.168.1.20:41234", default) == "disconnected 192.168.1.20:41234",
            "host:disconnect returns daemon message");
        Check((await client.MdnsServicesAsync(default)).Count == 1, "host:mdns:services parses services");
        Check(await client.GetSerialNoAsync("fake-usb-1", default) == "HW-FAKE-01", "host-serial get-serialno trims result");

        int fwdPort = await client.ForwardTcpToLocalAbstractAsync("fake-usb-1", "test_sock", default);
        Check(fwdPort == 44123, "forward returns dynamic port across double OKAY framing");
        Check((await client.ListForwardsAsync(default)).Single().Remote == "localabstract:test_sock", "host:list-forward parses rules");
        var spaceForwards = AdbSocketClient.ParseForwards("adb-e1868f06-7gMgqD (2)._adb-tls-connect._tcp tcp:44123 localabstract:sonicroute_out\n");
        Check(spaceForwards.Count == 1
            && spaceForwards[0].Serial == "adb-e1868f06-7gMgqD (2)._adb-tls-connect._tcp"
            && spaceForwards[0].Local == "tcp:44123"
            && spaceForwards[0].Remote == "localabstract:sonicroute_out",
            "host:list-forward preserves spaces in mDNS collision serials");
        await client.RemoveForwardAsync("fake-usb-1", "tcp:44123", default);

        // sync push >64 KiB chunking + shell v2 stdout/stderr/exit + shell v1 fallback
        string tempDir = Path.Combine(Path.GetTempPath(), "sr-adb-sock-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            byte[] payload = Enumerable.Range(0, 70_000).Select(i => (byte)(i & 0xFF)).ToArray();
            string localFile = Path.Combine(tempDir, "payload.bin");
            await File.WriteAllBytesAsync(localFile, payload);
            await client.PushAsync("fake-usb-1", localFile, "/data/local/tmp/payload.bin", default);
            Check(server.PushedFiles.TryGetValue("/data/local/tmp/payload.bin", out var pushed)
                && pushed.Mode == 33188 && pushed.Data.SequenceEqual(payload),
                "sync: push streams >64KiB payload across DATA chunks with 33188 mode");

            var shellRes = await client.RunShellAsync("fake-usb-1", "echo_both_and_fail", default);
            Check(shellRes.ExitCode == 42 && shellRes.Output == "out-line\n" && shellRes.Error == "err-line\n",
                "shell,v2,raw captures separate stdout, stderr and exit code");

            var v1Res = await client.RunShellAsync("legacy-v1", "getprop ro.serialno", default);
            Check(v1Res.ExitCode == 0 && v1Res.Output == "LEGACY-01\n",
                "shell: v1 fallback activates when shell,v2,raw is unsupported");

            // Decoupled AdbClient helper catalog + outputs + clipboard + media + pcm output (NO scrcpy-server file present!)
            File.WriteAllText(Path.Combine(tempDir, "sonicroute-catalog.jar"), "FAKE-JAR-CONTENT");
            var adb = new AdbClient(tempDir, client);
            Check(!File.Exists(Path.Combine(tempDir, "scrcpy-server")) && !File.Exists(Path.Combine(adb.RuntimeDirectory, "adb.exe")), "test directory contains zero scrcpy-server or adb.exe");

            var devs = await adb.DevicesAsync(default);
            Check(devs.Count == 1 && devs[0].HardwareId == "HW-FAKE-01", "AdbClient.DevicesAsync enriches hardware id via IAdbTransport");

            var dev = devs[0];
            var apps = await adb.AppsAsync(dev, default);
            Check(apps.Count == 1 && apps[0].Package == "com.example.music", "AppsAsync executes using only sonicroute-catalog.jar");
            var outs = await adb.OutputsAsync(dev, default);
            Check(outs.Count == 2 && outs[1].Kind == AndroidOutputSinkKind.Bluetooth, "OutputsAsync executes using only sonicroute-catalog.jar");
            Check(!server.Commands.Any(c => c.Contains(".server", StringComparison.Ordinal)),
                "AppsAsync and OutputsAsync never reference scrcpy-server in CLASSPATH");

            // MediaSessionClient duplex over shell,v2 stdin/stdout (verify NO UTF-8 BOM!)
            using (var mediaCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var mediaClient = new AndroidMediaSessionClient(adb);
                var gotState = new TaskCompletionSource<AndroidMediaState>(TaskCreationOptions.RunContinuationsAsynchronously);
                var mediaTask = mediaClient.WatchAsync(dev, s => gotState.TrySetResult(s), mediaCts.Token);
                var state = await gotState.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check(state.Title == "Song", "MediaSessionClient parses initial JSON state");
                await mediaClient.SendAsync("toggle", mediaCts.Token);
                await server.WaitForShellInputAsync("toggle\n", TimeSpan.FromSeconds(2));
                Check(!server.ObservedStdinBom, "MediaSessionClient writes UTF-8 without BOM and LF endings");
                mediaCts.Cancel();
                try { await mediaTask; } catch (OperationCanceledException) { }
            }

            // ClipboardClient duplex over shell,v2 stdin/stdout
            using (var clipCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var clipClient = new AndroidClipboardClient(adb);
                var clipReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var clipChanged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var clipTask = clipClient.WatchAsync(dev, () => clipReady.TrySetResult(), t => clipChanged.TrySetResult(t), clipCts.Token);
                await clipReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check((await clipChanged.Task.WaitAsync(TimeSpan.FromSeconds(2))) == "hello-from-phone",
                    "ClipboardClient receives ready and clipboard text without scrcpy-server");
                await clipClient.SendAsync("hello-from-pc", 5, clipCts.Token);
                await server.WaitForShellInputAsync("\"hello-from-pc\"", TimeSpan.FromSeconds(2));
                Check(!server.ObservedStdinBom, "ClipboardClient writes UTF-8 without BOM");
                clipCts.Cancel();
                try { await clipTask; } catch (OperationCanceledException) { }
            }

            // AudioOutputBridge direct localabstract PCM stream (no adb forward needed!)
            int forwardsBefore = server.Requests.Count(r => r.Contains(":forward:", StringComparison.Ordinal));
            using (var outCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var connType = typeof(AndroidAudioSession).Assembly.GetType("SonicRoute.AndroidAudio.AndroidAudioOutputConnection")!;
                var openTask = (Task)connType.GetMethod("OpenAsync")!.Invoke(null, [adb, dev, "speaker", outCts.Token, null, null])!;
                await openTask;
                await using var conn = (IAsyncDisposable)openTask.GetType().GetProperty("Result")!.GetValue(openTask)!;
                var stream = (Stream)connType.GetProperty("Stream")!.GetValue(conn)!;
                byte[] pcmFrame = [1, 2, 3, 4, 5, 6, 7, 8];
                await stream.WriteAsync(pcmFrame, outCts.Token);
                await stream.FlushAsync(outCts.Token);
                byte[] arrived = await server.WaitForLocalAbstractBytesAsync(pcmFrame.Length, TimeSpan.FromSeconds(2));
                Check(arrived.SequenceEqual(pcmFrame), "AudioOutputBridge streams PCM directly over localabstract without adb forward");
                await ((Task)connType.GetMethod("WaitForRoutingAsync")!.Invoke(conn, [outCts.Token])!);
                Check((int)connType.GetProperty("RoutedType")!.GetValue(conn)! == 2
                    && (int)connType.GetProperty("BufferFrames", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(conn)! == 7688
                    && (int)connType.GetProperty("StartThresholdFrames", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(conn)! == 960,
                    "AudioOutputBridge parses listening + nested routed metadata while ignoring preferredApplied:false");
                await ((Task)connType.GetMethod("SwitchTargetAsync")!.Invoke(conn, ["bt:aa", outCts.Token])!);
                Check((int)connType.GetProperty("RoutedType")!.GetValue(conn)! == 8,
                    "AudioOutputBridge SwitchTargetAsync switches to bluetooth sink");
                await ((Task)connType.GetMethod("SwitchTargetAsync")!.Invoke(conn, ["speaker", outCts.Token])!);
                Check((int)connType.GetProperty("RoutedType")!.GetValue(conn)! == 2,
                    "AudioOutputBridge SwitchTargetAsync switches back to phone speaker");
                connType.GetMethod("SetPaused")!.Invoke(conn, [true]);
                await server.WaitForShellInputAsync("mute 1\n", TimeSpan.FromSeconds(2));
            }
            Check(server.Requests.Count(r => r.Contains(":forward:", StringComparison.Ordinal)) == forwardsBefore,
                "AudioOutputConnection uses direct localabstract stream with zero host forward rules");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class FakeAdbServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly Queue<string> _trackUpdates = new();
        private readonly SemaphoreSlim _trackSignal = new(0);
        private readonly SemaphoreSlim _localAbstractSignal = new(0);
        private readonly List<byte> _stdinBytes = new();
        private readonly List<byte> _localAbstractBytes = new();
        private readonly object _gate = new();
        private string? _lastRemoteJar;

        public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;
        public ConcurrentQueue<string> Requests { get; } = new();
        public ConcurrentQueue<string> Commands { get; } = new();
        public ConcurrentDictionary<string, (int Mode, byte[] Data)> PushedFiles { get; } = new();
        public string? FailNextPair { get; set; }
        public bool ObservedStdinBom { get; private set; }

        public FakeAdbServer()
        {
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public void EnqueueTrackUpdate(string text)
        {
            lock (_trackUpdates) _trackUpdates.Enqueue(text);
            _trackSignal.Release();
        }

        public async Task WaitForShellInputAsync(string needle, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                string current;
                lock (_gate) current = Encoding.UTF8.GetString(_stdinBytes.ToArray());
                if (current.Contains(needle, StringComparison.Ordinal)) return;
                await Task.Delay(10);
            }
            throw new TimeoutException("Expected shell stdin substring not observed: " + needle);
        }

        public async Task<byte[]> WaitForLocalAbstractBytesAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_localAbstractBytes.Count >= count) return _localAbstractBytes.Take(count).ToArray();
                }
                await Task.Delay(10);
            }
            throw new TimeoutException("Expected localabstract PCM bytes not observed.");
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
                }
                catch (OperationCanceledException) { client?.Dispose(); break; }
                catch (ObjectDisposedException) { client?.Dispose(); break; }
                catch { client?.Dispose(); }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using var _ = client;
            client.NoDelay = true;
            var stream = client.GetStream();
            try
            {
                string req = await ReadSmartRequestAsync(stream, token).ConfigureAwait(false);
                Requests.Enqueue(req);

                if (req == "host:version")
                {
                    await SendOkayPayloadAsync(stream, "0029", token).ConfigureAwait(false);
                    return;
                }
                if (req == "host:devices-l")
                {
                    await SendOkayPayloadAsync(stream, "fake-usb-1\tdevice model:Fake_Phone transport_id:7\n", token).ConfigureAwait(false);
                    return;
                }
                if (req == "host:track-devices-l")
                {
                    await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
                    await SendLengthPayloadAsync(stream, "fake-usb-1\tdevice model:Fake_Phone transport_id:7\n", token).ConfigureAwait(false);
                    while (!token.IsCancellationRequested)
                    {
                        await _trackSignal.WaitAsync(token).ConfigureAwait(false);
                        string? next = null;
                        lock (_trackUpdates) if (_trackUpdates.Count > 0) next = _trackUpdates.Dequeue();
                        if (next != null) await SendLengthPayloadAsync(stream, next, token).ConfigureAwait(false);
                    }
                    return;
                }
                if (req.StartsWith("host:pair:", StringComparison.Ordinal))
                {
                    await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
                    if (FailNextPair != null)
                    {
                        string err = FailNextPair; FailNextPair = null;
                        await stream.WriteAsync("FAIL"u8.ToArray(), token).ConfigureAwait(false);
                        await SendLengthPayloadAsync(stream, err, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendLengthPayloadAsync(stream, "Successfully paired to 192.168.1.20:37000 [guid=adb-abc]", token).ConfigureAwait(false);
                    }
                    return;
                }
                if (req.StartsWith("host:connect:", StringComparison.Ordinal))
                {
                    await SendOkayPayloadAsync(stream, "connected to " + req["host:connect:".Length..], token).ConfigureAwait(false);
                    return;
                }
                if (req.StartsWith("host:disconnect:", StringComparison.Ordinal))
                {
                    await SendOkayPayloadAsync(stream, "disconnected " + req["host:disconnect:".Length..], token).ConfigureAwait(false);
                    return;
                }
                if (req == "host:mdns:services")
                {
                    await SendOkayPayloadAsync(stream, "adb-abc-123\t_adb-tls-connect._tcp\t192.168.1.20:41234\n", token).ConfigureAwait(false);
                    return;
                }
                if (req == "host-serial:fake-usb-1:get-serialno")
                {
                    await SendOkayPayloadAsync(stream, "HW-FAKE-01\n", token).ConfigureAwait(false);
                    return;
                }
                if (req.StartsWith("host-serial:fake-usb-1:forward:", StringComparison.Ordinal))
                {
                    await stream.WriteAsync("OKAYOKAY"u8.ToArray(), token).ConfigureAwait(false);
                    await SendLengthPayloadAsync(stream, "44123", token).ConfigureAwait(false);
                    return;
                }
                if (req == "host:list-forward")
                {
                    await SendOkayPayloadAsync(stream, "fake-usb-1 tcp:44123 localabstract:test_sock\n", token).ConfigureAwait(false);
                    return;
                }
                if (req.StartsWith("host-serial:fake-usb-1:killforward:", StringComparison.Ordinal))
                {
                    await stream.WriteAsync("OKAYOKAY"u8.ToArray(), token).ConfigureAwait(false);
                    return;
                }
                if (req.StartsWith("host:transport:", StringComparison.Ordinal))
                {
                    string serial = req["host:transport:".Length..];
                    await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
                    string service = await ReadSmartRequestAsync(stream, token).ConfigureAwait(false);
                    Commands.Enqueue(service);

                    if (service == "sync:")
                    {
                        await HandleSyncAsync(stream, token).ConfigureAwait(false);
                        return;
                    }
                    if (service.StartsWith("localabstract:", StringComparison.Ordinal))
                    {
                        await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
                        byte[] buf = new byte[4096];
                        int n;
                        while ((n = await stream.ReadAsync(buf, token).ConfigureAwait(false)) > 0)
                        {
                            lock (_gate) _localAbstractBytes.AddRange(buf.AsSpan(0, n).ToArray());
                            _localAbstractSignal.Release();
                        }
                        return;
                    }
                    if (serial == "legacy-v1" && service.StartsWith("shell,v2,", StringComparison.Ordinal))
                    {
                        await SendFailAsync(stream, "unknown command shell,v2", token).ConfigureAwait(false);
                        return;
                    }
                    if (serial == "legacy-v1" && service.StartsWith("shell:", StringComparison.Ordinal))
                    {
                        await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
                        await stream.WriteAsync("LEGACY-01\n"u8.ToArray(), token).ConfigureAwait(false);
                        return;
                    }
                    if (service.StartsWith("shell,v2,raw:", StringComparison.Ordinal))
                    {
                        string cmd = service["shell,v2,raw:".Length..];
                        await HandleShellV2Async(stream, cmd, token).ConfigureAwait(false);
                        return;
                    }
                }
                await SendFailAsync(stream, "unhandled fake request: " + req, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }

        private async Task HandleSyncAsync(NetworkStream stream, CancellationToken token)
        {
            await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
            byte[] hdr = new byte[8];
            string? path = null;
            int mode = 0;
            using var ms = new MemoryStream();
            while (true)
            {
                await ReadExactAsync(stream, hdr, token).ConfigureAwait(false);
                string tag = Encoding.ASCII.GetString(hdr, 0, 4);
                int len = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(4, 4));
                if (tag == "SEND")
                {
                    byte[] specBytes = new byte[len];
                    await ReadExactAsync(stream, specBytes, token).ConfigureAwait(false);
                    string spec = Encoding.UTF8.GetString(specBytes);
                    int comma = spec.LastIndexOf(',');
                    path = spec[..comma];
                    mode = int.Parse(spec[(comma + 1)..], CultureInfo.InvariantCulture);
                    _lastRemoteJar = path;
                }
                else if (tag == "DATA")
                {
                    byte[] chunk = new byte[len];
                    await ReadExactAsync(stream, chunk, token).ConfigureAwait(false);
                    ms.Write(chunk);
                }
                else if (tag == "DONE")
                {
                    PushedFiles[path!] = (mode, ms.ToArray());
                    byte[] okay = new byte[8];
                    "OKAY"u8.CopyTo(okay);
                    await stream.WriteAsync(okay, token).ConfigureAwait(false);
                }
                else if (tag == "QUIT")
                {
                    break;
                }
            }
        }

        private async Task HandleShellV2Async(NetworkStream stream, string cmd, CancellationToken token)
        {
            await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
            if (cmd == "getprop ro.serialno")
            {
                await SendV2PacketAsync(stream, 1, "HW-FAKE-01\n"u8.ToArray(), token).ConfigureAwait(false);
                await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
                return;
            }
            if (cmd == "echo_both_and_fail")
            {
                await SendV2PacketAsync(stream, 1, "out-line\n"u8.ToArray(), token).ConfigureAwait(false);
                await SendV2PacketAsync(stream, 2, "err-line\n"u8.ToArray(), token).ConfigureAwait(false);
                await SendV2PacketAsync(stream, 3, [42], token).ConfigureAwait(false);
                return;
            }
            if (cmd.Contains("org.sonicroute.android.Catalog", StringComparison.Ordinal))
            {
                await SendV2PacketAsync(stream, 1, "{\"package\":\"com.example.music\",\"name\":\"Music\",\"uid\":10123,\"category\":1}\n"u8.ToArray(), token).ConfigureAwait(false);
                await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
                return;
            }
            if (cmd.Contains("org.sonicroute.android.AudioOutputBridge list", StringComparison.Ordinal))
            {
                await SendV2PacketAsync(stream, 1, "{\"outputs\":[{\"key\":\"speaker\",\"kind\":\"speaker\",\"id\":1,\"type\":2,\"name\":\"Speaker\"},{\"key\":\"bt:aa\",\"kind\":\"bluetooth\",\"id\":5,\"type\":8,\"name\":\"Headphones\"}]}\n"u8.ToArray(), token).ConfigureAwait(false);
                await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
                return;
            }
            if (cmd.Contains("org.sonicroute.android.MediaSessionBridge", StringComparison.Ordinal))
            {
                await SendV2PacketAsync(stream, 1, "SR_MEDIA_PID:4567\n{\"package\":\"com.example.music\",\"title\":\"Song\",\"artist\":\"Artist\",\"playing\":true,\"actions\":512}\n"u8.ToArray(), token).ConfigureAwait(false);
                await PumpStdinPacketsAsync(stream, token).ConfigureAwait(false);
                await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
                return;
            }
            if (cmd.Contains("org.sonicroute.android.ClipboardBridge", StringComparison.Ordinal))
            {
                await SendV2PacketAsync(stream, 1, "SR_CLIPBOARD_PID:4568\n{\"ready\":true,\"revision\":5}\n{\"revision\":6,\"text\":\"hello-from-phone\"}\n"u8.ToArray(), token).ConfigureAwait(false);
                await PumpStdinPacketsAsync(stream, token).ConfigureAwait(false);
                await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
                return;
            }
            if (cmd.Contains("org.sonicroute.android.AudioOutputBridge play", StringComparison.Ordinal))
            {
                await SendV2PacketAsync(stream, 1,
                    "SR_PCOUT_PID:4569\n{\"listening\":true,\"outputs\":[{\"key\":\"speaker\",\"kind\":\"speaker\",\"id\":3,\"type\":2,\"name\":\"Speaker\",\"address\":\"\"},{\"key\":\"bt:aa\",\"kind\":\"bluetooth\",\"id\":5,\"type\":8,\"name\":\"Headphones\",\"address\":\"aa\"}]}\n"u8.ToArray(),
                    token).ConfigureAwait(false);
                await _localAbstractSignal.WaitAsync(token).ConfigureAwait(false);
                await SendV2PacketAsync(stream, 1,
                    "{\"routed\":{\"target\":\"speaker\",\"preferredApplied\":false,\"bufferFrames\":4096,\"startThresholdFrames\":480,\"id\":9,\"type\":9,\"name\":\"Wrong\",\"key\":\"other\"}}\n"u8.ToArray(),
                    token).ConfigureAwait(false);
                await SendV2PacketAsync(stream, 1,
                    "{\"routed\":{\"target\":\"speaker\",\"preferredApplied\":true,\"bufferFrames\":7688,\"startThresholdFrames\":960,\"id\":3,\"type\":2,\"name\":\"Speaker\",\"key\":\"speaker\"}}\n"u8.ToArray(),
                    token).ConfigureAwait(false);
                await PumpStdinPacketsAsync(stream, token, async line =>
                {
                    if (line == "target bt:aa")
                    {
                        await SendV2PacketAsync(stream, 1,
                            "{\"routed\":{\"target\":\"bt:aa\",\"preferredApplied\":true,\"bufferFrames\":3840,\"startThresholdFrames\":960,\"id\":5,\"type\":8,\"name\":\"Headphones\",\"key\":\"bt:aa\"}}\n"u8.ToArray(),
                            token).ConfigureAwait(false);
                    }
                    else if (line == "target speaker")
                    {
                        await SendV2PacketAsync(stream, 1,
                            "{\"routed\":{\"target\":\"speaker\",\"preferredApplied\":true,\"bufferFrames\":7688,\"startThresholdFrames\":960,\"id\":3,\"type\":2,\"name\":\"Speaker\",\"key\":\"speaker\"}}\n{\"switchSetup\":{\"policyMs\":12.3,\"preferredMs\":4.5,\"releaseMs\":1.2}}\n"u8.ToArray(),
                            token).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
                await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
                return;
            }
            await SendV2PacketAsync(stream, 3, [0], token).ConfigureAwait(false);
        }

        private async Task PumpStdinPacketsAsync(NetworkStream stream, CancellationToken token, Func<string, Task>? onLine = null)
        {
            byte[] hdr = new byte[5];
            var lineBuffer = new StringBuilder();
            while (!token.IsCancellationRequested)
            {
                int r = await ReadUpToAsync(stream, hdr, token).ConfigureAwait(false);
                if (r < 5) break;
                byte id = hdr[0];
                int len = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(1, 4));
                byte[] data = new byte[len];
                if (len > 0) await ReadExactAsync(stream, data, token).ConfigureAwait(false);
                if (id == 0 && len > 0)
                {
                    if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) ObservedStdinBom = true;
                    lock (_gate) _stdinBytes.AddRange(data);
                    if (onLine != null)
                    {
                        lineBuffer.Append(Encoding.UTF8.GetString(data));
                        while (true)
                        {
                            string text = lineBuffer.ToString();
                            int nl = text.IndexOf('\n');
                            if (nl < 0) break;
                            string line = text[..nl].TrimEnd('\r');
                            lineBuffer.Remove(0, nl + 1);
                            if (line == "quit") return;
                            await onLine(line).ConfigureAwait(false);
                        }
                    }
                }
                else if (id == 4)
                {
                    break;
                }
            }
        }

        private static async Task SendV2PacketAsync(NetworkStream stream, byte id, byte[] payload, CancellationToken token)
        {
            byte[] buf = new byte[5 + payload.Length];
            buf[0] = id;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(1, 4), payload.Length);
            payload.CopyTo(buf, 5);
            await stream.WriteAsync(buf, token).ConfigureAwait(false);
        }

        private static async Task<string> ReadSmartRequestAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] lenBuf = new byte[4];
            await ReadExactAsync(stream, lenBuf, token).ConfigureAwait(false);
            int len = int.Parse(Encoding.ASCII.GetString(lenBuf), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte[] payload = new byte[len];
            await ReadExactAsync(stream, payload, token).ConfigureAwait(false);
            return Encoding.UTF8.GetString(payload);
        }

        private static async Task SendOkayPayloadAsync(NetworkStream stream, string payload, CancellationToken token)
        {
            await stream.WriteAsync("OKAY"u8.ToArray(), token).ConfigureAwait(false);
            await SendLengthPayloadAsync(stream, payload, token).ConfigureAwait(false);
        }

        private static async Task SendFailAsync(NetworkStream stream, string message, CancellationToken token)
        {
            await stream.WriteAsync("FAIL"u8.ToArray(), token).ConfigureAwait(false);
            await SendLengthPayloadAsync(stream, message, token).ConfigureAwait(false);
        }

        private static async Task SendLengthPayloadAsync(NetworkStream stream, string payload, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            byte[] pkt = new byte[4 + bytes.Length];
            Encoding.ASCII.GetBytes(bytes.Length.ToString("x4", CultureInfo.InvariantCulture), pkt.AsSpan(0, 4));
            bytes.CopyTo(pkt, 4);
            await stream.WriteAsync(pkt, token).ConfigureAwait(false);
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buf, CancellationToken token)
        {
            int off = 0;
            while (off < buf.Length)
            {
                int n = await stream.ReadAsync(buf.AsMemory(off), token).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException();
                off += n;
            }
        }

        private static async Task<int> ReadUpToAsync(Stream stream, byte[] buf, CancellationToken token)
        {
            int off = 0;
            while (off < buf.Length)
            {
                int n = await stream.ReadAsync(buf.AsMemory(off), token).ConfigureAwait(false);
                if (n == 0) break;
                off += n;
            }
            return off;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
            _cts.Dispose();
            _trackSignal.Dispose();
        }
    }
}

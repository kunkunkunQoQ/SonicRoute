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

}

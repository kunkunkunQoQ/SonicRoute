using System.Diagnostics;
using System.Reflection;
using SonicRoute.AndroidAudio;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task WindowsLinkLanReconnectAsync(string address, string testDirectory)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var service = new WindowsLinkService(testDirectory, "SonicRoute 联调", port: 46918);
        await service.EnableAsync(limit.Token);
        var saved = service.Peers.Single(p => p.Trusted && p.Address == address);
        var watch = Stopwatch.StartNew();
        await service.ConnectAsync(address, token: limit.Token);
        Check(service.Peers.Any(p => p.Id == saved.Id && p.Connected), "typed address restores an authenticated connection to the saved peer");
        Console.WriteLine($"CONNECTED {saved.Name} in {watch.ElapsedMilliseconds} ms");
        var apps = await service.RequestCatalogAsync(saved.Id, includeIcons: false, limit.Token, audioOnly: true);
        Console.WriteLine($"AUDIO CATALOG {apps.Length} rows");
        await Task.Delay(1200, limit.Token);
        using var host = Process.GetCurrentProcess();
        host.Refresh();
        long privateStart = host.PrivateMemorySize64;
        long workingStart = host.WorkingSet64;
        long allocated = GC.GetTotalAllocatedBytes(true);
        var before = host.TotalProcessorTime;
        var traffic = service.ReadTraffic(saved.Id);
        watch.Restart();
        await Task.Delay(TimeSpan.FromSeconds(24), limit.Token);
        host.Refresh();
        var endTraffic = service.ReadTraffic(saved.Id);
        Check(service.Peers.Any(p => p.Id == saved.Id && p.Connected) && service.Streams.Length == 0,
            "paired peer stays available without an open media stream");
        Console.WriteLine($"IDLE seconds={watch.Elapsed.TotalSeconds:F2} processCpuMs={(host.TotalProcessorTime - before).TotalMilliseconds:F1}"
            + $" allocatedBytes={GC.GetTotalAllocatedBytes(true) - allocated} privateDeltaBytes={host.PrivateMemorySize64 - privateStart}"
            + $" workingSetStartBytes={workingStart} workingSetEndBytes={host.WorkingSet64}"
            + $" peerTrafficBytes={endTraffic.Sent - traffic.Sent + endTraffic.Received - traffic.Received}");
    }

    private static async Task WindowsLinkLanToneAsync(string address, string testDirectory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var token = deadline.Token;
        bool listen = address.StartsWith("listen:", StringComparison.Ordinal);
        bool local = address == "local";
        string? expectedPeer = listen ? address.Split('/')[1] : null;
        int port = listen ? int.Parse(address[7..].Split('/')[0]) : 0;
        await using var receiver = local ? new WindowsLinkService(Path.Combine(testDirectory, "receiver"), "Test receiver",
            port: 0, discovery: false) : null;
        if (receiver != null)
        {
            receiver.PairingRequested += p => receiver.AcceptPairing(p.Id, true);
            await receiver.EnableAsync(token);
            address = receiver.LocalAddress;
        }
        await using var service = new WindowsLinkService(testDirectory, "SonicRoute 联调", port: port, discovery: listen);
        service.PairingRequested += pairing =>
        {
            Console.WriteLine($"PAIR {pairing.Name} {pairing.Code}");
            if (local) service.AcceptPairing(pairing.Id, true);
            else _ = Task.Run(async () => service.AcceptPairing(pairing.Id,
                string.Equals((await Console.In.ReadLineAsync(token))?.Trim(), pairing.Code, StringComparison.Ordinal)));
        };
        await service.EnableAsync(token);
        if (listen)
        {
            Console.WriteLine($"LISTEN {service.LocalAddress}");
            while (!service.Peers.Any(p => p.Connected)) await Task.Delay(200, token);
        }
        else await service.ConnectAsync(address, token: token);
        var peer = service.Peers.Single(p => p.Connected);
        if (listen && peer.Id != expectedPeer) throw new IOException("Unexpected test peer identity.");
        Console.WriteLine($"CONNECTED {peer.Name} capabilities={(int)peer.Capabilities} localCapture={service.CanSendAudio}");
        var apps = await service.RequestCatalogAsync(peer.Id, includeIcons: false, token, audioOnly: true);
        Console.WriteLine($"AUDIO CATALOG {apps.Length} rows");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("--windows-link-tone-source");
        using var source = Process.Start(start)!;
        try
        {
            if (await source.StandardOutput.ReadLineAsync(token) != "READY") throw new IOException("Test tone source failed to open.");
            using var meterStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var meterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long audibleSamples = 0;
            int maxSample = 0;
            var meter = LinkAudioCapture.CaptureAsync(source.Id, packet =>
            {
                foreach (short sample in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(packet.Span))
                {
                    int amplitude = Math.Abs((int)sample);
                    if (amplitude > 32) audibleSamples++;
                    maxSample = Math.Max(maxSample, amplitude);
                }
            }, () => meterStarted.TrySetResult(), meterStop.Token);
            await meterStarted.Task.WaitAsync(TimeSpan.FromSeconds(4), token);
            Guid stream = await service.SendAudioAsync(peer.Id, source.Id, "SonicRoute 测试音", token, appKey: "sonicroute-test-tone");
            long before = service.ReadTraffic(peer.Id).Sent;
            Console.WriteLine("PLAY receiver acknowledged playback");
            await source.StandardInput.WriteLineAsync("play");
            await source.StandardInput.FlushAsync();
            await source.WaitForExitAsync(token);
            if (source.ExitCode != 0) throw new IOException("Tone source failed.");
            meterStop.Cancel();
            try { await meter; } catch (OperationCanceledException) when (meterStop.IsCancellationRequested) { }
            Console.WriteLine($"CAPTURE peak={maxSample}/32768 nonSilentSamples={audibleSamples}");
            var received = receiver?.IncomingAudioSources.FirstOrDefault()?.Session.Pcm;
            if (local)
            {
                Console.WriteLine($"RECEIVE nonSilentSamples={received?.NonSilentSamplesWritten} consumed={received?.SamplesConsumed} gain={received?.EffectiveGain}");
                Check(received?.NonSilentSamplesWritten > 2000, "non-silent tone samples arrive in the playback buffer");
                Check(received!.EffectiveGain > 0.99f, "smart volume preserves the quiet desktop signal at full playback volume");
            }
            Check(maxSample > 150 && audibleSamples > 2000, "the actual sending application's WASAPI signal contains the test tones");
            long bytes = service.ReadTraffic(peer.Id).Sent - before;
            Check(bytes > 48000, $"peer acknowledged playback and {bytes} bytes were sent during the test tone");
            await service.StopStreamAsync(stream);
        }
        finally { source.StandardInput.Close(); }
    }

    private static async Task PlayLinkToneAsync()
    {
        var pcm = new AndroidPcmSource();
        using var player = new WasapiPcmPlayer(pcm, null);
        player.Play();
        Console.WriteLine("READY");
        if (await Console.In.ReadLineAsync() != "play") return;
        var block = new float[4800 * 2];
        foreach (int hz in new[] { 440, 554, 659 })
        {
            for (int i = 0; i < block.Length / 2; i++)
            {
                float edge = Math.Min(1f, Math.Min(i, 4799 - i) / 240f);
                block[2 * i] = block[2 * i + 1] = 0.12f * edge * (float)Math.Sin(2 * Math.PI * hz * i / 48000);
            }
            pcm.Write(block);
            pcm.Write(block);
            await Task.Delay(430);
        }
        await Task.Delay(250);
    }
}

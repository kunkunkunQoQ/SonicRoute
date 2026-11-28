using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task PlayOwnedFixtureWaveAsync()
    {
        using var devices = new MMDeviceEnumerator();
        using var endpoint = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var memory = new MemoryStream();
        using (var writer = new WaveFileWriter(memory, new WaveFormat(48000, 16, 2)))
        {
            for (int i = 0; i < 48_000; i++)
            {
                double fade = Math.Min(1d, Math.Min(i, 47_999 - i) / 480d);
                float sample = (float)(.012 * fade * Math.Sin(2 * Math.PI * 500 * i / 48_000));
                writer.WriteSample(sample); writer.WriteSample(sample);
            }
        }
        using var wave = new WaveFileReader(new MemoryStream(memory.ToArray()));
        using var output = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 50);
        output.Init(new RepeatedListenWave(wave));
        Console.WriteLine("OWNED_PLAYER_READY");
        if (await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)) != "play") return;
        output.Play();
        await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
        output.Stop();
    }

    private static async Task VerifyOwnedPlayerAsync(AndroidDevice phone, SrapSession session, string target, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var ct = deadline.Token;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--play-owned-wav");
        using var player = Process.Start(start) ?? throw new IOException("The isolated fixture player did not start.");
        var ready = new TaskCompletionSource<SrapPcmOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var router = new PcToPhoneAudioManager(() => throw new InvalidOperationException("Expected an authenticated app peer."));
        router.OpenAppPlayback = async (peer, sink, stop, routed) =>
        {
            try
            {
                var output = (SrapPcmOutput)await SrapPcmOutput.OpenAsync(session, peer, sink, stop, routed);
                ready.TrySetResult(output);
                return output;
            }
            catch (Exception ex) { ready.TrySetException(ex); throw; }
        };
        try
        {
            if (await player.StandardOutput.ReadLineAsync(ct) != "OWNED_PLAYER_READY")
                throw new InvalidOperationException("The selected player did not load its synthetic WAV.");
            string selection = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(phone), target);
            Check(router.RouteProcess(player.Id, selection, phone, silenceLocal: false).Success, "the regular per-process phone route selected only the owned fixture PID");
            var track = await ready.Task.WaitAsync(ct);
            var consumed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            long observed = 0, targetAck = long.MaxValue;
            void OnCredit(JsonElement body)
            {
                if (SrapClient.Text(body, "name") != "audio.credit" || !body.TryGetProperty("streamId", out var stream) ||
                    !stream.TryGetUInt32(out uint id) || id != track.Id || !body.TryGetProperty("nextSeq", out var count) ||
                    !count.TryGetInt64(out var packets)) return;
                Interlocked.Exchange(ref observed, packets);
                if (packets >= Interlocked.Read(ref targetAck)) consumed.TrySetResult(packets);
            }
            session.StreamEvent += OnCredit;
            try
            {
                player.StandardInput.WriteLine("play"); player.StandardInput.Flush();
                long nonSilent;
                while ((nonSilent = router.GetDiagnostics(player.Id)?.NonSilentBytesStreamed ?? 0) < 19_200)
                {
                    ct.ThrowIfCancellationRequested();
                    if (player.HasExited || track.Completion.IsCompleted) throw new IOException("The fixture process or phone track closed early.");
                    await Task.Delay(25, ct);
                }
                // At most four data packets can be in flight. A further full credit window
                // proves the phone consumed the nonzero packets counted before this point.
                Interlocked.Exchange(ref targetAck, Interlocked.Read(ref observed) + SrapPcmOutput.Window + 1);
                var acknowledged = await consumed.Task.WaitAsync(ct);
                Check(acknowledged > 0, $"phone acknowledged the dedicated player's WASAPI process-loopback data (nonSilentBytes={nonSilent}, acknowledgedPackets={acknowledged})");
            }
            finally { session.StreamEvent -= OnCredit; }
            Check(await router.StopProcessAsync(player.Id) && track.StopConfirmed && !session.Token.IsCancellationRequested,
                "local stop closed the ordinary drag-and-drop track while the trusted phone stayed connected");
        }
        finally
        {
            if (!player.HasExited)
            {
                try { player.StandardInput.WriteLine("stop"); player.StandardInput.Flush(); }
                catch (IOException) { }
                if (!player.WaitForExit(2500)) player.Kill();
            }
        }
    }
}

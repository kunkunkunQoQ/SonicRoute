using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

internal static partial class Program
{
    private static async Task<int> CheckLocalMirrorsAsync(string directory)
    {
        Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", Directory.CreateDirectory(Path.Combine(directory, Guid.NewGuid().ToString("N"))).FullName);
        var mirrors = typeof(SonicRoute.App).Assembly.GetType("SonicRoute.LocalAppOutputMirrors", true)!;
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var set = mirrors.GetMethod("SetExtraOutputsAsync", statics)!.CreateDelegate<Func<int, IReadOnlyCollection<string>, CancellationToken, Task>>();
        var get = mirrors.GetMethod("GetExtraOutputs", statics)!.CreateDelegate<Func<int, string[]>>();
        var clear = mirrors.GetMethod("ClearAsync", statics)!.CreateDelegate<Func<int, Task>>();
        var resolve = mirrors.GetMethod("ResolveOutputs", statics)!.CreateDelegate<Func<string[], bool, string[]>>();
        using (var defaultEndpoint = WasapiDeviceLease.Open(null))
        {
            var id = defaultEndpoint.Device.ID;
            Require(resolve([id, id], true).SequenceEqual([id]), "an explicit mirror can target the current default, once per endpoint ID");
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(28));
        var token = deadline.Token;
        var before = Snapshot();
        var free = IdleTestOutputs(before);
        foreach (var o in before) Console.WriteLine($"Endpoint {o.Name}: default={o.Default} muted={o.Muted} active={HasActivePlayback(o.Id)}");
        Require(free.Length >= 2, "the test can use idle non-default local endpoints");
        var source = free[0];
        var destinations = free.Skip(1).ToArray();
        Console.WriteLine("Source " + source.Name + "; mirrors " + string.Join(", ", destinations.Select(d => d.Name)));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--mirror-tone"); start.ArgumentList.Add(source.Id);
        using var tone = Process.Start(start) ?? throw new IOException("Tone fixture could not start.");
        int pid = tone.Id;
        try
        {
            await tone.StandardOutput.ReadLineAsync(token);
            await Until(() => Snapshot(pid).Any(d => d.Id == source.Id && d.ToneActive), token);
            await set(pid, [source.Id], token);
            Require(get(pid).SequenceEqual([source.Id]), "an explicit mirror is kept on the source's active playback endpoint");
            string[] requested = [..destinations.Select(d => d.Id), destinations[0].Id];
            await set(pid, requested, token);
            Require(get(pid).Order().SequenceEqual(destinations.Select(d => d.Id).Order()), "one entry per explicitly selected mirror endpoint");
            await Task.WhenAll(free.Select(d => Observe713HzAsync(d.Id, token)));
            Require(Snapshot(pid).Single(d => d.ToneActive).Id == source.Id, "native source and every selected mirror render the 713 Hz tone without moving the source session");
            var continuity = await Task.WhenAll(free.Select(d => MeasureToneGapsAsync(d.Id, token)));
            for (int i = 0; i < free.Length; i++)
                Console.WriteLine($"Native PCM {free[i].Name}: {continuity[i]} quiet 10 ms intervals over 2 s");
            Require(continuity.Skip(1).All(gaps => gaps <= continuity[0] + 1), "the selected mirrors do not add repeated gaps to continuous source audio");
            try
            {
                await set(pid, [destinations[0].Id, "{missing-local-test-endpoint}"], token);
                throw new InvalidOperationException("An unavailable endpoint was accepted.");
            }
            catch (Exception ex) when (ex is COMException or IOException or ArgumentException) { }
            Require(get(pid).Length == destinations.Length, "an unavailable new selection preserves the previous set");
            await clear(pid);
            Require(get(pid).Length == 0 && Snapshot(pid).Single(d => d.ToneActive).Id == source.Id, "clearing extras leaves original playback intact");
            string nextSource = destinations[0].Id;
            await set(pid, [source.Id, nextSource], token);
            await tone.StandardInput.WriteLineAsync(nextSource);
            await tone.StandardOutput.ReadLineAsync(token);
            await Until(() => Snapshot(pid).Any(d => d.Id == nextSource && d.ToneActive), token);
            await Task.WhenAll(Observe713HzAsync(source.Id, token), Observe713HzAsync(nextSource, token));
            Require(get(pid).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([source.Id, nextSource]),
                "moving the native session between two selected endpoints preserves both mirrors");
            await tone.StandardInput.WriteLineAsync(); await tone.WaitForExitAsync(token);
            await Until(() => get(pid).Length == 0, token);
            var after = Snapshot();
            Require(before.All(b => after.Any(a => a.Id == b.Id && a.Muted == b.Muted && a.Default == b.Default)), "process exit releases mirrors and all device mute/default settings remain intact");
            return 0;
        }
        finally
        {
            if (!tone.HasExited) { await tone.StandardInput.WriteLineAsync(); await tone.WaitForExitAsync(); }
            await clear(pid);
        }
    }

    private static bool HasActivePlayback(string id)
    {
        using var lease = WasapiDeviceLease.Open(id);
        var sessions = lease.Device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) return true;
        }
        return false;
    }

    private static Output[] IdleTestOutputs(Output[] endpoints) =>
        endpoints.Where(o => !o.Default && !o.Muted && !HasActivePlayback(o.Id))
            .OrderBy(o => o.Occupied)
            .ThenByDescending(o => o.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase))
            .Take(3).ToArray();

    // The edition-specific UI runs in its own process. This source supplies the same
    // tone and hardware measurements to both .NET 10 and .NET Framework checks.
    private static async Task PanelToneProbeAsync(string? outputSet)
    {
        string[]? fixedIds = outputSet != null && File.Exists(outputSet)
            ? System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(outputSet)) ?? throw new InvalidDataException("Empty output set.") : null;
        if (fixedIds != null && (fixedIds.Length != 3 || fixedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3))
            throw new InvalidDataException("The shared output set must contain three distinct endpoints.");
        var deadline = Stopwatch.StartNew();
        Output[] outputs;
        while (true)
        {
            var inventory = Snapshot();
            outputs = fixedIds == null ? IdleTestOutputs(inventory) : fixedIds.Select(id =>
                inventory.SingleOrDefault(o => o.Id == id && !o.Default && !o.Muted)
                ?? throw new IOException("A fixed endpoint is missing, muted, or has become the default.")).ToArray();
            if (outputs.Length >= (outputSet == null ? 2 : 3) && outputs.All(o => !HasActivePlayback(o.Id))) break;
            if (deadline.Elapsed.TotalSeconds >= 30) throw new IOException("The requested group of idle playback endpoints is unavailable.");
            await Task.Delay(500);
        }
        if (outputSet != null && fixedIds == null)
        {
            using var file = new FileStream(outputSet, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            System.Text.Json.JsonSerializer.Serialize(file, outputs.Select(o => o.Id));
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(outputs.Select(o => o.Id)));
        while (await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)) is { Length: > 0 } command)
        {
            if (command == "only-primary")
            {
                await Observe713HzAsync(outputs[0].Id, CancellationToken.None);
                async Task<bool> HasTone(string endpoint)
                {
                    try { await Observe713HzAsync(endpoint, CancellationToken.None, TimeSpan.FromSeconds(2)); return true; }
                    catch (TimeoutException) { return false; }
                }
                var extras = await Task.WhenAll(outputs.Skip(1).Select(o => HasTone(o.Id)));
                Console.WriteLine(extras.All(present => !present) ? "713 Hz only on primary" : "Unexpected mirrored tone");
                continue;
            }
            if (command != "measure") throw new InvalidDataException("Unknown tone probe request.");
            await Task.WhenAll(outputs.Select(o => Observe713HzAsync(o.Id, CancellationToken.None)));
            int[] gaps = await Task.WhenAll(outputs.Select(o => MeasureToneGapsAsync(o.Id, CancellationToken.None)));
            string report = string.Join(", ", outputs.Select((o, index) => $"{o.Name} ({gaps[index]} quiet 10 ms intervals/2 s)"));
            Console.WriteLine(gaps.Skip(1).All(g => g <= gaps[0] + 1) ? "713 Hz on " + report : "Playback gaps on " + report);
        }
    }

    private static async Task MovableToneAsync(string? id)
    {
        while (!string.IsNullOrEmpty(id))
        {
            using var device = WasapiDeviceLease.Open(id);
            using var player = new WasapiOut(device.Device, AudioClientShareMode.Shared, true, 30);
            player.Init(new SignalGenerator(48000, 2) { Gain = 0.013, Frequency = 713, Type = SignalGeneratorType.Sin }.ToWaveProvider());
            player.Play();
            Console.WriteLine("ready");
            id = await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(75));
        }
    }

    private static async Task Observe713HzAsync(string endpoint, CancellationToken token, TimeSpan? timeout = null)
    {
        using var device = WasapiDeviceLease.Open(endpoint);
        using var recorder = new WasapiLoopbackCapture(device.Device);
        var format = recorder.WaveFormat;
        if (format.BitsPerSample != 32) throw new InvalidOperationException("Expected native float PCM from the test endpoint.");
        var detected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.DataAvailable += (_, args) =>
        {
            var data = MemoryMarshal.Cast<byte, float>(args.Buffer.AsSpan(0, args.BytesRecorded));
            int count = data.Length / format.Channels;
            if (count < 96) return;
            double sin = 0, cos = 0, power = 0, step = 2 * Math.PI * 713 / format.SampleRate;
            for (int i = 0; i < count; i++)
            {
                float sample = data[i * format.Channels];
                sin += sample * Math.Sin(step * i); cos += sample * Math.Cos(step * i); power += sample * sample;
            }
            if (power > count * 1e-10 && (sin * sin + cos * cos) / (power * count) > 0.38) detected.TrySetResult();
        };
        recorder.StartRecording();
        try { await detected.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5), token); }
        finally { recorder.StopRecording(); }
    }

    private static async Task<int> MeasureToneGapsAsync(string endpoint, CancellationToken token)
    {
        using var device = WasapiDeviceLease.Open(endpoint);
        using var recorder = new WasapiLoopbackCapture(device.Device);
        var format = recorder.WaveFormat;
        int frames = 0, intervals = 0, gaps = 0;
        float peak = 0f;
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.DataAvailable += (_, packet) =>
        {
            var samples = MemoryMarshal.Cast<byte, float>(packet.Buffer.AsSpan(0, packet.BytesRecorded));
            for (int i = 0; i < samples.Length; i += format.Channels)
            {
                peak = Math.Max(peak, Math.Abs(samples[i]));
                if (++frames < format.SampleRate / 100) continue;
                if (peak > 0.00001f || intervals != 0)
                {
                    if (peak <= 0.00001f) gaps++;
                    if (++intervals == 200) result.TrySetResult(gaps);
                }
                frames = 0;
                peak = 0f;
            }
        };
        recorder.StartRecording();
        try { return await result.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
        finally { recorder.StopRecording(); }
    }
}

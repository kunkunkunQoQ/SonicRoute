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
        var set = mirrors.GetMethod("SetExtraOutputsAsync", statics)!.CreateDelegate<Func<int, string?, IReadOnlyCollection<string>, CancellationToken, Task>>();
        var get = mirrors.GetMethod("GetExtraOutputs", statics)!.CreateDelegate<Func<int, string[]>>();
        var clear = mirrors.GetMethod("ClearAsync", statics)!.CreateDelegate<Func<int, Task>>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(28));
        var token = deadline.Token;
        var before = Snapshot();
        var free = before.Where(o => !o.Default && !o.Muted && !HasActivePlayback(o.Id))
            .OrderBy(o => o.Occupied)
            .ThenByDescending(o => o.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase))
            .Take(3).ToArray();
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
            using (var defaultEndpoint = WasapiDeviceLease.Open(null))
                await set(pid, AudioService.SystemDefaultDeviceId, [AudioService.SystemDefaultDeviceId, defaultEndpoint.Device.ID, source.Id], token);
            Require(!get(pid).Any(), "the default alias, its physical output, and a native session never create duplicate playback");
            string[] requested = [source.Id, ..destinations.Select(d => d.Id), destinations[0].Id];
            await set(pid, source.Id, requested, token);
            Require(get(pid).Order().SequenceEqual(destinations.Select(d => d.Id).Order()), "one entry per additional output, even when the primary and duplicates are selected");
            await Task.WhenAll(free.Select(d => Observe713HzAsync(d.Id, token)));
            Require(Snapshot(pid).Single(d => d.ToneActive).Id == source.Id, "native source and every selected mirror render the 713 Hz tone without moving the source session");
            try
            {
                await set(pid, source.Id, [destinations[0].Id, "{missing-local-test-endpoint}"], token);
                throw new InvalidOperationException("An unavailable endpoint was accepted.");
            }
            catch (Exception ex) when (ex is COMException or IOException or ArgumentException) { }
            Require(get(pid).Length == destinations.Length, "an unavailable new selection preserves the previous set");
            await clear(pid);
            Require(get(pid).Length == 0 && Snapshot(pid).Single(d => d.ToneActive).Id == source.Id, "clearing extras leaves original playback intact");
            await set(pid, source.Id, [destinations[0].Id], token);
            await tone.StandardInput.WriteLineAsync(destinations[0].Id);
            await tone.StandardOutput.ReadLineAsync(token);
            await Until(() => get(pid).Length == 0, token);
            Require(Snapshot(pid).Single(d => d.ToneActive).Id == destinations[0].Id, "moving the app directly to a mirrored endpoint automatically removes that echo path");
            await set(pid, destinations[0].Id, [source.Id], token);
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

    private static async Task MovableToneAsync(string? id)
    {
        while (!string.IsNullOrEmpty(id))
        {
            using var device = WasapiDeviceLease.Open(id);
            using var player = new WasapiOut(device.Device, AudioClientShareMode.Shared, true, 30);
            player.Init(new SignalGenerator(48000, 2) { Gain = 0.013, Frequency = 713, Type = SignalGeneratorType.Sin }.ToWaveProvider());
            player.Play();
            Console.WriteLine("ready");
            id = await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(35));
        }
    }

    private static async Task Observe713HzAsync(string endpoint, CancellationToken token)
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
        try { await detected.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
        finally { recorder.StopRecording(); }
    }
}

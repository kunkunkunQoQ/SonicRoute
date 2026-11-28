using System.Diagnostics;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private sealed class MeterObservation
    {
        internal bool Own, Default, SystemSound, AudioEngine, ProductCapture;
        internal readonly float[] Maxima = new float[7];
        internal readonly int[] Readings = new int[7];
    }

    // Diagnostic only. No capture client is opened and no identifier, path or PID is written.
    private static async Task PcListenMeterProbeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var devices = new MMDeviceEnumerator();
        using var normal = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var observed = new Dictionary<string, MeterObservation>(StringComparer.Ordinal);
        int stage = 0;
        void Measure(AudioSessionControl s, bool target, float peak)
        {
            if (!observed.TryGetValue(s.GetSessionInstanceIdentifier, out var reading))
            {
                int pid = (int)s.GetProcessID;
                reading = new() { Own = pid == Environment.ProcessId, Default = target, SystemSound = s.IsSystemSoundsSession,
                    ProductCapture = s.GetSessionIdentifier.EndsWith("%b{" + ProcessCaptureIdentity.SessionId + "}", StringComparison.OrdinalIgnoreCase) };
                if (pid > 0 && !reading.Own)
                {
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        reading.AudioEngine = string.Equals(process.MainModule?.FileName, Path.Combine(Environment.SystemDirectory, "audiodg.exe"),
                            StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { }
                }
                observed.Add(s.GetSessionInstanceIdentifier, reading);
            }
            reading.Maxima[stage] = !float.IsFinite(peak) ? 1f : Math.Max(peak, reading.Maxima[stage]);
            reading.Readings[stage]++;
        }
        for (int i = 0; i < 10; i++)
        {
            GetRenderActivity(devices, normal.ID, Measure);
            if (i < 9) await Task.Delay(60);
        }
        bool quiet = observed.Values.All(x => x.Maxima[0] == 0f);
        if (quiet)
        {
            const int rate = 48_000, stepMs = 480;
            const int framesPerStep = rate * stepMs / 1000;
            using var buffer = new MemoryStream();
            using (var generated = new WaveFileWriter(buffer, new WaveFormat(rate, 16, 2)))
            {
                double[] amplitudes = [.012, 0, .018, 0, .009, 0];
                foreach (double amplitude in amplitudes)
                for (int i = 0; i < framesPerStep; i++)
                {
                    double fade = Math.Min(1d, Math.Min(i, framesPerStep - 1 - i) / 480d);
                    float sample = (float)(amplitude * fade * Math.Sin(2 * Math.PI * 523.25 * i / rate));
                    generated.WriteSample(sample); generated.WriteSample(sample);
                }
            }
            using var wave = new WaveFileReader(new MemoryStream(buffer.ToArray()));
            using var player = new WasapiOut(normal, AudioClientShareMode.Shared, true, 50);
            player.Init(wave);
            var clock = Stopwatch.StartNew();
            player.Play();
            while (clock.ElapsedMilliseconds < 6 * stepMs)
            {
                int ms = (int)clock.ElapsedMilliseconds;
                if (ms % stepMs is >= 150 and <= 400)
                {
                    stage = 1 + ms / stepMs;
                    GetRenderActivity(devices, normal.ID, Measure);
                }
                await Task.Delay(35);
            }
            player.Stop();
        }
        bool disabledCable = false;
        foreach (var endpoint in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Disabled))
        using (endpoint) { if (IsInstalledLocalCable(endpoint)) disabledCable = true; }
        var data = new { knownWavePlayed = quiet, pcmCaptured = false, networkTransport = false, enabledIdleLocalCable = FindIdleLocalCable(devices) != null,
            disabledInstalledLocalCable = disabledCable, levels = new[] { "before", "tone1", "silence1", "tone2", "silence2", "tone3", "silence3" },
            sessions = observed.Values.Select((s, i) => new { anonymousIndex = i + 1, ownTestPlayer = s.Own, onDefaultEndpoint = s.Default,
                systemSound = s.SystemSound, windowsAudioEngine = s.AudioEngine, knownProductCapture = s.ProductCapture,
                peakPerSegment = s.Maxima, readings = s.Readings }) };
        string result = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "meter-only.json"), result);
        Console.WriteLine(result);
    }
}

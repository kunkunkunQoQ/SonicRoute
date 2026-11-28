using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private readonly record struct RenderActivity(int Active, int Audible, bool OwnDefault, bool OwnPeak,
        int Endpoints, int IdleEndpoints, int KnownLoopbacks, int AudibleKnownLoopbacks);

    private static bool IsInstalledLocalCable(MMDevice endpoint)
    {
        var last = endpoint.ID.LastIndexOf('.');
        if (last < 0 || !Guid.TryParseExact(endpoint.ID[(last + 1)..], "B", out var id)) return false;
        using var properties = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + id.ToString("B") + @"\Properties");
        if (properties?.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string != "VB-Audio Hi-Fi Cable" ||
            properties.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},2") is not string controller) return false;
        const string prefix = @"{1}.ROOT\MEDIA\";
        if (!controller.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            controller.Length != prefix.Length + 4 || !controller[prefix.Length..].All(char.IsAsciiDigit)) return false;
        using var instance = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + controller[4..]);
        if (instance?.GetValue("Driver") is not string driver) return false;
        using var settings = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver);
        return settings?.GetValue("ProviderName") as string == "VB-Audio Software" &&
            settings.GetValue("DriverDesc") as string == "VB-Audio Hi-Fi Cable" &&
            settings.GetValue("MatchingDeviceId") as string == "VBAudioHFVAIO";
    }

    private static string? FindIdleLocalCable(MMDeviceEnumerator devices)
    {
        foreach (var endpoint in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        using (endpoint)
        {
            if (!IsInstalledLocalCable(endpoint)) continue;
            var sessions = endpoint.AudioSessionManager.Sessions;
            bool idle = true;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (session.State == AudioSessionState.AudioSessionStateActive) { idle = false; break; }
            }
            if (idle) return endpoint.ID;
        }
        return null;
    }

    private static RenderActivity GetRenderActivity(MMDeviceEnumerator devices, string? target = null,
        Action<AudioSessionControl, bool, float>? observe = null)
    {
        int active = 0, audible = 0, endpoints = 0, idleEndpoints = 0, knownLoopbacks = 0, audibleKnownLoopbacks = 0;
        bool own = false, ownPeak = false;
        string loopbackSuffix = "%b{" + ProcessCaptureIdentity.SessionId + "}";
        foreach (var endpoint in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (endpoint)
            {
                var sessions = endpoint.AudioSessionManager.Sessions;
                int endpointActive = 0;
                endpoints++;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                    endpointActive++;
                    float peak = session.AudioMeterInformation.MasterPeakValue;
                    observe?.Invoke(session, endpoint.ID == target, peak);
                    if (target != null && session.GetProcessID == Environment.ProcessId)
                    {
                        own |= endpoint.ID == target;
                        ownPeak |= float.IsFinite(peak) && peak > 0f;
                        continue;
                    }
                    active++;
                    bool known = session.GetSessionIdentifier.EndsWith(loopbackSuffix, StringComparison.OrdinalIgnoreCase);
                    if (known) knownLoopbacks++;
                    if (!float.IsFinite(peak) || peak != 0f)
                    {
                        audible++;
                        if (known) audibleKnownLoopbacks++;
                    }
                }
                if (endpointActive == 0) idleEndpoints++;
            }
        }
        return new(active, audible, own, ownPeak, endpoints, idleEndpoints, knownLoopbacks, audibleKnownLoopbacks);
    }

    private static async Task PcListenAuditAsync()
    {
        using var devices = new MMDeviceEnumerator();
        using var endpoint = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        int maxActive = 0, maxAudible = 0;
        RenderActivity observed = default;
        for (int i = 0; i < 10; i++)
        {
            observed = GetRenderActivity(devices);
            maxActive = Math.Max(maxActive, observed.Active);
            maxAudible = Math.Max(maxAudible, observed.Audible);
            if (i < 9) await Task.Delay(60);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { ready = maxAudible == 0, activeSessions = maxActive,
            nonzeroPeakSessions = maxAudible, knownSonicRouteCaptureSessions = observed.KnownLoopbacks,
            renderEndpoints = observed.Endpoints, idleEndpoints = observed.IdleEndpoints, idleVerifiedLocalCable = FindIdleLocalCable(devices) != null,
            meterReadings = 10, pcmCaptured = false }));
    }

    // Started by the isolated UI runner as a sibling of SonicRoute. Watches active render sessions
    // only while this bounded fixture is running. Does not adjust Windows session or endpoint volumes.
    private static async Task PcListenWaveAsync(string directory, int ownerPid)
    {
        Directory.CreateDirectory(directory);
        void Receipt(string name, object value)
        {
            var path = Path.Combine(directory, name);
            string file = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(file, JsonSerializer.Serialize(value));
            File.Move(file, path);
        }
        string phase = "preflight";
        int audits = 0, maxOtherActive = 0, nonzeroOther = 0, nonzeroKnownLoopbacks = 0;
        bool fixtureActive = false, fixturePeak = false;
        try
        {
            using var owner = Process.GetProcessById(ownerPid);
            using var devices = new MMDeviceEnumerator();
            string? cableId = FindIdleLocalCable(devices);
            using var output = cableId == null ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : devices.GetDevice(cableId);
            var guard = new KnownWaveMeterGuard();
            bool CheckActiveSessions(long? elapsedMs)
            {
                audits++;
                var observed = elapsedMs == null ? GetRenderActivity(devices, output.ID, guard.Before) :
                    guard.Check(devices, output.ID, elapsedMs.Value);
                maxOtherActive = Math.Max(maxOtherActive, observed.Active);
                nonzeroOther = Math.Max(nonzeroOther, observed.Audible);
                nonzeroKnownLoopbacks = Math.Max(nonzeroKnownLoopbacks, observed.AudibleKnownLoopbacks);
                fixtureActive |= observed.OwnDefault;
                fixturePeak |= observed.OwnPeak;
                return observed.OwnDefault;
            }
            for (int i = 0; i < 10; i++) { CheckActiveSessions(null); if (i < 9) await Task.Delay(60); }
            const int sampleRate = 48_000;
            string path = Path.Combine(directory, "short-tone.wav");
            using (var file = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, 2)))
            {
                int framesPerSegment = sampleRate * KnownWaveMeterGuard.SegmentMs / 1000;
                foreach (double amplitude in KnownWaveMeterGuard.Amplitudes)
                for (int i = 0; i < framesPerSegment; i++)
                {
                    double position = Math.Min(1d, Math.Min(i, framesPerSegment - 1 - i) / 4608d);
                    double envelope = .5d - .5d * Math.Cos(Math.PI * position);
                    float sample = (float)(amplitude * envelope * Math.Sin(2 * Math.PI * 500 * i / sampleRate));
                    file.WriteSample(sample);
                    file.WriteSample(sample);
                }
            }
            using var wave = new WaveFileReader(path);
            var loop = new RepeatedListenWave(wave);
            using var playback = new WasapiOut(output, AudioClientShareMode.Shared, true, 60);
            playback.Init(loop);
            phase = "own-wav";
            playback.Play();
            bool ready = false;
            long start = Stopwatch.GetTimestamp();
            string stop = Path.Combine(directory, "wave.stop");
            while (!File.Exists(stop))
            {
                if (owner.HasExited || Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(85))
                    throw new TimeoutException("The bounded WAV fixture expired.");
                if (playback.PlaybackState != PlaybackState.Playing) throw new InvalidOperationException("The WAV fixture stopped early.");
                bool own = CheckActiveSessions((long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if (!ready && guard.Ready && own && fixturePeak && loop.BytesRead > 0)
                {
                    Receipt("wave-ready.json", new { pid = Environment.ProcessId, ownWavePlaying = true, otherAudioActive = false,
                        verifiedLocalCable = cableId != null, calibratedReturns = guard.MatchedReturns });
                    ready = true;
                }
                await Task.Delay(30);
            }
            CheckActiveSessions((long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            playback.Stop();
            Receipt("wave-done.json", new { pid = Environment.ProcessId, auditedRenderEndpoints = "all active",
                sessionAudits = audits, otherAudioActive = false, unverifiedExternalPeaks = 0, calibratedReturns = guard.MatchedReturns,
                maxOtherActiveSessions = maxOtherActive, fixtureActive, fixturePeak,
                waveFrames = loop.BytesRead / 4, verifiedLocalCable = cableId != null, endpointVolumeWrites = 0, sessionVolumeWrites = 0 });
        }
        catch (Exception e)
        {
            Receipt("wave-failure.json", new { reason = e.Message, phase, sessionAudits = audits, maxOtherActiveSessions = maxOtherActive,
                nonzeroOtherSessions = nonzeroOther, nonzeroKnownSonicRouteCaptureSessions = nonzeroKnownLoopbacks, fixtureActive, fixturePeak });
            throw;
        }
    }

    private sealed class RepeatedListenWave(WaveFileReader source) : IWaveProvider
    {
        private long read;
        internal long BytesRead => Interlocked.Read(ref read);
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(byte[] buffer, int offset, int count)
        {
            int written = 0;
            while (written < count)
            {
                int n = source.Read(buffer, offset + written, count - written);
                if (n == 0) source.Position = 0;
                else written += n;
            }
            Interlocked.Add(ref read, written);
            return written;
        }
    }
}

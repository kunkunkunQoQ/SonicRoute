using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task CheckPhoneSinkHardwareAsync(string runtime, string? savedProfile = null, string? address = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(savedProfile == null ? 35 : 90));
        var token = timeout.Token;
        // Each AudioTrack carries only silence. With a saved profile use the same verified
        // wireless reconnect path as the app, including when no external ADB server is running.
        await using var adb = new AdbClient(runtime, savedProfile == null
            ? new AdbSocketClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 5037)) : null);
        var device = savedProfile == null ? (await adb.DevicesAsync(token)).Single(d => d.State == "device")
            : (await new AndroidWirelessConnection(adb).ReconnectAsync(new AndroidPhoneStore(savedProfile).Profile.LastDevice
                ?? throw new InvalidOperationException("No saved phone"), address, token)).Device;
        var sinks = await adb.OutputsAsync(device, token);
        var candidates = sinks.Where(s => s.Kind == AndroidOutputSinkKind.Speaker ||
            s.Kind == AndroidOutputSinkKind.Bluetooth && s.AndroidDeviceType != 7).ToArray();
        Check(candidates.Any(s => s.Kind == AndroidOutputSinkKind.Speaker), "the connected phone reports its speaker");
        Console.WriteLine($"Connected Bluetooth media outputs: {candidates.Count(s => s.Kind == AndroidOutputSinkKind.Bluetooth)}");
        foreach (var sink in candidates)
        {
            var received = new AndroidPcmSource();
            using var player = new AndroidPhonePcmPlayer(adb, device, received, sink.Key, token);
            await player.VerifyBeforePlaybackAsync(token);
            player.Play();
            received.Write(new float[1920]);
            while (player.Diagnostics.Bytes < 3840) await Task.Delay(15, token);
            Check(player.Diagnostics.RoutedType == sink.AndroidDeviceType && player.Diagnostics.NonSilentBytes == 0,
                $"{sink.Kind}: Android confirmed hardware type {sink.AndroidDeviceType} and consumed silent PCM");
            if (savedProfile != null)
            {
                var frame = new float[1920];
                using var cadence = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
                for (int i = 0; i < 500; ++i)
                {
                    await cadence.WaitForNextTickAsync(token);
                    received.Write(frame);
                }
                while (player.Diagnostics.Bytes < 501L * frame.Length * sizeof(short))
                {
                    if (player.Failure is { } error) throw error;
                    await Task.Delay(10, token);
                }
                Check(received.DroppedSamples == 0 && player.Failure == null
                    && (await adb.DevicesAsync(token)).Any(d => d.State == "device" && AndroidDeviceIdentity.MatchesSavedDevice(device, d)),
                    $"{sink.Kind}: all 10 seconds of PCM sent, zero queue drops, same phone still online");
            }
        }
    }
}

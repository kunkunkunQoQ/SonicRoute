using System.Buffers.Binary;
using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    // Write only to the test's own AudioTrack; the helper reports Android's chosen
    // device through AudioTrack.getRoutedDevice(). No app capture or default change.
    private static async Task LiveCurrentOutputAsync(string runtime, string serial, int serverPort, string report)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var adb = new AdbClient(runtime, new AdbSocketClient(serverPort));
        var device = (await adb.DevicesAsync(timeout.Token)).Single(d => d.Serial == serial && d.State == "device");
        var observed = new TaskCompletionSource<(string Key, int Type, string Name)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var audio = await AndroidAudioOutputConnection.OpenAsync(adb, device, "current", timeout.Token,
            onRouted: (key, type, name) => observed.TrySetResult((key, type, name)));
        byte[] tone = new byte[4800 * 4];
        for (int i = 0; i < tone.Length / 4; i++)
        {
            short sample = (short)(300 * Math.Sin(2 * Math.PI * 713 * i / 48000));
            BinaryPrimitives.WriteInt16LittleEndian(tone.AsSpan(i * 4), sample);
            BinaryPrimitives.WriteInt16LittleEndian(tone.AsSpan(i * 4 + 2), sample);
        }
        await audio.Stream.WriteAsync(tone, timeout.Token);
        await audio.WaitForRoutingAsync(timeout.Token);
        var route = await observed.Task.WaitAsync(timeout.Token);
        Check(route.Key == "current" && route.Type > 0 && audio.RoutedType == route.Type,
            $"Android verified current via getRoutedDevice: {route.Type}, {route.Name}");
        // The helper opens muted until Android verifies the output.
        await audio.Stream.WriteAsync(tone, timeout.Token);
        await Task.Delay(200, timeout.Token);
        bool roundTrip = false;
        if (route.Type == 2)
        {
            // Exercise both modes on the very same hardware endpoint.
            foreach (string key in new[] { "speaker", "current" })
            {
                var switched = audio.SwitchTargetAsync(key, timeout.Token);
                var silence = new byte[tone.Length];
                while (!switched.IsCompleted)
                {
                    await audio.Stream.WriteAsync(silence, timeout.Token);
                    await Task.Delay(100, timeout.Token);
                }
                await switched;
                using var actual = JsonDocument.Parse(audio.LastRouteReport!);
                Check(actual.RootElement.GetProperty("key").GetString() == key && audio.RoutedType == route.Type,
                    $"same test AudioTrack verified {key} on device type {route.Type}");
            }
            roundTrip = true;
        }
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Phone = device.Name, route.Key, route.Type, route.Name,
            SampleRate = 48000, TestToneHz = 713, TestToneBytes = tone.Length * 2, RoundTripOnSameEndpoint = roundTrip, NativeReport = audio.LastRouteReport }));
    }
}

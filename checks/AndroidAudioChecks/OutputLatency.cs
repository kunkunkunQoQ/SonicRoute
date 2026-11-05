using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    // Bounded, silent hardware timing; never opens a phone app, changes its media state,
    // or restarts shared ADB. The helper's actual routed device remains the acceptance gate.
    private static async Task OutputLatencyLiveAsync(string output)
    {
        Directory.CreateDirectory(output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var adb = new AdbClient(new AndroidRuntimeBundle(Path.Combine(output, "runtime")));
        var device = (await adb.DevicesAsync(timeout.Token)).First(d => d.State == "device");
        var sinks = await adb.OutputsAsync(device, timeout.Token);
        var bluetooth = sinks.FirstOrDefault(s => s.Kind == AndroidOutputSinkKind.Bluetooth && s.AndroidDeviceType != 7);
        string before = await adb.RunAsync(timeout.Token, "forward", "--list");
        var connectionType = typeof(AndroidAudioSession).Assembly.GetType("SonicRoute.AndroidAudio.AndroidAudioOutputConnection")!;
        var open = connectionType.GetMethod("OpenAsync")!;
        var switchTarget = connectionType.GetMethod("SwitchTargetAsync")!;
        var waitForRouting = connectionType.GetMethod("WaitForRoutingAsync")!;
        var measurements = new List<object>();
        for (int round = 0; round < 3; round++)
        {
            var clock = Stopwatch.StartNew();
            var opening = (Task)open.Invoke(null, [adb, device, "speaker", timeout.Token, null, null])!;
            await opening;
            object connection = opening.GetType().GetProperty("Result")!.GetValue(opening)!;
            double openMs = clock.Elapsed.TotalMilliseconds;
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var stream = (Stream)connectionType.GetProperty("Stream")!.GetValue(connection)!;
            var pump = Task.Run(async () =>
            {
                byte[] silence = new byte[3840]; // One 20ms PCM block; test-only pacing.
                while (true)
                {
                    await stream.WriteAsync(silence, sending.Token);
                    await Task.Delay(20, sending.Token);
                }
            }, sending.Token);
            try
            {
                await (Task)waitForRouting.Invoke(connection, [timeout.Token])!;
                double readyMs = clock.Elapsed.TotalMilliseconds;
                Check((int)connectionType.GetProperty("RoutedType")!.GetValue(connection)! == 2, "silent timing confirms the built-in speaker hardware");
                measurements.Add(new { Round = round, Phase = "cold-speaker", OpenMs = openMs, ReadyMs = readyMs, Diagnostics = Diagnostics() });
                Console.WriteLine($"LATENCY cold-speaker round={round} openMs={openMs:F1} readyMs={readyMs:F1}");
                if (bluetooth != null)
                {
                    await SwitchAsync(bluetooth.Key, bluetooth.AndroidDeviceType, "warm-bluetooth");
                    await SwitchAsync("speaker", 2, "warm-speaker");
                }
                async Task SwitchAsync(string key, int expected, string phase)
                {
                    clock.Restart();
                    await (Task)switchTarget.Invoke(connection, [key, timeout.Token])!;
                    double elapsed = clock.Elapsed.TotalMilliseconds;
                    Check((int)connectionType.GetProperty("RoutedType")!.GetValue(connection)! == expected,
                        "silent timing verifies " + phase + " without replacing the helper or socket");
                    measurements.Add(new { Round = round, Phase = phase, ReadyMs = elapsed, Diagnostics = Diagnostics() });
                    Console.WriteLine($"LATENCY {phase} round={round} readyMs={elapsed:F1}");
                }
                object Diagnostics()
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var setup = ((double PolicyMs, double PreferredMs, double ReleaseMs))connectionType.GetProperty("SwitchSetup", flags)!.GetValue(connection)!;
                    int buffer = (int)connectionType.GetProperty("BufferFrames", flags)!.GetValue(connection)!;
                    int threshold = (int)connectionType.GetProperty("StartThresholdFrames", flags)!.GetValue(connection)!;
                    Check(threshold > 0 && threshold <= Math.Min(960, buffer), "one-chunk start threshold retains the larger jitter buffer");
                    Console.WriteLine($"DIAG buffer={buffer}; threshold={threshold}; policyMs={setup.PolicyMs:F1}; preferredMs={setup.PreferredMs:F1}; releaseMs={setup.ReleaseMs:F1}");
                    return new { BufferFrames = buffer, StartThresholdFrames = threshold, setup.PolicyMs, setup.PreferredMs, setup.ReleaseMs };
                }
            }
            finally
            {
                sending.Cancel();
                try { await pump; } catch (OperationCanceledException) { }
                await ((IAsyncDisposable)connection).DisposeAsync();
            }
        }
        string after = await adb.RunAsync(timeout.Token, "forward", "--list");
        Check(before.Trim() == after.Trim(), "silent timing removes only its own forwards and preserves shared ADB connections");
        await File.WriteAllTextAsync(Path.Combine(output, "output-latency.json"), JsonSerializer.Serialize(new
        {
            Assertions = _checks, Measurements = measurements, BluetoothAvailable = bluetooth != null,
            Scope = "Three cold helper opens and warm hardware switches; zero PCM only, no app controls, route acknowledgement excludes acoustic latency."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

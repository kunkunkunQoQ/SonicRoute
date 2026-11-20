using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static async Task<int> CheckPcCurrentOutputAsync(string output, string serial, int port)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var assembly = typeof(App).Assembly;
        var host = assembly.GetType("SonicRoute.AndroidAudioHost", true)!;
        var adb = (AdbClient)host.GetProperty("Adb", statics)!.GetValue(null)!;
        typeof(AdbClient).GetField("_resolvedTransport", instance)!.SetValue(adb, new AdbSocketClient(port));
        var phone = (await adb.DevicesAsync(token)).Single(d => d.Serial == serial && d.State == "device");
        var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult<IReadOnlyList<AndroidDevice>>([phone]));
        var cache = assembly.GetType("SonicRoute.PhoneAudioWindowCache", true)!;
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        discovery.Publish([phone]);
        var phones = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        phones.RememberDevice(phone);

        var outputs = Snapshot();
        var endpoint = IdleTestOutputs(outputs).First();
        string target = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(phone), "current");
        // Per-app Windows audio policy uses executable identity. The observer must have a different identity from the source.
        string sourceExe = Path.Combine(AppContext.BaseDirectory, $"ToneSource-{Guid.NewGuid():N}.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "AndroidAudioChecks.exe"), sourceExe);
        try
        {
            return await PlayAndCheckAsync(sourceExe);
        }
        finally { File.Delete(sourceExe); }

        async Task<int> PlayAndCheckAsync(string path)
        {
            var start = new ProcessStartInfo(path)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("--mirror-tone"); start.ArgumentList.Add(endpoint.Id);
            using var tone = Process.Start(start) ?? throw new IOException("Cannot start the isolated Windows audio source.");
            using var policy = new AudioPolicyConfig(EDataFlow.eRender);
            var original = (media: policy.GetDefaultEndPoint(tone.Id, ERole.eMultimedia), console: policy.GetDefaultEndPoint(tone.Id, ERole.eConsole));
            var mirrors = assembly.GetType("SonicRoute.LocalAppOutputMirrors", true)!;
            var clear = mirrors.GetMethod("ClearAsync", statics)!.CreateDelegate<Func<int, Task>>();
            try
            {
                Require(await tone.StandardOutput.ReadLineAsync(token) == "ready", "independent PC test application started on an idle non-default endpoint");
                var app = AudioService.GetApps(true).Single(a => a.ProcessId == tone.Id);
                var set = assembly.GetType("SonicRoute.AppPlaybackOutputs", true)!.GetMethod("SetAsync", statics)!
                    .CreateDelegate<Func<AudioAppInfo, string[], string?, bool, Task<(bool Success, int HResult, string Message)>>>();
                var selected = await set(app, [endpoint.Id, target], endpoint.Id, false).WaitAsync(token);
                Require(selected.Success, "normal application playback selection accepted Windows + phone current: " + selected.Message);
                await Observe713HzAsync(endpoint.Id, token);
                var routes = (IDictionary)mirrors.GetField("Routes", statics)!.GetValue(null)!;
                var route = routes[tone.Id]!;
                var sink = ((Array)route.GetType().GetProperty("Sinks", instance)!.GetValue(route)!).Cast<object>().Single();
                var player = (AndroidPhonePcmPlayer)sink.GetType().GetField("Player", instance)!.GetValue(sink)!;
                await Until(() => player.Diagnostics.NonSilentBytes >= 96000 && player.Diagnostics.RoutedType > 0, token);
                var connection = (AndroidAudioOutputConnection)typeof(AndroidPhonePcmPlayer).GetField("_output", instance)!.GetValue(player)!;
                string helperPid = (string)typeof(AndroidAudioOutputConnection).GetField("_pid", instance)!.GetValue(connection)!;
                if (!int.TryParse(helperPid, out int id) || id <= 0) throw new IOException("Missing device-side helper PID.");
                string routeReport = connection.LastRouteReport ?? throw new IOException("Missing AudioTrack route confirmation.");
                using var native = JsonDocument.Parse(routeReport);
                Require(native.RootElement.GetProperty("key").GetString() == "current" && native.RootElement.GetProperty("type").GetInt32() > 0,
                    "phone AudioTrack.getRoutedDevice confirmed current during application mirror playback");
                async Task<string[]> Tracks()
                {
                    string result = await adb.RunAsync(token, "-s", serial, "shell", "dumpsys", "media.audio_flinger");
                    // Newer Android versions format the client column as pid/uid; older dumps use pid alone.
                    string activeClient = $@"^\s*(?:[A-Za-z]+\s+)?\d+\s+yes\s+{id}(?=/|\s)";
                    return result.Split('\n').Where(line => Regex.IsMatch(line, activeClient, RegexOptions.IgnoreCase))
                        .Select(line => line.Trim()).ToArray();
                }
                string[] first = await Tracks();
                await Task.Delay(350, token);
                string[] last = await Tracks();
                Require(first.Length > 0 && last.Length > 0, "Android AudioFlinger reports the test helper's active track");
                var diagnostics = player.Diagnostics;
                await File.WriteAllTextAsync(Path.Combine(output, "pc-app-to-current-live.json"), JsonSerializer.Serialize(new
                {
                    WindowsPid = tone.Id, WindowsEndpoint = endpoint.Name, Frequency = 713, Phone = phone.Name,
                    PhoneHelperPid = id, diagnostics.Bytes, diagnostics.NonSilentBytes, diagnostics.RoutedType,
                    NativeRoute = routeReport, AudioFlingerFirst = first, AudioFlingerLast = last
                }, new JsonSerializerOptions { WriteIndented = true }), token);
                var after = Snapshot();
                var changed = outputs.Where(o => !after.Any(item => item.Id == o.Id && item.Default == o.Default && item.Muted == o.Muted)).ToArray();
                foreach (var before in changed)
                {
                    var now = after.FirstOrDefault(o => o.Id == before.Id);
                    Console.WriteLine($"Endpoint {before.Name} before default={before.Default}, muted={before.Muted}, after default={now?.Default}, muted={now?.Muted}");
                }
                Require(changed.Length == 0,
                    "Windows default devices and endpoint mute settings preserved");
                return 0;
            }
            finally
            {
                try { await clear(tone.Id); }
                finally
                {
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndPoint(original.media, tone.Id, ERole.eMultimedia));
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndPoint(original.console, tone.Id, ERole.eConsole));
                    tone.StandardInput.Close();
                    await tone.WaitForExitAsync();
                }
            }
        }
    }
}

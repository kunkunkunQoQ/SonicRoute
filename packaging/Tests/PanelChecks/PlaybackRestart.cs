using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void CheckPlaybackRestart(string probeExe, string outputSet, string resultDirectory)
    {
        string data = Directory.CreateDirectory(Path.Combine(resultDirectory, "profile-" + Guid.NewGuid().ToString("N"))).FullName;
        using var meter = StartPlaybackMeter(probeExe, outputSet);
        string[] ids = JsonSerializer.Deserialize<string[]>(ReadPlaybackReply(meter, 40))!;
        using var current = Process.GetCurrentProcess();
        string host = current.MainModule!.FileName;
        string entry = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"\"{typeof(Program).Assembly.Location}\" " : "";
        string workerArgs = entry + $"--playback-restore-worker \"{data}\" \"{outputSet}\"";
        Process? worker = null, tone = null;
        void StartWorker()
        {
            worker = StartPlaybackChild(host, workerArgs);
            string reply = ReadPlaybackReply(worker);
            Check(reply == "ready", $"fresh product worker {worker.Id} started with shared profile: {reply}");
        }
        void StartTone()
        {
            tone = StartPlaybackChild(probeExe, "--mirror-tone " + ids[0]);
            Check(ReadPlaybackReply(tone) == "ready", $"fixture PID {tone.Id} plays on the isolated primary endpoint");
        }
        void StopChild(ref Process? child)
        {
            if (child == null) return;
            using var process = child;
            child = null;
            if (!process.HasExited) process.StandardInput.Close();
            if (!process.WaitForExit(10000) || process.ExitCode != 0)
                throw new IOException("Playback fixture did not shut down normally: PID " + process.Id);
        }
        void Request(string command)
        {
            worker!.StandardInput.WriteLine($"{command} {tone!.Id}");
            string reply = ReadPlaybackReply(worker, 20);
            if (reply != "ok") Console.WriteLine($"Worker {worker.Id} reported {reply}");
            Check(reply == "ok", $"worker {worker.Id}, source {tone.Id}: {command}: {reply}");
        }
        void CheckSound(bool all)
        {
            meter.StandardInput.WriteLine(all ? "measure" : "only-primary");
            string reply = ReadPlaybackReply(meter);
            Check(all ? reply.StartsWith("713 Hz on ", StringComparison.Ordinal) : reply == "713 Hz only on primary", reply);
        }

        Console.WriteLine("Product " + typeof(App).Assembly.Location + " on runtime " + Environment.Version);
        Console.WriteLine("Output IDs " + string.Join(", ", ids));
        using var policy = new AudioPolicyConfig(EDataFlow.eRender);
        string? media = null, console = null;
        bool capturedPolicy = false;
        try
        {
            StartTone();
            media = policy.GetDefaultEndPoint(tone!.Id, ERole.eMultimedia);
            console = policy.GetDefaultEndPoint(tone.Id, ERole.eConsole);
            capturedPolicy = true;
            StartWorker();
            Request("select-three");
            CheckSound(true);
            StopChild(ref worker);
            string saved = Path.Combine(data, "playback-outputs.json");
            using (var json = JsonDocument.Parse(File.ReadAllText(saved)))
            {
                var setting = json.RootElement.EnumerateObject().Single();
                var destinations = setting.Value.GetProperty("Outputs").EnumerateArray().Select(item => item.GetString()!);
                Check(new HashSet<string>(destinations, StringComparer.OrdinalIgnoreCase).SetEquals(ids),
                    "one saved application identity contains all selected endpoints");
            }

            StartWorker();
            Request("await-restored");
            CheckSound(true);
            StopChild(ref worker);
            int oldPid = tone.Id;
            StopChild(ref tone);

            StartWorker();
            StartTone();
            Check(tone!.Id != oldPid, "restarted source has a different PID");
            Request("await-restored");
            CheckSound(true);
            Request("select-one");
            StopChild(ref worker);
            using (var json = JsonDocument.Parse(File.ReadAllText(saved)))
                Check(!json.RootElement.EnumerateObject().Any(), "plain single-output selection removes the persisted multi-output choice");

            StartWorker();
            CheckSound(false);
            Request("assert-single");
            StopChild(ref worker);
        }
        finally
        {
            try { StopChild(ref worker); }
            finally
            {
                try
                {
                    if (capturedPolicy)
                    {
                        if (tone == null) StartTone();
                        Marshal.ThrowExceptionForHR(policy.SetDefaultEndPoint(media, tone!.Id, ERole.eMultimedia));
                        Marshal.ThrowExceptionForHR(policy.SetDefaultEndPoint(console, tone.Id, ERole.eConsole));
                    }
                }
                finally { StopChild(ref tone); meter.StandardInput.Close(); }
            }
        }
    }

    private static int RunRestoreWorker(string data, string outputSet)
    {
        Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", data);
        try
        {
            var app = new ProbeApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            string[] ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(outputSet))!;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var product = typeof(App).Assembly;
            T Method<T>(string name, string method) where T : Delegate =>
                (T)product.GetType("SonicRoute." + name, true)!.GetMethod(method, statics)!.CreateDelegate(typeof(T));
            var start = Method<Action>("AppPlaybackRestore", "Start");
            var stop = Method<Func<Task>>("AppPlaybackRestore", "StopAsync");
            var shutdownMirrors = Method<Func<Task>>("LocalAppOutputMirrors", "ShutdownAsync");
            var get = Method<Func<int, string[]>>("LocalAppOutputMirrors", "GetExtraOutputs");
            var set = Method<Func<AudioAppInfo, string[], string?, Task<(bool Success, int HResult, string Message)>>>("AppPlaybackOutputs", "SetAsync");
            start();
            try
            {
                // In the late-source scenario the initial inventory must have finished before
                // the source starts, so its recovery exercises the real WASAPI notification.
                var initialScan = (Task)product.GetType("SonicRoute.AppPlaybackRestore", true)!.GetField("_worker", statics)!.GetValue(null)!;
                Finish(initialScan);
                Console.WriteLine("ready");
                while (true)
                {
                    var read = Task.Run(Console.ReadLine);
                    Finish(read);
                    if (read.Result == null) break;
                    var command = read.Result.Split(' ');
                    int pid = int.Parse(command[1]);
                    switch (command[0])
                    {
                        case "select-three":
                        case "select-one":
                            var source = AudioService.GetApps(refresh: true).Single(item => item.ProcessId == pid);
                            var selection = set(source, command[0] == "select-three" ? ids : new[] { ids[0] }, ids[0]);
                            Finish(selection);
                            if (!selection.Result.Success) throw new InvalidOperationException(selection.Result.Message);
                            break;
                        case "await-restored":
                            var watch = Stopwatch.StartNew();
                            while (!new HashSet<string>(get(pid), StringComparer.OrdinalIgnoreCase).SetEquals(ids.Skip(1)))
                            {
                                if (watch.Elapsed.TotalSeconds > 14) throw new TimeoutException("Mirrors did not return for source " + pid);
                                Finish(Task.Delay(80));
                            }
                            break;
                        case "assert-single":
                            if (get(pid).Length != 0) throw new InvalidOperationException("The cleared mirrors returned on restart.");
                            break;
                        default: throw new InvalidDataException("Unknown fixture command.");
                    }
                    Console.WriteLine("ok");
                }
            }
            finally
            {
                Finish(stop());
                Finish(shutdownMirrors());
                ConfigService.FlushPendingSaves();
            }
            return 0;
        }
        catch (Exception error) { Console.WriteLine("Error: " + error); return 1; }
    }
}

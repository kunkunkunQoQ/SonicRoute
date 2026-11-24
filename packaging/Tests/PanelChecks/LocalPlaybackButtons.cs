using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static Process StartPlaybackChild(string executable, string args) =>
        Process.Start(new ProcessStartInfo(executable, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
        }) ?? throw new IOException("Cannot start the playback fixture.");

    private static string ReadPlaybackReply(Process child, int timeoutSeconds = 12)
    {
        var read = child.StandardOutput.ReadLineAsync();
        if (!read.Wait(TimeSpan.FromSeconds(timeoutSeconds))) throw new TimeoutException("No reply from playback fixture PID " + child.Id);
        return read.Result ?? throw new EndOfStreamException("Playback fixture PID " + child.Id + " exited.");
    }

    private static Process StartPlaybackMeter(string probeExecutable, string? outputSet)
    {
        // Windows saves audio routing by application identity. The meter and tone
        // must not present the same executable to the policy service.
        string dotnet = Environment.GetEnvironmentVariable("SONICROUTE_CHECK_DOTNET_HOST") ?? "dotnet";
        string meterArgs = $"\"{Path.ChangeExtension(probeExecutable, ".dll")}\" --panel-tone-probe";
        if (outputSet != null) meterArgs += $" \"{outputSet}\"";
        return StartPlaybackChild(dotnet, meterArgs);
    }

    private static void CheckLocalPlaybackButtons(MethodInfo theme, string probeExecutable, string? outputSet)
    {
        theme.Invoke(null, new object[] { "light", "blue" });
        Console.WriteLine("Product " + typeof(App).Assembly.Location + " on runtime " + Environment.Version);
        using var probe = StartPlaybackMeter(probeExecutable, outputSet);
        string[] ids = JsonSerializer.Deserialize<string[]>(ReadPlaybackReply(probe, 40))!;
        Console.WriteLine("Output IDs " + string.Join(", ", ids));
        using var tone = StartPlaybackChild(Path.GetFullPath(probeExecutable), "--mirror-tone " + ids[0]);
        if (ReadPlaybackReply(tone) != "ready") throw new IOException("The tone did not start.");
        var panel = new QuickPanelModernWindow();
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var getExtras = typeof(App).Assembly.GetType("SonicRoute.LocalAppOutputMirrors", true)!
            .GetMethod("GetExtraOutputs", statics)!;
        int pid = tone.Id;
        using var policy = new AudioPolicyConfig(EDataFlow.eRender);
        string? media = policy.GetDefaultEndPoint(pid, ERole.eMultimedia), console = policy.GetDefaultEndPoint(pid, ERole.eConsole);
        try
        {
            var devices = AudioService.GetDevices(EDataFlow.eRender);
            var shown = ids.Select(id => devices.Single(d => d.Id == id)).ToList();
            var config = ConfigService.Load();
            panel.GetType().GetField("_outputDisplay", Instance)!.SetValue(panel, PanelDevices.WithSystemDefault(shown, EDataFlow.eRender, config));
            var source = new AudioAppInfo { ProcessId = (uint)pid, ProcessName = tone.ProcessName, DisplayName = "Hardware probe" };
            Finish((Task)panel.GetType().GetMethod("BuildAppRowsAsync", Instance)!
                .Invoke(panel, new object[] { new List<AudioAppInfo> { source }, config })!);
            var row = Field<IDictionary>(panel, "_rows")[pid]!;
            row.GetType().GetField("Expanded", Instance)!.SetValue(row, true);
            Finish((Task)panel.GetType().GetMethod("BuildRowDevicesAsync", Instance)!.Invoke(panel, new[] { row })!);
            var buttons = Field<List<(Button Button, string Id, EDataFlow Flow)>>(row, "DeviceButtons");
            void Choose(int index, ModifierKeys modifiers, params int[] expected)
            {
                var button = buttons.Single(b => b.Id == ids[index]).Button;
                string? selectedBefore = policy.GetDefaultEndPoint(pid, ERole.eMultimedia);
                ClickWithModifiers(button, modifiers);
                Finish(Field<Task>(row, "PlaybackChange"));
                var target = expected.Select(i => ids[i]).ToArray();
                var activeStyle = panel.FindResource("DevButtonActive");
                var mirrorStyle = panel.FindResource("DevButtonMirror");
                string[] Active() => buttons.Where(b => ReferenceEquals(b.Button.Style, activeStyle) || ReferenceEquals(b.Button.Style, mirrorStyle))
                    .Select(b => b.Id).ToArray();
                int NativeCount() => buttons.Count(b => b.Flow == EDataFlow.eRender && ReferenceEquals(b.Button.Style, activeStyle));
                var elapsed = Stopwatch.StartNew();
                while (!new HashSet<string>(Active()).SetEquals(target) && elapsed.ElapsedMilliseconds < 2000) Finish(Task.Delay(30));
                var active = Active();
                if (!new HashSet<string>(active).SetEquals(target))
                    Console.WriteLine($"FAILURE ROUTE PID {pid} exited={tone.HasExited} before={selectedBefore ?? "default"} after={policy.GetDefaultEndPoint(pid, ERole.eMultimedia) ?? "default"} extras={string.Join(",", (string[])getExtras.Invoke(null, new object[] { pid })!)}");
                Check(new HashSet<string>(active).SetEquals(target) && NativeCount() <= 1,
                    $"{modifiers} click on output {index} highlights [{string.Join(",", active.Select(id => Array.IndexOf(ids, id)))}], expected [{string.Join(",", expected)}]");
            }
            Choose(0, ModifierKeys.None, 0);
            Console.WriteLine("Primary after single click " + (policy.GetDefaultEndPoint(pid, ERole.eMultimedia) ?? "default"));
            MeasureLocalPlayback(1);
            Choose(1, ModifierKeys.Control, 0, 1);
            Choose(1, ModifierKeys.Control, 0);
            Choose(0, ModifierKeys.None, 0);
            Choose(ids.Length - 1, ModifierKeys.Shift, Enumerable.Range(0, ids.Length).ToArray());
            probe.StandardInput.WriteLine("measure");
            var response = ReadPlaybackReply(probe);
            Check(response.StartsWith("713 Hz on ", StringComparison.Ordinal), response);
            MeasureLocalPlayback(ids.Length);
            Choose(0, ModifierKeys.None, 0);
            Check(((string[])getExtras.Invoke(null, new object[] { pid })!).Length == 0,
                "plain click releases all additional renderers");
            Choose(1, ModifierKeys.Control, 0, 1);
        }
        finally
        {
            panel.Close();
            probe.StandardInput.Close();
            if (!tone.HasExited)
            {
                try
                {
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndPoint(media, pid, ERole.eMultimedia));
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndPoint(console, pid, ERole.eConsole));
                }
                finally { tone.StandardInput.Close(); }
                if (!tone.WaitForExit(10000)) throw new TimeoutException("The tone did not exit.");
            }
        }
        var elapsed = Stopwatch.StartNew();
        while (((string[])getExtras.Invoke(null, new object[] { pid })!).Length != 0 && elapsed.ElapsedMilliseconds < 5000)
            Finish(Task.Delay(50));
        Check(((string[])getExtras.Invoke(null, new object[] { pid })!).Length == 0,
            "exiting the source process releases its remaining mirror");
    }

    private static void MeasureLocalPlayback(int outputs)
    {
        // Keep the STA dispatcher available, as it would be in the running quick panel.
        // The tone source and loopback meters run in separate processes.
        Finish(Task.Delay(TimeSpan.FromSeconds(2)));
        using var current = Process.GetCurrentProcess();
        TimeSpan before = current.TotalProcessorTime;
        long privateBefore = current.PrivateMemorySize64;
        long workingBefore = current.WorkingSet64;
        var privateWorkingBefore = PrivateWorkingSet(current);
        var window = Stopwatch.StartNew();
        Finish(Task.Delay(TimeSpan.FromSeconds(15)));
        current.Refresh();
        double cpuMs = (current.TotalProcessorTime - before).TotalMilliseconds;
        Console.WriteLine(FormattableString.Invariant(
            $"METRIC outputs={outputs} elapsedMs={window.Elapsed.TotalMilliseconds:F0} cpuMs={cpuMs:F1} privateStartBytes={privateBefore} privateEndBytes={current.PrivateMemorySize64} workingStartBytes={workingBefore} workingEndBytes={current.WorkingSet64} privateWorkingStartBytes={privateWorkingBefore} privateWorkingEndBytes={PrivateWorkingSet(current)}"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        internal uint Size, PageFaultCount;
        internal UIntPtr PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool,
            Pagefile, PeakPagefile, PrivateUsage, PrivateWorkingSet;
        internal ulong SharedCommit;
    }
    [DllImport("psapi.dll")]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx2 counters, uint size);

    private static string PrivateWorkingSet(Process process)
    {
        var counters = new ProcessMemoryCountersEx2 { Size = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx2)) };
        return GetProcessMemoryInfo(process.Handle, ref counters, counters.Size) && counters.PrivateWorkingSet.ToUInt64() > 0
            ? counters.PrivateWorkingSet.ToUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable";
    }

    // Change this thread's keyboard state so the production Click handler reads WPF modifiers.
    private static void ClickWithModifiers(Button button, ModifierKeys modifiers)
    {
        byte[] saved = new byte[256];
        if (!GetKeyboardState(saved)) throw new IOException("Cannot read the test thread keyboard state.");
        try
        {
            var keys = new byte[256];
            if ((modifiers & ModifierKeys.Control) != 0) keys[0x11] = keys[0xa2] = 0x80;
            if ((modifiers & ModifierKeys.Shift) != 0) keys[0x10] = keys[0xa0] = 0x80;
            if (!SetKeyboardState(keys)) throw new IOException("Cannot set the keyboard state on the private desktop.");
            if (Keyboard.Modifiers != modifiers) throw new IOException($"WPF reported {Keyboard.Modifiers} on the private desktop; expected {modifiers}.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { SetKeyboardState(saved); }
    }

    [DllImport("user32.dll")] private static extern bool GetKeyboardState([Out] byte[] keys);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] keys);
}

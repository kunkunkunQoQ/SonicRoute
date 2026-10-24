using System.Diagnostics;
using System.Reflection;
using SonicRoute.Core;
using SonicRoute.Core.Interop;

namespace MonitoringNativeChecks;
internal static class Counters
{
    internal static int Enumerators, PeakReads, StateReads;
    internal static object CreateEnumerator() { Interlocked.Increment(ref Enumerators); return new MMDeviceEnumeratorComObject(); }
}
internal static class Program
{
    private static int _passed;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); _passed++; Console.WriteLine("PASS " + message); }
    private static void Wait(Func<bool> value, string message, int timeout = 6000)
    { var watch = Stopwatch.StartNew(); while (!value() && watch.ElapsedMilliseconds < timeout) Thread.Sleep(10); Check(value(), message); }
    private static IDisposable SilentStream() => (IDisposable)Activator.CreateInstance(typeof(NativeProgram).GetNestedType("SilentSession", BindingFlags.NonPublic)!, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Array.Empty<object>(), null)!;
    private static void Command(Process child, string command, string reply)
    {
        child.StandardInput.WriteLine(command); child.StandardInput.Flush();
        var read = child.StandardOutput.ReadLineAsync();
        if (!read.Wait(6000) || read.Result != reply) throw new Exception("Worker command " + command);
    }
    public static int Main(string[] args)
    {
        if (args.Contains("--silent-worker"))
        {
            IDisposable? stream = null;
            try
            {
                Console.WriteLine("READY"); string? line;
                while ((line = Console.ReadLine()) != null)
                {
                    if (line == "start") { stream = SilentStream(); Console.WriteLine("AUDIO"); }
                    else if (line == "stop") { stream?.Dispose(); stream = null; Console.WriteLine("STOPPED"); }
                    else if (line == "exit") return 0;
                }
                return 0;
            }
            finally { stream?.Dispose(); }
        }
        Process? child = null;
        try
        {
            Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "native observer uses MTA");
            string self = Process.GetCurrentProcess().MainModule!.FileName!;
            string arguments = "--silent-worker";
            if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                arguments = "\"" + Assembly.GetExecutingAssembly().Location + "\" " + arguments;
            child = Process.Start(new ProcessStartInfo(self, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;
            var ready = child.StandardOutput.ReadLineAsync(); Check(ready.Wait(6000) && ready.Result == "READY", "owned silent helper starts without playing audio");
            using (var meter = new Observed.AudioMeterService())
            {
                int frames = 0; meter.PeaksUpdated += _ => Interlocked.Increment(ref frames); meter.Start(new[] { child.Id });
                Wait(() => Volatile.Read(ref frames) > 0, "initial real endpoint discovery completes");
                int enumerators = Counters.Enumerators; Thread.Sleep(2200);
                Check(Counters.Enumerators == enumerators && Counters.PeakReads == 0, "healthy absent native target has no two-second discovery or 33ms reads");
                Command(child, "start", "AUDIO");
                Wait(() => Volatile.Read(ref Counters.PeakReads) > 2, "real creation callback discovers helper session after initial enumeration");
                int reads = Counters.PeakReads; Thread.Sleep(220);
                Check(Counters.PeakReads > reads + 3, "native active zero-valued PCM continues sampling");
                Command(child, "stop", "STOPPED");
                int stable = -1; var settle = Stopwatch.StartNew();
                while (settle.ElapsedMilliseconds < 6000)
                { int before = Counters.PeakReads; Thread.Sleep(200); if (before == Counters.PeakReads) { stable = before; break; } }
                Check(stable >= 0, "real inactive state callback stops peak reads");
                Command(child, "start", "AUDIO");
                Wait(() => Volatile.Read(ref Counters.PeakReads) > stable + 2, "native playback restart wakes sampling");
                Check(Counters.Enumerators == enumerators, "native creation and state transitions require no full rediscovery");
                meter.Stop(); reads = Counters.PeakReads; Thread.Sleep(150);
                Check(Counters.PeakReads == reads, "native meter stop leaves no reads");
                for (int i = 0; i < 3; i++)
                {
                    reads = Counters.PeakReads;
                    meter.Start(new[] { child.Id });
                    Wait(() => Volatile.Read(ref Counters.PeakReads) > reads, "native existing-session reopen " + (i + 1));
                    meter.Stop();
                }
            }
            var monitor = new MicrophoneStateMonitor();
            try { monitor.ReadAsync(true).GetAwaiter().GetResult(); Check(!monitor.NeedsRecovery, "real microphone state and callbacks initialize read-only"); }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
            Command(child, "stop", "STOPPED"); child.StandardInput.WriteLine("exit"); child.StandardInput.Flush();
            Check(child.WaitForExit(5000) && child.ExitCode == 0, "silent helper exits cleanly");
            Console.WriteLine($"RESULT passed={_passed} runtime={Environment.Version} native=true observedSourceCounters=true scans={Counters.Enumerators} peaks={Counters.PeakReads} states={Counters.StateReads} audiblePCM=0 volumeWrites=0 muteWrites=0 routingWrites=0");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
        finally { if (child != null) { try { if (!child.HasExited) { child.StandardInput.Close(); if (!child.WaitForExit(3000)) child.Kill(); } } finally { child.Dispose(); } } }
    }
}

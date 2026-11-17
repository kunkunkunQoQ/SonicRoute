using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Compat;

internal static partial class Program
{
    private static Type RestartType => typeof(App).Assembly.GetType("SonicRoute.RestartCoordinator")!;
    private static Task<bool> StartReplacement(Func<ProcessStartInfo, Process?> starter, int timeout = 2000) =>
        (Task<bool>)RestartType.GetMethod("TryStartAsync", StaticHidden)!.Invoke(null,
            new object[] { AppInfo.ExecutablePath!, starter, timeout })!;

    private static int RestartHelper(string output, string[] args)
    {
        if (args[1] == "--restart-exit") return 2;
        if (args[1] == "--restart-no-ready") { Thread.Sleep(350); return 2; }
        string nonce = args[2]; string name = @"Local\SonicRoute.RestartCheck." + nonce;
        if (args[1] == "--restart-parent")
        {
            using var mutex = new Mutex(true, name, out _);
            bool started = StartReplacement(info => Process.Start(HelperStart(Path.Combine(output, "child"),
                "--restart-child", nonce + " " + info.Arguments))).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(output, "parent.json"), JsonSerializer.Serialize(new { ReadyBeforeExit = started }));
            mutex.ReleaseMutex();
            return started ? 0 : 1;
        }
        object[] arguments = { args.Skip(3).ToArray() };
        bool handedOff = (bool)RestartType.GetMethod("CompleteHandoff", StaticHidden)!.Invoke(null, arguments)!;
        using var inherited = new Mutex(false, name);
        bool owned;
        try { owned = inherited.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (owned) inherited.ReleaseMutex();
        File.WriteAllText(Path.Combine(output, "child.json"), JsonSerializer.Serialize(new
        { Handoff = handedOff, MutexAvailable = owned, InternalArgumentsStripped = ((string[])arguments[0]).Length == 0 }));
        return handedOff && owned ? 0 : 1;
    }

    private static void CheckRestart(string output)
    {
        var missing = StartReplacement(_ => throw new Win32Exception("Synthetic start failure")); Pump(missing);
        Check(!missing.Result, "a Process.Start exception keeps the old process eligible to continue running");
        var absent = StartReplacement(_ => null); Pump(absent);
        Check(!absent.Result, "a null process result is not treated as successful restart");
        int earlyId = 0;
        var died = StartReplacement(_ =>
        {
            var process = Process.Start(HelperStart(Path.Combine(output, "early-exit"), "--restart-exit"))!;
            earlyId = process.Id; return process;
        });
        Pump(died);
        Check(!died.Result && WaitHelperExit(earlyId), "a child that exits before acknowledgement cannot cause old-process shutdown");
        int quietId = 0;
        var timed = StartReplacement(_ =>
        {
            var process = Process.Start(HelperStart(Path.Combine(output, "no-ready"), "--restart-no-ready"))!;
            quietId = process.Id; return process;
        }, 80);
        Pump(timed);
        Check(!timed.Result, "restart readiness has an operation-scoped deadline without an idle watcher");
        WaitHelperExit(quietId);
        string parentOutput = Path.Combine(output, "handoff");
        using var parent = Process.Start(HelperStart(parentOutput, "--restart-parent", Guid.NewGuid().ToString("N")))!;
        Pump(Task.Run(() => { if (!parent.WaitForExit(7000)) throw new TimeoutException("Synthetic restart parent timed out"); }));
        string childResult = Path.Combine(parentOutput, "child", "child.json");
        Pump(WaitForFile(childResult));
        using var json = JsonDocument.Parse(File.ReadAllText(childResult));
        Check(parent.ExitCode == 0 && json.RootElement.GetProperty("Handoff").GetBoolean()
            && json.RootElement.GetProperty("MutexAvailable").GetBoolean()
            && json.RootElement.GetProperty("InternalArgumentsStripped").GetBoolean(),
            "real helper processes acknowledge readiness, release the old singleton and start with normal arguments");
    }

    private static bool WaitHelperExit(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.WaitForExit(2500); }
        catch (ArgumentException) { return true; }
    }

    private static async Task WaitForFile(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (deadline.ElapsedMilliseconds > 4000) throw new TimeoutException("Helper result was not written");
            await Task.Delay(30);
        }
    }

    private static void CheckDiagnostics(ProbeApp app, string output)
    {
        ExceptionDiagnostics.InstallProcessHandlers(); ExceptionDiagnostics.InstallProcessHandlers();
        bool seen = false, wasUnhandled = false;
        DispatcherUnhandledExceptionEventHandler controlled = (_, e) =>
        {
            if (e.Exception.Message != "synthetic-dispatcher-probe") return;
            wasUnhandled = !e.Handled; seen = true; e.Handled = true; // This test handler alone keeps the fixture alive.
        };
        app.DispatcherUnhandledException += controlled;
        app.Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("synthetic-dispatcher-probe")));
        Pump(app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task);
        app.DispatcherUnhandledException -= controlled;
        Check(seen && wasUnhandled && File.ReadAllText(ExceptionDiagnostics.LogPath).Contains("synthetic-dispatcher-probe"),
            "the real App dispatcher handler records errors without declaring unsafe exceptions handled");
        for (int i = 0; i < 35; i++) ExceptionDiagnostics.Record("size-probe", new InvalidOperationException(new string('x', 40000)));
        Check(File.Exists(ExceptionDiagnostics.LogPath + ".1") && new FileInfo(ExceptionDiagnostics.LogPath).Length < 1100000
            && new FileInfo(ExceptionDiagnostics.LogPath + ".1").Length < 1100000,
            "diagnostic entries and retained log rotation have bounded size");
        string fatal = Path.Combine(output, "fatal");
        using var child = Process.Start(HelperStart(fatal, "--fatal-probe"))!;
        Pump(Task.Run(() => { if (!child.WaitForExit(5000)) throw new TimeoutException("Synthetic fatal helper did not exit"); }));
        string log = Path.Combine(fatal, "isolated-data", "Logs", "diagnostics.log");
        Check(child.ExitCode != 0 && File.Exists(log) && File.ReadAllText(log).Contains("synthetic-fatal-probe"),
            "an actual unhandled worker-thread exception writes diagnostics before its isolated process terminates");
    }
}

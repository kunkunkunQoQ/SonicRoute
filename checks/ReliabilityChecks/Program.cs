using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Compat;

internal static partial class Program
{
    private static int _checks;
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags StaticHidden = BindingFlags.Static | BindingFlags.NonPublic;
    private static void Check(bool ok, string name)
    { if (!ok) throw new InvalidOperationException("FAIL " + name); ++_checks; Console.WriteLine("PASS " + name); }
    private sealed class ProbeApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", Path.Combine(output, "isolated-data"));
        SetErrorMode(3); // Suppress OS error boxes in this test process and its helpers only.
        if (args.Length > 1 && args[1] == "--fatal-probe")
        {
            ExceptionDiagnostics.InstallProcessHandlers();
            var thread = new Thread(() => throw new InvalidOperationException("synthetic-fatal-probe"));
            thread.Start(); thread.Join(); return 5;
        }
        using var log = new StreamWriter(Path.Combine(output, "console.log")) { AutoFlush = true };
        Console.SetOut(log); Console.SetError(log);
        try
        {
            if (args.Length > 1 && args[1].StartsWith("--restart-", StringComparison.Ordinal)) return RestartHelper(output, args);
            CheckConfiguration();
            var app = new ProbeApp { Resources = ProductResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            CheckIcons(); CheckMouseBindings(); CheckDiagnostics(app, output); CheckRestart(output);
            File.WriteAllText(Path.Combine(output, "reliability.json"), JsonSerializer.Serialize(new
            { Success = true, Assertions = _checks, ProductStartup = false, RealPhone = false, UserKeys = false,
                RealAudioWrites = false, UserProcessesStopped = false, PrivateDesktop = true }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }

    private static ProcessStartInfo HelperStart(string output, string mode, string extra = "")
    {
        string assembly = Assembly.GetExecutingAssembly().Location;
#if NET48
        return new ProcessStartInfo(assembly, "\"" + output + "\" " + mode + " " + extra) { UseShellExecute = false, CreateNoWindow = true };
#else
        return new ProcessStartInfo(AppInfo.ExecutablePath!, "\"" + assembly + "\" \"" + output + "\" " + mode + " " + extra) { UseShellExecute = false, CreateNoWindow = true };
#endif
    }

    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
}

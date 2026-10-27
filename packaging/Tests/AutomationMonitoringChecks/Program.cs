using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static class Program
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly string Target = "SRA" + Guid.NewGuid().ToString("N").Substring(0, 8);
    private static readonly List<Process> Children = new();
    private static int _passed;
    private static object? Field(string name) => typeof(AutoRuleService).GetField(name, Flags)!.GetValue(null);
    private static void Set(string name, object value) => typeof(AutoRuleService).GetField(name, Flags)!.SetValue(null, value);
    private static void Recover() => typeof(AutoRuleService).GetMethod("TryRecoverProcessEvents", Flags)!.Invoke(null, null);
    private static ProcessChangeSource Source => (ProcessChangeSource)Field("_processEvents")!;
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); _passed++; Console.WriteLine("PASS " + text); }
    private static async Task Wait(Func<bool> test, string text, int timeout = 5000)
    { var watch = Stopwatch.StartNew(); while (!test()) { if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException(text); await Task.Delay(10); } }
    private static int Writes(int volume) => SonicRoute.SystemVolumeService.Volumes.Count(v => v.Value == volume);
    private static AutoRule Rule(AutoRuleTrigger trigger, int volume)
    {
        var rule = new AutoRule { Name = "Monitoring " + trigger, Enabled = true, Trigger = trigger, TriggerApp = Target };
        rule.SetSteps(new[] { new AutoRuleStep { Action = AutoRuleAction.SetSystemVolume, Volume = volume } });
        AutoRuleStore.Save(rule); return rule;
    }
    private static async Task<Process> Child()
    {
        string self = Process.GetCurrentProcess().MainModule!.FileName!;
        string? runtimeRoot = null;
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            runtimeRoot = Path.GetDirectoryName(self);
            self = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
        }
        string alias = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Target + ".exe");
        if (!File.Exists(alias)) File.Copy(self, alias);
        var start = new ProcessStartInfo(alias, "--worker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        if (runtimeRoot != null) start.EnvironmentVariables["DOTNET_ROOT"] = runtimeRoot;
        var child = Process.Start(start)!;
        Children.Add(child);
        var ready = child.StandardOutput.ReadLineAsync();
        if (await Task.WhenAny(ready, Task.Delay(5000)) != ready || await ready != "READY") throw new Exception("Child startup");
        return child;
    }
    private static async Task Exit(Process child) { child.StandardInput.WriteLine("exit"); child.StandardInput.Flush(); await Wait(() => child.HasExited, "child exit"); }
    private static async Task Run()
    {
        AutoRuleService.RefreshWatcher();
        Check(Field("_processEvents") == null && Field("_watchFallbackTimer") == null, "no rules have no process source or timer");
        _passed += await HotkeyChecks.Run();
        var start = Rule(AutoRuleTrigger.AppStart, 21); var exit = Rule(AutoRuleTrigger.AppExit, 22);
        AutoRuleService.RefreshWatcher(); await Wait(() => Field("_snapshotBaselineTask") == null && !(bool)Field("_watchTickRunning")!, "baseline");
        Check(Field("_watchFallbackTimer") == null && Writes(21) == 0 && Writes(22) == 0, "healthy events establish baseline without false rules or fallback");
        var first = Source; first.IsAvailable = false; first.Emit(null);
        await Wait(() => Field("_watchFallbackTimer") is DispatcherTimer { IsEnabled: true }, "fallback enabled");
        Check(((DispatcherTimer)Field("_watchFallbackTimer")!).Interval == TimeSpan.FromSeconds(1), "process fallback interval remains exactly one second");
        Set("_processEventRetryAt", long.MaxValue);
        var child = await Child(); await Wait(() => Writes(21) == 1, "fallback start"); await Exit(child); await Wait(() => Writes(22) == 1, "fallback exit");
        Check(Writes(21) == 1 && Writes(22) == 1, "fallback detects real owned process start and last exit without event assistance");

        ProcessChangeSource.AvailableOnStart = false; first.DisposalGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int created = ProcessChangeSource.Created; Set("_processEventRetryAt", 0L); Recover();
        for (int i = 0; i < 20; i++) Recover();
        Check((bool)Field("_processEventRecoveryPending")! && ProcessChangeSource.Created == created, "one recovery waits for old disposal and coalesces duplicate requests");
        first.DisposalGate.SetResult(true); await Wait(() => !ReferenceEquals(Source, first), "replacement");
        await Wait(() => !(bool)Field("_processEventRecoveryPending")!, "recovery settled");
        Check(!Source.IsAvailable && (int)Field("_processEventRetryDelay")! == 4000 && Field("_watchFallbackTimer") is DispatcherTimer { IsEnabled: true }, "failed replacement retains one-second snapshots with four-second subscription backoff");
        created = ProcessChangeSource.Created; Recover(); Check(ProcessChangeSource.Created == created, "subscription backoff prevents immediate repeated registration");
        first.EmitRetired(); Check((int)Field("_processNotificationQueued")! == 0, "retired source callback cannot queue new detection");

        ProcessChangeSource.AvailableOnStart = true; Set("_processEventRetryAt", 0L); Recover();
        await Wait(() => Source.IsAvailable && Field("_watchFallbackTimer") == null, "events restored");
        Check((int)Field("_processEventRetryDelay")! == 2000 && Writes(21) == 1 && Writes(22) == 1, "event recovery stops fallback and resets backoff without rebaselining false triggers");
        var config = ConfigService.Load(); config.BackgroundPollingFallbackEnabled = true; AutoRuleService.RefreshWatcher();
        created = ProcessChangeSource.Created; await Task.Delay(1150);
        Check(Field("_watchFallbackTimer") is DispatcherTimer { IsEnabled: true } && ProcessChangeSource.Created == created, "explicit compatibility keeps one-second timer without rebuilding healthy WMI");
        config.BackgroundPollingFallbackEnabled = false; AutoRuleService.RefreshWatcher();
        Check(Field("_watchFallbackTimer") == null, "turning compatibility off restores event-only operation");

        var retiring = Source; retiring.IsAvailable = false; retiring.DisposalGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        retiring.Emit(null); await Wait(() => Field("_watchFallbackTimer") != null, "second failure"); Set("_processEventRetryAt", 0L); Recover();
        start.Enabled = exit.Enabled = false; AutoRuleStore.Save(start); AutoRuleStore.Save(exit); AutoRuleService.RefreshWatcher(); created = ProcessChangeSource.Created;
        retiring.DisposalGate.SetResult(true); await Wait(() => !(bool)Field("_processEventRecoveryPending")!, "disabled recovery completion");
        Check(Field("_processEvents") == null && Field("_processSnapshot") == null && Field("_watchFallbackTimer") == null && ProcessChangeSource.Created == created, "disabling rules while disposal pending prevents orphan subscriptions or timers");

        start.Enabled = exit.Enabled = true; start.SetSteps(new[] { new AutoRuleStep { Action = AutoRuleAction.SetSystemVolume, Volume = 31, DelayMs = 60000 } });
        AutoRuleStore.Save(start); AutoRuleStore.Save(exit); AutoRuleService.RefreshWatcher();
        await Wait(() => Field("_snapshotBaselineTask") == null && !(bool)Field("_watchTickRunning")!, "second baseline");
        var delayed = await Child(); Source.Emit(Target); await Wait(() => AutoRuleService.RunningRuleIds().Contains(start.Id), "long rule started");
        await Exit(delayed); Source.Emit(Target);
        await Wait(() => Field("_runningSnapshot") is HashSet<string> snapshot && !snapshot.Contains(Target) && (int)Field("_processNotificationQueued")! == 0, "snapshot advanced during delay");
        var queue = (System.Collections.ICollection)Field("_watchRequests")!;
        Check(queue.Count == 1 && Writes(31) == 0 && Writes(22) == 1, "60-second step does not block exit detection and preserves sequential execution queue");
        exit.Enabled = false; AutoRuleStore.Save(exit); AutoRuleService.RefreshWatcher();
        AutoRuleService.Shutdown();
        await Wait(() => !AutoRuleService.RunningRuleIds().Contains(start.Id), "execution canceled");
        Check(Writes(31) == 0 && Writes(22) == 1 && Field("_watchFallbackTimer") == null, "shutdown cancels long step and clears disabled pending rule without writes");
    }
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--worker")) { Console.WriteLine("READY"); Console.ReadLine(); return 0; }
        Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "isolated-" + Guid.NewGuid().ToString("N")));
        var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            async Task RunHotkeys() { _passed += await HotkeyChecks.Run(); }
            async Task RunInputMute() { _passed += await InputMuteChecks.Run(); }
            bool focused = args.Contains("--hotkeys-only") || args.Contains("--app-input-only");
            var run = args.Contains("--app-input-only") ? RunInputMute() : args.Contains("--hotkeys-only") ? RunHotkeys() : Run(); var frame = new DispatcherFrame();
            run.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame); run.GetAwaiter().GetResult();
            Console.WriteLine($"RESULT passed={_passed} runtime={Environment.Version} AUDIO_WRITES=FAKES_ONLY PROCESS_CHILDREN={(focused ? "NONE" : "REAL")}"); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
        finally
        {
            AutoRuleService.Shutdown(); CurrentAppService.StopForegroundWatcher(); AutoRuleStore.Shutdown();
            foreach (var child in Children) { try { if (!child.HasExited) { child.StandardInput.Close(); if (!child.WaitForExit(2000)) child.Kill(); } } finally { child.Dispose(); } }
        }
    }
}

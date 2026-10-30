using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void CheckAbcde(MethodInfo theme, List<Window> windows)
    {
        theme.Invoke(null, new object[] { "light", "#00D5DD" });
        void Set(object target, string name, object? value) => target.GetType().GetField(name, Instance)!.SetValue(target, value);
        var writerType = typeof(App).Assembly.GetType("SonicRoute.CoalescedVolumeWriter")!;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var writes = new ConcurrentQueue<(int Pid, int Value)>();
        int callbacks = 0;
        Func<int, int, (bool, int)> write = (pid, value) =>
        {
            writes.Enqueue((pid, value));
            if (value == 1) { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException(); }
            return (true, value);
        };
        object writer = Activator.CreateInstance(writerType, Instance, null, new object[] { Dispatcher.CurrentDispatcher, write }, null)!;
        var enqueue = writerType.GetMethod("Enqueue", Instance)!;
        void Add(int pid, int value) => enqueue.Invoke(writer, new object[] { pid, value, (Action<int, int, bool, int>)((_, _, _, _) => callbacks++) });
        void Flush() => writerType.GetMethod("Flush", Instance)!.Invoke(writer, null);
        try
        {
            Add(10, 1); Check(entered.Wait(3000), "full-window volume first input starts immediately");
            Check((bool)writerType.GetMethod("HasPending", Instance)!.Invoke(writer, new object[] { 10 })!, "slow write remains pending for periodic UI read protection");
            for (int i = 2; i <= 100; i++) { Add(10, i); Flush(); }
            Add(20, 73); Flush();
            Check(writes.Count == 1, "blocked full-window writer keeps later input out of native calls");
            ((IDisposable)writer).Dispose(); release.Set();
            Finish((Task)writerType.GetProperty("Completion", Instance)!.GetValue(writer)!); DrainUi();
            Check(writes.Where(w => w.Pid == 10).Select(w => w.Value).SequenceEqual(new[] { 1, 100 }) && writes.Last(w => w.Pid == 20).Value == 73,
                "full-window writes coalesce absolute values per PID and commit final values after close");
            Check(callbacks == 0 && !Field<DispatcherTimer>(writer, "_timer").IsEnabled, "closed writer suppresses readback and stops timer");
            Check(!(bool)writerType.GetMethod("HasPending", Instance)!.Invoke(writer, new object[] { 10 })!, "completed final write clears pending demand");
            Console.WriteLine("FULL_VOLUME_METRIC inputs=101 pids=2 nativeWrites=3 disposedCallbacks=0");
        }
        finally { release.Set(); ((IDisposable)writer).Dispose(); }

        var panel = new QuickPanelModernWindow(); windows.Add(panel);
        var queued = Field<Dictionary<int, int>>(panel, "_pendingVolume");
        var latest = Field<ConcurrentDictionary<int, int>>(panel, "_latestAppWrites");
        var panelWrites = new ConcurrentQueue<(int Pid, int Value)>();
        using var panelEntered = new ManualResetEventSlim(); using var panelRelease = new ManualResetEventSlim();
        Set(panel, "_writeAppVolume", (Func<int, int, bool>)((pid, value) =>
        {
            panelWrites.Enqueue((pid, value));
            if (value == 1) { panelEntered.Set(); if (!panelRelease.Wait(5000)) throw new TimeoutException(); }
            return true;
        }));
        void PanelAdd(int pid, int value, int sequence) { latest[pid] = sequence; queued[pid] = value; Call(panel, "FlushPendingVolumes"); }
        try
        {
            PanelAdd(30, 1, 1); Check(panelEntered.Wait(3000), "quick-panel slow write enters fake boundary");
            for (int i = 2; i <= 50; i++) PanelAdd(30, i, i);
            PanelAdd(40, 91, 51); panel.Close(); panelRelease.Set(); Finish(Field<Task>(panel, "_volumeWriteTask"));
            Check(panelWrites.Where(w => w.Pid == 30).Select(w => w.Value).SequenceEqual(new[] { 1, 50 }) && panelWrites.Any(w => w.Pid == 40 && w.Value == 91),
                "quick-panel skips queued obsolete app values and keeps other PID plus close final value");
            Console.WriteLine("QUICK_APP_WRITE_METRIC inputs=51 pids=2 nativeWrites=3");
        }
        finally { panelRelease.Set(); panel.Close(); }

        foreach (bool compact in new[] { true, false })
        {
            var rowsPanel = new QuickPanelModernWindow(); windows.Add(rowsPanel);
            var cfg = ConfigService.Load(); cfg.QuickPanelUseLegacyAppRows = compact;
            cfg.QuickPanelShowOutputName = cfg.QuickPanelCompactShowOutputName = false;
            bool muteKnown = true, muted = true;
            Set(rowsPanel, "_readPanelState", (Func<IEnumerable<int>, (Dictionary<int, (int Volume, bool Muted, bool MuteKnown)> Apps, bool? AllMuted)>)
                (pids => (pids.ToDictionary(pid => pid, _ => (50, muted, muteKnown)), muteKnown ? muted : null)));
            var apps = new List<AudioAppInfo> { new() { ProcessId = 1010, ProcessName = "fixture-a" }, new() { ProcessId = 1011, ProcessName = "fixture-b" } };
            void Build() => Finish((Task)rowsPanel.GetType().GetMethod("BuildAppRowsAsync", Instance)!.Invoke(rowsPanel, new object[] { apps, cfg })!);
            Build(); var rows = Field<IDictionary>(rowsPanel, "_rows"); object a = rows[1010]!, b = rows[1011]!;
            apps.Insert(1, new AudioAppInfo { ProcessId = 1012, ProcessName = "fixture-c" }); Build();
            Check(ReferenceEquals(a, rows[1010]) && ReferenceEquals(b, rows[1011]) && rows.Count == 3,
                "adding an app reuses both existing rows: compact=" + compact);
            apps.RemoveAt(1); Build();
            Check(ReferenceEquals(a, rows[1010]) && ReferenceEquals(b, rows[1011]) && rows.Count == 2,
                "removing an app preserves unaffected rows: compact=" + compact);
            apps[0] = new AudioAppInfo { ProcessId = 1010, ProcessName = "reused-pid" }; Build();
            Check(!ReferenceEquals(a, rows[1010]) && ReferenceEquals(b, rows[1011]), "PID identity replacement rebuilds only its own row");
            muteKnown = false; muted = false; Build();
            var muteIcon = Field<Button>(b, "IconButton"); muteIcon.ApplyTemplate();
            Check(Field<bool>(b, "IsMuted") && !Field<Button>(b, "IconButton").IsEnabled && Field<Slider>(b, "Slider").IsEnabled,
                "unknown mute preserves prior visual and disables only mute while volume stays available");
            Check(muteIcon.Opacity == .5 && ToolTipService.GetShowOnDisabled(muteIcon)
                && Equals(muteIcon.ToolTip, L10n.T("Tray.AudioUnavailable"))
                && System.Windows.Automation.AutomationProperties.GetName(muteIcon).Contains("fixture-b")
                && System.Windows.Automation.AutomationProperties.GetName(Field<System.Windows.Controls.Primitives.ToggleButton>(b, "ExpandButton")).Contains("fixture-b"),
                "unknown mute has a visible disabled cue, tooltip, and readable icon and expand names");
            rowsPanel.Close();
        }
        bool whiteForeground = true;
        foreach (string mode in new[] { "light", "dark" })
        foreach (string accent in new[] { "#00FFFF", "#FFFFFF", "#000000" })
        {
            theme.Invoke(null, new object[] { mode, accent });
            whiteForeground &= ((SolidColorBrush)Application.Current.Resources["Theme.OnAccent"]).Color == Colors.White;
        }
        Check(whiteForeground, "shared accent foreground preserves v1.29 white styling across themes");

        var log = typeof(AutoRuleScheduler).GetMethod("Log", BindingFlags.Static | BindingFlags.NonPublic)!;
        object logWrite = typeof(AutoRuleScheduler).GetField("_logWriteGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        lock (logWrite)
            for (int i = 0; i < 500; i++) log.Invoke(null, new object[] { "abcde-log-" + i.ToString("D4") });
        typeof(AutoRuleScheduler).GetMethod("FlushLogs", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        string diagnostic = System.IO.File.ReadAllText(System.IO.Path.Combine(AppDataRoot.DirectoryPath, "scheduler.log"));
        Check(Enumerable.Range(0, 500).All(i => diagnostic.Contains("abcde-log-" + i.ToString("D4")))
            && ((Task)typeof(AutoRuleScheduler).GetField("_logDrain", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).IsCompleted,
            "diagnostic log buffers slow disk work, flushes all records and leaves no idle worker");
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

// Test module only. The runner must suppress startup, redirect storage and block
// every native audio write in BOTH copies before invoking this module.
internal static class V121UiCases
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static object? Get(object target, string name) => target.GetType().GetField(name, Instance)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Instance)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Instance)!.Invoke(target, args);

    public static void Run(Action<object> add, Action<int> pump, Func<long> allocated)
    {
        if (Environment.GetEnvironmentVariable("SONICROUTE_V121_READ_ONLY") != "1")
            throw new InvalidOperationException("Audio write protection must be active");
        string safeRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data")) + Path.DirectorySeparatorChar;
        string rulesDir = Path.GetFullPath(AutoRuleStore.RulesDir);
        if (!rulesDir.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Rule fixture directory must be inside test-data");
        Directory.CreateDirectory(rulesDir);
        foreach (string file in Directory.GetFiles(rulesDir, "*.json")) File.Delete(file);
        var rules = Enumerable.Range(0, 100).Select(i => new AutoRule
        {
            Id = "v121-ui-" + i.ToString("D3"), Name = "V121 UI " + i.ToString("D3"), Enabled = false,
            Actions = Enumerable.Range(0, 8).Select(_ => new AutoRuleStep
            { Action = AutoRuleAction.ShowOsd, OsdTitle = "Validation", OsdText = "UI fixture" }).ToList()
        }).ToList();
        foreach (var rule in rules)
            File.WriteAllText(Path.Combine(rulesDir, rule.Name + ".json"), JsonSerializer.Serialize(rule));
        AutoRuleStore.Invalidate();

        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        // Deliberately no Show(): these tests measure logical row construction /
        // Dispatcher allocations, not normal on-screen rendering or page startup.
        var page = (FrameworkElement)main.FindName("AutomationPage");
        page.Visibility = Visibility.Visible;
        var items = (ItemsControl)main.FindName("AutoRuleList");
        void Await(Task task)
        {
            var timeout = Stopwatch.StartNew();
            while (!task.IsCompleted && timeout.ElapsedMilliseconds < 10000) pump(10);
            if (!task.IsCompleted) throw new TimeoutException("UI refresh did not complete in 10 seconds");
            task.GetAwaiter().GetResult();
        }
        void Refresh() => Await((Task)Call(main, "RefreshAutoRulesAsync")!);
        object[] Rows() => items.Items.Cast<object>().ToArray();
        void MeasureRefresh(string name, object[] before)
        {
            long startAllocation = allocated();
            var watch = Stopwatch.StartNew();
            Refresh();
            watch.Stop();
            var after = Rows();
            int reused = before.Count(row => after.Any(next => ReferenceEquals(row, next)));
            add(new { Case = name, Rows = after.Length, ReusedRows = reused,
                CompletionMs = watch.Elapsed.TotalMilliseconds,
                UiThreadAllocatedBytes = startAllocation < 0 ? -1 : allocated() - startAllocation,
                LogicalUiOnly = true, DispatcherPumpSliceMs = 10 });
        }
        Refresh();
        var initial = Rows();
        add(new { Case = "ui-initial-100-rules", Rows = initial.Length, Pass = initial.Length == 100, LogicalUiOnly = true });
        MeasureRefresh("ui-100-rules-unchanged-refresh", initial);
        var runIds = (HashSet<string>)Get(main, "_autoRunIds")!;
        runIds.Add(rules[0].Id);
        MeasureRefresh("ui-100-rules-running-status-refresh", initial);
        var runningRows = Rows();
        bool candidateRows = typeof(MainWindow).GetField("_autoRows", Instance) != null;
        bool? runningButtonCorrect = null;
        if (candidateRows)
        {
            var rowMap = (IDictionary)Get(main, "_autoRows")!;
            object row = rowMap[rules[0].Id]!;
            var run = (Button)Get(row, "Run")!;
            runningButtonCorrect = !run.IsEnabled && Equals(run.Content, L10n.T("Auto.Running"));
        }
        add(new { Case = "ui-running-button-state", CandidateRowMapAvailable = candidateRows,
            RunningButtonCorrect = runningButtonCorrect,
            Pass = !candidateRows || runningButtonCorrect == true });
        runIds.Clear();
        Refresh();
        var beforeEdit = Rows();
        var edited = AutoRuleStore.Find(rules[50].Id)!;
        edited.Name += " renamed";
        AutoRuleStore.Save(edited);
        MeasureRefresh("ui-100-rules-one-rule-change", beforeEdit);
        var afterEdit = Rows();
        int retained = beforeEdit.Count(row => afterEdit.Any(next => ReferenceEquals(row, next)));
        add(new { Case = "ui-row-reuse-contract", IsCandidate = candidateRows,
            RunningReused = initial.Count(row => runningRows.Any(next => ReferenceEquals(row, next))),
            OneRuleChangeReused = retained,
            Pass = !candidateRows || (initial.Length == 100 && initial.All(row => runningRows.Any(next => ReferenceEquals(row, next))) && retained == 99) });

        var config = (AppConfig)Get(main, "_config")!;
        var saveCount = typeof(ConfigService).GetField("V121TestSaveCount", Static)
            ?? throw new InvalidOperationException("Test-only Save counter is required in both copies");
        int Saves() => Convert.ToInt32(saveCount.GetValue(null));
        var nameBox = (TextBox)main.FindName("AppsRenameBox");
        Set(main, "_appsSelected", new AudioAppInfo { ProcessId = 900099, ProcessName = "v121-ui-name-fixture", DisplayName = "Name fixture" });
        int beforeNames = Saves();
        for (int i = 1; i <= 10; i++) nameBox.Text = "Name edit " + i;
        int immediate = Saves() - beforeNames;
        pump(450);
        int settled = Saves() - beforeNames;
        bool hasDebounce = typeof(MainWindow).GetField("_nameSaveTimer", Instance) != null;
        using (var doc = JsonDocument.Parse(File.ReadAllText(ConfigService.ConfigPath)))
        {
            string? stored = doc.RootElement.GetProperty("AppNames").GetProperty("v121-ui-name-fixture").GetString();
            add(new { Case = "ui-name-10-edits-save-count", ImmediateSaves = immediate, SettledSaves = settled,
                FinalValue = stored, HasDebounce = hasDebounce,
                Pass = stored == "Name edit 10" && (!hasDebounce || immediate == 0 && settled == 1) });
        }
        if (hasDebounce)
        {
            int beforeFocus = Saves();
            nameBox.Text = "Name focus flush";
            Call(main, "NameEdit_LostKeyboardFocus", nameBox, null);
            bool flushed = Saves() - beforeFocus == 1 && Get(main, "_nameSaveTimer") == null;
            add(new { Case = "ui-name-focus-flush", Pass = flushed, HandlerInvokedDirectly = true });

            var devices = new List<AudioDeviceInfo>
            {
                new AudioDeviceInfo { Id = "v121-device-a", DisplayName = "Device A", Flow = EDataFlow.eRender },
                new AudioDeviceInfo { Id = "v121-device-b", DisplayName = "Device B", Flow = EDataFlow.eRender }
            };
            Set(main, "_outputs", devices);
            Set(main, "_suppressDevCombo", true);
            var overview = (ComboBox)main.FindName("OverviewOutputCombo");
            var apps = (ComboBox)main.FindName("AppsOutputCombo");
            overview.ItemsSource = devices; overview.SelectedItem = devices[0];
            apps.ItemsSource = devices; apps.SelectedItem = devices[1];
            Set(main, "_suppressDevCombo", false);
            config.DeviceNames[devices[0].Id] = "Renamed A";
            Call(main, "RefreshDeviceDisplays");
            string? ovId = (overview.SelectedItem as AudioDeviceInfo)?.Id;
            string? appId = (apps.SelectedItem as AudioDeviceInfo)?.Id;
            add(new { Case = "ui-device-rename-preserves-selection", OverviewId = ovId, AppsId = appId,
                Pass = ovId == devices[0].Id && appId == devices[1].Id,
                NativeWriteSuppressionAssessedBySource = true });
        }
        int beforeClose = Saves();
        nameBox.Text = "Name close flush";
        main.Close();
        pump(30);
        using (var doc = JsonDocument.Parse(File.ReadAllText(ConfigService.ConfigPath)))
        {
            string? stored = doc.RootElement.GetProperty("AppNames").GetProperty("v121-ui-name-fixture").GetString();
            add(new { Case = "ui-name-close-flush", FinalValue = stored, SavesSinceLastEdit = Saves() - beforeClose,
                Pass = stored == "Name close flush" });
        }

        var watchRead = typeof(AutoRuleService).GetMethod("GetWatchRules", Static);
        if (watchRead != null)
        {
            var watchFixture = AutoRuleStore.Find(rules[0].Id)!;
            watchFixture.Enabled = true;
            watchFixture.Trigger = AutoRuleTrigger.AppSwitch;
            AutoRuleStore.Save(watchFixture);
            var cached = (List<AutoRule>)watchRead.Invoke(null, null)!;
            long beforeWatchAllocation = allocated();
            bool sameList = true;
            for (int i = 0; i < 1000; i++)
                sameList &= ReferenceEquals(cached, watchRead.Invoke(null, null));
            long watchBytes = beforeWatchAllocation < 0 ? -1 : allocated() - beforeWatchAllocation;
            watchFixture.Enabled = false;
            AutoRuleStore.Save(watchFixture);
            var changed = (List<AutoRule>)watchRead.Invoke(null, null)!;
            add(new { Case = "ui-watcher-revision-cache", UnchangedReads = 1000, SameList = sameList,
                EnabledWatchRules = cached.Count, DisabledWatchRules = changed.Count,
                UiThreadAllocatedBytes = watchBytes, IncludesReflectionOverhead = true,
                Pass = sameList && cached.Count == 1 && changed.Count == 0 && !ReferenceEquals(cached, changed) });
        }

        Environment.SetEnvironmentVariable("SONICROUTE_V121_TEST_APPS_FIXTURE", "1");
        QuickPanelModernWindow? panel = null;
        try
        {
            config.ShowAppPeakMeter = false;
            panel = new QuickPanelModernWindow { ShowActivated = false, ShowInTaskbar = false };
            panel.Show();
            Await((Task)Call(panel, "LoadAsync")!);
            pump(50);
            var rows = (IDictionary)Get(panel, "_rows")!;
            bool ready = Equals(Get(panel, "_contentReady"), true);
            add(new { Case = "ui-quick-panel-load-fixture", ContentReady = ready, AppRows = rows.Count,
                Pass = ready && rows.Count == 8, SyntheticInactiveApps = true });
            if (rows.Count != 8) throw new InvalidOperationException("Fixture app seam missing in this copy");
            int index = 0;
            var expectedTargets = new HashSet<int>();
            foreach (DictionaryEntry entry in rows)
            {
                object row = entry.Value!;
                var slider = (Slider)row.GetType().GetField("Slider")!.GetValue(row)!;
                slider.IsEnabled = index < 3;
                row.GetType().GetField("IsMuted")!.SetValue(row, index == 1);
                if (index < 3 && index != 1) expectedTargets.Add(Convert.ToInt32(entry.Key));
                index++;
            }
            panel.SetMeterEnabled(true);
            Call(panel, "UpdateMeterTargets");
            var meter = (AudioMeterService?)Get(panel, "_meter");
            var targets = meter == null ? new HashSet<int>() : (HashSet<int>)Get(meter, "_targets")!;
            bool targetPass = targets.SetEquals(expectedTargets);
            panel.SetMeterEnabled(false);
            bool disabled = Get(panel, "_meter") == null && Get(panel, "_latestPeaks") == null;
            bool stopped = meter == null || Get(meter, "_worker") == null;
            add(new { Case = "ui-quick-panel-meter-targets-and-disable", TargetCount = targets.Count,
                ExpectedTargets = expectedTargets.Count, TargetsCorrect = targetPass,
                MeterReleased = disabled, WorkerStopped = stopped,
                Pass = targetPass && disabled && stopped, SyntheticRowsAndPids = true,
                Note = "Includes every eligible row, without viewport filtering; muted/disabled rows excluded." });
            panel.Close();
            pump(50);
            add(new { Case = "ui-quick-panel-close-cleanup", Pass = Equals(Get(panel, "_isClosed"), true) && Get(panel, "_meter") == null });
        }
        finally
        {
            if (panel != null && !Equals(Get(panel, "_isClosed"), true)) panel.Close();
            Environment.SetEnvironmentVariable("SONICROUTE_V121_TEST_APPS_FIXTURE", null);
        }
    }
}

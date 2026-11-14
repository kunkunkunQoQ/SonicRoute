using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SonicRoute;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void CheckMemoryFollowup(List<Window> windows)
    {
        ThemeService.Apply("light", "#00CBE9");
        var main = new MainWindow(); windows.Add(main);
        Call(main, "EnsureAutomationPage");
        var type = typeof(MainWindow);
        type.GetField("_suppressAutoUi", Instance)!.SetValue(main, true);
        var apps = Enumerable.Range(0, 40).Select(i => new AudioAppInfo
            { ProcessId = (uint)(int.MaxValue - 100 - i), ProcessName = "candidate-" + i, DisplayName = "App " + i }).ToList();
        apps.Add(new AudioAppInfo { ProcessId = 0, ProcessName = "SonicRoute", DisplayName = "Internal" });
        type.GetField("_autoApps", Instance)!.SetValue(main, apps);
        var candidates = (List<AppItem>)type.GetMethod("GetAppCandidates", Instance)!.Invoke(main, new object[] { apps })!;
        var icon = new DrawingImage(); icon.Freeze();
        var iconField = typeof(AppItem).GetField("_icon", Instance)!;
        foreach (var item in candidates) iconField.SetValue(item, icon); // No process/icon workers in this fixture.
        var trigger = Field<ComboBox>(main, "AutoTriggerAppCombo");
        trigger.ItemsSource = candidates; trigger.SelectedItem = candidates[0];
        var steps = Field<List<ComboBox>>(main, "_autoStepAppCombos");
        for (int i = 1; i <= 12; i++) steps.Add(new ComboBox { ItemsSource = candidates,
            SelectedItem = candidates[i], Tag = new AutoRuleStep { TargetApp = "candidate-" + i } });
        var batch = (Action)type.GetMethod("BindAutoAppCandidateBatch", Instance)!.CreateDelegate(typeof(Action), main);
        var bind = (Action<ComboBox, string?>)type.GetMethod("BindAutoAppCandidates", Instance)!.CreateDelegate(typeof(Action<ComboBox, string?>), main);
        batch();
        Check(steps.All(combo => ReferenceEquals(combo.ItemsSource, trigger.ItemsSource)) && trigger.Items.Count == 40,
            "one batch shares its filtered candidates across trigger and every step without internal applications");
        Check(((AppItem)trigger.SelectedItem).ProcessName == "candidate-0"
            && steps.Select((combo, i) => ((AppItem)combo.SelectedItem).ProcessName == "candidate-" + (i + 1)).All(value => value),
            "shared candidate results preserve each consumer's independent selected target");
        Check(trigger.IsSynchronizedWithCurrentItem == false && steps.All(combo => combo.IsSynchronizedWithCurrentItem == false),
            "batch sharing never synchronizes selection between different controls");
        var original = trigger.ItemsSource;
        batch();
        Check(ReferenceEquals(original, trigger.ItemsSource), "unchanged candidates reuse the existing item models and icons");
#if NET5_0_OR_GREATER
        void PreviousConsumerLoop()
        {
            bind(trigger, (trigger.SelectedItem as AppItem)?.ProcessName);
            foreach (var combo in steps) bind(combo, ((AutoRuleStep)combo.Tag).TargetApp);
        }
        for (int i = 0; i < 5; i++) { PreviousConsumerLoop(); batch(); }
        (long Bytes, double Milliseconds) Measure(Action action)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) action();
            return (GC.GetAllocatedBytesForCurrentThread() - before, clock.Elapsed.TotalMilliseconds);
        }
        var before = Enumerable.Range(0, 3).Select(_ => Measure(PreviousConsumerLoop)).OrderBy(value => value.Bytes).ToArray()[1];
        var after = Enumerable.Range(0, 3).Select(_ => Measure(batch)).OrderBy(value => value.Bytes).ToArray()[1];
        Check(after.Bytes < before.Bytes, "preparing once reduces measured allocations in the same warmed binding batch");
        Console.WriteLine($"MEASURE candidate-batch rounds=3 batches=100 consumers=13 apps=40 beforeBytes={before.Bytes} afterBytes={after.Bytes} beforeMs={before.Milliseconds:F3} afterMs={after.Milliseconds:F3}");
#endif
        var changed = apps.ToList();
        changed[2] = new AudioAppInfo { ProcessId = apps[2].ProcessId, ProcessName = apps[2].ProcessName,
            DisplayName = "Renamed", HasActiveSession = true };
        type.GetField("_autoApps", Instance)!.SetValue(main, changed);
        foreach (var item in (List<AppItem>)type.GetMethod("GetAppCandidates", Instance)!.Invoke(main, new object[] { changed })!)
            iconField.SetValue(item, icon);
        batch();
        Check(((AppItem)steps[1].SelectedItem).Info.DisplayName == "Renamed"
            && ((AppItem)steps[1].SelectedItem).Info.HasActiveSession,
            "a new metadata snapshot refreshes the matching selected item instead of using a stale identity cache");
        type.GetField("_autoApps", Instance)!.SetValue(main, changed.Where(info => info.ProcessName != "candidate-1").ToList());
        batch();
        Check(steps[0].SelectedIndex == -1 && ((AppItem)steps[1].SelectedItem).ProcessName == "candidate-2",
            "removing one app clears only that missing target while other selections survive");
        if (typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost") is { } host)
        {
            Check(!(bool)host.GetProperty("IsAdbCreated", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!,
                "candidate batching does not prepare the Android runtime or create ADB");
        }
    }
}

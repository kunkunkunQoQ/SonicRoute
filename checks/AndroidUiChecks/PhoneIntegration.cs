using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static AndroidMediaState? _mediaPreview;
    private static readonly Type UnifiedAudio = typeof(App).Assembly.GetType("SonicRoute.ApplicationAudio")!;
    private static List<AudioAppInfo> ExternalApps() => (List<AudioAppInfo>)UnifiedAudio.GetMethod("MergeExternal", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { Array.Empty<AudioAppInfo>() })!;

    private static void ChangeAndWait(AndroidAudioSource source, Action action, Func<AndroidAudioState, bool> condition)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(AndroidAudioState state) { if (condition(state)) ready.TrySetResult(); }
        source.Session.Changed += Changed;
        try { action(); Changed(source.Session.State); Pump(ready.Task.WaitAsync(TimeSpan.FromSeconds(3))); }
        finally { source.Session.Changed -= Changed; }
    }
    private static void CheckUnifiedApplications(MainWindow main, QuickPanelModernWindow panel)
    {
        var manager = (AndroidAudioManager)typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!
            .GetProperty("Sources", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var sources = manager.Sources;
        var first = sources[0]; var second = sources[1];
        var firstState = first.Session.State; var secondState = second.Session.State;
        string key = "android:" + first.App.Package, secondKey = "android:" + second.App.Package;
        var config = ConfigService.Load();
        bool hadAlias = config.AppNames.TryGetValue(key, out string? alias);
        var hidden = config.HiddenPanelApps.ToArray();
        var previousCurrent = CurrentAppService.Current;
        try
        {
            var apps = ExternalApps();
            Check(apps.Count == sources.Count && apps.All(app => app.IsExternal && app.ProcessId == 0)
                && apps.Select(app => app.Identity).Distinct().Count() == sources.Count,
                "phone candidates retain separate source identities without inventing Windows PIDs");
            var items = (List<AppItem>)typeof(MainWindow).GetMethod("GetAppCandidates", Hidden)!.Invoke(main, new object[] { apps })!;
            Check(items.Count == sources.Count && items.All(item => item.Subtitle.Contains(first.Device.Name)),
                "shared app candidates include every received phone session and show its device");
            typeof(MainWindow).GetField("_appItems", Hidden)!.SetValue(main, items);
            var list = (ListBox)main.FindName("AppsListBox"); list.ItemsSource = items;
            list.SelectedItem = items.Single(item => item.Identity == first.Id);
            Pump((Task)typeof(MainWindow).GetMethod("RefreshAppsSelectionAsync", Hidden)!.Invoke(main, null)!);
            var volume = (Slider)main.FindName("AppsVolumeSlider");
            Check(volume.IsEnabled && volume.Value == firstState.Volume
                && ((TextBlock)main.FindName("AppsDetailPid")).Text.Contains(first.Device.Name)
                && !((FrameworkElement)main.FindName("AppsInputCard")).IsEnabled,
                "full app detail reads the selected phone source and does not offer an unavailable recording stream");
            ChangeAndWait(first, () => volume.Value = 29, state => state.Volume == 29);
            Check(second.Session.State.Volume == secondState.Volume, "full app volume slider updates its phone session without changing its neighbour");
            var mute = (Button)main.FindName("AppsMuteButton");
            ChangeAndWait(first, () => mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), state => state.Muted != firstState.Muted);
            Check(second.Session.State.Muted == secondState.Muted, "full app mute shares the phone renderer state without muting another application");
            var output = (ComboBox)main.FindName("AppsOutputCombo");
            ChangeAndWait(first, () => output.SelectedItem = output.Items.OfType<AudioDeviceInfo>().Single(device => device.Id == "speaker"),
                state => state.OutputId == "speaker");
            Check(second.Session.State.OutputId == secondState.OutputId, "full app output selection routes only the selected incoming phone stream");
            var name = (TextBox)main.FindName("AppsRenameBox"); name.Text = "手机音乐测试名";
            Check(((TextBlock)main.FindName("AppsDetailTitle")).Text == "手机音乐测试名" && items[0].Label == "手机音乐测试名",
                "phone renaming reuses the existing title and candidate name configuration");
            name.Text = hadAlias ? alias ?? "" : "";
            list.SelectedItem = items.Single(item => item.Identity == second.Id);
            Pump((Task)typeof(MainWindow).GetMethod("RefreshAppsSelectionAsync", Hidden)!.Invoke(main, null)!);
            var visible = (CheckBox)main.FindName("AppsShowInPanelButton");
            int rowCount = ((ItemsControl)panel.FindName("AppListPanel")).Items.Count;
            visible.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            DrainUi();
            Check(((ItemsControl)panel.FindName("AppListPanel")).Items.Count == rowCount - 1
                && second.Session.State.State == CaptureState.Receiving,
                "phone quick-panel visibility switch hides its row while reception continues");
            visible.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            DrainUi();
            Check(((ItemsControl)panel.FindName("AppListPanel")).Items.Count == rowCount, "showing the phone source again restores its existing quick-panel row");
            typeof(MainWindow).GetField("_autoApps", Hidden)!.SetValue(main, apps);
            var target = new ComboBox();
            typeof(MainWindow).GetMethod("BindAutoAppCandidates", Hidden)!.Invoke(main, new object[] { target, secondKey });
            Check(target.Items.Count == sources.Count && (target.SelectedItem as AppItem)?.Identity == second.Id,
                "automation application selector lists and selects phone sources using their saved target key");
            int changed = 0; void CurrentChanged() => ++changed;
            CurrentAppService.CurrentChanged += CurrentChanged;
            try { CurrentAppService.Current = apps[0]; CurrentAppService.Current = apps[1]; }
            finally { CurrentAppService.CurrentChanged -= CurrentChanged; }
            Check(changed == 2 && CurrentAppService.Current?.Identity == apps[1].Identity,
                "switching between two phone applications changes the shared current application");
            var steps = new[] {
                new AutoRuleStep { Action = AutoRuleAction.SetAppVolume, TargetApp = key, Volume = 41 },
                new AutoRuleStep { Action = AutoRuleAction.AdjustAppVolume, TargetApp = key, VolumeDelta = -5 },
                new AutoRuleStep { Action = AutoRuleAction.SetAppMute, TargetApp = key, Muted = true },
                new AutoRuleStep { Action = AutoRuleAction.ToggleAppMute, TargetApp = key },
                new AutoRuleStep { Action = AutoRuleAction.SetAppOutput, TargetApp = secondKey, TargetDeviceId = "speaker" }
            };
            var execute = typeof(AutoRuleService).GetMethod("ExecuteStepAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var step in steps)
            {
                var detail = new ExecutionHistoryStep { TargetApp = step.TargetApp };
                var task = (Task<bool>)execute.Invoke(null, new object?[] { step, CancellationToken.None, new HashSet<string>(), detail, null })!;
                Pump(task);
                Check(task.Result && detail.TargetPid == null && detail.TargetSourceId != null,
                    "automation " + step.Action + " executes through the selected source and records its identity");
            }
            Check(first.Session.State.Volume == 36 && !first.Session.State.Muted && second.Session.State.OutputId == "speaker"
                && second.Session.State.Volume == secondState.Volume && manager.Sources.Count == sources.Count,
                "automation uses independent phone gains mute states and routes without retiring other sessions");
            CheckPhoneLifecycleRules(manager, first, key);
        }
        finally
        {
            if (hadAlias) config.AppNames[key] = alias!; else config.AppNames.Remove(key);
            config.HiddenPanelApps.Clear(); config.HiddenPanelApps.AddRange(hidden);
            first.Session.SetVolume(firstState.Volume); first.Session.SetMute(firstState.Muted); Pump(first.Session.SetOutputAsync(firstState.OutputId));
            second.Session.SetVolume(secondState.Volume); second.Session.SetMute(secondState.Muted); Pump(second.Session.SetOutputAsync(secondState.OutputId));
            CurrentAppService.Current = previousCurrent;
            UnifiedAudio.GetMethod("NotifyChanged", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            DrainUi();
        }
    }
    private static void CheckPhoneLifecycleRules(AndroidAudioManager manager, AndroidAudioSource target, string targetKey)
    {
        Check(AutoRuleStore.LoadAll().Count == 0, "lifecycle rule check starts in its own empty configuration directory");
        const string incoming = "android:cn.test.lifecycle";
        AutoRule Rule(AutoRuleTrigger trigger, int volume) => new()
        {
            Id = Guid.NewGuid().ToString("N"), Name = "Offline phone " + trigger, Enabled = true,
            Trigger = trigger, TriggerApp = incoming,
            Actions = new List<AutoRuleStep> { new() { Action = AutoRuleAction.SetAppVolume, TargetApp = targetKey, Volume = volume } }
        };
        var start = Rule(AutoRuleTrigger.AppStart, 62); var exit = Rule(AutoRuleTrigger.AppExit, 63);
        AndroidAudioSource? added = null;
        AutoRuleStore.Save(start); AutoRuleStore.Save(exit);
        try
        {
            AutoRuleService.RefreshWatcher();
            Check((bool)typeof(AutoRuleService).GetField("_externalWatching", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!
                && typeof(AutoRuleService).GetField("_processSnapshot", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) == null
                && typeof(AutoRuleService).GetField("_watchFallbackTimer", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) == null,
                "phone-only lifecycle rules use source events without a Windows process scanner or fallback timer");
            ChangeAndWait(target, () =>
            {
                added = AddFixture(manager, target.Device, target.App with { Package = "cn.test.lifecycle", Uid = 98765 });
                Notify(manager);
            }, state => state.Volume == 62);
            Check(target.Session.State.Volume == 62, "incoming phone session event triggers its saved start rule through the real rule queue");
            Task? removed = null;
            ChangeAndWait(target, () => removed = manager.RemoveAsync(added!.Id), state => state.Volume == 63);
            Pump(removed!); added = null;
            Check(target.Session.State.Volume == 63, "removing the incoming phone session triggers its saved exit rule");
            var deadline = DateTime.UtcNow.AddSeconds(3);
            bool Running(string id) => (bool)typeof(AutoRuleService).GetMethod("IsRunning", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { id })!;
            while ((Running(start.Id) || Running(exit.Id)) && DateTime.UtcNow < deadline) Pump(Task.Delay(5));
            Check(!Running(start.Id) && !Running(exit.Id), "lifecycle rule executions finish without leaving source work running");
        }
        finally
        {
            AutoRuleStore.Delete(start.Id); AutoRuleStore.Delete(exit.Id); AutoRuleService.RefreshWatcher();
            if (added != null) Pump(manager.RemoveAsync(added.Id));
        }
        Check(!(bool)typeof(AutoRuleService).GetField("_externalWatching", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!,
            "removing the last lifecycle rule releases its phone-session event subscription");
    }
}

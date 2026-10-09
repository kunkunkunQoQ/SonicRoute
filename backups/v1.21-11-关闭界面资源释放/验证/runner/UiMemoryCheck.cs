using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void UMAssert(bool value, string name)
    {
        Add(new { Case = "ui-memory-check", Name = name, Passed = value });
        if (!value) throw new InvalidOperationException(name);
    }
    private static object UMField(object owner, string name) => owner.GetType().GetField(name, PrivateInstance)!.GetValue(owner)!;
    private static void UMSet(object owner, string name, object value) => owner.GetType().GetField(name, PrivateInstance)!.SetValue(owner, value);
    private static object UMInvoke(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, PrivateInstance)!.Invoke(owner, args)!;

    private static void RunUiMemoryCheck()
    {
        Config = new AppConfig { Language = "zh-CN", CollapseDeviceSections = true, ExperimentalMic = true,
            Hotkeys = new Dictionary<string,string>(), ShowAppPeakMeter = false, MicMuteOsdPersistent = false };
        typeof(ConfigService).GetField("_cache", PrivateStatic)!.SetValue(null, Config);
        L10n.Instance.SetLanguage("zh-CN");
        UMAssert(AutoRuleStore.RulesDir.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase), "isolated-rule-directory");
        Directory.CreateDirectory(AutoRuleStore.RulesDir);
        UMAssert(Directory.GetFiles(AutoRuleStore.RulesDir, "*.json").Length == 0, "fresh-isolated-rules");
        var main = new MainWindow { Left = -5000, Top = -5000, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false, ShowActivated = false, IsHitTestVisible = false };
        main.Show(); Pump(650);
        var names = (ItemsControl)main.FindName("OutputNameList");
        var filters = (ItemsControl)main.FindName("OutputFilterList");
        UMAssert(names.Items.Count == 0 && filters.Items.Count == 0, "main-open-creates-no-settings-device-rows");
        ((FrameworkElement)main.FindName("OverviewPage")).Visibility = Visibility.Collapsed;
        ((FrameworkElement)main.FindName("SettingsPage")).Visibility = Visibility.Visible;
        IPCall(main, "LoadSettings"); Pump(80);
        UMAssert(names.Items.Count == 0 && filters.Items.Count == 0, "collapsed-settings-creates-no-device-rows");
        var nameToggle = (ToggleButton)main.FindName("DeviceNamesMoreToggle");
        nameToggle.IsChecked = true;
        IPCall(main, "DeviceNamesMoreToggle_Click", nameToggle, new RoutedEventArgs());
        Pump(150);
        var outputs = (List<AudioDeviceInfo>)UMField(main, "_outputs");
        UMAssert(names.Items.Count == outputs.Count + 1 && filters.Items.Count == 0, "expanding-names-creates-only-name-rows");
        var firstNameRow = names.Items[0];
        IPCall(main, "EnsureSettingsDeviceSections");
        UMAssert(ReferenceEquals(firstNameRow, names.Items[0]), "unchanged-name-rows-reused");
        var nameBox = IPChildren<TextBox>((DependencyObject)firstNameRow).First();
        nameBox.Text = "名称保存测试"; Pump(350);
        UMAssert(Config.DeviceNames[(string)nameBox.Tag] == nameBox.Text, "deferred-name-edit-still-saves");
        var filterToggle = (ToggleButton)main.FindName("KeepDevicesMoreToggle");
        filterToggle.IsChecked = true;
        IPCall(main, "KeepDevicesMoreToggle_Click", filterToggle, new RoutedEventArgs());
        UMAssert(filters.Items.Count == outputs.Count + 1, "expanding-filter-builds-device-rows");
        var filter = (CheckBox)filters.Items[0];
        var device = (AudioDeviceInfo)filter.Tag;
        filter.IsChecked = false;
        UMAssert(Config.HiddenOutputDevices.Contains(device.Id), "deferred-filter-writes-config");
        filter.IsChecked = true;
        UMAssert(!Config.HiddenOutputDevices.Contains(device.Id), "deferred-filter-restores-config");
        nameToggle.IsChecked = filterToggle.IsChecked = false;
        int nameCount = names.Items.Count;
        outputs.Add(new AudioDeviceInfo { Id = "fixture-device", DisplayName = "测试新设备" });
        IPCall(main, "InvalidateSettingsDeviceSections");
        IPCall(main, "EnsureSettingsDeviceSections");
        UMAssert(names.Items.Count == nameCount, "hidden-device-change-does-not-build-rows");
        nameToggle.IsChecked = true; IPCall(main, "EnsureSettingsDeviceSections");
        UMAssert(names.Items.Count == nameCount + 1, "device-change-applies-on-next-expand");
        UMSet(main, "_settingsNamesLanguage", "outdated"); IPCall(main, "EnsureSettingsDeviceSections");
        UMAssert((string)UMField(main, "_settingsNamesLanguage") == L10n.CurrentLanguage, "language-invalidates-name-rows");

        ((FrameworkElement)main.FindName("SettingsPage")).Visibility = Visibility.Collapsed;
        ((FrameworkElement)main.FindName("AutomationPage")).Visibility = Visibility.Visible;
        var apps = Enumerable.Range(1, 100).Select(i => new AudioAppInfo { ProcessId = (uint)(10000000+i), ProcessName = "fixture"+i, DisplayName = "Fixture "+i }).ToList();
        UMSet(main, "_autoApps", apps);
        IPCall(main, "FillAutoCombos");
        var steps = Enumerable.Range(1, 20).Select(i => new AutoRuleStep { Action = AutoRuleAction.SetAppVolume, TargetApp = "fixture"+i }).ToList();
        UMSet(main, "_autoSteps", steps);
        IPCall(main, "RenderAutoSteps"); IPCall(main, "SetAutomationEditorVisible", true); Pump(150);
        var combos = (List<ComboBox>)UMField(main, "_autoStepAppCombos");
        UMAssert(combos.Count == 20 && combos.All(c => ReferenceEquals(c.ItemsSource, combos[0].ItemsSource)), "twenty-app-combos-share-one-list");
        var unique = combos.SelectMany(c => c.Items.OfType<AppItem>()).Distinct().Count();
        UMAssert(unique == 100, "two-thousand-options-use-one-hundred-app-items");
        combos[0].SelectedIndex = 3;
        UMAssert(steps[0].TargetApp == "fixture4" && steps[1].TargetApp == "fixture2", "shared-list-selections-independent");
        UMAssert(combos.All(c => c.IsSynchronizedWithCurrentItem == false), "shared-view-currency-disabled");
        steps[0].TargetApp = "disconnected-fixture";
        IPCall(main, "BindAutoAppCandidates", combos[0], steps[0].TargetApp);
        UMAssert(combos[0].SelectedItem == null && steps[0].TargetApp == "disconnected-fixture", "missing-app-target-preserved");
        steps[0].TargetApp = "fixture4";
        IPCall(main, "BindAutoAppCandidates", combos[0], steps[0].TargetApp);
        var firstSource = combos[0].ItemsSource;
        IPCall(main, "BindAutoAppCandidates", combos[0], steps[0].TargetApp);
        UMAssert(ReferenceEquals(firstSource, combos[0].ItemsSource), "unchanged-candidate-snapshot-reused");
        IPCall(main, "BindSettingsApps", apps);
        UMAssert(ReferenceEquals(((ComboBox)main.FindName("FixedAppCombo")).ItemsSource, firstSource), "settings-and-automation-share-app-candidates");
        var host = (ItemsControl)main.FindName("AutoStepsHost");
        var actionCombos = host.Items.Cast<DependencyObject>().SelectMany(IPChildren<ComboBox>).Where(c => c.Items.Count == 24).ToList();
        UMAssert(actionCombos.Count == 20 && actionCombos.All(c => ReferenceEquals(c.ItemsSource, actionCombos[0].ItemsSource)), "action-definitions-shared-between-steps");
        var definitions = actionCombos[0].Items.Cast<object>().ToList();
        UMAssert(definitions.Count(x => x.GetType().GetProperty("IsAction")!.GetValue(x)!.Equals(true)) == 18, "all-eighteen-actions-retained");
        UMAssert(IPChildren<TextBlock>(actionCombos[0]).Any(t => t.Text == L10n.T("Auto.ActionAppVolume")), "selected-action-label-rendered");
        IPImage(main, "automation-shared-options.png");
        actionCombos[0].SelectedItem = definitions.First(x => Equals(x.GetType().GetProperty("Action")!.GetValue(x), AutoRuleAction.ShowOsd));
        UMAssert(steps[0].Action == AutoRuleAction.ShowOsd, "data-action-selection-updates-step");
        ((FrameworkElement)main.FindName("AutomationPage")).Visibility = Visibility.Collapsed;
        IPCall(main, "StopAutoRefresh");
        UMAssert(host.Items.Count == 20 && steps.Count == 20, "temporary-page-switch-preserves-draft");
        ((FrameworkElement)main.FindName("AutomationPage")).Visibility = Visibility.Visible;
        IPCall(main, "SetAutomationEditorVisible", false);
        UMAssert(host.Items.Count == 0 && ((ICollection)UMField(main, "_autoStepViews")).Count == 0 && combos.Count == 0 && steps.Count == 0, "cancel-releases-editor-rows-and-references");

        IPCall(main, "AutoNew_Click", main, new RoutedEventArgs());
        var trigger = (ComboBox)main.FindName("AutoTriggerCombo");
        trigger.SelectedItem = trigger.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, AutoRuleTrigger.Startup));
        UMSet(main, "_autoSteps", new List<AutoRuleStep> { new AutoRuleStep { Action = AutoRuleAction.ShowOsd, OsdTitle = "测试，不执行" } });
        IPCall(main, "RenderAutoSteps");
        ((TextBox)main.FindName("AutoNameBox")).Text = "   ";
        ((TextBox)main.FindName("AutoNameBox")).Text = "";
        Pump(80); IPImage(main, "automation-empty-name-hint.png");
        IPCall(main, "AutoSave_Click", main, new RoutedEventArgs()); Pump(180);
        var first = AutoRuleStore.LoadAll().Single();
        UMAssert(first.Name == "规则1" && first.Actions[0].OsdTitle == "测试，不执行", "blank-ui-name-saves-rule-one-and-content");
        UMAssert(host.Items.Count == 0, "successful-save-releases-editor");
        var second = new AutoRule { Id = Guid.NewGuid().ToString("N"), Name = "" };
        AutoRuleStore.Save(second, "规则");
        UMAssert(second.Name == "规则2", "default-name-increments");
        File.WriteAllText(Path.Combine(AutoRuleStore.RulesDir, "规则3.json"), "invalid fixture json");
        AutoRuleStore.Invalidate();
        var fourth = new AutoRule { Id = Guid.NewGuid().ToString("N"), Name = " " };
        AutoRuleStore.Save(fourth, "规则");
        UMAssert(fourth.Name == "规则4", "default-name-skips-occupied-invalid-file");
        var named = new AutoRule { Id = Guid.NewGuid().ToString("N"), Name = "自定义规则" };
        AutoRuleStore.Save(named, "规则");
        UMAssert(named.Name == "自定义规则", "explicit-name-unchanged");
        var english = new AutoRule { Id = Guid.NewGuid().ToString("N"), Name = "" };
        AutoRuleStore.Save(english, "Rule");
        UMAssert(english.Name == "Rule1", "localized-prefix-supported");
        Pump(200); AutoRuleStore.LoadAll();
        IPCall(main, "AutoEdit_Click", new Button { Tag = first.Id }, new RoutedEventArgs()); Pump(100);
        string file = Path.Combine(AutoRuleStore.RulesDir, first.Name + ".json");
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            IPCall(main, "AutoSave_Click", main, new RoutedEventArgs());
            UMAssert(host.Items.Count == 1 && ((FrameworkElement)main.FindName("AutoEditCard")).Visibility == Visibility.Visible,
                "failed-save-keeps-editor-and-draft");
        }
        IPCall(main, "AutoCancel_Click", main, new RoutedEventArgs());
        UMAssert(host.Items.Count == 0, "cancel-after-failed-save-releases-editor");
        IPImage(main, "automation-default-name.png");
        Add(new { Case = "ui-memory-summary", Passed = true, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            SharedCandidateItems = unique, CandidateReferences = 2000, Note = "Object reuse verified; no process memory benchmark or real rule/audio/hotkey writes." });
        main.Close(); Pump(100);
    }
}

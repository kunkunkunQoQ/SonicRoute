using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SonicRoute.Core.Models;
using ComboBox = System.Windows.Controls.ComboBox;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private bool _settingsNamesBuilt;
        private string? _settingsNamesLanguage;
        private List<AudioAppInfo> _candidateSnapshot = new();
        private List<AppItem> _candidateItems = new();
        private List<AutomationActionChoice>? _actionChoices;
        private string? _actionChoicesLanguage;

        private void InvalidateSettingsDeviceSections()
        {
            _settingsFilterBuilt = false;
            _settingsNamesBuilt = false;
        }

        // 保留轻量宿主；进入设置且区域展开后才生成设备行。
        private void EnsureSettingsDeviceSections()
        {
            if (_isClosed || SettingsPage.Visibility != Visibility.Visible) return;
            string language = L10n.CurrentLanguage;
            if ((!_config.CollapseDeviceSections || KeepDevicesMoreToggle.IsChecked == true)
                && (!_settingsFilterBuilt || _settingsFilterLanguage != language))
            {
                BuildDeviceFilter();
                _settingsFilterBuilt = true;
                _settingsFilterLanguage = language;
                UpdateSelectAllLabels();
            }
            if ((!_config.CollapseDeviceSections || DeviceNamesMoreToggle.IsChecked == true)
                && (!_settingsNamesBuilt || _settingsNamesLanguage != language))
            {
                BuildDeviceNameLists();
                _settingsNamesBuilt = true;
                _settingsNamesLanguage = language;
            }
        }

        // 一个快照共用数据项及列表，ComboBox 仍各自拥有容器和选中状态。
        private List<AppItem> GetAppCandidates(List<AudioAppInfo> apps)
        {
            if (!SameAutoApps(_candidateSnapshot, apps))
            {
                var previous = _candidateItems.GroupBy(item => item.ProcessId).ToDictionary(group => group.Key, group => group.First());
                _candidateItems = apps.Select(app => previous.TryGetValue((int)app.ProcessId, out var item)
                    && SameAutoApp(item.Info, app) ? item : AppItem.From(app)).ToList();
                _candidateSnapshot = apps;
            }
            return _candidateItems;
        }

        private static bool SameAutoApp(AudioAppInfo a, AudioAppInfo b) =>
            a.ProcessId == b.ProcessId && a.HasActiveSession == b.HasActiveSession
            && string.Equals(a.ProcessName, b.ProcessName, StringComparison.Ordinal)
            && string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal);

        private void BindAutoAppCandidates(ComboBox combo, string? processName)
        {
            bool previous = _suppressAutoUi;
            _suppressAutoUi = true;
            try
            {
                combo.IsSynchronizedWithCurrentItem = false;
                combo.ItemsSource = GetAppCandidates(_autoApps);
                SelectAutoApp(combo, processName);
            }
            finally { _suppressAutoUi = previous; }
            if (combo.IsDropDownOpen) LoadAppComboIcons(combo);
        }

        private void ReleaseAutomationEditor()
        {
            EndAutoDrag(false);
            EndAutomationRuleDrag(false);
            _autoCapturingHotkey = false;
            _autoSteps.Clear();
            _autoStepAppCombos.Clear();
            _autoStepDeviceCombos.Clear();
            _autoStepNumbers.Clear();
            _autoStepViews.Clear();
            AutoStepsHost.Items.Clear();
            AutoTriggerAppCombo.ItemsSource = null;
            AutoTriggerAppCombo.Items.Clear();
            _autoEditingId = null;
            _autoHotkeyCombo = "";
            AutoNameBox.Clear();
        }

        private sealed class AutomationActionChoice
        {
            public string Text { get; init; } = "";
            public AutoRuleAction? Action { get; init; }
            public bool IsAction => Action.HasValue;
            public double FontSize => IsAction ? 12.5 : 11;
            public FontWeight FontWeight => FontWeights.SemiBold;
            public Thickness Padding { get; init; }
            public override string ToString() => Text;
        }

        private List<AutomationActionChoice> AutoActionItems()
        {
            if (_actionChoices != null && _actionChoicesLanguage == L10n.CurrentLanguage) return _actionChoices;
            var items = new List<AutomationActionChoice>();
            void Group(string key, params AutoRuleAction[] actions)
            {
                items.Add(new AutomationActionChoice { Text = L10n.T(key), Padding = new Thickness(10, items.Count == 0 ? 3 : 10, 10, 2) });
                foreach (var action in actions)
                    items.Add(new AutomationActionChoice { Text = L10n.T(AutoActionKey(action)), Action = action, Padding = new Thickness(10, 6, 10, 6) });
            }
            Group("Auto.GroupSystem", AutoRuleAction.SetSystemOutput, AutoRuleAction.SetSystemInput, AutoRuleAction.SetSystemVolume,
                AutoRuleAction.AdjustSystemVolume, AutoRuleAction.SetSystemMute, AutoRuleAction.ToggleSystemMute);
            Group("Auto.GroupApp", AutoRuleAction.SetAppOutput, AutoRuleAction.SetAppInput, AutoRuleAction.SetAppVolume,
                AutoRuleAction.AdjustAppVolume, AutoRuleAction.SetAppMute, AutoRuleAction.ToggleAppMute);
            Group("Auto.GroupMic", AutoRuleAction.SetGlobalMicMute, AutoRuleAction.ToggleGlobalMicMute);
            Group("Auto.GroupProgram", AutoRuleAction.LaunchProgram, AutoRuleAction.RunPowerShell);
            Group("Auto.GroupAutomation", AutoRuleAction.ExecuteRule);
            Group("Auto.GroupNotice", AutoRuleAction.ShowOsd);
            _actionChoicesLanguage = L10n.CurrentLanguage;
            return _actionChoices = items;
        }
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Clipboard = System.Windows.Clipboard;
using Binding = System.Windows.Data.Binding;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private int _autoExecutionRefreshPending;
        private void InitializeAutoExtras()
        {
            ComboBoxItem Item(string key, object tag)
            {
                var item = new ComboBoxItem { Tag = tag };
                item.SetBinding(ContentControl.ContentProperty, new Binding("[" + key + "]") { Source = L10n.Instance });
                return item;
            }
            AutoStateFilter.Items.Add(Item("Auto.FilterAllStates", 0));
            AutoStateFilter.Items.Add(Item("Auto.Enabled", 1));
            AutoStateFilter.Items.Add(Item("Auto.Disabled", 2));
            AutoTriggerFilter.Items.Add(Item("Auto.FilterAllTriggers", -1));
            string[] keys = { "Auto.TriggerHotkey", "Auto.TriggerAppStart", "Auto.TriggerAppSwitch", "Auto.TriggerAppExit", "Auto.TriggerSchedule", "Auto.TriggerStartup" };
            for (int index = 0; index < keys.Length; index++) AutoTriggerFilter.Items.Add(Item(keys[index], index));
            AutoStateFilter.SelectedIndex = AutoTriggerFilter.SelectedIndex = 0;
            AutoRuleService.ExecutionStateChanged += AutoExecutionStateChanged;
        }

        private void AutoSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyAutoRuleFilters();
        private void AutoFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyAutoRuleFilters();

        private void ApplyAutoRuleFilters()
        {
            if (AutoSearchBox == null || AutoEmptyText == null) return;
            EndAutomationRuleDrag(false);
            string query = AutoSearchBox.Text.Trim();
            int state = (AutoStateFilter?.SelectedItem as ComboBoxItem)?.Tag is int s ? s : 0;
            int trigger = (AutoTriggerFilter?.SelectedItem as ComboBoxItem)?.Tag is int t ? t : -1;
            int visible = 0;
            foreach (var rule in _autoRules)
            {
                bool match = (query.Length == 0 || (rule.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    && (state == 0 || rule.Enabled == (state == 1)) && (trigger < 0 || (int)rule.Trigger == trigger);
                if (_autoRows.TryGetValue(rule.Id, out var row)) row.Element.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match) visible++;
            }
            AutoEmptyText.Text = L10n.T(_autoRules.Count == 0 ? "Auto.Empty" : "Auto.NoMatches");
            AutoEmptyText.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AutoExecutionStateChanged()
        {
            if (_isClosed || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _autoExecutionRefreshPending, 1) != 0) return;
            _uiLifetime.Post(Dispatcher, new Action(() =>
            {
                Interlocked.Exchange(ref _autoExecutionRefreshPending, 0);
                if (!_isClosed && AutomationPage.Visibility == Visibility.Visible) _ = RefreshAutoRulesAsync();
            }));
        }

        private Button BuildAutoExtrasButton(string id)
        {
            var button = BuildAutomationIconButton(AutomationIcons.More, "Auto.More", id);
            button.Click += (_, _) =>
            {
                var menu = ConvenienceMenus.Create();
                menu.PlacementTarget = button;
                var command = ConvenienceMenus.Item(L10n.T("Auto.CopyCommand"));
                command.Click += (_, _) =>
                {
                    try { Clipboard.SetText(RuleCommandLine.BuildShortCommand(id)); ShowToast(L10n.T("Auto.CommandCopied")); }
                    catch { ShowToast(L10n.T("Auto.CommandCopyFailed")); }
                };
                var favorites = _config.FavoriteRuleIds ??= new();
                var favorite = ConvenienceMenus.Item(L10n.T("Auto.Favorite"));
                favorite.IsCheckable = true;
                favorite.IsChecked = favorites.Contains(id);
                favorite.Click += (_, _) =>
                {
                    if (favorite.IsChecked) { if (!favorites.Contains(id)) favorites.Add(id); }
                    else favorites.RemoveAll(value => value == id);
                    ConfigService.Save(_config);
                };
                menu.Items.Add(command);
                menu.Items.Add(favorite);
                button.ContextMenu = menu;
                menu.IsOpen = true;
            };
            return button;
        }

        private ComboBox BuildTargetRuleCombo(AutoRuleStep step)
        {
            var combo = new ComboBox { Style = (Style)FindResource("SelCombo"), Width = 280 };
            void Populate()
            {
                string target = step.TargetRuleId;
                combo.Items.Clear();
                foreach (var rule in _autoRules.Where(rule => rule.Id != _autoEditingId))
                    combo.Items.Add(new ComboBoxItem { Content = rule.Name, Tag = rule.Id });
                var selected = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == target);
                if (selected == null && !string.IsNullOrWhiteSpace(target))
                {
                    selected = new ComboBoxItem { Content = L10n.T("Auto.MissingRule") + " (" + target + ")", Tag = target, IsEnabled = false };
                    combo.Items.Add(selected);
                }
                combo.SelectedItem = selected;
            }
            Populate();
            combo.DropDownOpened += (_, _) => Populate();
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem { Tag: string id }) step.TargetRuleId = id;
            };
            return combo;
        }
    }
}

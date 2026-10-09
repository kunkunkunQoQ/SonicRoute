using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private Button BuildAutomationIconButton(Geometry icon, string labelKey, string id)
        {
            var button = new Button { Content = AutomationIcons.Create(icon, 17), Tag = id, ToolTip = L10n.T(labelKey) };
            button.SetResourceReference(StyleProperty, "AutomationIconButton");
            AutomationProperties.SetName(button, L10n.T(labelKey));
            return button;
        }

        private UIElement BuildAutoRuleRow(AutoRule rule)
        {
            var card = new Border();
            card.SetResourceReference(StyleProperty, "AutomationRuleCard");
            AutomationProperties.SetName(card, rule.Name);
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star), MinWidth = 100 });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 110, MaxWidth = 180 });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            void Add(UIElement element, int column) { Grid.SetColumn(element, column); layout.Children.Add(element); }

            var grip = new Border
            {
                Child = AutomationIcons.Create(AutomationIcons.Grip, 15), Background = Brushes.Transparent,
                Tag = rule.Id, Cursor = Cursors.ScrollNS, ToolTip = L10n.T("Auto.DragRules")
            };
            grip.MouseLeftButtonDown += AutomationRuleGrip_Down;
            grip.MouseMove += AutomationRuleGrip_Move;
            grip.MouseLeftButtonUp += AutomationRuleGrip_Up;
            grip.LostMouseCapture += AutomationRuleGrip_LostCapture;
            Add(grip, 0);
            var action = rule.Actions.FirstOrDefault()?.Action ?? rule.Action;
            Add(AutomationIcons.Create(AutomationActionIcon(action), 22, "Theme.Accent"), 1);

            var name = new TextBlock
            {
                Text = rule.Name, FontSize = 14, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = rule.Name
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextPrimary");
            var nameLine = new DockPanel();
            if (rule.Trigger == AutoRuleTrigger.Schedule && rule.ScheduleMode == 0 && !string.IsNullOrWhiteSpace(rule.LastRunKey))
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 6, Height = 6, Fill = InvertAccentBrush(), Margin = new Thickness(5, 0, 0, 0),
                    ToolTip = L10n.T("Auto.OnceExecuted"), VerticalAlignment = VerticalAlignment.Top
                };
                DockPanel.SetDock(dot, Dock.Right);
                nameLine.Children.Add(dot);
            }
            nameLine.Children.Add(name);
            string summary = BuildAutoActionSummary(rule);
            var subtitle = new TextBlock
            {
                Text = summary, ToolTip = summary, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0)
            };
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
            var conflict = new TextBlock
            {
                Text = L10n.T("Auto.HotkeyConflict"), ToolTip = L10n.T("Auto.HotkeyConflict"), FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0),
                Visibility = IsAutoHotkeyConflict(rule) ? Visibility.Visible : Visibility.Collapsed
            };
            conflict.SetResourceReference(TextBlock.ForegroundProperty, "Theme.OsdTitleText");
            var labels = new StackPanel { Margin = new Thickness(8, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(nameLine);
            labels.Children.Add(subtitle);
            labels.Children.Add(conflict);
            Add(labels, 2);

            var trigger = new Border
            {
                CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 8, 0), MinHeight = 48, VerticalAlignment = VerticalAlignment.Center
            };
            trigger.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceCard");
            trigger.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
            var triggerLayout = new Grid();
            triggerLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            triggerLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            triggerLayout.Children.Add(AutomationIcons.Create(AutomationTriggerIcon(rule.Trigger), 17));
            var triggerLabels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var triggerTitle = new TextBlock { Text = AutomationTriggerTitle(rule.Trigger), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
            triggerTitle.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextPrimary");
            string detail = AutomationTriggerDetail(rule);
            var triggerDetail = new TextBlock
            {
                Text = detail, FontSize = 10.5, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible
            };
            triggerDetail.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
            triggerLabels.Children.Add(triggerTitle);
            triggerLabels.Children.Add(triggerDetail);
            Grid.SetColumn(triggerLabels, 1);
            triggerLayout.Children.Add(triggerLabels);
            trigger.Child = triggerLayout;
            trigger.ToolTip = triggerTitle.Text + (detail.Length == 0 ? "" : "\n" + detail);
            Add(trigger, 3);

            var enabled = new CheckBox
            {
                Tag = rule.Id, IsChecked = rule.Enabled, Width = 34, Margin = new Thickness(2, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = L10n.T(rule.Enabled ? "Auto.Disable" : "Auto.Enable")
            };
            enabled.SetResourceReference(StyleProperty, "AutomationSwitch");
            AutomationProperties.SetName(enabled, L10n.T("Auto.Enable") + " " + rule.Name);
            enabled.Click += AutoToggle_Click;
            Add(enabled, 4);

            var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var run = BuildAutomationIconButton(AutomationIcons.Play, "Auto.Run", rule.Id);
            run.Click += AutoRun_Click;
            var edit = BuildAutomationIconButton(AutomationIcons.Edit, "Auto.Edit", rule.Id);
            edit.Click += AutoEdit_Click;
            var copy = BuildAutomationIconButton(AutomationIcons.Copy, "Auto.Copy", rule.Id);
            copy.Click += AutoCopy_Click;
            var delete = BuildAutomationIconButton(AutomationIcons.Delete, "Auto.Delete", rule.Id);
            delete.Click += AutoDelete_Click;
            actions.Children.Add(run);
            actions.Children.Add(edit);
            actions.Children.Add(copy);
            actions.Children.Add(delete);
            actions.Children.Add(BuildAutoExtrasButton(rule.Id));
            Add(actions, 5);
            card.Child = layout;
            _autoRows[rule.Id] = new AutoRuleRow { Id = rule.Id, Element = card, Run = run, Conflict = conflict };
            UpdateAutomationRowState(_autoRows[rule.Id], _autoRunIds.Contains(rule.Id));
            return card;
        }

        private static Geometry AutomationActionIcon(AutoRuleAction action) => action switch
        {
            AutoRuleAction.SetGlobalMicMute or AutoRuleAction.ToggleGlobalMicMute or AutoRuleAction.SetAppInput or AutoRuleAction.SetSystemInput => AutomationIcons.Microphone,
            AutoRuleAction.LaunchProgram => AutomationIcons.Switch,
            AutoRuleAction.RunPowerShell => AutomationIcons.Terminal,
            AutoRuleAction.ExecuteRule => AutomationIcons.Link,
            AutoRuleAction.ShowOsd => AutomationIcons.Monitor,
            _ => AutomationIcons.Speaker
        };

        private static Geometry AutomationTriggerIcon(AutoRuleTrigger trigger) => trigger switch
        {
            AutoRuleTrigger.Hotkey => AutomationIcons.Keyboard,
            AutoRuleTrigger.Schedule => AutomationIcons.Clock,
            AutoRuleTrigger.AppSwitch => AutomationIcons.Switch,
            AutoRuleTrigger.AppExit => AutomationIcons.Exit,
            AutoRuleTrigger.Startup => AutomationIcons.Play,
            _ => AutomationIcons.Application
        };

        private static string AutomationTriggerTitle(AutoRuleTrigger trigger) => L10n.T(trigger switch
        {
            AutoRuleTrigger.AppStart => "Auto.TriggerAppStart", AutoRuleTrigger.AppSwitch => "Auto.TriggerAppSwitch",
            AutoRuleTrigger.AppExit => "Auto.TriggerAppExit", AutoRuleTrigger.Schedule => "Auto.TriggerSchedule",
            AutoRuleTrigger.Startup => "Auto.TriggerStartup", _ => "Auto.TriggerHotkey"
        });

        private static string AutomationTriggerDetail(AutoRule rule)
        {
            if (rule.Trigger == AutoRuleTrigger.Hotkey) return string.IsNullOrWhiteSpace(rule.Hotkey) ? L10n.T("Auto.HotkeyUnbound") : rule.Hotkey;
            if (rule.Trigger == AutoRuleTrigger.Startup) return "SonicRoute.exe";
            if (rule.Trigger == AutoRuleTrigger.Schedule)
            {
                string text = BuildScheduleTriggerText(rule);
                string prefix = L10n.T("Auto.TriggerSchedule") + "·";
                return text.StartsWith(prefix, StringComparison.Ordinal) ? text.Substring(prefix.Length) : text;
            }
            string app = rule.TriggerApp ?? "";
            return app.Length == 0 || app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app : app + ".exe";
        }

        private static void SetAutomationRunState(Button button, bool running)
        {
            if (button.Content is Viewbox { Child: Path path })
                path.Data = running ? AutomationIcons.Waiting : AutomationIcons.Play;
            button.IsEnabled = !running;
            button.ToolTip = L10n.T(running ? "Auto.Running" : "Auto.RunHint");
            AutomationProperties.SetName(button, L10n.T(running ? "Auto.Running" : "Auto.Run"));
        }

        private void UpdateAutomationRowState(AutoRuleRow row, bool running)
        {
            SetAutomationRunState(row.Run, running);
            if (row.Element is Border card)
                card.Tag = running || (AutoEditCard.Visibility == Visibility.Visible && row.Id == _autoEditingId) ? "active" : null;
        }

        private void UpdateAutomationSelection()
        {
            foreach (var row in _autoRows.Values) UpdateAutomationRowState(row, _autoRunIds.Contains(row.Id));
        }

        private void RevealAutomationEditor()
        {
            UpdateAutomationSelection();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_isClosed && AutomationPage.Visibility == Visibility.Visible && AutoEditCard.Visibility == Visibility.Visible)
                    AutomationScroll.ScrollToTop();
            }));
        }

        private bool SortAutomationRules()
        {
            var previous = _autoRules.ToArray();
            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string id in _config.AutomationRuleOrder ?? new List<string>())
                if (id != null && !order.ContainsKey(id)) order[id] = order.Count;
            _autoRules.Sort((a, b) =>
            {
                int left = order.TryGetValue(a.Id, out int ai) ? ai : int.MaxValue;
                int right = order.TryGetValue(b.Id, out int bi) ? bi : int.MaxValue;
                int compared = left.CompareTo(right);
                if (compared == 0) compared = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                return compared != 0 ? compared : StringComparer.Ordinal.Compare(a.Id, b.Id);
            });
            for (int index = 0; index < previous.Length; index++)
                if (!ReferenceEquals(previous[index], _autoRules[index])) return true;
            return false;
        }

        private void AutoListOptions_Click(object sender, RoutedEventArgs e)
        {
            var menu = ConvenienceMenus.Create();
            menu.PlacementTarget = (UIElement)sender;
            var reset = ConvenienceMenus.Item(L10n.T("Auto.ResetFilters"));
            reset.Click += (_, _) => { AutoSearchBox.Clear(); AutoStateFilter.SelectedIndex = AutoTriggerFilter.SelectedIndex = 0; };
            var order = ConvenienceMenus.Item(L10n.T("Auto.ResetRuleOrder"));
            order.IsEnabled = _config.AutomationRuleOrder?.Count > 0;
            order.Click += (_, _) => SaveAutomationRuleOrder(new List<string>());
            var folder = ConvenienceMenus.Item(L10n.T("Auto.OpenFolder"));
            folder.Click += AutoOpenRuleDir_Click;
            menu.Items.Add(reset);
            menu.Items.Add(order);
            menu.Items.Add(folder);
            menu.IsOpen = true;
        }

        private void SaveAutomationRuleOrder(List<string> ids)
        {
            var previous = _config.AutomationRuleOrder;
            _config.AutomationRuleOrder = ids;
            try { ConfigService.Save(_config); }
            catch { _config.AutomationRuleOrder = previous; ShowToast(L10n.T("Auto.SaveFailed")); return; }
            SortAutomationRules();
            _autoRuleListSignature = null;
            _ = RefreshAutoRulesAsync();
        }

        private FrameworkElement? _automationRuleGrip;
        private string? _automationRuleDragId, _automationRuleDropId;
        private Point _automationRuleDragStart, _automationRulePointer;
        private bool _automationRuleDragging, _automationRuleDropAfter;
        private Border? _automationRuleDropCard;
        private DispatcherOperation? _automationRuleDragOperation;

        private void AutomationRuleGrip_Down(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string id } grip) return;
            EndAutomationRuleDrag(false);
            _automationRuleGrip = grip;
            _automationRuleDragId = id;
            _automationRuleDragStart = e.GetPosition(AutoRuleList);
            if (!grip.CaptureMouse()) EndAutomationRuleDrag(false);
            e.Handled = true;
        }

        private void AutomationRuleGrip_Move(object sender, MouseEventArgs e)
        {
            if (!ReferenceEquals(sender, _automationRuleGrip)) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndAutomationRuleDrag(false); return; }
            _automationRulePointer = e.GetPosition(AutoRuleList);
            if (!_automationRuleDragging)
            {
                if (Math.Abs(_automationRulePointer.Y - _automationRuleDragStart.Y) < Math.Max(6, SystemParameters.MinimumVerticalDragDistance)) return;
                _automationRuleDragging = true;
                if (_automationRuleDragId != null && _autoRows.TryGetValue(_automationRuleDragId, out var source)) source.Element.Opacity = 0.55;
            }
            if (_automationRuleDragOperation?.Status == DispatcherOperationStatus.Pending) return;
            _automationRuleDragOperation = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(UpdateAutomationRuleDrop));
            e.Handled = true;
        }

        private void UpdateAutomationRuleDrop()
        {
            if (_automationRuleGrip == null || !_automationRuleDragging) return;
            var previous = _automationRuleDropCard;
            bool previousAfter = _automationRuleDropAfter;
            _automationRuleDropCard = null;
            _automationRuleDropId = null;
            if (_automationRulePointer.X >= 0 && _automationRulePointer.X <= AutoRuleList.ActualWidth)
            {
                foreach (var rule in _autoRules)
                {
                    if (!_autoRows.TryGetValue(rule.Id, out var row) || row.Element is not Border card || card.Visibility != Visibility.Visible) continue;
                    double top = card.TranslatePoint(new Point(0, 0), AutoRuleList).Y;
                    _automationRuleDropId = rule.Id;
                    _automationRuleDropCard = card;
                    _automationRuleDropAfter = _automationRulePointer.Y >= top + card.ActualHeight / 2;
                    if (_automationRulePointer.Y < top + card.ActualHeight) break;
                }
            }
            if (!ReferenceEquals(previous, _automationRuleDropCard) || previousAfter != _automationRuleDropAfter)
            {
                previous?.ClearValue(Border.BorderBrushProperty);
                previous?.ClearValue(Border.BorderThicknessProperty);
                if (_automationRuleDropId != _automationRuleDragId && _automationRuleDropCard != null)
                {
                    _automationRuleDropCard.SetResourceReference(Border.BorderBrushProperty, "Theme.Accent");
                    _automationRuleDropCard.BorderThickness = _automationRuleDropAfter ? new Thickness(1, 1, 1, 2) : new Thickness(1, 2, 1, 1);
                }
            }
            // 边缘滚动只随拖动输入发生，松手后没有计时器或渲染订阅。
            double y = Mouse.GetPosition(AutomationScroll).Y;
            if (y < 24) AutomationScroll.ScrollToVerticalOffset(Math.Max(0, AutomationScroll.VerticalOffset - 18));
            else if (y > AutomationScroll.ViewportHeight - 24) AutomationScroll.ScrollToVerticalOffset(AutomationScroll.VerticalOffset + 18);
        }

        private void AutomationRuleGrip_Up(object sender, MouseButtonEventArgs e)
        {
            if (!ReferenceEquals(sender, _automationRuleGrip)) return;
            _automationRulePointer = e.GetPosition(AutoRuleList);
            if (_automationRuleDragging) UpdateAutomationRuleDrop();
            EndAutomationRuleDrag(true);
            e.Handled = true;
        }

        private void AutomationRuleGrip_LostCapture(object sender, MouseEventArgs e)
        {
            if (ReferenceEquals(sender, _automationRuleGrip)) EndAutomationRuleDrag(false);
        }

        private void EndAutomationRuleDrag(bool commit)
        {
            string? source = _automationRuleDragId, target = _automationRuleDropId;
            bool moved = _automationRuleDragging, after = _automationRuleDropAfter;
            var grip = _automationRuleGrip;
            _automationRuleGrip = null;
            _automationRuleDragId = _automationRuleDropId = null;
            _automationRuleDragging = false;
            _automationRuleDragOperation?.Abort();
            _automationRuleDragOperation = null;
            _automationRuleDropCard?.ClearValue(Border.BorderBrushProperty);
            _automationRuleDropCard?.ClearValue(Border.BorderThicknessProperty);
            _automationRuleDropCard = null;
            if (source != null && _autoRows.TryGetValue(source, out var row)) row.Element.ClearValue(OpacityProperty);
            if (grip?.IsMouseCaptured == true) grip.ReleaseMouseCapture();
            if (!commit || !moved || source == null || target == null || source == target) return;
            var ids = _autoRules.Select(rule => rule.Id).ToList();
            if (!ids.Remove(source)) return;
            int index = ids.IndexOf(target);
            if (index < 0) return;
            ids.Insert(index + (after ? 1 : 0), source);
            if (!ids.SequenceEqual(_autoRules.Select(rule => rule.Id))) SaveAutomationRuleOrder(ids);
        }
    }
}

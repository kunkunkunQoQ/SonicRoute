using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SonicRoute.Core.Models;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using Path = System.Windows.Shapes.Path;
using Brushes = System.Windows.Media.Brushes;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private sealed class AutoStepView
        {
            internal Border Card = null!;
            internal Border Badge = null!;
            internal Border Parameters = null!;
            internal Path Icon = null!;
        }

        private readonly Dictionary<AutoRuleStep, AutoStepView> _autoStepViews = new();
        private readonly Dictionary<(bool Dark, int Group), (SolidColorBrush bg, SolidColorBrush border, SolidColorBrush fg)> _autoBlockPaletteCache = new();
        private double _autoListScrollOffset;

        private void SetAutomationEditorVisible(bool visible)
        {
            EndAutomationRuleDrag(false);
            EndAutoDrag(false);
            if (visible && AutoEditCard.Visibility != Visibility.Visible)
                _autoListScrollOffset = AutomationScroll.VerticalOffset;
            AutoListPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            AutoEditCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
            {
                _autoCapturingHotkey = false;
                AutomationScroll.ScrollToVerticalOffset(_autoListScrollOffset);
            }
            UpdateAutomationSelection();
        }

        private UIElement BuildAutoStepRow(AutoRuleStep step)
        {
            var (bg, border, fg) = AutoBlockPalette(step.Action);
            var block = new Border
            {
                Background = bg, BorderBrush = border, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11), Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10), Tag = step
            };
            var body = new StackPanel();
            var head = new DockPanel { LastChildFill = true };
            var grip = new Border
            {
                Tag = block, Width = 22, Height = 34, Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.ScrollNS, Margin = new Thickness(0, 0, 8, 0),
                ToolTip = L10n.T("Auto.DragOrder"), VerticalAlignment = VerticalAlignment.Center
            };
            var gripContent = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            gripContent.Children.Add(AutomationIcons.Create(AutomationIcons.Grip, 15));
            // 步骤编号仍供已有拖动状态维护使用，标题不再额外占用一行。
            var number = new TextBlock
            {
                Text = string.Format(L10n.T("Auto.StepNumber"), _autoSteps.IndexOf(step) + 1),
                Visibility = Visibility.Collapsed
            };
            _autoStepNumbers[step] = number;
            AutomationProperties.SetName(grip, number.Text + ", " + L10n.T("Auto.DragOrder"));
            gripContent.Children.Add(number);
            grip.Child = gripContent;
            grip.MouseLeftButtonDown += AutoStepGrip_MouseLeftButtonDown;
            grip.MouseMove += AutoStepGrip_MouseMove;
            grip.MouseLeftButtonUp += AutoStepGrip_MouseLeftButtonUp;
            grip.LostMouseCapture += AutoStepGrip_LostMouseCapture;
            DockPanel.SetDock(grip, Dock.Left);
            head.Children.Add(grip);

            var deleteContent = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(9, 0, 9, 0) };
            deleteContent.Children.Add(AutomationIcons.Create(AutomationIcons.Delete, 15, "Theme.Fail"));
            var deleteText = new TextBlock { Text = L10n.T("Auto.Delete"), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            deleteText.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Fail");
            deleteContent.Children.Add(deleteText);
            var delete = new Button { Content = deleteContent, Tag = step, Height = 34, MinWidth = 68, Margin = new Thickness(7, 0, 0, 0) };
            delete.SetResourceReference(StyleProperty, "GhostButton");
            AutomationProperties.SetName(delete, L10n.T("Auto.Delete"));
            delete.Click += AutoStepDelete_Click;
            DockPanel.SetDock(delete, Dock.Right);
            head.Children.Add(delete);
            var copy = BuildAutomationIconButton(AutomationIcons.Copy, "Auto.Copy", "");
            copy.Tag = step;
            copy.Click += AutoStepCopy_Click;
            DockPanel.SetDock(copy, Dock.Right);
            head.Children.Add(copy);

            var combo = new ComboBox
            {
                Style = (Style)FindResource("SelCombo"), MinWidth = 100,
                BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold,
                MaxDropDownHeight = 440, ClipToBounds = true, Tag = step
            };
            foreach (var item in AutoActionItems()) combo.Items.Add(item);
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is AutoRuleAction action && action == step.Action);
            combo.ToolTip = L10n.T(AutoActionKey(step.Action));
            AutomationProperties.SetName(combo, L10n.T("Auto.Action"));

            var glyph = AutomationIcons.Create(AutomationActionIcon(step.Action), 18);
            var badge = new Border { Background = bg, CornerRadius = new CornerRadius(8), Width = 32, Height = 30, Child = glyph, Margin = new Thickness(4, 0, 0, 0) };
            var glyphPath = (Path)glyph.Child;
            glyphPath.Stroke = fg;
            var actionField = new DockPanel();
            DockPanel.SetDock(badge, Dock.Left);
            actionField.Children.Add(badge);
            actionField.Children.Add(combo);
            var actionBorder = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                MinWidth = 220, MaxWidth = 330, HorizontalAlignment = HorizontalAlignment.Left, Child = actionField
            };
            actionBorder.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceBg");
            actionBorder.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
            head.Children.Add(actionBorder);
            body.Children.Add(head);

            var parameters = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Margin = new Thickness(0, 9, 0, 0) };
            parameters.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceBgAlpha");
            parameters.Child = BuildAutomationStepParameters(step);
            body.Children.Add(parameters);
            block.Child = body;
            _autoStepViews[step] = new AutoStepView { Card = block, Badge = badge, Parameters = parameters, Icon = glyphPath };
            combo.SelectionChanged += AutoStepAction_SelectionChanged;
            return block;
        }

        private UIElement BuildAutomationStepParameters(AutoRuleStep step)
        {
            var parameters = (StackPanel)BuildStepParams(step);
            var row = new Grid { Margin = new Thickness(0, 6, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var label = new TextBlock { Text = L10n.T("Auto.Delay"), VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(StyleProperty, "AutomationFormLabel");
            row.Children.Add(label);
            var inputs = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(inputs, 1);
            row.Children.Add(inputs);
            var delay = new TextBox
            {
                Text = step.DelayMs.ToString(), Width = 80, Height = 30, FontSize = 12.5,
                Padding = new Thickness(8, 3, 8, 3), Tag = step, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left, TextAlignment = TextAlignment.Left
            };
            AutomationProperties.SetName(delay, L10n.T("Auto.Delay"));
            delay.TextChanged += AutoStepDelay_TextChanged;
            inputs.Children.Add(delay);
            foreach (string key in new[] { "Auto.DelayUnit", "Auto.DelayRange" })
            {
                var hint = new TextBlock { Text = L10n.T(key), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
                inputs.Children.Add(hint);
            }
            // 保留每个步骤的失败停止选项，延时显示在字段与该选项之间。
            parameters.Children.Insert(parameters.Children.Count - 1, row);
            return parameters;
        }

        private void UpdateAutomationStepView(AutoRuleStep step)
        {
            if (!_autoStepViews.TryGetValue(step, out var view)) return;
            // 被替换参数区中的下拉不再参与后台刷新，避免长期编辑积累失效控件。
            _autoStepAppCombos.RemoveAll(combo => view.Parameters.IsAncestorOf(combo));
            foreach (var combo in _autoStepDeviceCombos.Keys.Where(combo => view.Parameters.IsAncestorOf(combo)).ToArray())
                _autoStepDeviceCombos.Remove(combo);
            UpdateAutomationStepPalette(step, view);
            view.Parameters.Child = BuildAutomationStepParameters(step);
        }

        private void RefreshAutomationEditorPalette()
        {
            foreach (var entry in _autoStepViews) UpdateAutomationStepPalette(entry.Key, entry.Value);
        }

        private void UpdateAutomationStepPalette(AutoRuleStep step, AutoStepView view)
        {
            var (bg, border, fg) = AutoBlockPalette(step.Action);
            view.Card.Background = bg;
            view.Card.BorderBrush = border;
            view.Badge.Background = bg;
            view.Icon.Data = AutomationActionIcon(step.Action);
            view.Icon.Stroke = fg;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Binding = System.Windows.Data.Binding;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private AutoRule? _autoTemplateDraft;
        private AutoRuleTemplate? _autoTemplate;
        private ContextMenu? _autoTemplateMenu;

        private static Binding TemplateTextBinding(string key) => new Binding("[" + key + "]") { Source = L10n.Instance };

        private void AutoTemplates_Click(object sender, RoutedEventArgs e)
        {
            if (_autoTemplateMenu?.IsOpen == true) { CloseAutomationTemplates(); return; }
            CloseAutomationTemplates();
            var menu = ConvenienceMenus.Create();
            menu.PlacementTarget = AutoTemplatesButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            foreach (var template in AutoRuleTemplates.All)
            {
                var item = ConvenienceMenus.Item("");
                item.Tag = template;
                item.SetBinding(MenuItem.HeaderProperty, TemplateTextBinding(template.NameKey));
                item.SetBinding(FrameworkElement.ToolTipProperty, TemplateTextBinding(template.DescriptionKey));
                item.SetBinding(AutomationProperties.NameProperty, TemplateTextBinding(template.NameKey));
                item.Click += AutoTemplate_Click;
                menu.Items.Add(item);
            }
            _autoTemplateMenu = menu;
            AutoTemplatesButton.ContextMenu = menu;
            menu.Closed += AutomationTemplates_Closed;
            menu.IsOpen = true;
        }

        private void AutomationTemplates_Closed(object sender, RoutedEventArgs e) => CloseAutomationTemplates();

        private void CloseAutomationTemplates()
        {
            var menu = _autoTemplateMenu;
            _autoTemplateMenu = null;
            if (menu == null) return;
            menu.Closed -= AutomationTemplates_Closed;
            menu.IsOpen = false;
            if (AutoTemplatesButton != null) AutoTemplatesButton.ContextMenu = null;
            menu.Items.Clear();
        }

        private void AutoTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: AutoRuleTemplate template }) OpenAutomationTemplate(template);
        }

        private void OpenAutomationTemplate(AutoRuleTemplate template)
        {
            ReleaseAutomationEditor();
            var occupied = new HashSet<string>(_autoRules.Select(rule => rule.Name), StringComparer.OrdinalIgnoreCase);
            string baseName = L10n.T(template.NameKey), name = baseName;
            for (int suffix = 2; occupied.Contains(name); suffix++) name = baseName + " (" + suffix + ")";
            _autoTemplate = template;
            _autoTemplateDraft = template.CreateDraft(name);
            OpenAutomationRuleEditor(_autoTemplateDraft, isTemplate: true);
        }

        private void RefreshAutomationTemplateHint()
        {
            if (AutoTemplateHintText == null) return;
            AutoTemplateHintText.Visibility = _autoTemplate == null ? Visibility.Collapsed : Visibility.Visible;
            AutoTemplateHintText.Text = _autoTemplate == null ? ""
                : L10n.T("Template.DraftHint") + "\n" + L10n.T(_autoTemplate.DescriptionKey);
        }

        private List<AudioDeviceInfo> AutomationDeviceChoices(AutoRuleStep step, EDataFlow flow, IEnumerable<AudioDeviceInfo> devices)
        {
            var display = DisplayDevices(devices);
            return step.Action is AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput
                ? PanelDevices.WithSystemDefault(display, flow, _config) : display;
        }

        // Return the existing localized error key and the failing step, without showing a modal dialog.
        private string? ValidateAutomationEditor(out int stepIndex)
        {
            stepIndex = -1;
            var trigger = SelectedAutoTrigger();
            if (trigger == AutoRuleTrigger.Hotkey && string.IsNullOrWhiteSpace(_autoHotkeyCombo)) return "Auto.HotkeyRequired";
            if (trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppSwitch or AutoRuleTrigger.AppExit
                && string.IsNullOrWhiteSpace((AutoTriggerAppCombo.SelectedItem as AppItem)?.Info.ProcessName)) return "Auto.TriggerAppRequired";
            if (trigger == AutoRuleTrigger.Schedule && AutoScheduleModeCombo.SelectedIndex == 2 && CollectAutoWeekday().Count == 0)
                return "Auto.ScheduleWeekdayRequired";
            for (int index = 0; index < _autoSteps.Count; index++)
            {
                stepIndex = index;
                var step = _autoSteps[index];
                if (step.Action == AutoRuleAction.ExecuteRule && (string.IsNullOrWhiteSpace(step.TargetRuleId)
                    || step.TargetRuleId == (_autoTemplateDraft?.Id ?? _autoEditingId) || AutoRuleStore.Find(step.TargetRuleId) == null))
                    return "Auto.RuleRequired";
                if (AutoRuleActions.NeedsApp(step.Action))
                {
                    if (string.IsNullOrWhiteSpace(step.TargetApp) || (_autoTemplateDraft != null
                        && !_autoStepAppCombos.Any(combo => ReferenceEquals(combo.Tag, step) && combo.SelectedItem is AppItem)))
                        return "Auto.ActionAppRequired";
                }
                if (AutoRuleActions.NeedsDevice(step.Action))
                {
                    if (string.IsNullOrWhiteSpace(step.TargetDeviceId) || (_autoTemplateDraft != null
                        && !_autoStepDeviceCombos.Keys.Any(combo => ReferenceEquals(combo.Tag, step) && combo.SelectedItem is AudioDeviceInfo))
                        || (step.Action is not (AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput) && AudioService.IsSystemDefault(step.TargetDeviceId)))
                        return "Auto.DeviceRequired";
                }
                if ((step.Action == AutoRuleAction.LaunchProgram && step.CurrentLaunchItems().Count == 0)
                    || (step.Action == AutoRuleAction.RunPowerShell && step.ProgramPaths.All(string.IsNullOrWhiteSpace)))
                    return "Auto.ProgramRequired";
            }
            stepIndex = -1;
            return null;
        }

        private AutoRule SaveAutomationEditorRule()
        {
            var trigger = SelectedAutoTrigger();
            var rule = _autoTemplateDraft ?? (_autoEditingId == null ? null : AutoRuleStore.Find(_autoEditingId)) ?? new AutoRule();
            rule.Name = AutoNameBox.Text.Trim();
            rule.Trigger = trigger;
            rule.Hotkey = _autoHotkeyCombo.Trim();
            rule.TriggerApp = trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppSwitch or AutoRuleTrigger.AppExit
                ? (AutoTriggerAppCombo.SelectedItem as AppItem)?.Info.ProcessName ?? "" : "";
            rule.ScheduleMode = trigger == AutoRuleTrigger.Schedule ? Math.Max(0, AutoScheduleModeCombo.SelectedIndex) : 0;
            rule.ScheduleTime = trigger == AutoRuleTrigger.Schedule ? CollectAutoScheduleTime() : "";
            rule.ScheduleWeekdays = trigger == AutoRuleTrigger.Schedule ? CollectAutoWeekday() : new List<int>();
            rule.SetSteps(_autoSteps);
            if (_autoTemplateDraft != null) rule.Enabled = false;
            // Preserve the saved enable state on edit. Ordinary new rules keep their existing enabled default.
            AutoRuleStore.Save(rule, L10n.T("Auto.DefaultNamePrefix"));
            return rule;
        }
    }
}

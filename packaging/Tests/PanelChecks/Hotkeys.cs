using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SonicRoute;
using SonicRoute.Core;

internal static partial class Program
{
    private static void CheckDeviceHotkeysAndTraySettings(MethodInfo theme, List<Window> windows)
    {
        var config = ConfigService.Load();
        var devices = new[] { HotkeyActions.ActSwitchOutput, HotkeyActions.ActSwitchInput,
            HotkeyActions.ActSwitchAllOutput, HotkeyActions.ActSwitchAllInput,
            HotkeyActions.ActSetDefaultOutput, HotkeyActions.ActSetDefaultInput };
        foreach (string mode in new[] { "light", "dark" })
        {
            config.ExperimentalMic = true; config.Hotkeys.Clear();
            config.Hotkeys["切换当前应用快捷设备"] = "Ctrl+F13";
            config.Hotkeys["切换系统默认输入设备"] = "";
            config.Hotkeys["静音当前应用"] = "Ctrl+F14";
            config.Hotkeys["静音当前应用麦克风"] = "Ctrl+F15";
            theme.Invoke(null, new object[] { mode, "blue" });
            var window = new MainWindow(); windows.Add(window);
            Check(Field<Grid?>(window, "SettingsPage") == null, mode + " tray settings remain deferred until settings page is opened");
            Call(window, "EnsureSettingsPage");
            config.PanelChangeSystemDefault = true; config.TrayWheelEverywhere = false;
            Call(window, "LoadSettings");
            var systemDefault = Field<CheckBox>(window, "SettingsPanelChangeSysDef");
            var trayArea = Field<CheckBox>(window, "SettingsTrayWheelEverywhere");
            Check(systemDefault.IsChecked == true && trayArea.IsChecked == false, mode + " quick panel system-default on and own-icon tray area off are displayed");
            Check(typeof(MainWindow).GetField("SettingsTrayAltWheelSystemDefault", Instance) == null
                && typeof(AppConfig).GetProperty("TrayAltWheelSystemDefault") == null, mode + " cancelled tray child option has no control or config residue");
            Check(ReferenceEquals(trayArea.Style, window.FindResource("SettingsSwitchRow")), mode + " tray row reuses existing settings style");
            config.PanelChangeSystemDefault = false; config.TrayWheelEverywhere = true; Call(window, "LoadSettings");
            Check(systemDefault.IsChecked == false && trayArea.IsChecked == true, mode + " existing saved settings continue to display their explicit values");
            Call(window, "EnsureHotkeysPage"); Call(window, "BuildHotkeyList");
            var list = Field<ItemsControl>(window, "HotkeyList");
            Dictionary<string, Button> Buttons() => list.Items.OfType<DockPanel>().SelectMany(row => row.Children.OfType<Button>()).ToDictionary(button => (string)button.Tag);
            string Text(Button button) => ((TextBlock)button.Content).Text;
            var buttons = Buttons();
            Check(buttons.Count == 14 && devices.All(buttons.ContainsKey) && buttons.ContainsKey(HotkeyActions.ActMute)
                && !buttons.ContainsKey("静音当前应用麦克风"), mode + " playback mute is restored while retired app microphone shortcut is absent");
            Check(Text(buttons[HotkeyActions.ActMute]).StartsWith("Ctrl+F14", StringComparison.Ordinal),
                mode + " existing customized playback mute binding is restored");
            foreach (string action in devices)
            {
                var row = (DockPanel)buttons[action].Parent;
                Check(row.Children.OfType<TextBlock>().Single().Text == HotkeyActions.DisplayName(action), mode + " original localized action label restored: " + action);
            }
            Check(Text(buttons[HotkeyActions.ActSwitchOutput]).StartsWith("Ctrl+F13", StringComparison.Ordinal), mode + " original customized device binding remains visible");
            Check(string.IsNullOrEmpty(Text(buttons[HotkeyActions.ActSetDefaultInput])), mode + " original explicitly cleared binding remains cleared");
            buttons[HotkeyActions.ActSwitchOutput].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<string?>(window, "_recordingAction") == HotkeyActions.ActSwitchOutput, mode + " original device button enters existing recorder");
            config.ExperimentalMic = false; Call(window, "BuildHotkeyList"); buttons = Buttons();
            Check(buttons.Count == 12 && !buttons.ContainsKey(HotkeyActions.ActSwitchInput) && !buttons.ContainsKey(HotkeyActions.ActSwitchAllInput)
                && !buttons.ContainsKey("静音当前应用麦克风"), mode + " microphone off hides only the two remaining app-input device actions");
            Check(buttons.ContainsKey(HotkeyActions.ActSetDefaultInput), mode + " system default input shortcut remains available");
            config.ExperimentalMic = true; Call(window, "BuildHotkeyList");
            Check(Buttons().Count == 14 && Field<string?>(window, "_recordingAction") == null, mode + " rebuild restores current list and ends capture");
            window.Close();
            Check(Field<ItemsControl?>(window, "HotkeyList") == null, mode + " closing window releases original hotkey rows through existing cleanup");
            Check(Field<CheckBox?>(window, "SettingsTrayWheelEverywhere") == null && Field<CheckBox?>(window, "SettingsPanelChangeSysDef") == null, mode + " closing window releases settings through existing cleanup");
        }
    }
}

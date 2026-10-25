using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SonicRoute;
using SonicRoute.Core;

internal static partial class Program
{
    private static void CheckDirectionalHotkeys(MethodInfo theme, List<Window> windows)
    {
        var config = ConfigService.Load();
        var previous = new[] { HotkeyActions.ActSwitchOutputPrevious, HotkeyActions.ActSwitchInputPrevious,
            HotkeyActions.ActSwitchAllOutputPrevious, HotkeyActions.ActSwitchAllInputPrevious,
            HotkeyActions.ActSetDefaultOutputPrevious, HotkeyActions.ActSetDefaultInputPrevious };
        foreach (string mode in new[] { "light", "dark" })
        {
            config.ExperimentalMic = true; config.Hotkeys.Clear();
            config.Hotkeys["切换当前应用快捷设备"] = "Ctrl+F13";
            config.Hotkeys["切换系统默认输入设备"] = "";
            theme.Invoke(null, new object[] { mode, "blue" });
            var window = new MainWindow(); windows.Add(window);
            Call(window, "EnsureHotkeysPage"); Call(window, "BuildHotkeyList");
            var list = Field<ItemsControl>(window, "HotkeyList");
            Dictionary<string, Button> Buttons() => list.Items.OfType<DockPanel>().SelectMany(row => row.Children.OfType<Button>()).ToDictionary(button => (string)button.Tag);
            string Text(Button button) => ((TextBlock)button.Content).Text;
            var buttons = Buttons();
            Check(buttons.Count == 20 && previous.All(buttons.ContainsKey), mode + " actual hotkey BAML lists 12 directional device actions and 8 retained actions");
            foreach (string action in previous)
            {
                var row = (DockPanel)buttons[action].Parent;
                Check(row.Children.OfType<TextBlock>().Single().Text == HotkeyActions.DisplayName(action)
                    && !Text(buttons[action]).Contains("⚠") && !string.IsNullOrWhiteSpace(Text(buttons[action])), mode + " previous action has localized label and explicit unset button: " + action);
            }
            Check(Text(buttons[HotkeyActions.ActSwitchOutputNext]).StartsWith("Ctrl+F13", StringComparison.Ordinal), mode + " old customized binding displays under next");
            Check(Text(buttons[HotkeyActions.ActSetDefaultInputNext]) == Text(buttons[HotkeyActions.ActSetDefaultInputPrevious]), mode + " old explicitly cleared next binding stays unset");
            buttons[HotkeyActions.ActSwitchOutputPrevious].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<string?>(window, "_recordingAction") == HotkeyActions.ActSwitchOutputPrevious, mode + " previous button enters existing binding recorder");
            config.ExperimentalMic = false; Call(window, "BuildHotkeyList"); buttons = Buttons();
            Check(buttons.Count == 16 && buttons.Keys.All(a => !HotkeyActions.RequiresMicrophoneOption(a)), mode + " microphone off removes both app-input directions");
            Check(buttons.ContainsKey(HotkeyActions.ActSetDefaultInputPrevious) && buttons.ContainsKey(HotkeyActions.ActSetDefaultInputNext), mode + " system default input directions remain available");
            config.ExperimentalMic = true; Call(window, "BuildHotkeyList");
            Check(Buttons().Count == 20 && Field<string?>(window, "_recordingAction") == null, mode + " rebuild restores input pairs and ends prior capture");
            window.Close();
            Check(Field<ItemsControl?>(window, "HotkeyList") == null, mode + " closing window releases directional hotkey rows through existing cleanup");
        }
    }
}

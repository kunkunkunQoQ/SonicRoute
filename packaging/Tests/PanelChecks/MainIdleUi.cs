using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void CheckMainIdleUi()
    {
        ThemeService.Apply("light", "blue");
        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false
        };
        // Keep the Full window's initial phone read pending in this UI-only fixture.
        var phoneRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(MainWindow).GetField("_phoneInitialization", Instance)?.SetValue(window, phoneRead.Task);
        var app = (App)Application.Current;
        var mainField = typeof(App).GetField("_mainWindow", Instance)!;
        mainField.SetValue(app, window);
        typeof(App).GetField("_startupWarmupComplete", Instance)!.SetValue(app, true);
        DispatcherTimer? Timer() => (DispatcherTimer?)typeof(App).GetField("_uiReclaimTimer", Instance)!.GetValue(app);
        void Navigate(string name) => Field<RadioButton>(window, name).IsChecked = true;
        void Release() => typeof(App).GetMethod("ReclaimUiWhenIdle", Instance)!.Invoke(app, new object?[] { null, EventArgs.Empty });
        try
        {
            window.Show();
            OutputWait(() => Field<bool>(window, "_initialDeviceLoadComplete"), "main window finished initial device read");
            SeedSections(window, 8);
            ConfigService.Load().CollapseDeviceSections = true;
            AutoRuleStore.Save(new AutoRule { Name = "Disabled idle UI fixture", Enabled = false });

            Navigate("NavHotkeys");
            var keys = Items(window, "HotkeyList");
            int keyCount = keys.Items.Count;
            Navigate("NavAutomation");
            var rules = Items(window, "AutoRuleList");
            OutputWait(() => rules.Items.Count == 1, "disabled rule has a visible card in the isolated store");
            Field<Button>(window, "AutoNewButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var draft = Field<TextBox>(window, "AutoNameBox");
            draft.Text = "unsaved rule draft";
            Navigate("NavSettings");
            var keep = Field<ToggleButton>(window, "KeepDevicesMoreToggle");
            var rename = Field<ToggleButton>(window, "DeviceNamesMoreToggle");
            void Toggle(ToggleButton button, bool value)
            {
                button.IsChecked = value;
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            Toggle(keep, true); Toggle(rename, true); Toggle(keep, false);
            var filters = Items(window, "OutputFilterList");
            var names = Items(window, "OutputNameList");
            var editor = NameBox(names.Items[2]);
            editor.Text = "alias with trailing spaces  ";
            editor.Select(2, 3);
            int selectionStart = editor.SelectionStart, selectionLength = editor.SelectionLength;
            Release();
            Check(keyCount > 0 && keys.Items.Count == keyCount && rules.Items.Count == 1 && filters.Items.Count == 9,
                "visible main window keeps generated controls");

            SetField(window, "_appIconImportBusy", true);
            window.WindowState = WindowState.Minimized;
            var timer = Timer();
            Check(timer is { IsEnabled: true } && timer.Interval == TimeSpan.FromSeconds(30),
                "minimizing waits 30 seconds on the application's idle timer");
            window.WindowState = WindowState.Normal;
            Check(!timer!.IsEnabled && keys.Items.Count == keyCount, "quick restore cancels reclamation and retains warm rows");
            window.WindowState = WindowState.Minimized;
            Check(ReferenceEquals(timer, Timer()) && timer.IsEnabled, "minimizing again reuses the same timer");
            Release();
            Check(keys.Items.Count == keyCount && rules.Items.Count == 1, "active UI operation protects minimized window");
            SetField(window, "_appIconImportBusy", false);
            OutputWait(() => !Field<bool>(window, "_nameSavePending"), "pending device alias has finished saving");
            Release();
            Check(keys.Items.Count == 0 && rules.Items.Count == 0 && filters.Items.Count == 0,
                "minimized window drops inactive hotkey and automation rows and folded device rows");
            Check(names.Items.Count == 9 && ReferenceEquals(editor, NameBox(names.Items[2]))
                && editor.Text == "alias with trailing spaces  " && editor.SelectionStart == selectionStart && editor.SelectionLength == selectionLength,
                "current expanded device editor keeps its text, selection and control");
            Check(draft.Text == "unsaved rule draft", "reclaim preserves an unsaved automation draft on another page");
            window.WindowState = WindowState.Normal;
            Toggle(keep, true);
            Check(filters.Items.Count == 9, "expanding device filters rebuilds reclaimed rows");
            Navigate("NavHotkeys");
            Check(keys.Items.Count == keyCount, "returning to hotkeys rebuilds the list from current registrations");
            Navigate("NavAutomation");
            OutputWait(() => rules.Items.Count == 1, "returning to automation rebuilds a rule card with unchanged store revision");
            Check(ReferenceEquals(draft, Field<TextBox>(window, "AutoNameBox")) && draft.Text == "unsaved rule draft",
                "restored automation page keeps the original editor and draft");
            window.Hide();
            Check(ReferenceEquals(timer, Timer()) && timer.IsEnabled, "hiding the restored window schedules the same timer");
            Release();
            Check(keys.Items.Count == 0 && names.Items.Count == 0 && rules.Items.Count == 1,
                "hidden window drops previous settings and hotkey rows and preserves its current automation page");
            window.Show();
            Navigate("NavSettings");
            Toggle(rename, true);
            Check(NameBox(names.Items[2]).Text == "alias with trailing spaces",
                "regenerated device row displays its committed alias");
            var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost");
            if (host != null) Check(!(bool)host.GetProperty("IsAdbCreated", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!,
                "UI test used no ADB client");
        }
        finally
        {
            mainField.SetValue(app, null);
            window.Close();
            Timer()?.Stop();
            phoneRead.TrySetResult(true);
            DrainUi();
        }
    }
}

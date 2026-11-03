using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

internal static partial class Program
{
    private static void CheckPhoneDeviceManagement(Catalog catalog, AndroidPhoneStore store, string output)
    {
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var first = catalog.Device with { State = "device" };
        store.RememberDevice(first);
        string firstId = store.Profile.Id;
        string firstOriginalName = store.Profile.Name;
        store.Rename("工作手机");
        var second = first with
        {
            Serial = "phone-layout-secondary-serial",
            Name = "客厅手机",
            HardwareId = string.IsNullOrWhiteSpace(first.HardwareId) ? "phone-layout-secondary-hardware" : first.HardwareId + "-secondary",
            TransportId = "phone-layout-secondary-transport",
            IsWifi = !first.IsWifi
        };
        store.RememberDevice(second);
        string secondId = store.Profile.Id;
        store.Rename("客厅手机");
        store.SelectProfile(firstId);
        cache.GetMethod("SetDevices", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { new[] { first, second } });

        var main = new MainWindow();
        try
        {
            Check(main.FindName("OverviewPage") == null && main.FindName("NavOverview") == null
                && ((RadioButton)main.FindName("NavApps")).IsChecked == true && typeof(MainWindow).GetMethod("RefreshOverviewAsync", Hidden) == null,
                "overview navigation, visual tree and controller are removed and Apps is the default page");
            typeof(MainWindow).GetMethod("InitializePhoneManagement", Hidden)!.Invoke(main, new object[] { false });
            typeof(MainWindow).GetMethod("SelectPhoneManagement", Hidden)!.Invoke(main, null);
            var page = (PhoneDevicePage)((Border)main.FindName("PhoneDetailsHost")).Child;
            Check(((FrameworkElement)main.FindName("AppsDetailsScroll")).Visibility == Visibility.Collapsed
                && ((StackPanel)main.FindName("PhoneDevicesHost")).Visibility == Visibility.Visible,
                "selecting the phone switches the existing Apps details pane to device management");

            var sidebar = (StackPanel)main.FindName("PhoneDevicesHost");
            var profileButtons = sidebar.Children.OfType<Button>().Where(b => b.Content is FrameworkElement).ToArray();
            var addButton = sidebar.Children.OfType<Button>().Single(b => b.Content?.ToString() == "＋  添加设备");
            string SidebarName(Button button) => Descendants<TextBlock>(button).FirstOrDefault()?.Text ?? "";
            Check(store.Profiles.Count == 2 && profileButtons.Length == 2
                && profileButtons.Select(SidebarName).ToHashSet().SetEquals(new[] { "工作手机", "客厅手机" })
                && sidebar.Children.OfType<Button>().Count() == 3,
                "two distinct hardware identities create separate named phone rows beside Add Device");

            var retained = (ItemsControl)page.FindName("RetainedApps");
            int retainedCount = store.Profile.RetainedApps.Count;
            Check(retained.Items.Count == retainedCount && ((TextBlock)page.FindName("RetainedCount")).Text == $"{retainedCount} 个",
                "selected phone binds its saved retained-app list and count without enumerating installed apps");
            Check(addButton.Content?.ToString() == "＋  添加设备",
                "the phone sidebar keeps a separate Add Device action");

            store.Rename("书房手机");
            var firstButton = profileButtons.Single(b => SidebarName(b) == "书房手机");
            Check(store.GetDisplayName(first) == "书房手机" && store.GetDisplayName(second) == "客厅手机"
                && store.GetDisplayName(first with { Name = "Raw model" }) == "书房手机",
                "custom phone names resolve by hardware identity without leaking raw model names or another phone's alias");
            Check(((TextBlock)page.FindName("DeviceName")).Text == "书房手机" && SidebarName(firstButton) == "书房手机",
                "renaming the active phone updates only its profile row and management name");
            store.Rename("工作手机");
            var rename = (Button)page.FindName("RenameButton"); var nameEditor = (TextBox)page.FindName("PhoneNameBox");
            Check(page.FindName("PageHeading") == null && rename.Parent == ((TextBlock)page.FindName("DeviceName")).Parent
                && rename.Style == main.FindResource("GhostButton") && rename.Width == 26,
                "duplicate phone heading is removed and the desktop rename button sits beside the device name");
            rename.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(nameEditor.Visibility == Visibility.Visible && nameEditor.Text == "工作手机",
                "phone rename opens the prefilled inline editor for the selected profile");
            nameEditor.Text = "书房手机";
            Check(store.Profile.Name == "书房手机" && SidebarName(firstButton) == "书房手机",
                "inline phone edits save to the selected profile and update its sidebar row");
            nameEditor.Text = "工作手机";
            typeof(PhoneDevicePage).GetMethod("Name_LostFocus", Hidden)!.Invoke(page, new object?[] { nameEditor, null });
            Check(nameEditor.Visibility == Visibility.Collapsed, "leaving the inline name editor ends editing without extra confirmation");

            Render((FrameworkElement)main.Content, 1040, 720, System.IO.Path.Combine(output, "phone-management-light.png"));
            var firstChoice = retained.Items.Cast<PhoneAppChoice>().First();
            var quickPanelToggle = Descendants<CheckBox>(retained).First(c =>
                System.Windows.Automation.AutomationProperties.GetName(c) == "在快速手机面板显示");
            Check(Descendants<Button>(retained).Any(b => b.ToolTip?.ToString()?.Contains("快速面板") == true)
                && quickPanelToggle.Style == page.FindResource("AutomationSwitch")
                && ((Button)page.FindName("AddAppsButton")).Content?.ToString() == "＋  添加快速面板显示应用",
                "retained rows pair a pin with the shared AutomationSwitch and use the updated add-app wording");
            quickPanelToggle.IsChecked = false;
            Check(store.Profile.RetainedApps.Any(a => a.Package == firstChoice.Item.App.Package)
                && store.Profile.QuickPanelApps.All(a => a.Package != firstChoice.Item.App.Package)
                && retained.Items.Count == retainedCount,
                "turning off quick-panel visibility hides an app without removing its retained membership");
            quickPanelToggle.IsChecked = true;
            Check(store.Profile.RetainedApps.Any(a => a.Package == firstChoice.Item.App.Package)
                && store.Profile.QuickPanelApps.Any(a => a.Package == firstChoice.Item.App.Package)
                && retained.Items.Count == retainedCount,
                "turning visibility back on restores the saved app without losing its membership");

            foreach (bool dark in new[] { false, true })
            {
                ThemeService.Apply(dark ? "dark" : "light", "#00CBE9");
                Render((FrameworkElement)main.Content, 1040, 720, System.IO.Path.Combine(output, dark ? "phone-management-dark.png" : "phone-management-light.png"));
                Check(!Descendants<TextBlock>(page).Any(t => t.Text == "⠿")
                    && Descendants<Button>(page).Any(b => b.Content?.ToString() == "＋  添加快速面板显示应用"),
                    (dark ? "dark" : "light") + " management uses a compact retained-app list without drag handles");
                Check(Grid.GetColumn((FrameworkElement)page.FindName("DeviceCommands")) == 2
                    && !Descendants<Button>((ItemsControl)page.FindName("RetainedApps")).Any(b => b.Content?.ToString() == "接收"),
                    (dark ? "dark" : "light") + " device commands stay on the right and retained rows contain only the pin action");
                var label = Descendants<TextBlock>(page).First(t => t.Text == store.Profile.RetainedApps[0].Name);
                Check(((System.Windows.Media.SolidColorBrush)label.Foreground).Color == ((System.Windows.Media.SolidColorBrush)page.FindResource("Theme.TextPrimary")).Color,
                    (dark ? "dark" : "light") + " retained app names follow the active text color inside generated rows");
            }

            var secondButton = profileButtons.Single(b => SidebarName(b) == "客厅手机");
            secondButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(store.Profile.Id == secondId && ((TextBlock)page.FindName("DeviceName")).Text == "客厅手机"
                && ((TextBlock)page.FindName("DeviceDescription")).Text.Contains("已连接"),
                "selecting the second sidebar row activates its matching online device profile");
            firstButton = profileButtons.Single(b => SidebarName(b) == "工作手机");
            firstButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            cache.GetMethod("SetDevices", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { new[] { second } });
            DrainUi();
            Check(profileButtons.Length == 2 && SidebarName(firstButton) == "工作手机"
                && Descendants<TextBlock>(firstButton).Any(t => t.Text == "未连接")
                && store.Profile.Id == firstId
                && ((TextBlock)page.FindName("DeviceDescription")).Text.Contains("未连接")
                && ((Button)page.FindName("ReconnectButton")).IsEnabled
                && !((Button)page.FindName("DisconnectButton")).IsEnabled,
                "an offline saved phone remains named and reconnectable without a fabricated online state or profile switch");

            typeof(PhoneDevicePage).GetMethod("ShowConnection", Hidden)!.Invoke(page, null);
            Check(((FrameworkElement)page.FindName("ConnectionPanel")).Visibility == Visibility.Visible
                && ((TextBox)page.FindName("PairCodeBox")).MaxLength == 6
                && ((FrameworkElement)page.FindName("PairQrImage")).Visibility == Visibility.Collapsed
                && ((Button)page.FindName("AddAppsButton")).Content?.ToString() == "＋  添加快速面板显示应用",
                "ShowConnection explicitly opens the pairing page without starting ADB or discarding saved phone profiles");
            Render((FrameworkElement)main.Content, 1040, 720, System.IO.Path.Combine(output, "phone-connection-dark.png"));
            Render((FrameworkElement)main.Content, 1250, 720, System.IO.Path.Combine(output, "phone-pairing-wide-dark.png")); DrainUi(); main.UpdateLayout();
            Check(Grid.GetColumn((Border)page.FindName("CodePairCard")) == 1, "wide connection page puts QR and pairing-code methods side by side");
            Render((FrameworkElement)main.Content, 780, 720, System.IO.Path.Combine(output, "phone-pairing-narrow-dark.png")); DrainUi(); main.UpdateLayout();
            Check(Grid.GetRow((Border)page.FindName("CodePairCard")) == 1, "narrow connection page stacks pairing methods without clipping inputs");

            var token = (System.Threading.CancellationTokenSource)typeof(PhoneDevicePage).GetField("_lifetime", Hidden)!.GetValue(page)!;
            main.Close();
            Check(token.IsCancellationRequested && ((TextBox)page.FindName("PairCodeBox")).Text.Length == 0,
                "closing the complete interface cancels phone-page work and clears transient pairing inputs");
            store.Rename(firstOriginalName);
        }
        finally { main.Close(); ThemeService.Apply("light", "#00CBE9"); }
    }
}

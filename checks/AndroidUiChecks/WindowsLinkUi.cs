using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static int WindowsLinkUi(string output, bool exerciseTouch = false)
    {
        Directory.CreateDirectory(output);
        string state = Path.Combine(Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!, "WindowsLink");
        Directory.CreateDirectory(state);
        string peerId = new('A', 64);
        File.WriteAllText(Path.Combine(state, "trusted.json"), JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = peerId,
                Name = "Fixture PC",
                Address = "127.0.0.1:46916",
                Capabilities = 127 | 8192,
                AutoConnect = false
            }
        }));
        const BindingFlags staticMember = BindingFlags.Static | BindingFlags.NonPublic;
        Type hub = typeof(App).Assembly.GetType("SonicRoute.WindowsLinkHub")!;
        var offline = new AndroidDevice("ui-fixture", "Android", "offline", false, "win-link-ui");
        var backup = new AndroidDevice("ui-fixture-backup", "Android Backup", "offline", false, "win-link-ui-backup");
        IReadOnlyList<AndroidDevice> androidDevices = [offline, backup];
        var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult(androidDevices));
        discovery.Publish(androidDevices);
        Type cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        cache.GetProperty("Discovery", staticMember)!.SetValue(null, discovery);
        Type androidHost = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var phones = (AndroidPhoneStore)androidHost.GetProperty("Phone", staticMember)!.GetValue(null)!;
        phones.RememberDevice(offline);
        phones.Retain(new AndroidApp("cn.fixture.music", "Android Music", 10, "media"));
        phones.RememberDevice(backup);
        phones.SelectProfile(phones.FindProfile(offline)!.Id);

        using var page = new PhoneDevicePage();
        var quick = new PhoneAudioWindow(loadDevicesOnShow: false);
        var main = new MainWindow();
        var modernPanel = new QuickPanelModernWindow();
        try
        {
            VerifyNineLanguageKeys();
            CheckSilenceLeaseFailures();
            VerifyWindowsLinkLocalAudioCatalog(hub);

            typeof(PhoneDevicePage).GetMethod("ShowConnection", Hidden)!.Invoke(page, null);
            var windowsChoice = (RadioButton)page.FindName("WindowsLinkConnectionChoice");
            var android = (RadioButton)page.FindName("AndroidConnectionChoice");
            var airPlay = (RadioButton)page.FindName("AirPlayConnectionChoice");
            var candidate = (ContentControl)page.FindName("WindowsLinkCard");
            Check(android.IsChecked == true && airPlay.IsChecked == false && windowsChoice.IsChecked == false,
                "Add Device preserves Android selection and exposes Windows Link beside AirPlay");

            windowsChoice.IsChecked = true;
            Check(IsWindowsLinkConnectView(candidate.Content) && candidate.Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("PairingCard")).Visibility == Visibility.Collapsed,
                "Windows Link mounts WindowsLinkConnectView inside Add Device and hides Android pairing");

            var connectView = (FrameworkElement)candidate.Content!;
            RefreshWindowsLinkView(connectView);
            var widePeersList = (ItemsControl)connectView.FindName("DiscoveredPeersList");
            Check(((FrameworkElement)connectView.FindName("ReceiverHeader")).Visibility == Visibility.Visible
                && connectView.FindName("ServiceToggle") is CheckBox { Visibility: Visibility.Visible }
                && widePeersList.Visibility == Visibility.Visible && widePeersList.Items.Count == 1
                && ((FrameworkElement)connectView.FindName("SearchHintText")).Visibility == Visibility.Collapsed
                && Grid.GetRow((UIElement)connectView.FindName("DirectButtonsGrid")) == 0,
                "wide WindowsLinkConnectView shows ReceiverHeader, saved peer row, and single-row direct connection form");

            var preview = new Border { Child = page, Padding = new Thickness(10) };
            preview.Resources["AutomationSwitch"] = main.FindResource("AutomationSwitch");
            preview.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceBg");
            foreach (string theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme, "#00CBE9");
                Render(preview, 760, 560, Path.Combine(output, $"add-device-windows-link-{theme}.png"));
            }
            Check(DirectFormIsOpen(connectView), "the device page renders the address field and Connect button without a disclosure");
            ThemeService.Apply("light", "#00CBE9");

            airPlay.IsChecked = true;
            Check(candidate.Visibility == Visibility.Collapsed
                && ((FrameworkElement)page.FindName("AirPlayCard")).Visibility == Visibility.Visible,
                "switching to AirPlay hides the embedded Windows Link surface");
            android.IsChecked = true;
            Check(((FrameworkElement)page.FindName("PairingCard")).Visibility == Visibility.Visible,
                "switching back restores the existing Android pairing card");

            var combo = (ComboBox)quick.FindName("DeviceCombo");
            quick.Show();
            DrainUi();
            object airPlayChoice = typeof(PhoneAudioWindow).GetField("AirPlayChoice", staticMember)!.GetValue(null)!;
            object windowsLinkChoice = typeof(PhoneAudioWindow).GetField("WindowsLinkChoice", staticMember)!.GetValue(null)!;
            object savedPc = combo.Items.Cast<object>().Single(item => DeviceId(item) == peerId);
            object[] choices = [.. androidDevices, savedPc, airPlayChoice, windowsLinkChoice];
            combo.SelectedItem = androidDevices[0];
            DrainUi();
            var footerCard = (Border)quick.FindName("PhoneDeviceCard");
            double androidHeight = footerCard.ActualHeight;
            Check(PeerName(savedPc) == "Fixture PC" && combo.Items.Cast<object>().SequenceEqual(choices),
                "the dropdown lists saved Android and Windows devices before the AirPlay and Windows Link entries");

            combo.IsDropDownOpen = true;
            DrainUi();
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
            var menu = (FrameworkElement)popup.Child;
            var savedRow = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromItem(savedPc);
            var airPlayRow = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromItem(airPlayChoice);
            var windowsLinkRow = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromItem(windowsLinkChoice);
            Point top = new(0, 0);
            Check(popup.IsOpen && savedRow.TranslatePoint(top, menu).Y < airPlayRow.TranslatePoint(top, menu).Y
                && airPlayRow.TranslatePoint(top, menu).Y < windowsLinkRow.TranslatePoint(top, menu).Y,
                "the rendered dropdown positions AirPlay and Windows Link beneath the saved PC");
            Render(menu, (int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), Path.Combine(output, "quick-device-order.png"));
            combo.IsDropDownOpen = false;

            object service = hub.GetProperty("Service", staticMember)!.GetValue(null)!;
            Check(!(bool)service.GetType().GetProperty("IsEnabled")!.GetValue(service)!
                && !File.Exists(Path.Combine(state, "identity.dpapi")),
                "restoring the saved device from disk does not start Windows Link or create an identity");

            combo.SelectedItem = windowsLinkChoice;
            DrainUi();
            var pane = (ScrollViewer)quick.FindName("WindowsLinkPane");
            var compactView = (FrameworkElement)pane.Content!;
            RefreshWindowsLinkView(compactView);
            quick.UpdateLayout();
            var compactPeersList = (ItemsControl)compactView.FindName("DiscoveredPeersList");
            Check(pane.Visibility == Visibility.Visible && IsWindowsLinkConnectView(compactView)
                && ((FrameworkElement)quick.FindName("AirPlayPane")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)compactView.FindName("ReceiverHeader")).IsVisible
                && compactView.FindName("ServiceToggle") is CheckBox { IsVisible: true }
                && compactPeersList.Visibility == Visibility.Visible && compactPeersList.Items.Count == 1
                && DirectFormIsOpen(compactView)
                && Grid.GetRow((UIElement)compactView.FindName("DirectButtonsGrid")) == 0
                && ((FrameworkElement)quick.FindName("RefreshButton")).Visibility == Visibility.Collapsed,
                "quick Windows Link setup keeps its service switch at the top and its direct connection action beside the address");

            foreach (string theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme, "#00CBE9");
                Render((FrameworkElement)quick.Content, 235, 450, Path.Combine(output, $"quick-windows-link-{theme}.png"));
            }
            ThemeService.Apply("light", "#00CBE9");

            combo.SelectedItem = airPlayChoice;
            Check(pane.Visibility == Visibility.Collapsed
                && ((FrameworkElement)quick.FindName("AirPlayPane")).Visibility == Visibility.Visible
                && ((TextBlock)quick.FindName("ConnectionText")).Visibility == Visibility.Visible,
                "switching from Windows Link shows AirPlay with the shared connection footer");

            Check(quick.FindName("WindowsLinkSettingsButton") == null && quick.FindName("WindowsLinkBackButton") == null,
                "legacy Windows Link gear and back buttons are removed from PhoneAudioWindow");

            var apps = (ListBox)quick.FindName("PhoneApps");
            var search = (TextBox)quick.FindName("SearchBox");
            var mediaCard = (FrameworkElement)quick.FindName("DeviceMediaCard");
            var refreshButton = (Button)quick.FindName("RefreshButton");
            var template = apps.ItemTemplate;
            var rowStyle = apps.ItemContainerStyle;

            SeedPinnedApp(service, peerId, "chrome", "Chrome Browser");
            combo.SelectedItem = savedPc;
            DrainUi();
            Check(androidHeight > 0 && footerCard.ActualHeight == androidHeight && refreshButton.ActualHeight == androidHeight,
                "a Windows peer uses the same measured selector and refresh height as Android");
            Check(apps.IsVisible && search.IsVisible && mediaCard.Visibility == Visibility.Collapsed
                && refreshButton.IsVisible && apps.Items.Count == 0
                && pane.Visibility == Visibility.Collapsed
                && ((TextBlock)quick.FindName("ConnectionText")).Text == L10n.T("Phone.Disconnected"),
                "a saved offline Windows computer keeps unified reconnect without showing cached application rows");
            Check(((TextBlock)quick.FindName("PeerEmptyText")).Text == L10n.T("Phone.WindowsLinkRefreshToConnect")
                && ((Border)quick.FindName("PhoneSurface")).Background == quick.FindResource("Theme.WindowBg")
                && ((TextBlock)quick.FindName("ConnectionText")).VerticalAlignment == VerticalAlignment.Center,
                "the offline panel explains how to reconnect on an opaque surface with an inline connection status");
            Render((FrameworkElement)quick.Content, 235, 450, Path.Combine(output, "quick-windows-offline.png"));

            var peerType = savedPc.GetType();
            var capabilities = peerType.GetProperty("Capabilities")!.GetValue(savedPc);
            object connectedPc = Activator.CreateInstance(peerType,
                [peerId, "Fixture PC", null, "127.0.0.1:46916", true, true, true, capabilities])!;
            combo.ItemsSource = new object[] { offline, backup, connectedPc, airPlayChoice, windowsLinkChoice };
            combo.SelectedItem = connectedPc;
            Array deviceCatalog = SeedWindowsRemoteCatalog(quick);
            DrainUi();
            string systemAudioName = L10n.T("Phone.WindowsLinkDeviceAudioDesc");
            Check(apps.Items.Cast<object>().Select(RowName).SequenceEqual(new[] { systemAudioName, "Cloud Music", "Background audio" })
                && apps.ItemTemplate == template && apps.ItemContainerStyle == rowStyle
                && search.IsVisible && mediaCard.Visibility == Visibility.Collapsed && refreshButton.IsVisible
                && pane.Visibility == Visibility.Collapsed
                && ((TextBlock)quick.FindName("ConnectionText")).Text == L10n.T("Phone.ConnectedStatus"),
                "the connected audio list starts with system audio, followed by the peer's app sessions in order");

            var firstSystemRow = (ListBoxItem)apps.ItemContainerGenerator.ContainerFromIndex(0);
            var musicItem = apps.Items.Cast<object>().Single(item => RowName(item) == "Cloud Music");
            CheckWindowsAudioRowMenu((ListBoxItem)apps.ItemContainerGenerator.ContainerFromItem(musicItem), output);
            CheckWindowsAudioRowMenu(firstSystemRow);

            CheckWindowsAudioCatalogNotice(hub);
            search.Text = "Music";
            Check(apps.Items.Count == 1 && RowName(apps.Items[0]) == "Cloud Music",
                "audio search only filters applications in the remote audio snapshot");
            search.Text = "Chrome";
            Check(apps.Items.Count == 0, "audio search cannot bring back a saved shortcut with no remote audio session");
            Check(((TextBlock)quick.FindName("PeerEmptyText")).Text == L10n.T("Phone.WindowsLinkNoMatches"),
                "an empty search shows the no-results state instead of a disconnected device");
            search.Text = systemAudioName;
            Check(apps.Items.Count == 1 && RowName(apps.Items[0]) == systemAudioName,
                "the all-sounds list entry is searchable by its displayed name");
            search.Clear();
            var viewChoices = (FrameworkElement)quick.FindName("WindowsLinkViewChoices");
            var audioChoice = (RadioButton)quick.FindName("WindowsLinkAudioChoice");
            var windowChoice = (RadioButton)quick.FindName("WindowsLinkWindowChoice");
            apps.SelectedItem = null;
            Render((FrameworkElement)quick.Content, 235, 450, Path.Combine(output, "quick-system-audio.png"));
            ThemeService.Apply(true, "#00CBE9");
            apps.SelectedIndex = 0;
            Render((FrameworkElement)quick.Content, 235, 450, Path.Combine(output, "quick-system-audio-dark.png"));
            ThemeService.Apply(false, "#00CBE9");
            apps.SelectedIndex = -1;
            modernPanel.ShowActivated = false;
            modernPanel.Show();
            // The fixture drives both windows on one desktop. Keep the source icon clear of the target HWND.
            var workArea = SystemParameters.WorkArea;
            quick.Left = workArea.Left + 8;
            quick.Top = modernPanel.Top = workArea.Top + 8;
            modernPanel.Left = quick.Left + quick.ActualWidth + 12;
            DrainUi();
            CheckWindowsAudioPushHit(quick, modernPanel, hub, service, connectedPc, output);
            if (exerciseTouch)
                CheckTouchPeerRow(quick, modernPanel, hub);
            Check(viewChoices.IsVisible && Grid.GetRow(viewChoices) < Grid.GetRow((UIElement)quick.FindName("DeviceSearchCard"))
                && audioChoice.IsChecked == true, "Windows Link shows the audio view selector above search");
            windowChoice.IsChecked = true;
            quick.UpdateLayout();
            string secondaryDisplay = string.Format(L10n.T("Phone.WindowsLinkDisplayNumber"), 2);
            Check(audioChoice.IsChecked == false && apps.Items.Cast<object>().All(item => RowName(item) != systemAudioName)
                && apps.Items.Count == 5
                && apps.Items.Cast<object>().Any(row => RowName(row) == "Only a window")
                && apps.Items.Cast<object>().Any(row => RowName(row) == "Second player window")
                && apps.Items.Cast<object>().All(row => (bool)row.GetType().GetProperty("Window")!.GetValue(row)!),
                "the windows view includes displays and independent windows from the same process");
            object primary = apps.Items[0], secondary = apps.Items[1];
            string Description(object item) => (string)item.GetType().GetProperty("Description")!.GetValue(item)!;
            Check(RowName(primary) == string.Format(L10n.T("Phone.WindowsLinkDisplayNumber"), 1)
                && RowName(secondary) == secondaryDisplay && Description(secondary).Contains("2560 × 1440")
                && Description(primary).Contains(L10n.T("Phone.WindowsLinkPrimaryDisplay"))
                && !Description(secondary).Contains(L10n.T("Phone.WindowsLinkPrimaryDisplay")),
                "both remote monitors precede application windows and expose their size and primary status");
            search.Text = secondaryDisplay;
            quick.UpdateLayout();
            Check(apps.Items.Count == 1 && ReferenceEquals(apps.Items[0], secondary),
                "search selects the intended remote monitor by its displayed name");
            var displayRow = (ListBoxItem)apps.ItemContainerGenerator.ContainerFromItem(secondary);
            displayRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(ReferenceEquals(typeof(PhoneAudioWindow).GetField("_windowsDragItem", Hidden)!.GetValue(quick), secondary)
                && !Descendants<Button>(displayRow).Any(button => button.IsVisible),
                "pressing a monitor row arms that exact display for video drag without audio playback actions");
            search.Text = "Second";
            Check(apps.Items.Count == 1 && RowName(apps.Items[0]) == "Second player window",
                "window search filters individual window titles within the same process");
            search.Text = "No matching window";
            Check(apps.Items.Count == 0, "window search filters only the current windows view");
            search.Clear();
            Render((FrameworkElement)quick.Content, 235, 450, Path.Combine(output, "quick-windows-view.png"));
            audioChoice.IsChecked = true;
            Check(windowChoice.IsChecked == false && apps.Items.Count == 3 && RowName(apps.Items[0]) == systemAudioName,
                "switching back restores the system audio row above the independent app list");

            foreach (string theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme, "#00CBE9");
                Render((FrameworkElement)quick.Content, 235, 450, Path.Combine(output, $"quick-windows-peer-{theme}.png"));
            }
            ThemeService.Apply("light", "#00CBE9");

            typeof(PhoneAudioWindow).GetMethod("WindowsLinkRemoteCatalogChanged", Hidden)!.Invoke(quick, [peerId]);
            Check(apps.Items.Count == 1 && RowName(apps.Items[0]) == systemAudioName,
                "peer invalidation clears stale app sessions while system audio remains available");
            windowChoice.IsChecked = true;
            Check(apps.Items.Count == 0, "peer invalidation also clears the cached windows");
            audioChoice.IsChecked = true;

            typeof(PhoneDevicePage).GetMethod("ShowWindowsLinkPeer", Hidden)!.Invoke(page, [peerId]);
            DrainUi();
            var retainedPanel = (StackPanel)page.FindName("RetainedPanel");
            var streamingCard = (Border)page.FindName("StreamingCard");
            var retainedApps = (ItemsControl)page.FindName("RetainedApps");
            var allowInputSwitch = (CheckBox)page.FindName("WindowsLinkAllowInputSwitch");
            var videoResCombo = (ComboBox)page.FindName("WindowsVideoResolutionCombo");
            var videoEncCombo = (ComboBox)page.FindName("VideoEncoderCombo");
            var videoDecCombo = (ComboBox)page.FindName("VideoDecoderCombo");
            var videoBitRateCombo = (ComboBox)page.FindName("VideoBitRateCombo");
            var videoFpsCombo = (ComboBox)page.FindName("VideoFpsCombo");

            Check(((TextBlock)page.FindName("DeviceName")).Text == "Fixture PC"
                && ((FrameworkElement)page.FindName("DeviceCard")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("PhoneDeviceCommands")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("UnpairButton")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)page.FindName("ClipboardCard")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("WindowsLinkInputCard")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("AudioFixedHint")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("AudioEncoderCombo")).Visibility == Visibility.Collapsed
                && retainedPanel.Children.IndexOf((UIElement)page.FindName("ClipboardCard")) ==
                   retainedPanel.Children.IndexOf((UIElement)page.FindName("WindowsFileCard")) + 1
                && ReferenceEquals(retainedPanel.Children[^1], page.FindName("WindowsLinkInputCard"))
                && streamingCard.Visibility == Visibility.Visible
                && videoResCombo.Items.Count == 3 && Equals(videoResCombo.SelectedValue, "1080")
                && videoBitRateCombo.Items.Count == 5
                && videoFpsCombo.Items.Count == 3
                && retainedApps.Items.Count == 0,
                "PhoneDevicePage reuses DeviceCard, StreamingCard, RetainedPanel, and WindowsLinkInputCard for Windows Link peers");
            bool OffersHevc(ComboBox box) => box.Items.Cast<object>().Any(option =>
                option.GetType().GetProperty("Label")?.GetValue(option) is string label && label.Contains("H.265"));
            Check(videoResCombo.Visibility == Visibility.Visible && OffersHevc(videoEncCombo)
                && OffersHevc(videoDecCombo) && videoDecCombo.Items.Count == 4,
                "Windows Link exposes separate resolution, encoder and decoder selectors with H.265 in both codec menus");
            Check(((TextBlock)page.FindName("SmartVolumeHeader")).Text == L10n.T("Phone.SmartVolumeTitle")
                && ((FrameworkElement)page.FindName("SmartVolumeHeader")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("AddAppsButton")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)page.FindName("WindowsAudioEmpty")).Visibility == Visibility.Visible,
                "offline device details show the smart-volume section without the manual add button or cached pinned apps");

            using (var connecting = new CancellationTokenSource())
            {
                var activeOperation = typeof(PhoneDevicePage).GetField("_operation", Hidden)!;
                activeOperation.SetValue(page, connecting);
                try { typeof(PhoneDevicePage).GetMethod("RefreshWindowsLinkPeerState", Hidden)!.Invoke(page, null); }
                finally { activeOperation.SetValue(page, null); }
                Check(!connecting.IsCancellationRequested, "device status updates leave an in-progress Windows Link reconnect running");
            }
            var nativeHint = (TextBlock)page.FindName("WindowsNativeResolutionHint");
            videoResCombo.SelectedValue = "0";
            DrainUi();
            using (var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "trusted.json"))))
            {
                var entry = saved.RootElement.EnumerateArray().Single(peer => peer.GetProperty("Id").GetString() == peerId);
                Check(entry.GetProperty("Settings").GetProperty("VideoMaxHeight").GetInt32() == 0,
                    "choosing native resolution persists the source-size preference for this PC");
            }
            typeof(PhoneDevicePage).GetMethod("ShowWindowsLinkPeer", Hidden)!.Invoke(page, [peerId]);
            DrainUi();
            Check(Equals(videoResCombo.SelectedValue, "0") && nativeHint.Visibility == Visibility.Collapsed,
                "reopening an offline peer restores native resolution without an upgrade message");
            var offlinePeer = nativeHint.DataContext;
            nativeHint.DataContext = new { Connected = true, SupportsNativeVideo = false };
            DrainUi();
            Check(nativeHint.Visibility == Visibility.Visible && nativeHint.Text == L10n.T("Phone.WindowsLinkNativeUpdateHint"),
                "a connected older peer displays an upgrade or fixed-resolution choice for native video");
            nativeHint.DataContext = new { Connected = true, SupportsNativeVideo = true };
            DrainUi();
            Check(nativeHint.Visibility == Visibility.Collapsed, "a native-capable PC needs no upgrade hint");
            nativeHint.DataContext = offlinePeer;
            videoResCombo.SelectedValue = "720";
            videoBitRateCombo.SelectedValue = "24000000";
            videoFpsCombo.SelectedValue = "60";
            allowInputSwitch.IsChecked = true;
            allowInputSwitch.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            DrainUi();
            string trustedJson = File.ReadAllText(Path.Combine(state, "trusted.json"));
            Check(trustedJson.Contains("720") && trustedJson.Contains("24000000") && trustedJson.Contains("\"AllowRemoteInput\":true"),
                "Windows Link streaming combos and remote input switch persist per peer in trusted.json");

            void ApplyAudioCatalog() => typeof(PhoneDevicePage).GetMethod("ApplyWindowsAudioApps", Hidden)!
                .Invoke(page, [service, peerId, deviceCatalog]);
            ApplyAudioCatalog();
            preview.Measure(new Size(680, 620));
            preview.Arrange(new Rect(0, 0, 680, 620));
            preview.UpdateLayout();
            double ControlTop(FrameworkElement control) => control.TranslatePoint(new Point(0, 0), streamingCard).Y;
            Check(ControlTop(videoResCombo) == ControlTop(videoBitRateCombo)
                && ControlTop(videoResCombo) == ControlTop(videoFpsCombo)
                && ControlTop(videoEncCombo) == ControlTop(videoDecCombo)
                && ControlTop(videoEncCombo) >= ControlTop(videoResCombo) + videoResCombo.ActualHeight,
                "Windows Link groups resolution, bitrate and frame rate above the encoder and decoder");
            var switches = Descendants<CheckBox>(retainedApps).ToArray();
            string? AppKey(CheckBox toggle)
            {
                object? item = toggle.DataContext.GetType().GetProperty("Item")?.GetValue(toggle.DataContext);
                return item?.GetType().GetProperty("Key")?.GetValue(item) as string;
            }
            Check(switches.Length == 2 && switches.Select(AppKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(["background", "cloudmusic"])
                && switches.All(toggle => toggle.IsChecked == true && toggle.Visibility == Visibility.Visible)
                && !Descendants<Button>(retainedApps).Any(button => button.Visibility == Visibility.Visible),
                "the remote audio snapshot yields one smart-volume switch per app, including idle audio sessions, with no pin action");
            var musicSwitch = switches.Single(toggle => AppKey(toggle) == "cloudmusic");
            typeof(ToggleButton).GetMethod("OnClick", Hidden)!.Invoke(musicSwitch, null);
            ApplyAudioCatalog();
            preview.UpdateLayout();
            Check(Descendants<CheckBox>(retainedApps).Single(toggle => AppKey(toggle) == "cloudmusic").IsChecked == false
                && Descendants<CheckBox>(retainedApps).Single(toggle => AppKey(toggle) == "background").IsChecked == true,
                "clicking one app's switch preserves its preference when the catalog is rebuilt and leaves the other app enabled");

            foreach (string theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme, "#00CBE9");
                Render(preview, 680, 620, Path.Combine(output, $"windows-peer-device-page-{theme}.png"));
            }
            ThemeService.Apply("light", "#00CBE9");

            typeof(PhoneDevicePage).GetMethod("ShowDevice", Hidden)!.Invoke(page, null);
            DrainUi();
            preview.UpdateLayout();
            Check(videoResCombo.Visibility == Visibility.Collapsed
                && ControlTop(videoEncCombo) == ControlTop(videoDecCombo)
                && ControlTop(videoEncCombo) == ControlTop(videoBitRateCombo)
                && ControlTop(videoEncCombo) == ControlTop(videoFpsCombo),
                "the Android video row restores both codecs, bitrate and frame rate together");
            Check(((FrameworkElement)page.FindName("UnpairButton")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("ClipboardCard")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("WindowsLinkInputCard")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)page.FindName("AudioFixedHint")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)page.FindName("AudioEncoderCombo")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("AddAppsButton")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("SmartVolumeHeader")).Visibility == Visibility.Collapsed
                && retainedApps.Items.OfType<PhoneAppChoice>().Any(c => c.Item.Name == "Android Music"),
                "switching PhoneDevicePage back to an Android device restores Android controls, combos, and retained apps");

            main.Show();
            DrainUi();
            typeof(App).GetField("_mainWindow", Hidden)!.SetValue(Application.Current, main);
            Until(() => main.FindName("PhoneDevicesList") is ListBox rows
                && rows.Items.Cast<object>().Any(item => DeviceId(item) == peerId));
            var devices = (ListBox)main.FindName("PhoneDevicesList");
            var appListBox = (ListBox)main.FindName("AppsListBox");
            Check(ReferenceEquals(devices.ItemTemplate, appListBox.ItemTemplate)
                && ReferenceEquals(devices.ItemContainerStyle, appListBox.ItemContainerStyle)
                && !Descendants<Button>(devices).Any(b => b.Name == "WindowsLinkDeviceSettings"),
                "Applications sidebar shares AppsCardItemTemplate with no gear button");

            devices.SelectedItem = devices.Items.Cast<object>().Single(item => DeviceId(item) == peerId);
            Until(() => ((Border)main.FindName("PhoneDetailsHost")).Child is PhoneDevicePage);
            var livePage = (PhoneDevicePage)((Border)main.FindName("PhoneDetailsHost")).Child;
            DrainUi();
            Check(((TextBlock)livePage.FindName("DeviceName")).Text == "Fixture PC"
                && ((FrameworkElement)livePage.FindName("StreamingCard")).Visibility == Visibility.Visible
                && ((FrameworkElement)livePage.FindName("WindowsLinkInputCard")).Visibility == Visibility.Visible,
                "selecting the Windows computer in the Applications sidebar opens the unified device details page");

            foreach (string theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme, "#00CBE9");
                Render((FrameworkElement)main.Content, (int)main.ActualWidth, (int)main.ActualHeight,
                    Path.Combine(output, $"main-sidebar-windows-peer-{theme}.png"));
            }
            ThemeService.Apply("light", "#00CBE9");

            var renameButton = (Button)livePage.FindName("RenameButton");
            var nameBox = (TextBox)livePage.FindName("PhoneNameBox");
            renameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(nameBox.IsVisible && nameBox.Text == "Fixture PC", "the pencil opens the current Windows Link name for editing");

            nameBox.Text = "Renamed PC";
            nameBox.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, nameBox, renameButton)
                { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            DrainUi();
            using var savedPeers = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(state, "trusted.json")));
            var savedAlias = savedPeers.RootElement.EnumerateArray()
                .Single(peer => peer.GetProperty("Id").GetString() == peerId).GetProperty("Alias").GetString();
            object renamedPc = combo.Items.Cast<object>().Single(item => DeviceId(item) == peerId);
            choices = [.. androidDevices, renamedPc, airPlayChoice, windowsLinkChoice];
            Check(!nameBox.IsVisible && savedAlias == "Renamed PC"
                && ((TextBlock)livePage.FindName("DeviceName")).Text == "Renamed PC"
                && PeerName(renamedPc) == "Renamed PC"
                && combo.Items.Cast<object>().SequenceEqual(choices),
                "inline pencil rename updates trusted.json, DeviceName, and the QuickPanel dropdown");

            var appAudioType = typeof(App).Assembly.GetType("SonicRoute.ApplicationAudio")!;
            bool isWinLinkOut = (bool)appAudioType.GetMethod("IsExternalOutputDevice", staticMember)!.Invoke(null, ["winlink-out:" + peerId])!;
            Check(modernPanel.FindName("WindowsLinkDropZone") is Border && isWinLinkOut,
                "QuickPanelModernWindow registers WindowsLinkDropZone and recognizes winlink-out external output devices");

            CheckWindowsLinkFileControls(livePage, service, connectedPc, output);
            CheckWindowsLinkTrustControls(livePage, service, connectedPc, output);

            // Keep the post-removal Add Device page on Windows Link instead of starting Android QR pairing.
            ((RadioButton)livePage.FindName("WindowsLinkConnectionChoice")).IsChecked = true;
            var remove = (Button)livePage.FindName("RemoveDeviceButton");
            var prompt = (Border)livePage.FindName("WindowsRemovePanel");
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(prompt.IsVisible && ((TextBlock)livePage.FindName("WindowsRemoveMessage")).Text.Contains("Renamed PC")
                && File.ReadAllText(Path.Combine(state, "trusted.json")).Contains(peerId),
                "the first remove click names the peer and keeps its saved pairing");
            ((Button)livePage.FindName("WindowsRemoveCancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!prompt.IsVisible && File.ReadAllText(Path.Combine(state, "trusted.json")).Contains(peerId),
                "canceling removal keeps the pairing and trust");
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var confirmRemove = (Button)livePage.FindName("WindowsRemoveConfirm");
            using (File.Open(Path.Combine(state, "trusted.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                confirmRemove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(prompt.IsVisible && File.ReadAllText(Path.Combine(state, "trusted.json")).Contains(peerId)
                && ((TextBlock)livePage.FindName("WindowsRemoveMessage")).Text == string.Format(L10n.T("Phone.LinkRemoveFailed"), "Renamed PC"),
                "a blocked settings write keeps the device, explains the failure and allows retry");
            confirmRemove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DrainUi();
            choices = [.. androidDevices, airPlayChoice, windowsLinkChoice];
            Check(combo.Items.Cast<object>().SequenceEqual(choices)
                && !File.ReadAllText(Path.Combine(state, "trusted.json")).Contains(peerId),
                "RemoveDeviceButton removes the saved Windows Link peer and leaves AirPlay and Windows Link last");
            RefreshWindowsLinkView(connectView);
            Check(widePeersList.Visibility == Visibility.Collapsed && widePeersList.Items.Count == 0,
                "removing the last Windows Link peer collapses the discovered/paired peers list");

            VerifyWindowsLinkPeerStatusFormatting(peerType);

            combo.SelectedItem = combo.Items.OfType<AndroidDevice>().Single(device => device.Serial == offline.Serial);
            DrainUi();
            Check(apps.IsVisible && apps.Items.OfType<PhoneAppItem>().Any(item => item.Name == "Android Music"),
                "switching back restores the Android app list and footer");

            Check(!(bool)androidHost.GetProperty("IsAdbCreated", staticMember)!.GetValue(null)!,
                "the two simulated Android devices do not create a real ADB backend");

            CheckLocalWindowsAudioDrops(quick, modernPanel, hub);
            File.WriteAllText(Path.Combine(output, "windows-link-ui.json"),
                JsonSerializer.Serialize(new { Assertions = _checks, SavedPeersRestoreOffline = true, RedesignedUi = true },
                    new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally
        {
            modernPanel.Close();
            quick.Close();
            main.Close();
            var shutdown = hub.GetMethod("ShutdownAsync", staticMember)!;
            ((Task)shutdown.Invoke(null, null)!).GetAwaiter().GetResult();
        }
    }

    private static bool DirectFormIsOpen(FrameworkElement view)
    {
        var card = (Border)view.FindName("DirectCard");
        var address = (TextBox)view.FindName("AddressBox");
        var connect = (Button)view.FindName("ConnectButton");
        return !Descendants<Expander>(card).Any() && address.ActualWidth > 0 && address.ActualHeight > 0
            && connect.ActualWidth > 0 && connect.ActualHeight > 0;
    }

    private static bool IsWindowsLinkConnectView(object? content) =>
        content?.GetType().FullName == "SonicRoute.WindowsLinkConnectView";

    private static void RefreshWindowsLinkView(object view) =>
        ((Task)view.GetType().GetMethod("RefreshAsync", Hidden)!.Invoke(view, null)!).GetAwaiter().GetResult();

    private static string? DeviceId(object item) => item.GetType().GetProperty("Id")?.GetValue(item) as string;
    private static string? PeerName(object item) => item.GetType().GetProperty("DisplayName")?.GetValue(item) as string;
    private static string? RowName(object item) => item.GetType().GetProperty("Name")?.GetValue(item) as string;

    private static void SeedPinnedApp(object service, string peerId, string key, string name)
    {
        var remoteAppType = service.GetType().Assembly.GetType("SonicRoute.WindowsLink.LinkRemoteApp")!;
        object remoteApp = Activator.CreateInstance(remoteAppType,
            [key, name, key, 1200, false, true, 0x1001L, name, (byte[]?)null])!;
        service.GetType().GetMethod("PinApp")!.Invoke(service, [peerId, remoteApp]);
    }

    private static void VerifyWindowsLinkLocalAudioCatalog(Type hub)
    {
        const BindingFlags staticFields = BindingFlags.NonPublic | BindingFlags.Static;
        var audio = typeof(AudioService);
        var cache = audio.GetField("_appsCache", staticFields)!;
        var cacheAt = audio.GetField("_appsCacheAt", staticFields)!;
        var cacheGeneration = audio.GetField("_appsCacheGeneration", staticFields)!;
        var readTask = audio.GetField("_appsReadTask", staticFields)!;
        var fields = new[] { cache, cacheAt, cacheGeneration, readTask };
        var originals = fields.Select(field => field.GetValue(null)).ToArray();

        var originalConfig = ConfigService.Load();
        var hiddenConfig = originalConfig.Snapshot();
        hiddenConfig.HiddenPanelApps.Add("HIDDEN-FIXTURE");
        ConfigService.QueueSave(hiddenConfig);
        try
        {
            cache.SetValue(null, new List<AudioAppInfo>
            {
                new() { ProcessId = 2_000_001, ProcessName = "hidden-fixture", HasActiveSession = true },
                new() { ProcessId = 2_000_002, ProcessName = "visible-fixture", DisplayName = "Visible fixture" }
            });
            cacheAt.SetValue(null, Environment.TickCount64);
            cacheGeneration.SetValue(null, audio.GetField("_appsGeneration", staticFields)!.GetValue(null));
            readTask.SetValue(null, null);
            var catalogScopeType = typeof(AndroidDevice).Assembly.GetType("SonicRoute.WindowsLink.LinkCatalogScope")!;
            var audioScope = Enum.Parse(catalogScopeType, "Audio");
            var read = (Task)hub.GetMethod("BuildScopedLocalCatalogAsync", staticFields)!.Invoke(null, [audioScope, true, CancellationToken.None])!;
            read.GetAwaiter().GetResult();
            var items = (Array)read.GetType().GetProperty("Result")!.GetValue(read)!;
            Check(items.Length == 1 && RowName(items.GetValue(0)!) == "Visible fixture",
                "the sender reuses its cached audio-session list and excludes locally hidden apps case-insensitively");
        }
        finally
        {
            for (int i = 0; i < fields.Length; i++) fields[i].SetValue(null, originals[i]);
            ConfigService.QueueSave(originalConfig);
        }
    }

    private static Array SeedWindowsRemoteCatalog(PhoneAudioWindow quick)
    {
        var remoteAppType = typeof(AndroidDevice).Assembly.GetType("SonicRoute.WindowsLink.LinkRemoteApp")!;
        object MakeApp(object?[] values, bool hasAudio, string? instanceId = null)
        {
            object app = Activator.CreateInstance(remoteAppType, values)!;
            remoteAppType.GetProperty("HasAudioSession")!.SetValue(app, hasAudio);
            if (instanceId != null)
                remoteAppType.GetProperty("InstanceId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(app, instanceId);
            return app;
        }

        var music = MakeApp(["cloudmusic", "Cloud Music", "cloudmusic", 2200, true, true, 0x2002L, "Cloud Music", null], true, "w:2002:898:1:1");
        var background = MakeApp(["background", "Background audio", "background", 3300, false, false, 0L, null, null], true, "a:ce4:2");
        var window1 = MakeApp(["player", "Only a window", "player", 4400, false, true, 0x4004L, "Only a window", null], false, "w:4004:1130:1:1");
        var window2 = MakeApp(["player", "Second player window", "player", 4400, false, true, 0x4005L, "Second player window", null], false, "w:4005:1130:2:1");
        var audio = Array.CreateInstance(remoteAppType, 2);
        audio.SetValue(music, 0);
        audio.SetValue(background, 1);
        object MakeDisplay(int number, int width, int height, bool primary)
        {
            string key = $"sonicroute:monitor:fixture-{number}";
            object app = MakeApp([key, $"Fixture monitor {number}", "", 0, false, false, 0L, null, null], false, key);
            var display = remoteAppType.GetProperty("Display")!;
            display.SetValue(app, Activator.CreateInstance(display.PropertyType, [number, width, height, primary]));
            return app;
        }
        var windows = Array.CreateInstance(remoteAppType, 5);
        windows.SetValue(music, 0);
        windows.SetValue(window1, 1);
        windows.SetValue(window2, 2);
        windows.SetValue(MakeDisplay(1, 1920, 1080, true), 3);
        windows.SetValue(MakeDisplay(2, 2560, 1440, false), 4);

        var type = typeof(PhoneAudioWindow);
        type.GetField("_windowsRemoteAudioApps", Hidden)!.SetValue(quick, audio);
        type.GetField("_windowsRemoteWindows", Hidden)!.SetValue(quick, windows);
        type.GetMethod("UpdateWindowsLinkRows", Hidden)!.Invoke(quick, null);

        var otherMusicProcess = MakeApp(["CLOUDMUSIC", "Other player process", "cloudmusic", 2201, true, false, 0L, null, null], true);
        var deviceCatalog = Array.CreateInstance(remoteAppType, 4);
        audio.CopyTo(deviceCatalog, 0);
        deviceCatalog.SetValue(otherMusicProcess, 2);
        deviceCatalog.SetValue(window1, 3);
        return deviceCatalog;
    }

    private static void CheckWindowsAudioRowMenu(ListBoxItem row, string? output = null)
    {
        var app = row.Content.GetType().GetProperty("App")!.GetValue(row.Content)!;
        bool systemAudio = (string?)app.GetType().GetProperty("Key")!.GetValue(app) == "sonicroute:system-audio";
        string source = systemAudio ? "all-sounds row" : "app row";
        Check(!Descendants<Button>(row).Any(), $"the {source} has no trailing action button");
        Point point = new(row.ActualWidth - 8, row.ActualHeight / 2);
        byte[] savedKeys = new byte[256];
        if (!GetKeyboardState(savedKeys)) throw new InvalidOperationException("Private dispatcher key state unavailable.");
        var keys = (byte[])savedKeys.Clone();
        void ApplyKeys()
        {
            if (!SetKeyboardState(keys)) throw new InvalidOperationException("Could not set the private dispatcher key state.");
        }
        try
        {
            ReportWpfMouse(row, point, "Activate, AbsoluteMove");
            keys[2] = 0x80; ApplyKeys();
            ReportWpfMouse(row, point, "Button2Press");
            keys[2] = 0; ApplyKeys();
            ReportWpfMouse(row, point, "Button2Release");
            var menu = row.ContextMenu;
            Check(menu is { IsOpen: true, Placement: PlacementMode.MousePoint } && row.IsSelected
                && menu.Items.OfType<MenuItem>().Select(i => i.Header as string).SequenceEqual(
                    new[] { L10n.T("Phone.WindowsLinkDropOnlyHere"), L10n.T("Phone.WindowsLinkDropBothComputers") })
                && !((MenuItem)menu.Items[0]).IsEnabled && ((MenuItem)menu.Items[1]).IsEnabled,
                $"right-clicking the {source} opens both tray menu choices with shared playback available and exclusive disabled");
            if (output != null) CheckWindowsLinkMenuStyle(menu!, output);
            menu!.IsOpen = false;
            DrainUi();

            void KeyGesture(Key key, RoutedEvent routedEvent)
            {
                row.Focus();
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(row)!, Environment.TickCount, key)
                    { RoutedEvent = routedEvent, Source = row };
                if (key == Key.F10) typeof(KeyEventArgs).GetMethod("MarkSystem", Hidden)!.Invoke(args, null);
                InputManager.Current.ProcessInput(args);
                Check(row.ContextMenu is { IsOpen: true, Placement: PlacementMode.Bottom },
                    $"the focused {source} opens its menu through " + (key == Key.Apps ? "the Menu key" : "Shift+F10"));
                row.ContextMenu!.IsOpen = false;
                DrainUi();
            }
            KeyGesture(Key.Apps, Keyboard.KeyUpEvent);
            keys[0x10] = keys[0xa0] = 0x80; ApplyKeys();
            KeyGesture(Key.F10, Keyboard.KeyDownEvent);
        }
        finally { SetKeyboardState(savedKeys); }
    }

    private static void CheckWindowsLinkFileControls(PhoneDevicePage page, object service, object peer, string output)
    {
        var remote = peer.GetType().Assembly;
        object Peer(int capabilities) => Activator.CreateInstance(peer.GetType(),
            [DeviceId(peer), "Fixture PC", null, "127.0.0.1:46916", true, true, true,
                Enum.ToObject(remote.GetType("SonicRoute.WindowsLink.LinkCapabilities")!, capabilities)])!;
        var update = typeof(PhoneDevicePage).GetMethod("UpdateWindowsFileCard", Hidden)!;
        var send = (Button)page.FindName("SendWindowsFileButton");
        update.Invoke(page, [Peer(1023)]);
        Check(!send.IsEnabled && ((TextBlock)page.FindName("WindowsFileStatus")).Text == L10n.T("Phone.LinkFileUpdatePeer"),
            "an older connected PC shows the file transfer upgrade hint");
        update.Invoke(page, [Peer(1023 | 4096)]);
        Check(send.IsEnabled && send.AllowDrop && send.Focusable && send.MinHeight >= 104
            && System.Windows.Automation.AutomationProperties.GetName(send) == L10n.T("Phone.LinkFileDropHint"),
            "a connected file-capable PC exposes the full file drop area as a keyboard-accessible picker");

        var host = Window.GetWindow(page);
        send.BringIntoView();
        DrainUi();
        var fileData = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(output, "windows-link-file-offer.png"), output });
        bool accentWhileOver = false;
        Check(OleDragOver(host, send, fileData, .25, over: () =>
            accentWhileOver = ReferenceEquals(send.BorderBrush, page.FindResource("Theme.Accent"))) == DragDropEffects.Copy
            && accentWhileOver && !ReferenceEquals(send.BorderBrush, page.FindResource("Theme.Accent"))
            && OleDragOver(host, send, new DataObject(DataFormats.Text, "text only"), .75) == DragDropEffects.None,
            "Explorer file drags highlight the entire send area, raw text is rejected and leaving clears the accent");
        var fileCard = (FrameworkElement)page.FindName("WindowsFileCard");
        fileCard.Measure(new Size(580, double.PositiveInfinity));
        Render(fileCard, 580, (int)Math.Ceiling(fileCard.DesiredSize.Height), Path.Combine(output, "windows-link-file-drop.png"));
        send.BringIntoView();
        DrainUi();
        // A legacy destination must reject a mixed drop before sending any part of it.
        string candidate = Path.Combine(output, "file-drop-fixture.txt");
        File.WriteAllText(candidate, "fixture");
        fileData.SetData(DataFormats.FileDrop, new[] { candidate, output });
        Check(send.ActualHeight >= 104 && OleDragOver(host, send, fileData, .93, drop: true) == DragDropEffects.Copy,
            "the device page receives the native multi-item drop at the bottom of the send area");
        var stamp = System.Diagnostics.Stopwatch.StartNew();
        var fileStatus = (TextBlock)page.FindName("WindowsFileStatus");
        while (stamp.Elapsed < TimeSpan.FromSeconds(3) && fileStatus.Text != L10n.T("Phone.LinkBrowseUpdate"))
            Pump(Task.Delay(15));
        Check(send.IsEnabled && fileStatus.Text == L10n.T("Phone.LinkBrowseUpdate"),
            "a legacy destination reports that it needs an update and receives no part of a mixed drop");

        Guid id = Guid.NewGuid();
        var offer = Activator.CreateInstance(remote.GetType("SonicRoute.WindowsLink.LinkFileOffer")!,
            [id, DeviceId(peer), "report.txt", 1024L, false])!;
        var dialogType = typeof(App).Assembly.GetType("SonicRoute.WindowsLinkReceiveWindow")!;
        var dialog = (Window)Activator.CreateInstance(dialogType, Hidden, null, [service, offer, "Fixture PC"], null)!;
        try
        {
            Check(((TextBlock)dialog.FindName("SenderLabel")).Text.Contains("Fixture PC")
                && ((TextBlock)dialog.FindName("FileLabel")).Text == "report.txt"
                && ((TextBlock)dialog.FindName("FileSizeLabel")).Text == "1 KiB"
                && System.IO.Path.IsPathFullyQualified(((TextBlock)dialog.FindName("FolderLabel")).Text)
                && ((Button)dialog.FindName("SaveAsButton")).IsEnabled
                && ((Button)dialog.FindName("SaveButton")).IsEnabled,
                "an incoming offer identifies sender, file, size and receiving folder with receive and save-as actions");
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new Size(410, double.PositiveInfinity));
            Render(content, 410, (int)Math.Ceiling(content.DesiredSize.Height), Path.Combine(output, "windows-link-file-offer.png"));
            void State(string state)
            {
                object progress = Activator.CreateInstance(remote.GetType("SonicRoute.WindowsLink.LinkFileProgress")!,
                    [id, DeviceId(peer), "report.txt", 1024L, 512L, true,
                        Enum.Parse(remote.GetType("SonicRoute.WindowsLink.LinkFileState")!, state)])!;
                dialogType.GetMethod("Update", Hidden)!.Invoke(dialog, [progress]);
            }
            State("Transferring");
            Check(((ProgressBar)dialog.FindName("ReceiveProgress")).Value == 0.5
                && ((Button)dialog.FindName("DeclineButton")).Content as string == L10n.T("Auto.Cancel"),
                "an accepted receive displays byte progress and a cancel action");
            State("Completed");
            Check(((TextBlock)dialog.FindName("ResultLabel")).Text == string.Format(L10n.T("Phone.LinkFileSaved"), "report.txt")
                && ((Button)dialog.FindName("SaveButton")).Visibility == Visibility.Collapsed,
                "a completed download displays the result and closes file selection");
        }
        finally { dialog.Close(); }
    }

    private static object CaptureSystemAudioDrag(PhoneAudioWindow quick, Type hub, ListBox apps, object expectedPeer)
    {
        quick.UpdateLayout();
        var row = (ListBoxItem)apps.ItemContainerGenerator.ContainerFromIndex(0);
        var ordinary = (ListBoxItem)apps.ItemContainerGenerator.ContainerFromIndex(1);
        var icon = Descendants<Image>(row).First(image => image.Name == "AppIconImage");
        var iconCenter = icon.TranslatePoint(new Point(icon.ActualWidth / 2, icon.ActualHeight / 2), row);
        var rowCenter = new Point(row.ActualWidth * .7, row.ActualHeight / 2);
        const BindingFlags hubMember = BindingFlags.Static | BindingFlags.NonPublic;
        var iconCache = hub.Assembly.GetType("SonicRoute.PhoneSourceIcon")!.GetProperty("SystemAudioImage", hubMember)!.GetValue(null);
        Check(ReferenceEquals(row.Template, ordinary.Template) && ReferenceEquals(row.ContentTemplate, ordinary.ContentTemplate)
            && ReferenceEquals(row.ContentTemplate, apps.ItemTemplate) && icon.IsVisible && icon.Source != null
            && ReferenceEquals(icon.Source, iconCache) && icon.ActualWidth > 0 && icon.ActualHeight > 0
            && row.InputHitTest(iconCenter) is DependencyObject hit && row.IsAncestorOf(hit),
            "system audio is a normal application list item with the shared templates and a visible cached icon");

        var group = (DrawingGroup)((DrawingImage)icon.Source!).Drawing;
        var tile = (GeometryDrawing)group.Children[0];
        var speaker = (GeometryDrawing)((DrawingGroup)group.Children[1]).Children[0];
        var original = ((SolidColorBrush)row.FindResource("Theme.Accent")).Color;
        bool dark = ((SolidColorBrush)row.FindResource("Theme.WindowBg")).Color.R < 128;
        try
        {
            foreach (var (accent, ink) in new[] { ("#FFDE45", Colors.Black), ("#342068", Colors.White) })
            {
                ThemeService.Apply(false, accent);
                DrainUi();
                Check(ReferenceEquals(icon.Source, iconCache) && tile.Brush is SolidColorBrush background
                    && background.Color == (Color)ColorConverter.ConvertFromString(accent)
                    && speaker.Pen.Brush is SolidColorBrush foreground && foreground.Color == ink,
                    $"the mounted system audio icon adopts light theme color {accent} and keeps a contrasting speaker");
                ThemeService.Apply(true, accent);
                DrainUi();
                Check(ReferenceEquals(icon.Source, iconCache) && ReferenceEquals(tile.Brush, row.FindResource("Theme.DeviceIconBackground"))
                    && ReferenceEquals(speaker.Pen.Brush, row.FindResource("Theme.DeviceIconForeground"))
                    && !ReferenceEquals(tile.Brush, row.FindResource("Theme.Accent")),
                    "changing only the mode updates the existing system audio drawing to the dark device palette");
            }
        }
        finally { ThemeService.Apply(dark, $"#{original.R:X2}{original.G:X2}{original.B:X2}"); }

        var source = hub.GetProperty("DragSource", hubMember)!;
        var stateChanged = hub.GetEvent("DragStateChanged", hubMember)!;
        var payloads = new List<object>();
        var cancel = new OperationCanceledException("Stop the fixture gesture before entering the native OLE loop.");
        Action observe = () =>
        {
            if (source.GetValue(null) is not { } published) return;
            payloads.Add(published);
            throw cancel;
        };

        var keyboard = new byte[256];
        if (!GetKeyboardState(keyboard)) throw new InvalidOperationException("Cannot read the private test thread's key state.");
        var simulatedKeys = (byte[])keyboard.Clone();
        void Pointer(Point at, string actions, bool pressed)
        {
            simulatedKeys[1] = pressed ? (byte)0x80 : (byte)0;
            if (!SetKeyboardState(simulatedKeys)) throw new InvalidOperationException("Cannot set the test thread's key state.");
            ReportWpfMouse(row, at, actions);
        }
        void Click(Point at)
        {
            Pointer(at, "AbsoluteMove", false);
            Pointer(at, "Button1Press", true);
            Pointer(at, "Button1Release", false);
        }
        void Drag(Point origin)
        {
            var end = new Point(origin.X + SystemParameters.MinimumHorizontalDragDistance + 8, origin.Y);
            Pointer(origin, "AbsoluteMove", false);
            Pointer(origin, "Button1Press", true);
            try { Pointer(end, "AbsoluteMove", true); }
            catch (OperationCanceledException ex) when (ReferenceEquals(ex, cancel)) { }
            Pointer(end, "Button1Release", false);
        }

        // The isolated loopback service contains no live peer. Only an actual playback request replaces this marker.
        var error = typeof(PhoneAudioWindow).GetField("_windowsLinkError", Hidden)!;
        var priorError = error.GetValue(quick);
        const string untouched = "fixture playback not requested";
        stateChanged.GetAddMethod(true)!.Invoke(null, [observe]);
        try
        {
            apps.SelectedItem = ordinary.Content;
            error.SetValue(quick, untouched);
            Pointer(iconCenter, "Activate, AbsoluteMove", false);
            Click(iconCenter);
            if (!ReferenceEquals(apps.SelectedItem, row.Content) || payloads.Count != 0 || !Equals(error.GetValue(quick), untouched))
                Console.WriteLine($"Selection after click: row {apps.SelectedIndex}, hit {Mouse.DirectlyOver}, drags {payloads.Count}, playback requested {!Equals(error.GetValue(quick), untouched)}");
            Check(ReferenceEquals(apps.SelectedItem, row.Content) && payloads.Count == 0 && Equals(error.GetValue(quick), untouched),
                "a single click on the system audio icon selects its row without starting playback");

            Drag(rowCenter);
            Drag(iconCenter);
            Check(Equals(error.GetValue(quick), untouched), "moving a pressed system row past the drag threshold does not also activate playback");
            Click(rowCenter);
            Click(rowCenter);
            DrainUi();
            Check(error.GetValue(quick) is string doubleClickError && doubleClickError != untouched,
                "double-clicking the system audio row reaches the application's playback action");
            error.SetValue(quick, untouched);
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(row)!, Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = row };
            row.RaiseEvent(enter);
            DrainUi();
            Check(enter.Handled && error.GetValue(quick) is string keyError && keyError != untouched,
                "Enter on the selected system audio row reaches the same playback action");
        }
        finally
        {
            Mouse.Capture(null);
            SetKeyboardState(keyboard);
            error.SetValue(quick, priorError);
            typeof(PhoneAudioWindow).GetMethod("UpdateWindowsLinkFooter", Hidden)!.Invoke(quick, null);
            stateChanged.GetRemoveMethod(true)!.Invoke(null, [observe]);
        }
        var dragType = hub.Assembly.GetType("SonicRoute.WindowsLinkAppDrag")!;
        Check(payloads.Count == 2 && source.GetValue(null) == null && payloads.All(payload =>
        {
            var app = dragType.GetProperty("App")!.GetValue(payload)!;
            return ReferenceEquals(dragType.GetProperty("Peer")!.GetValue(payload), expectedPeer)
                && dragType.GetProperty("Window")!.GetValue(payload) is false
                && (string?)app.GetType().GetProperty("Key")!.GetValue(app) == "sonicroute:system-audio";
        }), "dragging either the icon or the row body publishes the selected computer's system audio and clears it on cancel");
        return payloads[0];
    }

    // Feed pointer reports into this WPF dispatcher. No physical cursor movement or system input injection.
    private static void ReportWpfMouse(FrameworkElement element, Point at, string actions)
    {
        var source = (HwndSource)PresentationSource.FromVisual(element)!;
        var client = source.CompositionTarget!.TransformToDevice.Transform(element.TranslatePoint(at, (UIElement)source.RootVisual));
        var assembly = typeof(Mouse).Assembly;
        Type InputType(string name) => assembly.GetType("System.Windows.Input." + name, true)!;
        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        object report = Activator.CreateInstance(InputType("RawMouseInputReport"), instance, null,
            [Enum.Parse(InputType("InputMode"), "Foreground"), Environment.TickCount, source,
             Enum.Parse(InputType("RawMouseActions"), actions), (int)Math.Round(client.X), (int)Math.Round(client.Y), 0, IntPtr.Zero], null)!;
        var args = (InputEventArgs)Activator.CreateInstance(InputType("InputReportEventArgs"), instance, null, [null, report], null)!;
        args.RoutedEvent = (RoutedEvent)typeof(InputManager).GetField("PreviewInputReportEvent", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        InputManager.Current.ProcessInput(args);
    }

    [DllImport("user32.dll")] private static extern bool GetKeyboardState([Out] byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    private static void CheckWindowsLinkAudioDrop(PhoneAudioWindow quick, QuickPanelModernWindow panel, Type hub,
        object connectedPeer, object systemAudioDrag, string output)
    {
        var source = hub.GetProperty("DragSource", BindingFlags.Static | BindingFlags.NonPublic)!;
        var remoteAssembly = connectedPeer.GetType().Assembly;
        var appType = remoteAssembly.GetType("SonicRoute.WindowsLink.LinkRemoteApp")!;
        var dragType = hub.Assembly.GetType("SonicRoute.WindowsLinkAppDrag")!;
        var capabilitiesType = remoteAssembly.GetType("SonicRoute.WindowsLink.LinkCapabilities")!;
        object Drag(int capabilities, bool window = false, bool? exclusive = null, bool returning = false, string? peerId = null, bool speakerFallback = false)
        {
            var app = Activator.CreateInstance(appType, ["bilibili", "哔哩哔哩", "bilibili", 4150, true, false, 0L, null, null])!;
            appType.GetProperty("CanPlayExclusively")!.SetValue(app, exclusive);
            appType.GetProperty("CanMuteSourceSpeaker")!.SetValue(app, speakerFallback);
            appType.GetProperty("InstanceId")!.SetValue(app, "a:1036:1");
            object peer = Activator.CreateInstance(connectedPeer.GetType(),
                [peerId ?? DeviceId(connectedPeer), "Fixture PC", null, "127.0.0.1:46916", true, true, true,
                    Enum.ToObject(capabilitiesType, capabilities)])!;
            object drag = Activator.CreateInstance(dragType, [peer, app, window])!;
            dragType.GetProperty("CanReturn", Hidden)!.SetValue(drag, returning);
            return drag;
        }

        var scroll = (ScrollViewer)panel.FindName("AppListScroll");
        var overlay = (Border)panel.FindName("AndroidDropHighlight");
        var only = (Border)panel.FindName("AndroidComputerDropZone");
        var both = (Border)panel.FindName("AndroidSharedDropZone");
        var single = (Border)panel.FindName("WindowsLinkDropZone");
        var peerList = (ListBox)quick.FindName("PhoneApps");
        var returnZone = (Border)quick.FindName("AndroidReturnDropZone");
        var peerOverlay = (Border)quick.FindName("PcDropHighlight");
        DragEventArgs Raise(object drag, bool lower, bool drop = false)
        {
            var data = new DataObject("SonicRoute.WindowsLinkApp.v1", drag);
            var e = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), Hidden, null,
                [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, scroll,
                    new Point(10, scroll.ActualHeight * (lower ? 0.75 : 0.25))], null)!;
            e.RoutedEvent = drop ? DragDrop.PreviewDropEvent : DragDrop.PreviewDragOverEvent;
            scroll.RaiseEvent(e);
            return e;
        }

        try
        {
            var full = Drag(4 | 128 | 256, exclusive: true);
            source.SetValue(null, full);
            Check(overlay.Visibility == Visibility.Visible && only.Opacity == 1 && both.Opacity == 1
                && single.Visibility == Visibility.Collapsed, "lifting a Windows audio app reveals both playback targets");
            panel.UpdateLayout();
            var realData = new DataObject("SonicRoute.WindowsLinkApp.v1", full);
            Check(!peerOverlay.IsVisible && !returnZone.IsVisible && peerList.IsVisible
                && OleDragOver(quick, peerList, realData, .5, drop: true) == DragDropEffects.None && !peerOverlay.IsVisible,
                "dragging an app out of the peer catalog keeps its source list visible and rejects a self-drop");
            bool upper = false, lower = false;
            Check(OleDragOver(panel, only, realData, .5, drop: true, over: () =>
                    upper = ReferenceEquals(only.Background, panel.FindResource("Theme.Overlay"))) == DragDropEffects.Copy && upper
                && OleDragOver(panel, both, realData, .5, drop: true, over: () =>
                    lower = ReferenceEquals(both.Background, panel.FindResource("Theme.Overlay"))) == DragDropEffects.Copy && lower,
                "native drops on the upper and lower mixer cells select destination-only and shared playback respectively");
            Render((FrameworkElement)panel.Content, 372, 450, Path.Combine(output, "quick-windows-drop-before.png"));
            Check(scroll.ActualHeight > 0 && Raise(full, false).Effects == DragDropEffects.Copy
                && ReferenceEquals(only.Background, panel.FindResource("Theme.Overlay")) && Raise(full, true).Effects == DragDropEffects.Copy
                && ReferenceEquals(both.Background, panel.FindResource("Theme.Overlay")),
                "Windows audio highlights destination-only above and simultaneous playback below through the existing drag handler");
            Render((FrameworkElement)panel.Content, 372, 450, Path.Combine(output, "quick-windows-drop-both.png"));

            source.SetValue(null, systemAudioDrag);
            panel.UpdateLayout();
            Check(single.IsVisible && !only.IsVisible && !both.IsVisible
                && OleDragOver(panel, single, new DataObject("SonicRoute.WindowsLinkApp.v1", systemAudioDrag), .5) == DragDropEffects.Copy,
                "the captured whole-computer audio drag is accepted on its single shared-playback target");

            var sharedOnly = Drag(4);
            source.SetValue(null, sharedOnly);
            Check(Raise(sharedOnly, false).Effects == DragDropEffects.None && only.Opacity < 1
                && Raise(sharedOnly, false, drop: true).Effects == DragDropEffects.None
                && Raise(sharedOnly, true).Effects == DragDropEffects.Copy && both.Opacity == 1,
                "a shared-only peer keeps both zones and rejects an exclusive drop without starting shared audio");

            var unavailableApp = Drag(4 | 128 | 256, exclusive: false);
            source.SetValue(null, unavailableApp);
            var unavailableData = new DataObject("SonicRoute.WindowsLinkApp.v1", unavailableApp);
            Check(OleDragOver(panel, only, unavailableData, .5, drop: true) == DragDropEffects.None && only.Opacity < 1
                && OleDragOver(panel, both, unavailableData, .5) == DragDropEffects.Copy && both.Opacity == 1,
                "an explicit per-app refusal disables only the upper drop even on an exclusive-capable peer");

            var singleSpeaker = Drag(4 | 128 | 512, exclusive: false, speakerFallback: true);
            source.SetValue(null, singleSpeaker);
            panel.UpdateLayout();
            var singleSpeakerData = new DataObject("SonicRoute.WindowsLinkApp.v1", singleSpeaker);
            Check(OleDragOver(panel, only, singleSpeakerData, .5) == DragDropEffects.Copy && only.Opacity == 1
                && Descendants<TextBlock>(only).Any(t => t.Text == L10n.T("Phone.WindowsLinkMuteSpeakerDesc"))
                && OleDragOver(panel, both, singleSpeakerData, .5) == DragDropEffects.Copy,
                "a single-speaker Bilibili source accepts the upper drop and explains the source-speaker mute");
            Render((FrameworkElement)panel.Content, 372, 450, Path.Combine(output, "quick-windows-single-speaker.png"));

            var returning = Drag(4 | 128 | 256, returning: true);
            source.SetValue(null, returning);
            quick.UpdateLayout();
            var returnData = new DataObject("SonicRoute.WindowsLinkApp.v1", returning);
            Check(returnZone.IsVisible && peerOverlay.IsVisible && !overlay.IsVisible
                && OleDragOver(panel, scroll, returnData, .5) == DragDropEffects.None
                && OleDragOver(quick, returnZone, returnData, .5, drop: true) == DragDropEffects.Move,
                "an incoming mixer row can be dropped back on its own computer without showing playback targets");
            var foreign = Drag(4, returning: true, peerId: new string('B', 64));
            source.SetValue(null, foreign);
            Check(!returnZone.IsVisible && OleDragOver(quick, peerList,
                    new DataObject("SonicRoute.WindowsLinkApp.v1", foreign), .5, drop: true) == DragDropEffects.None,
                "a transferred app from a different PC does not activate the current computer's return target");

            var receiveOnly = Drag(1 | 128 | 512);
            source.SetValue(null, receiveOnly);
            Check(Raise(receiveOnly, false).Effects == DragDropEffects.None && only.Opacity < 1
                && Raise(receiveOnly, true).Effects == DragDropEffects.None && both.Opacity < 1
                && single.Visibility == Visibility.Collapsed
                && Descendants<TextBlock>(only).Any(label => label.Text == string.Format(L10n.T("Phone.WindowsLinkAudioUnavailable"), "Fixture PC")),
                "a peer without process capture keeps both options unavailable and identifies the affected sender");

            var window = Drag(4 | 128 | 256, window: true);
            source.SetValue(null, window);
            Check(overlay.Visibility == Visibility.Collapsed && Raise(window, false).Effects == DragDropEffects.None,
                "dragging a window leaves the audio targets closed");
        }
        finally { source.SetValue(null, null); }
    }

    private static void CheckWindowsLinkMenuStyle(ContextMenu menu, string output)
    {
        foreach (string theme in new[] { "dark", "light" })
        {
            ThemeService.Apply(theme, "#00CBE9");
            DrainUi();
            Check(menu.IsOpen && ReferenceEquals(menu.Style, menu.FindResource("ConvenienceMenu"))
                && ReferenceEquals(menu.Background, menu.FindResource("Theme.SurfaceBg"))
                && ReferenceEquals(menu.BorderBrush, menu.FindResource("Theme.Border"))
                && menu.Items.OfType<MenuItem>().All(item => ReferenceEquals(item.Style, menu.FindResource("ConvenienceMenuItem"))
                    && ReferenceEquals(item.Foreground, menu.FindResource("Theme.TextPrimary"))),
                $"the open Windows Link playback menu follows the tray menu style and {theme} theme brushes");
            Render(menu, (int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight),
                Path.Combine(output, $"windows-link-playback-menu-{theme}.png"));
        }
    }

    private static void CheckWindowsAudioCatalogNotice(Type hub)
    {
        var leaseType = typeof(AndroidDevice).Assembly.GetType("SonicRoute.AndroidAudio.WasapiDeviceLease", true)!;
        var enumeratorType = leaseType.GetNestedType("Enumerator", BindingFlags.NonPublic)!;
        using var enumerator = (IDisposable)Activator.CreateInstance(enumeratorType, nonPublic: true)!;
        using var endpoint = (IDisposable?)enumeratorType.GetMethod("TryDefault", Hidden)!.Invoke(enumerator, [NAudio.CoreAudioApi.Role.Multimedia]);
        if (endpoint == null) return;
        var device = (NAudio.CoreAudioApi.MMDevice)leaseType.GetProperty("Device", Hidden)!.GetValue(endpoint)!;
        var revision = hub.GetField("_catalogRevision", BindingFlags.Static | BindingFlags.NonPublic)!;
        long ReadRevision() => (long)revision.GetValue(null)!;
        long before = ReadRevision();
        var type = hub.Assembly.GetType("SonicRoute.WindowsLinkCatalogNotifications")!;
        var watcher = Activator.CreateInstance(type, nonPublic: true)!;
        try
        {
            Check(SpinWait.SpinUntil(() => ReadRevision() > before, 4000), "the sender subscribes to render session creation");
            before = ReadRevision();
            using var client = device.AudioClient;
            client.Initialize(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 0, 1_000_000, 0, client.MixFormat, Guid.NewGuid());
            Check(SpinWait.SpinUntil(() => ReadRevision() > before, 4000),
                "a new silent native WASAPI session invalidates the catalog while the sender panel is closed");
        }
        finally { ((Task)type.GetMethod("StopAsync", Hidden)!.Invoke(watcher, null)!).GetAwaiter().GetResult(); }
    }

    private static void CheckWindowsAudioPushHit(PhoneAudioWindow quick, QuickPanelModernWindow panel, Type hub,
        object fixtureService, object connectedPc, string output)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        Type serviceType = fixtureService.GetType();
        var ctor = serviceType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single();
        object?[] parameters = ctor.GetParameters().Select(param => param.Name switch
        {
            "stateDirectory" => Path.Combine(output, "push-node"),
            "port" => (object)0, "discovery" => false, _ => param.DefaultValue
        }).ToArray();
        object sender = ctor.Invoke(parameters);
        serviceType.GetProperty("ProbeAudioCapture", Hidden)!.SetValue(sender, (Func<CancellationToken, bool>)(_ => true));
        var active = hub.GetField("_service", statics)!;
        try
        {
            ((Task)serviceType.GetMethod("EnableAsync")!.Invoke(sender, [CancellationToken.None])!).GetAwaiter().GetResult();
            active.SetValue(null, sender);
            var list = (ListBox)quick.FindName("PhoneApps");
            var systemAudioDrag = CaptureSystemAudioDrag(quick, hub, list, connectedPc);
            var data = new DataObject("SonicRoute.PcApp.v1", new AudioAppInfo { ProcessId = 12345, ProcessName = "fixture" });
            quick.UpdateLayout();
            Check(OleDragOver(quick, list, data, .75) == DragDropEffects.Copy,
                "the device panel accepts a desktop app through its registered native OLE target");
            CheckWindowsLinkAudioDrop(quick, panel, hub, connectedPc, systemAudioDrag, output);
        }
        finally
        {
            active.SetValue(null, fixtureService);
            ((IAsyncDisposable)sender).DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // Drive the drop target registered with OLE. No cursor movements, injected input or clipboard writes.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OleEnter(nint target, nint data, int keys, long position, ref int effects);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OleOver(nint target, int keys, long position, ref int effects);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OleLeave(nint target);

    private static DragDropEffects OleDragOver(Window window, FrameworkElement zone, DataObject data, double fraction,
        bool drop = false, Action? over = null)
    {
        nint target = GetProp(new WindowInteropHelper(window).Handle, "OleDropTargetInterface");
        if (target == 0) throw new InvalidOperationException("The window has no OLE drop target.");
        nint unknown = Marshal.GetIUnknownForObject(data);
        nint native = 0;
        try
        {
            Guid iid = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out native));
            var point = zone.PointToScreen(new Point(zone.ActualWidth * .5, zone.ActualHeight * fraction));
            long coordinates = ((long)(int)point.Y << 32) | (uint)(int)point.X;
            nint vtable = Marshal.ReadIntPtr(target);
            T Entry<T>(int slot) where T : Delegate =>
                Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
            int effects = (int)(DragDropEffects.Copy | DragDropEffects.Move);
            Marshal.ThrowExceptionForHR(Entry<OleEnter>(3)(target, native, 1, coordinates, ref effects));
            bool dropped = false;
            try
            {
                effects = (int)(DragDropEffects.Copy | DragDropEffects.Move);
                Marshal.ThrowExceptionForHR(Entry<OleOver>(4)(target, 1, coordinates, ref effects));
                over?.Invoke();
                if (drop)
                {
                    effects = (int)(DragDropEffects.Copy | DragDropEffects.Move);
                    dropped = true;
                    Marshal.ThrowExceptionForHR(Entry<OleEnter>(6)(target, native, 0, coordinates, ref effects));
                }
                return (DragDropEffects)effects;
            }
            finally { if (!dropped) Entry<OleLeave>(5)(target); }
        }
        finally
        {
            if (native != 0) Marshal.Release(native);
            Marshal.Release(unknown);
        }
    }

    private static void VerifyNineLanguageKeys()
    {
        string[] requiredKeys =
        [
            "Phone.WindowsLinkLocalAddress", "Phone.WindowsLinkSteps", "Phone.WindowsLinkReady",
            "Phone.WindowsLinkEnable", "Phone.WindowsLinkDisable", "Phone.WindowsLinkPairPrompt",
            "Phone.WindowsLinkPairAccept", "Phone.WindowsLinkPairDecline", "Phone.WindowsLinkDirectDesc",
            "Phone.WindowsLinkNoDevicesFound", "Phone.WindowsLinkAudioFixed", "Phone.WindowsLinkAllowInputTitle",
            "Phone.WindowsLinkAllowInputHint", "Phone.WindowsLinkRes720p", "Phone.WindowsLinkRes1080p",
            "Phone.WindowsLinkResNative", "Phone.WindowsLinkNativeUpdateHint",
            "Phone.WindowsLinkDeviceAudioDesc", "Phone.WindowsLinkDisplayNumber", "Phone.WindowsLinkPrimaryDisplay",
            "Phone.WindowsLinkRemoteWindowDesc", "Phone.WindowsLinkRemoteAppDesc",
            "Phone.WindowsLinkPlayingSuffix", "Phone.WindowsLinkWindowBadge", "Phone.WindowsLinkDropPlayTitle",
            "Phone.WindowsLinkDropPlayDesc", "Phone.WindowsLinkDropReturnTitle", "Phone.WindowsLinkDropReturnDesc",
            "Phone.WindowsLinkDropSendTitle", "Phone.WindowsLinkDropSendDesc", "Phone.WindowsLinkOutputFormat",
            "Phone.WindowsLinkRowIconDragTooltip", "Phone.WindowsLinkCatalogEmpty", "Phone.WindowsLinkNeedConnect",
            "Phone.WindowsLinkAudioUnavailable", "Phone.WindowsLinkReconnectFailed", "Phone.WindowsLinkLocalAppsOnly",
            "Phone.WindowsLinkSearchWindows", "Phone.WindowsLinkPlaybackUnconfirmed", "Phone.WindowsLinkKeepLocalAudio",
            "Phone.WindowsLinkPlaybackReady", "Phone.WindowsLinkDropOnlyHere", "Phone.WindowsLinkDropBothComputers",
            "Phone.WindowsLinkExclusiveUnavailable", "Phone.WindowsLinkDevices", "Phone.WindowsLinkStateDiscovered",
            "Phone.WindowsLinkStatePaired", "Phone.WindowsLinkStateOnline", "Phone.WindowsLinkStateOffline",
            "Phone.WindowsLinkStateConnecting", "Phone.WindowsLinkStateFailed", "Phone.WindowsLinkFailDiscoveryTimeout",
            "Phone.WindowsLinkFailTcpUnreachable", "Phone.WindowsLinkFailPairingRejected", "Phone.WindowsLinkFailIdentityMismatch",
            "Phone.WindowsLinkFailSavedAddressUnavailable", "Phone.WindowsLinkFailIncompatible"
        ];
        string[] locales = ["zh-CN", "en-US", "zh-TW", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "ru-RU"];
        string previous = L10n.CurrentLanguage;
        try
        {
            foreach (string locale in locales)
            {
                L10n.Instance.SetLanguage(locale);
                foreach (string key in requiredKeys)
                {
                    string val = L10n.T(key);
                    if (string.IsNullOrWhiteSpace(val) || val == key)
                        throw new Exception($"Missing or untranslated key '{key}' in locale '{locale}'.");
                }
            }
            Check(true, $"all {requiredKeys.Length} Phone.WindowsLink* localization keys resolve across all 9 languages");
        }
        finally
        {
            L10n.Instance.SetLanguage(previous);
        }
    }

    private static void VerifyWindowsLinkPeerStatusFormatting(Type peerType)
    {
        var linkAsm = peerType.Assembly;
        var stateType = linkAsm.GetType("SonicRoute.WindowsLink.LinkPeerState")!;
        var failureType = linkAsm.GetType("SonicRoute.WindowsLink.LinkConnectFailure")!;
        var capType = linkAsm.GetType("SonicRoute.WindowsLink.LinkCapabilities")!;
        var helper = typeof(App).Assembly.GetType("SonicRoute.WindowsLinkPeerStatus")!;
        var textMethod = helper.GetMethod("Text", BindingFlags.Static | BindingFlags.NonPublic)!;
        var subtitleMethod = helper.GetMethod("Subtitle", BindingFlags.Static | BindingFlags.NonPublic)!;
        var failureMethod = helper.GetMethod("FailureText", BindingFlags.Static | BindingFlags.NonPublic)!;

        object MakePeer(string stateName, string failureName, bool trusted, bool online, bool connected)
        {
            object peer = Activator.CreateInstance(peerType,
                [new string('C', 64), "Peer", null, "192.168.1.50:46916", trusted, online, connected, Enum.ToObject(capType, 0)])!;
            peerType.GetProperty("State")!.SetValue(peer, Enum.Parse(stateType, stateName));
            peerType.GetProperty("Failure")!.SetValue(peer, Enum.Parse(failureType, failureName));
            return peer;
        }

        string discovered = (string)textMethod.Invoke(null, [MakePeer("Discovered", "None", false, true, false)])!;
        string online = (string)textMethod.Invoke(null, [MakePeer("Online", "None", true, true, false)])!;
        string connecting = (string)textMethod.Invoke(null, [MakePeer("Connecting", "None", true, false, false)])!;
        string connected = (string)textMethod.Invoke(null, [MakePeer("Connected", "None", true, true, true)])!;
        string offline = (string)textMethod.Invoke(null, [MakePeer("Offline", "None", true, false, false)])!;
        string paused = (string)textMethod.Invoke(null, [MakePeer("DisconnectedByUser", "None", true, false, false)])!;
        string failed = (string)textMethod.Invoke(null, [MakePeer("Failed", "PairingRejected", true, false, false)])!;
        string failedReason = (string)failureMethod.Invoke(null, [Enum.Parse(failureType, "PairingRejected")])!;
        string onlineSubtitle = (string)subtitleMethod.Invoke(null, [MakePeer("Online", "None", true, true, false)])!;
        string discoveredSubtitle = (string)subtitleMethod.Invoke(null, [MakePeer("Discovered", "None", false, true, false)])!;

        Check(new[] { discovered, online, connecting, connected, offline, paused, failed }.Distinct().Count() >= 6
            && !string.IsNullOrWhiteSpace(failedReason)
            && onlineSubtitle.Contains("192.168.1.50:46916")
            && onlineSubtitle.Contains(L10n.T("Phone.WindowsLinkStatePaired"))
            && !discoveredSubtitle.Contains(L10n.T("Phone.WindowsLinkStatePaired")),
            "WindowsLinkPeerStatus distinguishes all peer lifecycle states, pairing prefix, and failure reasons");
    }
}

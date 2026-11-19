using System.IO;
using System.Reflection;
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
    private static int WindowsLinkUi(string output)
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
                Capabilities = 127,
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

        using var page = new PhoneDevicePage();
        var quick = new PhoneAudioWindow(loadDevicesOnShow: false);
        var main = new MainWindow();
        var modernPanel = new QuickPanelModernWindow();
        try
        {
            VerifyNineLanguageKeys();
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
            Check(((FrameworkElement)connectView.FindName("ReceiverHeader")).Visibility == Visibility.Visible
                && ((FrameworkElement)connectView.FindName("ServiceToggleButton")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)connectView.FindName("DiscoveredPeersList")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)connectView.FindName("SearchHintText")).Visibility == Visibility.Collapsed
                && Grid.GetRow((UIElement)connectView.FindName("DirectButtonsGrid")) == 0,
                "wide WindowsLinkConnectView shows ReceiverHeader, 1-row direct connect, and no empty discovered card");

            var preview = new Border { Child = page, Padding = new Thickness(10) };
            preview.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceBg");
            foreach (string theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme, "#00CBE9");
                Render(preview, 760, 560, Path.Combine(output, $"add-device-windows-link-{theme}.png"));
            }
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
            Check(pane.Visibility == Visibility.Visible && IsWindowsLinkConnectView(compactView)
                && ((FrameworkElement)quick.FindName("AirPlayPane")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)compactView.FindName("ReceiverHeader")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)compactView.FindName("ServiceToggleButton")).Visibility == Visibility.Visible
                && ((FrameworkElement)compactView.FindName("DiscoveredPeersList")).Visibility == Visibility.Collapsed
                && Grid.GetRow((UIElement)compactView.FindName("DirectButtonsGrid")) == 1
                && ((FrameworkElement)quick.FindName("RefreshButton")).Visibility == Visibility.Collapsed,
                "quick Windows Link setup uses compact UseDeviceFooter 2-row layout without ReceiverHeader or empty card");

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
            Check(apps.IsVisible && search.IsVisible && mediaCard.Visibility == Visibility.Collapsed
                && refreshButton.IsVisible && apps.Items.Count == 0
                && pane.Visibility == Visibility.Collapsed
                && ((TextBlock)quick.FindName("ConnectionText")).Text == L10n.T("Phone.Disconnected"),
                "a saved offline Windows computer keeps unified reconnect without showing cached application rows");

            var peerType = savedPc.GetType();
            var capabilities = peerType.GetProperty("Capabilities")!.GetValue(savedPc);
            object connectedPc = Activator.CreateInstance(peerType,
                [peerId, "Fixture PC", null, "127.0.0.1:46916", true, true, true, capabilities])!;
            combo.ItemsSource = new object[] { offline, backup, connectedPc, airPlayChoice, windowsLinkChoice };
            combo.SelectedItem = connectedPc;
            SeedWindowsRemoteCatalog(quick);
            DrainUi();
            Check(apps.Items.Cast<object>().Select(RowName).SequenceEqual(new[] { "Cloud Music", "Background audio" })
                && apps.ItemTemplate == template && apps.ItemContainerStyle == rowStyle
                && search.IsVisible && mediaCard.Visibility == Visibility.Collapsed && refreshButton.IsVisible
                && pane.Visibility == Visibility.Collapsed
                && ((TextBlock)quick.FindName("ConnectionText")).Text == L10n.T("Phone.ConnectedStatus"),
                "the audio tab uses the remote audio-session list in peer order, without pinned or window-only entries");

            search.Text = "Music";
            Check(apps.Items.Count == 1 && RowName(apps.Items[0]) == "Cloud Music",
                "audio search only filters applications in the remote audio snapshot");
            search.Text = "Chrome";
            Check(apps.Items.Count == 0, "audio search cannot bring back a saved shortcut with no remote audio session");
            search.Clear();
            var viewChoices = (FrameworkElement)quick.FindName("WindowsLinkViewChoices");
            var audioChoice = (RadioButton)quick.FindName("WindowsLinkAudioChoice");
            var windowChoice = (RadioButton)quick.FindName("WindowsLinkWindowChoice");
            var systemAudio = (Button)quick.FindName("WindowsLinkSystemAudioButton");
            Check(systemAudio.IsVisible, "system audio remains available as a separate action for a capable peer");
            Check(viewChoices.IsVisible && Grid.GetRow(viewChoices) < Grid.GetRow((UIElement)quick.FindName("DeviceSearchCard"))
                && audioChoice.IsChecked == true, "Windows Link shows the audio view selector above search");
            windowChoice.IsChecked = true;
            Check(audioChoice.IsChecked == false && !systemAudio.IsVisible && apps.Items.Count == 2
                && apps.Items.Cast<object>().Any(row => RowName(row) == "Only a window")
                && apps.Items.Cast<object>().All(row => (bool)row.GetType().GetProperty("Window")!.GetValue(row)!),
                "the windows view replaces the audio list with shareable windows");
            search.Text = "No matching window";
            Check(apps.Items.Count == 0, "window search filters only the current windows view");
            search.Clear();
            Render((FrameworkElement)quick.Content, 235, 450, Path.Combine(output, "quick-windows-view.png"));
            audioChoice.IsChecked = true;
            Check(windowChoice.IsChecked == false && apps.Items.Count == 2, "switching back restores the independent audio list");

            foreach (string theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme, "#00CBE9");
                Render((FrameworkElement)quick.Content, 235, 450, Path.Combine(output, $"quick-windows-peer-{theme}.png"));
            }
            ThemeService.Apply("light", "#00CBE9");

            typeof(PhoneAudioWindow).GetMethod("WindowsLinkRemoteCatalogChanged", Hidden)!.Invoke(quick, [peerId]);
            Check(apps.Items.Count == 0, "peer invalidation clears stale application rows immediately");
            windowChoice.IsChecked = true;
            Check(apps.Items.Count == 0, "peer invalidation also clears the cached windows");
            audioChoice.IsChecked = true;

            typeof(PhoneDevicePage).GetMethod("ShowWindowsLinkPeer", Hidden)!.Invoke(page, [peerId]);
            DrainUi();
            var retainedPanel = (StackPanel)page.FindName("RetainedPanel");
            var streamingCard = (Border)page.FindName("StreamingCard");
            var retainedApps = (ItemsControl)page.FindName("RetainedApps");
            var allowInputSwitch = (CheckBox)page.FindName("WindowsLinkAllowInputSwitch");
            var videoResCombo = (ComboBox)page.FindName("VideoEncoderCombo");
            var videoDecCombo = (ComboBox)page.FindName("VideoDecoderCombo");
            var videoBitRateCombo = (ComboBox)page.FindName("VideoBitRateCombo");
            var videoFpsCombo = (ComboBox)page.FindName("VideoFpsCombo");

            Check(((TextBlock)page.FindName("DeviceName")).Text == "Fixture PC"
                && ((FrameworkElement)page.FindName("DeviceCard")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("PhoneDeviceCommands")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("UnpairButton")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)page.FindName("ClipboardCard")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)page.FindName("WindowsLinkInputCard")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("AudioFixedHint")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("AudioEncoderCombo")).Visibility == Visibility.Collapsed
                && ReferenceEquals(retainedPanel.Children[^1], page.FindName("ClipboardCard"))
                && ((Grid)streamingCard.Child).RowDefinitions.Count == 2
                && videoResCombo.Items.Count == 2
                && videoDecCombo.Items.Count == 3
                && videoBitRateCombo.Items.Count == 5
                && videoFpsCombo.Items.Count == 3
                && retainedApps.Items.Count == 1,
                "PhoneDevicePage reuses DeviceCard, StreamingCard, RetainedPanel, and WindowsLinkInputCard for Windows Link peers");

            videoResCombo.SelectedValue = "720";
            videoBitRateCombo.SelectedValue = "24000000";
            videoFpsCombo.SelectedValue = "60";
            allowInputSwitch.IsChecked = true;
            allowInputSwitch.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            DrainUi();
            string trustedJson = File.ReadAllText(Path.Combine(state, "trusted.json"));
            Check(trustedJson.Contains("720") && trustedJson.Contains("24000000") && trustedJson.Contains("\"AllowRemoteInput\":true"),
                "Windows Link streaming combos and remote input switch persist per peer in trusted.json");

            foreach (string theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme, "#00CBE9");
                Render(preview, 680, 620, Path.Combine(output, $"windows-peer-device-page-{theme}.png"));
            }
            ThemeService.Apply("light", "#00CBE9");

            typeof(PhoneDevicePage).GetMethod("ShowDevice", Hidden)!.Invoke(page, null);
            DrainUi();
            Check(((FrameworkElement)page.FindName("UnpairButton")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("ClipboardCard")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("WindowsLinkInputCard")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)page.FindName("AudioFixedHint")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)page.FindName("AudioEncoderCombo")).Visibility == Visibility.Visible
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

            CheckWindowsLinkAudioDrop(modernPanel, hub, connectedPc, output);

            // Keep the post-removal Add Device page on Windows Link instead of starting Android QR pairing.
            ((RadioButton)livePage.FindName("WindowsLinkConnectionChoice")).IsChecked = true;
            ((Button)livePage.FindName("RemoveDeviceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DrainUi();
            choices = [.. androidDevices, airPlayChoice, windowsLinkChoice];
            Check(combo.Items.Cast<object>().SequenceEqual(choices)
                && !File.ReadAllText(Path.Combine(state, "trusted.json")).Contains(peerId),
                "RemoveDeviceButton removes the saved Windows Link peer and leaves AirPlay and Windows Link last");

            combo.SelectedItem = combo.Items.OfType<AndroidDevice>().Single(device => device.Serial == offline.Serial);
            DrainUi();
            Check(apps.IsVisible && apps.Items.OfType<PhoneAppItem>().Any(item => item.Name == "Android Music"),
                "switching back restores the Android app list and footer");

            Check(!(bool)androidHost.GetProperty("IsAdbCreated", staticMember)!.GetValue(null)!,
                "the two simulated Android devices do not create a real ADB backend");

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
            var read = (Task)hub.GetMethod("BuildLocalCatalogAsync", staticFields)!.Invoke(null, [true, CancellationToken.None])!;
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

    private static void SeedWindowsRemoteCatalog(PhoneAudioWindow quick)
    {
        var remoteAppType = typeof(AndroidDevice).Assembly.GetType("SonicRoute.WindowsLink.LinkRemoteApp")!;
        object MakeApp(object?[] values, bool hasAudio)
        {
            object app = Activator.CreateInstance(remoteAppType, values)!;
            remoteAppType.GetProperty("HasAudioSession")!.SetValue(app, hasAudio);
            return app;
        }

        var music = MakeApp(["cloudmusic", "Cloud Music", "cloudmusic", 2200, true, true, 0x2002L, "Cloud Music", null], true);
        var background = MakeApp(["background", "Background audio", "background", 3300, false, false, 0L, null, null], true);
        var window = MakeApp(["player", "Only a window", "player", 4400, false, true, 0x4004L, "Only a window", null], false);
        var audio = Array.CreateInstance(remoteAppType, 2);
        audio.SetValue(music, 0);
        audio.SetValue(background, 1);
        var windows = Array.CreateInstance(remoteAppType, 2);
        windows.SetValue(music, 0);
        windows.SetValue(window, 1);

        var type = typeof(PhoneAudioWindow);
        type.GetField("_windowsRemoteAudioApps", Hidden)!.SetValue(quick, audio);
        type.GetField("_windowsRemoteWindows", Hidden)!.SetValue(quick, windows);
        type.GetMethod("UpdateWindowsLinkRows", Hidden)!.Invoke(quick, null);
    }

    private static void CheckWindowsLinkAudioDrop(QuickPanelModernWindow panel, Type hub, object connectedPeer, string output)
    {
        var source = hub.GetProperty("DragSource", BindingFlags.Static | BindingFlags.NonPublic)!;
        var remoteAssembly = connectedPeer.GetType().Assembly;
        var app = Activator.CreateInstance(remoteAssembly.GetType("SonicRoute.WindowsLink.LinkRemoteApp")!,
            ["music", "Fixture Music", "music", 4150, true, false, 0L, null, null])!;
        var dragType = hub.Assembly.GetType("SonicRoute.WindowsLinkAppDrag")!;
        var capabilitiesType = remoteAssembly.GetType("SonicRoute.WindowsLink.LinkCapabilities")!;
        object Drag(int capabilities, bool window = false)
        {
            object peer = Activator.CreateInstance(connectedPeer.GetType(),
                [DeviceId(connectedPeer), "Fixture PC", null, "127.0.0.1:46916", true, true, true,
                    Enum.ToObject(capabilitiesType, capabilities)])!;
            return Activator.CreateInstance(dragType, [peer, app, window])!;
        }

        var scroll = (ScrollViewer)panel.FindName("AppListScroll");
        var overlay = (Border)panel.FindName("AndroidDropHighlight");
        var only = (Border)panel.FindName("AndroidComputerDropZone");
        var both = (Border)panel.FindName("AndroidSharedDropZone");
        var single = (Border)panel.FindName("WindowsLinkDropZone");
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
            var full = Drag(4 | 128 | 256);
            source.SetValue(null, full);
            Check(overlay.Visibility == Visibility.Visible && only.Opacity == 1 && both.Opacity == 1
                && single.Visibility == Visibility.Collapsed, "lifting a Windows audio app reveals both playback targets");
            Render((FrameworkElement)panel.Content, 372, 450, Path.Combine(output, "quick-windows-drop-before.png"));
            Check(scroll.ActualHeight > 0 && Raise(full, false).Effects == DragDropEffects.Copy
                && ReferenceEquals(only.Background, panel.FindResource("Theme.Overlay")) && Raise(full, true).Effects == DragDropEffects.Copy
                && ReferenceEquals(both.Background, panel.FindResource("Theme.Overlay")),
                "Windows audio highlights destination-only above and simultaneous playback below through the existing drag handler");
            Render((FrameworkElement)panel.Content, 372, 450, Path.Combine(output, "quick-windows-drop-both.png"));

            var sharedOnly = Drag(4);
            source.SetValue(null, sharedOnly);
            Check(Raise(sharedOnly, false).Effects == DragDropEffects.None && only.Opacity < 1
                && Raise(sharedOnly, false, drop: true).Effects == DragDropEffects.None
                && Raise(sharedOnly, true).Effects == DragDropEffects.Copy && both.Opacity == 1,
                "a shared-only peer keeps both zones and rejects an exclusive drop without starting shared audio");

            var receiveOnly = Drag(1 | 128 | 512);
            source.SetValue(null, receiveOnly);
            Check(Raise(receiveOnly, false).Effects == DragDropEffects.None && only.Opacity < 1
                && Raise(receiveOnly, true).Effects == DragDropEffects.None && both.Opacity < 1
                && single.Visibility == Visibility.Collapsed
                && Descendants<TextBlock>(only).Any(label => label.Text.Contains("20348")),
                "a peer without process capture keeps both options unavailable and explains the Windows build requirement");

            var window = Drag(4 | 128 | 256, window: true);
            source.SetValue(null, window);
            Check(overlay.Visibility == Visibility.Collapsed && Raise(window, false).Effects == DragDropEffects.None,
                "dragging a window leaves the audio targets closed");
        }
        finally { source.SetValue(null, null); }
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
            "Phone.WindowsLinkDeviceAudioDesc", "Phone.WindowsLinkRemoteWindowDesc", "Phone.WindowsLinkRemoteAppDesc",
            "Phone.WindowsLinkPlayingSuffix", "Phone.WindowsLinkWindowBadge", "Phone.WindowsLinkDropPlayTitle",
            "Phone.WindowsLinkDropPlayDesc", "Phone.WindowsLinkDropReturnTitle", "Phone.WindowsLinkDropReturnDesc",
            "Phone.WindowsLinkDropSendTitle", "Phone.WindowsLinkDropSendDesc", "Phone.WindowsLinkOutputFormat",
            "Phone.WindowsLinkRowIconDragTooltip", "Phone.WindowsLinkCatalogEmpty", "Phone.WindowsLinkNeedConnect",
            "Phone.WindowsLinkAudioUnavailable", "Phone.WindowsLinkReconnectFailed", "Phone.WindowsLinkLocalAppsOnly",
            "Phone.WindowsLinkSearchWindows", "Phone.WindowsLinkPlaybackUnconfirmed", "Phone.WindowsLinkKeepLocalAudio",
            "Phone.WindowsLinkPlaybackReady", "Phone.WindowsLinkDropOnlyHere", "Phone.WindowsLinkDropBothComputers",
            "Phone.WindowsLinkExclusiveUnavailable"
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
}

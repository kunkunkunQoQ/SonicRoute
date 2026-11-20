using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using SonicRoute;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static int WindowsConnectUi(string output, bool fileBrowser)
    {
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        const string savedAddress = "[2001:db8:1111:2222:3333:4444:5555:6666]:46916";
        var root = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!;
        var hub = typeof(App).Assembly.GetType("SonicRoute.WindowsLinkHub")!;
        var original = hub.GetMethod("EnsureService", statics)!.Invoke(null, null)!;
        var type = original.GetType();
        var ctor = type.GetConstructors(BindingFlags.Public | Hidden).Single();
        static T Read<T>(object target, string name) => (T)target.GetType().GetProperty(name)!.GetValue(target)!;
        object Create(string name)
        {
            var path = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            if (name == "本机")
                File.WriteAllText(Path.Combine(path, "trusted.json"), JsonSerializer.Serialize(new[] {
                    new { Id = new string('A', 64), Name = "工作电脑", Address = savedAddress, Capabilities = 127, AutoConnect = false }
                }));
            object?[] values = ctor.GetParameters().Select(p => p.Name switch
            {
                "stateDirectory" => path, "name" => name, "port" => (object)0, "discovery" => false, _ => p.DefaultValue
            }).ToArray();
            var node = ctor.Invoke(values);
            type.GetProperty("ProbeAudioCapture", Hidden)!.SetValue(node, (Func<CancellationToken, bool>)(_ => false));
            Action<object> accept = pairing => type.GetMethod("AcceptPairing")!.Invoke(node, [Read<Guid>(pairing, "Id"), true]);
            var requested = type.GetEvent("PairingRequested")!;
            requested.AddEventHandler(node, accept.Method.CreateDelegate(requested.EventHandlerType!, accept.Target));
            type.GetMethod("RestoreSavedPeers", Hidden)!.Invoke(node, null);
            return node;
        }

        var local = Create("本机");
        var remote = Create("演示电脑");
        var active = hub.GetField("_service", statics)!;
        var updated = type.GetEvent("Changed")!;
        var onChange = hub.GetMethod("ServiceChanged", statics)!.CreateDelegate(typeof(Action));
        active.SetValue(null, local);
        updated.AddEventHandler(local, onChange);
        var clipboardChanged = type.GetEvent("ClipboardStateChanged", Hidden)!;
        var onClipboard = hub.GetMethod("ServiceClipboardStateChanged", statics)!.CreateDelegate(typeof(Action<string>));
        clipboardChanged.GetAddMethod(true)!.Invoke(local, [onClipboard]);
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        IReadOnlyList<AndroidDevice> noPhones = Array.Empty<AndroidDevice>();
        var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult(noPhones));
        discovery.Publish(noPhones);
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);

        using var page = new PhoneDevicePage();
        var quick = new PhoneAudioWindow(loadDevicesOnShow: false);
        var surface = new Border { Child = page };
        surface.SetResourceReference(Border.BackgroundProperty, "Theme.WindowBg");
        var host = new Window { Content = surface, Width = 570, Height = 590, Left = 24, Top = 32, ShowInTaskbar = false };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        static AutomationPeer Peer(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element)
            ?? throw new InvalidOperationException("A visible action lacks an automation peer.");
        static T Element<T>(FrameworkElement view, string name) where T : FrameworkElement => (T)view.FindName(name);
        try
        {
            host.Show();
            typeof(PhoneDevicePage).GetMethod("ShowConnection", Hidden)!.Invoke(page, null);
            Element<RadioButton>(page, "WindowsLinkConnectionChoice").IsChecked = true;
            var wide = (FrameworkElement)Element<ContentControl>(page, "WindowsLinkCard").Content;
            quick.Show();
            var choice = typeof(PhoneAudioWindow).GetField("WindowsLinkChoice", statics)!.GetValue(null);
            Element<ComboBox>(quick, "DeviceCombo").SelectedItem = choice;
            var viewport = Element<ScrollViewer>(quick, "WindowsLinkPane");
            var compact = (FrameworkElement)viewport.Content;
            quick.Left = host.Left + host.ActualWidth + 12;
            quick.Top = host.Top;
            DrainUi();

            var wideToggle = Element<CheckBox>(wide, "ServiceToggle");
            var compactToggle = Element<CheckBox>(compact, "ServiceToggle");
            var compactAction = (IToggleProvider)Peer(compactToggle).GetPattern(PatternInterface.Toggle);
            Check(!Read<bool>(local, "IsEnabled") && wideToggle.IsChecked == false && compactToggle.IsChecked == false
                && Peer(compactToggle).GetName() == L10n.T("Phone.WindowsLinkEnable"),
                "both actual hosts show the initial switch state and the localized enable action");
            var directInput = Element<TextBox>(compact, "AddressBox");
            var directButton = Element<Button>(compact, "ConnectButton");
            var directNotice = Element<TextBlock>(compact, "DirectNoticeText");
            void SubmitAddress(string address)
            {
                directInput.Text = address;
                ((IInvokeProvider)Peer(directButton).GetPattern(PatternInterface.Invoke)).Invoke();
            }
            SubmitAddress("https://example.invalid/");
            Until(() => directNotice.IsVisible && directNotice.Text == L10n.T("Phone.WindowsLinkDirectDesc"));
            Check(!Read<bool>(local, "IsEnabled"), "an invalid pasted address is explained beside the field and leaves the service off");
            directInput.Clear();
            compactAction.Toggle();
            Until(() => Read<bool>(local, "IsEnabled") && wideToggle.IsChecked == true && compactToggle.IsChecked == true
                && Element<TextBlock>(quick, "ConnectionText").Text == L10n.T("Phone.WindowsLinkReady") && compactToggle.IsEnabled);
            Check(Element<TextBlock>(wide, "ServiceStatusText").Text == L10n.T("Phone.WindowsLinkReady")
                && Peer(compactToggle).GetName() == L10n.T("Phone.WindowsLinkDisable"),
                "the compact header switch enables the isolated service and synchronizes wide header and compact footer status");
            Check(Element<TextBlock>(wide, "HeaderSubtitleText").IsVisible && !Element<TextBlock>(compact, "HeaderSubtitleText").IsVisible,
                "the settings view shows the service description and the narrower view keeps the header compact");

            SubmitAddress(Read<string>(local, "LocalAddress"));
            Until(() => directButton.IsEnabled && directNotice.IsVisible && directNotice.Text == L10n.T("Phone.WindowsLinkSelfAddress"));
            directInput.Clear();
            Check(!directNotice.IsVisible, "typing a new address clears the inline self-connection hint");

            foreach (var view in new[] { wide, compact })
            {
                var header = Element<Border>(view, "ReceiverHeader");
                var section = Element<Border>(view, "DevicesCard");
                var connect = Element<Button>(view, "ConnectButton");
                var address = Element<TextBox>(view, "AddressBox");
                var search = Element<Button>(view, "SearchButton");
                var copy = Element<Button>(view, "LocalAddressBadge");
                Check(header.IsVisible && header.TranslatePoint(new Point(0, header.ActualHeight), view).Y <= section.TranslatePoint(new Point(), view).Y + 1
                    && header.IsAncestorOf(Element<CheckBox>(view, "ServiceToggle")) && DirectFormIsOpen(view),
                    "the service toggle is above the device list and the direct connection fields are already expanded");
                address.Clear();
                Check(!connect.IsEnabled && search.IsEnabled && Peer(search).GetName() == L10n.T("Phone.SearchSavedDevices")
                    && Peer(connect).GetName() == L10n.T("Phone.ConnectShort") && !string.IsNullOrWhiteSpace(Peer(address).GetName())
                    && copy.IsVisible && Peer(copy).GetName().Contains(Read<string>(local, "LocalAddress"))
                    && Peer(copy).GetPattern(PatternInterface.Invoke) != null,
                    "search, copy, and address actions have accessible names, and empty direct-connect is disabled");
                address.Text = savedAddress;
                view.UpdateLayout();
                var list = Element<ItemsControl>(view, "DiscoveredPeersList");
                var text = Descendants<TextBlock>(list).First(label => label.Text == savedAddress && label.IsVisible);
                Check(connect.IsEnabled && text.TextWrapping == TextWrapping.NoWrap
                    && text.TextTrimming == TextTrimming.CharacterEllipsis && Equals(text.ToolTip, savedAddress)
                    && address.Text == savedAddress && text.TranslatePoint(new Point(text.ActualWidth, 0), view).X <= view.ActualWidth + 1,
                    "a long IPv6 endpoint remains available without wrapping the peer row or overflowing the view");
                address.Clear();
            }
            Check(quick.ActualWidth <= 235.5 && viewport.ScrollableWidth <= 1
                && Grid.GetRow(Element<FrameworkElement>(compact, "DirectButtonsGrid")) == 0,
                "the 235-DIP quick panel fits direct input and action on one row without horizontal scroll");

            if (!fileBrowser)
            {
                SaveWindowRegion(Element<FrameworkElement>(page, "ConnectionPanel"), Path.Combine(output, "windows-connect-settings.png"));
                SaveWindowRegion((FrameworkElement)quick.Content, Path.Combine(output, "windows-connect-quick.png"));
            }
            Pump((Task)type.GetMethod("EnableAsync")!.Invoke(remote, [stop.Token])!);
            Element<TextBox>(compact, "AddressBox").Text = Read<string>(remote, "LocalAddress");
            ((IInvokeProvider)Peer(Element<Button>(compact, "ConnectButton")).GetPattern(PatternInterface.Invoke)).Invoke();
            Until(() => Read<IEnumerable>(local, "Peers").Cast<object>().Any(p => Read<bool>(p, "Connected"))
                && Read<IEnumerable>(remote, "Peers").Cast<object>().Any(p => Read<bool>(p, "Connected")));
            Check(Read<IEnumerable>(remote, "Peers").Cast<object>().Any(p => Read<bool>(p, "Connected")),
                "invoking the narrow address action pairs and connects two test instances over a real loopback socket");
            Until(() => directButton.IsEnabled);
            SubmitAddress(Read<string>(remote, "LocalAddress"));
            Until(() => directButton.IsEnabled && directNotice.Text == L10n.T("Phone.WindowsLinkAlreadyConnected"));
            Check(Read<IEnumerable>(local, "Peers").Cast<object>().Count(p => Read<bool>(p, "Connected")) == 1,
                "reconnecting to the same address explains that the peer is already connected and keeps one session");
            var selector = Element<ComboBox>(quick, "DeviceCombo");
            var remoteId = Read<string>(Read<IEnumerable>(local, "Peers").Cast<object>().First(p => Read<bool>(p, "Connected")), "Id");
            Until(() => selector.Items.Cast<object>().Any(item => DeviceId(item) == remoteId));
            static Button? OpenButton(FrameworkElement view) => Descendants<Button>(Element<ItemsControl>(view, "DiscoveredPeersList"))
                .FirstOrDefault(button => button.Name == "OpenPeerButton" && button.IsVisible);
            Until(() => OpenButton(compact) != null);
            ((IInvokeProvider)Peer(OpenButton(compact)!).GetPattern(PatternInterface.Invoke)).Invoke();
            Until(() => DeviceId(selector.SelectedItem) == remoteId);
            Check(DeviceId(selector.SelectedItem) == remoteId, "Open on the connected quick-panel row selects that PC");
            var signal = Element<Grid>(quick, "ConnectionSignal");
            Until(() => signal.IsVisible && signal.ToolTip is string hint && hint.Contains("ms"));
            var deviceCard = Element<Border>(quick, "PhoneDeviceCard");
            var refresh = Element<Button>(quick, "RefreshButton");
            var status = Element<TextBlock>(quick, "ConnectionText");
            var signalRight = signal.TranslatePoint(new Point(signal.ActualWidth, 0), deviceCard);
            Check(signalRight.X <= status.TranslatePoint(new Point(), deviceCard).X && signalRight.Y >= 0
                && System.Windows.Automation.AutomationProperties.GetName(signal) == (string)signal.ToolTip,
                $"a connected Windows peer shows the measured TLS round trip beside its status ({signal.ToolTip})");
            double CenterY(FrameworkElement element) => element.TranslatePoint(new Point(0, element.ActualHeight / 2), deviceCard).Y;
            Check(Math.Abs(CenterY(selector) - CenterY(status)) < 1 && Math.Abs(CenterY(signal) - CenterY(status)) < 1,
                "the PC name, latency signal and connection status occupy the same line");
            Check(Math.Abs(deviceCard.ActualHeight - 36) < 0.51 && Math.Abs(deviceCard.ActualHeight - refresh.ActualHeight) < 0.01
                && Math.Abs(refresh.ActualHeight - refresh.ActualWidth) < 0.01
                && Math.Abs(refresh.TranslatePoint(new Point(), deviceCard).Y) < 0.01,
                "the compact PC selector and square refresh button share their top and bottom edges");
            if (!fileBrowser)
            {
                ThemeService.Apply(true, "#00CBE9");
                DrainUi();
                SaveWindowRegion(Element<Grid>(quick, "DeviceFooter"), Path.Combine(output, "connected-peer-footer-dark.png"));
                ThemeService.Apply(false, "#00CBE9");
            }
            selector.SelectedItem = choice;
            Check(!signal.IsVisible && !refresh.IsVisible, "leaving a paired peer hides its latency indicator and refresh button");
            if (fileBrowser)
            {
                var other = Read<IEnumerable>(local, "Peers").Cast<object>().Single(p => Read<bool>(p, "Connected"));
                WindowsFileBrowserUi(page, local, remote, Read<string>(other, "Id"), output);
                return 0;
            }
            Until(() => OpenButton(wide) != null);
            ((IInvokeProvider)Peer(OpenButton(wide)!).GetPattern(PatternInterface.Invoke)).Invoke();
            var clipboard = Element<CheckBox>(page, "ClipboardSyncToggle");
            var clipboardStatus = Element<TextBlock>(page, "ClipboardStatus");
            typeof(PhoneDevicePage).GetMethod("Activate", Hidden)!.Invoke(page, null);
            Until(() => Element<TextBlock>(page, "DeviceName").Text == "演示电脑" && clipboard.IsVisible && clipboard.IsEnabled);
            Check(clipboard.IsChecked == false && clipboardStatus.Text == L10n.T("Phone.LinkClipboardOff"),
                "Open on the connected settings row shows that PC with clipboard sharing initially off");
            ((IToggleProvider)Peer(clipboard).GetPattern(PatternInterface.Toggle)).Toggle();
            Until(() => clipboardStatus.Text == L10n.T("Phone.LinkClipboardWaitPeer"));
            var localId = Read<string>(Read<IEnumerable>(remote, "Peers").Cast<object>().Single(p => Read<bool>(p, "Connected")), "Id");
            var setClipboard = type.GetMethod("SetClipboardEnabled", Hidden)!;
            setClipboard.Invoke(remote, [localId, true]);
            Until(() => clipboardStatus.Text == L10n.T("Phone.ClipboardConnected"));
            setClipboard.Invoke(remote, [localId, false]);
            Until(() => clipboardStatus.Text == L10n.T("Phone.LinkClipboardWaitPeer"));
            ((IToggleProvider)Peer(clipboard).GetPattern(PatternInterface.Toggle)).Toggle();
            Until(() => clipboard.IsChecked == false && clipboardStatus.Text == L10n.T("Phone.LinkClipboardOff"));
            Check(clipboard.IsChecked == false, "both clipboard switches and immediate remote revocation are reflected without refreshing");
            typeof(PhoneDevicePage).GetMethod("ShowConnection", Hidden)!.Invoke(page, null);
            ((IToggleProvider)Peer(wideToggle).GetPattern(PatternInterface.Toggle)).Toggle();
            Until(() => !Read<bool>(local, "IsEnabled") && compactToggle.IsChecked == false
                && Element<TextBlock>(quick, "ConnectionText").Text == L10n.T("Phone.WindowsLinkOff"));
            Check(Element<TextBlock>(quick, "ConnectionText").Text == L10n.T("Phone.WindowsLinkOff"),
                "turning off the wide switch updates the already-open quick panel");
            return 0;
        }
        finally
        {
            quick.Close();
            host.Close();
            clipboardChanged.GetRemoveMethod(true)!.Invoke(local, [onClipboard]);
            updated.RemoveEventHandler(local, onChange);
            active.SetValue(null, original);
            Pump(((IAsyncDisposable)remote).DisposeAsync().AsTask());
            Pump(((IAsyncDisposable)local).DisposeAsync().AsTask());
        }
    }
}

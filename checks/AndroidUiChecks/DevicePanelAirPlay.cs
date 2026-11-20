using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static int DevicePanelAirPlayUi(string output, bool testLan)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var assembly = typeof(App).Assembly;
        var cache = assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var android = assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var airPlay = assembly.GetType("SonicRoute.AirPlayHost")!;
        int scans = 0;
        var discovery = new AndroidDeviceDiscovery(_ => { scans++; throw new InvalidOperationException("Unexpected Android scan."); });
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        var offline = new AndroidDevice("fixture-offline", "Android", "offline", false, "device-panel-fixture");
        var store = (AndroidPhoneStore)android.GetProperty("Phone", statics)!.GetValue(null)!;
        store.RememberDevice(offline);
        store.Retain(new AndroidApp("cn.fixture.music", "Music", 1001, "media"));
        discovery.Publish([offline]);

        PhoneAudioWindow panel = OpenPanel();
        using var widePage = new PhoneDevicePage();
        typeof(PhoneDevicePage).GetMethod("ShowConnection", Hidden)!.Invoke(widePage, null);
        ((RadioButton)widePage.FindName("AirPlayConnectionChoice")).IsChecked = true;
        var wideSurface = new Border { Child = widePage, Padding = new Thickness(10) };
        wideSurface.SetResourceReference(Border.BackgroundProperty, "Theme.WindowBg");
        var wideWindow = new Window { Content = wideSurface, Width = 600, Height = 350, ShowInTaskbar = false,
            ShowActivated = false, Left = -10000, Top = -10000 };
        wideWindow.SetResourceReference(Window.BackgroundProperty, "Theme.WindowBg");
        wideWindow.Show();
        DrainUi();
        var wide = (AirPlayReceiverView)widePage.FindName("AirPlayConnectionView");
        var wideToggle = (CheckBox)wide.FindName("AirPlayToggle");
        var wideStatus = (TextBlock)wide.FindName("ReceiveStatusLabel");
        object? Receiver() => airPlay.GetField("_receiver", statics)!.GetValue(null);
        try
        {
            var selector = (ComboBox)panel.FindName("DeviceCombo");
            object choice = typeof(PhoneAudioWindow).GetField("AirPlayChoice",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            var view = (AirPlayReceiverView)panel.FindName("AirPlayReceiverPane");
            var list = (ListBox)panel.FindName("PhoneApps");
            var deviceIcon = (System.Windows.Shapes.Path)panel.FindName("DeviceKindIcon");
            Check(panel.Title == "设备面板" && selector.Items.Count == 2 && selector.Items.OfType<AndroidDevice>().Count() == 1
                && selector.Items.Contains(choice) && selector.SelectedItem is AndroidDevice,
                "the device dropdown lists the saved Android device and the AirPlay receiver");
            var connections = Descendants<RadioButton>(widePage).Where(button => button.GroupName == "PhoneConnection")
                .Select(button => button.Name).ToHashSet();
            Check(connections.SetEquals(["AndroidConnectionChoice", "AirPlayConnectionChoice"]),
                "the connection page offers Android and AirPlay");
            Check(!view.IsVisible && panel.FindName("AirPlayButton") == null && list.Items.Count == 2,
                "Android shows its original app pane and no separate AirPlay footer button");
            Check(ReferenceEquals(deviceIcon.Data, AutomationIcons.Phone), "Android has its phone icon in the device selector");
            selector.SelectedItem = choice;
            DrainUi();
            var toggle = (CheckBox)view.FindName("AirPlayToggle");
            var codecs = (ComboBox)view.FindName("MirrorDecoder");
            var receiverName = (TextBox)view.FindName("ReceiverName");
            var requireCode = (CheckBox)view.FindName("RequireCode");
            var statusLabel = (TextBlock)view.FindName("ReceiveStatusLabel");
            var footerStatus = (TextBlock)panel.FindName("ConnectionText");
            Check(view.IsVisible && !list.IsVisible && !((FrameworkElement)panel.FindName("DeviceMediaCard")).IsVisible
                && !((FrameworkElement)panel.FindName("RefreshButton")).IsVisible
                && footerStatus.IsVisible && footerStatus.Text == statusLabel.Text
                && ((FrameworkElement)view.FindName("ReceiverHeader")).IsVisible && !((FrameworkElement)view.FindName("HeaderStatus")).IsVisible
                && wideStatus.IsVisible && Receiver() == null,
                "AirPlay reuses the device footer for status and stops Android-only UI");
            Check(ReferenceEquals(deviceIcon.Data, AutomationIcons.AirPlay), "AirPlay has its own icon in the device selector");
            var airPlayRow = AppItem.From(new AudioAppInfo { ProcessId = 0, ExternalSourceId = "airplay:fixture", ProcessName = "airplay:audio" });
            var androidRow = AppItem.From(new AudioAppInfo
            {
                ProcessId = 0, ExternalSourceId = "android:fixture", ProcessName = "android:" + AndroidApp.DeviceAudioPackage
            });
            airPlayRow.LoadIcon();
            androidRow.LoadIcon();
            Check(airPlayRow.Icon is DrawingImage { IsFrozen: true, Drawing: GeometryDrawing airPlayDrawing }
                && ReferenceEquals(airPlayDrawing.Geometry, AutomationIcons.AirPlay)
                && androidRow.Icon is DrawingImage { IsFrozen: true, Drawing: GeometryDrawing androidDrawing }
                && ReferenceEquals(androidDrawing.Geometry, AutomationIcons.Phone),
                "the mixer uses two distinct frozen device-source icons");
            Check(toggle.IsEnabled && toggle.IsChecked == false && toggle.Template.FindName("Track", toggle) is Border
                && wideToggle.IsChecked == false, "both AirPlay surfaces start off with the shared theme switch");
            bool hevc = MediaFoundationH264Decoder.SupportsHevc;
            if (hevc) Until(() => codecs.Items.Count == 3);
            Check(codecs.SelectedIndex == 0 && codecs.Items.Count == (hevc ? 3 : 2),
                "Automatic is selected first and HEVC is shown only when Windows has a decoder");
            Check(requireCode.IsChecked == false, "Receiver PIN starts off for existing settings");
            Point Position(FrameworkElement element) => element.TranslatePoint(new Point(0, 0), view);
            Check(requireCode.Template.FindName("Track", requireCode) is Border && requireCode.Template.FindName("Thumb", requireCode) != null,
                "the device panel resolves the shared rounded switch template");
            Check(Math.Abs(Position(receiverName).X - Position(codecs).X) < 1 && receiverName.ActualHeight == codecs.ActualHeight
                && Position(toggle).Y < Position(receiverName).Y && Position(codecs).X + codecs.ActualWidth <= view.ActualWidth + 1,
                "the primary action appears first and the two settings fit in aligned columns");
            foreach (var language in L10n.SupportedLanguages)
            {
                L10n.Instance.SetLanguage(language.Code); DrainUi();
                Check(panel.Title == L10n.T("Phone.AudioWindowTitle") && !panel.Title.StartsWith("Phone.")
                    && codecs.Items[0].Equals(L10n.T("Phone.StreamDecoderAutomatic"))
                    && (!hevc || codecs.Items[2].Equals(L10n.T("Phone.AirPlayHevcOption")))
                    && footerStatus.Text == L10n.T("Phone.AirPlayOff")
                    && System.Windows.Automation.AutomationProperties.GetName(toggle) == L10n.T("Phone.AirPlayReceive")
                    && !System.Windows.Automation.AutomationProperties.GetName(requireCode).StartsWith("Phone."),
                    language.Code + " localizes the panel title, receiver preferences and decoder choices");
            }
            L10n.Instance.SetLanguage("zh-CN");
            receiverName.Text = "  测试.投屏  ";
            receiverName.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, receiverName, toggle)
                { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            codecs.SelectedIndex = hevc ? 2 : 1;
            requireCode.IsChecked = true;
            requireCode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(ConfigService.PendingSaves);
            using (var saved = JsonDocument.Parse(File.ReadAllBytes(ConfigService.ConfigPath)))
                Check(saved.RootElement.GetProperty("AirPlayReceiverName").GetString() == "测试.投屏"
                    && saved.RootElement.GetProperty("AirPlayVideoDecoder").GetInt32() == codecs.SelectedIndex
                    && saved.RootElement.GetProperty("AirPlayRequireCode").GetBoolean(), "AirPlay preferences persist to the application configuration");
            Render((FrameworkElement)panel.Content, 235, 450, Path.Combine(output, "device-panel-airplay.png"));
            Render(wideSurface, 580, 225, Path.Combine(output, "airplay-connection-wide.png"));
            Check(((System.Windows.Controls.Primitives.UniformGrid)view.FindName("PreferenceFields")).Columns == 1 &&
                ((System.Windows.Controls.Primitives.UniformGrid)wide.FindName("PreferenceFields")).Columns == 2 &&
                ((TextBox)wide.FindName("ReceiverName")).Text == receiverName.Text,
                "the compact panel stacks its fields and the wide connection page aligns the same preferences side by side");
            ThemeService.Apply("dark", "#00CBE9");
            try
            {
                DrainUi();
                Render((FrameworkElement)panel.Content, 235, 450, Path.Combine(output, "device-panel-airplay-dark.png"));
            }
            finally { ThemeService.Apply("light", "#00CBE9"); }
            var busyStates = new HashSet<string>();
            Action rememberBusy = () => { if (!wideToggle.IsEnabled) busyStates.Add(wideStatus.Text); };
            wide.GetType().GetEvent("StatusUpdated", Hidden)!.GetAddMethod(true)!.Invoke(wide, [rememberBusy]);
            int port = 0;
            if (testLan)
            {
                toggle.IsChecked = true;
                Until(() => wideToggle.IsChecked == true && toggle.IsEnabled && wideToggle.IsEnabled &&
                    wideStatus.Text == L10n.T("Phone.AirPlayWaitingShort"));
                object receiver = Receiver()!;
                port = (int)receiver.GetType().GetProperty("Port", Hidden)!.GetValue(receiver)!;
                Check(port > 0 && !codecs.IsEnabled && IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port == port),
                    "the actual Windows LAN receiver starts and keeps the decoder fixed during reception");
                Check(((TextBlock)view.FindName("ReceiveCode")).Text.Length == 4
                    && statusLabel.Text == L10n.T("Phone.AirPlayWaitingShort")
                    && ((TextBlock)view.FindName("AirPlayStatusText")).Text == "测试.投屏", "protected reception displays the temporary code and the configured name");
                Render((FrameworkElement)panel.Content, 235, 450, Path.Combine(output, "device-panel-airplay-waiting.png"));
            }
            selector.SelectedItem = selector.Items.OfType<AndroidDevice>().Single();
            DrainUi();
            Check(list.IsVisible && list.Items.Count == 2 && !view.IsVisible && footerStatus.IsVisible && panel.AllowDrop && store.Profile.LastDevice != null
                && ReferenceEquals(deviceIcon.Data, AutomationIcons.Phone),
                "switching back restores Android apps and their selected device");
            selector.SelectedItem = choice;
            panel.Close();
            panel = OpenPanel();
            view = (AirPlayReceiverView)panel.FindName("AirPlayReceiverPane");
            Check(view.IsVisible && store.Profile.LastDevice != null, "reopening remembers AirPlay separately from the Android profile");
            if (testLan)
            {
                toggle = (CheckBox)view.FindName("AirPlayToggle");
                Check(Receiver() != null && toggle.IsChecked == true,
                    "switching devices and closing the panel leave explicit AirPlay reception running");
                wideToggle.IsChecked = false;
                Until(() => toggle.IsChecked == false && toggle.IsEnabled && wideToggle.IsEnabled && wideStatus.Text == L10n.T("Phone.AirPlayOff"));
                Check(busyStates.Contains(L10n.T("Phone.AirPlayStarting")) && busyStates.Contains(L10n.T("Phone.AirPlayStopping")),
                    "the two switch requests publish disabled starting and stopping states across the views");
                Check(Receiver() == null && !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port == port),
                    "Stop receiving closes the real TCP listener");
            }
            Until(() => ((ComboBox)view.FindName("MirrorDecoder")).SelectedIndex == (hevc ? 2 : 1));
            Check(((TextBox)view.FindName("ReceiverName")).Text == "测试.投屏" && ((CheckBox)view.FindName("RequireCode")).IsChecked == true,
                "reopening the view restores the name, PIN preference, and decoder choice");
            wide.GetType().GetEvent("StatusUpdated", Hidden)!.GetRemoveMethod(true)!.Invoke(wide, [rememberBusy]);
            var inactive = airPlay.GetField("_inactive", statics)!;
            var idle = inactive.GetValue(null)!;
            var failure = idle.GetType().GetMethod("<Clone>$")!.Invoke(idle, null)!;
            var stage = failure.GetType().GetProperty("Stage")!;
            stage.SetValue(failure, Enum.Parse(stage.PropertyType, "Failed"));
            failure.GetType().GetProperty("ErrorKey")!.SetValue(failure, "Phone.AirPlayNoIpv4");
            try
            {
                inactive.SetValue(null, failure);
                airPlay.GetMethod("PublishDisplay", statics)!.Invoke(null, null);
                Check(wideToggle.IsChecked == false && wideToggle.IsEnabled && wideStatus.Text == L10n.T("Phone.AirPlayFailed")
                    && ((TextBlock)view.FindName("AirPlayStatusText")).Text == L10n.T("Phone.AirPlayNoIpv4"),
                    "a receiver failure restores an available off switch with the specific reason on both surfaces");
            }
            finally
            {
                inactive.SetValue(null, idle);
                airPlay.GetMethod("PublishDisplay", statics)!.Invoke(null, null);
            }
            CheckAirPlayDropsAndTrack(panel, view, choice, discovery, store, airPlay, output);
            Check(scans == 0 && !(bool)android.GetProperty("IsAdbCreated", statics)!.GetValue(null)!,
                "AirPlay and the saved Android view need no device scan or ADB instance");
            File.WriteAllText(Path.Combine(output, "device-panel-airplay.json"), JsonSerializer.Serialize(new
            { Assertions = _checks, LanStartStop = testLan, Hevc = hevc, AndroidScans = scans, RealApple = false }));
            return 0;
        }
        finally
        {
            wideWindow.Close();
            panel.Close();
            if (Receiver() != null) Pump((Task)airPlay.GetMethod("StopAsync", statics)!.Invoke(null, null)!);
        }

        static PhoneAudioWindow OpenPanel()
        {
            var window = new PhoneAudioWindow(false) { ShowActivated = false, Left = -10000, Top = -10000 };
            window.Show();
            DrainUi();
            return window;
        }
    }

    private delegate void WriteAirPlaySamples(ReadOnlySpan<float> samples, int sampleRate);
    private sealed class AirPlayFixtureOutput : IPcmPlayer
    {
        public void Play() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static void CheckAirPlayDropsAndTrack(PhoneAudioWindow panel, AirPlayReceiverView view, object choice,
        AndroidDeviceDiscovery discovery, AndroidPhoneStore store, Type host, string output)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var library = typeof(AndroidDevice).Assembly;
        var receiverType = library.GetType("SonicRoute.AirPlay.AirPlayReceiver")!;
        var sessionType = receiverType.GetNestedType("Session", BindingFlags.NonPublic)!;
        var sourceType = library.GetType("SonicRoute.AirPlay.AirPlayAudioOutput")!;
        var statusType = library.GetType("SonicRoute.AirPlay.AirPlayStatus")!;
        var metadataType = library.GetType("SonicRoute.AirPlay.AirPlayMetadata")!;
        Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer> player = (_, _, _) => new AirPlayFixtureOutput();
        Func<Action, IDisposable> watch = _ => new AirPlayFixtureOutput();
        using var transport = new TcpClient();
        using var lifetime = new CancellationTokenSource();
        var receiver = (IAsyncDisposable)Activator.CreateInstance(receiverType, instance, null, ["Fixture", true, player, watch, false, false], null)!;
        var source = (IAsyncDisposable)Activator.CreateInstance(sourceType, instance, null,
            ["Simulated Apple device", host.GetMethod("Publish", statics)!.CreateDelegate<Action>(), (Action)(() => { }), player, watch, CancellationToken.None], null)!;
        var session = Activator.CreateInstance(sessionType, instance, null, [receiver, transport, IPAddress.Loopback, IPAddress.Loopback], null)!;
        sessionType.GetField("_stop", instance)!.SetValue(session, lifetime);
        sessionType.GetField("_output", instance)!.SetValue(session, source);
        var active = receiverType.GetField("_active", instance)!;
        active.SetValue(receiver, session);
        var receiverField = host.GetField("_receiver", statics)!;
        var quick = new QuickPanelModernWindow();
        try
        {
            ConfigService.Load().QuickPanelUseLegacyAppRows = false;
            receiverField.SetValue(null, receiver);
            sourceType.GetMethod("Write", instance)!.CreateDelegate<WriteAirPlaySamples>(source)([0.25f, -0.25f], AndroidPcmSource.SampleRate);
            string id = (string)sourceType.GetProperty("Id", instance)!.GetValue(source)!;
            var info = new AudioAppInfo { ProcessId = 0, ProcessName = "airplay:audio", ExternalSourceId = id };
            var data = new DataObject("SonicRoute.PcApp.v1", info);
            var playbackOutput = sourceType.GetProperty("State", instance)!;
            string? SelectedOutput() => ((AndroidAudioState)playbackOutput.GetValue(source)!).OutputId;
            DrainUi();
            var mixerRows = (System.Collections.IDictionary)typeof(QuickPanelModernWindow).GetField("_androidRows", Hidden)!.GetValue(quick)!;
            var row = mixerRows[id]!;
            var muteButton = (Button)row.GetType().GetField("Icon", instance)!.GetValue(row)!;
            var muteDot = (Border)row.GetType().GetField("MuteDot", instance)!.GetValue(row)!;
            var volume = (Slider)row.GetType().GetField("Volume", instance)!.GetValue(row)!;
            foreach (bool muted in new[] { true, false })
            {
                muteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DrainUi();
                Check(((AndroidAudioState)playbackOutput.GetValue(source)!).Muted == muted && muteButton.Opacity == 1
                    && muteDot.Visibility == (muted ? Visibility.Visible : Visibility.Collapsed)
                    && volume.Foreground == quick.FindResource(muted ? "Theme.AccentInverse" : "Theme.Accent"),
                    "AirPlay mute feedback follows icon clicks in the standard app layout: " + muted);
            }
            var statusField = receiverType.GetField("_status", instance)!;
            var selector = (ComboBox)panel.FindName("DeviceCombo");
            void Connected(byte[] cover)
            {
                var track = Activator.CreateInstance(metadataType, instance, null, ["测试歌曲", "测试歌手", cover], null);
                var stage = Enum.Parse(library.GetType("SonicRoute.AirPlay.AirPlayStage")!, "Connected");
                statusField.SetValue(receiver, Activator.CreateInstance(statusType, instance, null,
                    [stage, "Fixture", "Simulated Apple device", null, null, true, false, false, false, null, track], null));
                host.GetMethod("PublishDisplay", statics)!.Invoke(null, null);
            }
            using var png = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[256], 32)));
            encoder.Save(png);
            Connected(png.ToArray());
            var image = (Image)view.FindName("ArtworkImage");
            Until(() => image.Source is BitmapSource { IsFrozen: true });
            Check(((TextBlock)view.FindName("TrackTitle")).Text == "测试歌曲" && ((TextBlock)view.FindName("TrackArtist")).Text == "测试歌手"
                && ((FrameworkElement)view.FindName("MirrorControls")).IsVisible && ((CheckBox)view.FindName("AirPlayToggle")).IsChecked == true
                && ((TextBlock)view.FindName("ReceiveStatusLabel")).Text == L10n.T("Phone.ConnectedStatus"),
                "connected pane shows the enabled receiver, sender artwork, track text and window controls");
            Render((FrameworkElement)panel.Content, 235, 450, Path.Combine(output, "device-panel-airplay-connected.png"));

            var overlay = (Border)panel.FindName("PcDropHighlight");
            var unrelated = new DataObject("SonicRoute.PcApp.v1", new AudioAppInfo { ProcessName = "fixture", ProcessId = 42 });
            Check(Drag(panel, overlay, unrelated, drop: true) == DragDropEffects.None && !lifetime.IsCancellationRequested,
                "ordinary desktop drags cannot disconnect AirPlay");
            var device = new AndroidDevice("offline-fixture", "Android", "device", false, "device-panel-fixture");
            store.RememberDevice(device);
            store.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker]);
            discovery.Publish([device]);
            DrainUi();
            selector.SelectedItem = selector.Items.OfType<AndroidDevice>().Single();
            DrainUi();
            Check(image.Source == null, "leaving AirPlay releases the decoded cover");
            foreach (string sink in new[] { "current", "speaker" })
            {
                string endpoint = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), sink);
                Check(Drag(panel, overlay, data, drop: false) == DragDropEffects.Copy, "AirPlay drag opens Android drop targets for " + sink);
                Point center = PhoneDropCellCenter(panel, endpoint);
                Check(Drag(panel, overlay, data, drop: true, center) == DragDropEffects.Copy, "AirPlay accepts the " + sink + " drop");
                Until(() => SelectedOutput() == endpoint);
                Check(!lifetime.IsCancellationRequested, "the same AirPlay session routes to the exact " + sink + " endpoint");
            }

            var mixer = (FrameworkElement)quick.FindName("AppListScroll");
            var back = new DataObject("SonicRoute.AirPlayToWindows.v1", info);
            Check(Drag(mixer, mixer, back, drop: false) == DragDropEffects.Move
                && Drag(mixer, mixer, back, drop: true) == DragDropEffects.Move, "dragging back to the mixer accepts the system output zone");
            Until(() => SelectedOutput() == null);
            selector.SelectedItem = choice;
            DrainUi();
            Check(Drag(panel, overlay, data, drop: false) == DragDropEffects.Move
                && Drag(panel, overlay, data, drop: true) == DragDropEffects.Move && lifetime.IsCancellationRequested,
                "dropping onto the Apple interface ends the foreground AirPlay session");
        }
        finally
        {
            quick.Close();
            receiverField.SetValue(null, null);
            active.SetValue(receiver, null);
            Pump(source.DisposeAsync().AsTask());
            Pump(receiver.DisposeAsync().AsTask());
        }

        static DragDropEffects Drag(FrameworkElement target, FrameworkElement coordinates, DataObject data, bool drop, Point? position = null)
        {
            var routed = drop ? System.Windows.DragDrop.PreviewDropEvent : System.Windows.DragDrop.PreviewDragOverEvent;
            var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), instance, null,
                [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy | DragDropEffects.Move, coordinates, position ?? new Point(20, 20)], null)!;
            args.RoutedEvent = routed;
            target.RaiseEvent(args);
            return args.Effects;
        }
    }
}

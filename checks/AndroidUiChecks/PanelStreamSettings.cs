using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static int PanelStreamSettingsUi(string output)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        int scans = 0;
        var discovery = new AndroidDeviceDiscovery(_ => { scans++; throw new Exception("No device access in this fixture"); });
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        var device = new AndroidDevice("offline-fixture", "Fixture", "device", false, "panel-stream-fixture");
        discovery.Publish([]);
        var phoneStore = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        phoneStore.RememberDevice(device); phoneStore.Rename("小米");
        phoneStore.Retain(new AndroidApp("cn.test.music", "波点音乐", 1001, "media"));
        object Get(object obj, string name) => obj.GetType().GetField(name, Hidden)!.GetValue(obj)!;
        object? Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Hidden)!.Invoke(obj, args);
        int encoderQueries = 0, queryThread = 0;
        string? querySerial = null;
        var queryTokens = new System.Collections.Concurrent.ConcurrentQueue<CancellationToken>();
        var firstCatalog = new TaskCompletionSource<AndroidEncoderCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextCatalog = new TaskCompletionSource<AndroidEncoderCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<AndroidDevice, CancellationToken, Task<AndroidEncoderCatalog>> readEncoders = (phone, token) =>
        {
            queryThread = Environment.CurrentManagedThreadId; querySerial = phone.Serial; queryTokens.Enqueue(token);
            return Interlocked.Increment(ref encoderQueries) == 1 ? firstCatalog.Task : nextCatalog.Task;
        };
        using var page = (PhoneDevicePage)typeof(PhoneDevicePage).GetConstructors(Hidden).Single()
            .Invoke([null, null, readEncoders]);
        Check(encoderQueries == 0, "constructing a phone settings page does not query encoders");
        var canvas = new Grid();
        canvas.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "Theme.WindowBg");
        canvas.Children.Add(page);
        var pageHost = new Window { Content = canvas, Width = 520, Height = 640, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        var panel = new QuickPanelModernWindow();
        var phone = new PhoneAudioWindow(false) { ShowActivated = false, Left = -10000, Top = -10000 };
        try
        {
            Call(page, "Activate");
            DrainUi();
            Check(encoderQueries == 0, "activating an unloaded phone settings page starts no encoder query");
            pageHost.Show();
            var settingsRead = (Task<AndroidAudioSettingsStore>)host.GetMethod("PrepareStreamingAsync", statics)!.Invoke(null, [CancellationToken.None])!;
            Pump(settingsRead); DrainUi();
            var bitrate = (ComboBox)page.FindName("VideoBitRateCombo");
            var fpsCombo = (ComboBox)page.FindName("VideoFpsCombo");
            var audio = (ComboBox)page.FindName("AudioEncoderCombo");
            var video = (ComboBox)page.FindName("VideoEncoderCombo");
            var audioDecoder = (ComboBox)page.FindName("AudioDecoderCombo");
            var videoDecoder = (ComboBox)page.FindName("VideoDecoderCombo");
            var audioLatency = (ComboBox)page.FindName("AudioLatencyCombo");
            Check(Grid.GetRow(audioLatency) == 0 && Grid.GetColumn(audioLatency) == 4
                && (string)audioLatency.SelectedValue == AndroidStreamSettings.LatencyModeBalanced
                && audioLatency.Items.Count == 3,
                "audio latency mode selector occupies slot (0,4) and defaults to balanced with 3 modes");
            Until(() => Get(page, "_windowsHevcRead") is Task { IsCompletedSuccessfully: true });
            bool windowsHevc = MediaFoundationH264Decoder.SupportsHevc;
            var hevcOption = videoDecoder.Items.Cast<object>().Single(x => x.ToString()!.StartsWith("H.265 (HEVC)"));
            Check(encoderQueries == 0 && (bool)hevcOption.GetType().GetProperty("IsEnabled")!.GetValue(hevcOption)! == windowsHevc,
                "offline settings list H.265 with Windows availability and no Android encoder request");
            videoDecoder.SelectedValue = AndroidStreamSettings.WindowsHevcDecoder;
            Pump(settingsRead.Result.FlushAsync());
            Check(settingsRead.Result.GetStreaming(device).VideoDecoder == (windowsHevc ? AndroidStreamSettings.WindowsHevcDecoder : null),
                "offline decoder selection is saved only when Windows supports it");
            videoDecoder.SelectedValue = "";
            discovery.Publish([device]);
            Call(page, "ApplyDevices", (object)new[] { device });
            Until(() => Volatile.Read(ref encoderQueries) == 1);
            Check((string)bitrate.SelectedValue == "4000000" && (string)audio.SelectedValue == "" && Get(page, "_streamEncoders") == null,
                "streaming controls show usable saved defaults while the visible page preloads encoders");
            Check(querySerial == device.Serial && queryThread != Environment.CurrentManagedThreadId,
                "entering the visible device page queries only that phone on a worker");
            Check(videoDecoder.Items.Count == 4, "a pending Android query keeps the local decoder choice visible");
            audio.IsDropDownOpen = true; DrainUi();
            var loadingItem = audio.Items.Cast<object>().Single(x => x.GetType().GetProperty("Value")!.GetValue(x)?.ToString() == "#status");
            Check(audio.ItemContainerGenerator.ContainerFromItem(loadingItem) is ComboBoxItem { IsEnabled: false },
                "the cold encoder list immediately displays a disabled translated loading row");
            audio.IsDropDownOpen = false; video.IsDropDownOpen = true; DrainUi(); video.IsDropDownOpen = false;
            Check(encoderQueries == 1, "opening both encoder dropdowns reuses the page preload");
            var oldRead = (Task)Get(page, "_encoderRead");
            pageHost.Hide(); DrainUi();
            Check(queryTokens.First().IsCancellationRequested && Get(page, "_encoderOperation") == null,
                "hiding phone settings cancels its encoder work without starting idle retries");
            pageHost.Show(); DrainUi(); Until(() => Volatile.Read(ref encoderQueries) == 2);
            var currentRead = (Task)Get(page, "_encoderRead");
            firstCatalog.SetResult(new(["c2.old.opus.encoder"], ["c2.old.avc.encoder"]));
            Pump(oldRead); DrainUi();
            Check(Get(page, "_streamEncoders") == null && ReferenceEquals(currentRead, Get(page, "_encoderRead")),
                "a canceled late query cannot publish old encoders or clear the replacement query");
            Check(audio.SelectionBoxItem.ToString() == L10n.T("Phone.StreamAutomatic") && bitrate.SelectionBoxItem.ToString() == "4 Mbps",
                "the existing compact combo template displays readable option labels");

            string[] bitrateValues = bitrate.ItemsSource.Cast<object>().Select(x => x.GetType().GetProperty("Value")!.GetValue(x)!.ToString()!).ToArray();
            string[] fpsValues = fpsCombo.ItemsSource.Cast<object>().Select(x => x.GetType().GetProperty("Value")!.GetValue(x)!.ToString()!).ToArray();
            Check(bitrateValues.SequenceEqual(["1000000", "4000000", "6000000", "8000000", "12000000", "16000000", "24000000", "32000000", "50000000"])
                && !bitrateValues.Contains("2000000"),
                "video bitrate presets exclude 2 Mbps and include 24, 32, and 50 Mbps");
            Check(fpsValues.SequenceEqual(["15", "24", "30", "60", "120"]),
                "video fps presets include 120 fps");

            var simulatedCatalog = new AndroidEncoderCatalog(
                ["c2.android.opus.encoder"],
                ["c2.android.avc.encoder", "c2.qti.avc.encoder", "c2.mtk.avc.encoder", "c2.custom.oem.avc"],
                new Dictionary<string, AndroidEncoderKind>(StringComparer.Ordinal)
                {
                    ["c2.android.opus.encoder"] = AndroidEncoderKind.Software,
                    ["c2.android.avc.encoder"] = AndroidEncoderKind.Software,
                    ["c2.qti.avc.encoder"] = AndroidEncoderKind.Hardware,
                    ["c2.mtk.avc.encoder"] = AndroidEncoderKind.Hardware,
                    ["c2.custom.oem.avc"] = AndroidEncoderKind.Hardware,
                    ["c2.qti.hevc.encoder"] = AndroidEncoderKind.Hardware
                }, ["c2.qti.hevc.encoder", "c2.qti.hevc.encoder.cq", "c2.qti.hevc.encoder.hdr"]);
            nextCatalog.SetResult(simulatedCatalog);
            Pump(currentRead);
            DrainUi();
            pageHost.Hide(); DrainUi(); pageHost.Show(); DrainUi();
            Check(encoderQueries == 2, "revisiting a device page reuses its completed encoder list");

            var videoOptions = video.ItemsSource.Cast<object>().Select(x => (
                Label: x.GetType().GetProperty("Label")!.GetValue(x)!.ToString()!,
                Value: x.GetType().GetProperty("Value")!.GetValue(x)!.ToString()!)).ToArray();
            Check(videoOptions.Select(x => x.Value).SequenceEqual(["", "c2.custom.oem.avc", "c2.mtk.avc.encoder", "c2.qti.avc.encoder", "c2.android.avc.encoder"]),
                "hardware encoders sort ahead of software encoders while keeping exact codec IDs as values");

            foreach (var entry in L10n.SupportedLanguages)
            {
                L10n.Instance.SetLanguage(entry.Code);
                DrainUi();
                var localizedOptions = video.ItemsSource.Cast<object>().Select(x => (
                    Label: x.GetType().GetProperty("Label")!.GetValue(x)!.ToString()!,
                    Value: x.GetType().GetProperty("Value")!.GetValue(x)!.ToString()!)).ToArray();
                var qti = localizedOptions.Single(x => x.Value == "c2.qti.avc.encoder");
                var sw = localizedOptions.Single(x => x.Value == "c2.android.avc.encoder");
                var oem = localizedOptions.Single(x => x.Value == "c2.custom.oem.avc");
                Check(qti.Label.Contains(L10n.T("Phone.StreamVendorQualcomm")) && qti.Label.Contains(L10n.T("Phone.StreamEncoderHardware")) && qti.Label.Contains("c2.qti.avc.encoder")
                    && sw.Label.Contains(L10n.T("Phone.StreamVendorAndroid")) && sw.Label.Contains(L10n.T("Phone.StreamEncoderSoftware")) && sw.Label.Contains("c2.android.avc.encoder")
                    && oem.Label.Contains(L10n.T("Phone.StreamEncoderHardware")) && oem.Label.Contains("c2.custom.oem.avc")
                    && localizedOptions[0].Label == L10n.T("Phone.StreamAutomatic"),
                    entry.Code + " localizes encoder vendor, hardware/software type, and preserves codec IDs live");
                var decoderOptions = videoDecoder.Items.Cast<object>().Select(x => x.GetType().GetProperty("Label")!.GetValue(x)!.ToString()!).ToArray();
                var audioDecoderOptions = audioDecoder.Items.Cast<object>().Select(x => x.ToString()!).ToArray();
                var latencyOptions = audioLatency.Items.Cast<object>().Select(x => (
                    Label: x.GetType().GetProperty("Label")!.GetValue(x)!.ToString()!,
                    Value: x.GetType().GetProperty("Value")!.GetValue(x)!.ToString()!)).ToArray();
                Check(decoderOptions[0] == L10n.T("Phone.StreamDecoderAutomatic")
                    && decoderOptions[1] == string.Format(L10n.T("Phone.StreamDecoderAccelerated"), "Microsoft H.264 Video Decoder MFT / D3D11")
                    && decoderOptions[2] == string.Format(L10n.T("Phone.StreamDecoderSoftware"), "Microsoft H.264 Video Decoder MFT / CPU")
                    && audioDecoderOptions.Contains(string.Format(L10n.T("Phone.StreamDecoderManaged"), "Concentus Opus"))
                    && audioDecoderOptions.Contains(string.Format(L10n.T("Phone.StreamDecoderRawPcm"), "Raw PCM"))
                    && latencyOptions.SequenceEqual([
                        (L10n.T("Phone.StreamLatencyUltraLow"), AndroidStreamSettings.LatencyModeUltraLow),
                        (L10n.T("Phone.StreamLatencyBalanced"), AndroidStreamSettings.LatencyModeBalanced),
                        (L10n.T("Phone.StreamLatencyStable"), AndroidStreamSettings.LatencyModeStable)
                    ]),
                    entry.Code + " localizes available decoder modes, Raw PCM, and all three audio latency modes");
                foreach (var option in latencyOptions)
                {
                    audioLatency.SelectedValue = option.Value;
                    pageHost.UpdateLayout();
                    var label = Descendants<TextBlock>(audioLatency).Single(text => text.Text == option.Label);
                    var untrimmed = new TextBlock
                    {
                        Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize,
                        FontStyle = label.FontStyle, FontWeight = label.FontWeight, FontStretch = label.FontStretch,
                        Language = label.Language, FlowDirection = label.FlowDirection
                    };
                    untrimmed.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var right = audioLatency.TranslatePoint(new Point(audioLatency.ActualWidth, 0), pageHost).X;
                    Check(label.ActualWidth + 0.5 >= untrimmed.DesiredSize.Width && right <= pageHost.ActualWidth
                        && audio.ActualWidth >= 72 && audioDecoder.ActualWidth >= 72,
                        $"{entry.Code} displays '{option.Label}' in full at 520 DIP " +
                        $"({label.ActualWidth:F1} available, {untrimmed.DesiredSize.Width:F1} needed)");
                }
            }
            L10n.Instance.SetLanguage("zh-CN");
            DrainUi();

            video.SelectedValue = "c2.qti.avc.encoder";
            bitrate.SelectedValue = "50000000";
            fpsCombo.SelectedValue = "120";
            audioDecoder.SelectedValue = AndroidStreamSettings.ConcentusDecoder;
            videoDecoder.SelectedValue = AndroidStreamSettings.SoftwareVideoDecoder;
            Pump(settingsRead.Result.FlushAsync());
            Check(settingsRead.Result.GetStreaming(device) is { VideoEncoder: "c2.qti.avc.encoder", VideoBitRate: 50000000, MaxFps: 120, AudioBitRate: 128000,
                AudioDecoder: AndroidStreamSettings.ConcentusDecoder, VideoDecoder: AndroidStreamSettings.SoftwareVideoDecoder },
                "streaming edits persist exact encoder and decoder IDs, 50 Mbps, and 120 fps together");
            Check((videoDecoder.ToolTip as string)?.Contains("Microsoft H.264 Video Decoder MFT / CPU") == true
                && videoDecoder.SelectionBoxItem.ToString()!.Contains("软件解码"),
                "decoder selection and tooltip show translated mode and complete original name");
            Check((video.ToolTip as string)?.Contains("c2.qti.avc.encoder") == true && (video.ToolTip as string)?.Contains("高通") == true,
                "encoder combo tooltip shows full localized description");
            if (windowsHevc)
            {
                videoDecoder.SelectedValue = AndroidStreamSettings.WindowsHevcDecoder;
                Check(videoDecoder.SelectedItem.ToString()!.StartsWith("H.265 (HEVC)") && videoDecoder.SelectedItem.ToString()!.Contains("Microsoft HEVC Video Decoder MFT")
                    && video.Items.Count == 4 && (string)video.SelectedValue == "", "available Android H.265 uses its own translated decoder and encoder list");
                Check(video.Items.Cast<object>().Any(x => x.ToString()!.Contains(L10n.T("Phone.StreamEncoderHevcCq")) && x.ToString()!.Contains("c2.qti.hevc.encoder.cq"))
                    && video.Items.Cast<object>().Any(x => x.ToString()!.Contains(L10n.T("Phone.StreamEncoderHevcHdr")) && x.ToString()!.Contains("c2.qti.hevc.encoder.hdr")),
                    "HEVC encoder variants have distinct translated labels and unchanged codec IDs");
                video.SelectedValue = "c2.qti.hevc.encoder";
                Pump(settingsRead.Result.FlushAsync());
                Check(settingsRead.Result.GetStreaming(device) is { VideoDecoder: AndroidStreamSettings.WindowsHevcDecoder,
                    VideoEncoder: "c2.qti.avc.encoder", HevcEncoder: "c2.qti.hevc.encoder" }, "Android stores H.264 and H.265 encoder selections separately");
                videoDecoder.SelectedValue = AndroidStreamSettings.SoftwareVideoDecoder;
                Check((string)video.SelectedValue == "c2.qti.avc.encoder", "switching back to H.264 restores its previous encoder");
                videoDecoder.SelectedValue = AndroidStreamSettings.WindowsHevcDecoder;
                Check((string)video.SelectedValue == "c2.qti.hevc.encoder", "switching back to H.265 restores its previous encoder");
                videoDecoder.SelectedValue = AndroidStreamSettings.SoftwareVideoDecoder;
            }
            else Check(videoDecoder.Items[3].ToString() == L10n.T("Phone.StreamDecoderHevcWindowsUnavailable"), "the unsupported Windows decoder explains why it is disabled");
            Check((string)typeof(PhoneDevicePage).GetMethod("FormatEncoderLabel", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, ["c2.qti.avc.encoder", AndroidEncoderKind.Unknown])! is string offlineQti
                && offlineQti.Contains("高通") && offlineQti.Contains("硬件编码"),
                "saved Qualcomm encoder renders friendly label even offline before querying phone");

            bitrate.SelectedValue = "32000000";
            Pump(settingsRead.Result.FlushAsync());
            Check(settingsRead.Result.GetStreaming(device).VideoBitRate == 32000000, "32 Mbps persists cleanly");
            bitrate.SelectedValue = "50000000";
            Pump(settingsRead.Result.FlushAsync());

            var audioBitRateCombo = (ComboBox)page.FindName("AudioBitRateCombo");
            audio.SelectedValue = "c2.android.opus.encoder";
            audioBitRateCombo.SelectedValue = "192000";
            audioLatency.SelectedValue = AndroidStreamSettings.LatencyModeUltraLow;
            Pump(settingsRead.Result.FlushAsync());
            Check(settingsRead.Result.GetStreaming(device).AudioLatencyMode == AndroidStreamSettings.LatencyModeUltraLow
                && audio.IsEnabled && audioBitRateCombo.IsEnabled,
                "ultra-low latency mode persists cleanly while Opus encoder and bitrate controls remain enabled");
            audioLatency.SelectedValue = AndroidStreamSettings.LatencyModeStable;
            audioDecoder.SelectedValue = AndroidStreamSettings.RawPcmDecoder;
            Pump(settingsRead.Result.FlushAsync());
            Check(!audio.IsEnabled && !audioBitRateCombo.IsEnabled
                && settingsRead.Result.GetStreaming(device) is
                {
                    AudioDecoder: AndroidStreamSettings.RawPcmDecoder,
                    AudioLatencyMode: AndroidStreamSettings.LatencyModeStable,
                    AudioEncoder: "c2.android.opus.encoder",
                    AudioBitRate: 192000
                },
                "selecting Raw PCM disables Opus encoder and bitrate controls while preserving saved Opus settings and stable latency");
            audioDecoder.SelectedValue = AndroidStreamSettings.ConcentusDecoder;
            Pump(settingsRead.Result.FlushAsync());
            Check(audio.IsEnabled && audioBitRateCombo.IsEnabled
                && (string)audio.SelectedValue == "c2.android.opus.encoder"
                && (string)audioBitRateCombo.SelectedValue == "192000"
                && settingsRead.Result.GetStreaming(device) is
                {
                    AudioDecoder: AndroidStreamSettings.ConcentusDecoder,
                    AudioLatencyMode: AndroidStreamSettings.LatencyModeStable,
                    AudioEncoder: "c2.android.opus.encoder",
                    AudioBitRate: 192000
                },
                "switching back to Concentus Opus re-enables encoder and bitrate selectors with saved values intact");

            foreach (string mode in new[] { "light", "dark" })
            {
                ThemeService.Apply(mode, "#00CBE9");
                DrainUi();
                Render(canvas, 520, 640, Path.Combine(output, "phone-settings-" + mode + ".png"));
                var card = (Border)page.FindName("StreamingCard");
                Point Position(FrameworkElement control) => control.TranslatePoint(new Point(0, 0), card);
                var fps = Position(fpsCombo);
                var latency = Position(audioLatency);
                var videoCodec = Position(videoDecoder);
                var audioCodec = Position(audioDecoder);
                Check(((Grid)card.Child).RowDefinitions.Count(row => row.ActualHeight > 0) == 2
                    && fps.X + fpsCombo.ActualWidth <= card.ActualWidth - 8
                    && latency.X + audioLatency.ActualWidth <= card.ActualWidth - 8
                    && audio.ActualWidth >= 72 && videoDecoder.ActualWidth >= 72 && fpsCombo.ActualWidth >= 70
                    && audioLatency.ActualWidth >= 70 && Math.Abs(latency.X - fps.X) <= 1
                    && Math.Abs(latency.Y - Position(audio).Y) <= 1 && fps.Y >= latency.Y + audioLatency.ActualHeight
                    && Math.Abs(videoCodec.X - audioCodec.X) <= 1 && Math.Abs(videoCodec.Y - Position(video).Y) <= 1,
                    mode + " encoders, decoders, audio latency, 50 Mbps and 120 fps fit two rows cleanly in 520 DIP");
                var apps = (ItemsControl)page.FindName("RetainedApps");
                var title = Descendants<TextBlock>(page).First(x => x.Text == L10n.T("Phone.QuickPanelAppsTitle"));
                Border? ContainingCard(DependencyObject item)
                {
                    for (var current = VisualTreeHelper.GetParent(item); current != null; current = VisualTreeHelper.GetParent(current))
                        if (current is Border border && border.CornerRadius.TopLeft == 12) return border;
                    return null;
                }
                Check(ContainingCard(title) != null && ReferenceEquals(ContainingCard(title), ContainingCard(apps)),
                    mode + " quick-panel title description and apps share the extended card background");
            }
            Call(panel, "CreateOutputPopupContent");
            Call(panel, "BuildOutputVolumeRows", new List<AudioDeviceInfo>
            { new() { Id = "nonexistent-fixture", DisplayName = "耳机", Flow = EDataFlow.eRender } }, "nonexistent-fixture");
            var rows = (IDictionary)Get(panel, "_outputVolumeRows");
            var row = rows["nonexistent-fixture"]!;
            var slider = (Slider)Get(row, "Slider"); var percent = (TextBlock)Get(row, "Percent");
            slider.Value = 18; slider.Value = 0;
            Check(percent.Text == "—" && ((IDictionary)Get(panel, "_pendingOutputVolume")).Count == 0,
                "unread disabled sliders cannot display or enqueue a false zero-percent volume");
            Call(panel, "ApplyOutputVolumeState", row, 37, false);
            Check(percent.Text == "37%" && slider.Value == 37 && slider.IsEnabled
                && ((IDictionary)Get(panel, "_pendingOutputVolume")).Count == 0,
                "first native read displays its actual volume with no initialization writes");
            Call(panel, "ApplyOutputVolumeState", row, 0, false);
            Check(percent.Text == "0%" && slider.IsEnabled, "a real muted-to-zero reading remains visible as zero");
            Call(panel, "StopOutputVolumes");
            Check(((Popup)panel.FindName("OutputVolumesPopup")).Child == null && Get(panel, "_outputEntranceMotion") == null,
                "collapsing the extra panel releases its controls and entrance animation");
            phone.Show(); DrainUi(); phone.Hide();
            var surface = (Border)phone.FindName("PhoneSurface");
            Check(surface.Opacity == 1 && ((TranslateTransform)surface.RenderTransform).Y == 0,
                "hiding the phone panel stops and resets its entrance animation");
            Check(scans == 0, "opening settings and panels starts no device scan");
            Check(encoderQueries == 2, "language changes, saving, theme changes and dropdowns do not requery encoders");
            if (windowsHevc)
            {
                typeof(PhoneDevicePage).GetField("_streamEncoders", Hidden)!.SetValue(page, simulatedCatalog with { Hevc = [] });
                Call(page, "RefreshEncoderOptions");
                var unsupported = videoDecoder.Items.Cast<object>().Single(x => x.ToString() == L10n.T("Phone.StreamDecoderHevcAndroidUnavailable"));
                videoDecoder.SelectedItem = unsupported;
                Check(!(bool)unsupported.GetType().GetProperty("IsEnabled")!.GetValue(unsupported)!
                    && (string)videoDecoder.SelectedValue == AndroidStreamSettings.SoftwareVideoDecoder
                    && settingsRead.Result.GetStreaming(device).VideoDecoder == AndroidStreamSettings.SoftwareVideoDecoder,
                    "confirmed lack of a phone HEVC encoder disables the option and cannot save an invalid new selection");
            }
            Call(page, "Suspend");
            Check(Get(page, "_streamOperation") == null, "leaving settings cancels work used only by the streaming UI");
            File.WriteAllText(Path.Combine(output, "panel-stream-settings-summary.json"), JsonSerializer.Serialize(new
            { Assertions = _checks, RealAdb = false, ProductStarted = false, EncoderQueries = encoderQueries,
                Scope = "visible-only encoder preload, cancellation and late results, encoder/decoder labels across 9 languages, persistence and narrow two-row layout" },
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { pageHost.Content = null; canvas.Children.Clear(); pageHost.Close(); phone.Close(); panel.Close(); }
    }
}

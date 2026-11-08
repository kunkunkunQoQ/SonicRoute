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
        discovery.Publish([device]);
        var phoneStore = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        phoneStore.RememberDevice(device); phoneStore.Rename("小米");
        phoneStore.Retain(new AndroidApp("cn.test.music", "波点音乐", 1001, "media"));
        object Get(object obj, string name) => obj.GetType().GetField(name, Hidden)!.GetValue(obj)!;
        object? Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Hidden)!.Invoke(obj, args);
        using var page = new PhoneDevicePage();
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
            pageHost.Show();
            var settingsRead = (Task<AndroidAudioSettingsStore>)host.GetMethod("PrepareStreamingAsync", statics)!.Invoke(null, [CancellationToken.None])!;
            Pump(settingsRead); DrainUi();
            var bitrate = (ComboBox)page.FindName("VideoBitRateCombo");
            var fpsCombo = (ComboBox)page.FindName("VideoFpsCombo");
            var audio = (ComboBox)page.FindName("AudioEncoderCombo");
            var video = (ComboBox)page.FindName("VideoEncoderCombo");
            Check((string)bitrate.SelectedValue == "4000000" && (string)audio.SelectedValue == "" && Get(page, "_streamEncoders") == null,
                "streaming controls load current defaults without querying device encoders");
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
                    ["c2.custom.oem.avc"] = AndroidEncoderKind.Hardware
                });
            typeof(PhoneDevicePage).GetField("_streamEncoders", Hidden)!.SetValue(page, simulatedCatalog);
            Call(page, "ApplyStreamingSettings");
            DrainUi();

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
            }
            L10n.Instance.SetLanguage("zh-CN");
            DrainUi();

            video.SelectedValue = "c2.qti.avc.encoder";
            bitrate.SelectedValue = "50000000";
            fpsCombo.SelectedValue = "120";
            Pump(settingsRead.Result.FlushAsync());
            Check(settingsRead.Result.GetStreaming(device) is { VideoEncoder: "c2.qti.avc.encoder", VideoBitRate: 50000000, MaxFps: 120, AudioBitRate: 128000 },
                "streaming edits save friendly-labeled encoder ID, 50 Mbps, and 120 fps");
            Check((video.ToolTip as string)?.Contains("c2.qti.avc.encoder") == true && (video.ToolTip as string)?.Contains("高通") == true,
                "encoder combo tooltip shows full localized description");
            Check((string)typeof(PhoneDevicePage).GetMethod("FormatEncoderLabel", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, ["c2.qti.avc.encoder", AndroidEncoderKind.Unknown])! is string offlineQti
                && offlineQti.Contains("高通") && offlineQti.Contains("硬件编码"),
                "saved Qualcomm encoder renders friendly label even offline before querying phone");

            bitrate.SelectedValue = "32000000";
            Pump(settingsRead.Result.FlushAsync());
            Check(settingsRead.Result.GetStreaming(device).VideoBitRate == 32000000, "32 Mbps persists cleanly");
            bitrate.SelectedValue = "50000000";
            Pump(settingsRead.Result.FlushAsync());

            foreach (string mode in new[] { "light", "dark" })
            {
                ThemeService.Apply(mode, "#00CBE9");
                DrainUi();
                Render(canvas, 520, 640, Path.Combine(output, "phone-settings-" + mode + ".png"));
                var card = (Border)page.FindName("StreamingCard");
                var right = fpsCombo.TranslatePoint(new Point(fpsCombo.ActualWidth, 0), card).X;
                Check(((Grid)card.Child).RowDefinitions.Count == 2 && right <= card.ActualWidth - 8
                    && audio.ActualWidth >= 80 && fpsCombo.ActualWidth >= 80,
                    mode + " streaming options including 50 Mbps and 120 fps fit two rows cleanly in 520 DIP");
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
            Call(page, "Suspend");
            Check(Get(page, "_streamOperation") == null, "leaving settings cancels work used only by the streaming UI");
            File.WriteAllText(Path.Combine(output, "panel-stream-settings-summary.json"), JsonSerializer.Serialize(new
            { Assertions = _checks, RealAdb = false, ProductStarted = false, Scope = "encoder friendly labels across 9 languages, 24/32/50 Mbps, 120 fps, narrow layout, card backgrounds, initialization volume and animation lifecycle" },
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { pageHost.Content = null; canvas.Children.Clear(); pageHost.Close(); phone.Close(); panel.Close(); }
    }
}

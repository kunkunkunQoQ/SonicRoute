using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SonicRoute;
using SonicRoute.AndroidAudio;

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
        object? Receiver() => airPlay.GetField("_receiver", statics)!.GetValue(null);
        try
        {
            var selector = (ComboBox)panel.FindName("DeviceCombo");
            object choice = selector.Items.Cast<object>().Single(item => item is not AndroidDevice);
            var view = (AirPlayReceiverView)panel.FindName("AirPlayReceiverPane");
            var list = (ListBox)panel.FindName("PhoneApps");
            Check(panel.Title == "设备面板" && selector.Items.Count == 2 && selector.SelectedItem is AndroidDevice,
                "Device Panel has the saved Android device and AirPlay together in its dropdown");
            Check(!view.IsVisible && panel.FindName("AirPlayButton") == null && list.Items.Count == 2,
                "Android shows its original app pane and no separate AirPlay footer button");
            selector.SelectedItem = choice;
            DrainUi();
            var toggle = (Button)view.FindName("AirPlayToggle");
            var codecs = (ComboBox)view.FindName("MirrorDecoder");
            Check(view.IsVisible && !list.IsVisible && !((FrameworkElement)panel.FindName("DeviceMediaCard")).IsVisible
                && !((FrameworkElement)panel.FindName("RefreshButton")).IsVisible && !panel.AllowDrop && Receiver() == null,
                "AirPlay selection shows its own idle pane and stops Android-only UI");
            Check(toggle.IsEnabled && Equals(toggle.Content, L10n.T("Phone.AirPlayStart")), "AirPlay waits for the explicit receive button");
            bool hevc = MediaFoundationH264Decoder.SupportsHevc;
            if (hevc) Until(() => codecs.Items.Count == 3);
            Check(codecs.SelectedIndex == 0 && codecs.Items.Count == (hevc ? 3 : 2),
                "Automatic is selected first and HEVC is shown only when Windows has a decoder");
            foreach (var language in L10n.SupportedLanguages)
            {
                L10n.Instance.SetLanguage(language.Code); DrainUi();
                Check(panel.Title == L10n.T("Phone.AudioWindowTitle") && !panel.Title.StartsWith("Phone.")
                    && codecs.Items[0].Equals(L10n.T("Phone.StreamDecoderAutomatic"))
                    && (!hevc || codecs.Items[2].Equals(L10n.T("Phone.AirPlayHevcOption"))),
                    language.Code + " localizes the panel title and AirPlay decoder choices");
            }
            L10n.Instance.SetLanguage("zh-CN");
            int port = 0;
            if (testLan)
            {
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(() => !toggle.IsEnabled || Equals(toggle.Content, L10n.T("Phone.StopReceiving")));
                Until(() => Equals(toggle.Content, L10n.T("Phone.StopReceiving")) && toggle.IsEnabled);
                object receiver = Receiver()!;
                port = (int)receiver.GetType().GetProperty("Port", Hidden)!.GetValue(receiver)!;
                Check(port > 0 && !codecs.IsEnabled && IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port == port),
                    "the actual Windows LAN receiver starts and keeps the decoder fixed during reception");
            }
            Render((FrameworkElement)panel.Content, 235, 450, Path.Combine(output, "device-panel-airplay.png"));
            selector.SelectedItem = selector.Items.OfType<AndroidDevice>().Single();
            DrainUi();
            Check(list.IsVisible && list.Items.Count == 2 && !view.IsVisible && panel.AllowDrop && store.Profile.LastDevice != null,
                "switching back restores Android apps and their selected device");
            selector.SelectedItem = choice;
            panel.Close();
            panel = OpenPanel();
            view = (AirPlayReceiverView)panel.FindName("AirPlayReceiverPane");
            Check(view.IsVisible && store.Profile.LastDevice != null, "reopening remembers AirPlay separately from the Android profile");
            if (testLan)
            {
                toggle = (Button)view.FindName("AirPlayToggle");
                Check(Receiver() != null && Equals(toggle.Content, L10n.T("Phone.StopReceiving")),
                    "switching devices and closing the panel leave explicit AirPlay reception running");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(() => Equals(toggle.Content, L10n.T("Phone.AirPlayStart")) && toggle.IsEnabled);
                Check(Receiver() == null && !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port == port),
                    "Stop receiving closes the real TCP listener");
            }
            Check(scans == 0 && !(bool)android.GetProperty("IsAdbCreated", statics)!.GetValue(null)!,
                "AirPlay and the saved Android view need no device scan or ADB instance");
            File.WriteAllText(Path.Combine(output, "device-panel-airplay.json"), JsonSerializer.Serialize(new
            { Assertions = _checks, LanStartStop = testLan, Hevc = hevc, AndroidScans = scans, RealApple = false }));
            return 0;
        }
        finally
        {
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
}

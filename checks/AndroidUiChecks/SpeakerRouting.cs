using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static int SpeakerRoutingUi(string output)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        int scans = 0;
        var discovery = new AndroidDeviceDiscovery(_ =>
        {
            scans++;
            return Task.FromResult<IReadOnlyList<AndroidDevice>>(Array.Empty<AndroidDevice>());
        });
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        var device = new AndroidDevice("fixture-one", "2211133C", "device", true, "speaker-one");
        var other = new AndroidDevice("fixture-two", "Other model", "device", true, "speaker-two");
        discovery.Publish([device, other]);
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        store.RememberDevice(device); store.Rename("小米13");
        string profile = store.Profile.Id;
        store.RememberDevice(other); store.Rename("备用手机"); store.SelectProfile(profile);
        var bluetooth = new AndroidOutputSink("bt:8:test", AndroidOutputSinkKind.Bluetooth, 3, 8, "耳机");
        store.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker, bluetooth]);
        var readOutputs = host.GetMethod("GetOutputDevices", statics)!.CreateDelegate<Func<IReadOnlyList<AudioDeviceInfo>>>();
        string speakerId = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "speaker");
        string otherId = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(other), "speaker");
        var initial = readOutputs();
        Check(initial.Single(d => d.Id == speakerId).DisplayName == "小米13 扬声器"
            && initial.Single(d => d.Id == otherId).DisplayName == "备用手机 扬声器",
            "speaker labels use each saved phone name and retain independent identities");
        Check(ReferenceEquals(initial, readOutputs()), "unchanged output choices reuse the cached snapshot");
        store.Rename("我的小米13");
        Check(!ReferenceEquals(initial, readOutputs()) && readOutputs().Single(d => d.Id == speakerId).DisplayName == "我的小米13 扬声器",
            "renaming refreshes the label without changing its endpoint identity");

        var manager = (AndroidAudioManager)host.GetProperty("Sources", statics)!.GetValue(null)!;
        var music = new AndroidApp("cn.test.music", "Music", 1001, "media");
        var first = AddFixture(manager, device, music);
        var second = AddFixture(manager, device, music with { Package = "cn.test.video", Name = "Video", Uid = 1002 });
        var neighbour = AddFixture(manager, other, music);
        var config = ConfigService.Load(); config.QuickPanelShowPhoneAudio = false;
        var windows = new List<AudioDeviceInfo>
        {
            new() { Id = AudioService.SystemDefaultDeviceId, DisplayName = "系统默认播放", Flow = EDataFlow.eRender },
            new() { Id = "pc-headphones", DisplayName = "电脑耳机", Flow = EDataFlow.eRender }
        };
        var panel = new QuickPanelModernWindow();
        var main = new MainWindow();
        var phone = new PhoneAudioWindow(false);
        try
        {
            typeof(QuickPanelModernWindow).GetField("_outputDisplay", Hidden)!.SetValue(panel, windows.Concat(readOutputs()).ToList());
            typeof(QuickPanelModernWindow).GetMethod("RefreshAndroidRow", Hidden)!.Invoke(panel, [false]);
            var root = (FrameworkElement)((ItemsControl)panel.FindName("AppListPanel")).Items[0];
            Descendants<ToggleButton>(root).Single().IsChecked = true;
            var pills = Descendants<Button>(root).Where(b => b.Tag is AudioDeviceInfo).ToArray();
            Check(pills.Length == 3 && pills.Count(b => ((AudioDeviceInfo)b.Tag).Id == speakerId) == 1
                && pills.All(b => !AndroidOutputIdentity.IsPhoneOutput(((AudioDeviceInfo)b.Tag).Id) || ((AudioDeviceInfo)b.Tag).Id == speakerId),
                "incoming quick-panel choices include PC endpoints and only this source phone's speaker");
            Check(pills.Single(b => ((AudioDeviceInfo)b.Tag).Id == speakerId).Content?.ToString() == "我的小米13 扬声器",
                "phone speaker reuses a normal endpoint pill with the saved device label");
            store.Rename("小米13"); DrainUi();
            pills = Descendants<Button>(root).Where(b => b.Tag is AudioDeviceInfo).ToArray();
            Check(pills.Single(b => ((AudioDeviceInfo)b.Tag).Id == speakerId).Content?.ToString() == "小米13 扬声器",
                "renaming also refreshes an already-expanded incoming route group");

            typeof(PhoneAudioWindow).GetMethod("EnsurePcDropZones", Hidden)!.Invoke(phone, [true]);
            Check(Descendants<TextBlock>((Grid)phone.FindName("PcDropZonesGrid")).Any(t => t.Text == "仅在小米13 扬声器播放"),
                "phone drop feedback uses the same friendly speaker name");

            typeof(MainWindow).GetField("_outputs", Hidden)!.SetValue(main, windows.Concat(readOutputs()).ToList());
            typeof(MainWindow).GetMethod("RefreshDeviceDisplays", Hidden)!.Invoke(main, null);
            var selected = ExternalApps().Single(a => a.ExternalSourceId == second.Id);
            var items = new List<AppItem> { AppItem.From(selected) };
            typeof(MainWindow).GetField("_appItems", Hidden)!.SetValue(main, items);
            var apps = (ListBox)main.FindName("AppsListBox"); apps.ItemsSource = items; apps.SelectedIndex = 0;
            Pump((Task)typeof(MainWindow).GetMethod("RefreshAppsSelectionAsync", Hidden)!.Invoke(main, null)!);
            var combo = (ComboBox)main.FindName("AppsOutputCombo");
            Check(combo.Items.OfType<AudioDeviceInfo>().Count(d => AndroidOutputIdentity.IsPhoneOutput(d.Id)) == 1
                && combo.Items.OfType<AudioDeviceInfo>().Single(d => d.Id == speakerId).DisplayName == "小米13 扬声器",
                "full app details expose the same source phone speaker without foreign phone routes");
            Check(((ItemsControl)main.FindName("AppsOutputQuickPanel")).Items.OfType<Button>()
                .Any(b => b.Tag is AudioDeviceInfo d && d.Id == speakerId),
                "full app quick device buttons include the source phone speaker");

            var apply = UnifiedAudio.GetMethod("ApplyEndpointAsync", statics)!;
            var app = ExternalApps().Single(a => a.ExternalSourceId == first.Id);
            var rejected = (Task<(bool Success, int HResult, string Message)>)apply.Invoke(null, [app, EDataFlow.eRender, otherId])!;
            Pump(rejected);
            Check(!rejected.Result.Success && manager.Sources.Count == 3,
                "a foreign phone endpoint is rejected without retiring or rerouting a stream");
            pills.Single(b => ((AudioDeviceInfo)b.Tag).Id == speakerId).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForRetirement(first);
            Check(!manager.Sources.Contains(first) && manager.Sources.Contains(second) && manager.Sources.Contains(neighbour),
                "the real quick-panel speaker handler returns only its selected source to the phone");
            combo.SelectedItem = combo.Items.OfType<AudioDeviceInfo>().Single(d => d.Id == speakerId);
            WaitForRetirement(second);
            Check(!manager.Sources.Contains(second) && manager.Sources.Contains(neighbour),
                "full app speaker selection completes through the worker/dispatcher path without affecting another phone");
            Check(scans == 0 && !(bool)host.GetProperty("IsPcToPhoneCreated", statics)!.GetValue(null)!
                && !(bool)host.GetProperty("AreDisplaysCreated", statics)!.GetValue(null)!,
                "speaker routing checks create no scan, PC-to-phone loop or virtual-display worker");
            Pump(store.FlushAsync());
            File.WriteAllText(Path.Combine(output, "speaker-routing.json"), JsonSerializer.Serialize(new
            { Passed = true, Assertions = _checks, Scans = scans, RealPhoneAccess = false,
                Scope = "Fixture sessions; actual quick/full controls and dispatcher retirement. No real phone or audible acceptance." },
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { Pump(manager.StopAllAsync()); phone.Close(); panel.Close(); main.Close(); }

        void WaitForRetirement(AndroidAudioSource source)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (manager.Sources.Contains(source) && DateTime.UtcNow < deadline) Pump(Task.Delay(10));
            DrainUi();
        }
    }
}

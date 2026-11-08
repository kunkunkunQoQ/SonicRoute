using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

internal static partial class Program
{
    private static int DeviceAudioUi(string output)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        int scans = 0;
        var discovery = new AndroidDeviceDiscovery(_ =>
        { scans++; throw new InvalidOperationException("No device access in the device-audio UI fixture"); });
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        var device = new AndroidDevice("offline-fixture", "Fixture", "device", false, "device-audio-fixture");
        discovery.Publish([device]);
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        store.RememberDevice(device);
        store.Rename("小米");
        var music = new AndroidApp("cn.test.music", "Music", 1001, "media");
        store.Retain(music);
        var phone = new PhoneAudioWindow(false);
        QuickPanelModernWindow? panel = null;
        try
        {
            var list = (ListBox)phone.FindName("PhoneApps");
            var search = (TextBox)phone.FindName("SearchBox");
            Check(list.Items.Count == 0, "no device means no unbound whole-phone action");
            typeof(PhoneAudioWindow).GetMethod("BindDevices", Hidden)!.Invoke(phone, [new[] { device }, device.Serial]);
            typeof(PhoneAudioWindow).GetMethod("RetainedAppsChanged", Hidden)!.Invoke(phone, null);
            Check(list.Items.Count == 2 && list.Items[0] is PhoneAppItem { App.IsDeviceAudio: true, Name: "小米" },
                "device action appears first in the same app list and uses its existing item template");
            var wholeItem = (PhoneAppItem)list.Items[0];
            Check(wholeItem.Icon is DrawingImage { IsFrozen: true, Drawing: GeometryDrawing phoneDrawing }
                && ReferenceEquals(phoneDrawing.Geometry, AutomationIcons.Phone)
                && ReferenceEquals(wholeItem.Icon, new PhoneAppItem(AndroidApp.DeviceAudio).Icon),
                "device card and fresh drag preview share one frozen phone geometry without rasterization");
            var sourceIconItem = AppItem.From(new SonicRoute.Core.Models.AudioAppInfo
            { ProcessId = 0, ExternalSourceId = "android:icon-fixture", ProcessName = "android:" + AndroidApp.DeviceAudioPackage });
            Pump(Task.Run(sourceIconItem.LoadIcon)); DrainUi();
            Check(ReferenceEquals(sourceIconItem.Icon, wholeItem.Icon),
                "background-loaded whole-phone icons use the same cached vector as the phone list and drag preview");
            var state = AndroidMediaState.Parse("{\"package\":\"cn.test.music\",\"title\":\"Track\",\"artist\":\"Artist\",\"actions\":50,\"playing\":true,\"playingPackages\":[\"cn.test.music\"]}");
            typeof(PhoneAudioWindow).GetMethod("ApplyMediaState", Hidden)!.Invoke(phone, [state]);
            Check(ReferenceEquals(list.Items[0], wholeItem) && wholeItem.IsPlaying && ((PhoneAppItem)list.Items[1]).IsPlaying,
                "playing-app promotion cannot move an app above the whole-phone action");
            search.Text = "no matching application";
            Check(list.Items.Count == 1 && ReferenceEquals(list.Items[0], wholeItem),
                "app search leaves the top device action accessible");
            search.Text = "";
            foreach (string theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme, "#00CBE9");
                Render((FrameworkElement)phone.Content, 235, 450, Path.Combine(output, "device-audio-" + theme + ".png"));
                var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var second = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
                Check(first != null && second != null && ReferenceEquals(first.Style, second.Style)
                    && first.ActualHeight == second.ActualHeight && first.ActualHeight > 0,
                    "whole-phone and application cards share style and height in " + theme);
                Check(Descendants<Border>(first!).Single(b => b.Name == "AppIconAccent").Visibility == Visibility.Collapsed
                    && Descendants<TextBlock>(first!).Single(t => t.Name == "AppIconInitial").Visibility == Visibility.Collapsed
                    && Descendants<Border>(second!).Single(b => b.Name == "AppIconAccent").Visibility == Visibility.Visible,
                    "device uses a bare phone outline while ordinary application badges keep their style in " + theme);
            }
            Check(scans == 0 && !host.GetProperty("IsAdbCreated", statics)!.GetValue(null)!.Equals(true),
                "listing filtering and rendering the inactive device action create no ADB client or scan");
            var manager = (AndroidAudioManager)host.GetProperty("Sources", statics)!.GetValue(null)!;
            var source = AddFixture(manager, device, AndroidApp.DeviceAudio);
            panel = new QuickPanelModernWindow();
            foreach (string language in new[] { "zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU" })
            {
                L10n.Instance.SetLanguage(language); DrainUi();
                Check(wholeItem.Name == "小米"
                    && wholeItem.Description == L10n.T("Phone.DeviceAudioDescription")
                    && !wholeItem.Name.StartsWith("Phone.") && !wholeItem.Description.StartsWith("Phone."),
                    "device entry preserves the custom phone name and translates its description in " + language);
                var rows = (System.Collections.IDictionary)typeof(QuickPanelModernWindow).GetField("_androidRows", Hidden)!.GetValue(panel)!;
                var row = rows[source.Id]!;
                var iconItem = (AppItem)row.GetType().GetField("IconItem", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(row)!;
                Check(iconItem.Label == wholeItem.Name, "active source label follows the device entry in " + language);
            }
            L10n.Instance.SetLanguage("zh-CN");
            store.Rename("我的手机");
            typeof(PhoneAudioWindow).GetMethod("DeviceNamesChanged", Hidden)!.Invoke(phone, null);
            DrainUi();
            Check(wholeItem.Name == "我的手机" && ReferenceEquals(list.Items[0], wholeItem),
                "device rename refreshes the cached top entry without rebuilding the app list");
            var renamedRows = (System.Collections.IDictionary)typeof(QuickPanelModernWindow).GetField("_androidRows", Hidden)!.GetValue(panel)!;
            var renamedRow = renamedRows[source.Id]!;
            var renamedInfo = (AppItem)renamedRow.GetType().GetField("IconItem", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renamedRow)!;
            Check(renamedInfo.Label == "我的手机", "active whole-phone source also follows a custom phone rename");
            store.Remove(music.Package);
            typeof(PhoneAudioWindow).GetMethod("RetainedAppsChanged", Hidden)!.Invoke(phone, null);
            Check(list.Items.Count == 1 && ReferenceEquals(list.Items[0], wholeItem)
                && ((StackPanel)phone.FindName("PhoneEmptyPanel")).Visibility == Visibility.Collapsed,
                "a device with no pinned apps still exposes the whole-phone action without an empty overlay");
            var payload = new AndroidAppDrag(device, wholeItem.App);
            Pump((Task)typeof(App).GetMethod("ReturnAndroidToPhoneAsync", Hidden)!.Invoke(Application.Current, [payload])!);
            Check(manager.FindSource(source.Id) == null, "existing return operation releases the device source by its own identity");
            var other = device with { Serial = "offline-fixture-two", HardwareId = "device-audio-second" };
            store.RememberDevice(other); store.Rename("备用手机");
            typeof(PhoneAudioWindow).GetMethod("BindDevices", Hidden)!.Invoke(phone, [new[] { device, other }, other.Serial]);
            typeof(PhoneAudioWindow).GetMethod("RetainedAppsChanged", Hidden)!.Invoke(phone, null);
            Check(list.Items.Count == 1 && wholeItem.Name == "备用手机" && ReferenceEquals(list.Items[0], wholeItem),
                "switching phones reuses the top entry and reads the selected phone's own saved name");
            Check(scans == 0, "translating and returning a fixture source never starts device discovery");
            File.WriteAllText(Path.Combine(output, "device-audio-ui-summary.json"), JsonSerializer.Serialize(new
            { Assertions = _checks, RealAdb = false, ProductStarted = false, VisibleProductWindow = false,
                Scope = "shared app cards, ordering, search, light/dark renders, nine languages and return path" },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {_checks} device audio UI assertions; no phone access.");
            return 0;
        }
        finally
        {
            panel?.Close(); phone.Close(); L10n.Instance.SetLanguage("zh-CN"); ThemeService.Apply("light", "#00CBE9");
        }
    }
}

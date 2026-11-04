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
            CheckAndroidPeakMeter(panel, first, second);

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
                Scope = "Fixture sessions; actual quick/full controls, Android peak meter bar, and dispatcher retirement. No real phone or audible acceptance." },
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

    private static void CheckAndroidPeakMeter(QuickPanelModernWindow panel, AndroidAudioSource first, AndroidAudioSource second)
    {
        var content = (FrameworkElement)panel.Content;
        content.Measure(new Size(372, 450)); content.Arrange(new Rect(0, 0, 372, 450)); content.UpdateLayout();
        var rows = (System.Collections.IDictionary)typeof(QuickPanelModernWindow).GetField("_androidRows", Hidden)!.GetValue(panel)!;
        object firstRow = rows[first.Id]!;
        object secondRow = rows[second.Id]!;
        var rowType = firstRow.GetType();
        int firstMeterId = (int)rowType.GetField("MeterId", Hidden)!.GetValue(firstRow)!;
        int secondMeterId = (int)rowType.GetField("MeterId", Hidden)!.GetValue(secondRow)!;
        Check(firstMeterId < 0 && secondMeterId < 0 && firstMeterId != secondMeterId,
            "Android rows allocate distinct negative meter IDs that cannot collide with Windows PIDs");
        var slider = (Slider)rowType.GetField("Volume", Hidden)!.GetValue(firstRow)!;
        var peakVisual = (Border?)rowType.GetField("PeakVisual", Hidden)!.GetValue(firstRow);
        var peakTransform = (ScaleTransform?)rowType.GetField("PeakTransform", Hidden)!.GetValue(firstRow);
        Check(peakVisual != null && peakTransform != null
            && ReferenceEquals(peakVisual.Background, panel.FindResource("Theme.AccentPeak"))
            && peakVisual.Visibility == Visibility.Hidden && peakTransform.ScaleX == 0d && peakTransform.ScaleY == 1d,
            "Android row slider binds PART_PeakVisual with Theme.AccentPeak and an idle ScaleTransform");

        var pcm = new AndroidPcmSource();
        var pcmField = typeof(AndroidAudioSession).GetField("_pcm", Hidden)!;
        pcmField.SetValue(first.Session, pcm);
        int wakeups = 0;
        Action onPeakActivity = () => Interlocked.Increment(ref wakeups);
        AndroidPcmSource.PeakActivity += onPeakActivity;
        var createMeter = typeof(QuickPanelModernWindow).GetMethod("CreateAudioMeterService", Hidden)!;
        var meter = (AudioMeterService)createMeter.Invoke(panel, null)!;
        try
        {
            Check(pcm.ReadMeterPeak() == (false, 0f), "initial Android meter read returns idle and arms wakeup");
            pcm.Write(new float[960]);
            Check(wakeups == 0 && pcm.ReadMeterPeak() == (false, 0f), "silent PCM frames do not wake the meter service");
            var loud = new float[960]; Array.Fill(loud, 0.8f);
            pcm.Write(loud);
            pcm.Write(loud);
            Check(wakeups == 1, "consecutive active PCM writes coalesce to a single wakeup while waiting");
            var read = pcm.ReadMeterPeak();
            Check(read.Active && Math.Abs(read.Peak - 0.8f) < 0.001f && Math.Abs(pcm.LastPeak - 0.8f) < 0.001f,
                "Android meter peak preserves pre-volume signal level alongside raw LastPeak");

            IReadOnlyDictionary<int, float>? latestPeaks = null;
            using var sampled = new ManualResetEventSlim();
            meter.PeaksUpdated += snapshot => { latestPeaks = snapshot; sampled.Set(); };
            meter.UpdateVisibleTargets([firstMeterId]);
            meter.Start([firstMeterId, secondMeterId]);
            pcm.Write(loud);
            meter.WakeExternal();
            Check(sampled.Wait(2000), "AudioMeterService samples external Android sources on demand");
            Check(latestPeaks != null && latestPeaks.TryGetValue(firstMeterId, out float metered) && metered > 0.5f
                && !latestPeaks.ContainsKey(secondMeterId),
                "visible Android peak sampling updates only visible targets");

            typeof(QuickPanelModernWindow).GetField("_meterEnabled", Hidden)!.SetValue(panel, true);
            typeof(QuickPanelModernWindow).GetField("_displayPeaks", Hidden)!.SetValue(panel,
                new Dictionary<int, float> { [firstMeterId] = 0.8f, [secondMeterId] = 0.8f });
            typeof(QuickPanelModernWindow).GetMethod("RefreshMeterViewport", Hidden)!.Invoke(panel, null);
            float rowPeak = (float)rowType.GetField("PeakLevel", Hidden)!.GetValue(firstRow)!;
            Check(Math.Abs(rowPeak - 0.8f) < 0.001f && peakVisual!.Visibility == Visibility.Visible
                && Math.Abs(peakTransform!.ScaleX - 0.8d) < 0.02d,
                "visible Android row displays the real-time peak bar scaled to full volume");
            slider.Value = 50;
            Check(peakVisual.Visibility == Visibility.Visible && Math.Abs(peakTransform.ScaleX - 0.4d) < 0.02d,
                "changing the Android row volume immediately rescales PART_PeakVisual within the filled track");
            var targetsMethod = typeof(QuickPanelModernWindow).GetMethod("GetMeterTargets", Hidden)!;
            first.Session.SetMute(true); DrainUi();
            var activeTargets = ((IEnumerable<int>)targetsMethod.Invoke(panel, [false])!).ToArray();
            Check((bool)rowType.GetField("Muted", Hidden)!.GetValue(firstRow)!
                && (float)rowType.GetField("PeakLevel", Hidden)!.GetValue(firstRow)! == 0f
                && peakVisual.Visibility == Visibility.Hidden && peakTransform.ScaleX == 0d
                && !activeTargets.Contains(firstMeterId),
                "muting an Android row immediately hides its peak visual and removes it from active meter targets");
            first.Session.SetMute(false); DrainUi();
            activeTargets = ((IEnumerable<int>)targetsMethod.Invoke(panel, [false])!).ToArray();
            Check(!(bool)rowType.GetField("Muted", Hidden)!.GetValue(firstRow)! && activeTargets.Contains(firstMeterId),
                "unmuting an Android row restores its meter target eligibility");
            slider.Value = 100;
            panel.SetMeterEnabled(false);
            Check((float)rowType.GetField("PeakLevel", Hidden)!.GetValue(firstRow)! == 0f
                && peakVisual.Visibility == Visibility.Hidden && peakTransform.ScaleX == 0d,
                "disabling the meter clears Android row peaks and hides PART_PeakVisual");
        }
        finally
        {
            AndroidPcmSource.PeakActivity -= onPeakActivity;
            Pump(meter.DisposeAsync());
            pcmField.SetValue(first.Session, null);
        }
    }
}

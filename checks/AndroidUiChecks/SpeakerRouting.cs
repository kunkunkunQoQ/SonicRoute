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
        var music = new AndroidApp("cn.test.music", "Music", 1001, "media");
        store.RememberDevice(device); store.Rename("小米13"); store.Retain(music);
        string profile = store.Profile.Id;
        store.RememberDevice(other); store.Rename("备用手机"); store.SelectProfile(profile);
        var bluetooth = new AndroidOutputSink("bt:8:test", AndroidOutputSinkKind.Bluetooth, 3, 8, "耳机");
        store.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker, bluetooth]);
        var readOutputs = host.GetMethod("GetOutputDevices", statics)!.CreateDelegate<Func<IReadOnlyList<AudioDeviceInfo>>>();
        string speakerId = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "speaker");
        string bluetoothId = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), bluetooth.Key);
        string otherId = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(other), "speaker");
        string currentId = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "current");
        string otherCurrent = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(other), "current");
        var initial = readOutputs();
        static bool ImmediatelyBefore(IEnumerable<string> values, string current, string speaker)
        {
            var ids = values.ToArray();
            int index = Array.IndexOf(ids, speaker);
            return index > 0 && ids[index - 1] == current;
        }
        Check(ImmediatelyBefore(initial.Select(d => d.Id), currentId, speakerId)
            && ImmediatelyBefore(initial.Select(d => d.Id), otherCurrent, otherId)
            && ImmediatelyBefore(initial.Select(d => d.Id), speakerId, bluetoothId),
            "each handset groups its current output immediately before its own speaker and Bluetooth devices");
        Check(initial.Count(d => d.Id == currentId) == 1 && initial.Count(d => d.Id == otherCurrent) == 1
            && initial.Single(d => d.Id == currentId).DisplayName != initial.Single(d => d.Id == otherCurrent).DisplayName,
            "each connected phone has one distinguishable current media output");
        Check(initial.Single(d => d.Id == speakerId).DisplayName == "小米13 扬声器"
            && initial.Single(d => d.Id == bluetoothId).DisplayName == "耳机 (小米13 · 蓝牙)"
            && initial.Single(d => d.Id == otherId).DisplayName == "备用手机 扬声器",
            "speaker and Bluetooth labels use each saved phone name and retain independent identities");
        Check(ReferenceEquals(initial, readOutputs()), "unchanged output choices reuse the cached snapshot");
        store.Rename("我的小米13");
        Check(!ReferenceEquals(initial, readOutputs())
            && readOutputs().Single(d => d.Id == speakerId).DisplayName == "我的小米13 扬声器"
            && readOutputs().Single(d => d.Id == bluetoothId).DisplayName == "耳机 (我的小米13 · 蓝牙)",
            "renaming refreshes speaker and Bluetooth labels without changing endpoint identities");

        var manager = (AndroidAudioManager)host.GetProperty("Sources", statics)!.GetValue(null)!;
        var first = AddFixture(manager, device, music, key => new[] { device, other }.FirstOrDefault(d => AndroidDeviceIdentity.Key(d) == key));
        var second = AddFixture(manager, device, music with { Package = "cn.test.video", Name = "Video", Uid = 1002 });
        var neighbour = AddFixture(manager, other, music);
        var firstInfo = ExternalApps().Single(a => a.ExternalSourceId == first.Id);
        var adjustVolume = UnifiedAudio.GetMethod("AdjustVolumePercent", statics)!;
        var toggleMute = UnifiedAudio.GetMethod("ToggleMuteChecked", statics)!;
        var adjusted = ((int Current, int Requested, int Actual, bool Applied))adjustVolume.Invoke(null, [firstInfo, -85])!;
        Check(adjusted == (100, 15, 15, true) && second.Session.State.Volume == 100 && neighbour.Session.State.Volume == 100,
            "shared volume adjustment controls only the resolved source and preserves other phone sessions");
        var clamped = ((int Current, int Requested, int Actual, bool Applied))adjustVolume.Invoke(null, [firstInfo, 200])!;
        Check(clamped == (15, 100, 100, true), "shared source adjustment preserves clamping and actual-state feedback");
        var muted = ((bool Muted, bool Applied))toggleMute.Invoke(null, [firstInfo])!;
        var unmuted = ((bool Muted, bool Applied))toggleMute.Invoke(null, [firstInfo])!;
        Check(muted == (true, true) && unmuted == (false, true) && !second.Session.State.Muted && !neighbour.Session.State.Muted,
            "shared mute toggle uses the selected source without changing another session");
        var absent = new AudioAppInfo { ProcessId = 0, ExternalSourceId = "missing-source" };
        Check(((int, int, int, bool))adjustVolume.Invoke(null, [absent, 10])! == (-1, -1, -1, false)
            && ((bool, bool))toggleMute.Invoke(null, [absent])! == (false, false),
            "retired or unknown identities cannot fall through to a Windows audio control");
        var config = ConfigService.Load(); config.QuickPanelShowPhoneAudio = false;
        var windows = new List<AudioDeviceInfo>
        {
            new() { Id = AudioService.SystemDefaultDeviceId, DisplayName = "系统默认播放", Flow = EDataFlow.eRender },
            new() { Id = "pc-headphones", DisplayName = "电脑耳机", Flow = EDataFlow.eRender }
        };
        var merged = windows.Concat(readOutputs()).ToList();
        var airPlayInfo = new AudioAppInfo { ProcessId = 0, ProcessName = "airplay:audio", ExternalSourceId = "airplay:fixture" };
        var outputsForApp = UnifiedAudio.GetMethod("OutputsForApp", statics)!;
        var airPlayOutputs = (IEnumerable<AudioDeviceInfo>)outputsForApp.Invoke(null, [airPlayInfo, merged])!;
        Check(airPlayOutputs.Select(endpoint => endpoint.Id).SequenceEqual(merged.Select(endpoint => endpoint.Id)),
            "AirPlay gets Windows outputs and the connected phones' speaker and Bluetooth choices");
        var reordered = new[] { windows[0], merged.Single(d => d.Id == speakerId), windows[1],
            merged.Single(d => d.Id == otherId), merged.Single(d => d.Id == bluetoothId) };
        config.DeviceNames[speakerId] = "桌面扬声器";
        var androidChoices = ((IEnumerable<AudioDeviceInfo>)outputsForApp.Invoke(null, [firstInfo, reordered])!).ToArray();
        Check(androidChoices.Select(endpoint => endpoint.Id).SequenceEqual(new[] { windows[0].Id, AndroidAudioSession.SourcePhoneOutputId,
                speakerId, windows[1].Id, bluetoothId })
            && androidChoices[2].DisplayName == "桌面扬声器",
            "Android uses the shared order and device aliases for the endpoints it can reach");
        config.DeviceNames.Remove(speakerId);
        var panel = new QuickPanelModernWindow();
        var main = new MainWindow();
        var phone = new PhoneAudioWindow(false);
        try
        {
            typeof(QuickPanelModernWindow).GetField("_outputDisplay", Hidden)!.SetValue(panel, merged);
            var rowPlaybackDevices = typeof(QuickPanelModernWindow).GetMethod("RowPlaybackDevices", Hidden)!;
            var pcInfo = new AudioAppInfo { ProcessId = int.MaxValue - 310, ProcessName = "msedge" };
            config.HiddenOutputDevices.Add(otherId);
            IEnumerable<string> Choices(AudioAppInfo info) => ((IEnumerable<AudioDeviceInfo>)rowPlaybackDevices.Invoke(panel, [info, config])!)
                .Select(endpoint => endpoint.Id);
            Check(Choices(pcInfo).SequenceEqual(Choices(airPlayInfo)) && !Choices(airPlayInfo).Contains(otherId)
                && Choices(pcInfo).Count(id => id == currentId) == 1 && Choices(airPlayInfo).Count(id => id == otherCurrent) == 1,
                "PC apps and AirPlay use identical visible playback choices with hidden outputs respected");
            Check(ImmediatelyBefore(Choices(pcInfo), currentId, speakerId)
                && ImmediatelyBefore(Choices(airPlayInfo), currentId, speakerId)
                && Choices(pcInfo).Where(id => windows.Any(w => w.Id == id)).SequenceEqual(windows.Select(w => w.Id)),
                "quick-panel PC and AirPlay share current-before-speaker ordering while Windows devices retain their order");
            var shift = typeof(QuickPanelModernWindow).GetMethod("SelectPlaybackRange", statics)!;
            var selectedRange = (string[])shift.Invoke(null, [Choices(pcInfo).ToArray(), new HashSet<string> { currentId },
                bluetoothId, currentId, System.Windows.Input.ModifierKeys.Shift])!;
            Check(selectedRange.SequenceEqual(new[] { currentId, speakerId, bluetoothId }),
                "Shift from phone current output to Bluetooth includes exactly that handset's three displayed targets");
            config.HiddenOutputDevices.Remove(otherId);
            typeof(QuickPanelModernWindow).GetMethod("RefreshAndroidRow", Hidden)!.Invoke(panel, [false]);
            var root = (FrameworkElement)((ItemsControl)panel.FindName("AppListPanel")).Items[0];
            Descendants<ToggleButton>(root).Single().IsChecked = true;
            var pills = Descendants<Button>(root).Where(b => b.Tag is AudioDeviceInfo).ToArray();
            Check(pills.Count(b => ((AudioDeviceInfo)b.Tag).Id == AndroidAudioSession.SourcePhoneOutputId) == 1
                && pills.All(b => ((AudioDeviceInfo)b.Tag).Id != currentId)
                && pills.Count(b => ((AudioDeviceInfo)b.Tag).Id == otherCurrent) == 1
                && pills.Count(b => ((AudioDeviceInfo)b.Tag).Id == speakerId) == 1
                && pills.Count(b => ((AudioDeviceInfo)b.Tag).Id == bluetoothId) == 1
                && pills.All(b => !AndroidOutputIdentity.IsPhoneOutput(((AudioDeviceInfo)b.Tag).Id)
                    || ((AudioDeviceInfo)b.Tag).Id == speakerId || ((AudioDeviceInfo)b.Tag).Id == bluetoothId
                    || ((AudioDeviceInfo)b.Tag).Id == otherCurrent),
                "incoming quick panel exposes native playback once, the source's physical outputs, and the other phone's current output");
            var speakerPill = pills.Single(b => ((AudioDeviceInfo)b.Tag).Id == speakerId);
            var btPill = pills.Single(b => ((AudioDeviceInfo)b.Tag).Id == bluetoothId);
            Check(speakerPill.Content?.ToString() == "我的小米13 扬声器"
                && btPill.Content?.ToString() == "耳机"
                && ((AudioDeviceInfo)btPill.Tag).DisplayName == "耳机 (我的小米13 · 蓝牙)",
                "phone speaker and Bluetooth reuse normal endpoint pills with the saved device label");
            store.Rename("小米13"); DrainUi();
            pills = Descendants<Button>(root).Where(b => b.Tag is AudioDeviceInfo).ToArray();
            speakerPill = pills.Single(b => ((AudioDeviceInfo)b.Tag).Id == speakerId);
            btPill = pills.Single(b => ((AudioDeviceInfo)b.Tag).Id == bluetoothId);
            Check(speakerPill.Content?.ToString() == "小米13 扬声器"
                && btPill.Content?.ToString() == "耳机"
                && ((AudioDeviceInfo)btPill.Tag).DisplayName == "耳机 (小米13 · 蓝牙)",
                "renaming also refreshes an already-expanded incoming route group for both speaker and Bluetooth");
            CheckAndroidPeakMeter(panel, first, second, store, device, music);

            typeof(PhoneAudioWindow).GetMethod("EnsurePcDropZones", Hidden)!.Invoke(phone, [true]);
            var currentZone = (Border)phone.FindName("PcCurrentOutputDropZone");
            var speakerZone = (Border)phone.FindName("PcSpeakerDropZone");
            var btZone = (Border)phone.FindName("PcBluetoothDropZone_1");
            Check(Grid.GetRow(currentZone) == 0 && Grid.GetRow(speakerZone) == 1 && Grid.GetRow(btZone) == 2
                && Equals(currentZone.Tag, currentId) && Equals(speakerZone.Tag, speakerId) && Equals(btZone.Tag, bluetoothId),
                "drop cells retain endpoint identities and stable names while putting current output at the top");
            Check(Descendants<TextBlock>((Grid)phone.FindName("PcDropZonesGrid")).Any(t => t.Text == string.Format(L10n.T("Phone.PcDropOnlyOnSpeakerTitle"), "小米13 扬声器")),
                "phone drop feedback uses the same friendly speaker name");

            typeof(MainWindow).GetField("_outputs", Hidden)!.SetValue(main, windows.Concat(readOutputs()).ToList());
            typeof(MainWindow).GetMethod("RefreshDeviceDisplays", Hidden)!.Invoke(main, null);
            var selected = ExternalApps().Single(a => a.ExternalSourceId == second.Id);
            var items = new List<AppItem> { AppItem.From(selected) };
            typeof(MainWindow).GetField("_appItems", Hidden)!.SetValue(main, items);
            var apps = (ListBox)main.FindName("AppsListBox"); apps.ItemsSource = items; apps.SelectedIndex = 0;
            Pump((Task)typeof(MainWindow).GetMethod("RefreshAppsSelectionAsync", Hidden)!.Invoke(main, null)!);
            var combo = (ComboBox)main.FindName("AppsOutputCombo");
            var mainOrder = combo.Items.OfType<AudioDeviceInfo>().Select(d => d.Id).ToArray();
            Check(ImmediatelyBefore(mainOrder, AndroidAudioSession.SourcePhoneOutputId, speakerId)
                && ImmediatelyBefore(mainOrder, speakerId, bluetoothId)
                && mainOrder.Contains(otherCurrent) && !mainOrder.Contains(currentId),
                "main-window Android list uses the native current identity once in the source handset group");
            Check(combo.Items.OfType<AudioDeviceInfo>().Count(d => AndroidOutputIdentity.IsPhoneOutput(d.Id)) == 3
                && combo.Items.OfType<AudioDeviceInfo>().Single(d => d.Id == speakerId).DisplayName == "小米13 扬声器"
                && combo.Items.OfType<AudioDeviceInfo>().Single(d => d.Id == bluetoothId).DisplayName == "耳机 (小米13 · 蓝牙)",
                "full app details expose the source's speaker and Bluetooth devices plus the other phone's current media route");
            Check(((ItemsControl)main.FindName("AppsOutputQuickPanel")).Items.OfType<Button>()
                .Count(b => b.Tag is AudioDeviceInfo d && (d.Id == speakerId || d.Id == bluetoothId)) == 2,
                "full app quick device buttons include both the source phone speaker and Bluetooth device");

            var apply = UnifiedAudio.GetMethod("ApplyEndpointAsync", statics)!;
            var app = ExternalApps().Single(a => a.ExternalSourceId == first.Id);
            foreach (string target in new[] { otherCurrent, currentId })
            {
                var change = (Task<(bool Success, int HResult, string Message)>)apply.Invoke(null, [app, EDataFlow.eRender, target])!;
                Pump(change);
                Check(change.Result.Success && ((IMultiOutputAudioSession)first.Session).GetOutputs()
                    .SequenceEqual([target == currentId ? AndroidAudioSession.SourcePhoneOutputId : otherCurrent]),
                    "incoming selection routes to " + (target == currentId ? "the source handset natively" : "the second handset's current output"));
            }
            var rejected = (Task<(bool Success, int HResult, string Message)>)apply.Invoke(null, [app, EDataFlow.eRender, otherId])!;
            Pump(rejected);
            Check(!rejected.Result.Success && manager.Sources.Count == 3,
                "a foreign phone endpoint is rejected without retiring or rerouting a stream");
            btPill.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForOutput(first, bluetoothId);
            Check(manager.Sources.Count == 3 && first.Session.State.State == CaptureState.Receiving
                && first.Session.State.OutputId == bluetoothId && second.Session.State.OutputId == null,
                "quick-panel Bluetooth selection retains the controlled session and other applications");
            first.Session.SetVolume(37); first.Session.SetMute(true); DrainUi();
            Check(first.Session.State.Volume == 37 && first.Session.State.Muted && manager.Sources.Contains(first),
                "phone playback remains under main-panel volume and mute control");
            first.Session.SetMute(false);
            combo.SelectedItem = combo.Items.OfType<AudioDeviceInfo>().Single(d => d.Id == speakerId);
            WaitForOutput(second, speakerId);
            Check(manager.Sources.Count == 3 && second.Session.State.State == CaptureState.Receiving
                && second.Session.State.OutputId == speakerId && neighbour.Session.State.OutputId == null,
                "full app speaker selection retains the controlled session without affecting another phone");
            var exit = Descendants<Button>(root).Single(b => b.Content?.ToString() == "退出");
            Check(exit.Parent is WrapPanel wrap && ReferenceEquals(wrap.Children[wrap.Children.Count - 1], exit)
                && Descendants<Button>(root).Where(b => b.Tag is AudioDeviceInfo).All(b => ReferenceEquals(b.Parent, wrap)),
                "exit is the last button in the shared playback list");
            exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForRetirement(first);
            Check(!manager.Sources.Contains(first) && manager.Sources.Contains(second) && manager.Sources.Contains(neighbour),
                "only explicit exit relinquishes control, using the same return operation as dragging back to the phone panel");
            Check(scans == 0 && !(bool)host.GetProperty("IsPcToPhoneCreated", statics)!.GetValue(null)!
                && !(bool)host.GetProperty("AreDisplaysCreated", statics)!.GetValue(null)!,
                "speaker routing checks create no scan, PC-to-phone loop or virtual-display worker");
            typeof(MainWindow).GetMethod("EnsureSettingsPage", Hidden)!.Invoke(main, null);
            typeof(MainWindow).GetMethod("EnsureAutomationPage", Hidden)!.Invoke(main, null);
            var candidateApps = ExternalApps();
            var candidates = (List<AppItem>)typeof(MainWindow).GetMethod("GetAppCandidates", Hidden)!.Invoke(main, [candidateApps])!;
            var fixedChoice = (ComboBox)typeof(MainWindow).GetField("FixedAppCombo", Hidden)!.GetValue(main)!;
            var autoChoice = (ComboBox)typeof(MainWindow).GetField("AutoTriggerAppCombo", Hidden)!.GetValue(main)!;
            typeof(MainWindow).GetField("_suppressAppCombo", Hidden)!.SetValue(main, true);
            typeof(MainWindow).GetField("_suppressAutoUi", Hidden)!.SetValue(main, true);
            fixedChoice.ItemsSource = autoChoice.ItemsSource = candidates;
            fixedChoice.SelectedItem = candidates.Single(item => item.Info.ExternalSourceId == second.Id);
            autoChoice.SelectedItem = candidates.Single(item => item.Info.ExternalSourceId == neighbour.Id);
            typeof(MainWindow).GetField("_appItems", Hidden)!.SetValue(main, candidates);
            store.Rename("Batch phone");
            typeof(MainWindow).GetMethod("RefreshExternalAudioLists", Hidden)!.Invoke(main, null);
            Check(ReferenceEquals(fixedChoice.ItemsSource, autoChoice.ItemsSource),
                "a phone metadata batch merges a shared candidate source once for all consumers");
            Check((fixedChoice.SelectedItem as AppItem)?.Info.ExternalSourceId == second.Id
                && (autoChoice.SelectedItem as AppItem)?.Info.ExternalSourceId == neighbour.Id,
                "phone batch reuse retains independent selections for apps on different devices");
            Check(fixedChoice.Items.OfType<AppItem>().Single(item => item.Info.ExternalSourceId == second.Id).Info.SourceLabel == "来自 Batch phone",
                "shared phone candidate refresh carries the newest device metadata");
            config.QuickPanelCompactShowOutputName = config.QuickPanelShowOutputName = false;
            var panelType = typeof(QuickPanelModernWindow);
            panelType.GetField("_readPanelState", Hidden)!.SetValue(panel,
                (Func<IEnumerable<int>, (Dictionary<int, (int Volume, bool Muted, bool MuteKnown)> Apps, bool? AllMuted)>)
                (ids => (ids.ToDictionary(id => id, _ => (50, false, true)), false)));
            int pcReads = 0;
            panelType.GetField("_readRowEndpoints", Hidden)!.SetValue(panel,
                (Func<int, bool, (string Output, string Input)>)((_, _) =>
                {
                    Interlocked.Increment(ref pcReads);
                    return (speakerId, AudioService.SystemDefaultInputDeviceId);
                }));
            panelType.GetField("_micUiOn", Hidden)!.SetValue(panel, true);
            panelType.GetField("_inputDisplay", Hidden)!.SetValue(panel, new List<AudioDeviceInfo>
            {
                new() { Id = AudioService.SystemDefaultInputDeviceId, DisplayName = "系统输入", Flow = EDataFlow.eCapture }
            });
            Pump((Task)panelType.GetMethod("BuildAppRowsAsync", Hidden)!.Invoke(panel, [new List<AudioAppInfo> { pcInfo }, config])!);
            var pcRow = ((System.Collections.IDictionary)panelType.GetField("_rows", Hidden)!.GetValue(panel)!)[(int)pcInfo.ProcessId]!;
            var pcRoot = (FrameworkElement)pcRow.GetType().GetField("RootElement")!.GetValue(pcRow)!;
            Button[] DesktopRoutes() => Descendants<Button>(pcRoot).Where(button => button.Tag is AudioDeviceInfo).ToArray();
            Descendants<ToggleButton>(pcRoot).Single().IsChecked = true;
            Until(() => DesktopRoutes().Any(button => ((AudioDeviceInfo)button.Tag).Flow == EDataFlow.eCapture));
            var desktopButtons = DesktopRoutes();
            Check(desktopButtons.Where(button => ((AudioDeviceInfo)button.Tag).Flow == EDataFlow.eRender)
                .Select(button => ((AudioDeviceInfo)button.Tag).Id).SequenceEqual(Choices(pcInfo))
                && desktopButtons.Where(button => button.Style == panel.FindResource("DevButtonActive"))
                    .Select(button => ((AudioDeviceInfo)button.Tag).Id).SequenceEqual([speakerId, AudioService.SystemDefaultInputDeviceId]),
                "native expansion preserves normal playback order, the selected phone output, and an independent microphone choice");
            Check(desktopButtons.Count(button => ((AudioDeviceInfo)button.Tag).Id == currentId && button.IsEnabled) == 1,
                "a native Windows application exposes one enabled current-output button for the selected phone");
            int before = Volatile.Read(ref pcReads);
            second.Session.SetVolume(31);
            neighbour.Session.SetVolume(59);
            DrainUi();
            Check(Volatile.Read(ref pcReads) == before && desktopButtons.All(button => button.IsEnabled),
                "coalesced volume changes keep the unrelated native output choices ready without endpoint reads");
            var reportRoute = UnifiedAudio.GetMethod("NotifyRouteChanged", statics)!;
            reportRoute.Invoke(null, [(int)pcInfo.ProcessId - 1]); DrainUi();
            Check(Volatile.Read(ref pcReads) == before, "a different native app's route leaves the expanded row alone");
            reportRoute.Invoke(null, [(int)pcInfo.ProcessId]); DrainUi();
            Until(() => Volatile.Read(ref pcReads) > before && desktopButtons.All(button => button.IsEnabled));
            var combine = UnifiedAudio.GetMethod("CombineExternalMute", statics)!;
            second.Session.SetMute(true); neighbour.Session.SetMute(true);
            Check((bool)combine.Invoke(null, [true])! && !(bool)combine.Invoke(null, [false])!,
                "confirmed native batch mute includes both remote sources");
            neighbour.Session.SetMute(false);
            Check(!(bool)combine.Invoke(null, [true])!, "an unmuted remote overrides a muted native batch");
            discovery.Publish([device, other with { State = "offline" }]); DrainUi();
            var onlineOnly = windows.Concat(readOutputs()).ToList();
            foreach (var info in new[] { pcInfo, airPlayInfo, firstInfo })
                Check(!((IEnumerable<AudioDeviceInfo>)outputsForApp.Invoke(null, [info, onlineOnly])!).Any(d => d.Id == otherCurrent),
                    "disconnect removes the destination handset's current output from " + info.ProcessName);
            Pump(store.FlushAsync());
            File.WriteAllText(Path.Combine(output, "speaker-routing.json"), JsonSerializer.Serialize(new
            { Passed = true, Assertions = _checks, Scans = scans, RealPhoneAccess = false,
                Scope = "Fixture sessions; quick/full output choices retain source, volume and mute; explicit exit releases source. SmartVolume and Android peak meter retained. No real phone or audible acceptance." },
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
        void WaitForOutput(AndroidAudioSource source, string id)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (source.Session.State.OutputId != id && DateTime.UtcNow < deadline) Pump(Task.Delay(10));
            DrainUi();
        }
    }

    private static void CheckAndroidPeakMeter(QuickPanelModernWindow panel, AndroidAudioSource first, AndroidAudioSource second,
        AndroidPhoneStore store, AndroidDevice device, AndroidApp music)
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
        var peakVisualObj = (Border?)rowType.GetField("PeakVisual", Hidden)!.GetValue(firstRow);
        var peakTransformObj = (ScaleTransform?)rowType.GetField("PeakTransform", Hidden)!.GetValue(firstRow);
        Check(peakVisualObj != null && peakTransformObj != null
            && ReferenceEquals(peakVisualObj.Background, panel.FindResource("Theme.AccentPeak"))
            && peakVisualObj.Visibility == Visibility.Hidden && peakTransformObj.ScaleX == 0d && peakTransformObj.ScaleY == 1d,
            "Android row slider binds PART_PeakVisual with Theme.AccentPeak and an idle ScaleTransform");
        var peakVisual = peakVisualObj!;
        var peakTransform = peakTransformObj!;

        var pcm = new AndroidPcmSource { SmartVolume = first.Session.State.SmartVolume };
        var pcmField = typeof(AndroidAudioSession).GetField("_pcm", Hidden)!;
        pcmField.SetValue(first.Session, pcm);
        int wakeups = 0;
        Action onPeakActivity = () => Interlocked.Increment(ref wakeups);
        AndroidPcmSource.PeakActivity += onPeakActivity;
        var createMeter = typeof(QuickPanelModernWindow).GetMethod("CreateAudioMeterService", Hidden)!;
        var meter = (AudioMeterService)createMeter.Invoke(panel, null)!;
        try
        {
            Check(first.Session.State.SmartVolume && pcm.SmartVolume && store.IsSmartVolumeEnabled(device, music.Package),
                "SmartVolume is enabled by default on phone profile and active Android session");
            first.Session.SetVolume(10); DrainUi();
            var fullScale = new float[960]; Array.Fill(fullScale, 0.9f);
            pcm.Write(fullScale);
            float smartGainAt10 = pcm.EffectiveGain;
            var readBuf = new byte[960 * sizeof(float)];
            pcm.Read(readBuf, 0, readBuf.Length);
            float smartSample = Math.Abs(BitConverter.ToSingle(readBuf, 0));
            store.SetSmartVolume(music.Package, false);
            Check(!first.Session.State.SmartVolume && !pcm.SmartVolume
                && Math.Abs(pcm.EffectiveGain - 0.10f) < 0.0001f && smartGainAt10 < 0.025f && smartSample < 0.025f,
                "SmartVolume automatically attenuates loud full-scale audio at 10% slider volume and live-toggles off via store");
            store.SetSmartVolume(music.Package, true);
            Check(first.Session.State.SmartVolume && pcm.SmartVolume,
                "re-enabling SmartVolume in store immediately updates the active Android session and PCM source");
            first.Session.SetVolume(100); DrainUi();
            pcm.Clear();
            wakeups = 0;

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
            Check(Math.Abs(rowPeak - 0.8f) < 0.001f && peakVisual.Visibility == Visibility.Visible
                && Math.Abs(peakTransform.ScaleX - 0.8d) < 0.02d,
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

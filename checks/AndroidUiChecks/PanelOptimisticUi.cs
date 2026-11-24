using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SonicRoute;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private sealed class BlockingPcmPlayer : IPcmPlayer
    {
        private readonly Action? _onPlay;
        internal BlockingPcmPlayer(Action? onPlay = null) => _onPlay = onPlay;
        public void Play() => _onPlay?.Invoke();
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class ExternalRouteRequest(string sourceId, string? endpoint)
    {
        internal string SourceId { get; } = sourceId;
        internal string? Endpoint { get; } = endpoint;
        internal readonly TaskCompletionSource<bool> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class MicWriteRequest(int pid, string deviceId)
    {
        internal int Pid { get; } = pid;
        internal string DeviceId { get; } = deviceId;
        internal readonly TaskCompletionSource<bool> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SystemDefaultWriteRequest(string deviceId)
    {
        internal string DeviceId { get; } = deviceId;
        internal readonly TaskCompletionSource<bool> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static int CheckPanelOptimisticUi(App app, string output)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;

        var trayType = typeof(App).Assembly.GetType("SonicRoute.TrayWheelService")!;
        using var tray = (IDisposable)Activator.CreateInstance(trayType, new object?[] { null })!;
        object osd = trayType.GetField("_osdService", Hidden)!.GetValue(tray)!;
        var osdType = osd.GetType();
        typeof(App).GetField("_trayWheel", Hidden)!.SetValue(app, tray);

        var config = ConfigService.Load();
        bool origShowOutputName = config.QuickPanelShowOutputName;
        bool origCompactShowOutputName = config.QuickPanelCompactShowOutputName;
        bool origLegacy = config.QuickPanelUseLegacyAppRows;
        bool origChangeSysDefault = config.PanelChangeSystemDefault;

        try
        {
            var timings = new List<string>();
            CheckAirPlayOptimisticUi(timings);
            CheckPcMicOptimisticUi(timings);
            CheckUnifiedRowDeviceNames();
            CheckSystemDevComboSelection();
            CheckPerformanceAndGc(timings);

            string summary = string.Join(Environment.NewLine, timings);
            File.WriteAllText(Path.Combine(output, "panel-optimistic-ui-timings.log"), summary + Environment.NewLine);
            Console.WriteLine(summary);
            Console.WriteLine($"PASS {_checks} panel optimistic UI assertions.");
            return 0;
        }
        finally
        {
            config.QuickPanelShowOutputName = origShowOutputName;
            config.QuickPanelCompactShowOutputName = origCompactShowOutputName;
            config.QuickPanelUseLegacyAppRows = origLegacy;
            config.PanelChangeSystemDefault = origChangeSysDefault;
            typeof(App).GetField("_trayWheel", Hidden)!.SetValue(app, null);
            Window? osdWindow = (Window?)osdType.GetField("_window", Hidden)!.GetValue(osd);
            osdWindow?.Close();
        }

        string OsdTitle() => osdType.GetField("_title", Hidden)!.GetValue(osd) is TextBlock tb ? tb.Text : "";
        string OsdText() => osdType.GetField("_text", Hidden)!.GetValue(osd) is TextBlock tb ? tb.Text : "";
        bool OsdVisible() => (osdType.GetField("_window", Hidden)!.GetValue(osd) as Window)?.IsVisible == true;

        void ClearOsdWindow()
        {
            if (osdType.GetField("_window", Hidden)!.GetValue(osd) is Window w && w.IsVisible) w.Hide();
        }

        // ------------------------------------------------------------------
        // 1. AirPlay optimistic UI
        // ------------------------------------------------------------------
        void CheckAirPlayOptimisticUi(List<string> timings)
        {
            var library = typeof(AndroidDevice).Assembly;
            var host = typeof(App).Assembly.GetType("SonicRoute.AirPlayHost")!;
            var receiverType = library.GetType("SonicRoute.AirPlay.AirPlayReceiver")!;
            var sessionType = receiverType.GetNestedType("Session", BindingFlags.NonPublic)!;
            var sourceType = library.GetType("SonicRoute.AirPlay.AirPlayAudioOutput")!;
            var receiverField = host.GetField("_receiver", statics)!;
            var publish = host.GetMethod("Publish", statics)!.CreateDelegate<Action>();

            string system = AudioService.SystemDefaultDeviceId;
            const string devA = "ap-out-a", devB = "ap-out-b", devC = "ap-out-c";
            string phoneSpeaker = AndroidOutputIdentity.FormatDeviceId("airplay-phone-fixture", "speaker");

            var config = ConfigService.Load();
            config.QuickPanelUseLegacyAppRows = false;
            config.QuickPanelShowOutputName = true;
            config.QuickPanelCompactShowOutputName = false;

            var outputs = PanelDevices.WithSystemDefault([
                new AudioDeviceInfo { Id = devA, DisplayName = "AirPlay Out A", Flow = EDataFlow.eRender },
                new AudioDeviceInfo { Id = devB, DisplayName = "AirPlay Out B", Flow = EDataFlow.eRender },
                new AudioDeviceInfo { Id = devC, DisplayName = "AirPlay Out C", Flow = EDataFlow.eRender },
                new AudioDeviceInfo { Id = phoneSpeaker, DisplayName = "Fixture Phone 扬声器", Flow = EDataFlow.eRender },
            ], EDataFlow.eRender, config);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var requests = Channel.CreateUnbounded<ExternalRouteRequest>();
            bool blockBackend = false;
            bool initialPlayerCreated = false;

            Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer> MakePlayer(string sourceTag) => (_, endpoint, _) =>
            {
                if (!initialPlayerCreated && endpoint == null)
                {
                    initialPlayerCreated = true;
                    return new BlockingPcmPlayer();
                }
                if (!blockBackend) return new BlockingPcmPlayer();
                var req = new ExternalRouteRequest(sourceTag, endpoint);
                requests.Writer.TryWrite(req);
                bool ok = req.Finish.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
                return new BlockingPcmPlayer(() => { if (!ok) throw new IOException("Simulated AirPlay player failure."); });
            };

            Func<Action, IDisposable> watch = _ => new BlockingPcmPlayer();
            using var transport = new TcpClient();
            using var lifetime = new CancellationTokenSource();
            var playerFactory = MakePlayer("session-1");
            var receiver = (IAsyncDisposable)Activator.CreateInstance(receiverType, instance, null,
                ["AirPlayFixture", true, playerFactory, watch, false, false], null)!;
            var source = (IAsyncDisposable)Activator.CreateInstance(sourceType, instance, null,
                ["Simulated iPhone", publish, (Action)(() => { }), playerFactory, watch, CancellationToken.None], null)!;
            var session = Activator.CreateInstance(sessionType, instance, null,
                [receiver, transport, IPAddress.Loopback, IPAddress.Loopback], null)!;
            sessionType.GetField("_stop", instance)!.SetValue(session, lifetime);
            sessionType.GetField("_output", instance)!.SetValue(session, source);
            var activeField = receiverType.GetField("_active", instance)!;
            activeField.SetValue(receiver, session);
            receiverField.SetValue(null, receiver);

            sourceType.GetMethod("Write", instance)!.CreateDelegate<WriteAirPlaySamples>(source)([0.25f, -0.25f], AndroidPcmSource.SampleRate);
            string sourceId = (string)sourceType.GetProperty("Id", instance)!.GetValue(source)!;
            var stateProp = sourceType.GetProperty("State", instance)!;
            string? ActualOutput() => ((AndroidAudioState)stateProp.GetValue(source)!).OutputId;

            var panel = new QuickPanelModernWindow();
            var panelType = typeof(QuickPanelModernWindow);
            var androidRowsField = panelType.GetField("_androidRows", Hidden)!;
            var refreshAndroidRow = panelType.GetMethod("RefreshAndroidRow", Hidden)!;
            var buildAndroidDevices = panelType.GetMethod("BuildAndroidDevices", Hidden)!;
            var refreshSourceState = panelType.GetMethod("RefreshSourceState", Hidden)!;

            object GetRow(QuickPanelModernWindow w, string id) => ((IDictionary)androidRowsField.GetValue(w)!)[id]!;
            static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Hidden | BindingFlags.Public)!.GetValue(obj)!;
            static List<(Button Button, string Id)> Devices(object row) => Field<List<(Button, string)>>(row, "Devices");
            Button Pill(object row, string id) => Devices(row).Single(d => d.Id == id).Button;
            void AssertAirPlayShownOn(QuickPanelModernWindow win, object row, string desc, string expectedId)
            {
                var winActive = win.FindResource("DevButtonActive");
                var winIdle = win.FindResource("DevButton");
                var devs = Devices(row);
                var active = devs.Where(d => ReferenceEquals(d.Button.Style, winActive)).Select(d => d.Id).ToArray();
                Check(active.Length == 1 && string.Equals(active[0], expectedId, StringComparison.OrdinalIgnoreCase)
                    && devs.All(d => d.Button.IsEnabled && (string.Equals(d.Id, expectedId, StringComparison.OrdinalIgnoreCase) || ReferenceEquals(d.Button.Style, winIdle))),
                    desc);
            }
            void AssertAirPlayShown(object row, string desc, string expectedId) => AssertAirPlayShownOn(panel, row, desc, expectedId);
            void Complete(Task t) => Pump(t.WaitAsync(deadline.Token));
            ExternalRouteRequest TakeReq(string? expectedEndpoint)
            {
                var read = requests.Reader.ReadAsync(deadline.Token).AsTask();
                Complete(read);
                var r = read.Result;
                Check(string.Equals(r.Endpoint, expectedEndpoint, StringComparison.OrdinalIgnoreCase),
                    $"AirPlay backend receives serialized route '{expectedEndpoint ?? "null"}'");
                return r;
            }
            void Prepare(QuickPanelModernWindow w, List<AudioDeviceInfo> devs, string id)
            {
                panelType.GetField("_outputDisplay", Hidden)!.SetValue(w, devs);
                refreshAndroidRow.Invoke(w, [true]);
                var r = GetRow(w, id);
                Field<ToggleButton>(r, "Expand").IsChecked = true;
                Field<AnimatedExpandHost>(r, "Wrap").FinishImmediately();
                buildAndroidDevices.Invoke(w, [r]);
            }

            IAsyncDisposable? source2 = null;
            object? session2 = null;
            using var lifetime2 = new CancellationTokenSource();
            try
            {
                Prepare(panel, outputs, sourceId);
                var row = GetRow(panel, sourceId);
                AssertAirPlayShown(row, "AirPlay starts on Windows system default", system);

                // 1a. Click while backend is blocked -> immediate DevButtonActive, IsEnabled==true, State.OutputId unchanged
                blockBackend = true;
                Button bBtn = Pill(row, devB);
                long t0 = Stopwatch.GetTimestamp();
                bBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                double firstClickMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                AssertAirPlayShown(row, "AirPlay click highlights target immediately while backend is blocked", devB);
                Check(ActualOutput() == null && Devices(row).All(d => d.Button.IsEnabled),
                    "AirPlay blocked backend keeps State.OutputId unchanged and all row buttons enabled");
                timings.Add(FormattableString.Invariant($"AirPlay first-click-to-style={firstClickMs:F3} ms"));

                // 1j. Background RefreshSourceState(row, false) while pending does not overwrite optimistic selection or row name
                sourceType.GetMethod("SetVolume", instance)!.Invoke(source, [64]);
                refreshSourceState.Invoke(panel, [row, false]);
                AssertAirPlayShown(row, "AirPlay background RefreshSourceState(false) preserves pending highlight", devB);
                Check(Field<TextBlock>(row, "Name").Text == "AirPlay Out B",
                    "AirPlay background RefreshSourceState(false) preserves pending row output name");

                // 1b. Commit success -> stays selected and confirmed
                var reqB = TakeReq(devB);
                reqB.Finish.SetResult(true);
                Complete(Field<Task>(row, "PlaybackChange"));
                Check(ActualOutput() == devB, "AirPlay commit updates real State.OutputId to B");
                AssertAirPlayShown(row, "AirPlay commit keeps B highlighted and confirmed", devB);

                // Warm click timing + switch to A
                Button aBtn = Pill(row, devA);
                long tWarm = Stopwatch.GetTimestamp();
                aBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                double warmClickMs = Stopwatch.GetElapsedTime(tWarm).TotalMilliseconds;
                timings.Add(FormattableString.Invariant($"AirPlay warm-click-to-style={warmClickMs:F3} ms"));
                TakeReq(devA).Finish.SetResult(true);
                Complete(Field<Task>(row, "PlaybackChange"));
                Check(ActualOutput() == devA, "AirPlay warm click confirms A");

                // 1c. Failure rollback -> restores actual State.OutputId, highlight, and shows OSD error
                ClearOsdWindow();
                Pill(row, devB).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertAirPlayShown(row, "AirPlay pending failure initially shows B", devB);
                TakeReq(devB).Finish.SetResult(false);
                Complete(Field<Task>(row, "PlaybackChange"));
                Check(ActualOutput() == devA, "AirPlay failure leaves real State.OutputId on A");
                AssertAirPlayShown(row, "AirPlay failure rolls back highlight to A", devA);
                Check(OsdVisible() && OsdTitle() == L10n.T("Phone.AirPlayTitle") && OsdText() == L10n.T("Phone.AirPlayAudioError"),
                    "AirPlay failure shows AirPlay OSD error");

                // 1d. Rapid A -> B -> C -> immediately highlights C while B is blocked, B completion cannot overwrite C, serialized commits B then C
                Pill(row, devB).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var rapidB = TakeReq(devB);
                Pill(row, devC).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertAirPlayShown(row, "AirPlay rapid A->B->C immediately highlights C while B is blocked", devC);
                Check(!requests.Reader.TryPeek(out _), "AirPlay C write is serialized behind B");
                rapidB.Finish.SetResult(true);
                var rapidC = TakeReq(devC);
                AssertAirPlayShown(row, "AirPlay B completion cannot overwrite pending C highlight", devC);
                Check(ActualOutput() == devB, "AirPlay intermediate B committed while C is still pending");
                rapidC.Finish.SetResult(true);
                Complete(Field<Task>(row, "PlaybackChange"));
                Check(ActualOutput() == devC, "AirPlay A->B->C final confirms C");
                AssertAirPlayShown(row, "AirPlay A->B->C final highlight stays on C", devC);

                // 1e. Rapid A -> B -> A (and repeated A) -> stays on A, intermediate B failure does NOT overwrite newer A
                Pill(row, devA).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TakeReq(devA).Finish.SetResult(true);
                Complete(Field<Task>(row, "PlaybackChange"));
                Pill(row, devB).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var failB = TakeReq(devB);
                Pill(row, devA).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pill(row, devA).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertAirPlayShown(row, "AirPlay A->B->A (and repeated A) immediately shows A", devA);
                failB.Finish.SetResult(false);
                // When B fails and actual is already A, A's SetOutputAsync sees OutputId == A and returns without creating a new player
                Complete(Field<Task>(row, "PlaybackChange"));
                Check(ActualOutput() == devA, "AirPlay A->B->A stays on actual A after intermediate B failure");
                AssertAirPlayShown(row, "AirPlay A->B->A intermediate B failure does not overwrite newer A highlight", devA);

                // 1f. Ctrl+Click / Shift+Click multi-select on AirPlay (Windows + phone speaker) and single-click return
                void AssertAirPlayShownMulti(object r, string desc, params string[] expectedIds)
                {
                    var winActive = panel.FindResource("DevButtonActive");
                    var winIdle = panel.FindResource("DevButton");
                    var devs = Devices(r);
                    var active = devs.Where(d => ReferenceEquals(d.Button.Style, winActive)).Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var expected = expectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    Check(active.SetEquals(expected)
                        && devs.All(d => d.Button.IsEnabled && (expected.Contains(d.Id) || ReferenceEquals(d.Button.Style, winIdle))),
                        desc);
                }
                string[] ActualAirPlayOutputs() => ((IMultiOutputAudioSession)source).GetOutputs()
                    .Select(id => id ?? system).ToArray();

                Pill(row, devC).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TakeReq(devC).Finish.SetResult(true);
                Complete(Field<Task>(row, "PlaybackChange"));

                var savedKeys = new byte[256];
                if (!GetKeyboardState(savedKeys)) throw new IOException("Unable to read keyboard state.");
                try
                {
                    var ctrlKeys = (byte[])savedKeys.Clone();
                    ctrlKeys[0x11] = 0x80; // VK_CONTROL
                    ctrlKeys[0xA2] = 0x80; // VK_LCONTROL (read by WPF Keyboard.Modifiers)
                    SetKeyboardState(ctrlKeys);

                    Pill(row, devB).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var pendingCtrl = Field<object>(row, "PendingOutput");
                    var pendingCtrlOuts = (string[])pendingCtrl.GetType().GetProperty("Outputs")!.GetValue(pendingCtrl)!;
                    Check(pendingCtrlOuts.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([devC, devB]),
                        "AirPlay Ctrl+Click adds secondary output optimistically");
                    AssertAirPlayShownMulti(row, "AirPlay Ctrl+Click highlights both C and B optimistically", devC, devB);
                    TakeReq(devB).Finish.SetResult(true);
                    Complete(Field<Task>(row, "PlaybackChange"));
                    Check(ActualAirPlayOutputs().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([devC, devB]),
                        "AirPlay Ctrl+Click confirms [devC, devB] via IMultiOutputAudioSession");
                    AssertAirPlayShownMulti(row, "AirPlay Ctrl+Click keeps [devC, devB] highlighted after commit", devC, devB);

                    Pill(row, phoneSpeaker).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    AssertAirPlayShownMulti(row, "AirPlay Ctrl+Click adds phone speaker alongside Windows outputs", devC, devB, phoneSpeaker);
                    TakeReq(phoneSpeaker).Finish.SetResult(true);
                    Complete(Field<Task>(row, "PlaybackChange"));
                    Check(ActualAirPlayOutputs().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([devC, devB, phoneSpeaker]),
                        "AirPlay confirms [devC, devB, phoneSpeaker]");

                    Pill(row, phoneSpeaker).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Complete(Field<Task>(row, "PlaybackChange"));
                    Pill(row, devB).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Complete(Field<Task>(row, "PlaybackChange"));
                    Check(ActualAirPlayOutputs().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([devC]),
                        "AirPlay Ctrl+Click removes devB and phoneSpeaker cleanly back to [devC]");
                    AssertAirPlayShown(row, "AirPlay Ctrl+Click removal leaves only C highlighted", devC);

                    SetKeyboardState(savedKeys);
                    Pill(row, devC).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Complete(Field<Task>(row, "PlaybackChange"));

                    var shiftKeys = (byte[])savedKeys.Clone();
                    shiftKeys[0x10] = 0x80; // VK_SHIFT
                    shiftKeys[0xA0] = 0x80; // VK_LSHIFT (read by WPF Keyboard.Modifiers)
                    SetKeyboardState(shiftKeys);

                    Pill(row, phoneSpeaker).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    AssertAirPlayShownMulti(row, "AirPlay Shift+Click adds phone speaker range optimistically", devC, phoneSpeaker);
                    TakeReq(phoneSpeaker).Finish.SetResult(true);
                    Complete(Field<Task>(row, "PlaybackChange"));
                    Check(ActualAirPlayOutputs().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([devC, phoneSpeaker]),
                        "AirPlay Shift+Click confirms [devC, phoneSpeaker]");

                    Pill(row, devA).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    AssertAirPlayShownMulti(row, "AirPlay Shift+Click adds contiguous range while preserving existing selection", devA, devB, devC, phoneSpeaker);
                    TakeReq(devA).Finish.SetResult(true);
                    TakeReq(devB).Finish.SetResult(true);
                    Complete(Field<Task>(row, "PlaybackChange"));
                    Check(ActualAirPlayOutputs().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([devA, devB, devC, phoneSpeaker]),
                        "AirPlay Shift+Click confirms contiguous range plus preserved selection");
                    AssertAirPlayShownMulti(row, "AirPlay Shift+Click keeps all selected outputs highlighted after commit", devA, devB, devC, phoneSpeaker);
                }
                finally { SetKeyboardState(savedKeys); }

                Pill(row, devA).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertAirPlayShown(row, "AirPlay single Click returns optimistically to A", devA);
                TakeReq(devA).Finish.SetResult(true);
                Complete(Field<Task>(row, "PlaybackChange"));
                Check(ActualAirPlayOutputs().SequenceEqual([devA], StringComparer.OrdinalIgnoreCase),
                    "AirPlay single Click confirms return to single selection A");
                AssertAirPlayShown(row, "AirPlay single Click keeps only A highlighted after commit", devA);

                // 1g. Device disconnect (removed from _outputDisplay while pending or before write)
                // Case 1: queued behind blocked B, then C is removed from _outputDisplay before C's write starts
                Pill(row, devB).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var holdB = TakeReq(devB);
                Pill(row, devC).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var withoutC = outputs.Where(d => d.Id != devC).ToList();
                panelType.GetField("_outputDisplay", Hidden)!.SetValue(panel, withoutC);
                // Clicking stale C button now is rejected immediately before queueing
                ClearOsdWindow();
                Pill(row, devC).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(OsdVisible() && OsdText() == L10n.T("Ov.CurrentUnavailable"),
                    "AirPlay clicking unavailable disconnected device shows OSD error immediately");
                // Unblock B -> C's queued task sees C missing from _outputDisplay, skips backend write, rolls back to B
                ClearOsdWindow();
                holdB.Finish.SetResult(true);
                Complete(Field<Task>(row, "PlaybackChange"));
                Check(!requests.Reader.TryPeek(out _) && ActualOutput() == devB,
                    "AirPlay disconnected target is never sent to backend");
                AssertAirPlayShown(row, "AirPlay disconnected target rolls back to actual route B", devB);
                Check(OsdVisible() && OsdText() == L10n.T("Ov.CurrentUnavailable"),
                    "AirPlay disconnected target shows unavailable OSD during queued validation");

                // Case 2: device removed from _outputDisplay while pending and BuildAndroidDevices runs -> clears invalid target
                panelType.GetField("_outputDisplay", Hidden)!.SetValue(panel, outputs);
                void RebuildDevices(QuickPanelModernWindow win, object r) => buildAndroidDevices.Invoke(win, [r]);
                RebuildDevices(panel, row);
                Pill(row, devC).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var holdC = TakeReq(devC);
                panelType.GetField("_outputDisplay", Hidden)!.SetValue(panel, withoutC);
                RebuildDevices(panel, row);
                Check(Field<object?>(row, "PendingOutput") == null,
                    "AirPlay device removal during rebuild clears invalid pending target");
                AssertAirPlayShown(row, "AirPlay device removal during rebuild restores actual route B", devB);
                holdC.Finish.SetResult(true);
                Complete(Field<Task>(row, "PlaybackChange"));
                panelType.GetField("_outputDisplay", Hidden)!.SetValue(panel, outputs);
                RebuildDevices(panel, row);

                // 1i. Windows default (@@SYSTEM_DEFAULT@@) <-> phone speaker (android-out:...) switching
                // First switch from B to phone speaker, then to @@SYSTEM_DEFAULT@@, then back to phone speaker
                Pill(row, phoneSpeaker).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertAirPlayShown(row, "AirPlay switching to phone speaker highlights phone speaker optimistically", phoneSpeaker);
                TakeReq(phoneSpeaker).Finish.SetResult(true);
                Complete(Field<Task>(row, "PlaybackChange"));
                Check(ActualOutput() == phoneSpeaker, "AirPlay confirms phone speaker endpoint");

                Pill(row, system).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertAirPlayShown(row, "AirPlay switching to Windows default highlights system default optimistically", system);
                TakeReq(null).Finish.SetResult(true);
                Complete(Field<Task>(row, "PlaybackChange"));
                Check(ActualOutput() == null, "AirPlay confirms Windows default (null OutputId)");

                // Switch from Windows default back to phone speaker via _applyExternalOutput seam verification
                var origApplyExternal = (Func<AudioAppInfo, EDataFlow, string, Task<(bool, int, string)>>)
                    panelType.GetField("_applyExternalOutput", Hidden)!.GetValue(panel)!;
                string? externalHookTarget = null;
                panelType.GetField("_applyExternalOutput", Hidden)!.SetValue(panel,
                    (Func<AudioAppInfo, EDataFlow, string, Task<(bool, int, string)>>)(async (appInfo, flow, id) =>
                    {
                        externalHookTarget = id;
                        return await origApplyExternal(appInfo, flow, id);
                    }));
                Pill(row, phoneSpeaker).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TakeReq(phoneSpeaker).Finish.SetResult(true);
                Complete(Field<Task>(row, "PlaybackChange"));
                Check(externalHookTarget == phoneSpeaker && ActualOutput() == phoneSpeaker,
                    "AirPlay Windows default <-> phone speaker routes through _applyExternalOutput and confirms");
                panelType.GetField("_applyExternalOutput", Hidden)!.SetValue(panel, origApplyExternal);

                // 1h. AirPlay session stop and recreate (new AirPlayAudioOutput with new Id): late completion from old session cannot touch new row
                Pill(row, devB).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var staleOldReq = TakeReq(devB);
                Task oldPlaybackTask = Field<Task>(row, "PlaybackChange");

                // Stop old session and create new AirPlayAudioOutput with new Id
                var playerFactory2 = MakePlayer("session-2");
                initialPlayerCreated = false;
                blockBackend = false;
                source2 = (IAsyncDisposable)Activator.CreateInstance(sourceType, instance, null,
                    ["Simulated iPhone 2", publish, (Action)(() => { }), playerFactory2, watch, CancellationToken.None], null)!;
                session2 = Activator.CreateInstance(sessionType, instance, null,
                    [receiver, transport, IPAddress.Loopback, IPAddress.Loopback], null)!;
                sessionType.GetField("_stop", instance)!.SetValue(session2, lifetime2);
                sessionType.GetField("_output", instance)!.SetValue(session2, source2);
                activeField.SetValue(receiver, session2);
                sourceType.GetMethod("Write", instance)!.CreateDelegate<WriteAirPlaySamples>(source2)([0.25f, -0.25f], AndroidPcmSource.SampleRate);
                string sourceId2 = (string)sourceType.GetProperty("Id", instance)!.GetValue(source2)!;
                Check(sourceId2 != sourceId, "recreated AirPlay session has a distinct Id");

                blockBackend = true;
                refreshAndroidRow.Invoke(panel, [true]);
                Check(Field<bool>(row, "Retired"), "old AirPlay row is marked Retired on session recreate");
                var row2 = ((IDictionary)androidRowsField.GetValue(panel)!)[sourceId2]!;
                Field<ToggleButton>(row2, "Expand").IsChecked = true;
                Field<AnimatedExpandHost>(row2, "Wrap").FinishImmediately();
                buildAndroidDevices.Invoke(panel, [row2]);
                AssertAirPlayShown(row2, "new AirPlay row starts on Windows default", system);

                // Now unblock old session's late completion
                staleOldReq.Finish.SetResult(true);
                Complete(oldPlaybackTask);
                AssertAirPlayShown(row2, "late completion from old AirPlay session cannot touch new row", system);
                Check(((AndroidAudioState)stateProp.GetValue(source2)!).OutputId == null,
                    "late completion from old AirPlay session cannot change new session OutputId");

                // Switch new session to devC and verify panel close & reopen shows real confirmed State.OutputId
                Pill(row2, devC).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TakeReq(devC).Finish.SetResult(true);
                Complete(Field<Task>(row2, "PlaybackChange"));
                Check(((AndroidAudioState)stateProp.GetValue(source2)!).OutputId == devC, "new AirPlay session confirms C");

                // 1k. Panel close & reopen shows real confirmed State.OutputId
                panel.Close();
                var reopened = new QuickPanelModernWindow();
                try
                {
                    Prepare(reopened, outputs, sourceId2);
                    var reopenedRow = GetRow(reopened, sourceId2);
                    AssertAirPlayShownOn(reopened, reopenedRow, "reopened panel shows real confirmed AirPlay State.OutputId (C)", devC);
                    Check(Field<TextBlock>(reopenedRow, "Name").Text == "AirPlay Out C",
                        "reopened panel shows confirmed AirPlay output name");
                }
                finally { reopened.Close(); }
            }
            finally
            {
                if (!Field<bool>(panel, "_isClosed")) panel.Close();
                receiverField.SetValue(null, null);
                activeField.SetValue(receiver, null);
                Pump(source.DisposeAsync().AsTask());
                if (source2 != null) Pump(source2.DisposeAsync().AsTask());
                Pump(receiver.DisposeAsync().AsTask());
                publish();
            }
        }

        // ------------------------------------------------------------------
        // 2. PC Microphone input (eCapture) single-select optimistic UI + independence from PC output
        // ------------------------------------------------------------------
        void CheckPcMicOptimisticUi(List<string> timings)
        {
            string sysIn = AudioService.SystemDefaultInputDeviceId;
            string sysOut = AudioService.SystemDefaultDeviceId;
            const string outA = "pc-out-a", outB = "pc-out-b", outC = "pc-out-c";
            const string micA = "pc-mic-a", micB = "pc-mic-b", micC = "pc-mic-c", micExtra = "pc-mic-extra";
            const int pid1 = int.MaxValue - 701, pid2 = int.MaxValue - 702;

            var cfg = ConfigService.Load();
            cfg.QuickPanelUseLegacyAppRows = false;
            cfg.QuickPanelShowOutputName = false;
            cfg.QuickPanelCompactShowOutputName = false;

            var app1 = new AudioAppInfo { ProcessId = pid1, ProcessName = "mic-fixture-1", DisplayName = "Mic App 1" };
            var app2 = new AudioAppInfo { ProcessId = pid2, ProcessName = "mic-fixture-2", DisplayName = "Mic App 2" };

            var outDevices = new[] { sysOut, outA, outB, outC }
                .Select(id => new AudioDeviceInfo { Id = id, DisplayName = id, Flow = EDataFlow.eRender }).ToList();
            var inDevices = new[] { sysIn, micA, micB, micC }
                .Select(id => new AudioDeviceInfo { Id = id, DisplayName = id, Flow = EDataFlow.eCapture }).ToList();

            var actualMic = new ConcurrentDictionary<int, string> { [pid1] = micA, [pid2] = micA };
            var actualOut = new ConcurrentDictionary<int, (string Primary, string[] Outputs)>
            {
                [pid1] = (outA, [outA]),
                [pid2] = (outA, [outA])
            };

            var micWrites = Channel.CreateUnbounded<MicWriteRequest>();
            var outWrites = Channel.CreateUnbounded<PcWrite>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var panel = new QuickPanelModernWindow();
            var type = typeof(QuickPanelModernWindow);
            var activeStyle = panel.FindResource("DevButtonActive");
            var mirrorStyle = panel.FindResource("DevButtonMirror");
            var idleStyle = panel.FindResource("DevButton");
            var rows = (IDictionary)type.GetField("_rows", Hidden)!.GetValue(panel)!;

            void Set(string name, object value) => type.GetField(name, Hidden)!.SetValue(panel, value);
            static T Field<T>(object row, string name) => (T)row.GetType().GetField(name, Hidden | BindingFlags.Public)!.GetValue(row)!;
            static List<(Button Button, string Id, EDataFlow Flow)> Buttons(object row) => Field<List<(Button, string, EDataFlow)>>(row, "DeviceButtons");
            static Button DevBtn(object row, string id, EDataFlow flow) => Buttons(row).Single(b => b.Id == id && b.Flow == flow).Button;
            bool IsActive(Button b) => ReferenceEquals(b.Style, activeStyle);
            bool IsMirror(Button b) => ReferenceEquals(b.Style, mirrorStyle);
            void AssertFlow(object row, EDataFlow flow, string desc, params string[] expected)
            {
                var btns = Buttons(row).Where(b => b.Flow == flow).ToList();
                var active = btns.Where(b => IsActive(b.Button)).Select(b => b.Id).ToHashSet();
                if (flow == EDataFlow.eCapture)
                {
                    Check(active.SetEquals(expected)
                        && btns.All(b => b.Button.IsEnabled && (expected.Contains(b.Id) || ReferenceEquals(b.Button.Style, idleStyle))),
                        desc);
                    return;
                }
                var expectedActive = expected.Length == 0 ? new HashSet<string>() : new HashSet<string> { expected[0] };
                var expectedMirrors = expected.Skip(1).ToHashSet();
                var mirrors = btns.Where(b => IsMirror(b.Button)).Select(b => b.Id).ToHashSet();
                Check(active.SetEquals(expectedActive)
                    && mirrors.SetEquals(expectedMirrors)
                    && btns.All(b => b.Button.IsEnabled && (expected.Contains(b.Id) || ReferenceEquals(b.Button.Style, idleStyle))),
                    desc);
            }
            void Complete(Task t) => Pump(t.WaitAsync(deadline.Token));
            void Reload(List<AudioAppInfo> apps) => Complete((Task)type.GetMethod("BuildAppRowsAsync", Hidden)!.Invoke(panel, [apps, cfg])!);
            Task Build(object row) => (Task)type.GetMethod("BuildRowDevicesAsync", Hidden)!.Invoke(panel, [row])!;
            void Expand(object row) { row.GetType().GetField("Expanded")!.SetValue(row, true); Complete(Build(row)); }
            Task ClickOut(object row, string id, ModifierKeys mods = default) =>
                (Task)type.GetMethod("QueuePlaybackClickAsync", Hidden)!.Invoke(panel, [row, DevBtn(row, id, EDataFlow.eRender).Tag, mods])!;
            MicWriteRequest TakeMic(int expectedPid, string expectedMic)
            {
                var read = micWrites.Reader.ReadAsync(deadline.Token).AsTask();
                Complete(read);
                var req = read.Result;
                Check(req.Pid == expectedPid && req.DeviceId == expectedMic,
                    $"mic backend receives pid={expectedPid} -> {expectedMic}");
                return req;
            }
            PcWrite TakeOut(string[] expected, string preferred)
            {
                var read = outWrites.Reader.ReadAsync(deadline.Token).AsTask();
                Complete(read);
                var w = read.Result;
                Check(w.Outputs.SequenceEqual(expected) && w.Preferred == preferred,
                    $"output backend receives [{string.Join(", ", expected)}] pref={preferred}");
                return w;
            }

            Set("_outputDisplay", outDevices);
            Set("_inputDisplay", inDevices);
            Set("_micUiOn", true);
            Set("_readPanelState", (Func<IEnumerable<int>, (Dictionary<int, (int, bool, bool)>, bool?)>)
                (ids => (ids.ToDictionary(id => id, _ => (50, false, true)), false)));
            Set("_readRowEndpoints", (Func<int, bool, (string, string)>)((pid, _) => (actualOut[pid].Primary, actualMic[pid])));
            Set("_readRowPlaybackOutputs", (Func<int, string?, HashSet<string>>)((pid, _) =>
                actualOut[pid].Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase)));
            Set("_applyRowEndpoint", (Func<AudioAppInfo, EDataFlow, string, (bool, int, string)>)((appInfo, flow, devId) =>
            {
                if (flow == EDataFlow.eCapture)
                {
                    var req = new MicWriteRequest((int)appInfo.ProcessId, devId);
                    micWrites.Writer.TryWrite(req);
                    bool ok = req.Finish.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
                    if (ok) actualMic[(int)appInfo.ProcessId] = devId;
                    return (ok, ok ? 0 : -1, ok ? "" : "Simulated mic rejection");
                }
                return (true, 0, "");
            }));
            Set("_setRowPlaybackOutputs", (Func<AudioAppInfo, string[], string?, Task<(bool, int, string)>>)(async (appInfo, desired, preferred) =>
            {
                var w = new PcWrite(desired, preferred, Stopwatch.GetTimestamp());
                outWrites.Writer.TryWrite(w);
                if (!await w.Finish.Task.WaitAsync(deadline.Token)) return (false, -1, "Simulated output rejection");
                string nextPrimary = preferred != null && desired.Contains(preferred, StringComparer.OrdinalIgnoreCase) ? preferred
                    : desired.Contains(sysOut, StringComparer.OrdinalIgnoreCase) ? sysOut : desired[0];
                actualOut[(int)appInfo.ProcessId] = (nextPrimary, desired);
                return (true, 0, "");
            }));

            try
            {
                Reload([app1, app2]);
                object row1 = rows[pid1]!;
                object row2 = rows[pid2]!;
                Expand(row1);
                Expand(row2);
                AssertFlow(row1, EDataFlow.eCapture, "PC row1 mic starts on micA", micA);
                AssertFlow(row1, EDataFlow.eRender, "PC row1 output starts on outA", outA);

                // 2a. Clicking mic button immediately highlights target mic button while backend is blocked,
                // without disabling mic buttons or changing output (eRender) buttons
                long tMic0 = Stopwatch.GetTimestamp();
                DevBtn(row1, micB, EDataFlow.eCapture).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                double micFirstMs = Stopwatch.GetElapsedTime(tMic0).TotalMilliseconds;
                timings.Add(FormattableString.Invariant($"PC-mic first-click-to-style={micFirstMs:F3} ms"));
                AssertFlow(row1, EDataFlow.eCapture, "mic click immediately highlights micB while blocked", micB);
                AssertFlow(row1, EDataFlow.eRender, "mic click leaves output buttons untouched on outA", outA);
                Check(actualMic[pid1] == micA, "actual mic stays on micA while backend is blocked");

                // 2d. BuildRowDevicesAsync readback while mic click is pending does NOT overwrite pending mic selection
                Complete(Build(row1));
                AssertFlow(row1, EDataFlow.eCapture, "BuildRowDevicesAsync readback while mic pending preserves micB", micB);
                var withExtraMic = inDevices.Append(new AudioDeviceInfo { Id = micExtra, DisplayName = micExtra, Flow = EDataFlow.eCapture }).ToList();
                Set("_inputDisplay", withExtraMic);
                Complete(Build(row1));
                AssertFlow(row1, EDataFlow.eCapture, "BuildRowDevicesAsync device rebuild while mic pending keeps micB active", micB);
                Set("_inputDisplay", inDevices);
                Complete(Build(row1));

                // 2b. Success keeps confirmed mic; failure rolls back to actual mic
                TakeMic(pid1, micB).Finish.SetResult(true);
                Complete(Field<Task>(row1, "InputChange"));
                Check(actualMic[pid1] == micB, "mic success confirms micB on backend");
                AssertFlow(row1, EDataFlow.eCapture, "mic success keeps micB highlighted", micB);

                long tMicWarm = Stopwatch.GetTimestamp();
                DevBtn(row1, micC, EDataFlow.eCapture).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                double micWarmMs = Stopwatch.GetElapsedTime(tMicWarm).TotalMilliseconds;
                timings.Add(FormattableString.Invariant($"PC-mic warm-click-to-style={micWarmMs:F3} ms"));
                ClearOsdWindow();
                TakeMic(pid1, micC).Finish.SetResult(false);
                Complete(Field<Task>(row1, "InputChange"));
                Check(actualMic[pid1] == micB, "mic failure leaves actual mic on micB");
                AssertFlow(row1, EDataFlow.eCapture, "mic failure rolls back highlight to micB", micB);
                Check(OsdVisible() && OsdText().Contains("Simulated mic rejection"), "mic failure shows OSD error");

                // 2c. Rapid mic A -> B -> C keeps latest C; old failure does not overwrite newer selection
                DevBtn(row1, micA, EDataFlow.eCapture).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TakeMic(pid1, micA).Finish.SetResult(true);
                Complete(Field<Task>(row1, "InputChange"));

                DevBtn(row1, micB, EDataFlow.eCapture).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var rapidMicB = TakeMic(pid1, micB);
                DevBtn(row1, micC, EDataFlow.eCapture).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertFlow(row1, EDataFlow.eCapture, "rapid mic A->B->C immediately highlights micC while micB is blocked", micC);
                // Old B fails! Should NOT overwrite newer C
                rapidMicB.Finish.SetResult(false);
                var rapidMicC = TakeMic(pid1, micC);
                AssertFlow(row1, EDataFlow.eCapture, "old micB failure does NOT overwrite pending micC", micC);
                rapidMicC.Finish.SetResult(true);
                Complete(Field<Task>(row1, "InputChange"));
                Check(actualMic[pid1] == micC, "rapid mic A->B->C commits micC");
                AssertFlow(row1, EDataFlow.eCapture, "rapid mic A->B->C final stays on micC", micC);

                // 2e. PC output Ctrl / Shift multi-select works simultaneously and independently without affecting mic selection, and vice versa
                // Hold output write on outA+outB while also queuing micB (blocked)
                Task outCtrlTask = ClickOut(row1, outB, ModifierKeys.Control);
                var holdOutCtrl = TakeOut([outA, outB], outA);
                DevBtn(row1, micB, EDataFlow.eCapture).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertFlow(row1, EDataFlow.eCapture, "mic click updates immediately while output Ctrl write is blocked", micB);
                AssertFlow(row1, EDataFlow.eRender, "output Ctrl multi-select stays active while mic is pending", outA, outB);
                var holdMicB = TakeMic(pid1, micB);

                // Range-select outB..outC via Shift while keeping existing outA and while micB is still blocked
                Task outShiftTask = ClickOut(row1, outC, ModifierKeys.Shift);
                AssertFlow(row1, EDataFlow.eRender, "output Shift range preserves outA and updates independently while micB is blocked", outA, outB, outC);
                AssertFlow(row1, EDataFlow.eCapture, "output Shift leaves pending micB intact", micB);

                holdOutCtrl.Finish.SetResult(true);
                TakeOut([outA, outB, outC], outA).Finish.SetResult(true);
                Complete(outShiftTask);
                AssertFlow(row1, EDataFlow.eCapture, "output completion does not disturb still-pending micB", micB);
                holdMicB.Finish.SetResult(true);
                Complete(Field<Task>(row1, "InputChange"));
                AssertFlow(row1, EDataFlow.eRender, "both output multi-select and mic confirmed independently (render)", outA, outB, outC);
                AssertFlow(row1, EDataFlow.eCapture, "both output multi-select and mic confirmed independently (capture)", micB);

                // 2f. Different PC apps (pid1, pid2) switching concurrently do not interfere
                DevBtn(row1, micA, EDataFlow.eCapture).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var p1MicReq = TakeMic(pid1, micA);
                Task p1OutTask = ClickOut(row1, outA);
                var p1OutReq = TakeOut([outA], outA);

                // Now concurrently switch pid2 mic to micC and output to outC, and complete pid2 first!
                DevBtn(row2, micC, EDataFlow.eCapture).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var p2MicReq = TakeMic(pid2, micC);
                Task p2OutTask = ClickOut(row2, outC);
                var p2OutReq = TakeOut([outC], outC);

                p2MicReq.Finish.SetResult(true);
                p2OutReq.Finish.SetResult(true);
                Complete(Task.WhenAll(Field<Task>(row2, "InputChange"), p2OutTask));
                AssertFlow(row2, EDataFlow.eCapture, "pid2 mic confirms micC independently while pid1 is blocked", micC);
                AssertFlow(row2, EDataFlow.eRender, "pid2 output confirms outC independently while pid1 is blocked", outC);
                AssertFlow(row1, EDataFlow.eCapture, "pid2 completion does not disturb pid1 pending micA", micA);
                AssertFlow(row1, EDataFlow.eRender, "pid2 completion does not disturb pid1 pending outA", outA);

                // Fail pid1 mic and succeed pid1 output -> pid2 stays untouched
                p1MicReq.Finish.SetResult(false);
                p1OutReq.Finish.SetResult(true);
                Complete(Task.WhenAll(Field<Task>(row1, "InputChange"), p1OutTask));
                AssertFlow(row1, EDataFlow.eCapture, "pid1 mic failure rolls back pid1 to micB", micB);
                AssertFlow(row1, EDataFlow.eRender, "pid1 output confirms outA", outA);
                AssertFlow(row2, EDataFlow.eCapture, "pid1 mic failure does not affect pid2 micC", micC);
                AssertFlow(row2, EDataFlow.eRender, "pid1 completion does not affect pid2 outC", outC);
            }
            finally { panel.Close(); }
        }

        // ------------------------------------------------------------------
        // 3. Unified Application Row Device Name (QuickPanelShowOutputName = true and false)
        //    across PC, Android (IMultiOutputAudioSession), and AirPlay
        // ------------------------------------------------------------------
        void CheckUnifiedRowDeviceNames()
        {
            var library = typeof(AndroidDevice).Assembly;
            var airPlayHost = typeof(App).Assembly.GetType("SonicRoute.AirPlayHost")!;
            var receiverType = library.GetType("SonicRoute.AirPlay.AirPlayReceiver")!;
            var sessionType = receiverType.GetNestedType("Session", BindingFlags.NonPublic)!;
            var airPlaySourceType = library.GetType("SonicRoute.AirPlay.AirPlayAudioOutput")!;
            var receiverField = airPlayHost.GetField("_receiver", statics)!;
            var publishAirPlay = airPlayHost.GetMethod("Publish", statics)!.CreateDelegate<Action>();

            var androidHost = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
            var phoneCache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
            var discoveryProp = phoneCache.GetProperty("Discovery", statics)!;
            var origDiscovery = discoveryProp.GetValue(null);
            var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult<IReadOnlyList<AndroidDevice>>(Array.Empty<AndroidDevice>()));
            discoveryProp.SetValue(null, discovery);
            var androidManager = (AndroidAudioManager)androidHost.GetProperty("Sources", statics)!.GetValue(null)!;
            var phoneStore = (AndroidPhoneStore)androidHost.GetProperty("Phone", statics)!.GetValue(null)!;

            string system = AudioService.SystemDefaultDeviceId;
            const string pcOutA = "name-pc-a", pcOutB = "name-pc-b";
            var androidDevice = new AndroidDevice("name-phone-serial", "PhoneModel", "device", true, "name-phone-key");
            discovery.Publish([androidDevice]);
            phoneStore.RememberDevice(androidDevice);
            phoneStore.Rename("MyPhone");
            phoneStore.UpdateOutputs(androidDevice, [AndroidOutputSink.DefaultSpeaker]);
            string phoneSpeaker = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(androidDevice), "speaker");
            string androidNative = AndroidAudioSession.SourcePhoneOutputId;
            var androidApp = new AndroidApp("cn.name.test", "Android Player", 2001, "media");

            var cfg = ConfigService.Load();
            cfg.QuickPanelUseLegacyAppRows = false;
            cfg.QuickPanelShowOutputName = true;
            cfg.QuickPanelCompactShowOutputName = false;

            var sysDefaultDev = new AudioDeviceInfo { Id = system, DisplayName = "System Default", Flow = EDataFlow.eRender };
            var devA = new AudioDeviceInfo { Id = pcOutA, DisplayName = "Speakers A", Flow = EDataFlow.eRender, IsDefault = true };
            var devB = new AudioDeviceInfo { Id = pcOutB, DisplayName = "Headphones B", Flow = EDataFlow.eRender };
            var devPhone = new AudioDeviceInfo { Id = phoneSpeaker, DisplayName = "MyPhone Speaker", Flow = EDataFlow.eRender };
            var allOutputs = new List<AudioDeviceInfo> { sysDefaultDev, devA, devB, devPhone };

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // Setup Android IMultiOutputAudioSession fixture
            bool androidReject = false;
            var androidSource = AddFixture(androidManager, androidDevice, androidApp,
                key => !androidReject && key == AndroidDeviceIdentity.Key(androidDevice) ? androidDevice : null);
            var androidSession = androidSource.Session;
            var androidOps = (SemaphoreSlim)typeof(AndroidAudioSession).GetField("_operations", Hidden)!.GetValue(androidSession)!;

            // Setup AirPlay fixture
            var apRequests = Channel.CreateUnbounded<ExternalRouteRequest>();
            bool apBlock = false;
            bool apInitCreated = false;
            Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer> apPlayerFactory = (_, endpoint, _) =>
            {
                if (!apInitCreated && endpoint == null) { apInitCreated = true; return new BlockingPcmPlayer(); }
                if (!apBlock) return new BlockingPcmPlayer();
                var req = new ExternalRouteRequest("name-ap", endpoint);
                apRequests.Writer.TryWrite(req);
                bool ok = req.Finish.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
                return new BlockingPcmPlayer(() => { if (!ok) throw new IOException("AirPlay name test failure"); });
            };
            Func<Action, IDisposable> watch = _ => new BlockingPcmPlayer();
            using var transport = new TcpClient();
            using var lifetime = new CancellationTokenSource();
            var receiver = (IAsyncDisposable)Activator.CreateInstance(receiverType, instance, null,
                ["NameFixture", true, apPlayerFactory, watch, false, false], null)!;
            var apSource = (IAsyncDisposable)Activator.CreateInstance(airPlaySourceType, instance, null,
                ["AirPlay Player", publishAirPlay, (Action)(() => { }), apPlayerFactory, watch, CancellationToken.None], null)!;
            var apSession = Activator.CreateInstance(sessionType, instance, null,
                [receiver, transport, IPAddress.Loopback, IPAddress.Loopback], null)!;
            sessionType.GetField("_stop", instance)!.SetValue(apSession, lifetime);
            sessionType.GetField("_output", instance)!.SetValue(apSession, apSource);
            receiverType.GetField("_active", instance)!.SetValue(receiver, apSession);
            receiverField.SetValue(null, receiver);
            airPlaySourceType.GetMethod("Write", instance)!.CreateDelegate<WriteAirPlaySamples>(apSource)([0.25f, -0.25f], AndroidPcmSource.SampleRate);
            string apId = (string)airPlaySourceType.GetProperty("Id", instance)!.GetValue(apSource)!;
            var apStateProp = airPlaySourceType.GetProperty("State", instance)!;

            // Setup PC app fixture
            const int pcPid = int.MaxValue - 710;
            var pcApp = new AudioAppInfo { ProcessId = pcPid, ProcessName = "name-pc-app", DisplayName = "PC Player" };
            PcRoute pcActual = new(pcOutA, [pcOutA]);
            var pcWrites = Channel.CreateUnbounded<PcWrite>();

            var panel = new QuickPanelModernWindow();
            var panelType = typeof(QuickPanelModernWindow);
            var rows = (IDictionary)panelType.GetField("_rows", Hidden)!.GetValue(panel)!;
            var androidRows = (IDictionary)panelType.GetField("_androidRows", Hidden)!.GetValue(panel)!;
            var refreshAndroidRow = panelType.GetMethod("RefreshAndroidRow", Hidden)!;
            var buildAndroidDevices = panelType.GetMethod("BuildAndroidDevices", Hidden)!;
            var refreshSourceState = panelType.GetMethod("RefreshSourceState", Hidden)!;
            var queueIncoming = panelType.GetMethod("QueueIncomingOutputAsync", Hidden)!;

            void SetPanel(string name, object val) => panelType.GetField(name, Hidden)!.SetValue(panel, val);
            static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Hidden | BindingFlags.Public)!.GetValue(obj)!;
            void Complete(Task t) => Pump(t.WaitAsync(deadline.Token));

            SetPanel("_outputDisplay", allOutputs);
            SetPanel("_readPanelState", (Func<IEnumerable<int>, (Dictionary<int, (int, bool, bool)>, bool?)>)
                (ids => (ids.ToDictionary(id => id, _ => (60, false, true)), false)));
            SetPanel("_readRowEndpoints", (Func<int, bool, (string, string)>)((_, _) => (Volatile.Read(ref pcActual).Primary, "")));
            SetPanel("_readRowPlaybackOutputs", (Func<int, string?, HashSet<string>>)((_, _) =>
                Volatile.Read(ref pcActual).Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase)));
            SetPanel("_setRowPlaybackOutputs", (Func<AudioAppInfo, string[], string?, Task<(bool, int, string)>>)(async (_, desired, preferred) =>
            {
                var w = new PcWrite(desired, preferred, Stopwatch.GetTimestamp());
                pcWrites.Writer.TryWrite(w);
                if (!await w.Finish.Task.WaitAsync(deadline.Token)) return (false, -1, "PC name test failure");
                string nextPrimary = preferred != null && desired.Contains(preferred, StringComparer.OrdinalIgnoreCase) ? preferred
                    : desired.Contains(system, StringComparer.OrdinalIgnoreCase) ? system : desired[0];
                Volatile.Write(ref pcActual, new PcRoute(nextPrimary, desired));
                return (true, 0, "");
            }));

            try
            {
                void SetupRows(bool showOutputName)
                {
                    cfg.QuickPanelShowOutputName = showOutputName;
                    Complete((Task)panelType.GetMethod("BuildAppRowsAsync", Hidden)!.Invoke(panel, [new List<AudioAppInfo> { pcApp }, cfg])!);
                    SetPanel("_outputDisplay", allOutputs);
                    refreshAndroidRow.Invoke(panel, [true]);

                    object pcR = rows[pcPid]!;
                    pcR.GetType().GetField("Expanded")!.SetValue(pcR, true);
                    Complete((Task)panelType.GetMethod("BuildRowDevicesAsync", Hidden)!.Invoke(panel, [pcR])!);

                    object andR = androidRows[androidSource.Id]!;
                    Field<ToggleButton>(andR, "Expand").IsChecked = true;
                    Field<AnimatedExpandHost>(andR, "Wrap").FinishImmediately();
                    buildAndroidDevices.Invoke(panel, [andR]);

                    object apR = androidRows[apId]!;
                    Field<ToggleButton>(apR, "Expand").IsChecked = true;
                    Field<AnimatedExpandHost>(apR, "Wrap").FinishImmediately();
                    buildAndroidDevices.Invoke(panel, [apR]);
                }

                // --- Mode A: QuickPanelShowOutputName = true ---
                SetupRows(true);
                object pcRow = rows[pcPid]!;
                object andRow = androidRows[androidSource.Id]!;
                object apRow = androidRows[apId]!;
                string RowName(object r) => Field<TextBlock>(r, "Name").Text;
                Button PcBtn(object row, string id) => Field<List<(Button Button, string Id, EDataFlow Flow)>>(row, "DeviceButtons").Single(x => x.Id == id).Button;
                Button ApBtn(object row, string id) => Field<List<(Button Button, string Id)>>(row, "Devices").Single(x => x.Id == id).Button;

                // 3a. PC with ShowOutputName = true
                Check(RowName(pcRow) == "Speakers A", "PC row initially shows actual output name 'Speakers A'");
                Task pcClickB = (Task)panelType.GetMethod("QueuePlaybackClickAsync", Hidden)!.Invoke(panel,
                    [pcRow, PcBtn(pcRow, pcOutB).Tag, ModifierKeys.None])!;
                Check(RowName(pcRow) == "Headphones B" && Volatile.Read(ref pcActual).Primary == pcOutA,
                    "PC click immediately updates row.Name.Text to 'Headphones B' without faking backend OutputId");
                var wPcB = pcWrites.Reader.ReadAsync(deadline.Token).AsTask();
                Complete(wPcB);
                // Stale background readback during pending does NOT overwrite pending name
                Complete((Task)panelType.GetMethod("BuildRowDevicesAsync", Hidden)!.Invoke(panel, [pcRow])!);
                Check(RowName(pcRow) == "Headphones B", "PC stale BuildRowDevicesAsync readback preserves pending name");
                // Failure rolls back row.Name.Text to real output name
                wPcB.Result.Finish.SetResult(false);
                Complete(pcClickB);
                Check(RowName(pcRow) == "Speakers A", "PC failure rolls back row.Name.Text to actual 'Speakers A'");
                // Multi-output primary rule: Ctrl-add pcOutB or system keeps current primary pcOutA; Ctrl+Shift promotes system · default
                var pcActiveStyle = panel.FindResource("DevButtonActive");
                var pcMirrorStyle = panel.FindResource("DevButtonMirror");
                Task pcCtrlB = (Task)panelType.GetMethod("QueuePlaybackClickAsync", Hidden)!.Invoke(panel,
                    [pcRow, PcBtn(pcRow, pcOutB).Tag, ModifierKeys.Control])!;
                Check(RowName(pcRow) == "Speakers A"
                    && ReferenceEquals(PcBtn(pcRow, pcOutA).Style, pcActiveStyle)
                    && ReferenceEquals(PcBtn(pcRow, pcOutB).Style, pcMirrorStyle),
                    "PC Ctrl-add secondary physical keeps current primary 'Speakers A' and sets DevButtonMirror");
                Task pcCtrlSys = (Task)panelType.GetMethod("QueuePlaybackClickAsync", Hidden)!.Invoke(panel,
                    [pcRow, PcBtn(pcRow, system).Tag, ModifierKeys.Control])!;
                Check(RowName(pcRow) == "Speakers A"
                    && ReferenceEquals(PcBtn(pcRow, pcOutA).Style, pcActiveStyle)
                    && ReferenceEquals(PcBtn(pcRow, system).Style, pcMirrorStyle)
                    && ReferenceEquals(PcBtn(pcRow, pcOutB).Style, pcMirrorStyle),
                    "PC Ctrl-add system default keeps current primary 'Speakers A' as DevButtonActive and adds system as DevButtonMirror");
                Task pcNativeSys = (Task)panelType.GetMethod("QueuePlaybackClickAsync", Hidden)!.Invoke(panel,
                    [pcRow, PcBtn(pcRow, system).Tag, ModifierKeys.Control | ModifierKeys.Shift])!;
                Check(RowName(pcRow) == "System Default · Speakers A"
                    && ReferenceEquals(PcBtn(pcRow, system).Style, pcActiveStyle)
                    && ReferenceEquals(PcBtn(pcRow, pcOutA).Style, pcMirrorStyle)
                    && ReferenceEquals(PcBtn(pcRow, pcOutB).Style, pcMirrorStyle),
                    "PC Ctrl+Shift-click system default immediately promotes 'System Default · Speakers A' to DevButtonActive while physical outputs become DevButtonMirror");
                var w1 = pcWrites.Reader.ReadAsync(deadline.Token).AsTask(); Complete(w1); w1.Result.Finish.SetResult(true);
                var w2 = pcWrites.Reader.ReadAsync(deadline.Token).AsTask(); Complete(w2); w2.Result.Finish.SetResult(true);
                var w3 = pcWrites.Reader.ReadAsync(deadline.Token).AsTask(); Complete(w3); w3.Result.Finish.SetResult(true);
                Complete(pcNativeSys);
                Check(RowName(pcRow) == "System Default · Speakers A"
                    && ReferenceEquals(PcBtn(pcRow, system).Style, pcActiveStyle)
                    && ReferenceEquals(PcBtn(pcRow, pcOutA).Style, pcMirrorStyle)
                    && ReferenceEquals(PcBtn(pcRow, pcOutB).Style, pcMirrorStyle),
                    "PC multi-output success reconciles with actual route");
                // Switch PC to pcOutB and confirm reconciliation
                Task pcFinalB = (Task)panelType.GetMethod("QueuePlaybackClickAsync", Hidden)!.Invoke(panel,
                    [pcRow, PcBtn(pcRow, pcOutB).Tag, ModifierKeys.None])!;
                var w4 = pcWrites.Reader.ReadAsync(deadline.Token).AsTask(); Complete(w4); w4.Result.Finish.SetResult(true);
                Complete(pcFinalB);
                Check(RowName(pcRow) == "Headphones B" && ReferenceEquals(PcBtn(pcRow, pcOutB).Style, pcActiveStyle),
                    "PC single output success reconciles row.Name.Text to 'Headphones B'");

                // 3b. Android (IMultiOutputAudioSession) with ShowOutputName = true
                Check(RowName(andRow) == "System Default · Speakers A",
                    "Android row initially shows actual System Default · Speakers A");
                if (!androidOps.Wait(0)) throw new InvalidOperationException("Failed to hold Android semaphore");
                Task andPending = Task.CompletedTask;
                try
                {
                    andPending = (Task)queueIncoming.Invoke(panel, [andRow, pcOutB, ModifierKeys.None])!;
                    Check(RowName(andRow) == "Headphones B" && androidSession.State.OutputId == null,
                        "Android click immediately updates row.Name.Text to 'Headphones B' without faking State.OutputId");
                    refreshSourceState.Invoke(panel, [andRow, false]);
                    refreshSourceState.Invoke(panel, [andRow, true]);
                    Check(RowName(andRow) == "Headphones B",
                        "Android stale RefreshSourceState while pending preserves pending name");
                }
                finally { androidOps.Release(); }
                Complete(andPending);
                Check(RowName(andRow) == "Headphones B" && androidSession.State.OutputId == pcOutB,
                    "Android success reconciles row.Name.Text with actual route");

                // Android multi-output rule: Ctrl-add SourcePhoneOutputId keeps pcOutB (lowPriorityId = SourcePhoneOutputId);
                // single click SourcePhoneOutputId shows Phone.NativeAudioOutput
                if (!androidOps.Wait(0)) throw new InvalidOperationException("Failed to hold Android semaphore");
                try
                {
                    _ = queueIncoming.Invoke(panel, [andRow, androidNative, ModifierKeys.Control])!;
                    Check(RowName(andRow) == "Headphones B",
                        "Android Ctrl-add native phone output keeps physical 'Headphones B' as primary");
                    andPending = (Task)queueIncoming.Invoke(panel, [andRow, androidNative, ModifierKeys.None])!;
                    Check(RowName(andRow) == L10n.T("Phone.NativeAudioOutput"),
                        "Android single native phone output shows localized native audio name");
                }
                finally { androidOps.Release(); }
                Complete(andPending);

                // Android failure rollback for row.Name.Text
                androidReject = true;
                if (!androidOps.Wait(0)) throw new InvalidOperationException("Failed to hold Android semaphore");
                try
                {
                    andPending = (Task)queueIncoming.Invoke(panel, [andRow, phoneSpeaker, ModifierKeys.None])!;
                    Check(RowName(andRow) == "MyPhone Speaker" && androidSession.State.OutputId == androidNative,
                        "Android pending phone speaker updates name optimistically before failure");
                }
                finally { androidOps.Release(); }
                Complete(andPending);
                androidReject = false;
                Check(RowName(andRow) == L10n.T("Phone.NativeAudioOutput"),
                    "Android failure rolls back row.Name.Text to actual native output name");

                // 3c. AirPlay with ShowOutputName = true
                apBlock = true;
                Check(RowName(apRow) == "System Default · Speakers A",
                    "AirPlay row initially shows System Default · Speakers A");
                ApBtn(apRow, pcOutB).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(RowName(apRow) == "Headphones B" && ((AndroidAudioState)apStateProp.GetValue(apSource)!).OutputId == null,
                    "AirPlay click immediately updates row.Name.Text to 'Headphones B' without faking State.OutputId");
                refreshSourceState.Invoke(panel, [apRow, false]);
                refreshSourceState.Invoke(panel, [apRow, true]);
                Check(RowName(apRow) == "Headphones B",
                    "AirPlay stale RefreshSourceState while pending preserves pending name");
                var apReqFail = apRequests.Reader.ReadAsync(deadline.Token).AsTask();
                Complete(apReqFail);
                apReqFail.Result.Finish.SetResult(false);
                Complete(Field<Task>(apRow, "PlaybackChange"));
                Check(RowName(apRow) == "System Default · Speakers A",
                    "AirPlay failure rolls back row.Name.Text to actual 'System Default · Speakers A'");

                ApBtn(apRow, pcOutB).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var apReqOk = apRequests.Reader.ReadAsync(deadline.Token).AsTask();
                Complete(apReqOk);
                apReqOk.Result.Finish.SetResult(true);
                Complete(Field<Task>(apRow, "PlaybackChange"));
                Check(RowName(apRow) == "Headphones B" && ((AndroidAudioState)apStateProp.GetValue(apSource)!).OutputId == pcOutB,
                    "AirPlay success reconciles row.Name.Text with actual route 'Headphones B'");

                // --- Mode B: QuickPanelShowOutputName = false ---
                apBlock = false;
                SetupRows(false);
                pcRow = rows[pcPid]!;
                andRow = androidRows[androidSource.Id]!;
                apRow = androidRows[apId]!;

                Check(RowName(pcRow) == "PC Player" && RowName(andRow) == "Android Player" && RowName(apRow) == "AirPlay Player",
                    "ShowOutputName=false starts with application display names across PC, Android, and AirPlay");

                Task pcOffClick = (Task)panelType.GetMethod("QueuePlaybackClickAsync", Hidden)!.Invoke(panel,
                    [pcRow, PcBtn(pcRow, pcOutA).Tag, ModifierKeys.None])!;
                Check(RowName(pcRow) == "PC Player", "ShowOutputName=false keeps PC app name while pending");
                var wOff = pcWrites.Reader.ReadAsync(deadline.Token).AsTask();
                Complete(wOff); wOff.Result.Finish.SetResult(true);
                Complete(pcOffClick);
                Check(RowName(pcRow) == "PC Player", "ShowOutputName=false keeps PC app name after commit");

                if (!androidOps.Wait(0)) throw new InvalidOperationException("Failed to hold Android semaphore");
                try
                {
                    andPending = (Task)queueIncoming.Invoke(panel, [andRow, pcOutA, ModifierKeys.None])!;
                    Check(RowName(andRow) == "Android Player", "ShowOutputName=false keeps Android app name while pending");
                }
                finally { androidOps.Release(); }
                Complete(andPending);
                Check(RowName(andRow) == "Android Player", "ShowOutputName=false keeps Android app name after commit");

                apBlock = true;
                ApBtn(apRow, pcOutA).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(RowName(apRow) == "AirPlay Player", "ShowOutputName=false keeps AirPlay app name while pending");
                var apOffReq = apRequests.Reader.ReadAsync(deadline.Token).AsTask();
                Complete(apOffReq); apOffReq.Result.Finish.SetResult(false);
                Complete(Field<Task>(apRow, "PlaybackChange"));
                Check(RowName(apRow) == "AirPlay Player", "ShowOutputName=false keeps AirPlay app name after failure");
            }
            finally
            {
                if (!Field<bool>(panel, "_isClosed")) panel.Close();
                Pump(androidManager.StopAllAsync());
                discoveryProp.SetValue(null, origDiscovery);
                receiverField.SetValue(null, null);
                receiverType.GetField("_active", instance)!.SetValue(receiver, null);
                Pump(apSource.DisposeAsync().AsTask());
                Pump(receiver.DisposeAsync().AsTask());
                publishAirPlay();
            }
        }

        // ------------------------------------------------------------------
        // 4. SystemDevCombo_SelectionChanged (PanelChangeSystemDefault = true and false)
        // ------------------------------------------------------------------
        void CheckSystemDevComboSelection()
        {
            const string idA = "sys-dev-a", idB = "sys-dev-b", idC = "sys-dev-c";
            const int pid = int.MaxValue - 720;
            var devA = new AudioDeviceInfo { Id = idA, DisplayName = "System A", Flow = EDataFlow.eRender };
            var devB = new AudioDeviceInfo { Id = idB, DisplayName = "System B", Flow = EDataFlow.eRender };
            var devC = new AudioDeviceInfo { Id = idC, DisplayName = "System C", Flow = EDataFlow.eRender };
            var sysList = new List<AudioDeviceInfo> { devA, devB, devC };

            var cfg = ConfigService.Load();
            cfg.QuickPanelUseLegacyAppRows = false;
            cfg.QuickPanelShowOutputName = true;
            cfg.QuickPanelCompactShowOutputName = false;

            string actualDefault = idA;
            var sysWrites = Channel.CreateUnbounded<SystemDefaultWriteRequest>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var panel = new QuickPanelModernWindow();
            var type = typeof(QuickPanelModernWindow);
            var combo = (ComboBox)panel.FindName("SystemDevCombo");
            var rows = (IDictionary)type.GetField("_rows", Hidden)!.GetValue(panel)!;
            void Set(string name, object val) => type.GetField(name, Hidden)!.SetValue(panel, val);
            string? GetSysDevId() => (string?)type.GetField("_systemDeviceId", Hidden)!.GetValue(panel);
            Task GetSysChangeTask() => (Task)type.GetField("_systemDefaultChangeTask", Hidden)!.GetValue(panel)!;
            void Complete(Task t) => Pump(t.WaitAsync(deadline.Token));

            Set("_setSystemDefault", (Func<EDataFlow, string, (bool, int, string)>)((_, id) =>
            {
                var req = new SystemDefaultWriteRequest(id);
                sysWrites.Writer.TryWrite(req);
                bool ok = req.Finish.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
                if (ok) Volatile.Write(ref actualDefault, id);
                return (ok, ok ? 0 : -1, ok ? "" : "Simulated SetDefault failure");
            }));
            Set("_readSystemDefaultId", (Func<EDataFlow, string?>)(_ => Volatile.Read(ref actualDefault)));
            Set("_readPanelState", (Func<IEnumerable<int>, (Dictionary<int, (int, bool, bool)>, bool?)>)
                (ids => (ids.ToDictionary(i => i, _ => (50, false, true)), false)));

            try
            {
                var appItem = new AudioAppInfo { ProcessId = pid, ProcessName = "syscombo-app", DisplayName = "SysCombo App" };
                Complete((Task)type.GetMethod("BuildAppRowsAsync", Hidden)!.Invoke(panel, [new List<AudioAppInfo> { appItem }, cfg])!);
                object row = rows[pid]!;
                TextBlock rowName = (TextBlock)row.GetType().GetField("Name", Hidden | BindingFlags.Public)!.GetValue(row)!;

                var displayOutputs = PanelDevices.WithSystemDefault(sysList, EDataFlow.eRender, cfg);
                Set("_outputDisplay", displayOutputs);
                type.GetMethod("SetSystemDevices", Hidden)!.Invoke(panel, [sysList, idA]);
                type.GetMethod("RefreshRowDeviceNames", Hidden)!.Invoke(panel, null);
                Check(ReferenceEquals(combo.SelectedItem, devA) && GetSysDevId() == idA && devA.IsDefault && !devB.IsDefault
                    && rowName.Text.Contains("System A"),
                    "SystemDevCombo initializes on System A and row name reflects default System A");

                // 4a. PanelChangeSystemDefault = false -> changes _systemDeviceId without invoking _setSystemDefault
                cfg.PanelChangeSystemDefault = false;
                combo.SelectedItem = devB;
                DrainUi();
                Check(GetSysDevId() == idB && !sysWrites.Reader.TryPeek(out _) && devA.IsDefault && !devB.IsDefault,
                    "PanelChangeSystemDefault=false updates _systemDeviceId without invoking _setSystemDefault");

                // Reset selection to A via SetSystemDevices
                type.GetMethod("SetSystemDevices", Hidden)!.Invoke(panel, [sysList, idA]);

                // 4b. PanelChangeSystemDefault = true -> failure rollback
                cfg.PanelChangeSystemDefault = true;
                ClearOsdWindow();
                combo.SelectedItem = devB;
                var failReq = sysWrites.Reader.ReadAsync(deadline.Token).AsTask();
                Complete(failReq);
                failReq.Result.Finish.SetResult(false);
                Complete(GetSysChangeTask());
                DrainUi();
                Check(ReferenceEquals(combo.SelectedItem, devA) && GetSysDevId() == idA
                    && devA.IsDefault && !devB.IsDefault && rowName.Text.Contains("System A"),
                    "PanelChangeSystemDefault=true failure rolls back SelectedItem, _systemDeviceId, IsDefault, and row names to System A");
                Check(OsdVisible() && OsdTitle() == L10n.T("Act.SetDefaultOutput") && OsdText() == L10n.T("St.SysDefaultSetFail"),
                    "SystemDevCombo failure shows failure OSD");

                // 4c. Rapid A -> B -> C ignores stale earlier results so old A/B cannot overwrite C
                combo.SelectedItem = devB;
                var rapidB = sysWrites.Reader.ReadAsync(deadline.Token).AsTask();
                Complete(rapidB);
                combo.SelectedItem = devC;
                Task taskC = GetSysChangeTask();
                Check(ReferenceEquals(combo.SelectedItem, devC) && GetSysDevId() == idC,
                    "rapid A->B->C keeps combo and _systemDeviceId on C while B is blocked");
                // B fails late -> stale version != _systemDefaultChangeVersion -> must NOT overwrite C!
                rapidB.Result.Finish.SetResult(false);
                var rapidC = sysWrites.Reader.ReadAsync(deadline.Token).AsTask();
                Complete(rapidC);
                Check(ReferenceEquals(combo.SelectedItem, devC) && GetSysDevId() == idC,
                    "late failure of B does not overwrite C");
                rapidC.Result.Finish.SetResult(true);
                Complete(taskC);
                DrainUi();
                Check(Volatile.Read(ref actualDefault) == idC && ReferenceEquals(combo.SelectedItem, devC) && GetSysDevId() == idC
                    && devC.IsDefault && !devA.IsDefault && !devB.IsDefault && rowName.Text.Contains("System C"),
                    "rapid A->B->C confirms C on SelectedItem, _systemDeviceId, IsDefault, and row names");
            }
            finally { panel.Close(); }
        }

        // ------------------------------------------------------------------
        // 5. Performance & GC check
        // ------------------------------------------------------------------
        void CheckPerformanceAndGc(List<string> timings)
        {
            var library = typeof(AndroidDevice).Assembly;
            var host = typeof(App).Assembly.GetType("SonicRoute.AirPlayHost")!;
            var receiverType = library.GetType("SonicRoute.AirPlay.AirPlayReceiver")!;
            var sessionType = receiverType.GetNestedType("Session", BindingFlags.NonPublic)!;
            var sourceType = library.GetType("SonicRoute.AirPlay.AirPlayAudioOutput")!;
            var receiverField = host.GetField("_receiver", statics)!;
            var publish = host.GetMethod("Publish", statics)!.CreateDelegate<Action>();

            string system = AudioService.SystemDefaultDeviceId;
            const string devB = "perf-b", devC = "perf-c";
            const int pid = int.MaxValue - 730;
            var cfg = ConfigService.Load();
            cfg.QuickPanelUseLegacyAppRows = false;
            cfg.QuickPanelShowOutputName = true;
            cfg.QuickPanelCompactShowOutputName = false;

            var outputs = PanelDevices.WithSystemDefault([
                new AudioDeviceInfo { Id = devB, DisplayName = "Perf B", Flow = EDataFlow.eRender },
                new AudioDeviceInfo { Id = devC, DisplayName = "Perf C", Flow = EDataFlow.eRender }
            ], EDataFlow.eRender, cfg);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var apQueue = Channel.CreateUnbounded<ExternalRouteRequest>();
            var pcQueue = Channel.CreateUnbounded<PcWrite>();
            bool block = false;
            bool initCreated = false;

            Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer> playerFactory = (_, endpoint, _) =>
            {
                if (!initCreated && endpoint == null) { initCreated = true; return new BlockingPcmPlayer(); }
                if (!block) return new BlockingPcmPlayer();
                var req = new ExternalRouteRequest("perf-ap", endpoint);
                apQueue.Writer.TryWrite(req);
                req.Finish.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
                return new BlockingPcmPlayer();
            };
            Func<Action, IDisposable> watch = _ => new BlockingPcmPlayer();
            using var transport = new TcpClient();
            using var lifetime = new CancellationTokenSource();
            var receiver = (IAsyncDisposable)Activator.CreateInstance(receiverType, instance, null,
                ["PerfFixture", true, playerFactory, watch, false, false], null)!;
            var apSource = (IAsyncDisposable)Activator.CreateInstance(sourceType, instance, null,
                ["Perf AirPlay", publish, (Action)(() => { }), playerFactory, watch, CancellationToken.None], null)!;
            var session = Activator.CreateInstance(sessionType, instance, null,
                [receiver, transport, IPAddress.Loopback, IPAddress.Loopback], null)!;
            sessionType.GetField("_stop", instance)!.SetValue(session, lifetime);
            sessionType.GetField("_output", instance)!.SetValue(session, apSource);
            receiverType.GetField("_active", instance)!.SetValue(receiver, session);
            receiverField.SetValue(null, receiver);
            sourceType.GetMethod("Write", instance)!.CreateDelegate<WriteAirPlaySamples>(apSource)([0.25f, -0.25f], AndroidPcmSource.SampleRate);
            string apId = (string)sourceType.GetProperty("Id", instance)!.GetValue(apSource)!;

            var panel = new QuickPanelModernWindow();
            var type = typeof(QuickPanelModernWindow);
            var activeStyle = panel.FindResource("DevButtonActive");
            static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, Hidden | BindingFlags.Public)!.GetValue(o)!;
            void Complete(Task t) => Pump(t.WaitAsync(deadline.Token));

            string pcActual = system;
            type.GetField("_outputDisplay", Hidden)!.SetValue(panel, outputs);
            type.GetField("_readPanelState", Hidden)!.SetValue(panel, (Func<IEnumerable<int>, (Dictionary<int, (int, bool, bool)>, bool?)>)
                (ids => (ids.ToDictionary(i => i, _ => (50, false, true)), false)));
            type.GetField("_readRowEndpoints", Hidden)!.SetValue(panel, (Func<int, bool, (string, string)>)(
                (_, _) => (Volatile.Read(ref pcActual), "")));
            type.GetField("_readRowPlaybackOutputs", Hidden)!.SetValue(panel, (Func<int, string?, HashSet<string>>)(
                (_, _) => [Volatile.Read(ref pcActual)]));
            type.GetField("_setRowPlaybackOutputs", Hidden)!.SetValue(panel,
                (Func<AudioAppInfo, string[], string?, Task<(bool, int, string)>>)(async (_, desired, preferred) =>
                {
                    var w = new PcWrite(desired, preferred, Stopwatch.GetTimestamp());
                    pcQueue.Writer.TryWrite(w);
                    await w.Finish.Task.WaitAsync(deadline.Token);
                    Volatile.Write(ref pcActual, desired[0]);
                    return (true, 0, "");
                }));

            try
            {
                var pcApp = new AudioAppInfo { ProcessId = pid, ProcessName = "perf-pc", DisplayName = "Perf PC" };
                Complete((Task)type.GetMethod("BuildAppRowsAsync", Hidden)!.Invoke(panel, [new List<AudioAppInfo> { pcApp }, cfg])!);
                type.GetField("_outputDisplay", Hidden)!.SetValue(panel, outputs);
                type.GetMethod("RefreshAndroidRow", Hidden)!.Invoke(panel, [true]);

                var apRow = ((IDictionary)type.GetField("_androidRows", Hidden)!.GetValue(panel)!)[apId]!;
                Field<ToggleButton>(apRow, "Expand").IsChecked = true;
                Field<AnimatedExpandHost>(apRow, "Wrap").FinishImmediately();
                type.GetMethod("BuildAndroidDevices", Hidden)!.Invoke(panel, [apRow]);
                var apDevs = Field<List<(Button Button, string Id)>>(apRow, "Devices");
                Button apBtnB = apDevs.Single(d => d.Id == devB).Button;
                Button apBtnC = apDevs.Single(d => d.Id == devC).Button;

                var pcRow = ((IDictionary)type.GetField("_rows", Hidden)!.GetValue(panel)!)[pid]!;
                pcRow.GetType().GetField("Expanded")!.SetValue(pcRow, true);
                Complete((Task)type.GetMethod("BuildRowDevicesAsync", Hidden)!.Invoke(panel, [pcRow])!);
                var pcDevs = Field<List<(Button Button, string Id, EDataFlow Flow)>>(pcRow, "DeviceButtons");
                Button pcBtnB = pcDevs.Single(d => d.Id == devB && d.Flow == EDataFlow.eRender).Button;
                Button pcBtnC = pcDevs.Single(d => d.Id == devC && d.Flow == EDataFlow.eRender).Button;

                // Warm up 1 click each (non-blocked) so JIT/templates are ready
                apBtnB.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Complete(Field<Task>(apRow, "PlaybackChange"));
                long pcFirstStart = Stopwatch.GetTimestamp();
                pcBtnB.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                double pcFirstMs = Stopwatch.GetElapsedTime(pcFirstStart).TotalMilliseconds;
                var firstPcW = pcQueue.Reader.ReadAsync(deadline.Token).AsTask();
                Complete(firstPcW); firstPcW.Result.Finish.SetResult(true);
                Complete(Field<Task>(pcRow, "PlaybackChange"));

                long pcWarmStart = Stopwatch.GetTimestamp();
                pcBtnC.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                double pcWarmMs = Stopwatch.GetElapsedTime(pcWarmStart).TotalMilliseconds;
                var warmPcW = pcQueue.Reader.ReadAsync(deadline.Token).AsTask();
                Complete(warmPcW); warmPcW.Result.Finish.SetResult(true);
                Complete(Field<Task>(pcRow, "PlaybackChange"));
                timings.Add(FormattableString.Invariant($"PC first-click-to-style={pcFirstMs:F3} ms warm-click-to-style={pcWarmMs:F3} ms"));

                // 10 rapid AirPlay clicks UI-thread allocation & drain check
                block = true;
                long apAllocStart = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10; i++)
                    (i % 2 == 0 ? apBtnC : apBtnB).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                long apAlloc10 = GC.GetAllocatedBytesForCurrentThread() - apAllocStart;
                for (int i = 0; i < 10; i++)
                {
                    var req = apQueue.Reader.ReadAsync(deadline.Token).AsTask();
                    Complete(req);
                    req.Result.Finish.SetResult(true);
                }
                Complete(Field<Task>(apRow, "PlaybackChange"));
                long apDrainStart = Stopwatch.GetTimestamp();
                DrainUi();
                double apDrainMs = Stopwatch.GetElapsedTime(apDrainStart).TotalMilliseconds;
                Check(ReferenceEquals(apBtnB.Style, activeStyle) && apAlloc10 < 512 * 1024 && apDrainMs < 200,
                    FormattableString.Invariant($"10 rapid AirPlay clicks settle on final choice with low allocation ({apAlloc10} B) and no Dispatcher backlog ({apDrainMs:F2} ms)"));
                timings.Add(FormattableString.Invariant($"AirPlay 10-rapid-clicks uiAlloc={apAlloc10} bytes drainMs={apDrainMs:F2} ms"));

                // 10 rapid PC clicks UI-thread allocation & drain check
                long pcAllocStart = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10; i++)
                    (i % 2 == 0 ? pcBtnB : pcBtnC).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                long pcAlloc10 = GC.GetAllocatedBytesForCurrentThread() - pcAllocStart;
                for (int i = 0; i < 10; i++)
                {
                    var w = pcQueue.Reader.ReadAsync(deadline.Token).AsTask();
                    Complete(w);
                    w.Result.Finish.SetResult(true);
                }
                Complete(Field<Task>(pcRow, "PlaybackChange"));
                long pcDrainStart = Stopwatch.GetTimestamp();
                DrainUi();
                double pcDrainMs = Stopwatch.GetElapsedTime(pcDrainStart).TotalMilliseconds;
                Check(ReferenceEquals(pcBtnC.Style, activeStyle) && pcAlloc10 < 512 * 1024 && pcDrainMs < 200,
                    FormattableString.Invariant($"10 rapid PC clicks settle on final choice with low allocation ({pcAlloc10} B) and no Dispatcher backlog ({pcDrainMs:F2} ms)"));
                timings.Add(FormattableString.Invariant($"PC 10-rapid-clicks uiAlloc={pcAlloc10} bytes drainMs={pcDrainMs:F2} ms"));
            }
            finally
            {
                panel.Close();
                receiverField.SetValue(null, null);
                receiverType.GetField("_active", instance)!.SetValue(receiver, null);
                Pump(apSource.DisposeAsync().AsTask());
                Pump(receiver.DisposeAsync().AsTask());
                publish();
            }
        }
    }
}

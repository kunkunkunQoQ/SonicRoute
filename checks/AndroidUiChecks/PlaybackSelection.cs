using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{

    private sealed record PcRoute(string Primary, string[] Outputs);

    private sealed record PcWrite(string[] Outputs, string? Preferred, long Started)
    {
        internal readonly TaskCompletionSource<bool> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static int CheckPcOptimisticPlayback(string output)
    {
        string system = AudioService.SystemDefaultDeviceId;
        const string a = "fixture-a", b = "fixture-b", c = "fixture-c", hidden = "fixture-hidden", mic = "fixture-mic";
        const int pid = int.MaxValue - 456;
        var panel = new QuickPanelModernWindow();
        var type = typeof(QuickPanelModernWindow);
        var config = ConfigService.Load();
        config.QuickPanelShowOutputName = config.QuickPanelCompactShowOutputName = false;
        config.HiddenOutputDevices.Add(hidden);
        var app = new AudioAppInfo { ProcessId = pid, ProcessName = "fixture-player", DisplayName = "Fixture Player" };
        var devices = new[] { system, a, b, c, hidden }.Select(id => new AudioDeviceInfo
            { Id = id, DisplayName = id, Flow = EDataFlow.eRender }).ToList();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var writes = Channel.CreateUnbounded<PcWrite>();
        PcRoute actual = new(a, [a]);

        PlaybackReadGate? nextRead = null;
        Set("_outputDisplay", devices);
        Set("_inputDisplay", new List<AudioDeviceInfo> { new() { Id = mic, DisplayName = mic, Flow = EDataFlow.eCapture } });
        Set("_micUiOn", true);
        Set("_readPanelState", (Func<IEnumerable<int>, (Dictionary<int, (int, bool, bool)>, bool?)>)
            (ids => (ids.ToDictionary(id => id, _ => (45, false, true)), false)));
        Set("_readRowEndpoints", (Func<int, bool, (string, string)>)((_, _) => (Volatile.Read(ref actual).Primary, mic)));
        Set("_readRowPlaybackOutputs", (Func<int, string?, HashSet<string>>)((_, _) =>
        {
            var snapshot = Volatile.Read(ref actual).Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Interlocked.Exchange(ref nextRead, null) is { } gate)
            {
                gate.Entered.TrySetResult();
                gate.Release.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
            }
            return snapshot;
        }));
        Set("_setRowPlaybackOutputs", (Func<AudioAppInfo, string[], string?, Task<(bool, int, string)>>)(async (_, desired, preferred) =>
        {
            var write = new PcWrite(desired, preferred, Stopwatch.GetTimestamp());
            writes.Writer.TryWrite(write);
            if (!await write.Finish.Task.WaitAsync(deadline.Token)) return (false, -1, "Simulated route rejection.");
            string nextPrimary = preferred != null && desired.Contains(preferred, StringComparer.OrdinalIgnoreCase) ? preferred
                : desired.Contains(system, StringComparer.OrdinalIgnoreCase) ? system : desired[0];
            Volatile.Write(ref actual, new PcRoute(nextPrimary, desired));
            return (true, 0, "");
        }));

        var rows = (System.Collections.IDictionary)type.GetField("_rows", Hidden)!.GetValue(panel)!;
        var activeStyle = panel.FindResource("DevButtonActive");
        var mirrorStyle = panel.FindResource("DevButtonMirror");
        var idleStyle = panel.FindResource("DevButton");
        const ModifierKeys ctrl = ModifierKeys.Control, shift = ModifierKeys.Shift;
        bool closed = false;
        try
        {
            Reload([app]);
            object row = rows[pid]!;
            Expand(row);

            long clicked = Stopwatch.GetTimestamp();
            Button firstButton = Device(row, b).Button;
            firstButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            double feedbackMs = Stopwatch.GetElapsedTime(clicked).TotalMilliseconds;
            AssertShown(row, "first click changes the existing output button in the same event", b);
            var first = Take([b], b);
            Check(!first.Finish.Task.IsCompleted && Volatile.Read(ref actual).Primary == a
                && ReferenceEquals(firstButton, Device(row, b).Button) && IsActive(Device(row, mic).Button),
                "blocked backend leaves the optimistic output visible and the microphone style intact");

            Complete(Build(row));
            AssertShown(row, "an old A readback while B is blocked preserves B and leaves playback buttons clickable", b);
            Set("_outputDisplay", devices.Append(new AudioDeviceInfo { Id = "new-output", DisplayName = "New", Flow = EDataFlow.eRender }).ToList());
            Complete(Build(row));
            Check(!ReferenceEquals(firstButton, Device(row, b).Button) && IsActive(Device(row, b).Button),
                "a device-list rebuild applies B to the replacement buttons while the real route is still A");
            Set("_outputDisplay", devices);
            first.Finish.SetResult(true);
            Complete(Field<Task>(row, "PlaybackChange"));
            double completedMs = Stopwatch.GetElapsedTime(clicked).TotalMilliseconds;
            double startedMs = Stopwatch.GetElapsedTime(clicked, first.Started).TotalMilliseconds;
            AssertShown(row, "success keeps the confirmed B", b);

            Seed(row, a, [a]);
            Button warmButton = Device(row, b).Button;
            long warmClick = Stopwatch.GetTimestamp();
            warmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            double warmFeedbackMs = Stopwatch.GetElapsedTime(warmClick).TotalMilliseconds;
            var firstOfTwo = Take([b], b);
            var toC = Click(row, c);
            AssertShown(row, "A to B to C displays C before either route completes", c);
            Check(!writes.Reader.TryPeek(out _), "the C write is held behind B");
            firstOfTwo.Finish.SetResult(true);
            var lastOfTwo = Take([c], c);
            AssertShown(row, "B completion cannot repaint C while C is blocked", c);
            var stale = BlockRead();
            Task staleBuild = Build(row);
            Complete(stale.Entered.Task);
            lastOfTwo.Finish.SetResult(true);
            Complete(toC);
            stale.Release.TrySetResult();
            Complete(staleBuild);
            Check(Volatile.Read(ref actual).Primary == c, "A to B to C commits C on the backend");
            AssertShown(row, "a structurally valid B snapshot arriving after C confirmation cannot repaint C", c);

            Seed(row, a, [a]);
            _ = Click(row, b);
            var rejected = Take([b], b);
            _ = Click(row, a);
            var repeated = Click(row, a);
            rejected.Finish.SetResult(false);
            var backToA = Take([a], a);
            AssertShown(row, "A to B to A, including a repeat, keeps A visible after intermediate failure", a);
            backToA.Finish.SetResult(true);
            Take([a], a).Finish.SetResult(true);
            Complete(repeated);
            var failed = Click(row, b);
            Take([b], b).Finish.SetException(new IOException("Simulated WASAPI failure."));
            Complete(failed);
            AssertShown(row, "latest failure restores the actual A output", a);

            var concurrentChange = Click(row, b);
            var delayedCommit = Take([b], b);
            var delayedConfirmation = BlockRead();
            delayedCommit.Finish.SetResult(true);
            Complete(delayedConfirmation.Entered.Task);
            Seed(row, c, [c]);
            AssertShown(row, "new backend snapshots do not remove a pending visual selection", b);
            delayedConfirmation.Release.TrySetResult();
            Complete(concurrentChange);
            AssertShown(row, "a later external route read wins over an older completion readback", c);

            Seed(row, a, [a, hidden]);
            _ = Click(row, b, ctrl);
            AssertShown(row, "Ctrl adds B while retaining A and an invisible confirmed output", a, b);
            _ = Click(row, b, ctrl);
            AssertShown(row, "Ctrl removes the pending B without dropping hidden playback", a);
            _ = Click(row, c, ctrl | shift);
            AssertShown(row, "Ctrl+Shift adds unselected C as native while keeping A and hidden without range fill", c, a);
            _ = Click(row, a, ctrl | shift);
            AssertShown(row, "Ctrl+Shift switches native routing to already-selected physical device A", a, c);
            Take([a, b, hidden], a).Finish.SetResult(true);
            Take([a, hidden], a).Finish.SetResult(true);
            Take([a, c, hidden], c).Finish.SetResult(true);
            Take([a, c, hidden], a).Finish.SetResult(true);

            // Rapid queued Ctrl+Shift -> Ctrl preserves PreferredPrimary without flicker.
            _ = Click(row, b, ctrl | shift);
            AssertShown(row, "Ctrl+Shift adds B as native while keeping A, C and hidden", b, a, c);
            var rapidCtrl = Click(row, system, ctrl);
            AssertShown(row, "rapid Ctrl after pending Ctrl+Shift keeps B as native without flicker", b, system, a, c);
            Take([a, b, c, hidden], b).Finish.SetResult(true);
            Take([system, a, b, c, hidden], b).Finish.SetResult(true);
            Complete(rapidCtrl);
            AssertShown(row, "confirmed multi-output keeps B native with SystemDefault and mirrors", b, system, a, c);

            // Switch native between physical and SystemDefault via Ctrl+Shift.
            var toSystemNative = Click(row, system, ctrl | shift);
            AssertShown(row, "Ctrl+Shift switches native routing from physical B to SystemDefault", system, a, b, c);
            Take([system, a, b, c, hidden], system).Finish.SetResult(true);
            Complete(toSystemNative);
            Check(Volatile.Read(ref actual).Primary == system, "backend sets SystemDefault as primary when Ctrl+Shift-clicked");

            var fromSystemNative = Click(row, c, ctrl | shift);
            AssertShown(row, "Ctrl+Shift switches native routing from SystemDefault to physical C", c, system, a, b);
            Take([system, a, b, c, hidden], c).Finish.SetResult(true);
            Complete(fromSystemNative);
            Check(Volatile.Read(ref actual).Primary == c, "backend sets physical C as primary while keeping SystemDefault in mirrors");

            // Shift keeps existing visible + hidden selection outside the range and adds contiguous range.
            Seed(row, system, [system, hidden]);
            _ = Click(row, b, ctrl);
            var shiftKeep = Click(row, c, shift);
            AssertShown(row, "Shift keeps existing SystemDefault and hidden output while adding B..C range", system, b, c);
            Take([system, b, hidden], system).Finish.SetResult(true);
            Take([system, b, c, hidden], system).Finish.SetResult(true);
            Complete(shiftKeep);
            Check(Volatile.Read(ref actual).Outputs.Contains(hidden, StringComparer.OrdinalIgnoreCase),
                "Shift preserves hidden output alongside range");

            // Ctrl adding SystemDefault keeps physical primary A; plain click clears mirrors.
            Seed(row, a, [a]);
            var systemAndPhysical = Click(row, system, ctrl);
            AssertShown(row, "Ctrl combines system default as mirror while keeping physical A as native", a, system);
            Take([system, a], a).Finish.SetResult(true);
            Complete(systemAndPhysical);
            Check(Volatile.Read(ref actual).Primary == a, "backend keeps physical A as primary when SystemDefault is added with Ctrl");
            var physical = Click(row, a);
            Take([a], a).Finish.SetResult(true);
            Complete(physical);
            AssertShown(row, "a plain click replaces system default and all mirrors with the selected physical output", a);

            // Ctrl+Shift on a phone output is ignored.
            string phoneId = AndroidOutputIdentity.FormatDeviceId("fixture-phone", "speaker");
            Set("_outputDisplay", devices.Append(new AudioDeviceInfo { Id = phoneId, DisplayName = "Phone Speaker", Flow = EDataFlow.eRender }).ToList());
            Complete(Build(row));
            Task ignoredPhone = Click(row, phoneId, ctrl | shift);
            Check(ignoredPhone.IsCompletedSuccessfully && !writes.Reader.TryPeek(out _),
                "Ctrl+Shift click on a phone output is ignored without queueing a write");
            AssertShown(row, "ignored Ctrl+Shift click on phone output leaves A native unchanged", a);
            Set("_outputDisplay", devices);
            Complete(Build(row));

            var disconnect = BlockRead();
            Task removedTarget = Click(row, c);
            Complete(disconnect.Entered.Task);
            Set("_outputDisplay", devices.Where(d => d.Id != c).ToList());
            Check(Click(row, c).IsCompletedSuccessfully, "a stale button cannot queue another click on a disconnected device");
            Complete(Build(row));
            Check(!Buttons(row).Any(item => item.Id == c), "a structure refresh removes the disconnected output while its request is pending");
            disconnect.Release.TrySetResult();
            Complete(removedTarget);
            Check(!writes.Reader.TryPeek(out _), "a removed target is not passed to the backend");
            AssertShown(row, "target removal restores the confirmed route", a);
            Set("_outputDisplay", devices);
            Complete(Build(row));

            var retiredRead = BlockRead();
            Task retiredClick = Click(row, b);
            Complete(retiredRead.Entered.Task);
            Reload([]);
            Reload([app]);
            object replacement = rows[pid]!;
            Expand(replacement);
            Check(!ReferenceEquals(row, replacement), "the application row was retired and recreated");
            retiredRead.Release.TrySetResult();
            Complete(retiredClick);
            Check(!writes.Reader.TryPeek(out _), "a replaced row cannot send its old request after readback");
            AssertShown(replacement, "the old row cannot repaint the replacement", a);
            Seed(replacement, c, [c]);
            AssertShown(replacement, "external route readback updates the confirmed selection", c);

            var closeRead = BlockRead();
            Task closeClick = Click(replacement, a);
            Complete(closeRead.Entered.Task);
            panel.Close();
            closed = true;
            closeRead.Release.TrySetResult();
            Complete(closeClick);
            Check(!writes.Reader.TryPeek(out _) && Volatile.Read(ref actual).Primary == c,
                "closing the panel retires its queued output operation");

            string timing = FormattableString.Invariant($"Simulated WPF first-click-to-style={feedbackMs:F2} ms warmed-click-to-style={warmFeedbackMs:F2} ms first-click-to-write-start={startedMs:F2} ms first-click-to-confirmed={completedMs:F2} ms");
            Console.WriteLine(timing);
            File.WriteAllText(Path.Combine(output, "pc-playback-timing.log"), timing + Environment.NewLine);

            CheckSystemDefaultMirrorResolution();
            CheckThemeNativeMirrorContrastAndPreviews(output);
            return 0;
        }
        finally { if (!closed) panel.Close(); }

        void Set(string name, object value) => type.GetField(name, Hidden)!.SetValue(panel, value);
        static T Field<T>(object row, string name) => (T)row.GetType().GetField(name, Hidden | BindingFlags.Public)!.GetValue(row)!;
        static List<(Button Button, string Id, EDataFlow Flow)> Buttons(object row) => Field<List<(Button, string, EDataFlow)>>(row, "DeviceButtons");
        static (Button Button, string Id, EDataFlow Flow) Device(object row, string id) => Buttons(row).Single(item => item.Id == id);
        bool IsActive(Button button) => ReferenceEquals(button.Style, activeStyle);
        bool IsMirror(Button button) => ReferenceEquals(button.Style, mirrorStyle);
        bool IsIdle(Button button) => ReferenceEquals(button.Style, idleStyle);
        void AssertShown(object row, string description, string? native, params string[] mirrors)
        {
            var render = Buttons(row).Where(item => item.Flow == EDataFlow.eRender).ToList();
            var actualActive = render.Where(item => IsActive(item.Button)).Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actualMirrors = render.Where(item => IsMirror(item.Button)).Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedActive = native == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(new[] { native }, StringComparer.OrdinalIgnoreCase);
            var expectedMirrors = mirrors.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check(actualActive.SetEquals(expectedActive)
                && actualMirrors.SetEquals(expectedMirrors)
                && render.All(item => item.Button.IsEnabled
                    && (actualActive.Contains(item.Id) || actualMirrors.Contains(item.Id) || IsIdle(item.Button))),
                description);
        }
        void Complete(Task task) => Pump(task.WaitAsync(deadline.Token));
        void Reload(List<AudioAppInfo> apps) => Complete((Task)type.GetMethod("BuildAppRowsAsync", Hidden)!.Invoke(panel, [apps, config])!);
        Task Build(object row) => (Task)type.GetMethod("BuildRowDevicesAsync", Hidden)!.Invoke(panel, [row])!;
        void Expand(object row) { row.GetType().GetField("Expanded")!.SetValue(row, true); Complete(Build(row)); }
        Task Click(object row, string id, ModifierKeys modifiers = default) =>
            (Task)type.GetMethod("QueuePlaybackClickAsync", Hidden)!.Invoke(panel, [row, Device(row, id).Button.Tag, modifiers])!;
        void Seed(object row, string primary, string[] outputs)
        {
            Volatile.Write(ref actual, new PcRoute(primary, outputs));
            Complete(Build(row));
        }
        PlaybackReadGate BlockRead()
        {
            var gate = new PlaybackReadGate();
            Volatile.Write(ref nextRead, gate);
            return gate;
        }
        PcWrite Take(string[] expected, string preferred)
        {
            var receive = writes.Reader.ReadAsync(deadline.Token).AsTask();
            Complete(receive);
            var write = receive.Result;
            Check(write.Outputs.SequenceEqual(expected) && write.Preferred == preferred,
                "FIFO writes [" + string.Join(", ", expected) + "] with preferred output " + preferred);
            return write;
        }
    }

    private sealed class PlaybackReadGate
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static void CheckSystemDefaultMirrorResolution()
    {
        var mirrors = typeof(App).Assembly.GetType("SonicRoute.LocalAppOutputMirrors", true)!;
        var resolveOutputs = mirrors.GetMethod("ResolveOutputs", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<string[], bool, string[]>>();
        string system = AudioService.SystemDefaultDeviceId;
        Check(resolveOutputs([system], true).SequenceEqual([system]),
            "LocalAppOutputMirrors.ResolveOutputs accepts SystemDefaultDeviceId when default render output is active");
    }

    private static void CheckThemeNativeMirrorContrastAndPreviews(string output)
    {
        static double Luminance(System.Windows.Media.Color color)
        {
            static double Linear(byte channel)
            {
                double value = channel / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        static double ContrastRatio(System.Windows.Media.Color a, System.Windows.Media.Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }
        static System.Windows.Media.Color ResourceColor(string key) =>
            ((System.Windows.Media.SolidColorBrush)Application.Current.Resources[key]).Color;

        try
        {
            foreach (string mode in new[] { "light", "dark" })
            {
                foreach (string accent in new[] { "#2F80ED", "#22C55E", "#EC4899", "#00FFFF", "#1E3A8A" })
                {
                    ThemeService.Apply(mode, accent);
                    var nativeBg = ResourceColor("Theme.DeviceNativeBg");
                    var nativeFg = ResourceColor("Theme.DeviceNativeFg");
                    var overlay = ResourceColor("Theme.Overlay");
                    var mirrorBorder = ResourceColor("Theme.DeviceMirrorBorder");
                    var accentColor = ResourceColor("Theme.Accent");
                    double contrast = ContrastRatio(nativeFg, nativeBg);
                    Check(nativeBg != overlay && mirrorBorder != accentColor && contrast >= 4.5,
                        FormattableString.Invariant($"Theme {mode} {accent}: nativeBg!=overlay, mirrorBorder!=accent, contrast={contrast:F2}>=4.5"));
                }
            }

            RenderQuickPanelNativeMirrorPreviews(output);
        }
        finally { ThemeService.Apply("light", "#00CBE9"); }
    }

    private static void RenderQuickPanelNativeMirrorPreviews(string output)
    {
        string system = AudioService.SystemDefaultDeviceId;
        const string game = "preview-game", chat = "preview-chat", speakers = "preview-speakers";
        string phoneNative = AndroidOutputIdentity.FormatDeviceId("preview-phone", AndroidOutputSink.Current.Key);
        string phoneSpeaker = AndroidOutputIdentity.FormatDeviceId("preview-phone", "speaker");
        const int previewPid = int.MaxValue - 457;
        const int secondaryPid = int.MaxValue - 458;

        var previewDevices = new List<AudioDeviceInfo>
        {
            new() { Id = system, DisplayName = "系统输出", Flow = EDataFlow.eRender },
            new() { Id = game, DisplayName = "游戏", Flow = EDataFlow.eRender, IsDefault = true },
            new() { Id = chat, DisplayName = "聊天", Flow = EDataFlow.eRender },
            new() { Id = speakers, DisplayName = "扬声器", Flow = EDataFlow.eRender },
            new() { Id = phoneNative, DisplayName = "手机原生输出", Flow = EDataFlow.eRender },
            new() { Id = phoneSpeaker, DisplayName = "小米 扬声器", Flow = EDataFlow.eRender },
        };
        var previewApps = new List<AudioAppInfo>
        {
            new() { ProcessId = previewPid, ProcessName = "preview-game-app", DisplayName = "游戏语音" },
            new() { ProcessId = secondaryPid, ProcessName = "preview-browser", DisplayName = "浏览器" },
        };
        var previewConfig = new AppConfig
        {
            QuickPanelUseLegacyAppRows = false,
            QuickPanelShowOutputName = true,
            QuickPanelCompactShowOutputName = false,
        };

        var preview = new QuickPanelModernWindow();
        var type = typeof(QuickPanelModernWindow);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            void SetPreview(string name, object value) => type.GetField(name, Hidden)!.SetValue(preview, value);
            SetPreview("_outputDisplay", previewDevices);
            SetPreview("_micUiOn", false);
            SetPreview("_readPanelState", (Func<IEnumerable<int>, (Dictionary<int, (int, bool, bool)>, bool?)>)
                (ids => (ids.ToDictionary(id => id, id => id == previewPid ? (72, false, true) : (48, false, true)), false)));
            SetPreview("_readRowEndpoints", (Func<int, bool, (string, string)>)((pid, _) =>
                (pid == previewPid ? game : system, "")));
            SetPreview("_readRowPlaybackOutputs", (Func<int, string?, HashSet<string>>)((pid, _) =>
                pid == previewPid
                    ? new HashSet<string>(new[] { system, game, chat, speakers }, StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(new[] { system }, StringComparer.OrdinalIgnoreCase)));

            SetPreview("_suppressDevCombo", true);
            try
            {
                var combo = (ComboBox)preview.FindName("SystemDevCombo");
                combo.ItemsSource = previewDevices.Where(d => d.Id == game || d.Id == chat || d.Id == speakers).ToList();
                combo.SelectedIndex = 0;
                ((Slider)preview.FindName("VolumeSlider")).Value = 68;
                ((TextBlock)preview.FindName("VolumePercentText")).Text = "68%";
            }
            finally { SetPreview("_suppressDevCombo", false); }

            Pump(((Task)type.GetMethod("BuildAppRowsAsync", Hidden)!.Invoke(preview, [previewApps, previewConfig])!).WaitAsync(deadline.Token));
            var rows = (System.Collections.IDictionary)type.GetField("_rows", Hidden)!.GetValue(preview)!;
            object row = rows[previewPid]!;
            row.GetType().GetField("Expanded")!.SetValue(row, true);
            Pump(((Task)type.GetMethod("BuildRowDevicesAsync", Hidden)!.Invoke(preview, [row])!).WaitAsync(deadline.Token));
            ((AnimatedExpandHost)row.GetType().GetField("ExpandWrap")!.GetValue(row)!).SetExpanded(true, false);
            ((System.Windows.Media.RotateTransform)row.GetType().GetField("ExpandArrow")!.GetValue(row)!).Angle = 180;

            var btns = (List<(Button Button, string Id, EDataFlow Flow)>)row.GetType().GetField("DeviceButtons")!.GetValue(row)!;
            var activeStyle = preview.FindResource("DevButtonActive");
            var mirrorStyle = preview.FindResource("DevButtonMirror");
            var idleStyle = preview.FindResource("DevButton");
            Check(btns.Count == 6
                && ReferenceEquals(btns.Single(b => b.Id == game).Button.Style, activeStyle)
                && btns.Where(b => b.Id == system || b.Id == chat || b.Id == speakers).All(b => ReferenceEquals(b.Button.Style, mirrorStyle))
                && btns.Where(b => b.Id == phoneNative || b.Id == phoneSpeaker).All(b => ReferenceEquals(b.Button.Style, idleStyle)),
                "preview row renders 6 output buttons with native DevButtonActive, 3 DevButtonMirror, and 2 DevButton");

            var previews = new[]
            {
                ("preview-light-cyan.png", "light", "#00FFFF"),
                ("preview-dark-cyan.png", "dark", "#00FFFF"),
                ("preview-light-blue.png", "light", "#2F80ED"),
                ("preview-dark-blue.png", "dark", "#2F80ED"),
            };
            foreach (var (fileName, mode, accent) in previews)
            {
                ThemeService.Apply(mode, accent);
                DrainUi();
                string filePath = Path.Combine(output, fileName);
                Render((FrameworkElement)preview.Content, 372, 450, filePath);
                Check(File.Exists(filePath), "rendered " + fileName);
            }
        }
        finally { preview.Close(); }
    }

    private static void CheckPlaybackRange()
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var select = typeof(QuickPanelModernWindow).GetMethod("SelectPlaybackRange", statics)!
            .CreateDelegate<Func<string[], HashSet<string>, string, string?, ModifierKeys, string[]>>();
        string[] order = ["System", "Headset", "Speakers", "Phone"];
        string[] Pick(string[] selected, string clicked, string? anchor, ModifierKeys modifiers) =>
            select(order, new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase), clicked, anchor, modifiers);
        Check(Pick(["System"], "Phone", null, ModifierKeys.Control).SequenceEqual(["System", "Phone"])
            && Pick(["System", "Phone"], "System", "Phone", ModifierKeys.Control).SequenceEqual(["Phone"])
            && Pick(["Headset", "Phone"], "System", "Phone", ModifierKeys.None).SequenceEqual(["System"]),
            "Ctrl toggles only the clicked output and a plain click replaces the selection");
        Check(Pick(["System", "Phone", "Hidden"], "Headset", "Phone", ModifierKeys.Shift)
                .SequenceEqual(["System", "Headset", "Speakers", "Phone", "Hidden"])
            && Pick(["Headset"], "Phone", null, ModifierKeys.Shift).SequenceEqual(["Headset", "Speakers", "Phone"])
            && Pick(["System", "Speakers"], "Phone", "Headset", ModifierKeys.Shift)
                .SequenceEqual(["System", "Headset", "Speakers", "Phone"]),
            "Shift preserves existing visible/hidden selections and adds the full inclusive range in either direction");
        Check(Pick(["System", "Speakers", "Hidden"], "Phone", "System", ModifierKeys.Control | ModifierKeys.Shift)
                .SequenceEqual(["System", "Speakers", "Phone", "Hidden"])
            && Pick(["System", "Speakers"], "Speakers", "System", ModifierKeys.Control | ModifierKeys.Shift)
                .SequenceEqual(["System", "Speakers"]),
            "Ctrl+Shift preserves existing visible/hidden selections and adds only the clicked output without filling range");
    }
}

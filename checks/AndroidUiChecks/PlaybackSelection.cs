using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SonicRoute;
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
            Volatile.Write(ref actual, new PcRoute(desired.Contains(system) ? system : preferred ?? desired[0], desired));
            return (true, 0, "");
        }));

        var rows = (System.Collections.IDictionary)type.GetField("_rows", Hidden)!.GetValue(panel)!;
        var activeStyle = panel.FindResource("DevButtonActive");
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
            AssertShown(row, "Ctrl+Shift uses the pending anchor and retains A outside the range", a, b, c);
            _ = Click(row, c, shift);
            AssertShown(row, "Shift replaces the selection with the continuous B through C range", b, c);
            var systemAndPhysical = Click(row, system, ctrl);
            AssertShown(row, "Ctrl can combine system default with the pending physical outputs", system, b, c);
            Take([a, b, hidden], a).Finish.SetResult(true);
            Take([a, hidden], a).Finish.SetResult(true);
            Take([a, b, c, hidden], a).Finish.SetResult(true);
            Take([b, c], b).Finish.SetResult(true);
            Take([system, b, c], b).Finish.SetResult(true);
            Complete(systemAndPhysical);
            Check(Volatile.Read(ref actual).Primary == system, "the backend still receives all mirrors and chooses system default as primary");
            var physical = Click(row, a);
            Take([a], a).Finish.SetResult(true);
            Complete(physical);
            AssertShown(row, "a plain click replaces system default and all mirrors with the selected physical output", a);

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
            return 0;
        }
        finally { if (!closed) panel.Close(); }

        void Set(string name, object value) => type.GetField(name, Hidden)!.SetValue(panel, value);
        static T Field<T>(object row, string name) => (T)row.GetType().GetField(name, Hidden | BindingFlags.Public)!.GetValue(row)!;
        static List<(Button Button, string Id, EDataFlow Flow)> Buttons(object row) => Field<List<(Button, string, EDataFlow)>>(row, "DeviceButtons");
        static (Button Button, string Id, EDataFlow Flow) Device(object row, string id) => Buttons(row).Single(item => item.Id == id);
        bool IsActive(Button button) => ReferenceEquals(button.Style, activeStyle);
        void AssertShown(object row, string description, params string[] ids) => Check(Buttons(row)
            .Where(item => item.Flow == EDataFlow.eRender && IsActive(item.Button)).Select(item => item.Id).ToHashSet().SetEquals(ids)
            && Buttons(row).Where(item => item.Flow == EDataFlow.eRender).All(item => item.Button.IsEnabled), description);
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
}

using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private sealed record HotkeyWrite(string App, string Target, long Started)
    {
        internal readonly TaskCompletionSource<bool> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? ConfirmedTarget;
    }

    private static int CheckHotkeyOutputOsd(App app, string output, bool profileStartup)
    {
        const string a = "device-a", b = "device-b", c = "device-c", d = "device-d", hidden = "device-hidden";
        const int playerPid = 101234, otherPid = 101235, phoneOne = 101236, phoneTwo = 101237;
        string system = AudioService.SystemDefaultDeviceId;
        var player = NewApp(playerPid, "Player");
        var other = NewApp(otherPid, "Other");
        var firstPhone = NewApp(0, "Phone 1", "android:fixture-one");
        var secondPhone = NewApp(0, "Phone 2", "android:fixture-two");
        Dictionary<int, AudioAppInfo> apps = new() { [playerPid] = player, [otherPid] = other, [phoneOne] = firstPhone, [phoneTwo] = secondPhone };
        var routes = new ConcurrentDictionary<string, string>(apps.Values.ToDictionary(item => item.Identity, _ => a));
        var available = new ConcurrentDictionary<string, bool>(apps.Values.ToDictionary(item => item.Identity, _ => true));
        string? unconfirmedExternal = null;
        var allDevices = new[] { a, b, hidden, c, d }.Select(id => new AudioDeviceInfo { Id = id, DisplayName = id, Flow = EDataFlow.eRender }).ToList();
        var devices = allDevices;
        var writes = Channel.CreateUnbounded<HotkeyWrite>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var resolve = new PlaybackReadGate();
        PlaybackReadGate? discovery = null;
        var config = ConfigService.Load();
        config.DefaultAppMode = "recent";
        config.OsdVolumeVisualEnabled = true;
        config.HiddenOutputDevices.Add(hidden);
        config.DeviceNames[b] = "Renamed B";
        config.DeviceNames[system] = "Follow Windows";
        config.QuickPanelShowOutputName = config.QuickPanelCompactShowOutputName = false;

        var trayType = typeof(App).Assembly.GetType("SonicRoute.TrayWheelService")!;
        using var tray = (IDisposable)Activator.CreateInstance(trayType, new object?[] { null })!;
        object osd = Read<object>(tray, "_osdService");
        var osdTypeInfo = osd.GetType();
        var formatSwitchingTitle = osdTypeInfo.GetMethod("FormatDeviceSwitchingTitle", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        SetApp("_trayWheel", tray);
        SetApp("_resolveHotkeyOutputApp", (Func<int, IntPtr, AudioAppInfo?>)((pid, _) =>
        {
            if (Interlocked.Exchange(ref discovery, null) is { } gate)
            {
                gate.Entered.SetResult();
                gate.Release.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
            }
            return apps.TryGetValue(pid, out var item) && available[item.Identity] ? item : null;
        }));
        SetApp("_readHotkeyOutputDevices", (Func<List<AudioDeviceInfo>>)(() => Volatile.Read(ref devices)));
        SetApp("_readHotkeyOutput", (Func<AudioAppInfo, (string?, bool)>)(item =>
            (routes[item.Identity] == system ? null : routes[item.Identity], Volatile.Read(ref unconfirmedExternal) != item.Identity)));
        SetApp("_hotkeyOutputAppAvailable", (Func<AudioAppInfo, bool>)(item => available[item.Identity]));
        SetApp("_applyHotkeyOutput", (Func<AudioAppInfo, EDataFlow, string, (bool, int, string)>)((item, _, target) =>
        {
            var write = new HotkeyWrite(item.Identity, target, Stopwatch.GetTimestamp());
            writes.Writer.TryWrite(write);
            bool ok = write.Finish.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
            if (ok) routes[item.Identity] = write.ConfirmedTarget ?? target;
            return (ok, ok ? 0 : -1, ok ? "" : "fixture rejected the route");
        }));

        try
        {
            CheckLocalizationFormats();

            // 1. Cold startup with slow (>45ms) discovery & apply:
            //    - Audio discovery starts immediately without being blocked by cold window creation.
            //    - Within 45ms, OSD is NOT created/shown yet.
            //    - After 45ms, generic pending OSD appears, then upgrades in-place to "Player · 切换中".
            //    - 500ms min duration is measured from actual pending visibility, not key press or window creation.
            Volatile.Write(ref discovery, resolve);
            double visualTreeMs = 0;
            long nativeWindow = 0;
            if (profileStartup)
            {
                long started = Stopwatch.GetTimestamp();
                Invoke(osd, "EnsureWindow");
                visualTreeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Window()!.SourceInitialized += (_, _) => nativeWindow = Stopwatch.GetTimestamp();
            }

            long pressed = Stopwatch.GetTimestamp(), handling = 0, notified = 0, rendered = 0, opaque = 0;
            var firstRender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstOpacity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CompositionTarget.Rendering += FirstOpacity;
            Task? first = null;
            Complete(app.Dispatcher.BeginInvoke(new Action(() =>
            {
                handling = Stopwatch.GetTimestamp();
                first = Press(playerPid);
                notified = Stopwatch.GetTimestamp();
                Check(!Visible(), "within 45ms of hotkey press, no pending OSD window is shown yet");
            })).Task);
            Complete(resolve.Entered.Task);
            // Wait past the 45ms fast-completion window while discovery stays blocked.
            Pump(WaitUntil(() => Visible() && Text() == L10n.T("Ov.SwitchingOutput")));
            var firstWindow = Window()!;
            firstWindow.ContentRendered += (_, _) =>
            {
                if (firstRender.Task.IsCompleted) return;
                rendered = Stopwatch.GetTimestamp();
                firstRender.SetResult();
            };
            if (!profileStartup)
            {
                // ContentRendered may already have fired or fire right after first layout.
                Pump(Task.WhenAny(firstRender.Task, Task.Delay(50)));
                if (rendered == 0) rendered = Stopwatch.GetTimestamp();
            }
            else Complete(firstRender.Task);
            Complete(firstOpacity.Task);
            Check(!writes.Reader.TryPeek(out _) && !first!.IsCompleted && Text() == L10n.T("Ov.SwitchingOutput"),
                "after 45ms with blocked discovery, the WPF window renders the generic pending notification while the panel is closed");

            var textBlock = Read<TextBlock>(osd, "_text");
            var titleBlock = Read<TextBlock>(osd, "_title");
            var textProperty = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            long targetShown = 0;
            EventHandler targetChanged = (_, _) =>
            {
                if (textBlock.Text == "🔊 Renamed B" && Title() == FormatSwitching(player.Label))
                    targetShown = Stopwatch.GetTimestamp();
            };
            textProperty.AddValueChanged(textBlock, targetChanged);
            HotkeyWrite toB;
            try
            {
                resolve.Release.SetResult();
                toB = Take(player, b);
            }
            finally { textProperty.RemoveValueChanged(textBlock, targetChanged); }
            Pending(player.Label, "Renamed B");
            Check(ReferenceEquals(Window(), firstWindow), "updating from generic to target pending reuses the same OSD window");
            Check(routes[player.Identity] == a && !toB.Finish.Task.IsCompleted && targetShown > 0 && targetShown <= toB.Started,
                "the localized pending title and renamed target appear before the backend write starts while real route is A");
            double targetMs = Stopwatch.GetElapsedTime(pressed, targetShown).TotalMilliseconds;

            // 2. Cold/slow 100ms backend completion honors >= 500ms min visible time for "切换中":
            long coldFinishAt = Stopwatch.GetTimestamp();
            toB.Finish.SetResult(true);
            Complete(first!);
            Check(routes[player.Identity] == b, "backend route updates immediately without waiting for OSD 500ms hold");
            // Wait for confirmed final OSD (Title == player.Label).
            long confirmedAt = 0;
            EventHandler confirmWatcher = (_, _) =>
            {
                if (Title() == player.Label && Text() == "🔊 Renamed B" && confirmedAt == 0)
                    confirmedAt = Stopwatch.GetTimestamp();
            };
            textProperty.AddValueChanged(titleBlock, confirmWatcher);
            try
            {
                if (Title() == player.Label) confirmedAt = Stopwatch.GetTimestamp();
                else Pump(WaitUntil(() => Title() == player.Label && Text() == "🔊 Renamed B", 2500));
                if (confirmedAt == 0) confirmedAt = Stopwatch.GetTimestamp();
            }
            finally { textProperty.RemoveValueChanged(titleBlock, confirmWatcher); }
            double minVisibleObservedMs = Stopwatch.GetElapsedTime(targetShown, confirmedAt).TotalMilliseconds;
            Check(minVisibleObservedMs >= 490, FormattableString.Invariant($"slow pending OSD stayed visible >= 500ms ({minVisibleObservedMs:F1} ms)"));
            Check(ReferenceEquals(Window(), firstWindow), "pending-to-confirmed transition reuses the existing OSD window");

            // 3. Controlled latency matrix on warm OSD:
            //    5ms, 20ms, 44ms (with Dispatcher blocked to 60ms), 46ms (before pending drawn),
            //    100ms (slow + 500ms hold), 800ms (slow, immediate after completion),
            //    20ms failure, 200ms failure.
            var matrix = RunLatencyMatrix(app, tray, osd, firstWindow, routes, writes, playerPid, player, deadline.Token, Check);

            // 4. Rapid three presses A -> B -> C -> D with blocked writes:
            routes[player.Identity] = a;
            long warmStarted = Stopwatch.GetTimestamp();
            var toB2Task = Press(playerPid);
            double warmMs = Stopwatch.GetElapsedTime(warmStarted).TotalMilliseconds;
            var toB2 = Take(player, b);
            var toCTask = Press(playerPid);
            var toDTask = Press(playerPid);
            // Wait past 45ms so D enters pending state while toB2 is blocked.
            Pump(WaitUntil(() => Title() == FormatSwitching(player.Label) && Text() == "🔊 " + d));
            Check(!writes.Reader.TryPeek(out _), "three rapid presses skip hidden devices, show latest D pending and leave writes serialized");
            toB2.Finish.SetResult(true);
            var toC = Take(player, c);
            Pending(player.Label, d);
            toC.Finish.SetResult(false);
            var toD = Take(player, d);
            Pending(player.Label, d);
            toD.Finish.SetResult(true);
            Complete(Task.WhenAll(toB2Task, toCTask, toDTask));
            Pump(WaitUntil(() => Title() == player.Label && Text() == "🔊 " + d, 2500));
            double confirmedMs = Stopwatch.GetElapsedTime(warmStarted).TotalMilliseconds;
            Check(routes[player.Identity] == d && Text() == "🔊 " + d && Title() == player.Label,
                "older success and failure cannot overwrite D, and D is confirmed by readback");

            // 5. Wrap through renamed system-default item and null persisted route (fast completion):
            var wrap = Press(playerPid);
            var toSystem = Take(player, system);
            toSystem.Finish.SetResult(true);
            Complete(wrap);
            Check(Title() == player.Label && Text() == "🔊 Follow Windows" && routes[player.Identity] == system,
                "fast cycle wraps through the renamed system-default item without showing Switching");
            var fromDefault = Press(playerPid);
            Take(player, a).Finish.SetResult(true);
            Complete(fromDefault);
            Check(Title() == player.Label && Text() == "🔊 " + a, "a null persisted route starts from the visible system-default item");

            // 6. Failure and mismatched readback:
            var failure = Press(playerPid);
            var partial = Take(player, b);
            routes[player.Identity] = c;
            partial.Finish.SetResult(false);
            Complete(failure);
            Check(Title().StartsWith(L10n.T("Ov.OutputSwitchFailed"), StringComparison.Ordinal) && Text() == "🔊 " + c,
                "latest route failure displays the current readback instead of the requested or previously cached device");

            var mismatch = Press(playerPid);
            var wrongReadback = Take(player, d);
            wrongReadback.ConfirmedTarget = c;
            wrongReadback.Finish.SetResult(true);
            Complete(mismatch);
            Check(Title().StartsWith(L10n.T("Ov.OutputSwitchFailed"), StringComparison.Ordinal) && Text() == "🔊 " + c,
                "a successful write acknowledgment alone cannot confirm a mismatched route");

            // 7. New foreground app isolation (slow B vs fast Other):
            var leaving = Press(playerPid);
            var oldWrite = Take(player, d);
            var arriving = Press(otherPid);
            var newWrite = Take(other, b);
            oldWrite.Finish.SetResult(false);
            Complete(leaving);
            newWrite.Finish.SetResult(true);
            Complete(arriving);
            Check(Title() == other.Label && Text() == "🔊 Renamed B" && routes[player.Identity] == c && routes[other.Identity] == b,
                "new foreground app has an independent route and notice");

            // 8. Android external apps (both fast and slow >45ms + 500ms hold):
            var external1 = Press(phoneOne);
            var externalWrite1 = Take(firstPhone, b);
            var external2 = Press(phoneTwo);
            var externalWrite2 = Take(secondPhone, b);
            externalWrite1.Finish.SetResult(true);
            Complete(external1);
            Check(Title().EndsWith(secondPhone.Label, StringComparison.Ordinal) || Text() == "🔊 Renamed B",
                "external sessions with the same synthetic PID are isolated by source ID");
            externalWrite2.Finish.SetResult(false);
            Complete(external2);
            Check(Title().StartsWith(L10n.T("Ov.OutputSwitchFailed"), StringComparison.Ordinal) && Title().EndsWith(secondPhone.Label, StringComparison.Ordinal),
                "external phone failure retains target app identity");

            // Android external app slow (>45ms) shows "Phone 1 · 切换中" and honors 500ms min hold:
            routes[firstPhone.Identity] = a;
            var slowPhone = Press(phoneOne);
            var slowPhoneWrite = Take(firstPhone, b);
            Pump(WaitUntil(() => Title() == FormatSwitching(firstPhone.Label) && Text() == "🔊 Renamed B"));
            long phonePendingAt = Stopwatch.GetTimestamp();
            Pump(Task.Delay(60, deadline.Token));
            slowPhoneWrite.Finish.SetResult(true);
            Complete(slowPhone);
            Check(Title() == FormatSwitching(firstPhone.Label) && Text() == "🔊 Renamed B",
                "Android external app keeps localized Switching title while honoring 500ms minimum duration");
            Pump(WaitUntil(() => Title() == firstPhone.Label && Text() == "🔊 Renamed B", 2500));
            double phoneHoldMs = Stopwatch.GetElapsedTime(phonePendingAt).TotalMilliseconds;
            Check(phoneHoldMs >= 490, FormattableString.Invariant($"Android external app Switching stayed visible >= 500ms ({phoneHoldMs:F1} ms)"));

            var startingPhone = Press(phoneOne);
            var preferenceOnly = Take(firstPhone, c);
            Volatile.Write(ref unconfirmedExternal, firstPhone.Identity);
            preferenceOnly.Finish.SetResult(true);
            Complete(startingPhone);
            Check(Title().StartsWith(L10n.T("Ov.OutputSwitchFailed"), StringComparison.Ordinal) && Text() == L10n.T("Ov.CurrentUnavailable"),
                "a saved target on a source that has not started playback is not reported as a confirmed switch");
            Volatile.Write(ref unconfirmedExternal, null);

            // 9. Volume and mute OSD protection (both fast and during 500ms hold):
            var behindVolume = Press(playerPid);
            var volumeWrite = Take(player, d);
            Invoke(tray, "ShowVolumeOsd", "Volume", 30, "30%", false, "fixture-volume");
            Invoke(tray, "ShowVolumeOsd", "Volume", 31, "31%", false, "fixture-volume");
            volumeWrite.Finish.SetResult(true);
            Complete(behindVolume);
            DrainUi();
            Check(Title() == "Volume" && Read<TextBlock>(osd, "_percent").Text == "31%", "a newer coalesced volume OSD survives a completed switch");

            // Also verify volume interrupting an active 500ms hold is never overwritten when 500ms expires:
            routes[player.Identity] = a;
            var slowHoldSwitch = Press(playerPid);
            var slowHoldWrite = Take(player, b);
            Pump(WaitUntil(() => Title() == FormatSwitching(player.Label)));
            slowHoldWrite.Finish.SetResult(true);
            Complete(slowHoldSwitch);
            // Switch is now holding deferred success for 500ms; user adjusts volume!
            Invoke(tray, "ShowVolumeOsd", "Volume", 55, "55%", false, "fixture-volume");
            Pump(Task.Delay(600));
            Check(Title() == "Volume" && Read<TextBlock>(osd, "_percent").Text == "55%",
                "a volume adjustment during the 500ms hold cancels the deferred switch completion");

            routes[player.Identity] = d;
            var behindMute = Press(playerPid);
            var muteWrite = Take(player, system);
            Invoke(tray, "ShowMicMuteOsd", "Mic", true);
            muteWrite.Finish.SetResult(false);
            Complete(behindMute);
            Check(Title() == "Mic" && Text() == L10n.T("Ov.MicMuted"), "a newer mute OSD survives a failed switch");

            // 10. 10 consecutive fast hotkey presses: no stale OSD, no redundant "Switching", bounded allocations:
            routes[player.Identity] = a;
            int switchingEvents = 0;
            EventHandler burstWatcher = (_, _) =>
            {
                if (Title().Contains(L10n.T("Ov.Switching").Replace("{0}", "").Trim(), StringComparison.Ordinal))
                    switchingEvents++;
            };
            textProperty.AddValueChanged(titleBlock, burstWatcher);
            long memBefore = GC.GetAllocatedBytesForCurrentThread();
            long burstStart = Stopwatch.GetTimestamp();
            try
            {
                var burstTasks = new List<Task>(10);
                for (int i = 0; i < 10; i++)
                    burstTasks.Add(Press(playerPid));
                for (int i = 0; i < 10; i++)
                {
                    var readTask = writes.Reader.ReadAsync(deadline.Token).AsTask();
                    Complete(readTask);
                    readTask.Result.Finish.SetResult(true);
                }
                Complete(Task.WhenAll(burstTasks));
                DrainUi();
            }
            finally { textProperty.RemoveValueChanged(titleBlock, burstWatcher); }
            long memDelta = GC.GetAllocatedBytesForCurrentThread() - memBefore;
            double burstElapsedMs = Stopwatch.GetElapsedTime(burstStart).TotalMilliseconds;
            // 10 cycles over 4 visible devices [system, a, b, c, d] from a (idx=1): 1+10=11 mod 5 = 1 => a
            Check(Title() == player.Label && switchingEvents == 0,
                FormattableString.Invariant($"10 consecutive fast switches skipped Switching ({switchingEvents}) in {burstElapsedMs:F1}ms, UI alloc={memDelta}B"));

            // 11. Device removal & app exit during queued writes:
            routes[player.Identity] = a;
            var beforeRemoval = Press(playerPid);
            var stillPresent = Take(player, b);
            var removed = Press(playerPid);
            Pump(WaitUntil(() => Title() == FormatSwitching(player.Label) && Text() == "🔊 " + c));
            Volatile.Write(ref devices, allDevices.Where(item => item.Id != c).ToList());
            stillPresent.Finish.SetResult(true);
            Complete(Task.WhenAll(beforeRemoval, removed));
            Check(!writes.Reader.TryPeek(out _) && Text() == "🔊 Renamed B" && Title().StartsWith(L10n.T("Ov.OutputSwitchFailed"), StringComparison.Ordinal),
                "a device removed before its queued write is skipped and the prior route is restored in the OSD");
            Volatile.Write(ref devices, allDevices);

            var beforeExit = Press(playerPid);
            var atExit = Take(player, c);
            var afterExit = Press(playerPid);
            available[player.Identity] = false;
            atExit.Finish.SetResult(false);
            Complete(Task.WhenAll(beforeExit, afterExit));
            Check(!writes.Reader.TryPeek(out _) && Text() == L10n.T("Ov.CurrentUnavailable")
                && ((IDictionary)typeof(App).GetField("_hotkeyOutputs", Hidden)!.GetValue(app)!).Count == 0,
                "app exit clears the pending route and suppresses queued writes");
            available[player.Identity] = true;

            // 12. Visible QuickPanel & multi-device readback consistency:
            CheckVisiblePanel();

            string timing = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Simulated dispatch-to-handler={Stopwatch.GetElapsedTime(pressed, handling).TotalMilliseconds:F2} ms " +
                $"first-handler-return={Stopwatch.GetElapsedTime(handling, notified).TotalMilliseconds:F2} ms " +
                $"first-pending-to-ContentRendered={Stopwatch.GetElapsedTime(notified, rendered).TotalMilliseconds:F2} ms " +
                $"first-pending-to-nonzero-opacity={Stopwatch.GetElapsedTime(notified, opaque).TotalMilliseconds:F2} ms " +
                $"first-target-observed={targetMs:F2} ms first-write-start={Stopwatch.GetElapsedTime(pressed, toB.Started).TotalMilliseconds:F2} ms " +
                $"slow-min-visible={minVisibleObservedMs:F2} ms warm-handler-dispatch={warmMs:F2} ms " +
                $"scripted-three-write-confirmed={confirmedMs:F2} ms configured-fade-in={config.OsdFadeInMs} ms " +
                $"burst10-ms={burstElapsedMs:F2} ms burst10-ui-alloc={memDelta} B | {matrix}");
            if (profileStartup)
                timing += FormattableString.Invariant($"{Environment.NewLine}Separated startup sample visual-tree={visualTreeMs:F2} ms handler-to-native-window={Stopwatch.GetElapsedTime(handling, nativeWindow).TotalMilliseconds:F2} ms native-window-to-show-return={Stopwatch.GetElapsedTime(nativeWindow, notified).TotalMilliseconds:F2} ms");
            Console.WriteLine(timing);
            File.WriteAllText(Path.Combine(output, "hotkey-output-timing.log"), timing + Environment.NewLine);
            return 0;

            void FirstOpacity(object? sender, EventArgs e)
            {
                if (!Visible() || Window()!.Opacity <= 0) return;
                CompositionTarget.Rendering -= FirstOpacity;
                opaque = Stopwatch.GetTimestamp();
                firstOpacity.SetResult();
            }
        }
        finally { SetApp("_trayWheel", null); }

        void CheckLocalizationFormats()
        {
            var osdType = osd.GetType();
            var formatMethod = osdType.GetMethod("FormatDeviceSwitchingTitle", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            string FormatSwitch(string? appTitle) => (string)formatMethod.Invoke(null, [appTitle])!;
            string[] langs = ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "ru-RU"];
            foreach (string lang in langs)
            {
                L10n.Instance.SetLanguage(lang);
                string template = L10n.T("Ov.Switching");
                Check(template.Contains("{0}", StringComparison.Ordinal), lang + " Ov.Switching retains {0} placeholder");
                string formatted = FormatSwitch("Spotify");
                Check(formatted.Contains("Spotify", StringComparison.Ordinal) && !formatted.Contains("{0}", StringComparison.Ordinal),
                    lang + " formats Spotify verbatim: " + formatted);
                string emptyFallback = FormatSwitch("");
                Check(!string.IsNullOrWhiteSpace(emptyFallback) && !emptyFallback.Contains("·", StringComparison.Ordinal),
                    lang + " empty app name avoids dangling separator: " + emptyFallback);
            }
            L10n.Instance.SetLanguage("zh-CN");
            string zhTitle = FormatSwitch("Spotify");
            Check(zhTitle == "Spotify · 切换中", "zh-CN puts 切换中 after app name: " + zhTitle);
        }

        string RunLatencyMatrix(App appInstance, object trayObj, object osdObj, Window expectedWindow,
            ConcurrentDictionary<string, string> routeMap, Channel<HotkeyWrite> writeChannel,
            int pid, AudioAppInfo targetApp, CancellationToken ct, Action<bool, string> assert)
        {
            var titleTb = Read<TextBlock>(osdObj, "_title");
            var titleProp = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            var logs = new List<string>();

            // Helper to run one controlled-delay switch from a known starting route.
            void RunCase(string label, int backendMs, bool succeed, bool blockDispatcherPast45On44, bool expectSwitching)
            {
                routeMap[targetApp.Identity] = a;
                // Drain any pending hide/animations; keep window alive or warm.
                bool sawSwitching = false;
                long switchedConfirmedAt = 0;
                long pendingVisibleAt = 0;
                EventHandler watch = (_, _) =>
                {
                    string t = titleTb.Text;
                    if (t == FormatSwitching(targetApp.Label))
                    {
                        sawSwitching = true;
                        if (pendingVisibleAt == 0) pendingVisibleAt = Stopwatch.GetTimestamp();
                    }
                    else if ((succeed && t == targetApp.Label) || (!succeed && t.StartsWith(L10n.T("Ov.OutputSwitchFailed"), StringComparison.Ordinal)))
                    {
                        if (switchedConfirmedAt == 0) switchedConfirmedAt = Stopwatch.GetTimestamp();
                    }
                };
                titleProp.AddValueChanged(titleTb, watch);
                long startAt = Stopwatch.GetTimestamp();
                long backendDoneAt = 0;
                try
                {
                    var task = Press(pid);
                    var write = Take(targetApp, b);
                    if (backendMs <= 0)
                    {
                        write.Finish.SetResult(succeed);
                        backendDoneAt = Stopwatch.GetTimestamp();
                    }
                    else
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(backendMs, ct);
                            write.Finish.SetResult(succeed);
                            backendDoneAt = Stopwatch.GetTimestamp();
                        }, ct);
                    }

                    if (blockDispatcherPast45On44)
                    {
                        // Simulate a busy UI Dispatcher until ~60ms while backend finishes at 44ms!
                        Thread.Sleep(60);
                    }
                    else if (label == "46ms-before-draw")
                    {
                        // Block UI thread past 46ms so backend finishes at ~46ms before the 45ms timer tick runs!
                        Thread.Sleep(55);
                    }

                    Complete(task);
                    Pump(WaitUntil(() =>
                        (succeed && Title() == targetApp.Label && Text() == "🔊 Renamed B")
                        || (!succeed && Title().StartsWith(L10n.T("Ov.OutputSwitchFailed"), StringComparison.Ordinal)), 2500));
                    if (switchedConfirmedAt == 0) switchedConfirmedAt = Stopwatch.GetTimestamp();
                }
                finally { titleProp.RemoveValueChanged(titleTb, watch); }

                double backendElapsed = Stopwatch.GetElapsedTime(startAt, backendDoneAt).TotalMilliseconds;
                double osdVisibleElapsed = Stopwatch.GetElapsedTime(startAt, switchedConfirmedAt).TotalMilliseconds;
                assert(sawSwitching == expectSwitching,
                    FormattableString.Invariant($"{label}: sawSwitching={sawSwitching} expected={expectSwitching} (backend={backendElapsed:F1}ms, osd={osdVisibleElapsed:F1}ms)"));
                assert(ReferenceEquals(Window(), expectedWindow), label + " reuses existing OSD window");
                if (expectSwitching && succeed && backendMs < 500)
                {
                    double pendingVisibleMs = Stopwatch.GetElapsedTime(pendingVisibleAt, switchedConfirmedAt).TotalMilliseconds;
                    assert(pendingVisibleMs >= 490, FormattableString.Invariant($"{label}: pending visible >= 500ms ({pendingVisibleMs:F1}ms)"));
                    logs.Add(FormattableString.Invariant($"{label}(be={backendElapsed:F0}ms,pendVis={pendingVisibleMs:F0}ms)"));
                }
                else if (expectSwitching && succeed && backendMs >= 500)
                {
                    double afterBackendMs = Stopwatch.GetElapsedTime(backendDoneAt, switchedConfirmedAt).TotalMilliseconds;
                    assert(afterBackendMs < 150, FormattableString.Invariant($"{label}: slow completion updates immediately after backend ({afterBackendMs:F1}ms)"));
                    logs.Add(FormattableString.Invariant($"{label}(be={backendElapsed:F0}ms,post={afterBackendMs:F0}ms)"));
                }
                else if (expectSwitching && !succeed)
                {
                    double afterBackendMs = Stopwatch.GetElapsedTime(backendDoneAt, switchedConfirmedAt).TotalMilliseconds;
                    assert(afterBackendMs < 150, FormattableString.Invariant($"{label}: failure does not wait 500ms ({afterBackendMs:F1}ms)"));
                    logs.Add(FormattableString.Invariant($"{label}(be={backendElapsed:F0}ms,failPost={afterBackendMs:F0}ms)"));
                }
                else
                {
                    logs.Add(FormattableString.Invariant($"{label}(be={backendElapsed:F0}ms,osd={osdVisibleElapsed:F0}ms)"));
                }
            }

            RunCase("5ms-ok", 5, succeed: true, blockDispatcherPast45On44: false, expectSwitching: false);
            RunCase("20ms-ok", 20, succeed: true, blockDispatcherPast45On44: false, expectSwitching: false);
            RunCase("44ms-busy-ui", 35, succeed: true, blockDispatcherPast45On44: true, expectSwitching: false);
            RunCase("46ms-before-draw", 46, succeed: true, blockDispatcherPast45On44: false, expectSwitching: false);
            RunCase("100ms-ok", 100, succeed: true, blockDispatcherPast45On44: false, expectSwitching: true);
            RunCase("800ms-ok", 800, succeed: true, blockDispatcherPast45On44: false, expectSwitching: true);
            RunCase("20ms-fail", 20, succeed: false, blockDispatcherPast45On44: false, expectSwitching: false);
            RunCase("200ms-fail", 200, succeed: false, blockDispatcherPast45On44: false, expectSwitching: true);
            return string.Join(" ", logs);
        }

        void CheckVisiblePanel()
        {
            routes[player.Identity] = a;
            var panel = new QuickPanelModernWindow { ShowActivated = false };
            try
            {
                SetPanel("_outputDisplay", allDevices);
                SetPanel("_readPanelState", (Func<IEnumerable<int>, (Dictionary<int, (int, bool, bool)>, bool?)>)
                    (ids => (ids.ToDictionary(id => id, _ => (45, false, true)), false)));
                SetPanel("_readRowEndpoints", (Func<int, bool, (string, string)>)((_, _) => (routes[player.Identity], "")));
                SetPanel("_readRowPlaybackOutputs", (Func<int, string?, HashSet<string>>)((_, _) => [routes[player.Identity]]));
                Complete((Task)Invoke(panel, "BuildAppRowsAsync", new List<AudioAppInfo> { player }, config)!);
                object row = Read<IDictionary>(panel, "_rows")[playerPid]!;
                row.GetType().GetField("Expanded")!.SetValue(row, true);
                Complete((Task)Invoke(panel, "BuildRowDevicesAsync", row)!);
                SetApp("_quickPanel", panel);
                panel.Show();
                var buttons = Read<List<(Button Button, string Id, EDataFlow Flow)>>(row, "DeviceButtons");
                var restored = buttons.Single(entry => entry.Id == d).Button;
                var confirmed = buttons.Single(entry => entry.Id == c).Button;
                var active = panel.FindResource("DevButtonActive");
                var firstReadback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var property = DependencyPropertyDescriptor.FromProperty(FrameworkElement.StyleProperty, typeof(Button));
                EventHandler recovered = (_, _) => { if (ReferenceEquals(restored.Style, active)) firstReadback.TrySetResult(); };
                EventHandler changed = (_, _) => { if (ReferenceEquals(confirmed.Style, active)) updated.TrySetResult(); };
                property.AddValueChanged(restored, recovered);
                property.AddValueChanged(confirmed, changed);
                try
                {
                    var switchWithPanel = Press(playerPid);
                    var write = Take(player, b);
                    Pump(WaitUntil(() => Title() == FormatSwitching(player.Label) && Text() == "🔊 Renamed B"));
                    config.DefaultAppMode = "fixed"; // Force the next press through asynchronous app discovery.
                    var nextDiscovery = new PlaybackReadGate();
                    Volatile.Write(ref discovery, nextDiscovery);
                    var secondPress = Press(playerPid);
                    Complete(nextDiscovery.Entered.Task);
                    routes[player.Identity] = d;
                    write.Finish.SetResult(false);
                    Complete(firstReadback.Task);
                    Pump(WaitUntil(() => Text() == L10n.T("Ov.SwitchingOutput")));
                    Check(Text() == L10n.T("Ov.SwitchingOutput") && !secondPress.IsCompleted,
                        "a rejected write finishes and refreshes the open panel without overwriting the next press during discovery");
                    nextDiscovery.Release.SetResult();
                    var next = Take(player, c);
                    next.Finish.SetResult(true);
                    Complete(Task.WhenAll(switchWithPanel, secondPress));
                    Complete(updated.Task);
                    Pump(WaitUntil(() => Title() == player.Label && Text() == "🔊 " + c, 2500));
                    Check(panel.IsVisible && ReferenceEquals(confirmed.Style, active) && Text() == "🔊 " + c,
                        "the open PC panel reads back the same confirmed device as the OSD");
                }
                finally
                {
                    config.DefaultAppMode = "recent";
                    property.RemoveValueChanged(restored, recovered);
                    property.RemoveValueChanged(confirmed, changed);
                }
            }
            finally { SetApp("_quickPanel", null); panel.Close(); }

            void SetPanel(string name, object value) => typeof(QuickPanelModernWindow).GetField(name, Hidden)!.SetValue(panel, value);
        }

        async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                    throw new TimeoutException($"Condition timed out after {timeoutMs}ms (Visible={Visible()}, Title={Title()}, Text={Text()})");
                await Task.Delay(5, deadline.Token);
            }
        }

        static AudioAppInfo NewApp(int pid, string label, string? source = null) =>
            new() { ProcessId = (uint)pid, ProcessName = label, DisplayName = label, ExternalSourceId = source };
        static T Read<T>(object owner, string name) =>
            (T)owner.GetType().GetField(name, Hidden | System.Reflection.BindingFlags.Public)!.GetValue(owner)!;
        static object? Invoke(object owner, string method, params object?[] args) =>
            owner.GetType().GetMethod(method, Hidden | System.Reflection.BindingFlags.Public)!.Invoke(owner, args);
        void SetApp(string name, object? value) => typeof(App).GetField(name, Hidden)!.SetValue(app, value);
        Task Press(int pid) => (Task)typeof(App).GetMethod("ExecuteHotkeyCoreAsync", Hidden)!.Invoke(app, [HotkeyActions.ActSwitchOutput, pid])!;
        void Complete(Task task) => Pump(task.WaitAsync(deadline.Token));
        Window? Window() => (Window?)osdTypeInfo.GetField("_window", Hidden)!.GetValue(osd);
        bool Visible() => Window()?.IsVisible == true;
        string Title() => osdTypeInfo.GetField("_title", Hidden)!.GetValue(osd) is TextBlock tb ? tb.Text : "";
        string Text() => osdTypeInfo.GetField("_text", Hidden)!.GetValue(osd) is TextBlock tb ? tb.Text : "";
        string FormatSwitching(string label) => (string)formatSwitchingTitle.Invoke(null, [label])!;
        void Pending(string appLabel, string name) => Check(Visible() && Title() == FormatSwitching(appLabel) && Text() == "🔊 " + name,
            "visible pending OSD points to " + name);
        HotkeyWrite Take(AudioAppInfo expected, string target)
        {
            var read = writes.Reader.ReadAsync(deadline.Token).AsTask();
            Complete(read);
            var write = read.Result;
            Check(write.App == expected.Identity && write.Target == target, "worker writes " + target + " for " + expected.Label);
            return write;
        }
    }
}

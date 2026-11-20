using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private const BindingFlags ConcurrencyHidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags ConcurrencyStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private static int ConcurrencyFixesUi(string output)
    {
        CheckSilenceLeaseFailures();
        CheckRuleWatcherRecovery();
        CheckConfigWorkerSnapshots(output);
        CheckAudioCacheGenerations();
        CheckTrayTypeBoundaries();
        CheckDisposedAudioPolicy();
        PhoneClipboardChecks(output);
        Pump(CheckMediaResourceCleanupAsync(output));
        CheckMediaShutdownJoin(); // Cancels the application's media lifetime; keep last.
        File.WriteAllText(Path.Combine(output, "concurrency-result.json"), JsonSerializer.Serialize(new
        {
            Success = true, Assertions = _checks, RealDevice = false, RealClipboard = false,
            RealAudioRouting = false, NativeAudioEnumeration = false,
            Cases = "mute leases, watcher recovery, config snapshots, cache generations, tray types, clipboard events, failed cleanup, stdin close and async shutdown"
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} scoped concurrency/resource assertions.");
        return 0;
    }

    private static void CheckSilenceLeaseFailures()
    {
        CheckSourceSpeakerLeases();
        CheckSpareOutputRetries();
        CheckPlaybackMigration();
        var type = typeof(App).Assembly.GetType("SonicRoute.LocalSilenceLeasePool")!;
        bool known = true, muted = false, setSucceeds = true, routeSucceeds = true, throwRoute = false;
        int muteWrites = 0, routeWrites = 0;
        var routes = new Dictionary<int, (string? Multimedia, string? Console)>();
        object NewPool() => Activator.CreateInstance(type, ConcurrencyHidden, null, new object[]
        {
            (Func<string?>)(() => "silent-output"),
            (Func<string, (bool, bool)>)(_ => (muted, known)),
            (Func<string, bool, bool>)((_, value) => { muteWrites++; if (setSucceeds) muted = value; return setSucceeds; }),
            (Func<int, (string?, string?)>)(pid => routes.GetValueOrDefault(pid, ("original-output", "original-console"))),
            (Func<int, (string?, string?), bool>)((pid, ids) =>
            {
                routeWrites++;
                if (ids == ("silent-output", "silent-output"))
                {
                    routes[pid] = routeSucceeds ? ids : ("silent-output", "original-console");
                    if (throwRoute) throw new IOException("route fixture failure");
                    return routeSucceeds;
                }
                routes[pid] = ids; return true;
            })
        }, null)!;
        IPcProcessSilenceLease? Acquire(object pool, int pid) =>
            (IPcProcessSilenceLease?)type.GetMethod("Acquire", ConcurrencyHidden)!.Invoke(pool, new object[] { pid });

        known = false;
        Check(Acquire(NewPool(), 1) == null && muteWrites == 0 && routeWrites == 0,
            "unknown endpoint mute state creates no lease and changes no mute/route");
        known = true; setSucceeds = false;
        Check(Acquire(NewPool(), 1) == null && routeWrites == 0 && !muted,
            "failed endpoint mute cannot be counted as a successful silence lease");
        setSucceeds = true; routeSucceeds = false;
        Check(Acquire(NewPool(), 1) == null && !muted && routes[1] == ("original-output", "original-console"),
            "failed route rolls back partial role changes and the temporary endpoint mute");
        routeSucceeds = true; throwRoute = true;
        bool failed = false;
        try { Acquire(NewPool(), 2); } catch (TargetInvocationException ex) when (ex.InnerException is IOException) { failed = true; }
        Check(failed && !muted && routes[2] == ("original-output", "original-console"), "throwing route performs the same rollback without leaking a lease");
        throwRoute = false;
        var pool = NewPool();
        var first = Acquire(pool, 3)!; var second = Acquire(pool, 4)!;
        routeSucceeds = false;
        Check(Acquire(pool, 5) == null && muted && routes[5] == ("original-output", "original-console"), "a failed additional lease preserves existing successful leases");
        routes[3] = ("user-selected-output", "silent-output");
        first.Dispose(); first.Dispose();
        Check(muted && routes[3] == ("user-selected-output", "original-console"), "release restores its console route and preserves a new multimedia route");
        second.Release(false);
        Check(!muted && routes[4] == ("silent-output", "silent-output"), "last release restores only owned mute and honors route restoration choice");
        routeSucceeds = true; muted = true;
        Acquire(NewPool(), 6)!.Dispose();
        Check(muted, "an endpoint already muted before the lease stays muted after release");
        string? available = null;
        int isolatedWrites = 0;
        bool spareMuted = false;
        var isolated = Activator.CreateInstance(type, ConcurrencyHidden, null, new object[]
        {
            (Func<IReadOnlySet<int>, string?, IReadOnlyList<string>>)((_, current) =>
                available != null && (current == null || available == current) ? [available] : []),
            (Func<string, (bool, bool)>)(_ => (spareMuted, true)),
            (Func<string, bool, bool>)((_, value) => { spareMuted = value; return true; }),
            (Func<int, (string?, string?)>)(pid => routes.GetValueOrDefault(pid, ("preferred", "console"))),
            (Func<int, (string?, string?), bool>)((pid, ids) => { isolatedWrites++; routes[pid] = ids; return true; })
        }, null)!;
        Check(Acquire(isolated, 81) == null && isolatedWrites == 0,
            "The silence lease leaves all routes alone when no usable output exists");
        available = "unused-spare";
        var exclusive = Acquire(isolated, 81)!;
        Check(spareMuted && routes[81] == (available, available), "The silence lease mutes an unused secondary output and routes the selected app onto it");
        available = null;
        Check(Acquire(isolated, 82) == null && !routes.ContainsKey(82),
            "another app cannot acquire a previously eligible output after its mute or occupancy changes");
        type.GetMethod("Revalidate", ConcurrencyHidden)!.Invoke(isolated, null);
        Check(exclusive.Invalidated.IsCancellationRequested, "loss of an available secondary output revokes its exclusive route");
        routes[81] = ("manually-switched", "unused-spare");
        exclusive.Dispose();
        Check(!spareMuted && routes[81] == ("manually-switched", "console"), "ending the silence lease restores its mute and only unchanged route roles");
        available = "unused-spare";
        var userChanged = Acquire(isolated, 84)!;
        type.GetMethod("NoteUnmuted", ConcurrencyHidden)!.Invoke(isolated, [available]);
        type.GetMethod("Revalidate", ConcurrencyHidden)!.Invoke(isolated, null);
        bool revoked = userChanged.Invalidated.IsCancellationRequested;
        userChanged.Dispose();
        Check(revoked && spareMuted, "an external unmute and re-mute revokes the route and preserves the user's last setting");
        type.GetProperty("VerifyPlaybackRoute", ConcurrencyHidden)!.SetValue(isolated,
            (Func<int, string, bool, IReadOnlySet<int>, bool>)((_, _, _, _) => false));
        Check(Acquire(isolated, 83) == null && routes[83] == ("preferred", "console"),
            "a player that keeps rendering on its former device fails exclusive mode and regains its prior policy");

        bool allMuted = false;
        var mutedType = typeof(App).Assembly.GetType("SonicRoute.AlreadyMutedOutputLeases")!;
        var readOnly = Activator.CreateInstance(mutedType, ConcurrencyHidden | BindingFlags.Public, null,
            new object[] { (Func<bool>)(() => allMuted) }, null)!;
        IPcProcessSilenceLease? AcquireReadOnly() =>
            (IPcProcessSilenceLease?)mutedType.GetMethod("Acquire", ConcurrencyHidden)!.Invoke(readOnly, [91]);
        Check(AcquireReadOnly() == null, "one audible output rules out destination-only playback through an unchanged local route");
        allMuted = true;
        using (var alreadySilent = AcquireReadOnly()!)
        {
            allMuted = false;
            mutedType.GetMethod("Revalidate", ConcurrencyHidden)!.Invoke(readOnly, null);
            Check(alreadySilent.Invalidated.IsCancellationRequested, "adding an audible output revokes the read-only silence lease");
        }
        allMuted = true;
        using (var alreadySilent = AcquireReadOnly()!)
        {
            mutedType.GetMethod("NoteUnmuted", ConcurrencyHidden)!.Invoke(readOnly, null);
            mutedType.GetMethod("Revalidate", ConcurrencyHidden)!.Invoke(readOnly, null);
            Check(alreadySilent.Invalidated.IsCancellationRequested, "a user's unmute ends read-only routing even if the endpoint is muted again");
        }
    }

    private static void CheckSpareOutputRetries()
    {
        var type = typeof(App).Assembly.GetType("SonicRoute.LocalSilenceLeasePool", true)!;
        string[] candidates = ["usb", "line-out", "hdmi"];
        var mute = candidates.ToDictionary(id => id, id => id == "hdmi");
        var routes = new Dictionary<int, (string? Multimedia, string? Console)>();
        (string? Multimedia, string? Console) original = ("default-media", "default-console");
        var attempts = new List<string>();
        var beforeAttempt = new List<(string? Multimedia, string? Console)>();
        var activeAtVerification = new List<bool>();
        var accepted = new Dictionary<int, string> { [101] = "line-out", [102] = "usb" };
        var pool = Activator.CreateInstance(type, ConcurrencyHidden, null, new object[]
        {
            (Func<IReadOnlySet<int>, string?, IReadOnlyList<string>>)((_, current) => current == null ? candidates : [current]),
            (Func<string, (bool, bool)>)(id => (mute[id], true)),
            (Func<string, bool, bool>)((id, value) => { mute[id] = value; return true; }),
            (Func<int, (string?, string?)>)(pid => routes.GetValueOrDefault(pid, original)),
            (Func<int, (string?, string?), bool>)((pid, next) =>
            {
                if (next.Item1 != null && next.Item1 == next.Item2 && mute.ContainsKey(next.Item1))
                {
                    attempts.Add(next.Item1);
                    beforeAttempt.Add(routes.GetValueOrDefault(pid, original));
                    if (next.Item1 == "hdmi")
                    {
                        routes[pid] = ("hdmi", original.Console);
                        return false;
                    }
                }
                if (pid == 104 && next == original)
                {
                    routes[pid] = ("usb", original.Console);
                    return false;
                }
                routes[pid] = next;
                return true;
            })
        }, null)!;
        type.GetProperty("VerifyPlaybackRoute", ConcurrencyHidden)!.SetValue(pool, (Func<int, string, bool, IReadOnlySet<int>, bool>)((pid, id, wasActive, _) =>
        {
            activeAtVerification.Add(wasActive);
            if (pid == 101 && id == "usb") throw new InvalidOperationException("Player kept its original output");
            return accepted.GetValueOrDefault(pid) == id;
        }));
        IPcProcessSilenceLease? Acquire(int pid) =>
            (IPcProcessSilenceLease?)type.GetMethod("AcquireForPlayback", ConcurrencyHidden)!.Invoke(pool, [pid, pid == 101]);

        var first = Acquire(101);
        Check(first != null && attempts.SequenceEqual(["usb", "line-out"]) && beforeAttempt.All(route => route == original)
            && !mute["usb"] && mute["line-out"] && routes[101] == ("line-out", "line-out"),
            "a rejected first output is restored before trying the second, and a successful retry stops the search");
        Check(activeAtVerification.SequenceEqual([true, true]),
            "all candidates use the original activity of the app even when its session disappears during the first switch");
        attempts.Clear();
        var second = Acquire(102);
        Check(second != null && attempts.SequenceEqual(["line-out", "usb"]) && !first!.Invalidated.IsCancellationRequested
            && mute["line-out"] && mute["usb"] && routes[101] == ("line-out", "line-out") && routes[102] == ("usb", "usb"),
            "an app that cannot share the reserved output uses another spare while the first app keeps playing");
        first!.Dispose();
        Check(!mute["line-out"] && mute["usb"] && routes[101] == original && routes[102] == ("usb", "usb"),
            "returning the first app restores only its output and its own Windows policy");
        second!.Dispose();
        attempts.Clear();
        beforeAttempt.Clear();
        Check(Acquire(103) == null && attempts.SequenceEqual(candidates) && beforeAttempt.All(route => route == original)
            && routes[103] == original && !mute["usb"] && !mute["line-out"] && mute["hdmi"],
            "fallback is reached only after every spare fails, with partial policy changes rolled back and prior mutes preserved");
        attempts.Clear();
        bool rollbackFailed = false;
        try { Acquire(104); }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException) { rollbackFailed = true; }
        Check(rollbackFailed && attempts.SequenceEqual(["usb"]) && !mute["usb"] && routes[104] == ("usb", original.Console),
            "a Windows policy rollback failure ends the switch without treating the failed output as a new baseline");
    }

    private static void CheckPlaybackMigration()
    {
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost", true)!;
        var waitForMigration = host.GetMethod("WaitForPlaybackMigration", ConcurrencyStatic)!
            .CreateDelegate<Func<bool, Func<(bool Safe, bool Target, bool Other)>, Action, bool>>();
        var empty = (true, false, false);
        var target = (true, true, false);
        var old = (true, false, true);
        var unsafeTarget = (false, true, false);

        (bool Migrated, int Samples) Run(bool wasActive, params (bool, bool, bool)[] observations)
        {
            int count = 0;
            bool migrated = waitForMigration(wasActive,
                () => observations[Math.Min(count++, observations.Length - 1)], () => { });
            return (migrated, count);
        }

        var delayed = Run(true, old, empty, target);
        Check(delayed.Migrated && delayed.Samples > 3,
            "an active app can migrate after its old session disappears and the new one becomes stable");
        Check(!Run(true, old, empty).Migrated, "an active app disappearing on every output does not count as a successful move");
        Check(!Run(true, target, old).Migrated, "an app returning to its original output after briefly moving fails validation");
        Check(!Run(true, target, unsafeTarget).Migrated, "a spare becoming unsafe during verification is rejected");
        Check(Run(false, empty).Migrated, "a paused app can select a muted spare before playback resumes");

        var waitForSource = host.GetMethod("WaitForOriginalSpeaker", ConcurrencyStatic)!
            .CreateDelegate<Func<string, bool, Func<(string? Endpoint, bool Active)>, Action, bool>>();
        int samples = 0;
        (string? Endpoint, bool Active)[] returning = [("spare", true), (null, false), ("original", false), ("original", true)];
        Check(waitForSource("original", true, () => returning[Math.Min(samples++, returning.Length - 1)], () => { })
            && samples > returning.Length, "fallback waits for stable active playback back on the original speaker");
        Check(!waitForSource("original", true, () => ("spare", true), () => { })
            && !waitForSource("original", true, () => ("original", false), () => { }),
            "a transient spare or an inactive original cannot trigger source-speaker mute for a playing app");
        Check(waitForSource("original", false, () => ("original", false), () => throw new InvalidOperationException()),
            "a paused app with its original speaker still selected needs no extra retry delay");
    }

    private static void CheckSourceSpeakerLeases()
    {
        var type = typeof(App).Assembly.GetType("SonicRoute.SourceSpeakerLeases", true)!;
        var initial = new Dictionary<string, bool> { ["speaker"] = false, ["headphones"] = false };
        var outputs = new Dictionary<int, string> { [1] = "speaker", [2] = "speaker", [3] = "headphones" };
        var unavailable = new HashSet<string>();
        var writes = new List<(string, bool)>();
        bool failWrite = false;
        bool sourceActive = true;
        var pool = Activator.CreateInstance(type, ConcurrencyHidden, null, new object[]
        {
            (Func<int, (string?, bool)>)(pid => (outputs.GetValueOrDefault(pid), sourceActive)),
            (Func<string, (bool, bool)>)(id => (initial[id], !unavailable.Contains(id))),
            (Func<string, bool, bool>)((id, muted) =>
            {
                if (failWrite) return false;
                writes.Add((id, muted)); initial[id] = muted; return true;
            })
        }, null)!;
        IPcProcessSilenceLease? Acquire(int pid) =>
            (IPcProcessSilenceLease?)type.GetMethod("Acquire", ConcurrencyHidden)!.Invoke(pool, [pid, outputs.GetValueOrDefault(pid), true]);
        void Revalidate() => type.GetMethod("Revalidate", ConcurrencyHidden)!.Invoke(pool, null);
        void Unmuted() => type.GetMethod("NoteUnmuted", ConcurrencyHidden)!.Invoke(pool, ["speaker"]);

        Check(Acquire(99) == null && writes.Count == 0, "an ambiguous or missing application speaker causes no mute write");
        Check(type.GetMethod("Acquire", ConcurrencyHidden)!.Invoke(pool, [1, "headphones", true]) == null && writes.Count == 0,
            "a last-moment change of the confirmed source speaker cannot mute a different endpoint");
        sourceActive = false;
        Check(Acquire(1) == null && writes.Count == 0, "a disappearing active session at lease acquisition cannot mute a dormant output");
        sourceActive = true;
        failWrite = true;
        Check(Acquire(1) == null && !initial["speaker"], "a failed speaker mute does not produce a successful transfer");
        failWrite = false;
        using var first = Acquire(1)!;
        using var second = Acquire(2)!;
        using var headphones = Acquire(3)!;
        Check(writes.SequenceEqual([("speaker", true), ("headphones", true)]),
            "overlapping applications mute their shared speaker once and own a separate output independently");
        first.Dispose();
        Check(initial["speaker"] && initial["headphones"], "returning the first application preserves overlapping speaker leases");
        second.Dispose(); second.Dispose();
        Check(!initial["speaker"] && initial["headphones"] && writes.Count == 3, "the last release restores just its speaker once");

        initial["speaker"] = true;
        using (Acquire(1)!) { }
        Check(initial["speaker"] && writes.Count == 3, "transferring a pre-muted speaker preserves the user's original setting");
        initial["speaker"] = false;
        using (var moved = Acquire(1)!)
        {
            outputs[1] = "headphones"; Revalidate();
            Check(moved.Invalidated.IsCancellationRequested, "an application changing speakers ends its exclusive transfer");
        }
        outputs[1] = "speaker";
        using (var userChanged = Acquire(1)!)
        {
            Unmuted(); // A physical unmute followed by a user mute before the queued notification is processed.
            Revalidate();
            Check(userChanged.Invalidated.IsCancellationRequested, "a user's speaker unmute revokes the active transfer");
        }
        Check(initial["speaker"], "returning after a user unmute and re-mute preserves the user's latest setting");
        unavailable.Add("headphones"); Revalidate();
        Check(headphones.Invalidated.IsCancellationRequested, "an unplugged speaker releases the transfer");
        headphones.Dispose();
        unavailable.Clear(); Revalidate();
        Check(!initial["headphones"], "reconnecting an unavailable speaker restores its previously owned mute");
    }

    private static void CheckDormantPlaybackWatch()
    {
        using var devices = new MMDeviceEnumerator();
        if (!devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return;
        using var output = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var client = output.AudioClient;
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 1_000_000, 0, client.MixFormat, Guid.NewGuid());
        client.Start();
        client.Stop();
        using var resumed = new ManualResetEventSlim();
        var type = typeof(App).Assembly.GetType("SonicRoute.WindowsLinkPlaybackWatch", true)!;
        using var watch = (IDisposable)Activator.CreateInstance(type, ConcurrencyHidden, null,
            new object[] { Environment.ProcessId, "different-output", (Action)resumed.Set }, null)!;
        type.GetMethod("Refresh", ConcurrencyHidden)!.Invoke(watch, null);
        Check(!resumed.IsSet, "a dormant session on the previous endpoint can be watched without changing its volume or route");
        using var checkReserved = new ManualResetEventSlim();
        using var reserved = (IDisposable)Activator.CreateInstance(type, ConcurrencyHidden, null,
            new object[] { int.MaxValue, output.ID, (Action)(() => { }) }, null)!;
        type.GetProperty("CheckReservedOutput", ConcurrencyHidden)!.SetValue(reserved, (Action)checkReserved.Set);
        type.GetMethod("Refresh", ConcurrencyHidden)!.Invoke(reserved, null);
        client.Start();
        try
        {
            Check(resumed.Wait(TimeSpan.FromSeconds(3)), "resuming the same WASAPI session on its previous output revokes an exclusive share");
            Check(checkReserved.Wait(TimeSpan.FromSeconds(3)), "a dormant foreign WASAPI session resuming on the reserved output triggers revalidation");
        }
        finally { client.Stop(); }
    }

    private sealed class RecoveryCallbackWatcher(object storeGate) : FileSystemWatcher
    {
        internal readonly TaskCompletionSource<bool> Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(disposing); return; }
            try
            {
                var callback = Task.Run(() => { lock (storeGate) { } });
                Released.TrySetResult(callback.Wait(TimeSpan.FromSeconds(3)));
            }
            catch (Exception ex) { Released.TrySetException(ex); }
            finally { base.Dispose(disposing); }
        }
    }

    private static void CheckRuleWatcherRecovery()
    {
        AutoRuleStore.Shutdown();
        var type = typeof(AutoRuleStore);
        object gate = type.GetField("_lock", ConcurrencyStatic)!.GetValue(null)!;
        var file = new RecoveryCallbackWatcher(gate); var directory = new RecoveryCallbackWatcher(gate);
        type.GetField("_watcher", ConcurrencyStatic)!.SetValue(null, file);
        type.GetField("_directoryWatcher", ConcurrencyStatic)!.SetValue(null, directory);
        AutoRuleStore.RecoverMonitoring();
        Check(file.Released.Task.Wait(TimeSpan.FromSeconds(4)) && file.Released.Task.Result
            && directory.Released.Task.Wait(TimeSpan.FromSeconds(4)) && directory.Released.Task.Result,
            "watcher recovery detaches both watchers without holding the callback lock during disposal");
        var cache = new List<AutoRule>();
        type.GetField("_cache", ConcurrencyStatic)!.SetValue(null, cache);
        type.GetMethod("FileChanged", ConcurrencyStatic)!.Invoke(null, new object[]
        { file, new FileSystemEventArgs(WatcherChangeTypes.Changed, AutoRuleStore.RulesDir, "late.json") });
        Check(ReferenceEquals(cache, type.GetField("_cache", ConcurrencyStatic)!.GetValue(null)),
            "a delayed event from a detached watcher cannot invalidate the replacement cache");
        Directory.CreateDirectory(AutoRuleStore.RulesDir);
        file = new RecoveryCallbackWatcher(gate);
        type.GetField("_watcher", ConcurrencyStatic)!.SetValue(null, file);
        type.GetField("_directoryWritten", ConcurrencyStatic)!.SetValue(null, -1L);
        AutoRuleStore.CheckForExternalChanges();
        Check(file.Released.Task.Wait(TimeSpan.FromSeconds(4)) && file.Released.Task.Result,
            "directory recovery probe releases its retired watcher outside the store lock");
        AutoRuleStore.Shutdown();
    }

    private static void CheckConfigWorkerSnapshots(string output)
    {
        var config = ConfigService.ResetToDefault();
        config.VolumeStep = 7; config.Hotkeys["fixture"] = "published";
        ConfigService.QueueSave(config); ConfigService.FlushPendingSaves();
        string captured = ConfigService.CaptureExportSnapshot();
        long version = (long)typeof(ConfigService).GetField("_saveVersion", ConcurrencyStatic)!.GetValue(null)!;
        string saved = File.ReadAllText(ConfigService.ConfigPath);
        config.Hotkeys["fixture"] = "live edit"; config.VolumeStep = 9;
        string explicitFile = Path.Combine(output, "explicit-export.json"), publishedFile = Path.Combine(output, "published-export.json");
        Check(Task.Run(() => ConfigService.ExportTo(explicitFile, captured)).GetAwaiter().GetResult(),
            "background export accepts an owner-captured immutable snapshot");
        Check(Task.Run(() => ConfigService.ExportTo(publishedFile)).GetAwaiter().GetResult(),
            "background export without a supplied snapshot uses the last published settings");
        using var explicitJson = JsonDocument.Parse(File.ReadAllText(explicitFile));
        using var publishedJson = JsonDocument.Parse(File.ReadAllText(publishedFile));
        Check(explicitJson.RootElement.GetProperty("VolumeStep").GetInt32() == 7
            && publishedJson.RootElement.GetProperty("Hotkeys").GetProperty("fixture").GetString() == "published"
            && version == (long)typeof(ConfigService).GetField("_saveVersion", ConcurrencyStatic)!.GetValue(null)!
            && File.ReadAllText(ConfigService.ConfigPath) == saved,
            "exports neither enumerate live edits nor replace newer saves with a stale background copy");

        var updateType = typeof(App).Assembly.GetType("SonicRoute.Updates.UpdateService")!;
        using var service = (IDisposable)Activator.CreateInstance(updateType, ConcurrencyHidden, null, new object?[] { null }, null)!;
        var record = updateType.GetMethod("RecordCheckAsync", ConcurrencyHidden)!;
        using var posted = new ManualResetEventSlim();
        var recording = Task.Run(async () =>
        {
            var work = (Task)record.Invoke(service, new object[] { false })!;
            posted.Set(); await work;
        });
        Check(posted.Wait(TimeSpan.FromSeconds(3)) && !recording.IsCompleted && config.LastUpdateCheckUtc == DateTime.MinValue,
            "automatic update checks queue settings changes to the owner instead of copying UI dictionaries on a worker");
        Pump(recording.WaitAsync(TimeSpan.FromSeconds(3)));
        Check(config.LastUpdateCheckUtc != DateTime.MinValue, "the queued update preference change completes while the dispatcher remains responsive");
        ConfigService.FlushPendingSaves();
    }

    private static void CheckAudioCacheGenerations()
    {
        var type = typeof(AudioService);
        var fields = new[] { "_appsCache", "_appsCacheAt", "_appsCacheGeneration", "_appsReadTask", "_appsReadGeneration",
            "_deviceCacheRender", "_deviceCacheRenderAt", "_renderCacheGeneration" };
        var originals = fields.ToDictionary(name => name, name => type.GetField(name, ConcurrencyStatic)!.GetValue(null));
        void Set(string name, object value) => type.GetField(name, ConcurrencyStatic)!.SetValue(null, value);
        try
        {
            int generation = (int)type.GetField("_appsGeneration", ConcurrencyStatic)!.GetValue(null)!;
            var current = new List<AudioAppInfo> { new() { ProcessId = 1234567, DisplayName = "current snapshot" } };
            var stale = new TaskCompletionSource<List<AudioAppInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Set("_appsCache", current); Set("_appsCacheAt", Environment.TickCount64); Set("_appsCacheGeneration", generation);
            Set("_appsReadTask", stale.Task); Set("_appsReadGeneration", generation - 1);
            var read = AudioService.GetAppsAsync();
            Check(!ReferenceEquals(read, stale.Task) && ReferenceEquals(read.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(), current),
                "app cache rejects a pending read from an invalidated generation without native enumeration");
            Set("_appsReadTask", Task.FromResult(new List<AudioAppInfo>())); Set("_appsReadGeneration", generation);
            Check(ReferenceEquals(AudioService.GetAppsAsync().GetAwaiter().GetResult(), current),
                "a replaced forced-read snapshot is not mistaken for the cached async result");

            int renderGeneration = (int)type.GetField("_renderDeviceGeneration", ConcurrencyStatic)!.GetValue(null)!;
            Set("_deviceCacheRender", new List<AudioDeviceInfo> { new() { Id = "fixture-output", DisplayName = "original", Flow = EDataFlow.eRender } });
            Set("_deviceCacheRenderAt", Environment.TickCount64); Set("_renderCacheGeneration", renderGeneration);
            var devices = AudioService.GetDevices(EDataFlow.eRender); devices[0].IsDefault = true; devices.Clear();
            var again = AudioService.GetDevices(EDataFlow.eRender);
            Check(again.Count == 1 && again[0].DisplayName == "original" && !again[0].IsDefault, "device cache already protects its list and default flag from caller mutation");
        }
        finally { foreach (var entry in originals) type.GetField(entry.Key, ConcurrencyStatic)!.SetValue(null, entry.Value); }
    }

    private static void CheckTrayTypeBoundaries()
    {
        var parent = new MenuItem();
        var entry = new MenuItem { Tag = "fixture-output", Header = "plain header" };
        parent.Items.Add(entry); parent.Items.Add(new Separator());
        typeof(App).GetMethod("BeginTrayDeviceRead", ConcurrencyStatic)!.Invoke(null, new object[] { parent });
        Check(!entry.IsEnabled && parent.Items[1] is Separator, "tray device refresh tolerates separator items without an invalid cast");
        parent.Items.Clear(); string? selected = null;
        typeof(App).GetMethod("UpdateTrayDeviceItems", ConcurrencyStatic)!.Invoke(null, new object[]
        {
            parent, new[] { new AudioDeviceInfo { Id = "fixture-output", DisplayName = "device", Flow = EDataFlow.eRender } },
            new AppConfig(), "fixture-output", (Action<string, string>)((_, name) => selected = name)
        });
        entry = (MenuItem)parent.Items[0]; entry.Header = "replacement header";
        entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(selected == "replacement header", "tray click reads a replacement header without assuming TextBlock");
    }

    private static void CheckDisposedAudioPolicy()
    {
        var policy = new AudioPolicyConfig(EDataFlow.eRender); policy.Dispose(); policy.Dispose();
        int rejected = 0;
        foreach (Action action in new Action[] { () => policy.GetDefaultEndPoint(1), () => policy.SetDefaultEndPoint("unused", 1), () => policy.ClearAllPersistedApplicationDefaultEndpoints() })
            try { action(); } catch (ObjectDisposedException) { rejected++; }
        Check(rejected == 3, "all disposed policy operations reject before COM activation instead of resurrecting a released factory");
    }

    private static void CheckMediaShutdownJoin()
    {
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var pending = (HashSet<Task>)host.GetField("MediaWatches", ConcurrencyStatic)!.GetValue(null)!;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pending) pending.Add(held.Task);
        try
        {
            var shutdown = (Task)host.GetMethod("StopMediaAsync", ConcurrencyStatic)!.Invoke(null, null)!;
            Check(!shutdown.IsCompleted, "media shutdown returns a pending task instead of synchronously blocking the dispatcher");
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => held.TrySetResult()));
            Pump(shutdown.WaitAsync(TimeSpan.FromSeconds(3)));
            Check(shutdown.IsCompletedSuccessfully, "media shutdown joins pending cleanup while the dispatcher processes messages");
        }
        finally { held.TrySetResult(); lock (pending) pending.Remove(held.Task); }
    }
}

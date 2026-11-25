using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SonicRoute;

internal static partial class Program
{
    private static void CheckUiMemory(App app)
    {
        var type = typeof(App);
        object? Get(string name) => type.GetField(name, Instance)!.GetValue(app);
        void Set(string name, object? value) => type.GetField(name, Instance)!.SetValue(app, value);
        object? Invoke(string name, params object?[] args) => type.GetMethod(name, Instance)!.Invoke(app, args);
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var phoneHost = type.Assembly.GetType("SonicRoute.AndroidAudioHost");
        string framework = type.Assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;
        bool supportsTrim = phoneHost == null && (framework == ".NETFramework,Version=v4.8"
            || framework == ".NETCoreApp,Version=v10.0");
        var imports = type.GetMethods(statics).Select(m => m.GetCustomAttribute<DllImportAttribute>()?.EntryPoint).ToArray();
        Check(imports.Contains("SetProcessWorkingSetSize") == supportsTrim && !imports.Contains("EmptyWorkingSet"),
            "only .NET 10 Lite and Legacy import the controlled working-set call");
        if (supportsTrim)
        {
            Set("_startupWarmupComplete", true);
            Invoke("ReclaimUiWhenIdle", null, EventArgs.Empty);
            Check((long)Get("_lastWorkingSetTrimAt")! == 0, "startup without a window close never requests trim");
            Set("_startupWarmupComplete", false);
        }

        var window = new Window { DataContext = new object() };
        app.MainWindow = window;
        Invoke("ReleaseClosedUiWindow", window);
        Check(app.MainWindow == null && window.DataContext == null,
            "closed window loses the application root and its data context");
        Check(Get("_uiReclaimTimer") == null, "unfinished startup does not schedule idle work");
        Set("_startupWarmupComplete", true);
        Set("_trayMenu", new ContextMenu());
        Invoke("ScheduleUiReclaim");
        Check(Get("_uiReclaimTimer") == null, "an open tray menu prevents idle reclamation");
        Set("_trayMenu", null);
        Invoke("ScheduleUiReclaim");
        var timer = (DispatcherTimer)Get("_uiReclaimTimer")!;
        Check(timer.IsEnabled && timer.Interval == TimeSpan.FromMilliseconds(1500),
            "closed UI reuses the existing single delayed idle timer");
        Invoke("CancelUiReclaim");
        Check(!timer.IsEnabled, "reopening UI cancels the delayed reclamation");
        Invoke("ScheduleUiReclaim");
        Check(ReferenceEquals(timer, Get("_uiReclaimTimer")), "repeated close requests reuse one timer");

        if (supportsTrim)
        {
            void ClosedWindowWhileBusy(string label)
            {
                Invoke("ReleaseClosedUiWindow", window);
                Invoke("ReclaimUiWhenIdle", null, EventArgs.Empty);
                Check((long)Get("_lastWorkingSetTrimAt")! == 0 && !(bool)Get("_trimRequestedOnClose")!
                    && !timer.IsEnabled, label);
            }
            var mirrorType = type.Assembly.GetType("SonicRoute.LocalAppOutputMirrors")!;
            var changes = (System.Threading.SemaphoreSlim)mirrorType.GetField("Changes", statics)!.GetValue(null)!;
            changes.Wait();
            try { ClosedWindowWhileBusy("capture setup and teardown suppress trim without scheduling a retry"); }
            finally { changes.Release(); }
            var routes = (IDictionary)mirrorType.GetField("Routes", statics)!.GetValue(null)!;
            // An unstarted route for this probe process never opens a capture or playback device.
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var routeType = mirrorType.GetNestedType("Route", BindingFlags.NonPublic)!;
            var route = Activator.CreateInstance(routeType, Instance, null, new object[] { process.Id }, null)!;
            routes.Add(process.Id, route);
            try { ClosedWindowWhileBusy("an active local mirror suppresses trim"); }
            finally
            {
                routes.Remove(process.Id);
                ((System.Threading.Tasks.Task)routeType.GetMethod("StopAsync", Instance)!.Invoke(route, null)!).GetAwaiter().GetResult();
            }
            var backend = new System.Threading.Tasks.TaskCompletionSource<bool>();
            var serialize = type.Assembly.GetType("SonicRoute.AppPlaybackOutputs")!.GetMethod("SerializeAsync", statics)!;
            var switching = (System.Threading.Tasks.Task)serialize.MakeGenericMethod(typeof(bool)).Invoke(null,
                new object[] { process.Id, (Func<System.Threading.Tasks.Task<bool>>)(() => backend.Task) })!;
            try { ClosedWindowWhileBusy("an in-flight output transaction suppresses trim"); }
            finally { backend.SetResult(true); switching.GetAwaiter().GetResult(); }
            var write = new System.Threading.Tasks.TaskCompletionSource<bool>();
            type.GetMethod("ProtectAudioWriteFromTrim", statics)!.Invoke(null, new object[] { write.Task });
            ClosedWindowWhileBusy("a committed native audio write outliving its window suppresses trim");
            write.SetResult(true);
            Invoke("ReclaimUiWhenIdle", null, EventArgs.Empty);
            Check((long)Get("_lastWorkingSetTrimAt")! == 0 && !timer.IsEnabled, "finishing a write never retries a skipped trim");
            Invoke("ReleaseClosedUiWindow", window);
        }

        var audio = typeof(SonicRoute.Core.SessionVolumeService);
        var render = audio.GetField("_renderVolumes", statics)!;
        var capture = audio.GetField("_captureVolumes", statics)!;
        var initialRender = render.GetValue(null);
        var initialCapture = capture.GetValue(null);
        var probes = (IDictionary?)phoneHost?.GetField("ProbeTimestamps", statics)!.GetValue(null);
        if (probes != null) probes["fixture"] = 1L;
        int collections = GC.CollectionCount(GC.MaxGeneration);
        Invoke("ReclaimUiWhenIdle", null, EventArgs.Empty);
        Check(GC.CollectionCount(GC.MaxGeneration) == collections,
            "idle reclamation does not induce a full managed collection in any edition");
        Check(ReferenceEquals(initialRender, render.GetValue(null)) && ReferenceEquals(initialCapture, capture.GetValue(null)),
            "UI idle leaves shared rendering and capture volume snapshots in place");
        Check(!timer.IsEnabled && !(bool)Get("_uiResourcesDirty")!,
            "idle work stops its timer and consumes the UI dirty request");
        if (supportsTrim)
        {
            var at = (long)Get("_lastWorkingSetTrimAt")!;
            Check(at != 0 && !(bool)Get("_trimRequestedOnClose")!, "one eligible close makes a native trim attempt");
            Invoke("ReleaseClosedUiWindow", window);
            Invoke("ReclaimUiWhenIdle", null, EventArgs.Empty);
            Check((long)Get("_lastWorkingSetTrimAt")! == at && !timer.IsEnabled, "rapid closes respect the cooldown with no periodic retries");
            long expired = at - 31L * System.Diagnostics.Stopwatch.Frequency;
            Set("_lastWorkingSetTrimAt", expired);
            Invoke("ReclaimUiWhenIdle", null, EventArgs.Empty);
            Check((long)Get("_lastWorkingSetTrimAt")! == expired && !timer.IsEnabled, "cooldown expiration alone does not rearm the timer");
            Invoke("ReleaseClosedUiWindow", window);
            Invoke("ReclaimUiWhenIdle", null, EventArgs.Empty);
            Check((long)Get("_lastWorkingSetTrimAt")! >= at, "a new actual close can trim again after cooldown");
        }
        Invoke("ReclaimUiWhenIdle", null, EventArgs.Empty);
        Check(!timer.IsEnabled, "clean idle state causes no timer restart");
        if (probes != null)
        {
            Check(probes.Contains("fixture"), "UI idle does not reset the phone transport's probe state");
            var retired = type.GetMethod("RequestRetiredPhoneReclaim", Instance)!;
            for (int i = 0; i < 20; i++) retired.Invoke(app, null);
            Check((int)Get("_retiredPhoneReclaimQueued")! == 1, "retired phone resources coalesce one dispatcher request");
            DrainUi();
            Check(timer.IsEnabled && ReferenceEquals(timer, Get("_uiReclaimTimer")),
                "phone retirement reuses the single idle timer after an active stream finishes");
            Invoke("ReclaimUiWhenIdle", null, EventArgs.Empty);
            Check(!probes.Contains("fixture"), "phone retirement retains its existing idle transport cleanup");
        }
        Set("_exiting", true);
        Set("_uiResourcesDirty", true);
        Invoke("ScheduleUiReclaim");
        Check(!timer.IsEnabled, "shutdown does not schedule reclamation");
        Set("_exiting", false);
        window.Close();
    }
}

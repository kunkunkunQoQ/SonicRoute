using System;
using System.Diagnostics;
using System.Reflection;
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
        bool phone = type.Assembly.GetType("SonicRoute.AndroidAudioHost") != null;
        bool enabled = (bool)type.GetField("IdleWorkingSetTrimEnabled", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        Check(enabled == !phone, "working-set policy is enabled only in Lite/Legacy");
        var native = type.GetMethod("NativeSetProcessWorkingSetSize", BindingFlags.Static | BindingFlags.NonPublic);
        Check((native?.GetCustomAttribute<DllImportAttribute>() != null) == enabled,
            "Full does not compile the working-set native call");
        if (enabled) Check(native!.GetCustomAttribute<DllImportAttribute>()!.EntryPoint == "SetProcessWorkingSetSize",
            "Lite/Legacy reuse the v1.29 working-set API");

        var window = new Window { DataContext = new object() };
        app.MainWindow = window;
        Invoke("ReleaseClosedUiWindow", window);
        Check(app.MainWindow == null && window.DataContext == null,
            "closed window loses the application root and its data context");
        Check(Get("_uiReclaimTimer") == null && !(bool)Invoke("TryTrimIdleWorkingSet")!,
            "startup/warmup never requests working-set trimming");
        Set("_startupWarmupComplete", true);
        Set("_trayMenu", new ContextMenu());
        Invoke("ScheduleUiReclaim");
        Check(Get("_uiReclaimTimer") == null && !(bool)Invoke("TryTrimIdleWorkingSet")!,
            "an open tray menu prevents idle reclamation");
        Set("_trayMenu", null);
        Invoke("ScheduleUiReclaim");
        var timer = (DispatcherTimer)Get("_uiReclaimTimer")!;
        Check(timer.IsEnabled && timer.Interval == TimeSpan.FromMilliseconds(1500),
            "closed UI reuses the existing single delayed idle timer");
        Invoke("CancelUiReclaim");
        Check(!timer.IsEnabled, "reopening UI cancels the delayed reclamation");
        Invoke("ScheduleUiReclaim");
        Check(ReferenceEquals(timer, Get("_uiReclaimTimer")), "repeated close requests reuse one timer");

        using var process = Process.GetCurrentProcess();
        process.Refresh(); long beforeWorkingSet = process.WorkingSet64, beforePrivate = process.PrivateMemorySize64;
        bool trimmed = (bool)Invoke("TryTrimIdleWorkingSet")!;
        process.Refresh(); long afterWorkingSet = process.WorkingSet64, afterPrivate = process.PrivateMemorySize64;
        Check(trimmed == enabled, "native trimming succeeds only in the opted-in test process");
        long at = enabled ? (long)Get("_lastWorkingSetTrimAt")! : 0;
        Check(!(bool)Invoke("TryTrimIdleWorkingSet")! && (!enabled || (long)Get("_lastWorkingSetTrimAt")! == at),
            "rapid repeated closes cannot retrim within the cooldown");

        int collections = GC.CollectionCount(GC.MaxGeneration);
        Invoke("ReclaimUiWhenIdle", null, EventArgs.Empty);
        if (enabled) Check(GC.CollectionCount(GC.MaxGeneration) == collections,
            "Lite/Legacy idle reclamation does not force a full managed collection");
        Check(!timer.IsEnabled && !(bool)Get("_uiWorkingSetDirty")!,
            "idle work stops its timer and consumes the UI dirty request");
        if (enabled) Check(!(bool)Get("_uiWorkingSetTrimRequested")!, "working-set request is consumed without a retry loop");
        Invoke("ReclaimUiWhenIdle", null, EventArgs.Empty);
        Check(!timer.IsEnabled && (!enabled || (long)Get("_lastWorkingSetTrimAt")! == at),
            "clean idle state causes no repeated trim or timer restart");
        Set("_exiting", true);
        Set("_uiWorkingSetDirty", true);
        Invoke("ScheduleUiReclaim");
        Check(!timer.IsEnabled, "shutdown does not schedule reclamation");
        Set("_exiting", false);
        window.Close();
        Console.WriteLine($"MEMORY runtime={Environment.Version} phone={phone} trim={trimmed} workingSetBefore={beforeWorkingSet} workingSetAfter={afterWorkingSet} privateBefore={beforePrivate} privateAfter={afterPrivate}");
    }
}

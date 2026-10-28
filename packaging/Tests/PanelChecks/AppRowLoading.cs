using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static object CreateRowNameProbe(QuickPanelModernWindow panel, bool compact, bool deviceName)
    {
        var row = Activator.CreateInstance(panel.GetType().GetNestedType("AppRow", BindingFlags.NonPublic)!, true)!;
        void Set(string name, object value) => row.GetType().GetField(name, Instance)!.SetValue(row, value);
        Set("App", new AudioAppInfo { ProcessId = int.MaxValue - 130, ProcessName = "NameProbe", DisplayName = "Application name" });
        Set("Name", new TextBlock()); Set("Slider", new Slider()); Set("ExpandButton", new ToggleButton());
        Set("LegacyLayout", compact); Set("ShowOutputName", deviceName);
        return row;
    }

    private static List<AudioDeviceInfo> NameProbeDevices() => new[] {
        new AudioDeviceInfo { Id = AudioService.SystemDefaultDeviceId, DisplayName = "System playback", Flow = EDataFlow.eRender }
    }.Concat(Enumerable.Range(0, 64).Select(i => new AudioDeviceInfo { Id = "name-device-" + i,
        DisplayName = "Playback device " + i, Flow = EDataFlow.eRender, IsDefault = i == 63 })).ToList();

    private static void CheckRowLoadingOptimization(MethodInfo theme, List<Window> windows)
    {
        theme.Invoke(null, new object[] { "light", "blue" });
        var panel = new QuickPanelModernWindow(); windows.Add(panel);
        var update = panel.GetType().GetMethod("UpdateRowName", Instance)!;
        foreach (bool compact in new[] { true, false })
        {
            var devices = NameProbeDevices();
            panel.GetType().GetField("_outputDisplay", Instance)!.SetValue(panel, devices);
            var row = CreateRowNameProbe(panel, compact, true);
            var name = Field<TextBlock>(row, "Name");
            void Rename(int index, string text)
            {
                var previous = devices[index];
                devices[index] = new AudioDeviceInfo { Id = previous.Id, DisplayName = text, Flow = previous.Flow, IsDefault = previous.IsDefault };
            }
            void Read(string id) => update.Invoke(panel, new object[] { row, id });
            Read(AudioService.SystemDefaultDeviceId);
            string title = name.Text;
            Check(title == (compact ? devices[0].DisplayName : devices[0].DisplayName + " · " + devices[64].DisplayName),
                "name refresh preserves default title: compact=" + compact);
            for (int i = 0; i < 20; i++) Read(AudioService.SystemDefaultDeviceId);
            Check(ReferenceEquals(title, name.Text), "unchanged metadata preserves displayed text: compact=" + compact);
            Rename(0, "Default route alias"); Read(AudioService.SystemDefaultDeviceId);
            Check(name.Text.StartsWith("Default route alias"), "default-route alias is refreshed: compact=" + compact);
            Rename(64, "Renamed default device"); Read(AudioService.SystemDefaultDeviceId);
            Check(name.Text == (compact ? "Default route alias" : "Default route alias · Renamed default device"),
                "same route detects changed default-device name: compact=" + compact);
            devices[64].IsDefault = false; devices[1].IsDefault = true; Read(AudioService.SystemDefaultDeviceId);
            Check(name.Text == (compact ? "Default route alias" : "Default route alias · Playback device 0"),
                "default endpoint change refreshes suffix: compact=" + compact);
            Rename(1, "Shared alias"); Rename(2, "Shared alias");
            Read(devices[1].Id); Read(devices[2].Id);
            Check(name.Text == "Shared alias" && Field<string>(row, "OutputId") == devices[2].Id,
                "equal aliases still update the cached route target: compact=" + compact);
            Rename(2, "Second endpoint renamed"); Read(Field<string>(row, "OutputId"));
            Check(name.Text == "Second endpoint renamed", "later metadata uses the latest route: compact=" + compact);
            Read("missing-device");
            Check(name.Text == L10n.T("Ov.CurrentUnavailable"), "missing route removes old suffix: compact=" + compact);
        }
        var application = CreateRowNameProbe(panel, false, false);
        var config = ConfigService.Load();
        config.AppNames["NameProbe"] = "Application alias A";
        update.Invoke(panel, new object[] { application, AudioService.SystemDefaultDeviceId });
        config.AppNames["NameProbe"] = "Application alias B";
        update.Invoke(panel, new object[] { application, AudioService.SystemDefaultDeviceId });
        Check(Field<TextBlock>(application, "Name").Text == "Application alias B", "application renames bypass stale name parts");
        Check(Field<Dictionary<int, int>>(panel, "_pendingVolume").Count == 0, "name refresh queues no audio writes");
        panel.Close();

        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var taskField = typeof(AudioService).GetField("_appsReadTask", statics)!;
        var generationField = typeof(AudioService).GetField("_appsReadGeneration", statics)!;
        object? savedTask = taskField.GetValue(null), savedGeneration = generationField.GetValue(null);
        var pending = new TaskCompletionSource<List<AudioAppInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        QuickPanelModernWindow? loading = null;
        try
        {
            generationField.SetValue(null, typeof(AudioService).GetField("_appsGeneration", statics)!.GetValue(null));
            taskField.SetValue(null, pending.Task);
            loading = new QuickPanelModernWindow(); windows.Add(loading);
            var load = loading.GetType().GetMethod("LoadAsync", Instance)!;
            var first = (Task)load.Invoke(loading, null)!;
            var again = (Task)load.Invoke(loading, null)!;
            Check(ReferenceEquals(first, again) && !first.IsCompleted, "overlapped startup keeps its existing single in-flight load");
            loading.Close();
            async Task Complete()
            {
                if (await Task.WhenAny(first, Task.Delay(5000)) != first) throw new TimeoutException("Closed panel load did not cancel");
                await first;
            }
            Finish(Complete());
            Check(Field<bool>(loading, "_isClosed") && Field<Dictionary<int, int>>(loading, "_pendingVolume").Count == 0
                && ((ComboBox)loading.FindName("SystemDevCombo")).ItemsSource == null,
                "close cancels pending startup without audio writes or partial device binding");
            int closedVersion = Field<int>(loading, "_loadVersion");
            pending.TrySetResult(new List<AudioAppInfo>()); DrainUi();
            Finish((Task)load.Invoke(loading, null)!);
            Check(!Field<bool>(loading, "_contentReady") && Field<int>(loading, "_loadVersion") == closedVersion,
                "late shared app results and post-close load requests cannot revive the panel");
        }
        finally
        {
            loading?.Close(); pending.TrySetResult(new List<AudioAppInfo>());
            taskField.SetValue(null, savedTask); generationField.SetValue(null, savedGeneration);
        }
    }

    private static void MeasureRowNames(MethodInfo theme, List<Window> windows)
    {
#if NET8_0_OR_GREATER || NET10_0_OR_GREATER
        theme.Invoke(null, new object[] { "light", "blue" });
        var panel = new QuickPanelModernWindow(); windows.Add(panel);
        panel.GetType().GetField("_outputDisplay", Instance)!.SetValue(panel, NameProbeDevices());
        var update = panel.GetType().GetMethod("UpdateRowName", Instance)!;
        foreach (var item in new[] { (Name: "compact-default", Compact: true, Device: true),
            (Name: "full-default", Compact: false, Device: true), (Name: "application", Compact: false, Device: false) })
        {
            var row = CreateRowNameProbe(panel, item.Compact, item.Device);
            var arguments = new object[] { row, AudioService.SystemDefaultDeviceId };
            for (int i = 0; i < 2000; i++) update.Invoke(panel, arguments);
            for (int sample = 0; sample < 5; sample++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
                for (int i = 0; i < 20000; i++) update.Invoke(panel, arguments);
                timer.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Console.WriteLine("NAME_METRIC " + JsonSerializer.Serialize(new { item.Name, Sample = sample,
                    Calls = 20000, Devices = 65, WallMs = timer.Elapsed.TotalMilliseconds, AllocatedBytes = allocated }));
            }
        }
        panel.Close();
#else
        throw new NotSupportedException("Thread-allocation probe requires NET8 or NET10.");
#endif
    }
}

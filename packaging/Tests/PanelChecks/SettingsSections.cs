using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void SetField(object value, string name, object data) => value.GetType().GetField(name, Instance)!.SetValue(value, data);
    private static ItemsControl Items(MainWindow window, string name) => Field<ItemsControl>(window, name);
    private static List<AudioDeviceInfo> Devices(int count, EDataFlow flow) => Enumerable.Range(0, count)
        .Select(index => new AudioDeviceInfo { Id = flow + "-fixture-" + index, DisplayName = "设备 " + index + " (Test device)", Flow = flow }).ToList();
    private static void SeedSections(MainWindow window, int count)
    {
        SetField(window, "_outputs", Devices(count, EDataFlow.eRender));
        SetField(window, "_inputs", Devices(Math.Max(1, count / 2), EDataFlow.eCapture));
        SetField(window, "_inputDevicesLoaded", true);
        var config = ConfigService.Load(); config.ExperimentalMic = true;
        config.HiddenOutputDevices.Clear(); config.HiddenInputDevices.Clear(); config.DeviceNames.Clear();
    }
    private static TextBox NameBox(object row) => (TextBox)((Grid)((Border)row).Child).Children[1];

    private static void CheckSettingsSections(MainWindow window, string mode, string directory)
    {
        SeedSections(window, 8);
        Field<Grid>(window, "SettingsPage").Visibility = Visibility.Visible;
        var config = ConfigService.Load(); config.CollapseDeviceSections = true;
        Call(window, "EnsureSettingsDeviceSections");
        Check(Items(window, "OutputFilterList").Items.Count == 0 && Items(window, "OutputNameList").Items.Count == 0,
            mode + " folded sections do not create rows");
        Field<System.Windows.Controls.Primitives.ToggleButton>(window, "KeepDevicesMoreToggle").IsChecked = true;
        Field<System.Windows.Controls.Primitives.ToggleButton>(window, "DeviceNamesMoreToggle").IsChecked = true;
        Call(window, "EnsureSettingsDeviceSections");
        var filters = Items(window, "OutputFilterList"); var names = Items(window, "OutputNameList");
        var kept = filters.Items[2]; var nameRow = names.Items[2]; var box = NameBox(nameRow);
        var label = ((Grid)((Border)nameRow).Child).Children[0];
        Check(filters.Items.Count == 9 && Items(window, "InputFilterList").Items.Count == 5, mode + " expanded sections include virtual defaults and enabled inputs");
        string Title(UIElement value) => ((TextBlock)((StackPanel)((Grid)value).Children[1]).Children[0]).Text;
        TextBlock Detail(UIElement value) => (TextBlock)((StackPanel)((Grid)value).Children[1]).Children[1];
        var filterLabel = (Grid)((CheckBox)kept).Content;
        var detail = Detail(filterLabel); string detailText = detail.Text;
        box.Text = "聊天 (自定义)";
        window.GetType().GetMethod("FlushNameChanges", Instance)!.Invoke(window, new object[] { true });
        Check(Title(filterLabel) == "聊天 (自定义)" && Title(label) == "聊天 (自定义)", mode + " committed alias updates both card headings verbatim");
        Check(ReferenceEquals(detail, Detail(filterLabel)) && detail.Text == detailText && filterLabel.ToolTip!.ToString()!.Contains("Test device"),
            mode + " alias preserves original lower device description and tooltip");
        Check(ReferenceEquals(box, NameBox(names.Items[2])) && ((AudioDeviceInfo)((CheckBox)kept).Tag).Id == "eRender-fixture-1",
            mode + " alias update preserves editor and route ID");
        box.Text = "  "; window.GetType().GetMethod("FlushNameChanges", Instance)!.Invoke(window, new object[] { true });
        Check(Title(filterLabel) == "设备 1" && detail.Text == detailText, mode + " clearing alias restores original upper title only");
        config.DeviceNames[AudioService.SystemDefaultDeviceId] = "系统输出别名";
        Call(window, "RefreshSettingsDeviceTitles");
        Check(Title((Grid)((CheckBox)filters.Items[0]).Content) == "系统输出别名", mode + " virtual default device honors its own alias");
        config.DeviceNames.Remove(AudioService.SystemDefaultDeviceId); Call(window, "RefreshSettingsDeviceTitles");
        var seen = new HashSet<string>(); object gate = new(); bool overflow = false;
        using var barrier = new ManualResetEventSlim();
        using var watcher = new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.FileName, InternalBufferSize = 65536 };
        string marker = "";
        watcher.Created += (_, e) => { lock (gate) { if (e.Name!.StartsWith("config.json.") && e.Name.EndsWith(".tmp")) seen.Add(e.Name); if (e.Name == marker) barrier.Set(); } };
        watcher.Error += (_, _) => overflow = true; watcher.EnableRaisingEvents = true;
        int Observe(Action action)
        {
            int before; lock (gate) before = seen.Count;
            action(); barrier.Reset(); marker = "barrier-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(directory, marker), "done");
            if (!barrier.Wait(3000) || overflow) throw new Exception("File notification barrier failed");
            lock (gate) return seen.Count - before;
        }
        Check(Observe(() => { Call(window, "BuildDeviceFilter"); Call(window, "BuildDeviceNameLists"); }) == 0,
            mode + " synchronizing existing rows never saves config");
        Check(ReferenceEquals(kept, filters.Items[2]) && ReferenceEquals(nameRow, names.Items[2])
            && ReferenceEquals(label, ((Grid)((Border)nameRow).Child).Children[0]), mode + " unchanged rows and labels keep their actual controls");
        config.HiddenOutputDevices.Add("offline-output"); config.HiddenInputDevices.Add("offline-input");
        int Toggle(ItemsControl list) => Observe(() => window.GetType().GetMethod("ToggleSelectAll", Instance)!.Invoke(window,
            new object[] { list.Items.OfType<CheckBox>().ToList() }));
        Check(Toggle(filters) == 1 && filters.Items.OfType<CheckBox>().All(check => check.IsChecked == false), mode + " clear-all nine outputs performs exactly one atomic save");
        Check(Toggle(filters) == 1 && filters.Items.OfType<CheckBox>().All(check => check.IsChecked == true), mode + " select-all nine outputs performs exactly one atomic save");
        var inputs = Items(window, "InputFilterList");
        Check(Toggle(inputs) == 1 && config.HiddenInputDevices.Contains("offline-input") && config.HiddenOutputDevices.Contains("offline-output"),
            mode + " batch input selection preserves absent-device preferences with one save");
        Check(Observe(() => ((CheckBox)filters.Items[1]).IsChecked = false) == 1, mode + " individual checkbox still saves immediately");
        box.Text = "中文 draft  "; box.Select(2, 3);
        string language = L10n.CurrentLanguage; L10n.Instance.SetLanguage("en-US");
        Call(window, "BuildDeviceNameLists");
        Check(ReferenceEquals(box, NameBox(names.Items[2])) && box.Text == "中文 draft  " && box.SelectionStart == 2 && box.SelectionLength == 3,
            mode + " language refresh keeps edit control, untrimmed draft and selection");
        L10n.Instance.SetLanguage(language);
        var changed = Devices(8, EDataFlow.eRender);
        changed[1] = new AudioDeviceInfo { Id = changed[1].Id, DisplayName = "新设备名称 (Changed)", Flow = EDataFlow.eRender };
        SetField(window, "_outputs", changed); Call(window, "BuildDeviceNameLists");
        Check(ReferenceEquals(nameRow, names.Items[2]) && !ReferenceEquals(label, ((Grid)((Border)nameRow).Child).Children[0])
            && ReferenceEquals(box, NameBox(names.Items[2])), mode + " device label updates without replacing its editor");
        changed.RemoveAt(7); changed.Insert(0, new AudioDeviceInfo { Id = "new-output", DisplayName = "New", Flow = EDataFlow.eRender });
        Call(window, "BuildDeviceFilter"); Call(window, "BuildDeviceNameLists");
        Check(ReferenceEquals(nameRow, names.Items[3]) && ReferenceEquals(kept, filters.Items[3]) && names.Items.Count == 9,
            mode + " device addition and removal retain surviving rows in device order");
        config.ExperimentalMic = false; Call(window, "BuildDeviceFilter"); Call(window, "BuildDeviceNameLists");
        Check(inputs.Items.Count == 0 && Items(window, "InputNameList").Items.Count == 0, mode + " disabled microphone creates no hidden input rows");
        config.ExperimentalMic = true;
        window.GetType().GetMethod("ApplyExpMicUi", Instance)!.Invoke(window, new object[] { true });
        Check(inputs.Items.Count == 5 && Items(window, "InputNameList").Items.Count == 5,
            mode + " enabling microphone restores input rows using already loaded devices");
        var body = Field<StackPanel>(window, "KeepDevicesBody"); var host = (AnimatedExpandHost)body.Parent;
        var expand = typeof(MainWindow).GetMethod("SetPanelExpanded", BindingFlags.NonPublic | BindingFlags.Static)!;
        void Expand(bool on) => expand.Invoke(null, new object[] { body, on, false });
        Expand(false); int version = Field<int>(host, "_version");
        for (int i = 0; i < 20; i++) Expand(false);
        Check(Field<int>(host, "_version") == version && body.Visibility == Visibility.Collapsed,
            mode + " duplicate collapsed requests skip clock and layout state resets");
        Expand(true); version = Field<int>(host, "_version");
        for (int i = 0; i < 20; i++) Expand(true);
        Check(Field<int>(host, "_version") == version && body.Visibility == Visibility.Visible && double.IsNaN(host.RevealHeight),
            mode + " duplicate expanded requests retain natural-height layout");
        Expand(false); Expand(true); host.FinishImmediately();
        Check(body.Visibility == Visibility.Visible && body.Opacity == 1 && !host.IsAnimating,
            mode + " opposite requests still apply final visibility and stop clocks");
    }

    private static void MeasureSections(List<Window> windows, string[] args)
    {
        var size = args.FirstOrDefault(arg => arg.StartsWith("--device-count="));
        foreach (int count in size == null ? new[] { 8, 48 } : new[] { int.Parse(size.Substring("--device-count=".Length)) })
        {
            var window = new MainWindow(); windows.Add(window); Call(window, "EnsureSettingsPage"); SeedSections(window, count);
            void Refresh() { Call(window, "BuildDeviceFilter"); Call(window, "BuildDeviceNameLists"); }
#if NET10_0_OR_GREATER || NET8_0_OR_GREATER
            long firstAllocated = GC.GetAllocatedBytesForCurrentThread();
#endif
            var firstTimer = Stopwatch.StartNew(); Refresh(); firstTimer.Stop();
#if NET10_0_OR_GREATER || NET8_0_OR_GREATER
            firstAllocated = GC.GetAllocatedBytesForCurrentThread() - firstAllocated;
            Console.WriteLine($"METRIC_FIRST devices={count} wallMs={firstTimer.Elapsed.TotalMilliseconds:F3} allocatedBytes={firstAllocated}");
#endif
            Refresh();
#if NET10_0_OR_GREATER || NET8_0_OR_GREATER
            long allocated = GC.GetAllocatedBytesForCurrentThread();
#endif
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) Refresh();
            timer.Stop();
#if NET10_0_OR_GREATER || NET8_0_OR_GREATER
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine($"METRIC devices={count} refreshes=20 wallMs={timer.Elapsed.TotalMilliseconds:F3} allocatedBytes={allocated}");
#endif
            int writes = ObserveAtomicWrites(Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!, () =>
                window.GetType().GetMethod("ToggleSelectAll", Instance)!.Invoke(window,
                    new object[] { Items(window, "OutputFilterList").Items.OfType<CheckBox>().ToList() }));
            Console.WriteLine($"METRIC_BATCH devices={count} rows={count + 1} atomicWrites={writes}");
            var body = Field<StackPanel>(window, "KeepDevicesBody"); var host = (AnimatedExpandHost)body.Parent;
            var set = typeof(MainWindow).GetMethod("SetPanelExpanded", BindingFlags.NonPublic | BindingFlags.Static)!;
            set.Invoke(null, new object[] { body, false, false }); int version = Field<int>(host, "_version");
            for (int i = 0; i < 32; i++) set.Invoke(null, new object[] { body, false, false });
            Console.WriteLine($"METRIC_EXPAND devices={count} requests=32 resets={Field<int>(host, "_version") - version}");
            window.Close();
        }
    }

    private static int ObserveAtomicWrites(string directory, Action action)
    {
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        using var ready = new ManualResetEventSlim(); bool overflow = false;
        string marker = "barrier-" + Guid.NewGuid().ToString("N");
        using var watcher = new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.FileName, InternalBufferSize = 65536 };
        watcher.Created += (_, e) => { if (e.Name!.StartsWith("config.json.") && e.Name.EndsWith(".tmp")) seen.TryAdd(e.Name, 0); if (e.Name == marker) ready.Set(); };
        watcher.Error += (_, _) => overflow = true; watcher.EnableRaisingEvents = true;
        action(); File.WriteAllText(Path.Combine(directory, marker), "done");
        if (!ready.Wait(3000) || overflow) throw new Exception("Atomic write observation failed");
        return seen.Count;
    }
}

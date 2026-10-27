using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void CheckOutputOptimization(MethodInfo theme, List<Window> windows)
    {
        foreach (string mode in new[] { "light", "dark" })
        {
            theme.Invoke(null, new object[] { mode, "blue" });
            var panel = new QuickPanelModernWindow(); windows.Add(panel);
            object? Invoke(string name, params object?[] values) => panel.GetType().GetMethod(name, Instance)!.Invoke(panel, values);
            void Set(object target, string name, object value) => target.GetType().GetField(name, Instance)!.SetValue(target, value);
            var devices = new List<AudioDeviceInfo>
            {
                new() { Id = "optimization-no-device-a", DisplayName = "Speakers", Flow = EDataFlow.eRender },
                new() { Id = "optimization-no-device-b", DisplayName = "Headphones", Flow = EDataFlow.eRender }
            };
            Call(panel, "CreateOutputPopupContent");
            Invoke("SetSystemDevices", devices, devices[0].Id);
            Invoke("BuildOutputVolumeRows", devices, devices[0].Id);
            var rows = Field<IDictionary>(panel, "_outputVolumeRows");
            var row = rows[devices[0].Id]!;
            var target = Field<object>(row, "Target");
            var load = Activator.CreateInstance(panel.GetType().GetNestedType("OutputVolumeLoad", BindingFlags.NonPublic)!, true)!;
            Set(panel, "_outputLoad", load);
            var requested = Field<HashSet<string>>(load, "ReadDevices");
            Invoke("ApplyOutputVolumeState", row, 50, false);
            Set(row, "Pending", true); Set(target, "Version", 1);
            var versions = Invoke("CaptureOutputVolumeVersions")!;
            Invoke("ApplyOutputVolumeSnapshot", new Dictionary<string, (int, bool)> { [devices[0].Id] = (30, true) }, versions);
            Check(Field<Slider>(row, "Slider").Value == 50, mode + " pending volume keeps the user's current slider value");
            Check(Field<bool>(row, "NeedsReadback"), mode + " pending snapshot retains the consumed external mute notification for readback");
            Check(requested.Count == 0, mode + " pending action does not spin a deferred read loop");
            Invoke("ApplyOutputVolumeWriteReadback", target, 1, 70);
            Check(!Field<bool>(row, "Pending") && requested.SetEquals(new[] { devices[0].Id }),
                mode + " completed write requests only its skipped endpoint");
            Check(Field<int>(target, "Version") == 2, mode + " confirmed write advances monitor protection once");
            requested.Clear();
            Invoke("ApplyOutputVolumeSnapshot", new Dictionary<string, (int, bool)> { [devices[0].Id] = (30, true) }, versions);
            Check(Field<Slider>(row, "Slider").Value == 70 && requested.Contains(devices[0].Id),
                mode + " a late snapshot cannot overwrite a confirmed write and still retains its mute change");
            requested.Clear();
            Invoke("ApplyOutputVolumeSnapshot", new Dictionary<string, (int, bool)> { [devices[0].Id] = (70, true) }, Invoke("CaptureOutputVolumeVersions"));
            Check(Field<bool>(row, "Muted") && Field<TextBlock>(row, "Percent").Text == "70%",
                mode + " targeted readback reconciles the external mute without changing volume");
            var mute = Field<Button>(row, "Mute");
            var expression = mute.ReadLocalValue(Button.ForegroundProperty);
            int beforeVersion = Field<int>(target, "Version");
            for (int i = 0; i < 20; i++)
                Invoke("ApplyOutputVolumeSnapshot", new Dictionary<string, (int, bool)> { [devices[0].Id] = (70, true) }, Invoke("CaptureOutputVolumeVersions"));
            Check(Field<int>(target, "Version") == beforeVersion && ReferenceEquals(expression, mute.ReadLocalValue(Button.ForegroundProperty)),
                mode + " selected-device snapshots avoid self-sync and repeated mute resource expressions");
            Set(row, "MuteBusy", true); Set(row, "MuteVersion", 1);
            var beforeMute = Invoke("CaptureOutputVolumeVersions");
            Invoke("ApplyOutputVolumeSnapshot", new Dictionary<string, (int, bool)> { [devices[0].Id] = (70, false) }, beforeMute);
            Check(Field<bool>(row, "NeedsReadback") && requested.Count == 0, mode + " in-flight mute defers consumed state without extra reads");
            Invoke("ApplyOutputMuteReadback", row, true, true);
            Check(requested.SetEquals(new[] { devices[0].Id }) && !Field<bool>(row, "MuteBusy"), mode + " mute completion schedules the same targeted reconciliation");
            requested.Clear();
            Invoke("ApplyOutputVolumeSnapshot", new Dictionary<string, (int, bool)> { [devices[0].Id] = (70, false) }, beforeMute);
            Check(Field<bool>(row, "Muted") && requested.Contains(devices[0].Id), mode + " late pre-mute state cannot replace confirmed mute");
            requested.Clear();
            var sync = panel.GetType().GetMethod("ApplySystemVolumeReadback", Instance, null,
                new[] { typeof(string), typeof(int), typeof(bool) }, null)!;
            int externalVersion = Field<int>(target, "Version");
            sync.Invoke(panel, new object[] { devices[0].Id, 75, false });
            Check(Field<int>(target, "Version") == externalVersion + 1 && Field<Slider>(row, "Slider").Value == 75
                && !Field<bool>(row, "Muted"), mode + " external main-panel readback still synchronizes once with version protection");
            Set(row, "Pending", true);
            sync.Invoke(panel, new object[] { devices[0].Id, 20, true });
            Check(Field<Slider>(row, "Slider").Value == 75 && Field<bool>(row, "NeedsReadback"), mode + " main-panel readback also retains changes skipped during a pending write");
            Invoke("ApplyOutputVolumeWriteReadback", target, Field<int>(target, "Version"), 75);
            requested.Clear();

            var combo = (ComboBox)panel.FindName("SystemDevCombo");
            var source = combo.ItemsSource; var selected = combo.SelectedItem;
            int selections = 0; combo.SelectionChanged += (_, _) => selections++;
            List<AudioDeviceInfo> Snapshot(string name = "Speakers") => new()
            {
                new() { Id = devices[0].Id, DisplayName = name, Flow = EDataFlow.eRender },
                new() { Id = devices[1].Id, DisplayName = "Headphones", Flow = EDataFlow.eRender }
            };
            Invoke("ApplyOutputDeviceSnapshot", Snapshot(), devices[0].Id, ConfigService.Load());
            Check(ReferenceEquals(source, combo.ItemsSource) && ReferenceEquals(selected, combo.SelectedItem) && selections == 0,
                mode + " identical topology preserves ItemsSource and selection without rebinding");
            var title = Field<TextBlock>(row, "Name").Text;
            Invoke("BuildOutputVolumeRows", Snapshot(), devices[0].Id);
            Check(ReferenceEquals(row, rows[devices[0].Id]) && Field<TextBlock>(row, "Name").Text == title,
                mode + " identical metadata reuses the existing device row");
            Invoke("ApplyOutputDeviceSnapshot", Snapshot("Renamed speaker"), devices[1].Id, ConfigService.Load());
            Invoke("BuildOutputVolumeRows", Snapshot("Renamed speaker"), devices[1].Id);
            Check(!ReferenceEquals(source, combo.ItemsSource) && Field<string>(panel, "_systemDeviceId") == devices[0].Id
                && Field<TextBlock>(row, "Name").Text == "Renamed speaker"
                && Field<TextBlock>(rows[devices[1].Id]!, "Name").Text.Contains(L10n.T("Qp.DefaultOutputBadge")),
                mode + " changed name/default updates metadata while retaining the controlled device");
            Check(Field<IDictionary>(panel, "_pendingVolume").Count == 0 && Field<IDictionary>(panel, "_pendingOutputVolume").Count == 0,
                mode + " reconciliation and metadata checks execute no audio writes");
            panel.Close();
        }
    }
}

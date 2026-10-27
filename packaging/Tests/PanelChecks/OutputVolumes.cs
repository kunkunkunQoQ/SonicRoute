using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void CheckOutputVolumes(MethodInfo theme, List<Window> windows, string directory)
    {
        Directory.CreateDirectory(directory);
        var config = ConfigService.Load();
        Check(!config.QuickPanelShowAllOutputVolumes, "old configuration starts with extra panel disabled");
        var arrange = typeof(App).Assembly.GetType("SonicRoute.OutputVolumePopupLayout")!.GetMethod("Arrange", BindingFlags.NonPublic | BindingFlags.Static)!;
        Rect Place(Rect owner, Rect work, double height = 240) => (Rect)arrange.Invoke(null, new object[] { owner, work, new Size(366, height) })!;
        var area = new Rect(0, 0, 1920, 1040);
        foreach (var entry in new[]
        {
            (Name: "top left", Owner: new Rect(12, 10, 372, 450), Side: "right"),
            (Name: "top right", Owner: new Rect(1536, 10, 372, 450), Side: "left"),
            (Name: "bottom left", Owner: new Rect(12, 580, 372, 450), Side: "above"),
            (Name: "bottom right", Owner: new Rect(1536, 580, 372, 450), Side: "above")
        })
        {
            var placed = Place(entry.Owner, area);
            Check(area.Contains(placed) && !placed.IntersectsWith(entry.Owner), entry.Name + " extra panel stays inside work area without moving owner");
            Check(entry.Side == "right" ? placed.Left >= entry.Owner.Right : entry.Side == "left"
                ? placed.Right <= entry.Owner.Left : placed.Bottom <= entry.Owner.Top, entry.Name + " chooses " + entry.Side);
        }
        var small = Place(new Rect(10, 140, 372, 450), new Rect(0, 0, 500, 600), 450);
        Check(small.Height < 450 && small.Height > 60 && new Rect(0, 0, 500, 600).Contains(small), "limited space constrains extra height for scrolling");
        var negativeWork = new Rect(-1920, -1040, 1920, 1040);
        Check(negativeWork.Contains(Place(new Rect(-1900, -1020, 372, 450), negativeWork)), "negative monitor coordinates remain in work area");

        foreach (string mode in new[] { "light", "dark" })
        {
            theme.Invoke(null, new object[] { mode, "blue" });
            foreach (string lang in new[] { "zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "ru-RU" })
            {
                L10n.Instance.SetLanguage(lang);
                var panel = new QuickPanelModernWindow(); windows.Add(panel);
                var popup = (Popup)panel.FindName("OutputVolumesPopup");
                var toggle = (ToggleButton)panel.FindName("OutputVolumesToggle");
                Check(popup.Child == null && Field<object?>(panel, "_outputVolumeMonitor") == null
                    && Field<object?>(panel, "_outputRecoveryTimer") == null, mode + "/" + lang + " collapsed panel creates no extra controls/monitor/recovery timer");
                Call(panel, "CreateOutputPopupContent");
                Check(Field<object?>(panel, "_outputVolumeMonitor") == null, mode + "/" + lang + " extra control construction performs no audio subscription");
                var devices = new List<AudioDeviceInfo>
                {
                    new() { Id = "nonexistent-speakers", DisplayName = "音箱 / Speakers", Flow = EDataFlow.eRender },
                    new() { Id = "nonexistent-headphones", DisplayName = "耳机 / Headphones — very long device name", Flow = EDataFlow.eRender }
                };
                var bind = panel.GetType().GetMethod("SetSystemDevices", Instance)!;
                bind.Invoke(panel, new object[] { devices, devices[0].Id });
                var build = panel.GetType().GetMethod("BuildOutputVolumeRows", Instance)!;
                build.Invoke(panel, new object[] { devices, devices[0].Id });
                var rows = Field<IDictionary>(panel, "_outputVolumeRows");
                var pending = Field<IDictionary>(panel, "_pendingOutputVolume");
                var first = rows[devices[0].Id]!; var second = rows[devices[1].Id]!;
                var readback = panel.GetType().GetMethod("ApplyOutputVolumeState", Instance)!;
                readback.Invoke(panel, new object[] { first, 35, false });
                readback.Invoke(panel, new object[] { second, 60, true });
                Check(rows.Count == 2 && pending.Count == 0 && Field<TextBlock>(first, "Name").Text.Contains(L10n.T("Qp.DefaultOutputBadge")),
                    mode + "/" + lang + " initial state uses explicit devices and default badge without writes");
                var surface = Field<Border>(panel, "_outputPopupSurface");
                surface.Width = 366; surface.Height = 186;
                surface.Measure(new Size(366, 186)); surface.Arrange(new Rect(0, 0, 366, 186)); surface.UpdateLayout();
                Check(Field<Slider>(first, "Slider").ActualWidth > 100 && Field<Button>(first, "Mute").ActualWidth >= 54,
                    mode + "/" + lang + " device controls keep usable width");
                var bitmap = new RenderTargetBitmap(366, 186, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(directory, mode + "-" + lang + ".png"))) encoder.Save(stream);

                var slider = Field<Slider>(second, "Slider");
                slider.Value = 71; slider.Value = 72;
                Check(pending.Count == 1 && pending.Contains(devices[1].Id)
                    && ((AudioDeviceInfo)((ComboBox)panel.FindName("SystemDevCombo")).SelectedItem).Id == devices[0].Id,
                    mode + "/" + lang + " rapid headphone changes merge without selecting/changing default output");
                var target = Field<object>(second, "Target");
                int version = Field<int>(target, "Version");
                panel.GetType().GetMethod("ApplyOutputVolumeWriteReadback", Instance)!.Invoke(panel, new[] { target, (object)(version - 1), 10 });
                Check(slider.Value == 72 && Field<bool>(second, "Pending"), mode + "/" + lang + " old write readback cannot replace latest slider value");
                // These are deliberately missing endpoint IDs; do not execute the queued writes.
                pending.Clear(); Field<System.Windows.Threading.DispatcherTimer>(panel, "_volDebounce").Stop();
                panel.GetType().GetMethod("ApplyOutputVolumeWriteReadback", Instance)!.Invoke(panel, new[] { target, (object)version, 72 });
                Check(!Field<bool>(second, "Pending") && Field<Slider>(first, "Slider").Value == 35,
                    mode + "/" + lang + " matching readback completes only its target");
                slider.Value = 73;
                build.Invoke(panel, new object[] { new List<AudioDeviceInfo> { devices[0] }, devices[0].Id });
                Check(!Field<bool>(target, "Available") && pending.Count == 0 && ReferenceEquals(rows[devices[0].Id], first),
                    mode + "/" + lang + " unplug discards pending target and reuses remaining device control");
                Call(panel, "StopOutputVolumes");
                Check(popup.Child == null && rows.Count == 0 && Field<object?>(panel, "_outputRecoveryTimer") == null,
                    mode + "/" + lang + " collapse releases extra controls and recovery state");
                panel.Close();
            }
        }
        config.QuickPanelShowAllOutputVolumes = true; ConfigService.Save(config);
        var restored = new QuickPanelModernWindow(); windows.Add(restored);
        Check(((ToggleButton)restored.FindName("OutputVolumesToggle")).IsChecked == true
            && ((Popup)restored.FindName("OutputVolumesPopup")).Child == null, "saved expansion preference restores without loading before visible");
        restored.Close();
        Check(ConfigService.Load().QuickPanelShowAllOutputVolumes, "closing window preserves expansion preference");
        config.QuickPanelShowAllOutputVolumes = false; ConfigService.Save(config);
    }
}

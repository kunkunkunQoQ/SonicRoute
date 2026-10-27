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
    private static IEnumerable<DependencyObject> OutputVisuals(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var item in OutputVisuals(child)) yield return item;
        }
    }

    private static void SaveOutputPreview(string path, double width, params FrameworkElement[] controls)
    {
        double height = 0;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            foreach (var control in controls)
            {
                control.Measure(new Size(width, double.PositiveInfinity));
                double h = control.DesiredSize.Height;
                control.Arrange(new Rect(0, 0, width, h)); control.UpdateLayout();
                context.DrawRectangle(new VisualBrush(control), null, new Rect(0, height, width, h));
                height += h;
            }
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void CheckOutputFollowup(MethodInfo theme, List<Window> windows, string directory)
    {
        Directory.CreateDirectory(directory);
        var config = ConfigService.Load();
        Check(config.PanelChangeSystemDefault && !config.PanelOutputVolumesRetainedOnly,
            "old config defaults to system-default switching on and retained-only off");
        var devices = new List<AudioDeviceInfo>
        {
            new() { Id = "device-a", DisplayName = "音箱 / Speakers", Flow = EDataFlow.eRender, IsDefault = true },
            new() { Id = "device-b", DisplayName = "耳机 / Headphones", Flow = EDataFlow.eRender },
            new() { Id = AudioService.SystemDefaultDeviceId, Flow = EDataFlow.eRender },
            new() { Id = "capture-device", Flow = EDataFlow.eCapture }
        };
        var select = typeof(QuickPanelModernWindow).GetMethod("OutputVolumeDevices", BindingFlags.Static | BindingFlags.NonPublic)!;
        List<AudioDeviceInfo> Selected() => (List<AudioDeviceInfo>)select.Invoke(null, new object[] { devices, config })!;
        config.HiddenOutputDevices = new List<string> { "DEVICE-B" };
        Check(Selected().Count == 2, "retained-only off shows all real render devices without virtual/capture entries");
        config.PanelOutputVolumesRetainedOnly = true; config.PanelChangeSystemDefault = false;
        Check(Selected().Count == 1 && Selected()[0].Id == "device-a", "retained-only uses existing hidden devices case-insensitively and independently of default switching");
        config.HiddenOutputDevices = null!;
        Check(Selected().Count == 2, "older null hidden list is treated as retaining every real output");
        config.HiddenOutputDevices = new List<string> { "device-b" };
        config.PanelOutputVolumesRetainedOnly = false; config.PanelChangeSystemDefault = true;

        foreach (string mode in new[] { "light", "dark" })
        {
            theme.Invoke(null, new object[] { mode, "blue" });
            foreach (string lang in new[] { "zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "ru-RU" })
            {
                L10n.Instance.SetLanguage(lang);
                var panel = new QuickPanelModernWindow(); windows.Add(panel);
                var toggle = (ToggleButton)panel.FindName("OutputVolumesToggle");
                Call(panel, "RefreshOutputVolumeFilter");
                Check(Field<object?>(panel, "_outputPopupSurface") == null && Field<object?>(panel, "_outputVolumeMonitor") == null,
                    mode + "/" + lang + " changing filter while collapsed creates no extra controls or subscriptions");
                Check(ReferenceEquals(toggle.Background, panel.FindResource("Theme.SurfaceBg"))
                    && ((SolidColorBrush)toggle.Background).Color.A == 255,
                    mode + "/" + lang + " expand button uses opaque themed surface background");
                Call(panel, "CreateOutputPopupContent");
                var build = typeof(QuickPanelModernWindow).GetMethod("BuildOutputVolumeRows", Instance)!;
                build.Invoke(panel, new object[] { Selected(), "device-a" });
                var rows = Field<System.Collections.IDictionary>(panel, "_outputVolumeRows");
                var apply = typeof(QuickPanelModernWindow).GetMethod("ApplyOutputVolumeState", Instance)!;
                apply.Invoke(panel, new[] { rows["device-a"]!, (object)35, false });
                apply.Invoke(panel, new[] { rows["device-b"]!, (object)60, true });
                var surface = Field<Border>(panel, "_outputPopupSurface");
                surface.Width = 366; surface.Height = 186;
                SaveOutputPreview(Path.Combine(directory, mode + "-" + lang + "-extra.png"), 366, surface);
                Check(!OutputVisuals(surface).OfType<Button>().Any(button => Equals(button.Content, "×")),
                    mode + "/" + lang + " extra panel has no redundant close button");
                typeof(QuickPanelModernWindow).GetMethod("SetSystemDevices", Instance)!.Invoke(panel, new object[] { Selected(), "device-a" });
                SaveOutputPreview(Path.Combine(directory, mode + "-" + lang + "-toggle.png"), 338, (FrameworkElement)toggle.Parent);

                var main = new MainWindow(); windows.Add(main);
                Call(main, "EnsureSettingsPage"); Call(main, "LoadSettings");
                var normal = Field<CheckBox>(main, "SettingsPanelChangeSysDef");
                var retained = Field<CheckBox>(main, "SettingsPanelOutputVolumesRetainedOnly");
                var parent = normal.Parent as Panel;
                Check(normal.IsChecked == true && retained.IsChecked == false && parent != null
                    && ReferenceEquals(parent, retained.Parent) && parent.Children.IndexOf(retained) == parent.Children.IndexOf(normal) + 1
                    && retained.Margin.Left == normal.Margin.Left && retained.Style == normal.Style,
                    mode + "/" + lang + " retained-only is the next independent settings row with existing switch style");
                config.PanelChangeSystemDefault = false; config.PanelOutputVolumesRetainedOnly = true;
                Call(main, "LoadSettings");
                Check(normal.IsChecked == false && retained.IsChecked == true && retained.IsEnabled,
                    mode + "/" + lang + " saved default-switching off does not disable retained-only option");
                SaveOutputPreview(Path.Combine(directory, mode + "-" + lang + "-settings.png"), 620, normal, retained);
                main.Close();
                Check(Field<object?>(main, "SettingsPanelOutputVolumesRetainedOnly") == null,
                    mode + "/" + lang + " close releases deferred settings reference");
                panel.Close();
                config.PanelChangeSystemDefault = true; config.PanelOutputVolumesRetainedOnly = false;
            }
        }
        config.PanelChangeSystemDefault = false; config.PanelOutputVolumesRetainedOnly = true;
        ConfigService.Save(config);
        var saved = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigService.ConfigPath))!;
        Check(!saved.PanelChangeSystemDefault && saved.PanelOutputVolumesRetainedOnly, "both independent explicit settings survive real config serialization");
    }
}

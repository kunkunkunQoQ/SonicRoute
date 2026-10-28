using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void CheckAppRowNames(MethodInfo theme, List<Window> windows, string directory)
    {
        Directory.CreateDirectory(directory);
        var config = ConfigService.Load();
        var savedCurrent = CurrentAppService.Current;
        Check(!config.QuickPanelShowOutputName, "existing config defaults to application names");
        Check(!config.QuickPanelUseLegacyAppRows, "existing config defaults to the new app row layout");
        config.ExperimentalMode = config.ExperimentalMic = false;
        config.HiddenPanelApps.Clear();
        var apps = new List<AudioAppInfo> {
            new() { ProcessId = int.MaxValue - 81, ProcessName = "RowNameMissingA", DisplayName = "First application" },
            new() { ProcessId = int.MaxValue - 82, ProcessName = "RowNameMissingB", DisplayName = "Second application with a very long name for clipping checks" }
        };
        config.AppNames[apps[0].ProcessName!] = "自定义应用 / Custom application";
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var saved = new Dictionary<FieldInfo, object?>();
        void Seed(string name, object value)
        {
            var field = typeof(SessionVolumeService).GetField(name, statics)!;
            if (!saved.ContainsKey(field)) saved[field] = field.GetValue(null);
            field.SetValue(null, value);
        }
        var playback = new OverviewSession(); var capture = new OverviewSession();
        void Fresh()
        {
            Seed("_renderVolumes", apps.ToDictionary(a => (int)a.ProcessId, _ => new List<ISimpleAudioVolume> { playback }));
            Seed("_captureVolumes", apps.ToDictionary(a => (int)a.ProcessId, _ => new List<ISimpleAudioVolume> { capture }));
            Seed("_lastEnum", DateTime.UtcNow);
            Seed("_snapshotGeneration", typeof(SessionVolumeService).GetField("_cacheGeneration", statics)!.GetValue(null)!);
        }
        try
        {
            foreach (string mode in new[] { "light", "dark" })
            {
                L10n.Instance.SetLanguage("zh-CN");
                theme.Invoke(null, new object[] { mode, "#00D5DD" });
                config.QuickPanelShowOutputName = true;
                config.QuickPanelUseLegacyAppRows = false;
                var window = new MainWindow(); windows.Add(window);
                Call(window, "EnsureThemePage"); Call(window, "LoadTheme");
                var toggle = Field<CheckBox>(window, "PanelOutputNameCheck");
                var peak = Field<CheckBox>(window, "AppPeakMeterCheck");
                var legacy = Field<CheckBox>(window, "PanelLegacyRowsCheck");
                var legacyRow = (Grid)legacy.Parent;
                var settingRow = (Grid)toggle.Parent; var peakRow = (Grid)peak.Parent;
                var parent = (StackPanel)settingRow.Parent;
                Check(toggle.IsChecked == true && ReferenceEquals(parent, peakRow.Parent)
                    && ReferenceEquals(parent, legacyRow.Parent)
                    && parent.Children.IndexOf(legacyRow) == parent.Children.IndexOf(peakRow) + 1
                    && parent.Children.IndexOf(settingRow) == parent.Children.IndexOf(legacyRow) + 1,
                    mode + " theme orders peak meter, legacy layout and device-name rows as peers");
                config.QuickPanelUseLegacyAppRows = true; Call(window, "LoadTheme");
                Check(legacy.IsChecked == true && settingRow.Visibility == Visibility.Collapsed && toggle.IsChecked == true
                    && config.QuickPanelShowOutputName, mode + " legacy layout hides the device-name switch while preserving its saved value");
                config.QuickPanelUseLegacyAppRows = false; Call(window, "LoadTheme");
                Check(legacy.IsChecked == false && settingRow.Visibility == Visibility.Visible && toggle.IsChecked == true,
                    mode + " new layout restores the device-name switch and its saved choice");
                Call(window, "EnsureExperimentalPage"); Call(window, "LoadExperimentalSettings");
                Check(window.FindName("ExpPanelOutputNameCheck") == null && toggle.IsChecked == true && config.QuickPanelShowOutputName,
                    mode + " experimental page no longer owns or resets the setting");
                foreach (string locale in new[] { "zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "ru-RU" })
                {
                    L10n.Instance.SetLanguage(locale); Layout(settingRow, 420, 130); DrainUi();
                    var label = OutputVisuals(settingRow).OfType<TextBlock>().First(t => t.Text == L10n.T("Theme.PanelOutputName"));
                    Check(!label.Text.StartsWith("Theme.") && label.ActualWidth > 0
                        && toggle.TranslatePoint(new Point(), settingRow).X >= label.TranslatePoint(new Point(label.ActualWidth, 0), settingRow).X,
                        mode + " " + locale + " theme label and switch fit without overlap");
                    Layout(legacyRow, 420, 130); DrainUi();
                    var legacyLabel = OutputVisuals(legacyRow).OfType<TextBlock>().First(t => t.Text == L10n.T("Theme.PanelLegacyRows"));
                    Check(!legacyLabel.Text.StartsWith("Theme.") && legacyLabel.ActualWidth > 0
                        && legacy.TranslatePoint(new Point(), legacyRow).X >= legacyLabel.TranslatePoint(new Point(legacyLabel.ActualWidth, 0), legacyRow).X,
                        mode + " " + locale + " legacy-layout label and switch fit without overlap");
                }
                L10n.Instance.SetLanguage("zh-CN");
                SaveOutputPreview(Path.Combine(directory, mode + "-setting.png"), 520, peakRow, legacyRow, settingRow);
                window.Close();
                Check(Field<CheckBox?>(window, "PanelOutputNameCheck") == null && Field<CheckBox?>(window, "PanelLegacyRowsCheck") == null
                    && Field<Grid?>(window, "PanelOutputNameRow") == null, mode + " close releases the deferred controls");

                var panel = new QuickPanelModernWindow(); windows.Add(panel); panel.SetMeterEnabled(false);
                var devices = new List<AudioDeviceInfo> {
                    new() { Id = AudioService.SystemDefaultDeviceId, DisplayName = L10n.T("Dev.SystemDefaultOut"), Flow = EDataFlow.eRender },
                    new() { Id = "fixture-chat", DisplayName = "聊天 / SteelSeries Sonar - Chat", Flow = EDataFlow.eRender },
                    new() { Id = "fixture-default", DisplayName = "耳机 / A very long custom playback device name", Flow = EDataFlow.eRender, IsDefault = true }
                };
                panel.GetType().GetField("_outputDisplay", Instance)!.SetValue(panel, devices);
                void Build(bool output)
                {
                    config.QuickPanelShowOutputName = output; Fresh();
                    Finish((Task)panel.GetType().GetMethod("BuildAppRowsAsync", Instance)!.Invoke(panel, new object[] { apps, config })!);
                }
                var rows = Field<IDictionary>(panel, "_rows");
                object First() => rows[(int)apps[0].ProcessId]!;
                void Name(string id) => panel.GetType().GetMethod("UpdateRowName", Instance)!.Invoke(panel, new[] { First(), id });
                void Render(string state)
                {
                    var surface = (FrameworkElement)panel.Content; Layout(surface, 372, 450); DrainUi();
                    var item = First(); var text = Field<TextBlock?>(item, "Name");
                    var slider = Field<Slider>(item, "Slider"); var expand = Field<ToggleButton>(item, "ExpandButton");
                    var header = Field<Border>(item, "HeaderBorder");
                    var otherHeader = Field<Border>(rows[(int)apps[1].ProcessId]!, "HeaderBorder");
                    Rect Bounds(FrameworkElement e) => e.TransformToAncestor(header).TransformBounds(new Rect(e.RenderSize));
                    var sliderBounds = Bounds(slider);
                    if (Field<bool>(item, "LegacyLayout"))
                        Check(text == null && slider.Parent is DockPanel && header.Height == 36
                            && expand.Width == 32 && expand.Height == 16 && Bounds(expand).Left >= sliderBounds.Right,
                            mode + " " + state + " classic single row restores the original icon/slider/arrow geometry without labels");
                    else
                    {
                        var textBounds = Bounds(text!);
                        Check(textBounds.Bottom <= sliderBounds.Top + .1 && text!.ActualWidth > 80
                            && Math.Abs(textBounds.Left - sliderBounds.Left - 14) < .1
                            && expand.ActualWidth == 32 && Bounds(expand).Left >= sliderBounds.Right
                            && text.TextTrimming == TextTrimming.CharacterEllipsis,
                            mode + " " + state + " name sits above the slider with fixed-width arrow and ellipsis");
                    }
                    Check(ReferenceEquals(header.Background, panel.FindResource("Theme.Overlay"))
                        && otherHeader.ReadLocalValue(Border.BackgroundProperty) == DependencyProperty.UnsetValue,
                        mode + " " + state + " current application retains the original theme-colored background");
                    var track = (Track)slider.Template.FindName("PART_Track", slider); track.Thumb.ApplyTemplate();
                    var knob = (Border)track.Thumb.Template.FindName("ThumbBg", track.Thumb);
                    Check(knob.Opacity == 1 && knob.Visibility == Visibility.Visible && knob.Width == 14 && knob.Height == 14,
                        mode + " " + state + " original volume thumb remains visible at rest");
                    RenderAppCards(surface, Path.Combine(directory, mode + "-" + state + ".png"));
                }
                CurrentAppService.Current = apps[0]; Build(false);
                Check(Field<TextBlock>(First(), "Name").Text == config.AppNames[apps[0].ProcessName!] && !Field<bool>(First(), "ShowOutputName"), mode + " off displays the custom application name");
                Name("fixture-chat");
                Check(Field<TextBlock>(First(), "Name").Text == config.AppNames[apps[0].ProcessName!], mode + " route readback cannot replace application names while off");
                var original = First(); Build(false);
                Check(ReferenceEquals(original, First()), mode + " unchanged list reuses its name and slider controls");
                Render("application-names");
                Build(true);
                Check(!config.ExperimentalMode && Field<bool>(First(), "ShowOutputName")
                    && Field<TextBlock>(First(), "Name").Text == devices[0].DisplayName + " · " + devices[2].DisplayName,
                    mode + " on works without experimental mode and shows default route plus actual playback device");
                Name("fixture-chat");
                Check(Field<TextBlock>(First(), "Name").Text == devices[1].DisplayName, mode + " explicit route uses the custom device name");
                var defaultRow = rows[(int)apps[1].ProcessId]!;
                var changed = new List<AudioDeviceInfo> {
                    new() { Id = "fixture-chat", DisplayName = "聊天 / Renamed chat device", Flow = EDataFlow.eRender },
                    new() { Id = "fixture-default", DisplayName = devices[2].DisplayName, Flow = EDataFlow.eRender }
                };
                panel.GetType().GetMethod("ApplyOutputDeviceSnapshot", Instance)!.Invoke(panel, new object[] { changed, "fixture-chat", config });
                Check(Field<TextBlock>(First(), "Name").Text == changed[0].DisplayName
                    && Field<TextBlock>(defaultRow, "Name").Text == devices[0].DisplayName + " · " + changed[0].DisplayName,
                    mode + " existing topology notification refreshes named and default routes from cached IDs without route queries");
                Name("missing-output");
                Check(Field<TextBlock>(First(), "Name").Text == L10n.T("Ov.CurrentUnavailable"), mode + " missing endpoint keeps an unavailable label");
                Name("fixture-chat"); Render("device-names");
                CurrentAppService.Current = apps[1]; Call(panel, "UpdateRowHighlights");
                Check(ReferenceEquals(Field<Border>(defaultRow, "HeaderBorder").Background, panel.FindResource("Theme.Overlay"))
                    && Field<Border>(First(), "HeaderBorder").ReadLocalValue(Border.BackgroundProperty) == DependencyProperty.UnsetValue,
                    mode + " changing the current app transfers the original theme highlight");
                CurrentAppService.Current = apps[0];
                config.QuickPanelUseLegacyAppRows = true; Build(true);
                Check(config.QuickPanelShowOutputName && rows.Values.Cast<object>().All(r => Field<TextBlock?>(r, "Name") == null && !Field<bool>(r, "ShowOutputName")),
                    mode + " legacy layout suppresses name controls and route loading despite a saved enabled device-name setting");
                Render("legacy-layout");
                config.QuickPanelUseLegacyAppRows = false; Build(true);
                Check(Field<TextBlock?>(First(), "Name") != null && Field<bool>(First(), "ShowOutputName"),
                    mode + " disabling legacy layout restores the saved device-name display");
                Check(Field<IDictionary>(panel, "_pendingVolume").Count == 0 && playback.MuteWrites == 0
                    && capture.MuteReads + capture.MuteWrites == 0, mode + " display changes queue no audio writes or capture work");
                panel.Close();
            }
        }
        finally
        {
            foreach (var pair in saved) pair.Key.SetValue(null, pair.Value);
            CurrentAppService.Current = savedCurrent;
            L10n.Instance.SetLanguage("zh-CN");
        }
    }
}

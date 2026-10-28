using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private sealed class OverviewSession : ISimpleAudioVolume
    {
        internal bool Muted;
        internal int MuteReads, MuteWrites;
        public int GetMasterVolume(out float value) { value = .42f; return 0; }
        public int GetMute(out int value) { MuteReads++; value = Muted ? 1 : 0; return 0; }
        public int SetMasterVolume(float value, ref Guid context) => throw new InvalidOperationException("Unexpected volume write");
        public int SetMute(int value, ref Guid context) { MuteWrites++; Muted = value != 0; return 0; }
    }

    private static void CheckOverviewMute(MethodInfo theme, List<Window> windows, string directory)
    {
        Directory.CreateDirectory(directory);
        var config = ConfigService.Load(); config.ExperimentalMic = true;
        foreach (string language in new[] { "zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "ru-RU" })
        foreach (string mode in new[] { "light", "dark" })
        {
            L10n.Instance.SetLanguage(language);
            theme.Invoke(null, new object[] { mode, "#00D5DD" });
            var window = new MainWindow(); windows.Add(window);
            typeof(MainWindow).GetMethod("ApplyExpMicUi", Instance)!.Invoke(window, new object[] { true });
            var output = (ComboBox)window.FindName("OverviewOutputCombo");
            var input = (ComboBox)window.FindName("OverviewInputCombo");
            var mute = (CheckBox)window.FindName("OverviewMuteButton");
            var mic = (CheckBox)window.FindName("OverviewMicMuteButton");
            var root = (FrameworkElement)window.Content;
            Check(ReferenceEquals(mute.Style, window.FindResource("OverviewMuteSwitch"))
                && ReferenceEquals(mic.Style, mute.Style) && !mute.IsEnabled && !mic.IsEnabled,
                language + " " + mode + " overview reuses the shared switch and starts disabled before readback");
            bool geometry = true;
            var actionKey = typeof(MainWindow).GetMethod("AutoActionKey", BindingFlags.Static | BindingFlags.NonPublic)!;
            Check(new[] { AutoRuleAction.SetCurrentAppInputMute, AutoRuleAction.ToggleCurrentAppInputMute }.All(action =>
                actionKey.Invoke(null, new object[] { action }) is string key && key != "Auto.Action" && L10n.T(key) != key),
                language + " " + mode + " both new automation choices have localized labels");
            foreach (int width in new[] { 780, 920 })
            {
                Layout(root, width, 620); DrainUi();
                if (language == "zh-CN" && mode == "light" && width == 920)
                {
                    foreach (var control in new[] { mute, mic })
                    {
                        control.IsEnabled = true;
                        var surface = (Border)VisualTreeHelper.GetChild(control, 0);
                        var parts = (Grid)surface.Child;
                        var visual = (CheckBox)parts.Children[1];
                        bool Hits(Point point)
                        {
                            DependencyObject? hit = VisualTreeHelper.HitTest(root, control.TranslatePoint(point, root))?.VisualHit;
                            while (hit is UIElement element && !element.IsHitTestVisible) hit = VisualTreeHelper.GetParent(hit);
                            while (hit != null && hit is not CheckBox) hit = VisualTreeHelper.GetParent(hit);
                            return ReferenceEquals(hit, control);
                        }
                        Check(Hits(new Point(8, 18)) && Hits(new Point(24, 18))
                            && Hits(new Point(control.ActualWidth - 28, 18)),
                            control.Name + " card padding, label and knob all route clicks to one switch");
                        Check(ReferenceEquals(visual.Style, window.FindResource("AutomationSwitch"))
                            && !visual.IsHitTestVisible && !visual.Focusable,
                            control.Name + " reuses the shared switch as a non-interactive visual");
                        control.IsChecked = true;
                        Check(visual.IsChecked == true, control.Name + " visual follows its single interactive switch");
                    }
                }
                foreach (var pair in new[] { (Combo: output, Switch: mute), (Combo: input, Switch: mic) })
                {
                    var row = (WrapPanel)pair.Combo.Parent;
                    Rect Bounds(FrameworkElement element) => element.TransformToAncestor(row).TransformBounds(new Rect(element.RenderSize));
                    var comboRect = Bounds(pair.Combo); var switchRect = Bounds(pair.Switch);
                    geometry &= Math.Abs(comboRect.Left) < .01 && Math.Abs(comboRect.Width - 320) < .01
                        && switchRect.Right <= row.ActualWidth + .01
                        && (switchRect.Left >= comboRect.Right + 11.9 || switchRect.Top >= comboRect.Bottom + 7.9);
                }
                if ((language == "zh-CN" && width == 920) || (language == "de-DE" && width == 780))
                {
                    mute.IsEnabled = mic.IsEnabled = true;
                    mute.IsChecked = mic.IsChecked = width == 780;
                    root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(width, 620, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, $"{language}-{mode}-{width}.png")); encoder.Save(stream);
                }
            }
            Check(geometry, language + " " + mode + " switches fit beside selectors or wrap at the minimum window width");
            Check(mic.Parent is WrapPanel rowParent && rowParent.Parent is StackPanel
                && window.FindName("OverviewOutputCurrentText") == null && window.FindName("OverviewInputCurrentText") == null,
                language + " " + mode + " controls occupy device rows and removed current text stays absent");
            window.Close();
        }
        L10n.Instance.SetLanguage("zh-CN");
        CheckOverviewMuteReadback(windows);
    }

    private static void CheckOverviewMuteReadback(List<Window> windows)
    {
        var config = ConfigService.Load(); config.ExperimentalMic = true;
        var window = new MainWindow(); windows.Add(window);
        var mute = (CheckBox)window.FindName("OverviewMuteButton");
        var mic = (CheckBox)window.FindName("OverviewMicMuteButton");
        void Readback(int pid, bool muted) => typeof(MainWindow).GetMethod("ApplyAppInputMuteReadback", Instance)!.Invoke(window, new object[] { pid, muted });
        int clicks = 0; mute.Click += (_, _) => clicks++; mic.Click += (_, _) => clicks++;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var saved = new Dictionary<FieldInfo, object?>();
        void Seed(string name, object value)
        {
            var field = typeof(SessionVolumeService).GetField(name, statics)!;
            if (!saved.ContainsKey(field)) saved[field] = field.GetValue(null);
            field.SetValue(null, value);
        }
        var a = new AudioAppInfo { ProcessId = int.MaxValue - 71, ProcessName = "OverviewMissingA" };
        var b = new AudioAppInfo { ProcessId = int.MaxValue - 72, ProcessName = "OverviewMissingB" };
        var outputA = new OverviewSession { Muted = true }; var outputB = new OverviewSession();
        var inputA = new OverviewSession(); var inputB = new OverviewSession { Muted = true };
        void Fresh()
        {
            Seed("_renderVolumes", new Dictionary<int, List<ISimpleAudioVolume>> { [(int)a.ProcessId] = new() { outputA }, [(int)b.ProcessId] = new() { outputB } });
            Seed("_captureVolumes", new Dictionary<int, List<ISimpleAudioVolume>> { [(int)a.ProcessId] = new() { inputA }, [(int)b.ProcessId] = new() { inputB } });
            Seed("_lastEnum", DateTime.UtcNow);
            Seed("_snapshotGeneration", typeof(SessionVolumeService).GetField("_cacheGeneration", statics)!.GetValue(null)!);
        }
        void Select(AudioAppInfo? target)
        {
            typeof(MainWindow).GetField("_overviewApp", Instance)!.SetValue(window, target);
            Fresh();
            Finish((Task)typeof(MainWindow).GetMethod("RefreshOverviewDevicesVolumeAsync", Instance)!.Invoke(window, null)!);
        }
        try
        {
            Select(a);
            Check(mute.IsChecked == true && mic.IsChecked == false && mute.IsEnabled && mic.IsEnabled
                && inputA.MuteReads > 0 && inputB.MuteReads == 0, "overview reads current-app playback/capture state without reading another app's capture");
            Select(b);
            Check(mute.IsChecked == false && mic.IsChecked == true && inputB.MuteReads > 0,
                "changing the current app updates both independent mute switches");
            Readback((int)a.ProcessId, false);
            Check(mic.IsChecked == true, "another app's microphone readback cannot replace the current app's switch");
            Readback((int)b.ProcessId, false);
            Check(mic.IsChecked == false && clicks == 0, "current-app readback changes the switch without invoking its write handler");
            Select(new AudioAppInfo { ProcessId = int.MaxValue - 73, ProcessName = "OverviewNoRecording" });
            Check(!mic.IsEnabled, "an app without a recording session cannot enable the microphone switch");
            Select(b);
            config.ExperimentalMic = false;
            typeof(MainWindow).GetMethod("ApplyExpMicUi", Instance)!.Invoke(window, new object[] { false });
            int inputReads = inputA.MuteReads + inputB.MuteReads; Select(a);
            Check(((Border)window.FindName("OverviewInputCard")).Visibility == Visibility.Collapsed
                && inputA.MuteReads + inputB.MuteReads == inputReads && !mic.IsEnabled,
                "microphone option off hides the relocated switch and skips capture-state reads");
            Select(null);
            Check(!mute.IsEnabled && !mic.IsEnabled && mute.IsChecked == false && mic.IsChecked == false,
                "no current app clears and disables both switches");
            Check(clicks == 0, "all programmatic readbacks execute no mute click or audio write");
            Check(outputA.MuteWrites + outputB.MuteWrites + inputA.MuteWrites + inputB.MuteWrites == 0,
                "read-only state synchronization does not write any session");
            config.ExperimentalMic = true;
            typeof(MainWindow).GetMethod("ApplyExpMicUi", Instance)!.Invoke(window, new object[] { true });
            Select(a);
            async Task ClickMute(CheckBox control, bool input)
            {
                typeof(System.Windows.Controls.Primitives.ToggleButton).GetMethod("OnClick", Instance)!.Invoke(control, null);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (Field<bool>(window, input ? "_overviewMicMuteBusy" : "_overviewMuteBusy"))
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("Overview mute click did not complete");
                    await Task.Delay(1);
                }
            }
            Finish(ClickMute(mute, false));
            Check(!outputA.Muted && outputA.MuteWrites == 1 && outputB.MuteWrites == 0 && inputA.MuteWrites == 0
                && clicks == 1 && mute.IsEnabled,
                "switch Click explicitly unmutes only the selected app's playback session");
            Finish(ClickMute(mute, false));
            Check(outputA.Muted && outputA.MuteWrites == 2 && mute.IsChecked == true && clicks == 2 && mute.IsEnabled,
                "switch Click mutes playback and applies the actual readback");
            var inputCombo = (ComboBox)window.FindName("OverviewInputCombo");
            var inputItems = inputCombo.ItemsSource;
            int refreshRequest = Field<int>(window, "_overviewStateRequest");
            int outputReads = outputA.MuteReads + outputB.MuteReads;
            Finish(ClickMute(mic, true));
            Check(inputA.MuteWrites == 1 && inputA.Muted && inputB.MuteWrites == 0 && outputB.MuteWrites == 0,
                "microphone switch writes only the current app's fake capture session");
            Check(clicks == 3 && ReferenceEquals(inputItems, inputCombo.ItemsSource)
                && Field<int>(window, "_overviewStateRequest") == refreshRequest
                && outputA.MuteReads + outputB.MuteReads == outputReads,
                "microphone Click completes without rereading playback or rebinding device UI");
            // Checked capture writes force native read-only discovery after the fake session write.
            // The deliberately missing PID cannot be confirmed there, so UI must report failure.
            Check(((TextBlock)window.FindName("OverviewStatusText")).Text == L10n.T("Ov.AppMicUnavailable"),
                "a capture write absent from fresh discovery is never reported as confirmed success");
            Check(!mic.IsEnabled && mic.IsChecked == false,
                "missing capture after failure leaves the switch disabled with its actual readback");
            Select(null);
            window.Close(); Readback((int)b.ProcessId, true);
            Check(mic.IsChecked == false, "closed overview ignores late microphone readback");
        }
        finally
        {
            window.Close();
            foreach (var pair in saved) pair.Key.SetValue(null, pair.Value);
        }
    }
}

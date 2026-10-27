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
            Check(ReferenceEquals(mute.Style, window.FindResource("AutomationSwitch"))
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
                foreach (var pair in new[] { (Combo: output, Switch: mute), (Combo: input, Switch: mic) })
                {
                    var row = (WrapPanel)pair.Combo.Parent;
                    var surface = (Border)((Grid)pair.Switch.Parent).Parent;
                    Rect Bounds(FrameworkElement element) => element.TransformToAncestor(row).TransformBounds(new Rect(element.RenderSize));
                    var comboRect = Bounds(pair.Combo); var switchRect = Bounds(surface);
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
            Check(((FrameworkElement)mic.Parent).Parent is Border card && ((FrameworkElement)card.Parent).Parent is StackPanel
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
            Task Change(bool input) => (Task)typeof(MainWindow).GetMethod("ChangeOverviewMuteAsync", Instance)!.Invoke(window, new object[] { input })!;
            mute.IsChecked = false; Finish(Change(false));
            Check(!outputA.Muted && outputA.MuteWrites == 1 && outputB.MuteWrites == 0 && inputA.MuteWrites == 0,
                "off explicitly unmutes only the selected app's playback session");
            mute.IsChecked = true; Finish(Change(false));
            Check(outputA.Muted && outputA.MuteWrites == 2 && mute.IsChecked == true,
                "on explicitly mutes playback and applies the actual readback");
            mic.IsChecked = true; Finish(Change(true));
            Check(inputA.MuteWrites == 1 && inputA.Muted && inputB.MuteWrites == 0 && outputB.MuteWrites == 0,
                "microphone switch writes only the current app's fake capture session");
            // Checked capture writes force native read-only discovery after the fake session write.
            // The deliberately missing PID cannot be confirmed there, so UI must report failure.
            Check(((TextBlock)window.FindName("OverviewStatusText")).Text == L10n.T("Ov.AppMicUnavailable"),
                "a capture write absent from fresh discovery is never reported as confirmed success");
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

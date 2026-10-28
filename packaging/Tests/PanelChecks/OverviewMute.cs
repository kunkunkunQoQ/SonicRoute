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
        internal bool Muted, IgnoreWrite;
        internal int MuteReads, MuteWrites;
        public int GetMasterVolume(out float value) { value = .42f; return 0; }
        public int GetMute(out int value) { MuteReads++; value = Muted ? 1 : 0; return 0; }
        public int SetMasterVolume(float value, ref Guid context) => throw new InvalidOperationException("Unexpected volume write");
        public int SetMute(int value, ref Guid context) { MuteWrites++; if (!IgnoreWrite) Muted = value != 0; return 0; }
    }

    private static void CheckOverviewMute(MethodInfo theme, List<Window> windows, string directory)
    {
        Directory.CreateDirectory(directory);
        ConfigService.Load().ExperimentalMic = true;
        foreach (string language in new[] { "zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "ru-RU" })
        foreach (string mode in new[] { "light", "dark" })
        {
            L10n.Instance.SetLanguage(language);
            theme.Invoke(null, new object[] { mode, "#00D5DD" });
            var window = new MainWindow(); windows.Add(window);
            typeof(MainWindow).GetMethod("ApplyExpMicUi", Instance)!.Invoke(window, new object[] { true });
            var mute = (Button)window.FindName("OverviewMuteButton");
            var slider = (Slider)window.FindName("OverviewVolumeSlider");
            var root = (FrameworkElement)window.Content;
            Check(ReferenceEquals(mute.Style, window.FindResource("QuickPanelToolButton"))
                && Equals(mute.Content, L10n.T("Qp.Mute")) && !mute.IsEnabled,
                language + " " + mode + " overview reuses the quick-panel mute button style and localized content");
            Check(window.FindName("OverviewMicMuteButton") == null && window.FindName("OverviewMinus") == null
                && window.FindName("OverviewPlus") == null && !window.Resources.Contains("OverviewMuteSwitch"),
                language + " " + mode + " removed switches, plus/minus and their private style stay absent");
            bool geometry = true;
            foreach (int width in new[] { 780, 920 })
            {
                Layout(root, width, 620); DrainUi();
                var row = (DockPanel)slider.Parent;
                Rect Bounds(FrameworkElement element) => element.TransformToAncestor(row).TransformBounds(new Rect(element.RenderSize));
                var muteRect = Bounds(mute); var sliderRect = Bounds(slider);
                geometry &= ReferenceEquals(mute.Parent, row) && DockPanel.GetDock(mute) == Dock.Left
                    && Math.Abs(muteRect.Left) < .01 && sliderRect.Left >= muteRect.Right + 9.9
                    && slider.ActualWidth > 100 && sliderRect.Right <= row.ActualWidth + .01;
                foreach (string name in new[] { "OverviewOutputCombo", "OverviewInputCombo" })
                {
                    var combo = (ComboBox)window.FindName(name);
                    geometry &= combo.Parent is WrapPanel devices && devices.Children.Count == 1 && combo.ActualWidth == 320;
                }
                if ((language == "zh-CN" && width == 920) || (language == "de-DE" && width == 780))
                {
                    mute.IsEnabled = true;
                    typeof(MainWindow).GetMethod("ApplyOverviewMuteVisual", Instance)!.Invoke(window, new object[] { width == 780 });
                    root.UpdateLayout();
                    if (language == "zh-CN" && mode == "light")
                    {
                        DependencyObject? hit = VisualTreeHelper.HitTest(root,
                            mute.TranslatePoint(new Point(mute.ActualWidth / 2, mute.ActualHeight / 2), root))?.VisualHit;
                        while (hit != null && hit is not Button) hit = VisualTreeHelper.GetParent(hit);
                        Check(ReferenceEquals(hit, mute), "relocated mute surface routes hits to its actual button");
                    }
                    var bitmap = new RenderTargetBitmap(width, 620, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, $"{language}-{mode}-{width}.png")); encoder.Save(stream);
                }
            }
            Check(geometry, language + " " + mode + " mute sits left of the volume slider while device selectors retain their layout");
            window.Close();
        }
        L10n.Instance.SetLanguage("zh-CN");
        CheckOverviewMuteReadback(windows);
    }

    private static void CheckOverviewMuteReadback(List<Window> windows)
    {
        var config = ConfigService.Load(); config.ExperimentalMic = true;
        var window = new MainWindow(); windows.Add(window);
        var mute = (Button)window.FindName("OverviewMuteButton");
        bool Muted() => ReferenceEquals(mute.Foreground, window.FindResource("Theme.Accent"));
        void Readback(int pid, bool muted) => typeof(MainWindow).GetMethod("ApplyAppMuteReadback", Instance)!.Invoke(window, new object[] { pid, muted });
        int clicks = 0; mute.Click += (_, _) => clicks++;
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
            typeof(MainWindow).GetField("_overviewApp", Instance)!.SetValue(window, target); Fresh();
            Finish((Task)typeof(MainWindow).GetMethod("RefreshOverviewDevicesVolumeAsync", Instance)!.Invoke(window, null)!);
        }
        async Task Click()
        {
            typeof(Button).GetMethod("OnClick", Instance)!.Invoke(mute, null);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Field<bool>(window, "_overviewMuteBusy"))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Overview mute click did not complete");
                await Task.Delay(1);
            }
        }
        try
        {
            Select(a);
            Check(Muted() && mute.IsEnabled && inputA.MuteReads + inputB.MuteReads == 0,
                "overview reads playback state without querying either app's capture mute");
            Select(b);
            Check(!Muted() && mute.IsEnabled, "changing app updates the relocated mute button");
            Readback((int)a.ProcessId, true);
            Check(!Muted(), "another app's readback cannot overwrite the mute button");
            Readback((int)b.ProcessId, true);
            Check(Muted() && clicks == 0, "shortcut readback updates the button without Click or audio writes");
            config.ExperimentalMic = false;
            typeof(MainWindow).GetMethod("ApplyExpMicUi", Instance)!.Invoke(window, new object[] { false }); Select(a);
            Check(((Border)window.FindName("OverviewInputCard")).Visibility == Visibility.Collapsed
                && inputA.MuteReads + inputB.MuteReads == 0 && mute.IsEnabled,
                "microphone option does not affect the playback button or introduce capture reads");
            Select(null);
            Check(!mute.IsEnabled && !Muted(), "no current app clears and disables the playback button");
            Check(clicks == 0 && outputA.MuteWrites + outputB.MuteWrites + inputA.MuteWrites + inputB.MuteWrites == 0,
                "programmatic synchronization does not write audio");
            Select(a); Finish(Click());
            Check(!outputA.Muted && outputA.MuteWrites == 1 && outputB.MuteWrites == 0 && !Muted() && mute.IsEnabled,
                "button Click unmutes only the selected playback session and restores availability");
            Finish(Click());
            Check(outputA.Muted && outputA.MuteWrites == 2 && Muted() && clicks == 2,
                "button Click mutes once and uses actual readback for the accent visual");
            outputA.IgnoreWrite = true; Finish(Click());
            Check(outputA.Muted && Muted() && mute.IsEnabled
                && ((TextBlock)window.FindName("OverviewStatusText")).Text == L10n.T("Tray.AudioUnavailable"),
                "ignored mute write preserves actual state and reports failure");
            Check(inputA.MuteReads + inputB.MuteReads + inputA.MuteWrites + inputB.MuteWrites == 0,
                "all overview mute interactions leave capture sessions untouched");
            Select(null); window.Close(); Readback((int)a.ProcessId, true);
            Check(!Muted(), "closed overview ignores late playback readback");
        }
        finally
        {
            window.Close(); foreach (var pair in saved) pair.Key.SetValue(null, pair.Value);
        }
    }
}

using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct OutputNativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder value, uint length, out uint needed);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out OutputNativeRect rect);

    private static void AttachOutputTestDesktop()
    {
        var name = new System.Text.StringBuilder(256);
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, 512, out _)
            || !name.ToString().StartsWith("SonicRoute-OutputChecks-"))
            throw new InvalidOperationException("Launch this optional check on its private desktop using the companion launcher.");
    }

    private static void OutputWait(Func<bool> condition, string message)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 12000) Finish(Task.Delay(10));
        Check(condition(), message);
    }

    private static void CheckLiveOutputPopup(MethodInfo theme, List<Window> windows, string directory, bool followup = false, bool optimization = false)
    {
        Directory.CreateDirectory(directory);
        theme.Invoke(null, new object[] { "light", "blue" });
        L10n.Instance.SetLanguage("zh-CN");
        var config = ConfigService.Load();
        config.ShowAppPeakMeter = false;
        config.QuickPanelHeight = 450;
        config.QuickPanelShowAllOutputVolumes = true;
        config.QuickPanelPosMode = "custom";
        if (followup) CheckLoadedOutputSettings(windows);
        var work = SystemParameters.WorkArea;
        foreach (string corner in followup ? new[] { "top-left" } : new[] { "top-left", "top-right", "bottom-left", "bottom-right" })
        {
            config.QuickPanelCustomX = (int)(corner.EndsWith("left") ? work.Left + 12 : work.Right - 372 - 12);
            config.QuickPanelCustomY = (int)(corner.StartsWith("top") ? work.Top + 10 : work.Bottom - 450 - 10);
            var panel = new QuickPanelModernWindow(); windows.Add(panel);
            panel.ShowQuickPanel();
            var popup = (Popup)panel.FindName("OutputVolumesPopup");
            OutputWait(() => popup.IsOpen && Field<object?>(panel, "_outputPopupSurface") != null
                && !Field<bool>(panel, "_outputRefreshRunning"), corner + " actual popup loads with read-only audio discovery");
            panel.UpdateLayout();
            Finish(Task.Delay(60));
            var surface = Field<Border>(panel, "_outputPopupSurface");
            surface.UpdateLayout();
            var popupSource = (HwndSource)PresentationSource.FromVisual(surface)!;
            var ownerHandle = new WindowInteropHelper(panel).Handle;
            Check(GetWindowRect(ownerHandle, out var owner) && GetWindowRect(popupSource.Handle, out _), corner + " actual owner/popup HWNDs exist");
            GetWindowRect(popupSource.Handle, out var extra);
            Console.WriteLine($"PLACEMENT {corner} owner={owner.Left},{owner.Top},{owner.Right},{owner.Bottom} extra={extra.Left},{extra.Top},{extra.Right},{extra.Bottom} target={(popup.PlacementTarget as FrameworkElement)?.ActualWidth},{(popup.PlacementTarget as FrameworkElement)?.ActualHeight} planned={panel.GetType().GetMethod("CalculateOutputPopupRect", Instance)!.Invoke(panel, null)} surface={surface.ActualWidth},{surface.ActualHeight}");
            Check(panel.Left == config.QuickPanelCustomX && panel.Top == config.QuickPanelCustomY && panel.Height == 450,
                corner + " expansion preserves saved owner position and height");
            Check(corner == "top-left" ? extra.Left >= owner.Right : corner == "top-right"
                ? extra.Right <= owner.Left : extra.Bottom <= owner.Top,
                corner + " actual popup chooses expected neighboring side");
            var rows = Field<System.Collections.IDictionary>(panel, "_outputVolumeRows");
            if (rows.Count > 0)
            {
                var row = rows.Values.Cast<object>().First();
                Field<Slider>(row, "Slider").Focus();
                Finish(Task.Delay(30));
                Check(panel.IsVisible && popup.IsOpen, corner + " focusing extra controls keeps main panel open");
            }
            if (followup)
            {
                CheckLiveOutputSizingAndFilter(panel, work);
                CheckOutputRowsRecreatedDuringRead(panel);
                CheckOutputLoadCancellation(panel);
            }
            if (optimization && corner == "top-left")
            {
                CheckLiveOutputSizingAndFilter(panel, work);
                CheckOutputRowsRecreatedDuringRead(panel);
                CheckOutputLoadCancellation(panel);
                CheckLiveOutputOptimization(panel);
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(directory, corner + ".png"))) encoder.Save(stream);
            var monitor = Field<OutputVolumeMonitor>(panel, "_outputVolumeMonitor");
            var toggle = (ToggleButton)panel.FindName("OutputVolumesToggle");
            toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Finish(monitor.Completion);
            OutputWait(() => !Field<bool>(panel, "_outputRefreshRunning"), corner + " in-flight read settles after collapse");
            Check(!popup.IsOpen && popup.Child == null && Field<object?>(panel, "_outputVolumeMonitor") == null
                && Field<object?>(panel, "_outputRecoveryTimer") == null, corner + " collapse releases native popup contents, monitor and retry timer");
            Check(GetWindowRect(ownerHandle, out var after) && after.Left == owner.Left && after.Top == owner.Top
                && after.Right == owner.Right && after.Bottom == owner.Bottom, corner + " collapse leaves owner native rectangle unchanged");
            toggle.IsChecked = true;
            panel.GetType().GetField("_adjustMode", Instance)!.SetValue(panel, true);
            panel.GetType().GetMethod("SetAdjustMode", Instance)!.Invoke(panel, new object[] { false });
            Check(!popup.IsOpen && Field<object?>(panel, "_outputVolumeMonitor") == null,
                corner + " leaving position adjustment does not eagerly reload extra controls before closing");
            toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            OutputWait(() => popup.IsOpen && !Field<bool>(panel, "_outputRefreshRunning"), corner + " explicit reopen reconstructs extra panel");
            var replacement = Field<OutputVolumeMonitor>(panel, "_outputVolumeMonitor");
            Check(!ReferenceEquals(monitor, replacement), corner + " reopened panel uses fresh subscriptions");
            if (corner == "bottom-right")
            {
                var other = new Window { Width = 80, Height = 80, Left = work.Left + 500, Top = work.Top + 20 };
                windows.Add(other); other.Show(); other.Activate();
                OutputWait(() => !panel.IsVisible, "focus moving to another private-desktop window closes both panels");
                other.Close();
            }
            panel.Close(); Finish(replacement.Completion);
            Check(!popup.IsOpen && popup.Child == null && Field<object?>(panel, "_outputVolumeMonitor") == null, corner + " closing owner releases extra feature");
        }
    }

    private static void CheckLiveOutputOptimization(QuickPanelModernWindow panel)
    {
        DrainUi();
        var popup = (Popup)panel.FindName("OutputVolumesPopup");
        var load = Field<object>(panel, "_outputLoad");
        var lifetime = Field<object>(load, "Lifetime");
        double offset = popup.HorizontalOffset;
        for (int i = 0; i < 100; i++) Call(panel, "PositionOutputPopup");
        var pending = Field<System.Collections.IEnumerable>(lifetime, "_operations").Cast<System.Windows.Threading.DispatcherOperation>()
            .Count(op => op.Status == System.Windows.Threading.DispatcherOperationStatus.Pending);
        Check(Field<bool>(load, "PositionQueued") && pending == 1, "100 positioning requests share one queued placement operation");
        DrainUi();
        Check(!Field<bool>(load, "PositionQueued") && popup.HorizontalOffset == offset,
            "unchanged placement does not force a native offset change");
        var rect = (Rect)panel.GetType().GetMethod("GetOutputPopupRect", Instance)!.Invoke(panel, null)!;
        bool callbacksMatch = true;
        for (int i = 0; i < 100; i++)
        {
            var positions = popup.CustomPopupPlacementCallback(new Size(rect.Width, rect.Height), new Size(panel.ActualWidth, panel.ActualHeight), new System.Windows.Point());
            callbacksMatch &= positions[0].Point == new System.Windows.Point(rect.X, rect.Y);
        }
        Check(callbacksMatch, "cached placement callbacks preserve coordinates across repeated WPF queries");
        Call(panel, "InvalidateOutputPopupScreen"); DrainUi();
        Check(popup.HorizontalOffset != offset, "screen/DPI invalidation forces placement even with identical DIP bounds");
        var surface = Field<Border>(panel, "_outputPopupSurface");
        var source = (HwndSource)PresentationSource.FromVisual(surface)!;
        var owner = new WindowInteropHelper(panel).Handle;
        GetWindowRect(owner, out var ownerBefore); GetWindowRect(source.Handle, out var before);
        double left = panel.Left; panel.Left = left + 8; DrainUi();
        GetWindowRect(owner, out var ownerAfter); GetWindowRect(source.Handle, out var after);
        Check(after.Left - before.Left == ownerAfter.Left - ownerBefore.Left && after.Top == before.Top,
            "owner movement repositions cached popup in matching native screen units");
        panel.Left = left; DrainUi();

        var monitor = Field<OutputVolumeMonitor>(panel, "_outputVolumeMonitor");
        void Set(object target, string field, object value) => target.GetType().GetField(field, Instance)!.SetValue(target, value);
        int oldTopology = Field<int>(monitor, "_needsTopologyRecovery"), oldRecovery = Field<int>(monitor, "_needsRecovery");
        Set(panel, "_outputRefreshRunning", true); // Hold UI refresh so the recovery request can be inspected before consumption.
        try
        {
            Set(load, "TopologyDirty", 0); Set(monitor, "_needsRecovery", 1); Set(monitor, "_needsTopologyRecovery", 0);
            panel.GetType().GetMethod("OutputRecovery_Tick", Instance)!.Invoke(panel, new object?[] { null, EventArgs.Empty });
            Check(Field<int>(load, "TopologyDirty") == 0, "single-endpoint recovery avoids a full topology request");
            Set(monitor, "_needsTopologyRecovery", 1);
            panel.GetType().GetMethod("OutputRecovery_Tick", Instance)!.Invoke(panel, new object?[] { null, EventArgs.Empty });
            Check(Field<int>(load, "TopologyDirty") == 1, "missing device notifications retain full topology recovery");
        }
        finally
        {
            Set(panel, "_outputRefreshRunning", false);
            Set(monitor, "_needsRecovery", oldRecovery); Set(monitor, "_needsTopologyRecovery", oldTopology);
        }
        Finish((Task)panel.GetType().GetMethod("RefreshOutputVolumesAsync", Instance)!.Invoke(panel, null)!);
        DrainUi();
        Check(Field<object?>(panel, "_outputRecoveryTimer") == null || !Field<System.Windows.Threading.DispatcherTimer>(panel, "_outputRecoveryTimer").IsEnabled,
            "healthy endpoint recovery returns to event-driven operation");
    }

    private static void CheckOutputLoadCancellation(QuickPanelModernWindow panel)
    {
        var popup = (Popup)panel.FindName("OutputVolumesPopup");
        var toggle = (ToggleButton)panel.FindName("OutputVolumesToggle");
        var oldLoad = Field<object>(panel, "_outputLoad");
        var lifetime = Field<object>(oldLoad, "Lifetime");
        var token = (System.Threading.CancellationToken)lifetime.GetType().GetProperty("Token", Instance)!.GetValue(lifetime)!;
        var blocked = new TaskCompletionSource<int>();
        var read = lifetime.GetType().GetMethods(Instance).Single(m => m.Name == "ReadAsync"
            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsGenericType
            && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Task<>));
        var pendingRead = (Task<int>)read.MakeGenericMethod(typeof(int)).Invoke(lifetime, new object[] { blocked.Task })!;
        var pendingOwnerRead = (Task)lifetime.GetType().GetMethod("ReadAsync", Instance, null, new[] { typeof(Task) }, null)!
            .Invoke(lifetime, new object[] { blocked.Task })!;
        bool ran = false;
        var queued = (System.Windows.Threading.DispatcherOperation)lifetime.GetType().GetMethod("Post", Instance)!.Invoke(lifetime,
            new object[] { panel.Dispatcher, (Action)(() => ran = true), System.Windows.Threading.DispatcherPriority.Background })!;
        var oldMonitor = Field<OutputVolumeMonitor>(panel, "_outputVolumeMonitor");
        toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(token.IsCancellationRequested && queued.Status == System.Windows.Threading.DispatcherOperationStatus.Aborted
            && Field<object?>(panel, "_outputLoad") == null, "collapse cancels the extra panel's queued UI work and releases its loading scope");
        var ownerLifetime = Field<object>(panel, "_uiLifetime");
        Check(!((System.Threading.CancellationToken)ownerLifetime.GetType().GetProperty("Token", Instance)!.GetValue(ownerLifetime)!).IsCancellationRequested,
            "collapsing extra panel preserves main panel lifetime");
        Finish(pendingRead.ContinueWith(_ => { }));
        Finish(pendingOwnerRead.ContinueWith(_ => { }));
        Check(pendingRead.IsCanceled && pendingOwnerRead.IsCanceled && !blocked.Task.IsCompleted && !ran,
            "collapse stops waiting for an uninterruptible read without running queued extra UI work");
        blocked.SetResult(1);

        toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var load = Field<object>(panel, "_outputLoad");
        int expected = Field<List<AudioDeviceInfo>>(panel, "_outputDisplay").Count(d => !AudioService.IsSystemDefault(d.Id));
        Check(popup.IsOpen && Field<System.Collections.IDictionary>(panel, "_outputVolumeRows").Count == expected
            && Field<Border>(panel, "_outputPopupSurface").Height > 0,
            "reopen creates and sizes cached device rows synchronously before waiting for fresh audio discovery");
        Check(!ReferenceEquals(oldLoad, load), "reopen owns a fresh cancellation scope");
        var replacedMonitor = Field<OutputVolumeMonitor>(panel, "_outputVolumeMonitor");
        toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        OutputWait(() => popup.IsOpen && !Field<bool>(panel, "_outputRefreshRunning"),
            "rapid collapse/reopen completes the replacement load without waiting for retired UI work");
        Check(!ReferenceEquals(load, Field<object>(panel, "_outputLoad"))
            && Field<System.Collections.IDictionary>(panel, "_outputVolumeRows").Count == expected,
            "retired load cannot clear or replace current device rows");
        Finish(oldMonitor.Completion); Finish(replacedMonitor.Completion);
        Check(Field<System.Collections.IDictionary>(oldMonitor, "_volumes").Count == 0
            && Field<System.Collections.IDictionary>(replacedMonitor, "_volumes").Count == 0,
            "both retired loads release their endpoint subscriptions");
    }

    private static void CheckOutputRowsRecreatedDuringRead(QuickPanelModernWindow panel)
    {
        var config = ConfigService.Load();
        var rows = Field<System.Collections.IDictionary>(panel, "_outputVolumeRows");
        string id = rows.Keys.Cast<string>().First(key => Field<Slider>(rows[key]!, "Slider").IsEnabled);
        var monitor = Field<OutputVolumeMonitor>(panel, "_outputVolumeMonitor");
        lock (Field<object>(monitor, "_gate"))
        {
            // Hold only the read worker, then coalesce hide/show before it can
            // retire subscriptions. No audio setter or physical input is used.
            panel.GetType().GetMethod("RequestOutputVolumeReadback", Instance)!.Invoke(panel, new object?[] { null });
            config.PanelOutputVolumesRetainedOnly = true;
            config.HiddenOutputDevices = rows.Keys.Cast<string>().ToList();
            Call(panel, "RefreshOutputVolumeFilter");
            Check(rows.Count == 0, "in-flight filter edit removes old device rows immediately");
            config.PanelOutputVolumesRetainedOnly = false;
            config.HiddenOutputDevices.Clear();
            Call(panel, "RefreshOutputVolumeFilter");
            var target = Field<object>(rows[id]!, "Target");
            target.GetType().GetField("Version", Instance)!.SetValue(target, 1);
        }
        OutputWait(() => !Field<bool>(panel, "_outputRefreshRunning"), "coalesced filter reversal finishes refreshing recreated rows");
        Check(Field<Slider>(rows[id]!, "Slider").IsEnabled && Field<TextBlock>(rows[id]!, "Percent").Text != "—",
            "recreated clean endpoints load fresh state after an older readback is rejected");
    }

    private static void CheckLoadedOutputSettings(List<Window> windows)
    {
        var config = ConfigService.Load();
        var window = new MainWindow(); windows.Add(window);
        Call(window, "EnsureSettingsPage"); Call(window, "LoadSettings");
        window.Show();
        OutputWait(() => window.IsLoaded, "settings event checks run against a loaded product window");
        var normal = Field<CheckBox>(window, "SettingsPanelChangeSysDef");
        var retained = Field<CheckBox>(window, "SettingsPanelOutputVolumesRetainedOnly");
        Check(normal.IsChecked == true, "missing preference initializes actual system-default setting as on");
        normal.IsChecked = false;
        retained.IsChecked = true;
        Check(!config.PanelChangeSystemDefault && config.PanelOutputVolumesRetainedOnly && retained.IsEnabled,
            "loaded independent settings events save without changing audio devices");
        retained.ApplyTemplate();
        var decoration = Descendant<CheckBox>(retained);
        if (decoration != null) decoration.RaiseEvent(new RoutedEventArgs(CheckBox.UncheckedEvent));
        Check(config.PanelOutputVolumesRetainedOnly, "decorative switch bubbling cannot invert retained-only setting");
        var saved = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigService.ConfigPath))!;
        Check(!saved.PanelChangeSystemDefault && saved.PanelOutputVolumesRetainedOnly,
            "actual setting events persist independent values to isolated config");
        window.Close();
        Check(Field<object?>(window, "SettingsPanelOutputVolumesRetainedOnly") == null && ((App)Application.Current).QuickPanelInstance == null,
            "settings edits with no panel leave extra feature completely unloaded");
        config.PanelChangeSystemDefault = true; config.PanelOutputVolumesRetainedOnly = false;
    }

    private static void CheckLiveOutputSizingAndFilter(QuickPanelModernWindow panel, Rect work)
    {
        var config = ConfigService.Load();
        var rows = Field<System.Collections.IDictionary>(panel, "_outputVolumeRows");
        var surface = Field<Border>(panel, "_outputPopupSurface");
        var available = Field<List<AudioDeviceInfo>>(panel, "_outputDisplay")
            .Where(d => !AudioService.IsSystemDefault(d.Id)).Select(d => d.Id).ToArray();
        if (available.Length == 0) return;
        config.QuickPanelHeight = 350; panel.ApplyPanelHeightFromConfig();
        OutputWait(() => panel.ActualHeight == panel.Height && surface.ActualHeight <= panel.ActualHeight,
            "extra panel never exceeds a smaller 350-DIP main panel");
        config.QuickPanelHeight = 610; panel.ApplyPanelHeightFromConfig();
        double expected = Math.Min(work.Height * .75, Math.Min(panel.Height, 58 + rows.Count * 64));
        OutputWait(() => Math.Abs(surface.ActualHeight - expected) < 1,
            "larger main panel permits content beyond old fixed 450-DIP cap");
        double left = panel.Left, top = panel.Top;
        bool mainReady = ((Slider)panel.FindName("VolumeSlider")).IsEnabled;
        config.PanelOutputVolumesRetainedOnly = true;
        config.HiddenOutputDevices = available.ToList();
        Call(panel, "RefreshOutputVolumeFilter");
        OutputWait(() => rows.Count == 0 && !Field<bool>(panel, "_outputRefreshRunning") && surface.ActualHeight <= 90,
            "retaining no real output shows a compact empty panel");
        Check(Field<TextBlock>(panel, "_outputVolumeEmptyText").Visibility == Visibility.Visible
            && ((Slider)panel.FindName("VolumeSlider")).IsEnabled == mainReady,
            "empty retained list keeps original main volume control available");
        var monitor = Field<OutputVolumeMonitor>(panel, "_outputVolumeMonitor");
        Check(Field<System.Collections.IDictionary>(monitor, "_volumes").Count == 0,
            "filtered endpoints release their extra volume subscriptions");
        config.HiddenOutputDevices = available.Skip(1).ToList();
        Call(panel, "RefreshOutputVolumeFilter");
        OutputWait(() => rows.Count == 1 && !Field<bool>(panel, "_outputRefreshRunning") && Math.Abs(surface.ActualHeight - 122) < 1,
            "one retained device fits its actual row height without unused panel space");
        Check(rows.Contains(available[0]) && panel.Left == left && panel.Top == top && panel.Height == 610,
            "retained filtering changes only extra contents and preserves owner position/height");
        config.PanelOutputVolumesRetainedOnly = false;
        config.HiddenOutputDevices.Clear();
        config.QuickPanelHeight = 450; panel.ApplyPanelHeightFromConfig();
        Call(panel, "RefreshOutputVolumeFilter");
        OutputWait(() => rows.Count == available.Length && !Field<bool>(panel, "_outputRefreshRunning")
            && panel.ActualHeight == 450 && surface.ActualHeight <= 450,
            "disabling retained-only restores all outputs under the current main height cap");
    }
}

using System;
using System.Windows;
#if SONICROUTE_PHONE
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using Button = System.Windows.Controls.Button;
using Slider = System.Windows.Controls.Slider;
using Brush = System.Windows.Media.Brush;
#endif

namespace SonicRoute;

public partial class QuickPanelModernWindow
{
#if SONICROUTE_PHONE
    private ToggleButton PhoneAudioButton = null!;
    private Border AndroidDropHighlight = null!, AndroidComputerDropZone = null!, AndroidSharedDropZone = null!;
    private sealed class AndroidRow
    {
        internal AndroidAudioSource Source = null!;
        internal StackPanel Root = null!, Details = null!;
        internal Slider Volume = null!;
        internal Button Icon = null!;
        internal AppItem IconItem = null!;
        internal ToggleButton Expand = null!;
        internal AnimatedExpandHost Wrap = null!;
        internal RotateTransform Arrow = new(0);
        internal bool Expanded, Legacy, ShowOutputName, ApplyingState;
        internal TextBlock? Name;
        internal readonly List<(Button Button, string Id)> Devices = new();
    }
    private readonly Dictionary<string, AndroidRow> _androidRows = new(StringComparer.Ordinal);
    private int _androidRefreshQueued;
    private bool? _androidDropShared;
    private int AndroidRowOffset => _androidRows.Values.Count(r => AppListPanel.Items.Contains(r.Root));
    private void InitializeAndroidPanel()
    {
        InitializeAndroidDropOverlay();
        var phoneIcon = new System.Windows.Shapes.Path { Data = AutomationIcons.Phone,
            Width = 12, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 1.5 };
        phoneIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Theme.TextSecondary");
        PhoneAudioButton = new ToggleButton { Name = "PhoneAudioButton", Content = phoneIcon,
            Style = (Style)FindResource("PanelCompanionToggle"), Margin = new Thickness(8,0,0,0) };
        PhoneAudioButton.Click += PhoneAudioButton_Click;
        DockPanel.SetDock(PhoneAudioButton, Dock.Right);
        RegisterName(PhoneAudioButton.Name, PhoneAudioButton);
        PanelHeader.Children.Insert(0, PhoneAudioButton);
        AppListScroll.AllowDrop = true;
        AppListScroll.PreviewDragOver += Android_DragOver;
        AppListScroll.PreviewDrop += Android_Drop;
        AppListScroll.DragLeave += Android_DragLeave;
        ApplicationAudio.Changed += Android_StateChanged;
        AndroidAudioHost.DragStateChanged += Android_DragStateChanged;
        IsVisibleChanged += AndroidPanelVisibilityChanged;
        Android_DragStateChanged();
        UpdatePhoneAudioButton();
        if (AndroidAudioHost.IsCreated) RefreshAndroidRow();
    }
    private void InitializeAndroidDropOverlay()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
        AndroidDropHighlight = new Border { Name = "AndroidDropHighlight", CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2), Visibility = Visibility.Collapsed, IsHitTestVisible = false, Child = grid };
        AndroidDropHighlight.SetResourceReference(Border.BorderBrushProperty, "Theme.Accent");
        AndroidDropHighlight.SetResourceReference(BackgroundProperty, "Theme.SurfaceBg");
        Border Zone(string name, string title, string description, CornerRadius corners)
        {
            var labels = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
            var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextPrimary");
            var detail = new TextBlock { Text = description, FontSize = 11, Margin = new Thickness(0,6,0,0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
            labels.Children.Add(heading); labels.Children.Add(detail);
            var border = new Border { Name = name, CornerRadius = corners, Background = System.Windows.Media.Brushes.Transparent, Child = labels };
            System.Windows.Automation.AutomationProperties.SetName(border, title);
            RegisterName(name, border); return border;
        }
        AndroidComputerDropZone = Zone("AndroidComputerDropZone", "仅在电脑播放", "松开后，手机不再出声", new CornerRadius(6,6,0,0));
        AndroidSharedDropZone = Zone("AndroidSharedDropZone", "手机与电脑同时播放", "松开后，保留手机声音", new CornerRadius(0,0,6,6));
        Grid.SetRow(AndroidSharedDropZone, 1); grid.Children.Add(AndroidComputerDropZone); grid.Children.Add(AndroidSharedDropZone);
        var divider = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        divider.SetResourceReference(BackgroundProperty, "Theme.Accent"); grid.Children.Add(divider);
        var host = (Grid)AppListScroll.Parent;
        Grid.SetRow(AndroidDropHighlight, Grid.GetRow(AppListScroll));
        host.Children.Insert(host.Children.IndexOf(AppListScroll) + 1, AndroidDropHighlight);
        RegisterName(AndroidDropHighlight.Name, AndroidDropHighlight);
        // Preserve the PC viewport during a phone drag without creating phone UI in Lite.
        var style = new Style(typeof(ItemsControl), AppListPanel.Style);
        var trigger = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(Visibility)) { Source = AndroidDropHighlight }, Value = Visibility.Visible };
        trigger.Setters.Add(new Setter(VisibilityProperty, Visibility.Hidden)); style.Triggers.Add(trigger);
        AppListPanel.Style = style;
    }
    private void StopAndroidPanel()
    {
        ClearAndroidDropHighlight();
        ApplicationAudio.Changed -= Android_StateChanged;
        AndroidAudioHost.DragStateChanged -= Android_DragStateChanged;
        IsVisibleChanged -= AndroidPanelVisibilityChanged;
        foreach (var row in _androidRows.Values) row.Wrap.FinishImmediately();
        _androidRows.Clear();
    }
    internal void UpdatePhoneAudioButton()
    {
        bool enabled = ConfigService.Load().QuickPanelShowPhoneAudio;
        PhoneAudioButton.IsChecked = enabled;
        System.Windows.Automation.AutomationProperties.SetName(PhoneAudioButton, enabled ? "关闭手机音频" : "打开手机音频");
        if (enabled) PhoneAudioButton.SetResourceReference(BackgroundProperty, "Theme.Overlay");
        else PhoneAudioButton.SetResourceReference(BackgroundProperty, "Theme.SurfaceBg");
    }
    private void PhoneAudioButton_Click(object sender, RoutedEventArgs e) => ((App)System.Windows.Application.Current).TogglePhoneAudioWindow(this);
    private void Android_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        bool accepted = e.Data.GetDataPresent(AndroidAppDrag.Format, false)
            && e.Data.GetData(AndroidAppDrag.Format, false) is AndroidAppDrag;
        e.Effects = accepted ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        SetAndroidDropTarget(accepted ? IsSharedAndroidDrop(e.GetPosition(AppListScroll).Y, AppListScroll.ActualHeight) : null);
        e.Handled = true;
    }
    private void Android_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        // DragLeave bubbles when the hit child changes. Only leaving the viewport ends a drop target.
        var point = e.GetPosition(AppListScroll);
        if (AndroidAudioHost.IsDragging)
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            point = AppListScroll.PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
        }
        if (!new Rect(0, 0, AppListScroll.ActualWidth, AppListScroll.ActualHeight).Contains(point))
            ClearAndroidDropHighlight();
    }
    private void SetAndroidDropTarget(bool? shared)
    {
        bool visible = shared != null || AndroidAudioHost.IsDragging;
        if (_androidDropShared == shared && (AndroidDropHighlight.Visibility == Visibility.Visible) == visible) return;
        _androidDropShared = shared;
        if (!visible) { AndroidDropHighlight.Visibility = Visibility.Collapsed; return; }
        AndroidComputerDropZone.SetResourceReference(BackgroundProperty, shared == false ? "Theme.Overlay" : "Theme.SurfaceBg");
        AndroidSharedDropZone.SetResourceReference(BackgroundProperty, shared == true ? "Theme.Overlay" : "Theme.SurfaceBg");
        AndroidDropHighlight.Visibility = Visibility.Visible;
    }
    private void Android_DragStateChanged()
    {
        if (_isClosed) return;
        if (!Dispatcher.CheckAccess()) { _uiLifetime.Post(Dispatcher, Android_DragStateChanged); return; }
        SetAndroidDropTarget(null);
    }
    internal void ClearAndroidDropHighlight() => SetAndroidDropTarget(null);
    private static bool IsSharedAndroidDrop(double y, double height) => height > 0 && y >= height / 2;
    private async void Android_Drop(object sender, System.Windows.DragEventArgs e)
    {
        bool keepPhoneAudio = IsSharedAndroidDrop(e.GetPosition(AppListScroll).Y, AppListScroll.ActualHeight);
        ClearAndroidDropHighlight(); e.Handled = true;
        if (!e.Data.GetDataPresent(AndroidAppDrag.Format, false) || e.Data.GetData(AndroidAppDrag.Format, false) is not AndroidAppDrag payload)
        { e.Effects = System.Windows.DragDropEffects.None; return; }
        e.Effects = System.Windows.DragDropEffects.Copy;
        try
        {
            await AndroidAudioHost.AddAudioAsync(payload.Device, payload.App, keepPhoneAudio);
        }
        catch (Exception ex) { if (!_isClosed) ShowOsd("手机音频", ex.Message); }
    }
    private void Android_StateChanged()
    {
        if (_isClosed || Interlocked.Exchange(ref _androidRefreshQueued, 1) != 0) return;
        _uiLifetime.Post(Dispatcher, () =>
        {
            Interlocked.Exchange(ref _androidRefreshQueued, 0);
            if (!_isClosed && (!IsLoaded || IsVisible)) RefreshAndroidRow();
        });
    }
    private void AndroidPanelVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (IsVisible) Android_StateChanged(); }
    private void RefreshAndroidRow(bool fromBuildAppRows = false)
    {
        if (_isClosed) return;
        if (!fromBuildAppRows)
        {
            var cfg = ConfigService.Load();
            if (SystemDevCombo?.ItemsSource is IList<AudioDeviceInfo> sysOutputs)
            {
                _outputDisplay = PanelDevices.WithSystemDefault(
                    DisplayDevices(ApplicationAudio.MergeExternalOutputs(sysOutputs), cfg), EDataFlow.eRender, cfg);
            }
            foreach (var pcRow in _rows.Values)
            {
                int pid = (int)pcRow.App.ProcessId;
                string? phoneOut = ApplicationAudio.GetPhoneRoutedDeviceId(pid);
                if (phoneOut != null && pcRow.OutputId != phoneOut)
                    UpdateRowName(pcRow, phoneOut);
                else if (phoneOut == null && ApplicationAudio.IsPhoneOutputDevice(pcRow.OutputId))
                    UpdateRowName(pcRow, AudioService.SystemDefaultDeviceId);
                else if (pcRow.ShowOutputName)
                    UpdateRowName(pcRow, pcRow.OutputId);
                if (pcRow.Expanded) _ = BuildRowDevicesAsync(pcRow);
            }
        }
        if (!AndroidAudioHost.IsCreated && _androidRows.Count == 0) return;
        var config = ConfigService.Load();
        IReadOnlyList<AndroidAudioSource> sources = (AndroidAudioHost.IsCreated
            ? AndroidAudioHost.Sources.Sources : Array.Empty<AndroidAudioSource>())
            .Where(s => !config.HiddenPanelApps.Contains(ApplicationAudio.AndroidKey(s.App.Package), StringComparer.OrdinalIgnoreCase)).ToArray();
        bool legacy = config.QuickPanelUseLegacyAppRows;
        bool outputName = legacy ? config.QuickPanelCompactShowOutputName : config.QuickPanelShowOutputName;
        foreach (var row in _androidRows.Values.ToArray())
        {
            if (sources.Any(s => ReferenceEquals(s, row.Source)) && row.Legacy == legacy && row.ShowOutputName == outputName) continue;
            row.Wrap.FinishImmediately(); AppListPanel.Items.Remove(row.Root); _androidRows.Remove(row.Source.Id);
        }
        for (int i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (!_androidRows.TryGetValue(source.Id, out var row)) _androidRows[source.Id] = row = CreateAndroidRow(source, legacy, outputName);
            var state = source.Session.State;
            if ((int)row.Volume.Value != state.Volume)
            {
                row.ApplyingState = true;
                try { row.Volume.Value = state.Volume; }
                finally { row.ApplyingState = false; }
            }
            row.Volume.IsEnabled = state.State == CaptureState.Receiving;
            row.Volume.Foreground = state.Muted ? ThemeService.GetInvertedAccentBrush() : (Brush)FindResource("Theme.Accent");
            row.Icon.Opacity = state.Muted ? 0.45 : 1;
            row.IconItem.RefreshName();
            row.Root.ToolTip = state.Error ?? $"{row.IconItem.Label} · 来自 {AndroidAudioHost.Phone.GetDisplayName(source.Device)}";
            UpdateAndroidRoute(row);
            if (i >= AppListPanel.Items.Count || !ReferenceEquals(AppListPanel.Items[i], row.Root))
            {
                AppListPanel.Items.Remove(row.Root); AppListPanel.Items.Insert(i, row.Root);
            }
        }
    }
    private AndroidRow CreateAndroidRow(AndroidAudioSource source, bool legacy, bool showOutputName)
    {
        var row = new AndroidRow { Source = source, Legacy = legacy, ShowOutputName = showOutputName };
        var session = source.Session; var state = session.State;
        row.IconItem = AppItem.From(ApplicationAudio.Describe(source));
        AppItem.LoadIconsAsync(new List<AppItem> { row.IconItem });
        row.Root = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        var header = new Border { Height = legacy ? 36 : 52, Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(8) };
        var dock = new DockPanel();
        row.Icon = new Button { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("RowIconButton"), ToolTip = source.App.Name + " · 手机音频\n拖到面板外打开独立屏幕" };
        row.Icon.Content = new ContentControl { Content = row.IconItem, Width = 22, Height = 22,
            ContentTemplate = (DataTemplate)FindResource("AppIconTemplate") };
        row.Icon.Click += (_, _) => session.SetMute(!session.State.Muted);
        AndroidAppDragController.Attach(row.Icon, () => new AndroidAppDrag(source.Device, source.App));
        System.Windows.Automation.AutomationProperties.SetName(row.Icon, source.App.Name + " 手机音频静音");
        DockPanel.SetDock(row.Icon, Dock.Left); dock.Children.Add(row.Icon);
        row.Expand = new ToggleButton { Width = legacy && showOutputName ? 80 : 32, Height = legacy ? 24 : 32,
            VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("RowExpandButton"), ToolTip = L10n.T("Qp.ExpandDevices") };
        var glyph = new TextBlock { Text = "▾", RenderTransform = row.Arrow, RenderTransformOrigin = new System.Windows.Point(0.5, 0.5) };
        if (legacy && showOutputName)
        {
            var label = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            row.Name = NewRowName(); row.Name.MaxWidth = 58; label.Children.Add(row.Name); label.Children.Add(glyph); row.Expand.Content = label;
        }
        else row.Expand.Content = glyph;
        System.Windows.Automation.AutomationProperties.SetName(row.Expand, source.App.Name + " 输出与退出");
        DockPanel.SetDock(row.Expand, Dock.Right); dock.Children.Add(row.Expand);
        Slider volume = legacy ? new Slider() : new AppRowVolumeSlider(); row.Volume = volume;
        volume.Minimum = 0; volume.Maximum = 100; volume.Value = state.Volume; volume.Height = legacy ? 32 : 48;
        volume.VerticalAlignment = VerticalAlignment.Center; volume.Margin = new Thickness(0, 0, 6, 0);
        volume.Style = (Style)FindResource("RowSliderStyle"); volume.SetResourceReference(Slider.ForegroundProperty, "Theme.Accent");
        volume.ApplyTemplate();
        var track = volume.Template.FindName("PART_TrackBg", volume) as Border;
        var fill = volume.Template.FindName("PART_TrackFill", volume) as Border;
        if (track != null) track.SizeChanged += (_, _) => VolumeTrackLayout.Update(volume, track, fill);
        System.Windows.Automation.AutomationProperties.SetName(volume, source.App.Name + " 手机音量");
        volume.ValueChanged += (_, e) =>
        {
            VolumeTrackLayout.Update(volume, track, fill);
            if (!row.ApplyingState && (int)Math.Round(e.NewValue) != session.State.Volume) session.SetVolume((int)Math.Round(e.NewValue));
        };
        VolumePresets.Attach(volume);
        volume.MouseWheel += (_, e) =>
        { int step = Math.Clamp(ConfigService.Load().VolumeStep, 1, 20); volume.Value = Math.Clamp(volume.Value + (e.Delta > 0 ? step : -step), 0, 100); e.Handled = true; };
        if (!legacy)
        {
            var box = new Grid(); row.Name = NewRowName(); row.Name.Margin = new Thickness(14, 0, 20, 0);
            row.Name.IsHitTestVisible = false; row.Name.VerticalAlignment = VerticalAlignment.Top;
            volume.Padding = new Thickness(0, 16, 0, 0); box.Children.Add(volume); box.Children.Add(row.Name); dock.Children.Add(box);
        }
        else dock.Children.Add(volume);
        header.Child = dock; row.Root.Children.Add(header);
        row.Details = new StackPanel { Margin = new Thickness(26, 0, 0, 6), Visibility = Visibility.Collapsed };
        row.Wrap = new AnimatedExpandHost { Child = row.Details }; row.Root.Children.Add(row.Wrap);
        row.Expand.Checked += (_, _) => ExpandAndroidRow(row, true);
        row.Expand.Unchecked += (_, _) => ExpandAndroidRow(row, false);
        return row;
    }
    private void ExpandAndroidRow(AndroidRow row, bool expanded)
    {
        if (_isClosed || row.Expanded == expanded) return;
        StopRowScroll(); row.Expanded = expanded;
        if (!expanded) { UiMotion.RotateArrow(row.Arrow, false, true); row.Wrap.SetExpanded(false, true); return; }
        bool switching = CollapseNativeDeviceRows(null) | CollapseAndroidRows(row);
        BuildAndroidDevices(row);
        UiMotion.RotateArrow(row.Arrow, true, !switching);
        row.Wrap.SetExpanded(true, !switching, () => { if (!_isClosed && row.Expanded) row.Root.BringIntoView(); });
    }
    private bool CollapseAndroidRows(object? except)
    {
        bool changed = false;
        foreach (var row in _androidRows.Values)
        {
            if (ReferenceEquals(row, except) || (!row.Expanded && !row.Wrap.IsAnimating)) continue;
            changed = true; row.Expanded = false; row.Wrap.SetExpanded(false, false);
            UiMotion.RotateArrow(row.Arrow, false, false); row.Expand.IsChecked = false;
        }
        return changed;
    }
    private void BuildAndroidDevices(AndroidRow row)
    {
        row.Details.Children.Clear(); row.Devices.Clear();
        var cfg = ConfigService.Load();
        var outputs = _outputDisplay.Where(d => !cfg.HiddenOutputDevices.Contains(d.Id) && !ApplicationAudio.IsPhoneOutputDevice(d.Id));
        var wrap = AddRowDeviceGroup(row.Details, outputs, "Qp.Output", new Thickness(0, 0, 0, 8), (button, device) =>
        {
            row.Devices.Add((button, device.Id));
            button.Click += async (_, _) => await SetAndroidOutputAsync(row, device);
        });
        var exit = new Button { Content = "退出", ToolTip = "停止接收此手机应用" };
        exit.SetResourceReference(StyleProperty, "DevButton");
        System.Windows.Automation.AutomationProperties.SetName(exit, row.Source.App.Name + " 退出");
        exit.Click += async (_, _) =>
        {
            try { await AndroidAudioHost.Sources.RemoveAsync(row.Source.Id); }
            catch (Exception ex) { if (!_isClosed) ShowOsd("手机音频", ex.Message); }
        };
        wrap.Children.Add(exit); UpdateAndroidRoute(row);
    }
    private async Task SetAndroidOutputAsync(AndroidRow row, AudioDeviceInfo device)
    {
        foreach (var entry in row.Devices) entry.Button.IsEnabled = false;
        try { await row.Source.Session.SetOutputAsync(device.Id == AudioService.SystemDefaultDeviceId ? null : device.Id); }
        catch (Exception ex) { if (!_isClosed) ShowOsd("手机音频", ex.Message); }
        finally { if (!_isClosed) { UpdateAndroidRoute(row); foreach (var entry in row.Devices) entry.Button.IsEnabled = true; } }
    }
    private void UpdateAndroidRoute(AndroidRow row)
    {
        string id = row.Source.Session.State.OutputId ?? AudioService.SystemDefaultDeviceId;
        if (row.Name != null) row.Name.Text = row.ShowOutputName
            ? ShortName(_outputDisplay.FirstOrDefault(d => d.Id == id)?.DisplayName ?? "系统输出") : row.IconItem.Label;
        foreach (var entry in row.Devices) entry.Button.SetResourceReference(StyleProperty, entry.Id == id ? "DevButtonActive" : "DevButton");
    }
    private void RefreshAndroidAppIcons(string processName)
    {
        foreach (var row in _androidRows.Values.Where(row => row.IconItem.ProcessName == processName)) row.IconItem.ReloadIcon();
    }
#else
    private int AndroidRowOffset => 0;
    private void RefreshAndroidAppIcons(string processName) { }
    private void InitializeAndroidPanel() { }
    private void StopAndroidPanel() { }
    private void RefreshAndroidRow(bool fromBuildAppRows = false) { }
    private bool CollapseAndroidRows(object? except) => false;
#endif
}

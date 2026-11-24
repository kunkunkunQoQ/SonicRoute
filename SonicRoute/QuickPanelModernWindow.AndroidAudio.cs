using System;
using System.Windows;
#if SONICROUTE_PHONE
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using Button = System.Windows.Controls.Button;
using Slider = System.Windows.Controls.Slider;
#endif

namespace SonicRoute;

public partial class QuickPanelModernWindow
{
#if SONICROUTE_PHONE
    private ToggleButton PhoneAudioButton = null!;
    private bool _checkedPhonePlaybackDevices;
    private void CheckPhonePlaybackDevices()
    {
        if (_checkedPhonePlaybackDevices) return;
        _checkedPhonePlaybackDevices = true;
        _ = AndroidAudioHost.RefreshPhonePlaybackDevicesAsync(null, _uiLifetime.Token);
    }
    private Border AndroidDropHighlight = null!, AndroidComputerDropZone = null!, AndroidSharedDropZone = null!, AirPlayWindowsDropZone = null!;
    private Grid _androidDropZones = null!;
    private readonly record struct IncomingSource(AndroidAudioSource? Android, AirPlayAudioOutput? AirPlay)
    {
        internal string Id => Android?.Id ?? AirPlay!.Id;
        internal IPhoneAudioSession Session => (IPhoneAudioSession?)Android?.Session ?? AirPlay!;
        internal string PhoneName => Android != null ? AndroidAudioHost.Phone.GetDisplayName(Android.Device) : AirPlay!.Name;
        internal string Name => Android?.App is { IsDeviceAudio: false } app ? app.Name : PhoneName;
        internal string Key => Android != null ? ApplicationAudio.AndroidKey(Android.App.Package) : AirPlayHost.AppKey;
        internal AudioAppInfo Describe() => Android != null ? ApplicationAudio.Describe(Android) : ApplicationAudio.Describe(AirPlay!);
    }
    private sealed class AndroidRow
    {
        internal IncomingSource Source;
        internal int MeterId;
        internal StackPanel Root = null!, Details = null!;
        internal Slider Volume = null!;
        internal Border? TrackBg, TrackFill, PeakVisual;
        internal ScaleTransform? PeakTransform;
        internal float PeakLevel;
        internal bool Muted;
        internal Button Icon = null!;
        internal Border MuteDot = null!;
        internal AppItem IconItem = null!;
        internal ToggleButton Expand = null!;
        internal AnimatedExpandHost Wrap = null!;
        internal RotateTransform Arrow = new(0);
        internal bool Expanded, Legacy, ShowOutputName, ApplyingState;
        internal volatile bool Retired;
        internal TextBlock? Name;
        internal string? PhoneName, PlaybackAnchor;
        internal Task PlaybackChange = Task.CompletedTask;
        internal PendingAndroidOutput? PendingOutput;
        internal string[] AvailableOutputs = [];
        internal readonly List<(Button Button, string Id)> Devices = new();
    }
    private static readonly Func<AudioAppInfo, EDataFlow, string, Task<(bool Success, int HResult, string Message)>> DefaultApplyExternalOutput = ApplicationAudio.ApplyEndpointAsync;
    private Func<AudioAppInfo, EDataFlow, string, Task<(bool Success, int HResult, string Message)>> _applyExternalOutput = DefaultApplyExternalOutput;
    private sealed record PendingAndroidOutput(string[] Outputs, string? Anchor, string[] RequiredDevices) : PlaybackIntent(Outputs, Anchor)
    {
        internal volatile bool Invalidated;
        internal string? FirstMissingFrom(IEnumerable<AudioDeviceInfo> devices)
        {
            var available = devices.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return RequiredDevices.FirstOrDefault(id => !available.Contains(id));
        }
        internal bool IsMissingFrom(IEnumerable<AudioDeviceInfo> devices) => FirstMissingFrom(devices) != null;
    }
    private readonly Dictionary<string, AndroidRow> _androidRows = new(StringComparer.Ordinal);
    private readonly List<AndroidRow> _visibleAndroidMeterRows = new();
    private readonly ConcurrentDictionary<int, IPhoneAudioSession> _androidMeterSources = new();
    private int _nextAndroidMeterId;
    private readonly PendingAudioUiChange _pendingAudioUi = new();
    private bool _androidLanguageDirty;
    private bool? _androidDropShared;
    private int AndroidRowOffset => _androidRows.Values.Count(r => AppListPanel.Items.Contains(r.Root));
    private bool HasAndroidMeterRows => _androidRows.Count > 0;
    private bool HasVisibleAndroidMeterRows => _visibleAndroidMeterRows.Count > 0;
    private int NextAndroidMeterId()
    {
        int id = --_nextAndroidMeterId;
        if (id >= 0) id = _nextAndroidMeterId = -1;
        return id;
    }
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
        ApplicationAudio.UiChanged += QueueAudioChange;
        AndroidAudioHost.DragStateChanged += Android_DragStateChanged;
        L10n.Instance.PropertyChanged += AndroidLanguageChanged;
        IsVisibleChanged += AndroidPanelVisibilityChanged;
        Android_DragStateChanged();
        UpdatePhoneAudioButton();
        if (AndroidAudioHost.IsCreated || AirPlayHost.Source != null) RefreshAndroidRow();
    }
    private void InitializeAndroidDropOverlay()
    {
        var grid = _androidDropZones = new Grid();
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
        var modes = new Grid();
        modes.Children.Add(grid);
        AndroidDropHighlight = new Border { Name = "AndroidDropHighlight", CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2), Visibility = Visibility.Collapsed, AllowDrop = true, Child = modes };
        AndroidDropHighlight.PreviewDragOver += Android_DragOver;
        AndroidDropHighlight.PreviewDrop += Android_Drop;
        AndroidDropHighlight.DragLeave += Android_DragLeave;
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
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
            labels.Children.Add(heading); labels.Children.Add(detail);
            var border = new Border { Name = name, CornerRadius = corners, Background = System.Windows.Media.Brushes.Transparent, Child = labels };
            System.Windows.Automation.AutomationProperties.SetName(border, title);
            RegisterName(name, border); return border;
        }
        AndroidComputerDropZone = Zone("AndroidComputerDropZone", L10n.T("Phone.DropComputerOnlyTitle"), L10n.T("Phone.DropComputerOnlyDesc"), new CornerRadius(6,6,0,0));
        AndroidSharedDropZone = Zone("AndroidSharedDropZone", L10n.T("Phone.DropBothPlayTitle"), L10n.T("Phone.DropBothPlayDesc"), new CornerRadius(0,0,6,6));
        AirPlayWindowsDropZone = Zone("AirPlayWindowsDropZone", L10n.T("Dev.SystemDefaultOut"), L10n.T("Phone.AirPlayDropWindowsDesc"), new CornerRadius(6));
        AirPlayWindowsDropZone.Visibility = Visibility.Collapsed;
        AirPlayWindowsDropZone.SetResourceReference(BackgroundProperty, "Theme.Overlay");
        modes.Children.Add(AirPlayWindowsDropZone);
        Grid.SetRow(AndroidSharedDropZone, 1); grid.Children.Add(AndroidComputerDropZone); grid.Children.Add(AndroidSharedDropZone);
        var divider = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        divider.SetResourceReference(BackgroundProperty, "Theme.Accent"); grid.Children.Add(divider);
        var host = (Grid)AppListPanel.Parent;
        Grid.SetRow(AndroidDropHighlight, Grid.GetRow(AppListPanel));
        host.Children.Insert(host.Children.IndexOf(AppListPanel) + 1, AndroidDropHighlight);
        RegisterName(AndroidDropHighlight.Name, AndroidDropHighlight);
        // Preserve the PC viewport during a phone drag without creating phone UI in Lite.
        // Keep the scroll/drop surface alive while hiding its row containers.
        var style = new Style(typeof(ListBoxItem), AppListPanel.ItemContainerStyle);
        var trigger = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(Visibility)) { Source = AndroidDropHighlight }, Value = Visibility.Visible };
        trigger.Setters.Add(new Setter(VisibilityProperty, Visibility.Hidden)); style.Triggers.Add(trigger);
        AppListPanel.ItemContainerStyle = style;
    }
    private void StopAndroidPanel()
    {
        SubscribeAndroidMeter(false);
        ClearAndroidDropHighlight();
        ApplicationAudio.UiChanged -= QueueAudioChange;
        AndroidAudioHost.DragStateChanged -= Android_DragStateChanged;
        L10n.Instance.PropertyChanged -= AndroidLanguageChanged;
        IsVisibleChanged -= AndroidPanelVisibilityChanged;
        foreach (var row in _androidRows.Values)
        {
            row.Retired = true;
            ClearPendingAndroidOutput(row);
            row.Wrap.FinishImmediately();
        }
        _androidRows.Clear();
        _visibleAndroidMeterRows.Clear();
        _androidMeterSources.Clear();
    }
    private void AndroidLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isClosed) return;
        UpdateZoneText(AndroidComputerDropZone, L10n.T("Phone.DropComputerOnlyTitle"), L10n.T("Phone.DropComputerOnlyDesc"));
        UpdateZoneText(AndroidSharedDropZone, L10n.T("Phone.DropBothPlayTitle"), L10n.T("Phone.DropBothPlayDesc"));
        UpdateZoneText(AirPlayWindowsDropZone, L10n.T("Dev.SystemDefaultOut"), L10n.T("Phone.AirPlayDropWindowsDesc"));
        UpdatePhoneAudioButton();
        _androidLanguageDirty = true;
        if (!IsLoaded || IsVisible) RefreshAndroidRow();
        else Android_StateChanged();
    }
    private static void UpdateZoneText(Border? zone, string title, string description)
    {
        if (zone?.Child is StackPanel { Children.Count: >= 2 } labels
            && labels.Children[0] is TextBlock heading && labels.Children[1] is TextBlock detail)
        {
            heading.Text = title;
            detail.Text = description;
            System.Windows.Automation.AutomationProperties.SetName(zone, title);
        }
    }
    internal void UpdatePhoneAudioButton()
    {
        bool enabled = ConfigService.Load().QuickPanelShowPhoneAudio;
        PhoneAudioButton.IsChecked = enabled;
        System.Windows.Automation.AutomationProperties.SetName(PhoneAudioButton, enabled ? L10n.T("Phone.ClosePhoneAudio") : L10n.T("Phone.OpenPhoneAudio"));
        if (enabled) PhoneAudioButton.SetResourceReference(BackgroundProperty, "Theme.Overlay");
        else PhoneAudioButton.SetResourceReference(BackgroundProperty, "Theme.SurfaceBg");
    }
    private void PhoneAudioButton_Click(object sender, RoutedEventArgs e) => ((App)System.Windows.Application.Current).TogglePhoneAudioWindow(this);
    internal bool ContainsTouchDrop(System.Windows.Point screen)
    {
        FrameworkElement area = AndroidDropHighlight.IsVisible ? AndroidDropHighlight : AppListScroll;
        return area.IsVisible && new Rect(area.RenderSize).Contains(area.PointFromScreen(screen));
    }
    internal void HandleTouchDrag(PanelDragInput input, bool drop)
    {
        if (drop) DropAndroid(input);
        else HoverAndroid(input);
    }
    private void Android_DragOver(object sender, System.Windows.DragEventArgs e) => HoverAndroid(new PanelDragInput(e));
    private void HoverAndroid(PanelDragInput e)
    {
        if (AppIconDragController.AppFromDrag(e.Data, AirPlayHost.WindowsDragFormat) is { } airPlay
            && AirPlayHost.Find(airPlay.ExternalSourceId) != null)
        {
            if (AirPlayWindowsDropZone.Visibility != Visibility.Visible)
            {
                _androidDropZones.Visibility = Visibility.Collapsed;
                AirPlayWindowsDropZone.Visibility = AndroidDropHighlight.Visibility = Visibility.Visible;
            }
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
            return;
        }
        bool accepted = e.Data.GetDataPresent(AndroidAppDrag.Format, false)
            && e.Data.GetData(AndroidAppDrag.Format, false) is AndroidAppDrag;
        e.Effects = accepted ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        SetAndroidDropTarget(accepted ? IsSharedDrop(e) : null);
        e.Handled = true;
    }
    private void Android_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        // DragLeave bubbles when the hit child changes. Only leaving the viewport ends a drop target.
        var target = (FrameworkElement)sender;
        var point = e.GetPosition(target);
        if (AndroidAudioHost.IsDragging)
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            point = target.PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
        }
        if (!new Rect(0, 0, target.ActualWidth, target.ActualHeight).Contains(point))
            ClearAndroidDropHighlight();
    }
    private void SetAndroidDropTarget(bool? shared)
    {
        AirPlayWindowsDropZone.Visibility = Visibility.Collapsed;
        _androidDropZones.Visibility = Visibility.Visible;
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
        ClearAndroidDropHighlight();
    }
    internal void ClearAndroidDropHighlight() => SetAndroidDropTarget(null);

    private bool IsSharedDrop(PanelDragInput e)
    {
        FrameworkElement area = AndroidDropHighlight.IsVisible && AndroidDropHighlight.ActualHeight > 0
            ? AndroidDropHighlight : AppListScroll;
        return IsSharedAndroidDrop(e.GetPosition(area).Y, area.ActualHeight);
    }
    private static bool IsSharedAndroidDrop(double y, double height) => height > 0 && y >= height / 2;
    private void Android_Drop(object sender, System.Windows.DragEventArgs e) => DropAndroid(new PanelDragInput(e));
    private async void DropAndroid(PanelDragInput e)
    {
        bool keepPhoneAudio = IsSharedDrop(e);
        ClearAndroidDropHighlight(); e.Handled = true;
        if (AppIconDragController.AppFromDrag(e.Data, AirPlayHost.WindowsDragFormat) is { } airPlay
            && AirPlayHost.Find(airPlay.ExternalSourceId) != null)
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            var result = await ApplicationAudio.ApplyEndpointAsync(airPlay, EDataFlow.eRender, AudioService.SystemDefaultDeviceId);
            if (!result.Success && !_isClosed) ShowOsd(L10n.T("Phone.AirPlayTitle"), result.Message);
            return;
        }
        if (!e.Data.GetDataPresent(AndroidAppDrag.Format, false) || e.Data.GetData(AndroidAppDrag.Format, false) is not AndroidAppDrag payload)
        { e.Effects = System.Windows.DragDropEffects.None; return; }
        e.Effects = System.Windows.DragDropEffects.Copy;
        try
        {
            await AndroidAudioHost.AddAudioAsync(payload.Device, payload.App, keepPhoneAudio);
        }
        catch (Exception ex) { if (!_isClosed) ShowOsd(L10n.T("Phone.AudioWindowTitle"), AndroidErrorTranslation.Format(ex)); }
    }
    private void Android_StateChanged() => QueueAudioChange(AudioUiChange.All);
    private void QueueAudioChange(AudioUiChange change)
    {
        if (_isClosed || !_pendingAudioUi.Add(change)) return;
        _uiLifetime.Post(Dispatcher, () =>
        {
            var pending = _pendingAudioUi.Take();
            if (!_isClosed && (!IsLoaded || IsVisible)) RefreshAudioRows(pending, false);
        });
    }
    private void AndroidPanelVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) Android_StateChanged();
        else StopMeter();
    }
    private void RefreshAndroidRow(bool fromBuildAppRows = false) => RefreshAudioRows(AudioUiChange.All, fromBuildAppRows);

    private void RefreshAudioRows(AudioUiChange change, bool fromBuildAppRows)
    {
        if (_isClosed) return;
        bool outputsChanged = false;
        if (!fromBuildAppRows && change.Has(AudioUiScope.Outputs | AudioUiScope.Routes))
        {
            var cfg = ConfigService.Load();
            if (change.Has(AudioUiScope.Outputs) && SystemDevCombo?.ItemsSource is IList<AudioDeviceInfo> sysOutputs)
            {
                var outputs = PanelDevices.WithSystemDefault(
                    DisplayDevices(ApplicationAudio.MergeExternalOutputs(sysOutputs), cfg), EDataFlow.eRender, cfg);
                outputsChanged = outputs.Count != _outputDisplay.Count || !_outputDisplay.Zip(outputs,
                    static (a, b) => a.Id == b.Id && a.DisplayName == b.DisplayName).All(static same => same);
                if (outputsChanged) _outputDisplay = outputs;
            }
            foreach (var pcRow in _rows.Values)
            {
                int pid = (int)pcRow.App.ProcessId;
                if (!outputsChanged && (!change.Has(AudioUiScope.Routes) || change.Pid != 0 && change.Pid != pid)) continue;
                string? phoneOut = ApplicationAudio.GetPhoneRoutedDeviceId(pid);
                if (phoneOut != null && pcRow.OutputId != phoneOut)
                    UpdateRowName(pcRow, phoneOut);
                else if (phoneOut == null && ApplicationAudio.IsExternalOutputDevice(pcRow.OutputId))
                    UpdateRowName(pcRow, AudioService.SystemDefaultDeviceId);
                else if (pcRow.ShowOutputName)
                    UpdateRowName(pcRow, pcRow.OutputId);
                if (pcRow.Expanded) _ = BuildRowDevicesAsync(pcRow);
            }
        }
        if (!change.Has(AudioUiScope.SourceList | AudioUiScope.Outputs))
        {
            if (change.Has(AudioUiScope.SourceState))
            {
                bool meterChanged = false;
                if (change.SourceId == null)
                {
                    foreach (var row in _androidRows.Values) meterChanged |= RefreshSourceState(row, false);
                }
                else if (_androidRows.TryGetValue(change.SourceId, out var row))
                    meterChanged = RefreshSourceState(row, false);
                if (meterChanged) { UpdateMeterTargets(); QueueMeterViewport(); }
            }
            return;
        }
        var airPlay = AirPlayHost.Source;
        if (!AndroidAudioHost.IsCreated && _androidRows.Count == 0 && airPlay == null) return;
        var config = ConfigService.Load();
        var sources = new List<IncomingSource>();
        if (airPlay != null) sources.Add(new(null, airPlay));
        if (AndroidAudioHost.IsCreated)
            foreach (var source in AndroidAudioHost.Sources.Sources) sources.Add(new(source, null));
        sources.RemoveAll(source => config.HiddenPanelApps.Contains(source.Key, StringComparer.OrdinalIgnoreCase));
        bool legacy = config.QuickPanelUseLegacyAppRows;
        bool outputName = legacy ? config.QuickPanelCompactShowOutputName : config.QuickPanelShowOutputName;
        foreach (var row in _androidRows.Values.ToArray())
        {
            if (sources.Contains(row.Source) && row.Legacy == legacy && row.ShowOutputName == outputName) continue;
            row.Retired = true;
            ClearPendingAndroidOutput(row);
            row.Wrap.FinishImmediately(); AppListPanel.Items.Remove(row.Root); _androidRows.Remove(row.Source.Id);
            _androidMeterSources.TryRemove(row.MeterId, out _);
            _visibleAndroidMeterRows.Remove(row);
        }
        for (int i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (!_androidRows.TryGetValue(source.Id, out var row)) _androidRows[source.Id] = row = CreateAndroidRow(source, legacy, outputName);
            _androidMeterSources[row.MeterId] = source.Session;
            if (outputsChanged && !row.Expanded && row.PendingOutput is { } pending
                && pending.IsMissingFrom(ApplicationAudio.OutputsForApp(row.IconItem.Info, _outputDisplay)))
                ClearPendingAndroidOutput(row);
            string? previousName = row.PhoneName;
            RefreshSourceState(row, true);
            bool phoneNameChanged = previousName != row.PhoneName;
            if (row.Expanded && (outputsChanged || phoneNameChanged || _androidLanguageDirty
                || PhoneDeviceChoicesChanged(row, config)))
                BuildAndroidDevices(row);
            if (i >= AppListPanel.Items.Count || !ReferenceEquals(AppListPanel.Items[i], row.Root))
            {
                AppListPanel.Items.Remove(row.Root); AppListPanel.Items.Insert(i, row.Root);
            }
        }
        _androidLanguageDirty = false;
        if (!fromBuildAppRows && !_isClosed)
        {
            UpdateMeterTargets();
            QueueMeterViewport();
        }
    }

    private bool RefreshSourceState(AndroidRow row, bool refreshLabels)
    {
        var source = row.Source;
        bool wasMetered = row.Volume.IsEnabled && !row.Muted;
        var state = source.Session.State;
        if ((int)row.Volume.Value != state.Volume)
        {
            row.ApplyingState = true;
            try { row.Volume.Value = state.Volume; }
            finally { row.ApplyingState = false; }
        }
        row.Volume.IsEnabled = state.State == CaptureState.Receiving;
        ApplyIncomingRowMutedVisual(row, state.Muted);
        if (!refreshLabels)
        {
            UpdateAndroidRoute(row);
            return wasMetered != (row.Volume.IsEnabled && !row.Muted);
        }
        string phoneName = source.PhoneName;
        bool phoneNameChanged = row.PhoneName != phoneName;
        row.PhoneName = phoneName;
        if (phoneNameChanged || _androidLanguageDirty)
        {
            string appName = source.Name;
            row.IconItem.Info.DisplayName = appName;
            row.IconItem.RefreshSourceLabel(source.AirPlay != null ? "AirPlay" : string.Format(L10n.T("Phone.FromDevice"), phoneName));
            row.Icon.ToolTip = string.Format(L10n.T(source.Android != null ? "Phone.RowIconDragTooltip"
                : "Phone.AirPlayDragFromMixer"), appName);
            System.Windows.Automation.AutomationProperties.SetName(row.Icon, string.Format(L10n.T("Phone.RowIconMuteAutomation"), appName));
            row.Expand.ToolTip = L10n.T("Qp.ExpandDevices");
            System.Windows.Automation.AutomationProperties.SetName(row.Expand, string.Format(L10n.T("Phone.RowExpandAutomation"), appName));
            System.Windows.Automation.AutomationProperties.SetName(row.Volume, string.Format(L10n.T("Phone.RowVolumeAutomation"), appName));
        }
        row.IconItem.RefreshName();
        string errText = AndroidErrorTranslation.Format(state);
        row.Root.ToolTip = source.AirPlay != null ? $"{phoneName} · AirPlay"
            : !string.IsNullOrEmpty(errText) ? errText : string.Format(L10n.T("Phone.RowFromPhoneTooltip"), row.IconItem.Label, phoneName);
        UpdateAndroidRoute(row);
        return wasMetered != (row.Volume.IsEnabled && !row.Muted);
    }

    private AndroidRow CreateAndroidRow(IncomingSource source, bool legacy, bool showOutputName)
    {
        var row = new AndroidRow { Source = source, MeterId = NextAndroidMeterId(), Legacy = legacy, ShowOutputName = showOutputName };
        string appName = source.Name;
        var session = source.Session; var state = session.State;
        row.IconItem = AppItem.From(source.Describe());
        if (source.AirPlay != null) row.IconItem.LoadIcon();
        else AppItem.LoadIconsAsync(new List<AppItem> { row.IconItem });
        row.Root = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        var header = new Border { Height = legacy ? 36 : 52, Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(8) };
        var dock = new DockPanel();
        var iconGrid = CreateRowMuteIcon(row.IconItem, out row.Icon, out row.MuteDot);
        row.Icon.ToolTip = string.Format(L10n.T(source.Android != null ? "Phone.RowIconDragTooltip"
            : "Phone.AirPlayDragFromMixer"), appName);
        row.Icon.Click += (_, _) => session.SetMute(!session.State.Muted);
        if (source.Android is { } android) AndroidAppDragController.Attach(row.Icon, () => new AndroidAppDrag(android.Device, android.App));
        else AppIconDragController.AttachDesktop(row.Icon, row.IconItem, () =>
        {
            if (AirPlayHost.Find(source.Id) != null) AirPlayHost.ShowMirror();
        });
        System.Windows.Automation.AutomationProperties.SetName(row.Icon, string.Format(L10n.T("Phone.RowIconMuteAutomation"), appName));
        dock.Children.Add(iconGrid);
        row.Expand = new ToggleButton { Width = legacy && showOutputName ? 80 : 32, Height = legacy ? 24 : 32,
            VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("RowExpandButton"), ToolTip = L10n.T("Qp.ExpandDevices") };
        var glyph = new TextBlock { Text = "▾", RenderTransform = row.Arrow, RenderTransformOrigin = new System.Windows.Point(0.5, 0.5) };
        if (legacy && showOutputName)
        {
            var label = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            row.Name = NewRowName(); row.Name.MaxWidth = 58; label.Children.Add(row.Name); label.Children.Add(glyph); row.Expand.Content = label;
        }
        else row.Expand.Content = glyph;
        System.Windows.Automation.AutomationProperties.SetName(row.Expand, string.Format(L10n.T("Phone.RowExpandAutomation"), appName));
        DockPanel.SetDock(row.Expand, Dock.Right); dock.Children.Add(row.Expand);
        Slider volume = legacy ? new Slider() : new AppRowVolumeSlider(); row.Volume = volume;
        volume.Minimum = 0; volume.Maximum = 100; volume.Value = state.Volume; volume.Height = legacy ? 32 : 48;
        volume.VerticalAlignment = VerticalAlignment.Center; volume.Margin = new Thickness(0, 0, 6, 0);
        volume.Style = (Style)FindResource("RowSliderStyle");
        volume.ApplyTemplate();
        row.TrackBg = volume.Template.FindName("PART_TrackBg", volume) as Border;
        row.TrackFill = volume.Template.FindName("PART_TrackFill", volume) as Border;
        row.PeakVisual = volume.Template.FindName("PART_PeakVisual", volume) as Border;
        if (row.PeakVisual != null)
        {
            row.PeakTransform = new ScaleTransform(0, 1);
            row.PeakVisual.RenderTransform = row.PeakTransform;
        }
        if (row.TrackBg != null)
            row.TrackBg.SizeChanged += (_, _) =>
            {
                UpdateAndroidSliderLayout(row);
                if (_meterEnabled && IsVisible && !_visibleAndroidMeterRows.Contains(row))
                    QueueMeterViewport();
            };
        System.Windows.Automation.AutomationProperties.SetName(volume, string.Format(L10n.T("Phone.RowVolumeAutomation"), appName));
        volume.ValueChanged += (_, e) =>
        {
            UpdateAndroidSliderLayout(row);
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
    private AudioMeterService CreateAudioMeterService() => new(ReadAndroidMeterPeak);
    private void SubscribeAndroidMeter(bool enabled)
    {
        AndroidPcmSource.PeakActivity -= OnAndroidPeakActivity;
        if (enabled) AndroidPcmSource.PeakActivity += OnAndroidPeakActivity;
    }
    private void OnAndroidPeakActivity() => _meter?.WakeExternal();
    private (bool Active, float Peak) ReadAndroidMeterPeak(int meterId)
    {
        if (!_androidMeterSources.TryGetValue(meterId, out var session)) return (false, 0f);
        var state = session.State;
        if (state.State != CaptureState.Receiving || state.Muted || session.Pcm is not { } pcm) return (false, 0f);
        return pcm.ReadMeterPeak();
    }
    private IEnumerable<int> GetAndroidMeterTargets(bool visibleOnly)
    {
        var rows = visibleOnly ? (IEnumerable<AndroidRow>)_visibleAndroidMeterRows : _androidRows.Values;
        return rows.Where(r => r.Volume.IsEnabled && !r.Muted).Select(r => r.MeterId);
    }
    private void RefreshAndroidMeterViewport()
    {
        _visibleAndroidMeterRows.Clear();
        foreach (var row in _androidRows.Values)
        {
            if (!AppListScroll.IsAncestorOf(row.Volume) || (IsVisible && !row.Volume.IsVisible)
                || row.Volume.ActualHeight <= 0 || !AppListPanel.Items.Contains(row.Root)) continue;
            var bounds = row.Volume.TransformToAncestor(AppListScroll).TransformBounds(new Rect(row.Volume.RenderSize));
            if (bounds.Bottom <= 0 || bounds.Top >= AppListScroll.ActualHeight) continue;
            _visibleAndroidMeterRows.Add(row);
            ApplyVisibleAndroidPeak(row);
        }
    }
    private void ApplyVisibleAndroidPeaks()
    {
        foreach (var row in _visibleAndroidMeterRows) ApplyVisibleAndroidPeak(row);
    }
    private void ApplyVisibleAndroidPeak(AndroidRow row)
    {
        if (row.Muted || !row.Volume.IsEnabled) return;
        float next = _displayPeaks != null && _displayPeaks.TryGetValue(row.MeterId, out float level) ? level : 0f;
        if (row.PeakLevel == next) return;
        row.PeakLevel = next;
        UpdateAndroidPeakVisual(row);
    }
    private void ResetAndroidMeterPeaks(bool clearVisible = false)
    {
        if (clearVisible) _visibleAndroidMeterRows.Clear();
        foreach (var row in _androidRows.Values)
        {
            row.PeakLevel = 0f;
            UpdateAndroidPeakVisual(row);
        }
    }
    private void ApplyAllAndroidRowsMutedVisual(bool muted)
    {
        foreach (var row in _androidRows.Values) ApplyIncomingRowMutedVisual(row, muted);
    }
    private void ApplyIncomingRowMutedVisual(AndroidRow row, bool muted)
    {
        row.Muted = muted;
        if (muted || !row.Volume.IsEnabled) row.PeakLevel = 0f;
        ApplyRowMuteAppearance(row.Volume, row.MuteDot, muted);
        UpdateAndroidPeakVisual(row);
    }
    private void UpdateAndroidSliderLayout(AndroidRow row)
    {
        if (!VolumeTrackLayout.Update(row.Volume, row.TrackBg, row.TrackFill)) return;
        UpdateAndroidPeakVisual(row);
    }
    private void UpdateAndroidPeakVisual(AndroidRow row) =>
        UpdatePeakVisual(row.Volume, row.TrackBg, row.PeakVisual, row.PeakTransform, row.PeakLevel, row.Muted);
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
        var exit = new Button { Content = L10n.T("Tray.Exit"), ToolTip = L10n.T("Phone.StopReceivingApp") };
        exit.SetResourceReference(StyleProperty, "DevButton");
        System.Windows.Automation.AutomationProperties.SetName(exit, string.Format(L10n.T("Phone.RowExitAutomation"), row.Source.Name));
        exit.Click += async (_, _) =>
        {
            try
            {
                if (row.Source.Android is { } android)
                    await ((App)System.Windows.Application.Current).ReturnAndroidToPhoneAsync(new AndroidAppDrag(android.Device, android.App));
                else AirPlayHost.Disconnect(row.Source.Id);
            }
            catch (Exception ex) { if (!_isClosed) ShowOsd(L10n.T("Phone.AudioWindowTitle"), AndroidErrorTranslation.Format(ex)); }
        };
        var choices = ApplicationAudio.OutputsForApp(row.IconItem.Info, _outputDisplay).ToArray();
        row.AvailableOutputs = choices.Select(device => device.Id).ToArray();
        if (row.PendingOutput is { } pending && pending.IsMissingFrom(choices)) ClearPendingAndroidOutput(row);
        var hidden = ConfigService.Load().HiddenOutputDevices;
        AddRowDeviceGroup(row.Details, choices.Where(device => !hidden.Contains(device.Id)), EDataFlow.eRender, (button, device) =>
        {
            button.IsEnabled = true;
            row.Devices.Add((button, device.Id));
            if (row.Source.Session is IMultiOutputAudioSession)
            {
                button.ToolTip = L10n.T("Qp.PlaybackMultiSelect");
                System.Windows.Automation.AutomationProperties.SetHelpText(button, (string)button.ToolTip);
                button.Click += async (_, _) => await QueueIncomingOutputAsync(row, device.Id, System.Windows.Input.Keyboard.Modifiers);
            }
            else button.Click += async (_, _) => await QueueSingleExternalOutputAsync(row, device);
        }, trailingAction: exit);
        UpdateAndroidRoute(row);
    }

    private static bool PhoneDeviceChoicesChanged(AndroidRow row, AppConfig config)
    {
        var choices = ApplicationAudio.OutputsForApp(row.IconItem.Info, AndroidAudioHost.GetOutputDevices());
        var expected = choices.Where(d => ApplicationAudio.IsPhoneOutputDevice(d.Id) && !config.HiddenOutputDevices.Contains(d.Id))
            .Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !expected.SetEquals(row.Devices.Where(d => ApplicationAudio.IsPhoneOutputDevice(d.Id)).Select(d => d.Id));
    }

    private static HashSet<string> IncomingOutputs(IncomingSource source)
    {
        var selected = ((IMultiOutputAudioSession)source.Session).GetOutputs()
            .Select(id => id ?? AudioService.SystemDefaultDeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (source.Android?.Session.UsesSourcePhonePlayback == true) selected.Add(AndroidAudioSession.SourcePhoneOutputId);
        return selected;
    }

    private Task QueueIncomingOutputAsync(AndroidRow row, string clicked, System.Windows.Input.ModifierKeys modifiers)
    {
        var android = row.Source.Android;
        var airPlay = row.Source.AirPlay;
        if ((android == null && airPlay == null) || !Current() || !CanChange())
            return Task.CompletedTask;
        var timing = android != null ? AndroidOutputSwitchTiming.Create() : null;
        var order = row.Devices.Select(d => d.Id).ToArray();
        if (!order.Contains(clicked, StringComparer.OrdinalIgnoreCase)) return Task.CompletedTask;
        var prior = row.PendingOutput;
        var selected = prior == null ? IncomingOutputs(row.Source) : prior.Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var intent = CreatePlaybackIntent(order, selected, clicked, prior?.Anchor ?? row.PlaybackAnchor, modifiers);
        var next = intent.Outputs;
        if (android != null)
        {
            bool OwnForcedOutput(string id) => AndroidOutputIdentity.TryParseDeviceId(id, out var key, out _)
                && string.Equals(key, AndroidDeviceIdentity.Key(android.Device), StringComparison.OrdinalIgnoreCase);
            if (next.Contains(AndroidAudioSession.SourcePhoneOutputId) && next.Any(OwnForcedOutput))
                next = clicked == AndroidAudioSession.SourcePhoneOutputId ? next.Where(id => !OwnForcedOutput(id)).ToArray()
                    : next.Where(id => id != AndroidAudioSession.SourcePhoneOutputId).ToArray();
        }
        var liveChoices = ApplicationAudio.OutputsForApp(row.IconItem.Info, _outputDisplay).ToArray();
        if (next.Contains(clicked, StringComparer.OrdinalIgnoreCase)
            && !liveChoices.Any(d => string.Equals(d.Id, clicked, StringComparison.OrdinalIgnoreCase)))
        {
            ShowOsd(row.Source.Name, ApplicationAudio.IsPhoneOutputDevice(clicked)
                ? L10n.T("Phone.Err.SelectedPhoneOutputDisconnected") : L10n.T("Ov.CurrentUnavailable"));
            return Task.CompletedTask;
        }

        var request = new PendingAndroidOutput(next, intent.Anchor,
            next.Where(id => row.AvailableOutputs.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray());
        row.PendingOutput = request;
        UpdateAndroidRoute(row);
        timing?.Mark("ui_intent");
        var previous = row.PlaybackChange;
        return row.PlaybackChange = ApplyAsync();

        bool CanChange() => android != null ? android.Session.CanChangeActiveOutputs : airPlay!.CanChangeActiveOutputs;
        bool Current() => !_isClosed && !row.Retired
            && _androidRows.TryGetValue(row.Source.Id, out var live) && ReferenceEquals(live, row)
            && (android != null
                ? ReferenceEquals(AndroidAudioHost.Sources.FindSource(row.Source.Id), android)
                : ReferenceEquals(AirPlayHost.Find(row.Source.Id), airPlay));

        async Task ApplyAsync()
        {
            string outcome = "retired";
            try
            {
                using (timing?.Measure("row_queue")) await previous;
                if (!Current() || !CanChange() || request.Invalidated) return;
                if (request.FirstMissingFrom(ApplicationAudio.OutputsForApp(row.IconItem.Info, _outputDisplay)) is { } missingId)
                {
                    outcome = "unavailable";
                    ShowOsd(row.Source.Name, ApplicationAudio.IsPhoneOutputDevice(missingId)
                        ? L10n.T("Phone.Err.SelectedPhoneOutputDisconnected") : L10n.T("Ov.CurrentUnavailable"));
                    return;
                }
                var outputs = request.Outputs.Select(id => AudioService.IsSystemDefault(id) ? null : id).ToArray();
                if (android != null)
                {
                    await Task.Run(() => android.Session.SetActiveOutputsAsync(outputs,
                        () => !row.Retired && !request.Invalidated && !android.Lifetime.IsCancellationRequested, timing));
                }
                else if (request.Outputs.Length == 1 && !ReferenceEquals(_applyExternalOutput, DefaultApplyExternalOutput))
                {
                    var result = await _applyExternalOutput(row.IconItem.Info, EDataFlow.eRender, request.Outputs[0]);
                    if (!Current() || request.Invalidated) return;
                    if (!result.Success)
                    {
                        outcome = "failed";
                        ShowOsd(L10n.T("Phone.AirPlayTitle"), result.Message);
                        return;
                    }
                }
                else
                {
                    await airPlay!.SetActiveOutputsAsync(outputs, () => !row.Retired && !request.Invalidated);
                }
                outcome = "committed";
                if (Current()) row.PlaybackAnchor = request.Anchor;
            }
            catch (OperationCanceledException) when (!Current() || !CanChange() || request.Invalidated) { }
            catch (Exception ex)
            {
                outcome = "failed";
                if (Current() && !request.Invalidated)
                {
                    ShowOsd(airPlay != null ? L10n.T("Phone.AirPlayTitle") : row.Source.Name,
                        airPlay != null ? ApplicationAudio.FormatAirPlayRouteError(ex, request.Outputs)
                            : AndroidErrorTranslation.Format(ex));
                }
            }
            finally
            {
                using (timing?.Measure("ui_reconcile"))
                {
                    if (Current() && ReferenceEquals(row.PendingOutput, request))
                    {
                        row.PendingOutput = null;
                        UpdateAndroidRoute(row);
                    }
                }
                if (android != null) timing?.Report(android.Session, outcome, request.Outputs);
            }
        }
    }

    private static void ClearPendingAndroidOutput(AndroidRow row)
    {
        if (row.PendingOutput is { } pending) pending.Invalidated = true;
        row.PendingOutput = null;
    }

    private Task QueueSingleExternalOutputAsync(AndroidRow row, AudioDeviceInfo device)
    {
        if (!Current()) return Task.CompletedTask;
        if (!Available(device.Id))
        {
            ShowOsd(row.Source.Name, ApplicationAudio.IsPhoneOutputDevice(device.Id)
                ? L10n.T("Phone.Err.SelectedPhoneOutputDisconnected") : L10n.T("Ov.CurrentUnavailable"));
            return Task.CompletedTask;
        }

        var request = new PendingAndroidOutput([device.Id], device.Id, [device.Id]);
        row.PendingOutput = request;
        UpdateAndroidRoute(row);
        var previous = row.PlaybackChange;
        return row.PlaybackChange = ApplyAsync();

        bool Current() => !_isClosed && !row.Retired
            && _androidRows.TryGetValue(row.Source.Id, out var live) && ReferenceEquals(live, row)
            && (row.Source.AirPlay != null
                ? ReferenceEquals(AirPlayHost.Find(row.Source.Id), row.Source.AirPlay)
                : ReferenceEquals(AndroidAudioHost.Sources.FindSource(row.Source.Id)?.Session, row.Source.Session));

        bool Available(string id) => ApplicationAudio.OutputsForApp(row.IconItem.Info, _outputDisplay)
            .Any(output => string.Equals(output.Id, id, StringComparison.OrdinalIgnoreCase));

        async Task ApplyAsync()
        {
            try
            {
                await previous;
                if (!Current() || request.Invalidated) return;
                if (row.Source.AirPlay is { } airPlay && airPlay.State.State != CaptureState.Receiving) return;
                if (row.Source.Android is { } android && !android.Session.CanChangeActiveOutputs) return;
                if (request.IsMissingFrom(ApplicationAudio.OutputsForApp(row.IconItem.Info, _outputDisplay)))
                {
                    ShowOsd(row.Source.Name, ApplicationAudio.IsPhoneOutputDevice(device.Id)
                        ? L10n.T("Phone.Err.SelectedPhoneOutputDisconnected") : L10n.T("Ov.CurrentUnavailable"));
                    return;
                }

                var result = await _applyExternalOutput(row.IconItem.Info, EDataFlow.eRender, device.Id);
                if (!Current() || request.Invalidated) return;
                if (!result.Success)
                    ShowOsd(L10n.T(row.Source.AirPlay != null ? "Phone.AirPlayTitle" : "Phone.AudioWindowTitle"), result.Message);
            }
            catch (OperationCanceledException) when (!Current() || request.Invalidated) { }
            catch (Exception ex)
            {
                if (Current() && !request.Invalidated)
                    ShowOsd(L10n.T(row.Source.AirPlay != null ? "Phone.AirPlayTitle" : "Phone.AudioWindowTitle"), AndroidErrorTranslation.Format(ex));
            }
            finally
            {
                if (Current() && ReferenceEquals(row.PendingOutput, request))
                {
                    row.PendingOutput = null;
                    UpdateAndroidRoute(row);
                }
            }
        }
    }

    private void UpdateAndroidRoute(AndroidRow row)
    {
        bool stopped = row.Source.Android is { } source ? !source.Session.CanChangeActiveOutputs
            : row.Source.AirPlay is { } airPlay && airPlay.State.State != CaptureState.Receiving;
        if (row.PendingOutput != null && stopped)
            ClearPendingAndroidOutput(row);
        string actualId = row.Source.Session.State.OutputId ?? AudioService.SystemDefaultDeviceId;
        string displayId = row.PendingOutput is { } pendingOutput && !stopped
            ? SelectOptimisticPrimaryOutput(pendingOutput.Outputs, actualId, preferPhysical: false, lowPriorityId: AndroidAudioSession.SourcePhoneOutputId)
            : actualId;
        if (row.Name != null)
        {
            string name = row.IconItem.Label;
            if (row.ShowOutputName)
            {
                bool systemDefault = AudioService.IsSystemDefault(displayId);
                AudioDeviceInfo? output = null;
                foreach (var d in _outputDisplay)
                {
                    if (!string.Equals(d.Id, displayId, StringComparison.OrdinalIgnoreCase)) continue;
                    output = d;
                    break;
                }
                string outputName = string.Equals(displayId, AndroidAudioSession.SourcePhoneOutputId, StringComparison.OrdinalIgnoreCase)
                    ? L10n.T("Phone.NativeAudioOutput")
                    : output != null ? PanelDevices.Name(output)
                    : L10n.T(systemDefault ? "Dev.SystemDefaultOut" : "Ov.CurrentUnavailable");
                if (row.Legacy)
                    name = PanelDevices.ShortName(outputName);
                else if (systemDefault)
                {
                    string? suffix = null;
                    for (int i = 0; i < _outputDisplay.Count; i++)
                        if (_outputDisplay[i].IsDefault) { suffix = PanelDevices.Name(_outputDisplay[i]); break; }
                    name = suffix == null ? outputName : outputName + " · " + suffix;
                }
                else name = outputName;
            }
            if (row.Name.Text != name)
            {
                row.Name.Text = name;
                row.Name.ToolTip = name;
                if (!row.Legacy) row.Volume.ToolTip = name;
            }
        }
        if (!row.Expanded || row.Devices.Count == 0) return;
        var selected = row.PendingOutput?.Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? (row.Source.Session is IMultiOutputAudioSession ? IncomingOutputs(row.Source) : null);
        foreach (var entry in row.Devices)
            SetRowDeviceSelection(entry.Button, entry.Id, stopped ? "" : selected == null ? actualId : selected.Contains(entry.Id) ? entry.Id : "");
    }
    private void RefreshAndroidAppIcons(string processName)
    {
        foreach (var row in _androidRows.Values.Where(row => row.IconItem.ProcessName == processName)) row.IconItem.ReloadIcon();
    }
#else
    private int AndroidRowOffset => 0;
    private bool HasAndroidMeterRows => false;
    private bool HasVisibleAndroidMeterRows => false;
    private SonicRoute.Core.AudioMeterService CreateAudioMeterService() => new();
    private void SubscribeAndroidMeter(bool enabled) { }
    private System.Collections.Generic.IEnumerable<int> GetAndroidMeterTargets(bool visibleOnly) => System.Linq.Enumerable.Empty<int>();
    private void RefreshAndroidMeterViewport() { }
    private void ApplyVisibleAndroidPeaks() { }
    private void ResetAndroidMeterPeaks(bool clearVisible = false) { }
    private void ApplyAllAndroidRowsMutedVisual(bool muted) { }
    private void RefreshAndroidAppIcons(string processName) { }
    private void InitializeAndroidPanel() { }
    private void StopAndroidPanel() { }
    private void RefreshAndroidRow(bool fromBuildAppRows = false) { }
    private bool CollapseAndroidRows(object? except) => false;
#endif
}

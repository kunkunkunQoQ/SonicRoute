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
using SonicRoute.WindowsLink;
using Button = System.Windows.Controls.Button;
using Slider = System.Windows.Controls.Slider;
using Brush = System.Windows.Media.Brush;
#endif

namespace SonicRoute;

public partial class QuickPanelModernWindow
{
#if SONICROUTE_PHONE
    private ToggleButton PhoneAudioButton = null!;
    private Border AndroidDropHighlight = null!, AndroidComputerDropZone = null!, AndroidSharedDropZone = null!, AirPlayWindowsDropZone = null!, WindowsLinkDropZone = null!;
    private Grid _androidDropZones = null!;
    private readonly record struct IncomingSource(AndroidAudioSource? Android, AirPlayAudioOutput? AirPlay, WindowsLinkAudioSource? WindowsLink)
    {
        internal string Id => Android?.Id ?? AirPlay?.Id ?? WindowsLink!.Id;
        internal IPhoneAudioSession Session => (IPhoneAudioSession?)Android?.Session ?? (IPhoneAudioSession?)AirPlay ?? WindowsLink!.Session;
        internal string PhoneName => Android != null ? AndroidAudioHost.Phone.GetDisplayName(Android.Device)
            : AirPlay != null ? AirPlay.Name
            : ResolveWindowsPeerName(WindowsLink!);

        private static string ResolveWindowsPeerName(WindowsLinkAudioSource win) =>
            WindowsLinkHub.Service?.Peers.FirstOrDefault(p => string.Equals(p.Id, win.PeerId, StringComparison.OrdinalIgnoreCase))?.DisplayName
                ?? win.Name;
        internal string Name => Android?.App is { IsDeviceAudio: false } app ? app.Name
            : WindowsLink != null && !string.Equals(WindowsLink.AppKey, LinkRemoteApp.SystemAudioKey, StringComparison.OrdinalIgnoreCase)
                ? WindowsLink.Name
                : PhoneName;
        internal string Key => Android != null ? ApplicationAudio.AndroidKey(Android.App.Package)
            : AirPlay != null ? AirPlayHost.AppKey
            : "winlink:" + WindowsLink!.AppKey;
        internal AudioAppInfo Describe() => Android != null ? ApplicationAudio.Describe(Android)
            : AirPlay != null ? ApplicationAudio.Describe(AirPlay)
            : ApplicationAudio.Describe(WindowsLink!);
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
        internal AppItem IconItem = null!;
        internal ToggleButton Expand = null!;
        internal AnimatedExpandHost Wrap = null!;
        internal RotateTransform Arrow = new(0);
        internal bool Expanded, Legacy, ShowOutputName, ApplyingState, ChangingOutput;
        internal TextBlock? Name;
        internal string? PhoneName;
        internal readonly List<(Button Button, string Id)> Devices = new();
    }
    private readonly Dictionary<string, AndroidRow> _androidRows = new(StringComparer.Ordinal);
    private readonly List<AndroidRow> _visibleAndroidMeterRows = new();
    private readonly ConcurrentDictionary<int, IPhoneAudioSession> _androidMeterSources = new();
    private int _nextAndroidMeterId;
    private int _androidRefreshQueued;
    private bool _androidLanguageDirty;
    private bool? _androidDropShared;
    private WindowsLinkAppDrag? _windowsLinkDropSource;
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
        ApplicationAudio.Changed += Android_StateChanged;
        AndroidAudioHost.DragStateChanged += Android_DragStateChanged;
        WindowsLinkHub.DragStateChanged += Android_DragStateChanged;
        L10n.Instance.PropertyChanged += AndroidLanguageChanged;
        IsVisibleChanged += AndroidPanelVisibilityChanged;
        Android_DragStateChanged();
        UpdatePhoneAudioButton();
        if (AndroidAudioHost.IsCreated || AirPlayHost.Source != null || WindowsLinkHub.HasIncomingAudio) RefreshAndroidRow();
    }
    private void InitializeAndroidDropOverlay()
    {
        var grid = _androidDropZones = new Grid();
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
        var modes = new Grid();
        modes.Children.Add(grid);
        AndroidDropHighlight = new Border { Name = "AndroidDropHighlight", CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2), Visibility = Visibility.Collapsed, IsHitTestVisible = false, Child = modes };
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
        WindowsLinkDropZone = Zone("WindowsLinkDropZone", L10n.T("Phone.WindowsLinkDropPlayTitle"), L10n.T("Phone.WindowsLinkDropPlayDesc"), new CornerRadius(6));
        WindowsLinkDropZone.Visibility = Visibility.Collapsed;
        WindowsLinkDropZone.SetResourceReference(BackgroundProperty, "Theme.Overlay");
        modes.Children.Add(AirPlayWindowsDropZone);
        modes.Children.Add(WindowsLinkDropZone);
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
        ApplicationAudio.Changed -= Android_StateChanged;
        AndroidAudioHost.DragStateChanged -= Android_DragStateChanged;
        WindowsLinkHub.DragStateChanged -= Android_DragStateChanged;
        L10n.Instance.PropertyChanged -= AndroidLanguageChanged;
        IsVisibleChanged -= AndroidPanelVisibilityChanged;
        foreach (var row in _androidRows.Values) row.Wrap.FinishImmediately();
        _androidRows.Clear();
        _visibleAndroidMeterRows.Clear();
        _androidMeterSources.Clear();
    }
    private void AndroidLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isClosed) return;
        UpdateIncomingDropText(_windowsLinkDropSource);
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
    private void Android_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (AppIconDragController.WindowsLinkFromDrag(e.Data) is { } winDrag)
        {
            bool shared = IsSharedAndroidDrop(e.GetPosition(AppListScroll).Y, AppListScroll.ActualHeight);
            SetAndroidDropTarget(winDrag.Window ? null : shared, winDrag.Window ? null : winDrag);
            e.Effects = WindowsLinkDropAllowed(winDrag, shared) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (AppIconDragController.AppFromDrag(e.Data, AirPlayHost.WindowsDragFormat) is { } airPlay
            && AirPlayHost.Find(airPlay.ExternalSourceId) != null)
        {
            if (AirPlayWindowsDropZone.Visibility != Visibility.Visible)
            {
                _androidDropZones.Visibility = Visibility.Collapsed;
                WindowsLinkDropZone.Visibility = Visibility.Collapsed;
                AirPlayWindowsDropZone.Visibility = AndroidDropHighlight.Visibility = Visibility.Visible;
            }
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
            return;
        }
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
        if (AndroidAudioHost.IsDragging || WindowsLinkHub.IsDragging)
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            point = AppListScroll.PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
        }
        if (!new Rect(0, 0, AppListScroll.ActualWidth, AppListScroll.ActualHeight).Contains(point))
            ClearAndroidDropHighlight();
    }
    private void SetAndroidDropTarget(bool? shared, WindowsLinkAppDrag? windowsLink = null)
    {
        bool sourceChanged = !ReferenceEquals(_windowsLinkDropSource, windowsLink);
        bool airPlayWasVisible = AirPlayWindowsDropZone.Visibility == Visibility.Visible;
        bool visible = shared != null || windowsLink != null || AndroidAudioHost.IsDragging;
        if (!sourceChanged && !airPlayWasVisible && _androidDropShared == shared
            && (AndroidDropHighlight.Visibility == Visibility.Visible) == visible) return;

        _windowsLinkDropSource = windowsLink;
        _androidDropShared = shared;
        if (sourceChanged) UpdateIncomingDropText(windowsLink);
        AirPlayWindowsDropZone.Visibility = Visibility.Collapsed;
        bool systemAudio = windowsLink?.App.IsSystemAudio == true;
        WindowsLinkDropZone.Visibility = systemAudio ? Visibility.Visible : Visibility.Collapsed;
        _androidDropZones.Visibility = systemAudio ? Visibility.Collapsed : Visibility.Visible;
        AndroidDropHighlight.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;

        bool onlyAllowed = windowsLink == null || WindowsLinkDropAllowed(windowsLink, shared: false);
        bool sharedAllowed = windowsLink == null || WindowsLinkDropAllowed(windowsLink, shared: true);
        AndroidComputerDropZone.Opacity = onlyAllowed ? 1 : 0.62;
        AndroidSharedDropZone.Opacity = sharedAllowed ? 1 : 0.62;
        WindowsLinkDropZone.Opacity = sharedAllowed ? 1 : 0.62;
        AndroidComputerDropZone.SetResourceReference(BackgroundProperty, shared == false && onlyAllowed ? "Theme.Overlay" : "Theme.SurfaceBg");
        AndroidSharedDropZone.SetResourceReference(BackgroundProperty, shared == true && sharedAllowed ? "Theme.Overlay" : "Theme.SurfaceBg");
    }

    private void UpdateIncomingDropText(WindowsLinkAppDrag? source)
    {
        if (source == null)
        {
            UpdateZoneText(AndroidComputerDropZone, L10n.T("Phone.DropComputerOnlyTitle"), L10n.T("Phone.DropComputerOnlyDesc"));
            UpdateZoneText(AndroidSharedDropZone, L10n.T("Phone.DropBothPlayTitle"), L10n.T("Phone.DropBothPlayDesc"));
            return;
        }

        bool canSend = WindowsLinkHub.CanPullAudio(source.Peer, source.App);
        string unavailable = canSend ? "" : source.Peer.Connected
            ? string.Format(L10n.T("Phone.WindowsLinkAudioUnavailable"), source.Peer.DisplayName)
            : L10n.T("Phone.Disconnected");
        if (source.App.IsSystemAudio)
        {
            UpdateZoneText(WindowsLinkDropZone, L10n.T("Phone.WindowsLinkDropPlayTitle"),
                canSend ? L10n.T("Phone.WindowsLinkDropPlayDesc") : unavailable);
            return;
        }

        string onlyDetail = !canSend ? unavailable : WindowsLinkDropAllowed(source, shared: false)
            ? L10n.T("Phone.DropComputerOnlyDesc") : L10n.T("Phone.WindowsLinkExclusiveUnavailable");
        UpdateZoneText(AndroidComputerDropZone, L10n.T("Phone.WindowsLinkDropOnlyHere"), onlyDetail);
        UpdateZoneText(AndroidSharedDropZone, L10n.T("Phone.WindowsLinkDropBothComputers"),
            canSend ? L10n.T("Phone.DropBothPlayDesc") : unavailable);
    }
    private void Android_DragStateChanged()
    {
        if (_isClosed) return;
        if (!Dispatcher.CheckAccess()) { _uiLifetime.Post(Dispatcher, Android_DragStateChanged); return; }
        ClearAndroidDropHighlight();
    }
    internal void ClearAndroidDropHighlight() =>
        SetAndroidDropTarget(null, WindowsLinkHub.DragSource is { Window: false } drag ? drag : null);

    private static bool IsSharedAndroidDrop(double y, double height) => height > 0 && y >= height / 2;
    private static bool WindowsLinkDropAllowed(WindowsLinkAppDrag drag, bool shared) =>
        !drag.Window && WindowsLinkHub.CanPullAudio(drag.Peer, drag.App)
        && (shared || drag.App.IsSystemAudio || (drag.Peer.Capabilities & LinkCapabilities.DestinationOnly) != 0);
    private async void Android_Drop(object sender, System.Windows.DragEventArgs e)
    {
        bool keepPhoneAudio = IsSharedAndroidDrop(e.GetPosition(AppListScroll).Y, AppListScroll.ActualHeight);
        ClearAndroidDropHighlight(); e.Handled = true;
        if (AppIconDragController.WindowsLinkFromDrag(e.Data) is { } winDrag)
        {
            if (!WindowsLinkDropAllowed(winDrag, keepPhoneAudio)) { e.Effects = System.Windows.DragDropEffects.None; return; }
            e.Effects = System.Windows.DragDropEffects.Copy;
            try { await WindowsLinkHub.PullAudioAsync(winDrag.Peer, winDrag.App,
                destinationOnly: !keepPhoneAudio && !winDrag.App.IsSystemAudio); }
            catch (Exception ex) { if (!_isClosed) ShowOsd(winDrag.App.Name, ex.Message); }
            return;
        }
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
    {
        if (IsVisible) Android_StateChanged();
        else StopMeter();
    }
    private void RefreshAndroidRow(bool fromBuildAppRows = false)
    {
        if (_isClosed) return;
        bool outputsChanged = false;
        if (!fromBuildAppRows)
        {
            var cfg = ConfigService.Load();
            if (SystemDevCombo?.ItemsSource is IList<AudioDeviceInfo> sysOutputs)
            {
                var outputs = PanelDevices.WithSystemDefault(
                    DisplayDevices(ApplicationAudio.MergeExternalOutputs(sysOutputs), cfg), EDataFlow.eRender, cfg);
                outputsChanged = outputs.Count != _outputDisplay.Count || !_outputDisplay.Zip(outputs,
                    static (a, b) => a.Id == b.Id && a.DisplayName == b.DisplayName).All(static same => same);
                if (outputsChanged) _outputDisplay = outputs;
            }
            var exclusiveRoutes = WindowsLinkHub.Service?.Streams
                .Where(stream => !stream.Incoming && stream.DestinationOnly && stream.ProcessId.HasValue)
                .Select(stream => stream.ProcessId!.Value).ToHashSet();
            foreach (var pcRow in _rows.Values)
            {
                int pid = (int)pcRow.App.ProcessId;
                bool exclusive = exclusiveRoutes?.Contains(pid) == true;
                bool exclusiveChanged = pcRow.WindowsLinkExclusive != exclusive;
                pcRow.WindowsLinkExclusive = exclusive;
                string? phoneOut = ApplicationAudio.GetPhoneRoutedDeviceId(pid);
                if (phoneOut != null && pcRow.OutputId != phoneOut)
                    UpdateRowName(pcRow, phoneOut);
                else if (phoneOut == null && ApplicationAudio.IsExternalOutputDevice(pcRow.OutputId))
                    UpdateRowName(pcRow, AudioService.SystemDefaultDeviceId);
                else if (pcRow.ShowOutputName)
                    UpdateRowName(pcRow, pcRow.OutputId);
                if (pcRow.Expanded) _ = BuildRowDevicesAsync(pcRow);
                else if (pcRow.ShowOutputName && exclusiveChanged) _ = RefreshWindowsLinkEndpointAsync(pcRow, pid, exclusive);
            }
        }
        var airPlay = AirPlayHost.Source;
        var winLinks = WindowsLinkHub.IncomingAudioSources;
        if (!AndroidAudioHost.IsCreated && _androidRows.Count == 0 && airPlay == null && winLinks.Length == 0) return;
        var config = ConfigService.Load();
        var sources = new List<IncomingSource>();
        if (airPlay != null) sources.Add(new(null, airPlay, null));
        foreach (var wl in winLinks) sources.Add(new(null, null, wl));
        if (AndroidAudioHost.IsCreated)
            foreach (var source in AndroidAudioHost.Sources.Sources) sources.Add(new(source, null, null));
        sources.RemoveAll(source => config.HiddenPanelApps.Contains(source.Key, StringComparer.OrdinalIgnoreCase));
        bool legacy = config.QuickPanelUseLegacyAppRows;
        bool outputName = legacy ? config.QuickPanelCompactShowOutputName : config.QuickPanelShowOutputName;
        foreach (var row in _androidRows.Values.ToArray())
        {
            if (sources.Contains(row.Source) && row.Legacy == legacy && row.ShowOutputName == outputName) continue;
            row.Wrap.FinishImmediately(); AppListPanel.Items.Remove(row.Root); _androidRows.Remove(row.Source.Id);
            _androidMeterSources.TryRemove(row.MeterId, out _);
            _visibleAndroidMeterRows.Remove(row);
        }
        for (int i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (!_androidRows.TryGetValue(source.Id, out var row)) _androidRows[source.Id] = row = CreateAndroidRow(source, legacy, outputName);
            _androidMeterSources[row.MeterId] = source.Session;
            var state = source.Session.State;
            if ((int)row.Volume.Value != state.Volume)
            {
                row.ApplyingState = true;
                try { row.Volume.Value = state.Volume; }
                finally { row.ApplyingState = false; }
            }
            row.Volume.IsEnabled = state.State == CaptureState.Receiving;
            row.Muted = state.Muted;
            if (state.Muted || !row.Volume.IsEnabled) row.PeakLevel = 0f;
            row.Volume.Foreground = state.Muted ? ThemeService.GetInvertedAccentBrush() : (Brush)FindResource("Theme.Accent");
            row.Icon.Opacity = state.Muted ? 0.45 : 1;
            UpdateAndroidPeakVisual(row);
            string phoneName = source.PhoneName;
            bool phoneNameChanged = row.PhoneName != phoneName;
            row.PhoneName = phoneName;
            if (phoneNameChanged || _androidLanguageDirty)
            {
                string appName = source.Name;
                row.IconItem.Info.DisplayName = appName;
                row.IconItem.RefreshSourceLabel(source.AirPlay != null ? "AirPlay" : string.Format(L10n.T("Phone.FromDevice"), phoneName));
                row.Icon.ToolTip = string.Format(L10n.T(source.Android != null ? "Phone.RowIconDragTooltip"
                    : source.WindowsLink != null ? "Phone.WindowsLinkRowIconDragTooltip" : "Phone.AirPlayDragFromMixer"), appName);
                System.Windows.Automation.AutomationProperties.SetName(row.Icon, string.Format(L10n.T("Phone.RowIconMuteAutomation"), appName));
                row.Expand.ToolTip = L10n.T("Qp.ExpandDevices");
                System.Windows.Automation.AutomationProperties.SetName(row.Expand, string.Format(L10n.T("Phone.RowExpandAutomation"), appName));
                System.Windows.Automation.AutomationProperties.SetName(row.Volume, string.Format(L10n.T("Phone.RowVolumeAutomation"), appName));
            }
            row.IconItem.RefreshName();
            string errText = AndroidErrorTranslation.Format(state);
            row.Root.ToolTip = source.AirPlay != null ? $"{phoneName} · AirPlay"
                : source.WindowsLink != null ? string.Format(L10n.T("Phone.RowFromPhoneTooltip"), row.IconItem.Label, phoneName)
                : !string.IsNullOrEmpty(errText) ? errText : string.Format(L10n.T("Phone.RowFromPhoneTooltip"), row.IconItem.Label, phoneName);
            UpdateAndroidRoute(row);
            if (row.Expanded && (outputsChanged || phoneNameChanged || _androidLanguageDirty
                || source.Android is { } android && PhoneDeviceChoicesChanged(row, android.Device, config)))
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

    private async Task RefreshWindowsLinkEndpointAsync(AppRow row, int pid, bool exclusive)
    {
        try
        {
            var endpoint = await _uiLifetime.ReadAsync(pid,
                static id => ApplicationAudio.GetPersistedEndpoint(id, EDataFlow.eRender));
            if (!_isClosed && _rows.GetValueOrDefault(pid) == row && row.WindowsLinkExclusive == exclusive)
                UpdateRowName(row, endpoint == null ? AudioService.SystemDefaultDeviceId : AudioPolicyConfig.UnpackDeviceId(endpoint));
        }
        catch (OperationCanceledException) { }
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
        row.Icon = new Button { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("RowIconButton"), ToolTip = string.Format(L10n.T(source.Android != null ? "Phone.RowIconDragTooltip"
                : source.WindowsLink != null ? "Phone.WindowsLinkRowIconDragTooltip" : "Phone.AirPlayDragFromMixer"), appName) };
        row.Icon.Content = new ContentControl { Content = row.IconItem, Width = 22, Height = 22,
            ContentTemplate = (DataTemplate)FindResource("AppIconTemplate") };
        row.Icon.Click += (_, _) => session.SetMute(!session.State.Muted);
        if (source.Android is { } android) AndroidAppDragController.Attach(row.Icon, () => new AndroidAppDrag(android.Device, android.App));
        else if (source.WindowsLink is { } winLink)
        {
            WindowsLinkAppDragController.Attach(row.Icon, () =>
            {
                var peer = WindowsLinkHub.Service?.Peers.FirstOrDefault(p => string.Equals(p.Id, winLink.PeerId, StringComparison.OrdinalIgnoreCase))
                    ?? new LinkPeer(winLink.PeerId, source.PhoneName, null, null, true, true, true,
                        LinkCapabilities.AudioReceive | LinkCapabilities.SystemAudio | LinkCapabilities.ProcessAudio
                        | LinkCapabilities.WindowReceive | LinkCapabilities.WindowCapture | LinkCapabilities.RemoteBrowse);
                var remoteApp = new LinkRemoteApp(winLink.AppKey, winLink.Name, winLink.AppKey, 0, true, true, 0, winLink.Name, winLink.IconPng);
                return new WindowsLinkAppDrag(peer, remoteApp);
            }, () => row.IconItem.Icon);
        }
        else AppIconDragController.AttachDesktop(row.Icon, row.IconItem, () =>
        {
            if (AirPlayHost.Find(source.Id) != null) AirPlayHost.ShowMirror();
        });
        System.Windows.Automation.AutomationProperties.SetName(row.Icon, string.Format(L10n.T("Phone.RowIconMuteAutomation"), appName));
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
        System.Windows.Automation.AutomationProperties.SetName(row.Expand, string.Format(L10n.T("Phone.RowExpandAutomation"), appName));
        DockPanel.SetDock(row.Expand, Dock.Right); dock.Children.Add(row.Expand);
        Slider volume = legacy ? new Slider() : new AppRowVolumeSlider(); row.Volume = volume;
        volume.Minimum = 0; volume.Maximum = 100; volume.Value = state.Volume; volume.Height = legacy ? 32 : 48;
        volume.VerticalAlignment = VerticalAlignment.Center; volume.Margin = new Thickness(0, 0, 6, 0);
        volume.Style = (Style)FindResource("RowSliderStyle"); volume.SetResourceReference(Slider.ForegroundProperty, "Theme.Accent");
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
        foreach (var row in _androidRows.Values)
        {
            row.Muted = muted;
            if (muted) row.PeakLevel = 0f;
            row.Volume.Foreground = muted ? ThemeService.GetInvertedAccentBrush() : (Brush)FindResource("Theme.Accent");
            row.Icon.Opacity = muted ? 0.45 : 1;
            UpdateAndroidPeakVisual(row);
        }
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
                else if (row.Source.WindowsLink is { } winLink)
                    await ((App)System.Windows.Application.Current).ReturnWindowsLinkToPeerAsync(winLink.PeerId, winLink.AppKey);
                else AirPlayHost.Disconnect(row.Source.Id);
            }
            catch (Exception ex) { if (!_isClosed) ShowOsd(L10n.T("Phone.AudioWindowTitle"), AndroidErrorTranslation.Format(ex)); }
        };
        AddRowDeviceGroup(row.Details, RowPlaybackDevices(row.IconItem.Info, ConfigService.Load()), EDataFlow.eRender, (button, device) =>
        {
            button.IsEnabled = !row.ChangingOutput;
            row.Devices.Add((button, device.Id));
            button.Click += async (_, _) => await SetAndroidOutputAsync(row, device);
        }, trailingAction: exit);
        UpdateAndroidRoute(row);
    }

    private static bool PhoneDeviceChoicesChanged(AndroidRow row, AndroidDevice device, AppConfig config)
    {
        int count = 0;
        foreach (var (_, id) in AndroidAudioHost.GetDropZonesForDevice(device))
        {
            if (config.HiddenOutputDevices.Contains(id)) continue;
            if (!row.Devices.Any(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))) return true;
            count++;
        }
        return count != row.Devices.Count(entry => ApplicationAudio.IsPhoneOutputDevice(entry.Id));
    }

    private async Task SetAndroidOutputAsync(AndroidRow row, AudioDeviceInfo device)
    {
        if (row.ChangingOutput) return;
        row.ChangingOutput = true;
        foreach (var entry in row.Devices) entry.Button.IsEnabled = false;
        try
        {
            var result = await ApplicationAudio.ApplyEndpointAsync(row.IconItem.Info, EDataFlow.eRender, device.Id);
            if (!result.Success && !_isClosed) ShowOsd(L10n.T("Phone.AudioWindowTitle"), result.Message);
        }
        catch (Exception ex) { if (!_isClosed) ShowOsd(L10n.T("Phone.AudioWindowTitle"), AndroidErrorTranslation.Format(ex)); }
        finally
        {
            row.ChangingOutput = false;
            if (!_isClosed)
            {
                UpdateAndroidRoute(row);
                foreach (var entry in row.Devices) entry.Button.IsEnabled = true;
            }
        }
    }
    private void UpdateAndroidRoute(AndroidRow row)
    {
        string id = row.Source.Session.State.OutputId ?? AudioService.SystemDefaultDeviceId;
        if (row.Name != null) row.Name.Text = row.ShowOutputName
            ? ShortName(_outputDisplay.FirstOrDefault(d => d.Id == id)?.DisplayName ?? L10n.T("Dev.SystemDefaultOut")) : row.IconItem.Label;
        foreach (var entry in row.Devices) SetRowDeviceSelection(entry.Button, entry.Id, id);
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

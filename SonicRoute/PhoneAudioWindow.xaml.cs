#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace SonicRoute;

public sealed class PhoneDeviceNameConverter : DependencyObject, System.Windows.Data.IMultiValueConverter
{
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
        nameof(Revision), typeof(int), typeof(PhoneDeviceNameConverter), new PropertyMetadata(0));
    public int Revision { get => (int)GetValue(RevisionProperty); set => SetCurrentValue(RevisionProperty, value); }
    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => values.Length > 0 && values[0] is AndroidDevice device ? AndroidAudioHost.Phone.GetDisplayName(device) : "";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PhoneAppItem : INotifyPropertyChanged
{
    public AndroidApp App { get; }
    private string? _deviceName;
    public string Name => App.IsDeviceAudio ? _deviceName ?? App.Name : App.Name;
    public string Package => App.Package;
    public string Initial => App.IsDeviceAudio ? "♫" : string.IsNullOrEmpty(Name) ? "♪" : Name[..1];
    private ImageSource? _icon;
    private bool _iconDecoded;
    public ImageSource? Icon
    {
        get
        {
            if (App.IsDeviceAudio) return PhoneSourceIcon.Image;
            if (!Volatile.Read(ref _iconDecoded))
            {
                _icon = AppIconService.DecodePng(App.IconPng);
                Volatile.Write(ref _iconDecoded, true);
            }
            return _icon;
        }
    }
    // Several popular apps leave ApplicationInfo.category unset. Use a small identity-based
    // fallback for the apps shown in the phone UI; declared Android categories take priority.
    private int EffectiveCategory => App.PlatformCategory >= 0 ? App.PlatformCategory : App.Package switch
    {
        "cn.wenyu.bodian" or "com.netease.cloudmusic" or "com.tencent.qqmusic" => 1,
        "tv.danmaku.bili" or "com.ss.android.ugc.aweme" => 2,
        "com.tencent.mm" or "com.tencent.mobileqq" => 4,
        "com.miHoYo.Yuanshen" => 0,
        _ => -1
    };
    public string Category => EffectiveCategory switch { 0 => "games", 1 or 2 or 3 => "media", 4 => "social", _ => App.Category };
    public string Description => App.IsDeviceAudio ? L10n.T("Phone.DeviceAudioDescription") : EffectiveCategory switch
    {
        1 => L10n.T("Phone.Cat.MediaMusic"), 2 => L10n.T("Phone.Cat.MediaVideo"), 3 => L10n.T("Phone.Cat.MediaImage"),
        _ => Category switch { "media" => L10n.T("Phone.Cat.Media"), "games" => L10n.T("Phone.Cat.GameAudio"), "social" => L10n.T("Phone.Cat.SocialChat"), _ => L10n.T("Phone.Cat.OtherApp") }
    };
    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        internal set { if (_isPlaying == value) return; _isPlaying = value; PropertyChanged?.Invoke(this, new(nameof(IsPlaying))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLanguage() => PropertyChanged?.Invoke(this, new(nameof(Description)));
    internal void UpdateDeviceName(AndroidDevice device)
    {
        string name = AndroidAudioHost.Phone.GetDisplayName(device);
        if (_deviceName == name) return;
        _deviceName = name;
        PropertyChanged?.Invoke(this, new(nameof(Name)));
    }
    public PhoneAppItem(AndroidApp app)
    {
        App = app;
    }
}

internal static class PhoneAudioWindowCache
{
    internal static AndroidDeviceDiscovery Discovery { get; set; } = new(token => AndroidAudioHost.Adb.DevicesAsync(token, deduplicate: false));
    internal static IReadOnlyList<AndroidDevice>? Devices => Discovery.Devices;
    internal static string? SelectedSerial;
    internal static Task<IReadOnlyList<AndroidDevice>> ReadDevicesAsync(CancellationToken token) => Discovery.ReadAsync(token);
    internal static void InvalidateDevices() => Discovery.Invalidate();
    internal static event Action? DevicesChanged { add => Discovery.Changed += value; remove => Discovery.Changed -= value; }
    internal static void SetDevices(IReadOnlyList<AndroidDevice> devices) => Discovery.Publish(devices);
}

public partial class PhoneAudioWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly WindowUiLifetime _mediaUiLifetime = new();
    private readonly PanelEntranceMotion _entranceMotion;
    private bool _entrancePrepared;
    private CancellationTokenSource? _visibleRead;
    private CancellationTokenSource? _catalogRead;
    private CancellationTokenSource? _mediaRead;
    private AndroidMediaSessionClient? _mediaClient;
    private AndroidDevice? _mediaDevice;
    private Task _mediaTask = Task.CompletedTask;
    private readonly bool _enableMedia;
    private AndroidMediaState? _mediaState;
    private AndroidMediaState? _progressState;
    private IReadOnlyList<PhoneAppItem>? _mediaApps;
    private readonly HashSet<string> _playingPackages = new(StringComparer.Ordinal);
    private readonly object _mediaUiGate = new();
    private AndroidMediaState? _pendingMediaState;
    private CancellationTokenSource? _pendingMediaRead;
    private bool _mediaUpdateQueued;
    private long _mediaPositionSecond = -1, _mediaDurationMs = -1;
    private byte[]? _artworkBytes;
    private bool _seeking;
    private string? _seekPackage, _seekTitle;
    private IReadOnlyList<PhoneAppItem> _apps = Array.Empty<PhoneAppItem>();
    private readonly PhoneAppItem _deviceAudioItem = new(AndroidApp.DeviceAudio);
    private IReadOnlyList<AndroidApp>? _retainedSnapshot;
    private string? _boundProfileId;
    private Point _dragStart;
    private PhoneAppItem? _dragItem;
    private bool _closed, _refreshing, _suppressDeviceChanged;
    private Border PcDropHighlight = null!;
    private Border _androidReturnDropZone = null!;
    private bool _androidReturnHovered;
    private Grid _pcDropGrid = null!;
    private sealed record PcDropCell(Border Border, string Name, string DeviceId, string Title, AndroidOutputSink Sink);
    private readonly List<PcDropCell> _pcDropCells = new();
    private string? _pcDropSignature;
    private int? _pcDropTargetIndex;
    private bool _pcDropZonesDirty = true;
    private string _connStatusKey = "Phone.Disconnected";
    private string? _connDetailKey;
    private Exception? _connError;
    private string? _connRawMessage;
    private bool _connKeptOldList;
    private AndroidConnectionQuality? _lastQuality;
    private bool _hasQuality;
    private bool _hasAppliedMediaDetails;
    private string? _mediaErrorKey;
    private Exception? _mediaErrorEx;

    public PhoneAudioWindow() : this(true) { }
    public PhoneAudioWindow(bool loadDevicesOnShow)
    {
        _enableMedia = loadDevicesOnShow;
        InitializeComponent();
        _entranceMotion = new PanelEntranceMotion(PhoneSurface, PhoneSlide);
        L10n.Instance.PropertyChanged += OnLanguageChanged;
        InitializePcDropOverlay();
        Loaded += PhoneAudioWindow_Loaded;
        if (loadDevicesOnShow) Loaded += async (_, _) => await InitializeDevicesAsync();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                PreparePhoneEntrance();
                if (IsLoaded) _entranceMotion.Play();
                _pcDropZonesDirty = true;
                _visibleRead?.Dispose(); _visibleRead = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                AndroidAudioHost.Phone.Changed += RetainedAppsChanged; AndroidAudioHost.Phone.DisplayNamesChanged += DeviceNamesChanged;
                AndroidAudioHost.Phone.OutputsChanged += PhoneOutputsChanged;
                PhoneAudioWindowCache.DevicesChanged += DevicesChanged;
                DeviceNamesChanged(); RetainedAppsChanged(); UpdateMediaSubscription();
                Android_ReturnDragStateChanged();
                if (_enableMedia && IsLoaded) _ = InitializeDevicesAsync();
            }
            else
            {
                _entranceMotion.Stop(); _entrancePrepared = false;
                AndroidAudioHost.Phone.Changed -= RetainedAppsChanged; AndroidAudioHost.Phone.DisplayNamesChanged -= DeviceNamesChanged;
                AndroidAudioHost.Phone.OutputsChanged -= PhoneOutputsChanged;
                PhoneAudioWindowCache.DevicesChanged -= DevicesChanged;
                _visibleRead?.Cancel(); _catalogRead?.Cancel(); StopMediaSubscription();
                ClearPcDropHighlight();
            }
        };
        Closed += (_, _) =>
        {
            _entranceMotion.Stop(); _entrancePrepared = false;
            _closed = true; L10n.Instance.PropertyChanged -= OnLanguageChanged; _mediaUiLifetime.Dispose(); _lifetime.Cancel(); _catalogRead?.Cancel(); StopMediaSubscription();
            ClearPcDropHighlight();
            AppIconDragController.DesktopDragStateChanged -= Pc_DragStateChanged;
            AndroidAudioHost.DragStateChanged -= Android_ReturnDragStateChanged;
            if (AndroidAudioHost.IsPhoneCreated)
            {
                AndroidAudioHost.Phone.Changed -= RetainedAppsChanged;
                AndroidAudioHost.Phone.DisplayNamesChanged -= DeviceNamesChanged;
                AndroidAudioHost.Phone.OutputsChanged -= PhoneOutputsChanged;
            }
            PhoneAudioWindowCache.DevicesChanged -= DevicesChanged;
            MediaPositionSlider.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            _apps = Array.Empty<PhoneAppItem>(); PhoneApps.ItemsSource = null;
            _retainedSnapshot = null; _dragItem = null; _mediaState = null; _progressState = null; _mediaApps = null; _artworkBytes = null;
            _mediaErrorEx = null; _connError = null;
            _playingPackages.Clear();
            foreach (var cell in _pcDropCells)
            {
                try { UnregisterName(cell.Name); } catch { }
            }
            _pcDropCells.Clear();
            _pcDropSignature = null;
            _pcDropGrid?.Children.Clear();
            _pcDropGrid?.RowDefinitions.Clear();
            MediaArtwork.Source = MediaAppIcon.Source = null;
            _suppressDeviceChanged = true; DeviceCombo.ItemsSource = null;
            ((App)System.Windows.Application.Current).ReleaseClosedUiWindow(this);
        };
    }

    private void PhoneAudioWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!IsVisible || _closed) return;
        PreparePhoneEntrance();
        _entranceMotion.Play();
    }

    private void PreparePhoneEntrance()
    {
        if (!IsVisible || _closed || _entrancePrepared) return;
        _entranceMotion.Prepare();
        _entrancePrepared = true;
    }

    private void InitializePcDropOverlay()
    {
        _pcDropGrid = new Grid { Name = "PcDropZonesGrid" };
        _androidReturnDropZone = CreateDropCell("AndroidReturnDropZone", L10n.T("Phone.ReturnToPhoneTitle"), L10n.T("Phone.ReturnToPhoneDesc"), new CornerRadius(6));
        _androidReturnDropZone.Visibility = Visibility.Collapsed;
        var modes = new Grid();
        modes.Children.Add(_pcDropGrid);
        modes.Children.Add(_androidReturnDropZone);
        PcDropHighlight = new Border
        {
            Name = "PcDropHighlight",
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = modes
        };
        PcDropHighlight.SetResourceReference(Border.BorderBrushProperty, "Theme.Accent");
        PcDropHighlight.SetResourceReference(BackgroundProperty, "Theme.SurfaceBg");
        var host = (Grid)PhoneApps.Parent;
        Grid.SetRow(PcDropHighlight, Grid.GetRow(PhoneApps));
        host.Children.Insert(host.Children.IndexOf(PhoneEmptyPanel) + 1, PcDropHighlight);
        RegisterName(PcDropHighlight.Name, PcDropHighlight);
        RegisterName(_pcDropGrid.Name, _pcDropGrid);
        RegisterName(_androidReturnDropZone.Name, _androidReturnDropZone);

        var style = new Style(typeof(ListBox), PhoneApps.Style);
        var trigger = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(Visibility)) { Source = PcDropHighlight },
            Value = Visibility.Visible
        };
        trigger.Setters.Add(new Setter(VisibilityProperty, Visibility.Hidden));
        style.Triggers.Add(trigger);
        PhoneApps.Style = style;

        AppIconDragController.DesktopDragStateChanged += Pc_DragStateChanged;
        AndroidAudioHost.DragStateChanged += Android_ReturnDragStateChanged;
    }

    internal void EnsurePcDropZones(bool force = false)
    {
        // Device/profile/output events invalidate this snapshot. DragOver must only read
        // already-built cells, rather than allocate device IDs/signatures on every move.
        if (_closed || (!force && !_pcDropZonesDirty)) return;
        _pcDropZonesDirty = false;
        var device = DeviceCombo.SelectedItem as AndroidDevice
            ?? (AndroidAudioHost.IsPhoneCreated ? AndroidAudioHost.Phone.Profile.LastDevice : null);
        var zones = AndroidAudioHost.GetDropZonesForDevice(device);
        if (zones.Count == 0)
            zones = [(AndroidOutputSink.DefaultSpeaker, "")];

        var sigBuilder = new StringBuilder();
        sigBuilder.Append(device == null ? "" : AndroidDeviceIdentity.Key(device)).Append('|')
            .Append(L10n.CurrentLanguage).Append('|')
            .Append(AndroidAudioHost.OutputDisplayName(device, AndroidOutputSink.DefaultSpeaker)).Append('|');
        foreach (var (sink, deviceId) in zones)
            sigBuilder.Append(sink.Key).Append(':').Append(sink.Name).Append(':').Append(deviceId).Append(';');
        string signature = sigBuilder.ToString();
        if (_pcDropSignature == signature && _pcDropCells.Count == zones.Count) return;
        _pcDropSignature = signature;

        foreach (var old in _pcDropCells)
        {
            try { UnregisterName(old.Name); } catch { }
        }
        _pcDropCells.Clear();
        _pcDropGrid.Children.Clear();
        _pcDropGrid.RowDefinitions.Clear();

        for (int i = 0; i < zones.Count; i++)
        {
            _pcDropGrid.RowDefinitions.Add(new RowDefinition());
            var (sink, deviceId) = zones[i];
            string cellName = i == 0 ? "PcSpeakerDropZone" : $"PcBluetoothDropZone_{i}";
            string sinkName = sink.Name == "蓝牙音频设备" ? L10n.T("Phone.BluetoothAudioDevice") : sink.Name;
            string targetName = sink.Kind == AndroidOutputSinkKind.Speaker ? AndroidAudioHost.OutputDisplayName(device, sink) : sinkName;
            string title = sink.Kind == AndroidOutputSinkKind.Speaker
                ? string.Format(L10n.T("Phone.PcDropOnlyOnSpeakerTitle"), targetName)
                : string.Format(L10n.T("Phone.PcDropOnlyOnTargetTitle"), targetName);

            string description = sink.Kind == AndroidOutputSinkKind.Speaker
                ? string.Format(L10n.T("Phone.PcDropOnlyOnSpeakerDesc"), AndroidAudioHost.OutputDisplayName(device, sink))
                : L10n.T("Phone.PcDropOnlyOnBtDesc");
            var corners = zones.Count == 1
                ? new CornerRadius(6)
                : i == 0
                    ? new CornerRadius(6, 6, 0, 0)
                    : i == zones.Count - 1
                        ? new CornerRadius(0, 0, 6, 6)
                        : new CornerRadius(0);

            var border = CreateDropCell(cellName, title, description, corners);
            border.Tag = deviceId;
            RegisterName(cellName, border);
            Grid.SetRow(border, i);
            _pcDropGrid.Children.Add(border);
            _pcDropCells.Add(new PcDropCell(border, cellName, deviceId, title, sink));

            if (i < zones.Count - 1)
            {
                var divider = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
                divider.SetResourceReference(BackgroundProperty, "Theme.Accent");
                Grid.SetRow(divider, i);
                _pcDropGrid.Children.Add(divider);
            }
        }
        ApplyPcDropCellBackgrounds(_pcDropTargetIndex);
    }

    private static void UpdateDropCellText(Border? cell, string title, string description)
    {
        if (cell?.Child is StackPanel { Children.Count: >= 2 } labels
            && labels.Children[0] is TextBlock heading && labels.Children[1] is TextBlock detail)
        {
            heading.Text = title;
            detail.Text = description;
            System.Windows.Automation.AutomationProperties.SetName(cell, title);
        }
    }
    private static Border CreateDropCell(string name, string title, string description, CornerRadius corners)
    {
        var labels = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextPrimary");
        var detail = new TextBlock { Text = description, FontSize = 11, Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
        labels.Children.Add(heading); labels.Children.Add(detail);
        var cell = new Border { Name = name, CornerRadius = corners, Child = labels };
        System.Windows.Automation.AutomationProperties.SetName(cell, title);
        return cell;
    }

    private void ApplyPcDropCellBackgrounds(int? activeIndex)
    {
        for (int i = 0; i < _pcDropCells.Count; i++)
        {
            _pcDropCells[i].Border.SetResourceReference(BackgroundProperty,
                activeIndex == i ? "Theme.Overlay" : "Theme.SurfaceBg");
        }
    }

    private void SetPcDropTarget(int? index)
    {
        if (AndroidAudioHost.IsDragging) { SetAndroidReturnTarget(false); return; }
        _androidReturnDropZone.Visibility = Visibility.Collapsed;
        _pcDropGrid.Visibility = Visibility.Visible;
        bool visible = index != null || AppIconDragController.IsDesktopDragging;
        if (visible) EnsurePcDropZones();
        if (_pcDropTargetIndex == index && (PcDropHighlight.Visibility == Visibility.Visible) == visible) return;
        _pcDropTargetIndex = index;
        if (!visible)
        {
            PcDropHighlight.Visibility = Visibility.Collapsed;
            return;
        }
        ApplyPcDropCellBackgrounds(index);
        PcDropHighlight.Visibility = Visibility.Visible;
    }

    internal void ClearPcDropHighlight() => SetPcDropTarget(null);

    private void Android_ReturnDragStateChanged()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { _mediaUiLifetime.Post(Dispatcher, Android_ReturnDragStateChanged); return; }
        SetAndroidReturnTarget(false);
        if (!AndroidAudioHost.IsDragging) SetPcDropTarget(null);
    }

    private void SetAndroidReturnTarget(bool hovered)
    {
        bool visible = hovered || AndroidAudioHost.IsDragging;
        if (_androidReturnHovered == hovered && (_androidReturnDropZone.Visibility == Visibility.Visible) == visible) return;
        _androidReturnHovered = hovered;
        _androidReturnDropZone.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) { PcDropHighlight.Visibility = Visibility.Collapsed; return; }
        _pcDropGrid.Visibility = Visibility.Collapsed;
        _androidReturnDropZone.SetResourceReference(BackgroundProperty, hovered ? "Theme.Overlay" : "Theme.SurfaceBg");
        PcDropHighlight.Visibility = Visibility.Visible;
    }

    private static int GetPcDropZoneIndex(double y, double height, int count)
    {
        if (count <= 1 || height <= 0) return 0;
        int index = (int)Math.Floor(y / (height / count));
        return Math.Clamp(index, 0, count - 1);
    }

    private void Pc_DragStateChanged()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Pc_DragStateChanged)); return; }
        if (!IsVisible) return;
        bool watchingOutputs = _mediaRead is { IsCancellationRequested: false } && !_mediaTask.IsCompleted;
        if (AppIconDragController.IsDesktopDragging && !watchingOutputs
            && DeviceCombo.SelectedItem is AndroidDevice live && live.State == "device")
            _ = Task.Run(() => AndroidAudioHost.ProbeOutputsIfConnectedAsync(new[] { live }, force: false, _lifetime.Token));
        SetPcDropTarget(null);
    }

    private void PhoneOutputsChanged()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(PhoneOutputsChanged)); return; }
        _pcDropZonesDirty = true;
        if (PcDropHighlight.Visibility == Visibility.Visible || _pcDropCells.Count > 0)
            EnsurePcDropZones();
    }

    private async Task InitializeDevicesAsync()
    {
        if (PhoneAudioWindowCache.Devices is { } cachedDevices)
        {
            BindDevices(cachedDevices);
            await ShowSelectedDeviceAsync(forceRefresh: false);
            if (_enableMedia && !_closed && IsVisible && cachedDevices.All(d => d.State != "device"))
            {
                PhoneAudioWindowCache.InvalidateDevices();
                if (await RefreshDevicesAsync()) await ShowSelectedDeviceAsync(forceRefresh: false);
            }
            return;
        }
        if (await RefreshDevicesAsync()) await ShowSelectedDeviceAsync(forceRefresh: false);
    }
    private async Task<bool> RefreshDevicesAsync()
    {
        if (_closed || _refreshing) return false;
        _refreshing = true; RefreshButton.IsEnabled = false;
        SetConnectionStatusKey("Phone.StatusLoading");
        try
        {
            string? previous = (DeviceCombo.SelectedItem as AndroidDevice)?.Serial ?? PhoneAudioWindowCache.SelectedSerial;
            var devices = await PhoneAudioWindowCache.ReadDevicesAsync(_visibleRead?.Token ?? _lifetime.Token);
            if (_closed) return false;
            BindDevices(PhoneAudioWindowCache.Devices ?? devices, previous);
            if (DeviceCombo.SelectedItem == null) SetConnectionStatusKey("Phone.Disconnected", "Phone.DetailNoAuthorizedPhone");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { if (!_closed) SetConnectionStatusError("Phone.Disconnected", ex); return false; }
        finally { _refreshing = false; if (!_closed) RefreshButton.IsEnabled = true; }
    }
    private void BindDevices(IReadOnlyList<AndroidDevice> devices, string? preferredSerial = null)
    {
        _pcDropZonesDirty = true;
        _suppressDeviceChanged = true;
        try
        {
            var store = AndroidAudioHost.Phone;
            store.ObserveDevices(devices);
            var profile = store.Profile; _boundProfileId = profile.Id;
            var choices = AndroidDeviceIdentity.Distinct(devices.Where(d => !store.IsRemoved(d)).Concat(store.Profiles.Where(p => p.LastDevice != null)
                .Select(p => p.LastDevice! with { State = "offline" })).ToArray());
            if (DeviceCombo.ItemsSource is not IReadOnlyList<AndroidDevice> previous || !previous.SequenceEqual(choices))
                DeviceCombo.ItemsSource = choices;
            var currentChoices = (IReadOnlyList<AndroidDevice>)DeviceCombo.ItemsSource;
            DeviceCombo.SelectedItem = currentChoices.FirstOrDefault(d => AndroidDeviceIdentity.Matches(d, profile.LastDevice));
            PhoneAudioWindowCache.SelectedSerial = (DeviceCombo.SelectedItem as AndroidDevice)?.Serial;
            if (DeviceCombo.SelectedItem == null)
            { _apps = Array.Empty<PhoneAppItem>(); _retainedSnapshot = null; ApplyFilter(); SetConnectionStatusKey("Phone.Disconnected"); }
            if (PcDropHighlight.Visibility == Visibility.Visible || _pcDropCells.Count > 0)
                EnsurePcDropZones(force: true);
        }
        finally { _suppressDeviceChanged = false; }
    }
    private async void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceChanged) return;
        _pcDropZonesDirty = true;
        if (DeviceCombo.SelectedItem is AndroidDevice selected)
        {
            PhoneAudioWindowCache.SelectedSerial = selected.Serial;
            var profile = AndroidAudioHost.Phone.FindProfile(selected);
            if (profile != null) { _boundProfileId = profile.Id; AndroidAudioHost.Phone.SelectProfile(profile.Id); }
            else AndroidAudioHost.Phone.RememberDevice(selected);
            _boundProfileId = AndroidAudioHost.Phone.Profile.Id;
        }
        if (PcDropHighlight.Visibility == Visibility.Visible || _pcDropCells.Count > 0)
            EnsurePcDropZones(force: true);
        await ShowSelectedDeviceAsync(forceRefresh: false);
    }
    private async Task ShowSelectedDeviceAsync(bool forceRefresh)
    {
        _catalogRead?.Cancel();
        UpdateMediaSubscription();
        if (DeviceCombo.SelectedItem is not AndroidDevice device)
        { _apps = Array.Empty<PhoneAppItem>(); _retainedSnapshot = null; ApplyFilter(); SetConnectionStatusKey("Phone.Disconnected"); return; }
        ConnectionSignal.Visibility = device.IsWifi ? Visibility.Visible : Visibility.Collapsed;
        ConnectionUsbIcon.Visibility = device.IsWifi ? Visibility.Collapsed : Visibility.Visible;
        RetainedAppsChanged();
        if (device.State == "device" && (!_enableMedia || forceRefresh || !IsVisible))
            _ = AndroidAudioHost.ProbeOutputsIfConnectedAsync(new[] { device }, force: forceRefresh, _lifetime.Token);
        if (!forceRefresh)
        {
            SetConnectionStatusKey(device.State == "device" ? "Phone.ConnectedStatus" : "Phone.Disconnected",
                device.State == "device" ? null : "Phone.DetailOfflineKeptApps");

            return;
        }
        if (device.State != "device")
        { SetConnectionStatusKey("Phone.Disconnected", "Phone.DetailOfflineConfirmAuth"); return; }
        await LoadCatalogAsync(device);
    }
    private async Task LoadCatalogAsync(AndroidDevice device)
    {
        _catalogRead?.Cancel();
        using var read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _catalogRead = read;
        SetConnectionStatusKey("Phone.StatusLoading", "Phone.DetailLoadingAppsAndIcons");
        try
        {
            var packages = AndroidAudioHost.Phone.Profile.QuickPanelApps.Select(a => a.Package).ToArray();
            var apps = await AndroidAudioHost.Adb.AppsAsync(device, read.Token, packages);
            if (_closed || read.IsCancellationRequested || !ReferenceEquals(DeviceCombo.SelectedItem, device)) return;
            AndroidAudioHost.Phone.UpdateMetadata(device, apps);
            RetainedAppsChanged();
            SetConnectionStatusKey("Phone.ConnectedStatus");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed && !read.IsCancellationRequested)
                SetConnectionStatusError("Phone.StatusLoadFailed", ex, _apps.Count > 0);
        }
        finally { if (ReferenceEquals(_catalogRead, read)) _catalogRead = null; }
    }
    private static IReadOnlyList<PhoneAppItem> CreateItems(IReadOnlyList<AndroidApp> apps, CancellationToken token)
    {
        var items = new List<PhoneAppItem>(apps.Count);
        foreach (var app in apps)
        {
            token.ThrowIfCancellationRequested();
            items.Add(new PhoneAppItem(app));
        }
        return items;
    }
    private void ApplyFilter()
    {
        if (PhoneApps == null || SearchBox == null) return;
        string query = SearchBox.Text.Trim();
        var filtered = _apps.Where(a => a.App.IsDeviceAudio || query.Length == 0 || a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || a.Package.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.App.IsDeviceAudio).ThenByDescending(a => a.IsPlaying).ToArray();
        if (PhoneApps.ItemsSource is not IReadOnlyList<PhoneAppItem> previous || !previous.SequenceEqual(filtered))
            PhoneApps.ItemsSource = filtered;
        PhoneEmptyPanel.Visibility = _apps.Count == 0 && PcDropHighlight?.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RetainedAppsChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RetainedAppsChanged)); return; }
        if (_closed) return;
        var profile = AndroidAudioHost.Phone.Profile;
        if (_boundProfileId != profile.Id && IsVisible && !_suppressDeviceChanged)
        {
            BindDevices(PhoneAudioWindowCache.Devices ?? Array.Empty<AndroidDevice>());
            _ = ShowSelectedDeviceAsync(false);
        }
        if (DeviceCombo.SelectedItem is not AndroidDevice device)
        { _apps = Array.Empty<PhoneAppItem>(); _retainedSnapshot = null; ApplyFilter(); return; }
        _deviceAudioItem.UpdateDeviceName(device);
        if (PcDropHighlight.Visibility == Visibility.Visible || _pcDropCells.Count > 0)
            EnsurePcDropZones();
        var snapshot = profile.QuickPanelApps;
        if (ReferenceEquals(_retainedSnapshot, snapshot)) return;
        _retainedSnapshot = snapshot;
        if (_apps.Count == 0) _apps = new[] { _deviceAudioItem }.Concat(CreateItems(snapshot, _lifetime.Token)).ToArray();
        else
        {
            var existing = _apps.ToDictionary(a => a.Package, StringComparer.Ordinal);
            _apps = new[] { _deviceAudioItem }.Concat(snapshot.Select(a => existing.TryGetValue(a.Package, out var item) && item.App == a ? item : new PhoneAppItem(a))).ToArray();
        }
        ApplyFilter(); ApplyMediaState(_mediaState);
    }
    private void DeviceNamesChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(DeviceNamesChanged)); return; }
        if (_closed) return;
        var names = (PhoneDeviceNameConverter)Resources["PhoneDeviceName"];
        names.Revision = unchecked(names.Revision + 1);
        if (DeviceCombo.SelectedItem is AndroidDevice device) _deviceAudioItem.UpdateDeviceName(device);
    }
    private async void DevicesChanged()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(DevicesChanged)); return; }
        if (_closed || _refreshing) return;
        BindDevices(PhoneAudioWindowCache.Devices ?? Array.Empty<AndroidDevice>());
        await ShowSelectedDeviceAsync(false);
    }
    private void ManageApps_Click(object sender, RoutedEventArgs e)
    { if (System.Windows.Application.Current is App app) app.ShowPhoneManagement(); }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    { if (SearchHint != null) SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed; ApplyFilter(); }
    private void SetConnectionStatusKey(string statusKey, string? detailKey = null)
    {
        _connStatusKey = statusKey; _connDetailKey = detailKey; _connError = null; _connRawMessage = null; _connKeptOldList = false;
        UpdateConnectionStatusText();
    }
    private void SetConnectionStatusError(string statusKey, Exception ex, bool keptOldList = false)
    {
        _connStatusKey = statusKey; _connDetailKey = null; _connError = ex; _connRawMessage = null; _connKeptOldList = keptOldList;
        UpdateConnectionStatusText();
    }
    private void SetConnectionStatusMessage(string statusKey, string message)
    {
        _connStatusKey = statusKey; _connDetailKey = null; _connError = null; _connRawMessage = message; _connKeptOldList = false;
        UpdateConnectionStatusText();
    }
    private void UpdateConnectionStatusText()
    {
        ConnectionText.Text = L10n.T(_connStatusKey);
        ConnectionText.ToolTip = _connError != null
            ? (_connKeptOldList ? L10n.T("Phone.DetailKeptOldListPrefix") : "") + AndroidErrorTranslation.Format(_connError)
            : _connRawMessage
                ?? (_connDetailKey != null ? L10n.T(_connDetailKey) : null);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _catalogRead?.Cancel();
        if (!await RefreshDevicesAsync()) return;
        await ShowSelectedDeviceAsync(forceRefresh: true);
    }
    private void Apps_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(PhoneApps);
        _dragItem = (ItemsControl.ContainerFromElement(PhoneApps, e.OriginalSource as DependencyObject) as ListBoxItem)?.Content as PhoneAppItem;
    }
    private void Apps_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null || AndroidAudioHost.IsDragging) return;
        Point point = e.GetPosition(PhoneApps);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (DeviceCombo.SelectedItem is not AndroidDevice device) return;
        var payload = new AndroidAppDrag(device, _dragItem.App);
        try { AndroidAppDragController.Run(PhoneApps, payload); }
        finally { _dragItem = null; }
    }
    private void Apps_QueryContinueDrag(object sender, System.Windows.QueryContinueDragEventArgs e)
    { if (e.EscapePressed) { e.Action = System.Windows.DragAction.Cancel; e.Handled = true; } }
    private void PhoneApp_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(AndroidAppDrag.Format, false) && e.Data.GetData(AndroidAppDrag.Format, false) is AndroidAppDrag)
        {
            SetAndroidReturnTarget(true);
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
            return;
        }
        if (e.Data.GetDataPresent(AppIconDragController.DesktopFormat, false)
            && e.Data.GetData(AppIconDragController.DesktopFormat, false) is AudioAppInfo)
        {
            EnsurePcDropZones();
            double height = PcDropHighlight.ActualHeight > 0 ? PcDropHighlight.ActualHeight : Math.Max(1, PhoneApps.ActualHeight);
            int index = GetPcDropZoneIndex(e.GetPosition(PcDropHighlight).Y, height, _pcDropCells.Count);
            SetPcDropTarget(index);
            e.Effects = System.Windows.DragDropEffects.Copy;
            e.Handled = true;
            return;
        }
        SetPcDropTarget(null);
        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }
    private void PhoneApp_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        var point = e.GetPosition(PhoneApps);
        if (AppIconDragController.IsDesktopDragging || AndroidAudioHost.IsDragging)
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            point = PhoneApps.PointFromScreen(new Point(cursor.X, cursor.Y));
        }
        if (!new Rect(0, 0, PhoneApps.ActualWidth, PhoneApps.ActualHeight).Contains(point))
            ClearPcDropHighlight();
    }
    private async void PhoneApp_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(AndroidAppDrag.Format, false) && e.Data.GetData(AndroidAppDrag.Format, false) is AndroidAppDrag payload)
        {
            ClearPcDropHighlight();
            e.Handled = true;
            e.Effects = System.Windows.DragDropEffects.Move;
            try { await ((App)System.Windows.Application.Current).ReturnAndroidToPhoneAsync(payload); }
            catch (Exception ex) { if (!_closed) SetConnectionStatusError("Phone.StatusIncomplete", ex); }
            return;
        }
        if (e.Data.GetDataPresent(AppIconDragController.DesktopFormat, false)
            && e.Data.GetData(AppIconDragController.DesktopFormat, false) is AudioAppInfo pcApp)
        {
            EnsurePcDropZones();
            double height = PcDropHighlight.ActualHeight > 0 ? PcDropHighlight.ActualHeight : Math.Max(1, PhoneApps.ActualHeight);
            int index = GetPcDropZoneIndex(e.GetPosition(PcDropHighlight).Y, height, _pcDropCells.Count);
            var cell = _pcDropCells.Count > 0 ? _pcDropCells[index] : null;
            ClearPcDropHighlight();
            e.Handled = true;
            if (cell == null || string.IsNullOrWhiteSpace(cell.DeviceId))
            {
                e.Effects = System.Windows.DragDropEffects.None;
                if (!_closed) SetConnectionStatusKey("Phone.Disconnected", "Phone.DetailConnectPhoneFirst");
                return;
            }
            e.Effects = System.Windows.DragDropEffects.Copy;
            try
            {
                var result = await Task.Run(() => ApplicationAudio.ApplyEndpoint(pcApp, EDataFlow.eRender, cell.DeviceId));
                if (!_closed)
                {
                    if (result.Success)
                        ((App)System.Windows.Application.Current).ShowOsd(AppDisplayName.Get(pcApp), "🔊 " + cell.Title);
                    else
                        SetConnectionStatusMessage("Phone.StatusIncomplete", result.Message);
                }
            }
            catch (Exception ex) { if (!_closed) SetConnectionStatusError("Phone.StatusIncomplete", ex); }
            return;
        }
        ClearPcDropHighlight();
        e.Handled = true;
        e.Effects = System.Windows.DragDropEffects.None;
    }
    private void Apps_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _dragItem = null; return; }
        if (e.Key == Key.Enter && PhoneApps.SelectedItem is PhoneAppItem item && DeviceCombo.SelectedItem is AndroidDevice device)
        { e.Handled = true; _ = StartFromKeyboardAsync(device, item.App); }
    }
    private async Task StartFromKeyboardAsync(AndroidDevice device, AndroidApp app)
    {
        try { await AndroidAudioHost.AddAudioAsync(device, app); }
        catch (Exception ex) { if (!_closed) SetConnectionStatusError("Phone.StatusOperationFailed", ex); }
    }
    private void UpdateMediaSubscription()
    {
        if (!_enableMedia || !IsVisible || _closed) return;
        var device = DeviceCombo.SelectedItem as AndroidDevice;
        if (device?.State == "device" && _mediaRead is { IsCancellationRequested: false }
            && !_mediaTask.IsCompleted && _mediaDevice?.Serial == device.Serial && _mediaDevice.TransportId == device.TransportId)
            return; // device/profile notifications must not restart the same live helper
        _mediaRead?.Cancel();
        var previous = _mediaTask;
        var read = _mediaRead = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _mediaDevice = device;
        ApplyMediaState(null);
        ApplyConnectionQuality(null);
        if (device?.State != "device") { read.Dispose(); _mediaRead = null; return; }
        _mediaTask = WatchMediaAsync(device, previous, read);
    }
    private void StopMediaSubscription()
    {
        _mediaRead?.Cancel(); _mediaDevice = null; _mediaClient = null;
        // Retain cached app rows/metadata for fast reopen, not the last track's artwork.
        _mediaState = _progressState = null; _mediaApps = null; _artworkBytes = null;
        MediaArtwork.Source = MediaAppIcon.Source = null;
        lock (_mediaUiGate) { _pendingMediaState = null; _pendingMediaRead = null; }
        MediaPositionSlider.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
    }
    private void QueueMediaState(AndroidMediaState state, CancellationTokenSource read)
    {
        lock (_mediaUiGate)
        {
            if (_closed || read.IsCancellationRequested || !ReferenceEquals(_mediaRead, read)) return;
            _pendingMediaState = state; _pendingMediaRead = read;
            if (_mediaUpdateQueued) return;
            _mediaUpdateQueued = true;
        }
        // A callback burst can arrive while the input/render thread is occupied by a
        // drag. Keep its latest snapshot, with at most one pending UI operation.
        _mediaUiLifetime.Post(Dispatcher, ApplyPendingMediaState);
    }
    private void ApplyPendingMediaState()
    {
        AndroidMediaState? state; CancellationTokenSource? read;
        lock (_mediaUiGate)
        {
            state = _pendingMediaState; read = _pendingMediaRead;
            _pendingMediaState = null; _pendingMediaRead = null; _mediaUpdateQueued = false;
        }
        if (!_closed && state != null && read is { IsCancellationRequested: false } && ReferenceEquals(_mediaRead, read))
            ApplyMediaState(state);
    }
    private async Task WatchMediaAsync(AndroidDevice device, Task previous, CancellationTokenSource read)
    {
        try
        {
            await previous;
            read.Token.ThrowIfCancellationRequested();
            var client = _mediaClient = new AndroidMediaSessionClient(AndroidAudioHost.Adb);
            client.ConnectionQualityChanged += quality => _mediaUiLifetime.Post(Dispatcher, () =>
            {
                if (!_closed && !read.IsCancellationRequested && ReferenceEquals(_mediaRead, read)) ApplyConnectionQuality(quality);
            });
            await AndroidAudioHost.WatchMediaAsync(client, device, state => QueueMediaState(state, read), read.Token);
            if (_enableMedia && !_closed && !read.IsCancellationRequested)
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_closed && IsVisible && _enableMedia)
                    {
                        PhoneAudioWindowCache.InvalidateDevices();
                        _ = RefreshDevicesAsync();
                    }
                }));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed && !read.IsCancellationRequested && ReferenceEquals(_mediaRead, read))
            {
                ApplyMediaState(null);
                ApplyConnectionQuality(new(null));
                SetMediaError("Phone.MediaControlUnavailable", ex);

                if (_enableMedia && IsVisible)
                {
                    PhoneAudioWindowCache.InvalidateDevices();
                    _ = RefreshDevicesAsync();
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_mediaRead, read)) { _mediaRead = null; _mediaClient = null; _mediaDevice = null; }
            read.Dispose();
        }
    }
    private void ApplyConnectionQuality(AndroidConnectionQuality? quality)
    {
        _lastQuality = quality;
        _hasQuality = true;
        int strength = quality?.SignalLevel ?? 0;
        SignalDot.Opacity = strength >= 1 ? 1 : 0.2;
        SignalNear.Opacity = strength >= 2 ? 1 : 0.2;
        SignalOuter.Opacity = strength >= 4 ? 1 : strength == 3 ? 0.6 : 0.2;
        RefreshConnectionQualityText();
    }
    private void RefreshConnectionQualityText()
    {
        var quality = _lastQuality;
        string text = quality == null ? L10n.T("Phone.MeasuringLatency") : quality.RoundTripMs is { } ms
            ? string.Format(L10n.T("Phone.LatencyRoundTripFormat"), ms) : L10n.T("Phone.LatencyTimeout");
        ConnectionSignal.ToolTip = text;
        System.Windows.Automation.AutomationProperties.SetName(ConnectionSignal, text);
    }
    private void ApplyMediaState(AndroidMediaState? state)
    {
        var previous = _mediaState;
        bool sameApps = ReferenceEquals(_mediaApps, _apps);
        if (sameApps && ReferenceEquals(previous, state)) return;
        _mediaState = state;
        if (!sameApps || previous?.Playing != state?.Playing || previous?.Package != state?.Package
            || !SamePlayingPackages(previous?.PlayingPackages, state?.PlayingPackages))
        {
            _playingPackages.Clear();
            if (state != null) foreach (var package in state.PlayingPackages) _playingPackages.Add(package);
            if (state?.Playing == true) _playingPackages.Add(state.Package);
            bool playbackChanged = false;
            foreach (var item in _apps)
            {
                bool isPlaying = item.App.IsDeviceAudio ? _playingPackages.Count > 0 : _playingPackages.Contains(item.Package);
                playbackChanged |= item.IsPlaying != isPlaying;
                item.IsPlaying = isPlaying;
            }
            if (playbackChanged || PhoneApps.ItemsSource == null) ApplyFilter();
        }
        _mediaApps = _apps;
        byte[]? artwork = state?.ArtworkPng;
        bool artworkChanged = !ReferenceEquals(artwork, _artworkBytes)
            && !(artwork != null && _artworkBytes != null && artwork.AsSpan().SequenceEqual(_artworkBytes));
        _artworkBytes = artwork;
        bool detailsChanged = !sameApps || artworkChanged || previous?.Package != state?.Package
            || previous?.Title != state?.Title || previous?.Artist != state?.Artist
            || previous?.Playing != state?.Playing || previous?.Actions != state?.Actions;
        if (detailsChanged) ApplyMediaDetails(state, artwork, artworkChanged);
        UpdateMediaProgress(state);
    }
    private static bool SamePlayingPackages(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
        return true;
    }
    private void ApplyMediaDetails(AndroidMediaState? state, byte[]? artwork, bool artworkChanged)
    {
        _mediaState = state;
        _hasAppliedMediaDetails = true;
        _mediaErrorKey = null;
        _mediaErrorEx = null;
        var app = state == null ? null : _apps.FirstOrDefault(a => a.Package == state.Package);
        string? appName = app?.Name;
        if (artworkChanged)
        {
            MediaArtwork.Source = null;
            if (artwork is { Length: > 0 })
            {
                try
                {
                    MediaArtwork.Source = DecodeBoundedArtwork(artwork);
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
                { System.Diagnostics.Trace.WriteLine("Phone media artwork: " + ex.Message); }
            }
        }
        if (artwork is not { Length: > 0 } || MediaArtwork.Source == null) MediaArtwork.Source = app?.Icon;
        MediaArtworkPlaceholder.Visibility = MediaArtwork.Source == null ? Visibility.Visible : Visibility.Collapsed;
        MediaAppIcon.Source = app?.Icon;
        MediaAppBadgeFrame.Visibility = artwork is { Length: > 0 } && app?.Icon != null ? Visibility.Visible : Visibility.Collapsed;
        MediaTitle.Text = state?.HasSession == true ? (state.Title.Length > 0 ? state.Title : appName ?? state.Package) : L10n.T("Phone.MediaDefaultTitle");
        MediaSubtitle.Text = state?.HasSession == true
            ? string.Join(" · ", new[] { state.Artist.Length > 0 ? state.Artist : state.Playing ? L10n.T("Phone.MediaPlaying") : L10n.T("Phone.MediaPaused"), appName }
                .Where(value => !string.IsNullOrEmpty(value))) : L10n.T("Phone.MediaNonePlaying");

        MediaSubtitle.ToolTip = null;
        MediaPlayPausePath.Data = state?.Playing == true ? AutomationIcons.Pause : AutomationIcons.Play;
        System.Windows.Automation.AutomationProperties.SetName(MediaPlayPauseButton, state?.Playing == true ? L10n.T("Phone.MediaPause") : L10n.T("Phone.MediaPlay"));
        MediaPreviousButton.IsEnabled = state?.CanPrevious == true;
        MediaPlayPauseButton.IsEnabled = state?.CanToggle == true;
        MediaNextButton.IsEnabled = state?.CanNext == true;
    }
    private static BitmapSource? DecodeBoundedArtwork(byte[] artwork)
    {
        const int MaxArtworkDimensionPx = 112; // 56x56 DIP at 200% DPI
        int srcWidth = 0, srcHeight = 0;
        using (var probeStream = new MemoryStream(artwork, writable: false))
        {
            var decoder = BitmapDecoder.Create(probeStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count > 0)
            {
                var frame = decoder.Frames[0];
                srcWidth = frame.PixelWidth;
                srcHeight = frame.PixelHeight;
            }
        }

        using var stream = new MemoryStream(artwork, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (srcWidth > MaxArtworkDimensionPx || srcHeight > MaxArtworkDimensionPx)
        {
            if (srcWidth >= srcHeight) image.DecodePixelWidth = MaxArtworkDimensionPx;
            else image.DecodePixelHeight = MaxArtworkDimensionPx;
        }
        else if (srcWidth <= 0 || srcHeight <= 0)
        {
            image.DecodePixelWidth = MaxArtworkDimensionPx;
        }
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
    private static string MediaTime(double milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }
    private void UpdateMediaProgress(AndroidMediaState? state)
    {
        if (_seeking && state != null && state.Package == _seekPackage && state.Title == _seekTitle) return;
        _seeking = false;
        MediaPositionSlider.IsEnabled = state?.CanSeek == true;
        double position = state?.CurrentPositionMs ?? 0;
        // The current visible clock already predicts position between callbacks. Keep
        // it when another snapshot agrees, instead of rebuilding an animation for
        // every position notification. A seek, track/speed change or >200ms drift
        // still replaces the clock immediately.
        if (IsVisible && state?.Playing == true && _progressState is { Playing: true } clock
            && state.Package == clock.Package && state.Title == clock.Title
            && state.DurationMs == clock.DurationMs && state.Speed == clock.Speed
            && Math.Abs(position - clock.CurrentPositionMs) <= 200) return;
        _progressState = null;
        var property = System.Windows.Controls.Primitives.RangeBase.ValueProperty;
        MediaPositionSlider.BeginAnimation(property, null);
        MediaPositionSlider.Maximum = Math.Max(1, state?.DurationMs ?? 0);
        MediaPositionSlider.Value = position;
        UpdateMediaPositionText(position);
        long duration = state?.DurationMs ?? 0;
        if (_mediaDurationMs != duration)
        {
            _mediaDurationMs = duration;
            MediaDurationText.Text = duration > 0 ? MediaTime(duration) : "--:--";
        }
        if (IsVisible && state?.Playing == true && state.DurationMs > position && state.Speed > 0)
        {
            var animation = new DoubleAnimation(position, state.DurationMs,
                TimeSpan.FromMilliseconds((state.DurationMs - position) / state.Speed)) { FillBehavior = FillBehavior.HoldEnd };
            Timeline.SetDesiredFrameRate(animation, 1); // one UI update/sec, only during visible playback; no ADB poll
            MediaPositionSlider.BeginAnimation(property, animation);
            _progressState = state;
        }
    }
    private void MediaPosition_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (MediaPositionText != null) UpdateMediaPositionText(e.NewValue); }
    private void UpdateMediaPositionText(double position)
    {
        long second = (long)(Math.Max(0, position) / 1000);
        if (_mediaPositionSecond == second) return;
        _mediaPositionSecond = second;
        MediaPositionText.Text = MediaTime(position);
    }
    private void BeginMediaSeek()
    {
        if (_seeking || _mediaState?.CanSeek != true) return;
        _seeking = true; _progressState = null; _seekPackage = _mediaState.Package; _seekTitle = _mediaState.Title;
        double current = MediaPositionSlider.Value;
        MediaPositionSlider.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        MediaPositionSlider.Value = current;
    }
    private async Task CommitMediaSeekAsync()
    {
        if (!_seeking) return;
        _seeking = false;
        var state = _mediaState; var client = _mediaClient; var read = _mediaRead;
        long target = (long)Math.Round(MediaPositionSlider.Value);
        if (state?.CanSeek != true || state.Package != _seekPackage || state.Title != _seekTitle || client == null || read == null)
        { UpdateMediaProgress(state); return; }
        try
        {
            await client.SeekAsync(state.Package, target, read.Token);
            if (ReferenceEquals(_mediaState, state))
            {
                _mediaState = state with { PositionMs = target, ReceivedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() };
                UpdateMediaProgress(_mediaState);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        { if (!_closed) { UpdateMediaProgress(_mediaState); SetMediaError("Phone.MediaSeekIncomplete", ex); } }
    }
    private void MediaPosition_MouseDown(object sender, MouseButtonEventArgs e) => BeginMediaSeek();
    private async void MediaPosition_MouseUp(object sender, MouseButtonEventArgs e) => await CommitMediaSeekAsync();
    private async void MediaPosition_LostCapture(object sender, MouseEventArgs e) => await CommitMediaSeekAsync();
    private void MediaPosition_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _seeking) { _seeking = false; UpdateMediaProgress(_mediaState); e.Handled = true; }
        else if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown) BeginMediaSeek();
    }
    private async void MediaPosition_KeyUp(object sender, KeyEventArgs e)
    { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown) await CommitMediaSeekAsync(); }
    private async Task SendMediaAsync(string command)
    {
        var client = _mediaClient; var read = _mediaRead;
        if (client == null || read == null) return;
        try { await client.SendAsync(command, read.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) SetMediaError("Phone.MediaActionIncompleteRetry", ex); }
    }
    private void SetMediaError(string key, Exception ex)
    {
        _mediaErrorKey = key;
        _mediaErrorEx = ex;
        UpdateMediaErrorText();
    }
    private void UpdateMediaErrorText()
    {
        if (_mediaErrorKey != null) MediaSubtitle.Text = L10n.T(_mediaErrorKey);
        MediaSubtitle.ToolTip = _mediaErrorEx != null ? AndroidErrorTranslation.Format(_mediaErrorEx) : null;
    }
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed) return;
        UpdateDropCellText(_androidReturnDropZone, L10n.T("Phone.ReturnToPhoneTitle"), L10n.T("Phone.ReturnToPhoneDesc"));
        _pcDropZonesDirty = true;
        if (PcDropHighlight.Visibility == Visibility.Visible || _pcDropCells.Count > 0)
            EnsurePcDropZones(force: true);
        foreach (var item in _apps) item.RefreshLanguage();
        ApplyFilter();
        UpdateConnectionStatusText();
        if (_hasQuality) RefreshConnectionQualityText();
        if (_mediaErrorKey != null && _mediaErrorEx != null)
            UpdateMediaErrorText();
        else if (_hasAppliedMediaDetails)
            ApplyMediaDetails(_mediaState, _artworkBytes, false);
    }
    private async void MediaPrevious_Click(object sender, RoutedEventArgs e) => await SendMediaAsync("previous");
    private async void MediaPlayPause_Click(object sender, RoutedEventArgs e) => await SendMediaAsync("toggle");
    private async void MediaNext_Click(object sender, RoutedEventArgs e) => await SendMediaAsync("next");
    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    { if (e.OriginalSource is TextBlock && e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
#endif

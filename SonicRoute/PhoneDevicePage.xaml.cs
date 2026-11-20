#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SonicRoute.AndroidAudio;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SonicRoute;

public sealed class PhoneAppChoice : INotifyPropertyChanged
{
    public PhoneAppItem Item { get; }
    private bool _retained;
    public bool Retained
    {
        get => _retained;
        internal set
        {
            if (_retained == value) return;
            _retained = value;
            PropertyChanged?.Invoke(this, new(nameof(Retained)));
            PropertyChanged?.Invoke(this, new(nameof(ActionText)));
        }
    }
    public string ActionText => Retained ? L10n.T("Phone.RemoveFromQuickPanel") : L10n.T("Phone.AddToQuickPanel");
    internal void RefreshLanguage()
    {
        Item.RefreshLanguage();
        PropertyChanged?.Invoke(this, new(nameof(ActionText)));
        PropertyChanged?.Invoke(this, new(nameof(Item)));
    }
    private bool _smartVolume;
    public bool SmartVolume
    {
        get => _smartVolume;
        internal set { if (_smartVolume == value) return; _smartVolume = value; PropertyChanged?.Invoke(this, new(nameof(SmartVolume))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public PhoneAppChoice(PhoneAppItem item, bool retained, bool smartVolume = true)
    { Item = item; _retained = retained; _smartVolume = smartVolume; }
}

public partial class PhoneDevicePage : UserControl, IDisposable
{
    private sealed record UsbRestoreChoice(AndroidDevice Device, string Label);
    private readonly CancellationTokenSource _lifetime = new();
    private AndroidWirelessConnection? _wireless;
    private AndroidWirelessConnection Wireless => _wireless ??= new AndroidWirelessConnection(AndroidAudioHost.Adb);
    private readonly Func<AndroidQrPairing> _createQr;
    private readonly Func<AndroidDevice, CancellationToken, Task<AndroidEncoderCatalog>> _readStreamEncoders;
    private CancellationTokenSource? _operation;
    private IReadOnlyList<PhoneAppItem> _catalog = Array.Empty<PhoneAppItem>();
    private IReadOnlyList<PhoneAppChoice> _catalogChoices = Array.Empty<PhoneAppChoice>();
    private string? _catalogSerial;
    private string? _profileId;
    private AndroidDevice? _device;
    private static int _activePageCount;
    internal static bool HasActivePage => Volatile.Read(ref _activePageCount) > 0;
    private bool _active, _disposed, _busy, _showConnection, _suppressName, _applyingConnection;
    private IReadOnlyList<AndroidApp>? _retainedSnapshot;
    private IReadOnlyList<string>? _smartVolumeSnapshot;
    private double _deviceScrollOffset;
    private bool _suppressClipboard;
    private enum CatalogStatusKind { None, Loading, Empty, Loaded }
    private CatalogStatusKind _catalogStatusKind;
    private string _opStatusKey = "";
    private Exception? _opStatusError;
    internal bool IsConnectionPage => _showConnection || AndroidAudioHost.Phone.Profile.LastDevice == null;
    public PhoneDevicePage() : this(null) { }
    internal PhoneDevicePage(AndroidWirelessConnection? wireless, Func<AndroidQrPairing>? createQr = null,
        Func<AndroidDevice, CancellationToken, Task<AndroidEncoderCatalog>>? readStreamEncoders = null)
    {
        _wireless = wireless;
        _createQr = createQr ?? AndroidQrPairing.Create;
        _readStreamEncoders = readStreamEncoders ?? ((device, token) => AndroidAudioHost.Adb.EncodersAsync(device, token));
        InitializeComponent();
        ApplyDevices(PhoneAudioWindowCache.Devices ?? Array.Empty<AndroidDevice>());
        ProfileChanged();
    }
    internal void Activate()
    {
        if (_disposed || _active) return;
        _active = true;
        Interlocked.Increment(ref _activePageCount);
        AndroidAudioHost.Phone.Changed += ProfileChanged;
        L10n.Instance.PropertyChanged += OnLanguageChanged;
        if (System.Windows.Application.Current is App application)
        { application.PhoneClipboardChanged += RefreshClipboard; _ = LoadClipboardAsync(application); }
        RefreshLocalizedUi();
        if (PhoneAudioWindowCache.Devices == null && (!IsConnectionPage || AndroidConnectionChoice.IsChecked == true))
            _ = RunAsync(RefreshDevicesCoreAsync);
    }
    internal void ShowConnection()
    {
        _showConnection = true;
        ReleaseCatalog(); _operation?.Cancel(); ProfileChanged(); DeviceScroll.ScrollToTop();
        UpdateConnectionChoice();
    }
    internal void ShowDevice()
    {
        _showConnection = false;
        if (AndroidAudioHost.Phone.Profile.LastDevice == null) AndroidConnectionChoice.IsChecked = true;
        ReleaseCatalog(); _operation?.Cancel(); ProfileChanged(); DeviceScroll.ScrollToTop();
    }
    internal Task RefreshAsync()
    {
        if (AirPlayCard.IsVisible) return Task.CompletedTask;
        if (CatalogPanel.Visibility == Visibility.Visible) return LoadCatalogAsync(true);
        if (_device is { } device && !_busy)
        {
            CancelEncoderRead();
            if (AndroidAudioHost.IsAdbCreated) AndroidAudioHost.Adb.InvalidateEncoders(device);
            _streamEncoders = null;
            _encoderFailed = false;
        }
        return RunAsync(IsConnectionPage ? RefreshDevicesCoreAsync : RefreshSelectedDeviceCoreAsync);
    }
    internal void Suspend()
    {
        if (_active)
        {
            _active = false;
            Interlocked.Decrement(ref _activePageCount);
        }
        _operation?.Cancel();
        ResetAndroidFilePicker(null);
        StopQrPairing();
        ResetSavedDeviceSearch();
        CancelStreamingWork();
        AndroidAudioHost.Phone.Changed -= ProfileChanged;
        L10n.Instance.PropertyChanged -= OnLanguageChanged;
        if (System.Windows.Application.Current is App application) application.PhoneClipboardChanged -= RefreshClipboard;
        ReleaseCatalog();
        _device = null;
        PhoneNameBox.Visibility = Visibility.Collapsed;
        PairCodeBox.Clear(); PairQrImage.Source = null; PairQrImage.Visibility = Visibility.Collapsed;
        AndroidAudioHost.TrimForIdle();
    }
    internal void ApplyDevices(IReadOnlyList<AndroidDevice> devices)
    {
        ProfileChanged();
    }
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || !_active) return;
        RefreshLocalizedUi();
    }
    private void RefreshLocalizedUi()
    {
        ProfileChanged();
        SetBusy(_busy);
        RefreshQrStatus();
        UpdateCatalogStatus();
        foreach (PhoneAppChoice choice in RetainedApps.Items) choice.RefreshLanguage();
        foreach (var choice in _catalogChoices) choice.RefreshLanguage();
        if (CatalogPanel.Visibility == Visibility.Visible)
        {
            int count = CatalogApps.SelectedItems.Cast<PhoneAppChoice>().Count(choice => !choice.Retained);
            AddSelectedAppsMenu.Header = count == 0 ? L10n.T("Phone.AddSelectedApps") : string.Format(L10n.T("Phone.AddSelectedAppsCount"), count);
        }
    }
    private void ProfileChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(ProfileChanged)); return; }
        if (_disposed || IsLoaded && !_active) return;
        var profile = AndroidAudioHost.Phone.Profile;
        if (_profileId != profile.Id)
        {
            _profileId = profile.Id;
            if (!_applyingConnection) _operation?.Cancel();
            PhoneNameBox.Visibility = CatalogPanel.Visibility = Visibility.Collapsed;
            ReleaseCatalog();
        }
        _device = PhoneAudioWindowCache.GetOnlineDevice(profile.LastDevice);
        PhoneAudioWindowCache.SelectedSerial = _device?.Serial ?? profile.LastDevice?.Serial;
        bool connection = IsConnectionPage;
        if ((connection || _device == null) && CatalogPanel.Visibility == Visibility.Visible)
        { _operation?.Cancel(); ReleaseCatalog(); }
        DeviceName.Text = AndroidAudioHost.Phone.GetProfileDisplayName(profile);
        var display = _device ?? profile.LastDevice;
        DeviceDescription.Text = _device == null ? L10n.T("Phone.Disconnected")
            : $"{DeviceName.Text} · {(_device.IsWifi ? "Wi-Fi" : "USB")} · {L10n.T("Phone.ConnectedStatus")}";
        bool appsChanged = !ReferenceEquals(_retainedSnapshot, profile.RetainedApps);
        if (appsChanged)
        {
            _retainedSnapshot = profile.RetainedApps;
            RetainedApps.ItemsSource = profile.RetainedApps.Select(a => new PhoneAppChoice(new PhoneAppItem(a), true,
                !profile.DisabledSmartVolumeApps.Contains(a.Package))).ToArray();
        }
        else if (!ReferenceEquals(_smartVolumeSnapshot, profile.DisabledSmartVolumeApps))
            foreach (PhoneAppChoice choice in RetainedApps.Items) choice.SmartVolume = !profile.DisabledSmartVolumeApps.Contains(choice.Item.Package);
        _smartVolumeSnapshot = profile.DisabledSmartVolumeApps;
        RetainedCount.Text = string.Format(L10n.T("Phone.RetainedCountFormat"), profile.RetainedApps.Count);
        RetainedEmpty.Visibility = profile.RetainedApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RetainedPanel.Visibility = DeviceCard.Visibility = StreamingCard.Visibility = connection ? Visibility.Collapsed : Visibility.Visible;
        RefreshStreamingSettings(connection ? null : display);
        ConnectionPanel.Visibility = connection ? Visibility.Visible : Visibility.Collapsed;
        var usbChoices = connection && PhoneAudioWindowCache.Devices is { } cached
            ? cached.Where(d => d.State == "device" && !d.IsWifi && PhoneAudioWindowCache.GetOnlineDevice(d) == null
                && !AndroidConnectionManager.IsAmbiguous(d, cached))
                .Select(d => new UsbRestoreChoice(d, string.Format(L10n.T("Phone.RestoreUsbDevice"), d.Name))).ToArray()
            : Array.Empty<UsbRestoreChoice>();
        RestoreUsbDevices.ItemsSource = usbChoices.Length == 0 ? null : usbChoices;
        RestoreUsbDevices.Visibility = usbChoices.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenameButton.Visibility = RetainedPanel.Visibility;
        AddAppsButton.IsEnabled = _device != null && !_busy;
        DisconnectButton.IsEnabled = _device != null && !_busy;
        DisconnectButton.Content = _device?.IsWifi == false ? L10n.T("Phone.StopReceiving") : L10n.T("Phone.Disconnect");
        ReconnectButton.IsEnabled = display != null && !_busy;
        UnpairButton.IsEnabled = display != null && !_busy;
        RemoveDeviceButton.IsEnabled = profile.LastDevice != null && !_busy;
        if (appsChanged && CatalogPanel.Visibility == Visibility.Visible) ApplyCatalogFilter();
        UpdateAndroidFiles(connection);
        RefreshClipboard();
        UpdateQrPairing();
        UpdateSavedDeviceSearch();
        UpdateOperationStatus();
    }
    private async Task LoadClipboardAsync(App application)
    {
        await application.PreparePhoneClipboardAsync();
        if (!_disposed && _active) RefreshClipboard();
    }
    private void RefreshClipboard()
    {
        if (_disposed || !_active || System.Windows.Application.Current is not App application) return;
        var device = AndroidAudioHost.Phone.Profile.LastDevice;
        _suppressClipboard = true;
        try { ClipboardSyncToggle.IsChecked = application.PhoneClipboardEnabled(device); ClipboardSyncToggle.IsEnabled = device != null; }
        finally { _suppressClipboard = false; }
        ClipboardStatus.Text = application.PhoneClipboardStatus(device);
    }
    private async void ClipboardSync_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressClipboard || _disposed || !_active || AndroidAudioHost.Phone.Profile.LastDevice is not { } device
            || System.Windows.Application.Current is not App application) return;
        bool enabled = ClipboardSyncToggle.IsChecked == true;
        try
        {
            var sync = await application.PreparePhoneClipboardAsync();
            if (!AndroidAudioHost.Phone.IsRemoved(device)) sync.SetEnabled(device, enabled);
        }
        catch (Exception) { if (!_disposed) SetOperationStatusKey("Phone.ClipboardSaveError"); }
        if (!_disposed) RefreshClipboard();
    }
    private void SetOperationStatusKey(string key) { _opStatusKey = key; _opStatusError = null; UpdateOperationStatus(); }
    private void SetOperationStatusError(Exception ex) { _opStatusKey = ""; _opStatusError = ex; UpdateOperationStatus(); }
    private void UpdateOperationStatus()
    {
        string? operation = _opStatusError != null ? AndroidErrorTranslation.Format(_opStatusError)
            : string.IsNullOrEmpty(_opStatusKey) ? null : L10n.T(_opStatusKey);
        OperationStatus.Text = AndroidErrorTranslation.WithUsbAccessDetail(operation,
            IsConnectionPage ? null : AndroidAudioHost.Phone.Profile.LastDevice) ?? "";
    }
    private void SetCatalogStatus(CatalogStatusKind kind) { _catalogStatusKind = kind; UpdateCatalogStatus(); }
    private void UpdateCatalogStatus()
    {
        CatalogStatus.Text = _catalogStatusKind switch
        {
            CatalogStatusKind.Loading => L10n.T("Phone.LoadingPhoneApps"),
            CatalogStatusKind.Empty => L10n.T("Phone.CatalogEmpty"),
            CatalogStatusKind.Loaded => L10n.T("Phone.CatalogSelectionHint"),
            _ => ""
        };
    }
    private long _operationManagementRevision;
    private long _operationDiscoveryRevision;
    private async Task RunAsync(Func<CancellationToken, Task> action, string success = "", bool fromQr = false)
    {
        if (_disposed || _busy || !_active) return;
        _busy = true;
        _operationManagementRevision = PhoneAudioWindowCache.Manager.Revision;
        _operationDiscoveryRevision = PhoneAudioWindowCache.Discovery.Revision;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        SetBusy(true); SetOperationStatusKey("Phone.ProcessingStatus");
        StopQrPairing();
        try
        {
            // Retire the scanner before another pairing method uses the same transport.
            if (!fromQr && _qrTask != null) await _qrTask;
            operation.Token.ThrowIfCancellationRequested();
            await action(operation.Token);
            if (!_disposed && !operation.IsCancellationRequested) SetOperationStatusKey(success);
        }
        catch (OperationCanceledException) { if (!_disposed) SetOperationStatusKey("Phone.StatusCancelled"); }
        catch (Exception ex) { if (!_disposed && !operation.IsCancellationRequested) SetOperationStatusError(ex); }
        finally
        {
            if (ReferenceEquals(_operation, operation)) _operation = null;
            _busy = false;
            if (!_disposed) { SetBusy(false); ProfileChanged(); }
        }
    }
    private void SetBusy(bool busy)
    {
        PairButton.IsEnabled = !busy;
        PairAddressBox.IsEnabled = PairCodeBox.IsEnabled = QrButton.IsEnabled = !busy;
        ConnectAddressBox.IsEnabled = SavedConnectDevices.IsEnabled = !busy;
        if (busy) StopSavedDeviceSearch();
        RefreshSavedDeviceSearchStatus();
        RestoreUsbDevices.IsEnabled = !busy;
    }
    private async Task RefreshDevicesCoreAsync(CancellationToken token) =>
        ApplyRefreshedDevices(await PhoneAudioWindowCache.ReadDevicesAsync(token), token);

    private async Task RefreshSelectedDeviceCoreAsync(CancellationToken token) =>
        ApplyRefreshedDevices(await PhoneAudioWindowCache.RefreshSelectedDeviceAsync(token, _wireless), token);

    private void ApplyRefreshedDevices(IReadOnlyList<AndroidDevice> devices, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ApplyDevices(PhoneAudioWindowCache.Devices ?? devices);
        _ = AndroidAudioHost.ProbeOutputsIfConnectedAsync(devices, force: true, _lifetime.Token);
    }
    private void ApplyConnection(AndroidConnectionResult result, CancellationToken token, long? expectedRevision = null)
    {
        token.ThrowIfCancellationRequested();
        _showConnection = false;
        _applyingConnection = true;
        try
        {
            if (!PhoneAudioWindowCache.Discovery.PublishConnection(result, _operationDiscoveryRevision,
                current => PhoneAudioWindowCache.Manager.Admit(current, expectedRevision ?? _operationManagementRevision, requireCurrent: true)))
                throw AndroidError.WithCode(new IOException("设备身份或管理状态已改变，请重新确认连接。"), AndroidErrorCode.ReconnectNoDeviceFound);
            ApplyDevices(PhoneAudioWindowCache.Devices ?? result.Devices);
            _ = AndroidAudioHost.ProbeOutputsIfConnectedAsync([result.Device], force: true, _lifetime.Token);
        }
        finally { _applyingConnection = false; }
    }
    private async void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        var profile = AndroidAudioHost.Phone.Profile;
        if (profile.LastDevice is not { } device) return;
        await RunAsync(async _ =>
        {
            _applyingConnection = true;
            try
            {
                await AndroidAudioHost.RemovePhoneAsync(profile.Id, device);
            }
            finally { _applyingConnection = false; }
            _showConnection = AndroidAudioHost.Phone.Profile.LastDevice == null;
        }, "Phone.StatusRemovedKeepAuth");
    }
    private async void Unpair_Click(object sender, RoutedEventArgs e)
    {
        var device = _device ?? AndroidAudioHost.Phone.Profile.LastDevice; if (device == null) return;
        await RunAsync(async token =>
        {
            if (_device == null) throw AndroidError.WithCode(new IOException("手机未连接，请先重连，或在手机无线调试中移除此电脑。"), AndroidErrorCode.UnpairPhoneOffline);
            await AndroidAudioHost.StopManagedPhoneAsync(device, () => Wireless.UnpairAsync(device, token));
            AndroidAudioHost.Phone.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker]);
            await RefreshDevicesCoreAsync(token);
        }, "Phone.StatusUnpairRequested");
    }
    private async void RestoreUsb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: UsbRestoreChoice choice } || !IsConnectionPage) return;
        await RunAsync(async token =>
        {
            var devices = await PhoneAudioWindowCache.ReadDevicesAsync(token);
            var live = (PhoneAudioWindowCache.Discovery.CurrentDevices ?? []).FirstOrDefault(d => !d.IsWifi && d.State == "device"
                && AndroidDeviceIdentity.Matches(choice.Device, d) && PhoneAudioWindowCache.GetOnlineDevice(d) == null);
            if (live == null) throw AndroidError.WithCode(new IOException("设备尚未连接。"), AndroidErrorCode.UsbReconnectHint);
            ApplyConnection(new(live, PhoneAudioWindowCache.Devices ?? devices), token);
        }, "Phone.ConnectedStatus");
    }
    private async void PairCode_Click(object sender, RoutedEventArgs e)
    {
        string address = PairAddressBox.Text.Trim(), code = PairCodeBox.Text;
        PairCodeBox.Clear();
        await RunAsync(async token =>
        {
            var pairing = await Wireless.PairCodeAsync(address, code, token);
            SetOperationStatusKey("Phone.StatusPairedConnecting");
            ApplyConnection(await Wireless.FinishPairingAsync(pairing, token), token);
        }, "Phone.ConnectedStatus");
    }
    private async void Reconnect_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (AndroidAudioHost.Phone.Profile.LastDevice == null) return;
        await RefreshSelectedDeviceCoreAsync(token);
        if (_device == null) throw AndroidError.WithCode(new IOException("未发现设备，请开启无线调试或更新连接地址。"), AndroidErrorCode.ReconnectNoDeviceFound);
    });
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var device = _device; if (device == null) return;
        await RunAsync(async token =>
        {
            var aliases = (PhoneAudioWindowCache.Discovery.CurrentDevices ?? [device]).Where(d => d.IsWifi
                && AndroidDeviceIdentity.Matches(d, device)).DistinctBy(d => d.CommandTarget).ToArray();
            await AndroidAudioHost.StopManagedPhoneAsync(device, async () =>
            {
                foreach (var alias in aliases) await Wireless.DisconnectAsync(alias, token);
            });
            AndroidAudioHost.Phone.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker]);
            await RefreshDevicesCoreAsync(token);
        }, device.IsWifi ? "Phone.StatusWifiDisconnectedSaved" : "Phone.StatusUsbStoppedUnplug");
    }
    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        _suppressName = true;
        try { InlineNameEdit.Begin(DeviceName, PhoneNameBox); }
        finally { _suppressName = false; }
    }
    private void Name_TextChanged(object sender, TextChangedEventArgs e)
    { if (!_suppressName && !_disposed && PhoneNameBox.Visibility == Visibility.Visible) AndroidAudioHost.Phone.Rename(PhoneNameBox.Text); }
    private void Name_KeyDown(object sender, KeyEventArgs e) => InlineNameEdit.KeyDown(PhoneNameBox, e);
    private void Name_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => PhoneNameBox.Visibility = Visibility.Collapsed;
    private async void AddApps_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_active || _busy || _device == null) return;
        SetCatalogVisible(true);
        CatalogSearch.Focus();
        await LoadCatalogAsync(false);
    }
    private void CloseCatalog_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        ReleaseCatalog();
        if (AndroidAudioHost.IsCatalogCreated && !(System.Windows.Application.Current is App { IsPhoneWindowOpen: true }))
            AndroidAudioHost.Catalog.TrimForIdle();
    }
    private void SetCatalogVisible(bool visible)
    {
        if (visible && CatalogPanel.Visibility != Visibility.Visible) _deviceScrollOffset = DeviceScroll.VerticalOffset;
        DeviceScroll.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        CatalogPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) DeviceScroll.ScrollToVerticalOffset(_deviceScrollOffset);
    }
    private void ReleaseCatalog()
    {
        CatalogApps.ContextMenu.IsOpen = false;
        SetCatalogVisible(false);
        CatalogApps.ItemsSource = null;
        _catalog = Array.Empty<PhoneAppItem>(); _catalogChoices = Array.Empty<PhoneAppChoice>(); _catalogSerial = null; _catalogStatusKind = CatalogStatusKind.None;
    }
    private async Task LoadCatalogAsync(bool refresh)
    {
        var device = PhoneAudioWindowCache.GetOnlineDevice(_device); if (device == null) return;
        if (!refresh && _catalogSerial == device.CommandTarget) { ApplyCatalogFilter(); return; }
        await RunAsync(async token =>
        {
            SetCatalogStatus(CatalogStatusKind.Loading);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, PhoneAudioWindowCache.Manager.OperationToken(device));
            var apps = await AndroidAudioHost.Catalog.GetAsync(device, refresh, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!AndroidDeviceIdentity.SameTransport(_device, device) || !PhoneAudioWindowCache.Manager.IsCurrent(device)) return;
            _catalog = apps.Select(a => new PhoneAppItem(a)).ToArray(); _catalogSerial = device.CommandTarget;
            _catalogChoices = _catalog.Select(item => new PhoneAppChoice(item, false)).ToArray();
            AndroidAudioHost.Phone.UpdateMetadata(device, apps);
            ApplyCatalogFilter();
        });
    }
    private void CatalogSearch_Changed(object sender, TextChangedEventArgs e) => ApplyCatalogFilter();
    private void ApplyCatalogFilter()
    {
        if (CatalogApps == null || CatalogPanel.Visibility != Visibility.Visible) return;
        string query = CatalogSearch.Text.Trim();
        var retained = AndroidAudioHost.Phone.Profile.RetainedApps.Select(a => a.Package).ToHashSet(StringComparer.Ordinal);
        foreach (var choice in _catalogChoices) choice.Retained = retained.Contains(choice.Item.Package);
        var filtered = _catalogChoices.Where(a => query.Length == 0 || a.Item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || a.Item.Package.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (CatalogApps.ItemsSource is not IReadOnlyList<PhoneAppChoice> previous || !previous.SequenceEqual(filtered))
            CatalogApps.ItemsSource = filtered;
        SetCatalogStatus(_catalog.Count == 0 ? CatalogStatusKind.Empty : CatalogStatusKind.Loaded);
    }
    private void CatalogApps_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(CatalogApps, source) is ListBoxItem item)
        {
            // Right-clicking an existing selection must keep its other selected rows.
            if (!item.IsSelected) CatalogApps.SelectedItem = item.Content;
            item.Focus();
        }
    }
    private void CatalogMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (_disposed || !_active || CatalogPanel.Visibility != Visibility.Visible) { e.Handled = true; return; }
        int count = CatalogApps.SelectedItems.Cast<PhoneAppChoice>().Count(choice => !choice.Retained);
        AddSelectedAppsMenu.Header = count == 0 ? L10n.T("Phone.AddSelectedApps") : string.Format(L10n.T("Phone.AddSelectedAppsCount"), count);
        AddSelectedAppsMenu.IsEnabled = !_busy && count > 0;
    }
    private void AddSelectedApps_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_active || _busy || CatalogPanel.Visibility != Visibility.Visible) return;
        var apps = CatalogApps.SelectedItems.Cast<PhoneAppChoice>().Where(choice => !choice.Retained).Select(choice => choice.Item.App).ToArray();
        AndroidAudioHost.Phone.RetainMany(apps);
    }
    private void SmartVolume_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: PhoneAppChoice choice } toggle && RetainedApps.Items.Contains(choice)
            && choice.SmartVolume != (toggle.IsChecked == true))
            AndroidAudioHost.Phone.SetSmartVolume(choice.Item.Package, toggle.IsChecked == true);
    }
    private void RetainApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AndroidApp app) return;
        if (AndroidAudioHost.Phone.Profile.RetainedApps.Any(a => a.Package == app.Package)) AndroidAudioHost.Phone.Remove(app.Package);
        else AndroidAudioHost.Phone.Retain(app);
    }
    public void Dispose()
    {
        if (_disposed) return;
        Suspend(); _disposed = true; _lifetime.Cancel(); AndroidAudioHost.Phone.Changed -= ProfileChanged;
        RetainedApps.ItemsSource = CatalogApps.ItemsSource = null;
        _catalog = Array.Empty<PhoneAppItem>();
        _catalogChoices = Array.Empty<PhoneAppChoice>();
        _retainedSnapshot = null;
        _smartVolumeSnapshot = null;
        _device = null;
        _wireless = null;
        _opStatusError = null;
    }
}
#endif

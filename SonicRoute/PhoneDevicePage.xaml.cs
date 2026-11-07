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
using System.Windows.Media.Imaging;
using QRCoder;
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
    private readonly CancellationTokenSource _lifetime = new();
    private AndroidWirelessConnection? _wireless;
    private AndroidWirelessConnection Wireless => _wireless ??= new AndroidWirelessConnection(AndroidAudioHost.Adb);
    private readonly Func<AndroidQrPairing> _createQr;
    private CancellationTokenSource? _operation;
    private IReadOnlyList<PhoneAppItem> _catalog = Array.Empty<PhoneAppItem>();
    private IReadOnlyList<PhoneAppChoice> _catalogChoices = Array.Empty<PhoneAppChoice>();
    private string? _catalogSerial;
    private string? _profileId;
    private AndroidDevice? _device;
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
    internal PhoneDevicePage(AndroidWirelessConnection? wireless, Func<AndroidQrPairing>? createQr = null)
    {
        _wireless = wireless;
        _createQr = createQr ?? AndroidQrPairing.Create;
        InitializeComponent();
        ApplyDevices(PhoneAudioWindowCache.Devices ?? Array.Empty<AndroidDevice>());
        ProfileChanged();
    }
    internal void Activate()
    {
        if (_disposed || _active) return;
        _active = true;
        AndroidAudioHost.Phone.Changed += ProfileChanged;
        L10n.Instance.PropertyChanged += OnLanguageChanged;
        if (System.Windows.Application.Current is App application)
        { application.PhoneClipboardChanged += RefreshClipboard; _ = LoadClipboardAsync(application); }
        RefreshLocalizedUi();
        if (PhoneAudioWindowCache.Devices == null) _ = RunAsync(RefreshDevicesCoreAsync);
    }
    internal void ShowConnection() { _showConnection = true; ReleaseCatalog(); _operation?.Cancel(); ProfileChanged(); DeviceScroll.ScrollToTop(); }
    internal void ShowDevice() { _showConnection = false; ReleaseCatalog(); _operation?.Cancel(); ProfileChanged(); DeviceScroll.ScrollToTop(); }
    internal Task RefreshAsync() => CatalogPanel.Visibility == Visibility.Visible ? LoadCatalogAsync(true) : RunAsync(RefreshDevicesCoreAsync);
    internal void Suspend()
    {
        _active = false; _operation?.Cancel();
        AndroidAudioHost.Phone.Changed -= ProfileChanged;
        L10n.Instance.PropertyChanged -= OnLanguageChanged;
        if (System.Windows.Application.Current is App application) application.PhoneClipboardChanged -= RefreshClipboard;
        ReleaseCatalog();
        PhoneNameBox.Visibility = Visibility.Collapsed;
        PairCodeBox.Clear(); PairQrImage.Source = null; PairQrImage.Visibility = Visibility.Collapsed;
    }
    internal void ApplyDevices(IReadOnlyList<AndroidDevice> devices)
    {
        AndroidAudioHost.Phone.ObserveDevices(devices);
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
        UpdateOperationStatus();
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
        _device = PhoneAudioWindowCache.Devices?.FirstOrDefault(d => d.State == "device" && AndroidDeviceIdentity.Matches(d, profile.LastDevice));
        PhoneAudioWindowCache.SelectedSerial = _device?.Serial ?? profile.LastDevice?.Serial;
        bool connection = IsConnectionPage;
        if ((connection || _device == null) && CatalogPanel.Visibility == Visibility.Visible)
        { _operation?.Cancel(); ReleaseCatalog(); }
        DeviceName.Text = profile.Name;
        var display = _device ?? profile.LastDevice;
        DeviceDescription.Text = display == null ? L10n.T("Phone.Disconnected") : $"{AndroidAudioHost.Phone.GetDisplayName(display)} · {(display.IsWifi ? "Wi-Fi" : "USB")} · {(_device == null ? L10n.T("Phone.Disconnected") : L10n.T("Phone.ConnectedStatus"))}";
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
        RetainedPanel.Visibility = DeviceCard.Visibility = connection ? Visibility.Collapsed : Visibility.Visible;
        ConnectionPanel.Visibility = connection ? Visibility.Visible : Visibility.Collapsed;
        RenameButton.Visibility = RetainedPanel.Visibility;
        AddAppsButton.IsEnabled = _device != null && !_busy;
        DisconnectButton.IsEnabled = _device != null && !_busy;
        DisconnectButton.Content = _device?.IsWifi == false ? L10n.T("Phone.StopReceiving") : L10n.T("Phone.Disconnect");
        ReconnectButton.IsEnabled = display != null && !_busy;
        UnpairButton.IsEnabled = display != null && !_busy;
        RemoveDeviceButton.IsEnabled = profile.LastDevice != null && !_busy;
        if (appsChanged && CatalogPanel.Visibility == Visibility.Visible) ApplyCatalogFilter();
        RefreshClipboard();
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
        if (_opStatusError != null) OperationStatus.Text = AndroidErrorTranslation.Format(_opStatusError);
        else if (!string.IsNullOrEmpty(_opStatusKey)) OperationStatus.Text = L10n.T(_opStatusKey);
        else OperationStatus.Text = "";
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
    private async Task RunAsync(Func<CancellationToken, Task> action, string success = "")
    {
        if (_disposed || _busy || !_active) return;
        _busy = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        SetBusy(true); SetOperationStatusKey("Phone.ProcessingStatus");
        try
        {
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
        PairButton.IsEnabled = ConnectButton.IsEnabled = !busy;
        QrButton.Content = busy ? L10n.T("Phone.CancelPairing") : L10n.T("Phone.ShowQr");
        QrButton.IsEnabled = true;
    }
    private async Task RefreshDevicesCoreAsync(CancellationToken token)
    {
        var devices = await PhoneAudioWindowCache.ReadDevicesAsync(token);
        token.ThrowIfCancellationRequested();
        ApplyDevices(PhoneAudioWindowCache.Devices ?? devices);
        _ = AndroidAudioHost.ProbeOutputsIfConnectedAsync(devices, force: true, _lifetime.Token);
    }
    private void ApplyConnection(AndroidConnectionResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _showConnection = false;
        _applyingConnection = true;
        try
        {
            AndroidAudioHost.Phone.RememberDevice(result.Device);
            PhoneAudioWindowCache.SetDevices(result.Devices);
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
            try { await AndroidAudioHost.RemovePhoneAsync(profile.Id, device); }
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
            if (AndroidAudioHost.AreDisplaysCreated) await AndroidAudioHost.Displays.CloseDeviceAsync(device);
            if (AndroidAudioHost.IsCreated)
                foreach (var source in AndroidAudioHost.Sources.Sources.Where(s => AndroidDeviceIdentity.Matches(s.Device, device)).ToArray())
                    await AndroidAudioHost.Sources.RemoveAsync(source.Id);
            if (AndroidAudioHost.IsPcToPhoneCreated) await AndroidAudioHost.PcToPhone.StopPhoneAsync(device);
            AndroidAudioHost.Phone.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker]);
            await Wireless.UnpairAsync(device, token);
            PhoneAudioWindowCache.InvalidateDevices();
            PhoneAudioWindowCache.SetDevices((PhoneAudioWindowCache.Devices ?? Array.Empty<AndroidDevice>())
                .Where(d => !d.IsWifi || !AndroidDeviceIdentity.Matches(d, device)).ToArray());
            ApplyDevices(PhoneAudioWindowCache.Devices ?? Array.Empty<AndroidDevice>());
        }, "Phone.StatusUnpairRequested");
    }
    private void PairMethods_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wide = e.NewSize.Width >= 540;
        Grid.SetColumn(CodePairCard,wide ? 1 : 0); Grid.SetRow(CodePairCard,wide ? 0 : 1);
        Grid.SetColumnSpan(QrPairCard,wide ? 1 : 2); Grid.SetColumnSpan(CodePairCard,wide ? 1 : 2);
        QrPairCard.Margin = new Thickness(0,0,wide ? 8 : 0,10);
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        string address = ConnectAddressBox.Text.Trim();
        await RunAsync(async token => ApplyConnection(await Wireless.ConnectAsync(address, token), token), "Phone.ConnectedStatus");
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
    private async void QrPair_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _operation?.Cancel(); return; }
        var qr = _createQr();
        using (var code = QRCodeGenerator.GenerateQrCode(qr.Payload, QRCodeGenerator.ECCLevel.M))
        using (var renderer = new PngByteQRCode(code))
        using (var stream = new MemoryStream(renderer.GetGraphic(6)))
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); PairQrImage.Source = bitmap;
        }
        PairQrImage.Visibility = Visibility.Visible;
        await RunAsync(async token =>
        {
            SetOperationStatusKey("Phone.StatusQrScanPrompt");
            var pairing = await Wireless.PairQrAsync(qr, token);
            SetOperationStatusKey("Phone.StatusPairedConnecting");
            ApplyConnection(await Wireless.FinishPairingAsync(pairing, token), token);
        }, "Phone.ConnectedStatus");
        PairQrImage.Source = null; PairQrImage.Visibility = Visibility.Collapsed;
    }
    private async void Reconnect_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var last = AndroidAudioHost.Phone.Profile.LastDevice;
        if (last == null) return;
        if (last.IsWifi) ApplyConnection(await Wireless.ReconnectAsync(last, token), token);
        else await RefreshDevicesCoreAsync(token);
        if (_device == null) throw AndroidError.WithCode(new IOException("未发现设备，请开启无线调试或更新连接地址。"), AndroidErrorCode.ReconnectNoDeviceFound);
    });
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var device = _device; if (device == null) return;
        await RunAsync(async token =>
        {
            PhoneAudioWindowCache.InvalidateDevices();
            if (AndroidAudioHost.IsCreated)
                foreach (var source in AndroidAudioHost.Sources.Sources.Where(s => AndroidDeviceIdentity.Matches(s.Device, device)))
                    await AndroidAudioHost.Sources.RemoveAsync(source.Id);
            if (AndroidAudioHost.AreDisplaysCreated) await AndroidAudioHost.Displays.CloseDeviceAsync(device);
            if (AndroidAudioHost.IsPcToPhoneCreated) await AndroidAudioHost.PcToPhone.StopPhoneAsync(device);
            AndroidAudioHost.Phone.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker]);
            if (device.IsWifi)
            {
                var aliases = (PhoneAudioWindowCache.Devices ?? new[] { device }).Where(d => d.IsWifi
                    && AndroidDeviceIdentity.Matches(d, device)).DistinctBy(d => d.Serial).ToArray();
                if (aliases.Length == 0) aliases = new[] { device };
                foreach (var alias in aliases) await Wireless.DisconnectAsync(alias, token);
            }
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
    private void CloseCatalog_Click(object sender, RoutedEventArgs e) { _operation?.Cancel(); ReleaseCatalog(); }
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
        var device = _device; if (device == null) return;
        if (!refresh && _catalogSerial == device.Serial) { ApplyCatalogFilter(); return; }
        await RunAsync(async token =>
        {
            SetCatalogStatus(CatalogStatusKind.Loading);
            var apps = await AndroidAudioHost.Catalog.GetAsync(device, refresh, token);
            token.ThrowIfCancellationRequested();
            if (_device?.Serial != device.Serial) return;
            _catalog = apps.Select(a => new PhoneAppItem(a)).ToArray(); _catalogSerial = device.Serial;
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
        RetainedApps.ItemsSource = CatalogApps.ItemsSource = null; _catalog = Array.Empty<PhoneAppItem>();
    }
}
#endif

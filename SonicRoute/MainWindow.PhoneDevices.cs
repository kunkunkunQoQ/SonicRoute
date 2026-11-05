using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
#if SONICROUTE_PHONE
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using SonicRoute.AndroidAudio;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
#endif

namespace SonicRoute;

public partial class MainWindow
{
    private void AboutLinks_Loaded(object sender, RoutedEventArgs e)
    {
#if SONICROUTE_PHONE
        if (sender is not TextBlock links || links.Inlines.OfType<Hyperlink>().Any(link => link.Name == "OpenSourceLicensesLink")) return;
        var licenseLink = new Hyperlink(new Run("开源许可")) { Name = "OpenSourceLicensesLink",
            NavigateUri = new Uri("https://github.com/kunkunkunQoQ/SonicRoute/blob/master/packaging/AndroidLab/THIRD-PARTY-NOTICES.md") };
        licenseLink.SetResourceReference(Hyperlink.ForegroundProperty, "Theme.Accent");
        licenseLink.RequestNavigate += ThirdPartyNotices_RequestNavigate;
        links.Inlines.Add(new Run(" ‖ "));
        links.Inlines.Add(licenseLink);
#endif
    }
#if SONICROUTE_PHONE
    private void ThirdPartyNotices_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowToast(ex.Message); }
    }
    private StackPanel PhoneDevicesHost = null!;
    private Border PhoneDetailsHost = null!;
    private PhoneDevicePage? _phoneDevicePage;
    private readonly Dictionary<string, (Button Button, TextBlock Name, TextBlock State)> _phoneSidebarDevices = new();
    private Border? _phoneSidebarCount;
    private bool _phoneInitialized;
    private Task? _phoneInitialization;
    private Task InitializePhoneManagementAsync()
    {
        if (_phoneInitialized || _isClosed) return Task.CompletedTask;
        if (_phoneInitialization is { IsCompleted: false }) return _phoneInitialization;
        return _phoneInitialization = LoadPhoneManagementAsync();
    }
    private async Task LoadPhoneManagementAsync()
    {
        try
        {
            await AndroidAudioHost.PreparePhoneAsync(_uiLifetime.Token);
            if (!_isClosed) InitializePhoneManagement();
        }
        catch (OperationCanceledException) { }
    }
    private void InitializePhoneManagement(bool discover = false)
    {
        if (_phoneInitialized || _isClosed) return;
        _phoneInitialized = true;
        PhoneDevicesHost = new StackPanel { Name = "PhoneDevicesHost" };
        DockPanel.SetDock(PhoneDevicesHost, Dock.Top);
        AppsSidebarContent.Children.Insert(1, PhoneDevicesHost);
        RegisterName(PhoneDevicesHost.Name, PhoneDevicesHost);
        PhoneDetailsHost = new Border { Name = "PhoneDetailsHost", Visibility = Visibility.Collapsed };
        Grid.SetColumn(PhoneDetailsHost, 1); AppsPage.Children.Add(PhoneDetailsHost);
        RegisterName(PhoneDetailsHost.Name, PhoneDetailsHost);
        PhoneDevicesHost.Visibility = Visibility.Visible;
        var heading = new TextBlock { Text = "手机", Margin = new Thickness(2,0,0,5), FontSize = 12, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
        var headingGrid = new Grid(); headingGrid.ColumnDefinitions.Add(new()); headingGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        headingGrid.Children.Add(heading);
        _phoneSidebarCount = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(5,1,5,1), VerticalAlignment = VerticalAlignment.Top };
        _phoneSidebarCount.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceAlt");
        Grid.SetColumn(_phoneSidebarCount,1); headingGrid.Children.Add(_phoneSidebarCount); PhoneDevicesHost.Children.Add(headingGrid);
        var add = new Button { Content = "＋  添加设备", Height = 26, FontSize = 12, Padding = new Thickness(6,2,6,2), Margin = new Thickness(0,0,0,8) };
        add.SetResourceReference(StyleProperty,"QuickPanelToolButton");
        add.SetResourceReference(ForegroundProperty,"Theme.TextPrimary");
        add.Click += (_,_) => { SelectPhoneManagement(); _phoneDevicePage?.ShowConnection(); };
        PhoneDevicesHost.Children.Add(add);
        var line = new Border { Height = 1, Margin = new Thickness(2,0,2,8) }; line.SetResourceReference(Border.BackgroundProperty,"Theme.Border"); PhoneDevicesHost.Children.Add(line);
        AndroidAudioHost.Phone.Changed += PhoneLibraryChanged;
        AndroidAudioHost.Phone.OutputsChanged += PhoneLibraryChanged;
        PhoneAudioWindowCache.DevicesChanged += PhoneDevicesChanged;
        IsVisibleChanged += PhoneManagementVisibilityChanged; StateChanged += PhoneManagementStateChanged;
        var cachedDevices = PhoneAudioWindowCache.Devices ?? Array.Empty<AndroidDevice>();
        AndroidAudioHost.Phone.ObserveDevices(cachedDevices);
        _ = AndroidAudioHost.ProbeOutputsIfConnectedAsync(cachedDevices, force: false, _uiLifetime.Token);
        PhoneLibraryChanged();
        if (discover) _ = RefreshPhoneDevicesAsync();
    }
    private (Button Button, TextBlock Name, TextBlock State) CreatePhoneSidebarDevice(string id)
    {
        var button = new Button { Margin = new Thickness(0,0,0,4), Padding = new Thickness(6), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        button.SetResourceReference(StyleProperty, "QuickPanelToolButton");
        button.SetResourceReference(BackgroundProperty, "Theme.Overlay");
        var row = new Grid(); row.ColumnDefinitions.Add(new(){ Width = new GridLength(25) }); row.ColumnDefinitions.Add(new());
        button.SizeChanged += (_, e) => row.Width = Math.Max(0, e.NewSize.Width - 14);
        row.Children.Add(AutomationIcons.Create(AutomationIcons.Phone, 20));
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty,"Theme.TextPrimary");
        var state = new TextBlock { FontSize = 10, Margin = new Thickness(0,1,0,0), TextTrimming = TextTrimming.CharacterEllipsis };
        state.SetResourceReference(TextBlock.ForegroundProperty,"Theme.TextSecondary");
        labels.Children.Add(name); labels.Children.Add(state); Grid.SetColumn(labels,1); row.Children.Add(labels); button.Content = row;
        button.Click += (_,_) => { AndroidAudioHost.Phone.SelectProfile(id); SelectPhoneManagement(); _phoneDevicePage?.ShowDevice(); };
        return (button, name, state);
    }
    private void PhoneManagementVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (!IsVisible) SuspendPhoneManagement(); else ActivatePhoneManagement(); }
    private void PhoneManagementStateChanged(object? sender, EventArgs e)
    { if (WindowState == WindowState.Minimized) SuspendPhoneManagement(); else ActivatePhoneManagement(); }
    private void PhoneDevicesChanged()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(PhoneDevicesChanged)); return; }
        if (_isClosed) return;
        var devices = PhoneAudioWindowCache.Devices ?? Array.Empty<AndroidDevice>();
        AndroidAudioHost.Phone.ObserveDevices(devices);
        _ = AndroidAudioHost.ProbeOutputsIfConnectedAsync(devices, force: false, _uiLifetime.Token);
        PhoneLibraryChanged(); _phoneDevicePage?.ApplyDevices(devices);
    }
    private void PhoneLibraryChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(PhoneLibraryChanged)); return; }
        if (_isClosed || !_phoneInitialized) return;
        SyncExternalOutputDevices();
        if (IsLoaded && (!IsVisible || WindowState == WindowState.Minimized
            || AppsPage.Visibility != Visibility.Visible)) return;
        var store = AndroidAudioHost.Phone;
        var profiles = store.Profiles.Where(p => p.LastDevice != null).ToArray();
        var ids = profiles.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string removed in _phoneSidebarDevices.Keys.Where(id => !ids.Contains(id)).ToArray())
        { PhoneDevicesHost.Children.Remove(_phoneSidebarDevices[removed].Button); _phoneSidebarDevices.Remove(removed); }
        int index = 1;
        foreach (var profile in profiles)
        {
            if (!_phoneSidebarDevices.TryGetValue(profile.Id, out var entry))
            {
                entry = CreatePhoneSidebarDevice(profile.Id); _phoneSidebarDevices.Add(profile.Id, entry);
                PhoneDevicesHost.Children.Insert(index, entry.Button);
            }
            entry.Name.Text = profile.Name;
            var device = PhoneAudioWindowCache.Devices?.FirstOrDefault(d => d.State == "device" && AndroidDeviceIdentity.Matches(d, profile.LastDevice));
            entry.State.Text = device == null ? "未连接" : device.IsWifi ? "Wi-Fi 已连接" : "USB 已连接";
            entry.Button.SetResourceReference(BackgroundProperty, store.Profile.Id == profile.Id ? "Theme.Overlay" : "Theme.SurfaceBg");
            ++index;
        }
        var count = _phoneSidebarCount!.Child as TextBlock;
        if (count == null) { count = new TextBlock { FontSize = 11 }; count.SetResourceReference(TextBlock.ForegroundProperty,"Theme.TextSecondary"); _phoneSidebarCount.Child = count; }
        count.Text = profiles.Length.ToString();
    }
    internal async void SelectPhoneManagement()
    {
        await InitializePhoneManagementAsync();
        if (_isClosed) return;
        NavApps.IsChecked = true;
        _phoneDevicePage ??= new PhoneDevicePage();
        PhoneDetailsHost.Child = _phoneDevicePage;
        ShowPhoneDeviceDetails(true);
        AppsListBox.SelectedItem = null;
        ShowPhoneDeviceDetails(true); // selection clearing raises the ordinary app selection handler
        _phoneDevicePage.Activate();
    }
    private void ShowPhoneDeviceDetails(bool show)
    {
        if (!_phoneInitialized) return;
        PhoneDetailsHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AppsDetailsScroll.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show) _phoneDevicePage?.Suspend();
    }
    private void ActivatePhoneManagement()
    {
        if (!_phoneInitialized || !IsVisible || WindowState == WindowState.Minimized || AppsPage.Visibility != Visibility.Visible) return;
        PhoneLibraryChanged();
        if (PhoneDetailsHost.Visibility == Visibility.Visible) _phoneDevicePage?.Activate();
    }
    private void SuspendPhoneManagement() => _phoneDevicePage?.Suspend();
    private async Task RefreshPhoneDevicesAsync()
    {
        if (_isClosed || !_phoneInitialized || PhoneDetailsHost.Visibility != Visibility.Visible) return;
        try
        {
            var devices = await PhoneAudioWindowCache.ReadDevicesAsync(_uiLifetime.Token);
            if (_isClosed) return;
            devices = PhoneAudioWindowCache.Devices ?? devices;
            AndroidAudioHost.Phone.ObserveDevices(devices);
            _ = AndroidAudioHost.ProbeOutputsIfConnectedAsync(devices, force: true, _uiLifetime.Token);
            PhoneLibraryChanged(); _phoneDevicePage?.ApplyDevices(devices);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_isClosed) PhoneDevicesHost.ToolTip = ex.Message; }
    }
    private void ReleasePhoneManagement()
    {
        if (!_phoneInitialized) return;
        AndroidAudioHost.Phone.Changed -= PhoneLibraryChanged;
        AndroidAudioHost.Phone.OutputsChanged -= PhoneLibraryChanged;
        PhoneAudioWindowCache.DevicesChanged -= PhoneDevicesChanged;
        IsVisibleChanged -= PhoneManagementVisibilityChanged; StateChanged -= PhoneManagementStateChanged;
        _phoneDevicePage?.Dispose(); _phoneDevicePage = null; PhoneDetailsHost.Child = null; PhoneDevicesHost.Children.Clear(); _phoneSidebarDevices.Clear();
    }
#else
    private void InitializePhoneManagement() { }
    private Task InitializePhoneManagementAsync() => Task.CompletedTask;
    private void ActivatePhoneManagement() { }
    private void SuspendPhoneManagement() { }
    private void ShowPhoneDeviceDetails(bool show) { }
    private Task RefreshPhoneDevicesAsync() => Task.CompletedTask;
    private void ReleasePhoneManagement() { }
#endif
}

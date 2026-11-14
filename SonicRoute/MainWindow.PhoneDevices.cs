using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
#if SONICROUTE_PHONE
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;
using SonicRoute.AndroidAudio;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
#endif

namespace SonicRoute;

public partial class MainWindow
{
#if SONICROUTE_PHONE
    private StackPanel PhoneDevicesHost = null!;
    private Border PhoneDetailsHost = null!;
    private PhoneDevicePage? _phoneDevicePage;
    private ListBox _phoneDevicesList = null!;
    private System.Windows.Media.ImageSource _phoneSidebarIcon = null!;
    private readonly Dictionary<string, PhoneSidebarItem> _phoneSidebarDevices = new();
    private bool _suppressPhoneSelection;
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
        var heading = new TextBlock { Margin = new Thickness(0,0,8,0), VerticalAlignment = VerticalAlignment.Center };
        BindingOperations.SetBinding(heading, TextBlock.TextProperty, new Binding("[Phone.Title]") { Source = L10n.Instance });
        heading.SetResourceReference(StyleProperty, "SectionTitle");
        var headingGrid = new Grid { Margin = new Thickness(2,0,2,12) }; headingGrid.ColumnDefinitions.Add(new()); headingGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        headingGrid.Children.Add(heading);
        _phoneSidebarCount = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6,3,6,3), VerticalAlignment = VerticalAlignment.Center };
        _phoneSidebarCount.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceAlt");
        var count = new TextBlock { FontSize = 11 }; count.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
        _phoneSidebarCount.Child = count;
        var pen = new System.Windows.Media.Pen { Thickness = 1.5 };
        BindingOperations.SetBinding(pen, System.Windows.Media.Pen.BrushProperty, new Binding(nameof(TextBlock.Foreground)) { Source = count });
        _phoneSidebarIcon = new System.Windows.Media.DrawingImage(new System.Windows.Media.GeometryDrawing(null, pen, AutomationIcons.Phone));
        Grid.SetColumn(_phoneSidebarCount,1); headingGrid.Children.Add(_phoneSidebarCount); PhoneDevicesHost.Children.Add(headingGrid);
        _phoneDevicesList = new ListBox { Name = "PhoneDevicesList", BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent, FontSize = 13, MaxHeight = 220,
            ItemTemplate = (DataTemplate)FindResource("AppsCardItemTemplate"), ItemContainerStyle = (Style)FindResource("RoundedListBoxItem") };
        ScrollViewer.SetHorizontalScrollBarVisibility(_phoneDevicesList, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_phoneDevicesList, true);
        VirtualizingPanel.SetVirtualizationMode(_phoneDevicesList, VirtualizationMode.Recycling);
        _phoneDevicesList.SelectionChanged += PhoneSidebar_SelectionChanged;
        RegisterName(_phoneDevicesList.Name, _phoneDevicesList);
        PhoneDevicesHost.Children.Add(_phoneDevicesList);
        var add = new Button { Height = 26, FontSize = 12, Padding = new Thickness(6,2,6,2), Margin = new Thickness(0,0,0,8) };
        BindingOperations.SetBinding(add, ContentControl.ContentProperty, new Binding("[Phone.AddDeviceButton]") { Source = L10n.Instance });
        add.SetResourceReference(StyleProperty,"QuickPanelToolButton");
        add.SetResourceReference(ForegroundProperty,"Theme.TextPrimary");
        add.Click += (_,_) => { SelectPhoneManagement(); _phoneDevicePage?.ShowConnection(); UpdatePhoneSidebarSelection(); };
        PhoneDevicesHost.Children.Add(add);
        var line = new Border { Height = 1, Margin = new Thickness(2,0,2,8) }; line.SetResourceReference(Border.BackgroundProperty,"Theme.Border"); PhoneDevicesHost.Children.Add(line);
        AndroidAudioHost.Phone.Changed += PhoneLibraryChanged;
        AndroidAudioHost.Phone.OutputsChanged += PhoneLibraryChanged;
        L10n.Instance.PropertyChanged += PhoneLanguageChanged;
        PhoneAudioWindowCache.DevicesChanged += PhoneDevicesChanged;
        IsVisibleChanged += PhoneManagementVisibilityChanged; StateChanged += PhoneManagementStateChanged;
        var cachedDevices = PhoneAudioWindowCache.Devices ?? Array.Empty<AndroidDevice>();
        AndroidAudioHost.Phone.ObserveDevices(cachedDevices);
        _ = AndroidAudioHost.ProbeOutputsIfConnectedAsync(cachedDevices, force: false, _uiLifetime.Token);
        PhoneLibraryChanged();
        if (discover) _ = RefreshPhoneDevicesAsync();
    }
    private sealed class PhoneSidebarItem(string id, System.Windows.Media.ImageSource icon) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public System.Windows.Media.ImageSource Icon { get; } = icon;
        public string Label { get; private set; } = "";
        public string Subtitle { get; private set; } = "";
        public bool IsActive { get; private set; }
        public bool IsMuted => false;
        public bool IsHiddenInPanel => false;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Update(string label, string subtitle, bool active)
        {
            if (Label != label) { Label = label; PropertyChanged?.Invoke(this, new(nameof(Label))); }
            if (Subtitle != subtitle) { Subtitle = subtitle; PropertyChanged?.Invoke(this, new(nameof(Subtitle))); }
            if (IsActive != active) { IsActive = active; PropertyChanged?.Invoke(this, new(nameof(IsActive))); }
        }
    }
    private void PhoneSidebar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPhoneSelection || _phoneDevicesList.SelectedItem is not PhoneSidebarItem item) return;
        AndroidAudioHost.Phone.SelectProfile(item.Id);
        SelectPhoneManagement(); _phoneDevicePage?.ShowDevice(); UpdatePhoneSidebarSelection();
    }
    private void UpdatePhoneSidebarSelection()
    {
        bool suppressed = _suppressPhoneSelection; _suppressPhoneSelection = true;
        try
        {
            _phoneDevicesList.SelectedItem = PhoneDetailsHost.Visibility == Visibility.Visible && _phoneDevicePage?.IsConnectionPage == false
                && _phoneSidebarDevices.TryGetValue(AndroidAudioHost.Phone.Profile.Id, out var selected) ? selected : null;
        }
        finally { _suppressPhoneSelection = suppressed; }
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
        bool selectionSuppressed = _suppressPhoneSelection;
        _suppressPhoneSelection = true;
        try
        {
            foreach (string removed in _phoneSidebarDevices.Keys.Where(id => !ids.Contains(id)).ToArray())
            { _phoneDevicesList.Items.Remove(_phoneSidebarDevices[removed]); _phoneSidebarDevices.Remove(removed); }
            foreach (var profile in profiles)
            {
                if (!_phoneSidebarDevices.TryGetValue(profile.Id, out var entry))
                {
                    entry = new PhoneSidebarItem(profile.Id, _phoneSidebarIcon);
                    _phoneSidebarDevices.Add(profile.Id, entry); _phoneDevicesList.Items.Add(entry);
                }
                var device = PhoneAudioWindowCache.Devices?.FirstOrDefault(d => d.State == "device" && AndroidDeviceIdentity.Matches(d, profile.LastDevice));
                entry.Update(profile.Name, device == null ? L10n.T("Phone.Disconnected") : device.IsWifi ? L10n.T("Phone.WifiConnected") : L10n.T("Phone.UsbConnected"), device != null);
            }
        }
        finally { _suppressPhoneSelection = selectionSuppressed; }
        UpdatePhoneSidebarSelection();
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
        _phoneDevicePage.Activate();
    }
    private void ShowPhoneDeviceDetails(bool show)
    {
        if (!_phoneInitialized) return;
        PhoneDetailsHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AppsDetailsScroll.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        UpdatePhoneSidebarSelection();
        if (!show) _phoneDevicePage?.Suspend();
    }
    private void ActivatePhoneManagement()
    {
        if (!_phoneInitialized || !IsVisible || WindowState == WindowState.Minimized || AppsPage.Visibility != Visibility.Visible) return;
        PhoneLibraryChanged();
        if (PhoneDetailsHost.Visibility == Visibility.Visible) _phoneDevicePage?.Activate();
    }
    private void SuspendPhoneManagement() => _phoneDevicePage?.Suspend();
    private Exception? _phoneRefreshError;
    private void PhoneLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isClosed || !_phoneInitialized) return;
        SyncExternalOutputDevices();
        var store = AndroidAudioHost.Phone;
        foreach (var profile in store.Profiles.Where(p => p.LastDevice != null))
        {
            if (_phoneSidebarDevices.TryGetValue(profile.Id, out var entry))
            {
                var device = PhoneAudioWindowCache.Devices?.FirstOrDefault(d => d.State == "device" && AndroidDeviceIdentity.Matches(d, profile.LastDevice));
                entry.Update(profile.Name, device == null ? L10n.T("Phone.Disconnected") : device.IsWifi ? L10n.T("Phone.WifiConnected") : L10n.T("Phone.UsbConnected"), device != null);
            }
        }
        if (_phoneRefreshError != null) PhoneDevicesHost.ToolTip = AndroidErrorTranslation.Format(_phoneRefreshError);
    }
    private async Task RefreshPhoneDevicesAsync()
    {
        if (_isClosed || !_phoneInitialized || PhoneDetailsHost.Visibility != Visibility.Visible) return;
        try
        {
            if (_phoneDevicePage != null) await _phoneDevicePage.RefreshAsync();
            _phoneRefreshError = null; PhoneDevicesHost.ToolTip = null;
            if (!_isClosed) PhoneLibraryChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_isClosed) { _phoneRefreshError = ex; PhoneDevicesHost.ToolTip = AndroidErrorTranslation.Format(ex); } }
    }
    private void ReleasePhoneManagement()
    {
        if (!_phoneInitialized) return;
        AndroidAudioHost.Phone.Changed -= PhoneLibraryChanged;
        AndroidAudioHost.Phone.OutputsChanged -= PhoneLibraryChanged;
        L10n.Instance.PropertyChanged -= PhoneLanguageChanged;
        PhoneAudioWindowCache.DevicesChanged -= PhoneDevicesChanged;
        IsVisibleChanged -= PhoneManagementVisibilityChanged; StateChanged -= PhoneManagementStateChanged;
        _phoneDevicesList.SelectionChanged -= PhoneSidebar_SelectionChanged;
        _phoneDevicesList.Items.Clear();
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

#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SonicRoute.WindowsLink;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace SonicRoute;

public partial class WindowsLinkConnectView : UserControl
{
    private readonly bool _compact;
    private bool _useDeviceFooter;
    private bool _showing;
    private bool _updating;
    private bool _busy;
    private bool _searchFoundNone;
    private int _refreshQueued;
    private WindowsLinkService? _service;
    private Guid? _activePairingId;
    private string? _errorMessage;
    private string? _busyText;
    private string? _selectedSavedPeerId;
    private readonly Dictionary<Guid, LinkPairing> _pairings = new();

    private sealed record DiscoveredPeerRow(string Id, string DisplayName, string Address, string Subtitle);
    private sealed record SavedAddressOption(string PeerId, string DisplayName, string Address);

    internal event Action? StatusUpdated;

    internal string FooterStatus
    {
        get
        {
            if (_busy && !string.IsNullOrWhiteSpace(_busyText)) return _busyText!;
            if (_service?.IsEnabled != true) return L10n.T("Phone.Disconnected");
            return _pairings.Count > 0 ? L10n.T("Phone.ProcessingStatus") : L10n.T("Phone.WindowsLinkReady");
        }
    }

    internal string? FooterToolTip => !string.IsNullOrWhiteSpace(_errorMessage)
        ? _errorMessage
        : _service?.IsEnabled == true && !string.IsNullOrWhiteSpace(_service.LocalAddress)
            ? string.Format(L10n.T("Phone.WindowsLinkLocalAddress"), _service.LocalAddress)
            : FooterStatus;

    public WindowsLinkConnectView() : this(false) { }

    internal WindowsLinkConnectView(bool compact)
    {
        _compact = compact;
        InitializeComponent();
        HeaderIconPath.Data = AutomationIcons.Monitor;
        ApplyCompactMetrics(compact);

        Loaded += (_, _) => UpdateSubscription(IsVisible);
        Unloaded += (_, _) => UpdateSubscription(false);
        IsVisibleChanged += (_, _) => UpdateSubscription(IsLoaded && IsVisible);
    }

    internal void UseDeviceFooter()
    {
        _useDeviceFooter = true;
        ReceiverHeader.Visibility = Visibility.Collapsed;
        ServiceToggleButton.Visibility = Visibility.Visible;
        ApplyCompactMetrics(true);

        var pad = new Thickness(9, 8, 9, 8);
        var gap = new Thickness(0, 0, 0, 6);
        PairingCard.Padding = DirectCard.Padding = ErrorCard.Padding = pad;
        PairingCard.Margin = DirectCard.Margin = ErrorCard.Margin = gap;

        Grid.SetRow(DirectInputGrid, 0);
        Grid.SetColumn(DirectInputGrid, 0);
        Grid.SetColumnSpan(DirectInputGrid, 2);
        DirectInputGrid.Margin = new Thickness(0, 0, 0, 6);
        AddressBox.Height = 30;
        AddressBox.FontSize = 11.5;

        Grid.SetRow(DirectButtonsGrid, 1);
        Grid.SetColumn(DirectButtonsGrid, 0);
        Grid.SetColumnSpan(DirectButtonsGrid, 2);
        SearchBtnCol.Width = new GridLength(1, GridUnitType.Star);
        ConnectBtnCol.Width = new GridLength(1, GridUnitType.Star);
        SearchButton.Height = ConnectButton.Height = 30;
        SearchButton.FontSize = ConnectButton.FontSize = 11;
        SearchButton.Padding = ConnectButton.Padding = new Thickness(6, 0, 6, 0);
        SearchButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        ConnectButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;

        if (_showing) _ = RefreshAsync();
    }

    private void ApplyCompactMetrics(bool compact)
    {
        var pad = compact ? new Thickness(11, 10, 11, 10) : new Thickness(14, 12, 14, 12);
        var gap = compact ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 0, 10);
        ReceiverHeader.Padding = pad;
        ReceiverHeader.Margin = gap;
        PairingCard.Padding = pad;
        PairingCard.Margin = gap;
        DirectCard.Padding = pad;
        DirectCard.Margin = gap;
        ErrorCard.Padding = pad;
        ErrorCard.Margin = gap;
        DirectTitleText.FontSize = compact ? 12.5 : 13.5;
    }

    private void UpdateSubscription(bool visible)
    {
        bool shouldShow = visible && IsLoaded;
        if (_showing == shouldShow) return;
        _showing = shouldShow;
        if (shouldShow)
        {
            _service ??= WindowsLinkHub.EnsureService();
            _service.Changed += ServiceChanged;
            _service.PairingRequested += PairingRequested;
            _service.PairingEnded += PairingEnded;
            L10n.Instance.PropertyChanged += LanguageChanged;
            _ = RefreshAsync();
        }
        else if (_service != null)
        {
            _service.Changed -= ServiceChanged;
            _service.PairingRequested -= PairingRequested;
            _service.PairingEnded -= PairingEnded;
            L10n.Instance.PropertyChanged -= LanguageChanged;
        }
    }

    private void ServiceChanged() { if (_showing) QueueRefresh(); }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_showing) QueueRefresh();
    }

    private void PairingRequested(LinkPairing pairing)
    {
        if (!_showing) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_showing) return;
            _pairings[pairing.Id] = pairing;
            _ = RefreshAsync();
        }));
    }

    private void PairingEnded(Guid id)
    {
        if (!_showing) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _pairings.Remove(id);
            if (_showing) _ = RefreshAsync();
        }));
    }

    private void QueueRefresh()
    {
        if (!_showing) return;
        if (System.Threading.Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            System.Threading.Interlocked.Exchange(ref _refreshQueued, 0);
            if (_showing) await RefreshAsync();
        }));
    }

    internal Task RefreshAsync()
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync(() => RefreshAsync()).Task.Unwrap();

        _service ??= WindowsLinkHub.EnsureService();
        _pairings.Clear();
        foreach (var pairing in WindowsLinkHub.Pairings) _pairings[pairing.Id] = pairing;

        _updating = true;
        try
        {
            bool enabled = _service.IsEnabled;
            ReceiverHeader.Visibility = _useDeviceFooter ? Visibility.Collapsed : Visibility.Visible;
            ServiceToggleButton.Visibility = _useDeviceFooter ? Visibility.Visible : Visibility.Collapsed;
            ServiceToggle.IsChecked = enabled;
            ServiceToggle.ToolTip = L10n.T(enabled ? "Phone.WindowsLinkDisable" : "Phone.WindowsLinkEnable");
            ServiceToggleButton.Content = L10n.T(enabled ? "Phone.WindowsLinkDisable" : "Phone.WindowsLinkEnable");
            ServiceToggleButton.Tag = enabled ? null : "Start";
            ServiceStatusText.Text = L10n.T(enabled ? "Phone.WindowsLinkReady" : "Phone.Disconnected");
            ServiceStatusText.SetResourceReference(TextBlock.ForegroundProperty, enabled ? "Theme.Accent" : "Theme.TextSecondary");

            string localAddress = _service.LocalAddress;
            if (!string.IsNullOrWhiteSpace(localAddress))
            {
                LocalAddressText.Text = string.Format(L10n.T("Phone.WindowsLinkLocalAddress"), localAddress);
                LocalAddressBadge.Visibility = Visibility.Visible;
            }
            else
            {
                LocalAddressBadge.Visibility = Visibility.Collapsed;
            }

            UpdatePairingCard();
            UpdateSavedAddressCombo(_service.Peers);
            UpdateDiscoveredPeers(_service.Peers);
            UpdateErrorCard();

            SearchHintText.Visibility = _searchFoundNone && DiscoveredPeersList.Visibility != Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _updating = false;
        }

        StatusUpdated?.Invoke();
        return Task.CompletedTask;
    }

    private void UpdatePairingCard()
    {
        var pairing = _pairings.Values.FirstOrDefault();
        if (pairing == null)
        {
            _activePairingId = null;
            PairingCard.Visibility = Visibility.Collapsed;
            return;
        }

        _activePairingId = pairing.Id;
        PairingPromptText.Text = string.Format(L10n.T("Phone.WindowsLinkPairPrompt"), pairing.Name);
        PairingCodeText.Text = FormatPairingCode(pairing.Code);
        PairingCard.Visibility = Visibility.Visible;
    }

    private static string FormatPairingCode(string code)
    {
        string trimmed = code?.Trim() ?? "";
        return trimmed.Length == 8 ? $"{trimmed[..4]} {trimmed[4..]}" : trimmed;
    }

    private void UpdateSavedAddressCombo(LinkPeer[] peers)
    {
        var options = peers
            .Where(p => !string.IsNullOrWhiteSpace(p.Address))
            .Select(p => new SavedAddressOption(p.Id, $"{p.DisplayName} ({p.Address})", p.Address!))
            .ToArray();

        if (options.Length == 0)
        {
            SavedAddressCombo.Visibility = Visibility.Collapsed;
            SavedAddressCombo.ItemsSource = null;
            return;
        }

        string currentAddress = AddressBox.Text.Trim();
        SavedAddressCombo.ItemsSource = options;
        SavedAddressCombo.DisplayMemberPath = nameof(SavedAddressOption.DisplayName);
        SavedAddressCombo.SelectedIndex = Array.FindIndex(options,
            o => string.Equals(o.Address, currentAddress, StringComparison.OrdinalIgnoreCase));
        SavedAddressCombo.Visibility = Visibility.Visible;
    }

    private void UpdateDiscoveredPeers(LinkPeer[] peers)
    {
        var rows = peers
            .Where(p => !p.Trusted)
            .Select(p =>
            {
                string state = p.Online ? L10n.T("Phone.WindowsLinkReady") : L10n.T("Phone.Disconnected");
                string subtitle = string.IsNullOrWhiteSpace(p.Address) ? state : $"{state} · {p.Address}";
                return new DiscoveredPeerRow(p.Id, p.DisplayName, p.Address ?? "", subtitle);
            })
            .ToArray();

        if (rows.Length == 0)
        {
            DiscoveredPeersList.ItemsSource = null;
            DiscoveredPeersList.Visibility = Visibility.Collapsed;
            return;
        }

        _searchFoundNone = false;
        DiscoveredPeersList.ItemsSource = rows;
        DiscoveredPeersList.Visibility = Visibility.Visible;
    }

    private void UpdateErrorCard()
    {
        if (string.IsNullOrWhiteSpace(_errorMessage))
        {
            ErrorCard.Visibility = Visibility.Collapsed;
            ErrorText.Text = "";
        }
        else
        {
            ErrorText.Text = _errorMessage;
            ErrorCard.Visibility = Visibility.Visible;
        }
    }

    private async void ServiceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        await ToggleServiceAsync(ServiceToggle.IsChecked == true);
    }

    private async void ServiceToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool wantEnable = _service?.IsEnabled != true;
        await ToggleServiceAsync(wantEnable);
    }

    private Task ToggleServiceAsync(bool wantEnable)
    {
        _service ??= WindowsLinkHub.EnsureService();
        _searchFoundNone = false;
        return RunActionAsync(L10n.T("Phone.ProcessingStatus"), async () =>
        {
            if (wantEnable) await _service.EnableAsync();
            else await _service.DisableAsync();
        });
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        _service ??= WindowsLinkHub.EnsureService();
        _searchFoundNone = false;
        await RunActionAsync(L10n.T("Phone.ProcessingStatus"), async () =>
        {
            if (!_service.IsEnabled) await _service.EnableAsync();
            await _service.DiscoverAsync();
            if (!_service.Peers.Any(p => !p.Trusted)) _searchFoundNone = true;
        });
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectToInputAsync();

    private async void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ConnectToInputAsync();
    }

    private Task ConnectToInputAsync()
    {
        string address = AddressBox.Text.Trim();
        if (address.Length == 0)
        {
            _errorMessage = L10n.T("Phone.Err.InvalidAddress");
            return RefreshAsync();
        }

        _service ??= WindowsLinkHub.EnsureService();
        _searchFoundNone = false;
        string? peerId = _selectedSavedPeerId;
        if (peerId != null && !_service.Peers.Any(p =>
                string.Equals(p.Id, peerId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Address, address, StringComparison.OrdinalIgnoreCase)))
        {
            peerId = null;
        }

        return RunActionAsync(L10n.T("Phone.ProcessingStatus"), async () =>
        {
            if (!_service.IsEnabled) await _service.EnableAsync();
            await _service.ConnectAsync(address, peerId);
        });
    }

    private async void ConnectDiscovered_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DiscoveredPeerRow row }) return;
        _service ??= WindowsLinkHub.EnsureService();
        await RunActionAsync(L10n.T("Phone.ProcessingStatus"), async () =>
        {
            if (!_service.IsEnabled) await _service.EnableAsync();
            await _service.ConnectAsync(row.Address, row.Id);
        });
    }

    private void AcceptPairing_Click(object sender, RoutedEventArgs e)
    {
        if (_activePairingId is not { } id || _service == null) return;
        _service.AcceptPairing(id, true);
        _pairings.Remove(id);
        _ = RefreshAsync();
    }

    private void DeclinePairing_Click(object sender, RoutedEventArgs e)
    {
        if (_activePairingId is not { } id || _service == null) return;
        _service.AcceptPairing(id, false);
        _pairings.Remove(id);
        _ = RefreshAsync();
    }

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AddressWatermark.Visibility = string.IsNullOrEmpty(AddressBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SavedAddressCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || SavedAddressCombo.SelectedItem is not SavedAddressOption option) return;
        _selectedSavedPeerId = option.PeerId;
        AddressBox.Text = option.Address;
    }

    private void LocalAddressBadge_Click(object sender, MouseButtonEventArgs e)
    {
        if (_service == null || string.IsNullOrWhiteSpace(_service.LocalAddress)) return;
        try { Clipboard.SetText(_service.LocalAddress); } catch { }
    }

    private async Task RunActionAsync(string busyText, Func<Task> action)
    {
        _busy = true;
        _busyText = busyText;
        _errorMessage = null;
        SearchButton.IsEnabled = false;
        ConnectButton.IsEnabled = false;
        ServiceToggle.IsEnabled = false;
        ServiceToggleButton.IsEnabled = false;
        StatusUpdated?.Invoke();
        try
        {
            await action();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
        }
        finally
        {
            _busy = false;
            _busyText = null;
            SearchButton.IsEnabled = true;
            ConnectButton.IsEnabled = true;
            ServiceToggle.IsEnabled = true;
            ServiceToggleButton.IsEnabled = true;
        }

        await RefreshAsync();
    }
}
#endif

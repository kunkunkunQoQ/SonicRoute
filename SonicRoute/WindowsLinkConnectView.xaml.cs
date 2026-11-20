#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
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
    private bool _showing;
    private bool _updating;
    private bool _busy;
    private bool _searchFoundNone;
    private int _refreshQueued;
    private WindowsLinkService? _service;
    private IDisposable? _discoveryObserver;
    private CancellationTokenSource? _viewCts;
    private Guid? _activePairingId;
    private string? _errorMessage;
    private string? _busyText;
    private PeerRow[] _peerRows = [];
    private readonly Dictionary<Guid, LinkPairing> _pairings = new();

    private sealed record PeerRow(
        string Id,
        string DisplayName,
        string Address,
        string Status,
        string Subtitle,
        string Failure,
        Visibility FailureVisibility,
        bool CanConnect,
        Visibility ConnectVisibility,
        Visibility OpenVisibility,
        bool Trusted);

    internal event Action? StatusUpdated;
    internal event Action<string>? PeerOpened;

    internal string FooterStatus
    {
        get
        {
            if (_busy && !string.IsNullOrWhiteSpace(_busyText)) return _busyText!;
            if (_service?.IsEnabled != true) return L10n.T("Phone.WindowsLinkOff");
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
        InitializeComponent();
        HeaderSubtitleText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ApplyCompactMetrics(compact);

        Loaded += (_, _) => UpdateSubscription(IsVisible);
        Unloaded += (_, _) => UpdateSubscription(false);
        IsVisibleChanged += (_, _) => UpdateSubscription(IsLoaded && IsVisible);
    }

    internal void UseDeviceFooter() => HeaderStatus.Visibility = Visibility.Collapsed;

    private void DirectForm_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DirectForm == null || ConnectButton == null || DirectForm.ActualWidth <= 0) return;
        bool stack = DirectForm.ActualWidth - Math.Max(46, ConnectButton.DesiredSize.Width) < 114;
        int row = stack ? 1 : 0;
        if (Grid.GetRow(DirectButtonsGrid) == row) return;
        Grid.SetRow(DirectButtonsGrid, row);
        Grid.SetColumn(DirectButtonsGrid, stack ? 0 : 1);
        Grid.SetColumnSpan(DirectButtonsGrid, stack ? 2 : 1);
        Grid.SetColumnSpan(DirectInputGrid, stack ? 2 : 1);
        DirectInputGrid.Margin = stack ? new Thickness(0, 0, 0, 5) : new Thickness(0, 0, 6, 0);
    }

    private void ApplyCompactMetrics(bool compact)
    {
        var pad = compact ? new Thickness(10, 9, 10, 9) : new Thickness(14, 12, 14, 12);
        var gap = compact ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 0, 10);
        ReceiverHeader.Padding = pad;
        ReceiverHeader.Margin = gap;
        PairingCard.Padding = pad;
        PairingCard.Margin = gap;
        DevicesCard.Padding = pad;
        DevicesCard.Margin = gap;
        DirectCard.Padding = pad;
        DirectCard.Margin = gap;
        ErrorCard.Padding = pad;
        ErrorCard.Margin = gap;
    }

    private void UpdateSubscription(bool visible)
    {
        bool shouldShow = visible && IsLoaded;
        if (_showing == shouldShow) return;
        _showing = shouldShow;
        if (shouldShow)
        {
            _service ??= WindowsLinkHub.EnsureService();
            _viewCts?.Dispose();
            _viewCts = new CancellationTokenSource();
            _discoveryObserver?.Dispose();
            _discoveryObserver = _service.ObserveDiscovery();
            WindowsLinkHub.Changed += ServiceChanged;
            _service.PairingRequested += PairingRequested;
            _service.PairingEnded += PairingEnded;
            L10n.Instance.PropertyChanged += LanguageChanged;
            _ = RefreshAsync();
        }
        else
        {
            _viewCts?.Cancel();
            _viewCts?.Dispose();
            _viewCts = null;
            _discoveryObserver?.Dispose();
            _discoveryObserver = null;
            WindowsLinkHub.Changed -= ServiceChanged;
            if (_service != null)
            {
                _service.PairingRequested -= PairingRequested;
                _service.PairingEnded -= PairingEnded;
            }
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
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
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
            ServiceToggle.IsChecked = enabled;
            string action = L10n.T(enabled ? "Phone.WindowsLinkDisable" : "Phone.WindowsLinkEnable");
            ServiceToggle.ToolTip = action;
            System.Windows.Automation.AutomationProperties.SetName(ServiceToggle, action);
            ServiceStatusText.Text = _busy ? _busyText : L10n.T(enabled ? "Phone.WindowsLinkReady" : "Phone.WindowsLinkOff");
            ServiceStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, enabled ? "Theme.Success" : "Theme.TextSecondary");
            ConnectButton.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(AddressBox.Text);

            string localAddress = _service.LocalAddress;
            if (!string.IsNullOrWhiteSpace(localAddress))
            {
                LocalAddressText.Text = string.Format(L10n.T("Phone.WindowsLinkLocalAddress"), localAddress);
                LocalAddressBadge.ToolTip = L10n.T("Phone.WindowsLinkAddressMenu");
                System.Windows.Automation.AutomationProperties.SetName(LocalAddressBadge,
                    string.Format(L10n.T("Phone.WindowsLinkCopyAddress"), localAddress));
                LocalAddressBadge.Visibility = Visibility.Visible;
            }
            else
            {
                LocalAddressBadge.Visibility = Visibility.Collapsed;
            }

            LinkPeer[] peers = _service.Peers;
            UpdatePairingCard();
            UpdatePeerRows(peers);
            UpdateErrorCard();

            SearchHintText.Visibility = _searchFoundNone && !peers.Any(p => !p.Trusted)
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

    private void UpdatePeerRows(LinkPeer[] peers)
    {
        if (peers.Any(p => !p.Trusted)) _searchFoundNone = false;

        var rows = peers
            .Select(p =>
            {
                string subtitle = WindowsLinkPeerStatus.Subtitle(p);
                string failure = p.State == LinkPeerState.Failed ? WindowsLinkPeerStatus.FailureText(p.Failure) : "";
                bool canConnect = !_busy
                    && p.State != LinkPeerState.Connecting
                    && (p.Trusted || !string.IsNullOrWhiteSpace(p.Address));
                return new PeerRow(
                    p.Id,
                    p.DisplayName,
                    p.Address ?? "",
                    WindowsLinkPeerStatus.StateWithPairing(p),
                    subtitle,
                    failure,
                    string.IsNullOrEmpty(failure) ? Visibility.Collapsed : Visibility.Visible,
                    canConnect,
                    p.Connected ? Visibility.Collapsed : Visibility.Visible,
                    p.Connected ? Visibility.Visible : Visibility.Collapsed,
                    p.Trusted);
            })
            .ToArray();

        if (rows.Length == 0)
        {
            _peerRows = [];
            DiscoveredPeersList.ItemsSource = null;
            DiscoveredPeersList.Visibility = Visibility.Collapsed;
            return;
        }

        if (!_peerRows.SequenceEqual(rows))
        {
            _peerRows = rows;
            DiscoveredPeersList.ItemsSource = rows;
        }
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

    private async void ServiceToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || _busy || ServiceToggle == null) return;
        await ToggleServiceAsync(ServiceToggle.IsChecked == true);
    }

    private Task ToggleServiceAsync(bool wantEnable)
    {
        _service ??= WindowsLinkHub.EnsureService();
        _searchFoundNone = false;
        return RunActionAsync(L10n.T("Phone.ProcessingStatus"),
            token => App.SetWindowsLinkEnabledAsync(_service, wantEnable, token));
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await TriggerSearchAsync();

    internal Task TriggerSearchAsync(CancellationToken token = default)
    {
        _service ??= WindowsLinkHub.EnsureService();
        _searchFoundNone = false;
        return RunActionAsync(L10n.T("Phone.ProcessingStatus"), async viewToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, viewToken);
            if (!_service.IsEnabled)
                await App.SetWindowsLinkEnabledAsync(_service, true, linked.Token);
            await _service.SearchAsync(linked.Token);
            if (!linked.IsCancellationRequested && !_service.Peers.Any(p => !p.Trusted))
                _searchFoundNone = true;
        });
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectToInputAsync();

    private async void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (ConnectButton.IsEnabled) await ConnectToInputAsync();
    }

    private Task ConnectToInputAsync()
    {
        if (_busy) return Task.CompletedTask;
        string address = AddressBox.Text.Trim();
        if (!LinkDirectAddress.TryParse(address, out _))
        {
            ShowDirectNotice(L10n.T("Phone.WindowsLinkDirectDesc"));
            return Task.CompletedTask;
        }

        _service ??= WindowsLinkHub.EnsureService();
        _searchFoundNone = false;
        return RunActionAsync(L10n.T("Phone.ProcessingStatus"), async token =>
        {
            LinkResolvedAddress resolved;
            try { resolved = await _service.ResolveDirectAddressAsync(address, token); }
            catch (LinkSelfConnectionException) { _errorMessage = L10n.T("Phone.WindowsLinkSelfAddress"); return; }
            catch (System.Net.Sockets.SocketException) { _errorMessage = L10n.T("Phone.WindowsLinkNameUnavailable"); return; }
            if (resolved.ConnectedPeer != null) { ShowDirectNotice(L10n.T("Phone.WindowsLinkAlreadyConnected")); return; }
            if (!_service.IsEnabled)
                await App.SetWindowsLinkEnabledAsync(_service, true, token);
            await _service.ConnectResolvedAsync(resolved, null, token);
        }, direct: true);
    }

    private void OpenPeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PeerRow row } && _service?.Peers.Any(p => p.Id == row.Id && p.Connected) == true)
            PeerOpened?.Invoke(row.Id);
    }

    private async void ConnectPeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PeerRow row }) return;
        _service ??= WindowsLinkHub.EnsureService();
        _searchFoundNone = false;
        await RunActionAsync(L10n.T("Phone.ProcessingStatus"), async token =>
        {
            if (!_service.IsEnabled)
                await App.SetWindowsLinkEnabledAsync(_service, true, token);
            if (row.Trusted)
                await WindowsLinkHub.ReconnectPeerAsync(row.Id, token);
            else
                await _service.ConnectAsync(row.Address, row.Id, token);
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

    private void ShowDirectNotice(string? text)
    {
        if (DirectNoticeText == null) return;
        DirectNoticeText.Text = text ?? "";
        DirectNoticeText.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ShowDirectNotice(null);
        if (AddressWatermark != null) AddressWatermark.Visibility = string.IsNullOrEmpty(AddressBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (ConnectButton != null) ConnectButton.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(AddressBox.Text);
    }

    private async void LocalAddressBadge_Click(object sender, RoutedEventArgs e)
    {
        if (_service is not { IsEnabled: true } service || !LocalAddressBadge.IsEnabled) return;
        LocalAddressBadge.IsEnabled = false;
        try
        {
            var choices = await Task.Run(service.GetLocalAddresses);
            if (!IsVisible || !service.IsEnabled || choices.Length == 0) return;
            if (choices.Length == 1) { await CopyAddressAsync(choices[0].Address); return; }
            var menu = ConvenienceMenus.Create();
            menu.PlacementTarget = LocalAddressBadge;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            foreach (var choice in choices)
            {
                string label = string.IsNullOrEmpty(choice.Adapter) ? choice.Address : $"{choice.Adapter}  ·  {choice.Address}";
                var item = ConvenienceMenus.Item(label);
                item.Click += async (_, _) => await CopyAddressAsync(choice.Address);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { await CopyAddressAsync(service.LocalAddress); }
        finally { LocalAddressBadge.IsEnabled = true; }

        async Task CopyAddressAsync(string address)
        {
            if (address.Length == 0) return;
            bool copied = await ClipboardWriter.TryWriteTextAsync(address);
            if (IsVisible && service.IsEnabled)
                ShowDirectNotice(copied ? string.Format(L10n.T("Phone.WindowsLinkAddressCopied"), address) : L10n.T("Auto.CopyFailed"));
        }
    }

    private async Task RunActionAsync(string busyText, Func<CancellationToken, Task> action, bool direct = false)
    {
        if (_busy) return;
        _busy = true;
        _busyText = busyText;
        _errorMessage = null;
        ShowDirectNotice(null);
        SearchButton.IsEnabled = false;
        ConnectButton.IsEnabled = false;
        ServiceToggle.IsEnabled = false;
        ServiceStatusText.Text = busyText;
        if (_service != null) UpdatePeerRows(_service.Peers);
        StatusUpdated?.Invoke();
        CancellationToken viewToken = _viewCts?.Token ?? CancellationToken.None;
        try
        {
            await action(viewToken);
        }
        catch (OperationCanceledException) { }
        catch (LinkAmbiguousAddressException) { _errorMessage = L10n.T("Phone.WindowsLinkAmbiguousAddress"); }
        catch (LinkConnectionFailureException ex) { _errorMessage = WindowsLinkPeerStatus.FailureText(ex.Reason); }
        catch (LinkAuthenticationFailureException ex) { _errorMessage = WindowsLinkPeerStatus.FailureText(ex.Reason); }
        catch (System.Security.Authentication.AuthenticationException)
        { _errorMessage = WindowsLinkPeerStatus.FailureText(LinkConnectFailure.IdentityMismatch); }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
        }
        finally
        {
            _busy = false;
            _busyText = null;
            SearchButton.IsEnabled = true;
            ConnectButton.IsEnabled = !string.IsNullOrWhiteSpace(AddressBox.Text);
            ServiceToggle.IsEnabled = true;
        }

        if (direct && _errorMessage != null)
        {
            ShowDirectNotice(_errorMessage);
            _errorMessage = null;
        }
        await RefreshAsync();
    }
}
#endif

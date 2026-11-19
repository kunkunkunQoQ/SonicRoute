#if SONICROUTE_PHONE
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    private WindowsLinkConnectView? _windowsLinkView;
    internal string? SelectedWindowsLinkPeer { get; private set; }

    private void ConnectionChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (AirPlayCard == null || WindowsLinkCard == null || DirectConnectCard == null) return;
        UpdateConnectionChoice();
    }

    private void UpdateConnectionChoice()
    {
        bool airPlay = AirPlayConnectionChoice.IsChecked == true;
        bool windows = WindowsLinkConnectionChoice.IsChecked == true;
        bool connection = IsConnectionPage;
        PairingCard.Visibility = DirectConnectCard.Visibility = connection && !airPlay && !windows ? Visibility.Visible : Visibility.Collapsed;
        AirPlayCard.Visibility = connection && airPlay ? Visibility.Visible : Visibility.Collapsed;
        WindowsLinkCard.Visibility = connection && windows ? Visibility.Visible : Visibility.Collapsed;
        ConnectionChoices.Visibility = SelectedWindowsLinkPeer == null ? Visibility.Visible : Visibility.Collapsed;
        if (connection && windows)
        {
            _operation?.Cancel();
            _windowsLinkView ??= new WindowsLinkConnectView();
            WindowsLinkCard.Content = _windowsLinkView;
            _ = _windowsLinkView.RefreshAsync();
        }
        UpdateQrPairing();
    }

    internal void ShowWindowsLinkPeer(string id)
    {
        SelectedWindowsLinkPeer = id;
        _showConnection = false;
        ReleaseCatalog();
        _operation?.Cancel();
        PhoneNameBox.Visibility = Visibility.Collapsed;
        ProfileChanged();
        DeviceScroll.ScrollToTop();
    }

    private async Task RefreshWindowsLinkAsync()
    {
        if (SelectedWindowsLinkPeer != null)
        {
            await ReconnectWindowsLinkPeerAsync();
            return;
        }
        if (_windowsLinkView == null || WindowsLinkHub.Service is not { } service) return;
        try
        {
            if (service.IsEnabled) await service.DiscoverAsync(_lifetime.Token);
            await _windowsLinkView.RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetOperationStatusError(ex); }
    }
}
#endif

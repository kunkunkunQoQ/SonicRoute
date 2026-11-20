#if SONICROUTE_PHONE
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    private WindowsLinkConnectView? _windowsLinkView;
    internal event Action? PeerOpenedFromList;
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
            if (_windowsLinkView == null)
            {
                _windowsLinkView = new WindowsLinkConnectView();
                _windowsLinkView.PeerOpened += id =>
                {
                    ShowWindowsLinkPeer(id);
                    PeerOpenedFromList?.Invoke();
                };
            }
            WindowsLinkCard.Content = _windowsLinkView;
            _ = _windowsLinkView.RefreshAsync();
        }
        UpdateQrPairing();
    }

    internal void ShowWindowsLinkPeer(string id)
    {
        ClearWindowsRemovePrompt();
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
            if (WindowsLinkHub.Service?.Peers.Any(peer => peer.Id == SelectedWindowsLinkPeer && peer.Connected) == true)
                await LoadWindowsAudioAppsAsync(SelectedWindowsLinkPeer, true);
            else
                await ReconnectWindowsLinkPeerAsync();
            return;
        }
        if (_windowsLinkView == null) return;
        try
        {
            if (WindowsLinkHub.Service is { IsEnabled: true })
                await _windowsLinkView.TriggerSearchAsync(_lifetime.Token);
            else
                await _windowsLinkView.RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetOperationStatusError(ex); }
    }
}
#endif

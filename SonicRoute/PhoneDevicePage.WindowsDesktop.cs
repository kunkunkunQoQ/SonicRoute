#if SONICROUTE_PHONE
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SonicRoute.WindowsLink;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    private string? _desktopPeerId, _desktopStatusKey, _desktopError;
    private CancellationTokenSource? _desktopOpening;

    private static string? DesktopUnavailable(LinkPeer peer) => !peer.Connected ? "Phone.WindowsDesktopOffline"
        : !peer.Capabilities.HasFlag(LinkCapabilities.RemoteBrowse | LinkCapabilities.MonitorCapture) ? "Phone.WindowsDesktopUpgrade" : null;

    private void ResetWindowsDesktop()
    {
        var opening = _desktopOpening;
        _desktopOpening = null;
        _desktopPeerId = _desktopStatusKey = _desktopError = null;
        opening?.Cancel();
    }

    private void UpdateWindowsDesktop(LinkPeer? peer)
    {
        if (_desktopPeerId != peer?.Id || peer is { Connected: false }) ResetWindowsDesktop();
        _desktopPeerId = peer?.Id;
        WindowsDesktopRow.Visibility = peer == null ? Visibility.Collapsed : Visibility.Visible;
        if (peer == null) return;
        string? unavailable = DesktopUnavailable(peer);
        WindowsDesktopOpen.IsEnabled = unavailable == null && _desktopOpening == null && !_busy;
        string? key = unavailable ?? (_desktopOpening != null ? "Phone.WindowsDesktopOpening" : _desktopStatusKey);
        WindowsDesktopStatus.Text = key == null ? _desktopError ?? "" : L10n.T(key);
    }

    private async void WindowsDesktopOpen_Click(object sender, RoutedEventArgs e) => await OpenWindowsDesktopAsync();

    private async Task OpenWindowsDesktopAsync()
    {
        var service = WindowsLinkHub.Service;
        var peer = service?.Peers.FirstOrDefault(p => p.Id == SelectedWindowsLinkPeer);
        if (_disposed || !_active || _busy || _desktopOpening != null || service == null || peer == null || DesktopUnavailable(peer) != null) return;
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _desktopOpening = opening;
        _desktopStatusKey = _desktopError = null;
        UpdateWindowsDesktop(peer);
        try
        {
            var catalog = await service.RequestCatalogAsync(peer.Id, includeIcons: false, opening.Token, windowsOnly: true, refresh: true);
            opening.Token.ThrowIfCancellationRequested();
            var monitor = catalog.Where(app => app.IsMonitor).OrderByDescending(app => app.Display!.IsPrimary)
                .ThenBy(app => app.Display!.Number).FirstOrDefault();
            if (monitor == null) { _desktopStatusKey = "Phone.WindowsDesktopNoDisplay"; return; }
            var position = WindowsDesktopOpen.PointToScreen(new System.Windows.Point(WindowsDesktopOpen.ActualWidth, WindowsDesktopOpen.ActualHeight));
            var (_, audioAvailable) = await WindowsLinkHub.OpenDisplayNearAsync(new WindowsLinkAppDrag(peer, monitor, Window: true),
                (int)position.X, (int)position.Y, opening.Token);
            if (!audioAvailable && !opening.IsCancellationRequested) _desktopStatusKey = "Phone.WindowsLinkVideoAudioUnavailable";
        }
        catch (OperationCanceledException) when (opening.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!opening.IsCancellationRequested) _desktopError = AndroidErrorTranslation.Format(ex);
        }
        finally
        {
            if (ReferenceEquals(_desktopOpening, opening))
            {
                _desktopOpening = null;
                if (!_disposed) UpdateWindowsDesktop(service.Peers.FirstOrDefault(p => p.Id == SelectedWindowsLinkPeer));
            }
        }
    }
}
#endif

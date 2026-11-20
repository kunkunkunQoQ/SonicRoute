#if SONICROUTE_PHONE
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using SonicRoute.WindowsLink;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    private string? _trustPeer, _trustChallenge, _trustError, _trustBusyPeer;

    private void WindowsFullTrustChanged(string peerId)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => WindowsFullTrustChanged(peerId)));
            return;
        }
        if (!_active || _disposed || !string.Equals(SelectedWindowsLinkPeer, peerId, StringComparison.OrdinalIgnoreCase)) return;
        if (WindowsLinkHub.Service is { } service) RefreshSelectedWindowsTrust(service, peerId);
    }

    private void RefreshSelectedWindowsTrust(WindowsLinkService service, string peerId)
    {
        var peer = service.Peers.FirstOrDefault(p => string.Equals(p.Id, peerId, StringComparison.OrdinalIgnoreCase));
        if (peer != null) RefreshWindowsTrust(service, peer);
    }

    private void RefreshWindowsTrust(WindowsLinkService service, LinkPeer peer)
    {
        ApplyWindowsTrust(peer, service.GetFullTrustState(peer.Id));
        bool fullAccess = service.IsFullyTrusted(peer.Id);
        _suppressWindowsLinkInput = true;
        try
        {
            WindowsLinkAllowInputSwitch.IsChecked = fullAccess || service.GetPeerSettings(peer.Id).AllowRemoteInput;
            WindowsLinkAllowInputSwitch.IsEnabled = !_busy && !fullAccess;
        }
        finally
        {
            _suppressWindowsLinkInput = false;
        }
    }

    private void ApplyWindowsTrust(LinkPeer peer, LinkFullTrustState state)
    {
        if (_trustPeer != peer.Id || _trustChallenge != state.LocalCode)
        {
            _trustPeer = peer.Id;
            _trustChallenge = state.LocalCode;
            WindowsTrustPeerCode.Clear();
            _trustError = null;
        }
        bool entering = !state.Enabled && state.LocalCode != null;
        bool busy = _trustBusyPeer != null;
        string hint = peer.Connected && !state.Supported ? "Phone.LinkTrustUpdate"
            : state.Enabled ? "Phone.LinkTrustEnabled"
            : state.Submitted ? "Phone.LinkTrustWaiting"
            : entering && state.PeerCodeReady ? "Phone.LinkTrustExchange"
            : entering ? "Phone.LinkTrustAskPeer"
            : state.PeerCodeReady ? "Phone.LinkTrustInvite"
            : "Phone.LinkTrustIntro";
        WindowsTrustStatus.Text = _trustError ?? L10n.T(hint);
        WindowsTrustAction.Content = L10n.T(state.Enabled ? "Phone.LinkTrustRevoke" : entering ? "Auto.Cancel" : "Phone.LinkTrustGenerate");
        WindowsTrustAction.IsEnabled = !busy && (state.Enabled || peer.Connected && state.Supported);
        WindowsTrustCodes.Visibility = entering ? Visibility.Visible : Visibility.Collapsed;
        WindowsTrustLocal.Text = state.LocalCode ?? "";
        WindowsTrustPeerCode.IsEnabled = entering && state.PeerCodeReady && !state.Submitted && !busy;
        WindowsTrustVerify.IsEnabled = WindowsTrustPeerCode.IsEnabled && ValidTrustCode(WindowsTrustPeerCode.Text);
    }

    private static bool ValidTrustCode(string text) => text.Length == 8 && text.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0;

    private void WindowsTrustCode_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (WindowsTrustVerify == null) return;
        WindowsTrustVerify.IsEnabled = WindowsTrustPeerCode.IsEnabled && ValidTrustCode(WindowsTrustPeerCode.Text);
    }

    private void WindowsTrustCode_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !WindowsTrustVerify.IsEnabled) return;
        e.Handled = true;
        WindowsTrustVerify_Click(sender, e);
    }

    private async void WindowsTrustVerify_Click(object sender, RoutedEventArgs e)
    {
        if (!WindowsTrustVerify.IsEnabled || SelectedWindowsLinkPeer is not { } id || _trustBusyPeer != null
            || WindowsLinkHub.Service is not { } service) return;
        string code = WindowsTrustPeerCode.Text;
        string? challenge = _trustChallenge;
        _trustBusyPeer = id;
        _trustError = null;
        WindowsTrustAction.IsEnabled = WindowsTrustVerify.IsEnabled = WindowsTrustPeerCode.IsEnabled = false;
        try
        {
            bool accepted = await service.SubmitFullTrustCodeAsync(id, code, _lifetime.Token);
            if (SelectedWindowsLinkPeer == id && _trustChallenge == challenge)
            {
                WindowsTrustPeerCode.Clear();
                if (!accepted) _trustError = L10n.T("Phone.LinkTrustIncorrect");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (SelectedWindowsLinkPeer == id) _trustError = L10n.T("Phone.MediaActionIncompleteRetry");
        }
        finally { FinishTrustAction(service); }
    }

    private async void WindowsTrustAction_Click(object sender, RoutedEventArgs e)
    {
        if (!WindowsTrustAction.IsEnabled || SelectedWindowsLinkPeer is not { } id || _trustBusyPeer != null
            || WindowsLinkHub.Service is not { } service) return;
        var state = service.GetFullTrustState(id);
        _trustBusyPeer = id;
        _trustError = null;
        WindowsTrustAction.IsEnabled = WindowsTrustVerify.IsEnabled = false;
        try
        {
            if (state.Enabled || state.LocalCode != null) await service.RevokeFullTrustAsync(id, _lifetime.Token);
            else await service.BeginFullTrustAsync(id, _lifetime.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException or NotSupportedException)
        {
            if (SelectedWindowsLinkPeer == id) _trustError = L10n.T("Phone.MediaActionIncompleteRetry");
        }
        finally { FinishTrustAction(service); }
    }

    private void FinishTrustAction(WindowsLinkService service)
    {
        _trustBusyPeer = null;
        if (!_disposed && SelectedWindowsLinkPeer is { } peerId && WindowsLinkHub.Service == service)
            RefreshSelectedWindowsTrust(service, peerId);
    }

    private void WindowsReceiveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        bool android = SelectedWindowsLinkPeer == null;
        var service = WindowsLinkHub.Service;
        if (!android && service == null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L10n.T("Phone.LinkFileReceiveFolder"),
            InitialDirectory = android ? AndroidReceiveFolder.GetReceiveDirectory() : service!.GetReceiveDirectory(),
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            if (android) AndroidReceiveFolder.SetReceiveDirectory(dialog.FolderName);
            else service!.SetReceiveDirectory(dialog.FolderName);
            WindowsReceiveFolderPath.Text = dialog.FolderName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), ex.Message, L10n.T("Phone.LinkFileReceiveFolder"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
#endif

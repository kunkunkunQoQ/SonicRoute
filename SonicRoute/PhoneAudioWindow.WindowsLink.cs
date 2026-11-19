#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SonicRoute.WindowsLink;

namespace SonicRoute;

public partial class PhoneAudioWindow
{
    internal sealed class WindowsLinkQuickItem
    {
        public LinkRemoteApp App { get; }
        public bool Window { get; }
        public string Name => Window ? App.WindowTitle ?? App.Name : App.Name;
        public string Initial => string.IsNullOrEmpty(Name) ? "♪" : Name[..1];
        private ImageSource? _icon;
        private bool _iconDecoded;

        public ImageSource? Icon
        {
            get
            {
                if (!Volatile.Read(ref _iconDecoded))
                {
                    _icon = AppIconService.DecodePng(App.IconPng);
                    Volatile.Write(ref _iconDecoded, true);
                }
                return _icon;
            }
        }

        public string Description => App.ProcessName;
        public bool IsPlaying { get; }

        internal WindowsLinkQuickItem(LinkRemoteApp app, bool isPlaying, bool window = false)
        {
            App = app;
            Window = window;
            IsPlaying = isPlaying;
        }

        internal bool Matches(string query) => query.Length == 0
            || Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || App.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private string? _windowsLinkRowPeerId;
    private string? _windowsLinkError;
    private bool _windowsActionPending;
    private CancellationTokenSource? _windowsCatalogRead;
    private LinkRemoteApp[]? _windowsRemoteAudioApps;
    private LinkRemoteApp[]? _windowsRemoteWindows;
    private WindowsLinkQuickItem[] _windowsQuickItems = [];
    private bool WindowsLinkShowsWindows => CurrentWindowsPeer != null && WindowsLinkWindowChoice?.IsChecked == true;
    private bool WindowsLinkCanReturn(WindowsLinkAppDrag drag) =>
        CurrentWindowsPeer?.Id == drag.Peer.Id && WindowsLinkShowsWindows == drag.Window;

    private void WindowsLinkView_Changed(object sender, RoutedEventArgs e)
    {
        if (PhoneApps == null || CurrentWindowsPeer == null) return;
        CancelWindowsLinkCatalogRead();
        _windowsDragItem = null;
        _windowsLinkError = null;
        UpdateWindowsLinkSearchHint();
        UpdateWindowsLinkRows();
    }

    private void UpdateWindowsLinkSearchHint()
    {
        string hint = L10n.T(WindowsLinkShowsWindows ? "Phone.WindowsLinkSearchWindows" : "Phone.SearchAppsHint");
        SearchHint.Text = hint;
        System.Windows.Automation.AutomationProperties.SetName(SearchBox, hint);
    }

    private async void WindowsLinkItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(PhoneApps, e.OriginalSource as DependencyObject) is not ListBoxItem
            { Content: WindowsLinkQuickItem item } || CurrentWindowsPeer is not { } peer)
            return;
        e.Handled = true;
        await ActivateWindowsLinkItemAsync(peer, item);
    }

    private Task ActivateWindowsLinkItemAsync(LinkPeer peer, WindowsLinkQuickItem item)
    {
        if (!peer.Connected) return RefreshWindowsLinkPeerAsync(peer);
        if (!item.Window) return RunWindowsLinkActionAsync(peer, () => WindowsLinkHub.PullAudioAsync(peer, item.App, _lifetime.Token));
        var position = PointToScreen(new System.Windows.Point(ActualWidth, 24));
        return ((App)System.Windows.Application.Current).OpenWindowsLinkDisplayAsync(
            new WindowsLinkAppDrag(peer, item.App, Window: true), (int)position.X, (int)position.Y);
    }

    private async void WindowsLinkSystemAudio_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentWindowsPeer is not { Connected: true } peer || WindowsLinkShowsWindows) return;
        await RunWindowsLinkActionAsync(peer, () => WindowsLinkHub.PullAudioAsync(
            peer, LinkRemoteApp.SystemAudio(peer.DisplayName), _lifetime.Token));
    }

    private void DragWindowsLinkSystemAudio()
    {
        if (!WindowsLinkSystemAudioButton.IsVisible || CurrentWindowsPeer is not { Connected: true } peer) return;
        WindowsLinkAppDragController.Run(WindowsLinkSystemAudioButton,
            new WindowsLinkAppDrag(peer, LinkRemoteApp.SystemAudio(peer.DisplayName)));
    }

    private void WindowsLinkRemoteCatalogChanged(string peerId)
    {
        if (!Dispatcher.CheckAccess())
        {
            _mediaUiLifetime.Post(Dispatcher, () => WindowsLinkRemoteCatalogChanged(peerId));
            return;
        }
        if (_closed || !IsVisible || CurrentWindowsPeer?.Id != peerId) return;
        ResetWindowsLinkRows();
        _windowsDragItem = null;
        UpdateWindowsLinkRows();
    }

    private void ResetWindowsLinkRows()
    {
        CancelWindowsLinkCatalogRead();
        _windowsRemoteAudioApps = null;
        _windowsRemoteWindows = null;
        _windowsQuickItems = [];
        _windowsLinkError = null;
    }

    private void CancelWindowsLinkCatalogRead()
    {
        var read = _windowsCatalogRead;
        _windowsCatalogRead = null;
        read?.Cancel();
        read?.Dispose();
    }

    private void UpdateWindowsLinkRows()
    {
        if (CurrentWindowsPeer is not { } peer) return;
        bool showWindows = WindowsLinkShowsWindows;
        WindowsLinkSystemAudioButton.Visibility = !showWindows && peer.Connected && (peer.Capabilities & LinkCapabilities.SystemAudio) != 0
            ? Visibility.Visible : Visibility.Collapsed;
        var service = WindowsLinkHub.Service;

        if (!peer.Connected)
        {
            CancelWindowsLinkCatalogRead();
            _windowsRemoteAudioApps = null;
            _windowsRemoteWindows = null;
        }
        else if (IsVisible && _enableMedia && (showWindows ? _windowsRemoteWindows : _windowsRemoteAudioApps) == null
                 && _windowsCatalogRead == null)
        {
            _ = LoadWindowsLinkCatalogAsync(peer, force: false);
        }

        if (showWindows)
        {
            var openWindows = (service?.Streams ?? [])
                .Where(s => s.Incoming && s.Kind == LinkMediaKind.Window && s.PeerId == peer.Id)
                .Select(s => s.AppKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _windowsQuickItems = (_windowsRemoteWindows ?? []).Where(a => a.HasWindow && a.WindowHandle != 0)
                .DistinctBy(a => a.WindowHandle)
                .Select(a => new WindowsLinkQuickItem(a, openWindows.Contains(a.Key), window: true)).ToArray();
        }
        else
        {
            var activeIncoming = (service?.IncomingAudioSources ?? [])
                .Where(s => string.Equals(s.PeerId, peer.Id, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.AppKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _windowsQuickItems = (_windowsRemoteAudioApps ?? [])
                .Select(a => new WindowsLinkQuickItem(a, a.IsPlaying || activeIncoming.Contains(a.Key))).ToArray();
        }

        FilterWindowsLinkRows();
        UpdateWindowsLinkFooter();
    }

    private async Task LoadWindowsLinkCatalogAsync(LinkPeer peer, bool force)
    {
        if (WindowsLinkHub.Service is not { } service) return;
        CancelWindowsLinkCatalogRead();
        var read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _windowsCatalogRead = read;
        bool audioOnly = !WindowsLinkShowsWindows;
        if (force)
        {
            _windowsActionPending = true;
            _windowsLinkError = null;
            UpdateWindowsLinkFooter();
        }
        try
        {
            var apps = await service.RequestCatalogAsync(peer.Id, includeIcons: true, read.Token, audioOnly: audioOnly);
            if (_closed || read.IsCancellationRequested || CurrentWindowsPeer?.Id != peer.Id) return;
            if (audioOnly) _windowsRemoteAudioApps = apps;
            else _windowsRemoteWindows = apps;
            _windowsLinkError = null;
            UpdateWindowsLinkRows();
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed && !read.IsCancellationRequested && CurrentWindowsPeer?.Id == peer.Id)
            {
                _windowsLinkError = ex.Message;
                UpdateWindowsLinkFooter();
            }
        }
        finally
        {
            if (ReferenceEquals(_windowsCatalogRead, read)) _windowsCatalogRead = null;
            read.Dispose();
            if (force)
            {
                _windowsActionPending = false;
                if (!_closed && WindowsLinkSelected) UpdateWindowsLinkFooter();
            }
        }
    }

    private async Task RefreshWindowsLinkPeerAsync(LinkPeer peer)
    {
        if (_windowsActionPending || _closed || CurrentWindowsPeer?.Id != peer.Id) return;
        _windowsLinkError = null;
        if (!peer.Connected)
        {
            await RunWindowsLinkActionAsync(peer, () => WindowsLinkHub.ReconnectPeerAsync(peer.Id, _lifetime.Token));
            return;
        }

        RefreshButton.IsEnabled = false;
        try { await LoadWindowsLinkCatalogAsync(peer, force: true); }
        finally { if (!_closed) RefreshButton.IsEnabled = true; }
    }

    private void FilterWindowsLinkRows()
    {
        if (PhoneApps == null || SearchBox == null || CurrentWindowsPeer == null) return;
        string query = SearchBox.Text.Trim();
        var filtered = _windowsQuickItems.Where(row => row.Matches(query)).ToArray();
        WindowsLinkAudioEmpty.Visibility = !WindowsLinkShowsWindows && query.Length == 0
            && _windowsRemoteAudioApps is { Length: 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (PhoneApps.ItemsSource is not IReadOnlyList<WindowsLinkQuickItem> previous || !previous.SequenceEqual(filtered))
            PhoneApps.ItemsSource = filtered;
    }

    private async Task RunWindowsLinkActionAsync(LinkPeer peer, Func<Task> action)
    {
        if (_windowsActionPending || CurrentWindowsPeer?.Id != peer.Id) return;
        _windowsActionPending = true;
        _windowsLinkError = null;
        UpdateWindowsLinkFooter();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && CurrentWindowsPeer?.Id == peer.Id) _windowsLinkError = ex.Message; }
        finally
        {
            _windowsActionPending = false;
            if (!_closed && WindowsLinkSelected) UpdateWindowsLinkFooter();
        }
    }

    private void UpdateWindowsLinkFooter()
    {
        if (!WindowsLinkSelected || _closed) return;
        ConnectionSignal.Visibility = ConnectionUsbIcon.Visibility = System.Windows.Visibility.Collapsed;
        if (CurrentWindowsPeer is { } peer)
        {
            ConnectionText.Text = L10n.T(_windowsLinkError != null ? "Phone.StatusOperationFailed"
                : _windowsActionPending ? "Phone.ProcessingStatus"
                : peer.Connected ? "Phone.ConnectedStatus" : "Phone.Disconnected");
            ConnectionText.ToolTip = _windowsLinkError ?? ConnectionText.Text;
            return;
        }
        ConnectionText.Text = _windowsLinkView?.FooterStatus ?? L10n.T(WindowsLinkHub.Service?.IsEnabled == true ? "Phone.WindowsLinkReady" : "Phone.Disconnected");
        ConnectionText.ToolTip = _windowsLinkView?.FooterToolTip ?? ConnectionText.Text;
    }
}
#endif

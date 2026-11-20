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
using System.Windows.Media;
using SonicRoute.WindowsLink;

namespace SonicRoute;

public partial class PhoneAudioWindow
{
    internal sealed class WindowsLinkQuickItem : INotifyPropertyChanged
    {
        public LinkRemoteApp App { get; }
        public bool Window { get; }
        public string Name { get; }
        public string Initial => string.IsNullOrEmpty(Name) ? "♪" : Name[..1];
        private ImageSource? _icon;
        private bool _iconDecoded;
        private bool _isPlaying;

        public ImageSource? Icon
        {
            get
            {
                if (!Volatile.Read(ref _iconDecoded))
                {
                    _icon = App.IsSystemAudio ? PhoneSourceIcon.SystemAudioImage
                        : App.IsMonitor ? PhoneSourceIcon.MonitorImage : AppIconService.DecodePng(App.IconPng);
                    Volatile.Write(ref _iconDecoded, true);
                }
                return _icon;
            }
        }

        public string Description { get; }
        public bool IsPlaying
        {
            get => _isPlaying;
            internal set
            {
                if (_isPlaying == value) return;
                _isPlaying = value;
                PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal WindowsLinkQuickItem(LinkRemoteApp app, bool isPlaying, bool window, string name, string description)
        {
            App = app;
            Window = window;
            Name = name;
            Description = description;
            _isPlaying = isPlaying;
        }

        internal bool Matches(string query) => query.Length == 0
            || Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || App.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || App.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private string? _windowsLinkRowPeerId;
    private string? _windowsLinkError;
    private bool _windowsActionPending;
    private bool _windowsAudioLoadFailed;
    private bool _windowsWindowsLoadFailed;
    private CancellationTokenSource? _windowsCatalogRead;
    private LinkRemoteApp[]? _windowsRemoteAudioApps;
    private LinkRemoteApp[]? _windowsRemoteWindows;
    private WindowsLinkQuickItem[] _windowsQuickItems = [];
    private bool _peerAppearance;
    private string? _latencyPeer;
    private CancellationTokenSource? _latencyWatch;
    private long _lastWindowsAudioRead;

    // Peers without session-change notifications still get a fresh catalog on user interaction.
    private void WindowsAudioListEntered(object sender, System.Windows.Input.MouseEventArgs e) => RefreshWindowsAudioOnEntry();
    private void WindowsAudioListFocused(object sender, KeyboardFocusChangedEventArgs e) => RefreshWindowsAudioOnEntry();
    private void RefreshWindowsAudioOnEntry()
    {
        if (!_enableMedia || !IsVisible || _closed || _windowsCatalogRead != null || _windowsActionPending
            || WindowsLinkShowsWindows || CurrentWindowsPeer is not { Connected: true } peer
            || System.Diagnostics.Stopwatch.GetElapsedTime(_lastWindowsAudioRead) < TimeSpan.FromSeconds(3)) return;
        _ = LoadWindowsLinkCatalogAsync(peer, force: false);
    }

    private void WindowsAudioMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        bool fromKeyboard = e.CursorLeft == -1 && e.CursorTop == -1;
        var row = fromKeyboard && e.OriginalSource == PhoneApps
            ? PhoneApps.ItemContainerGenerator.ContainerFromItem(PhoneApps.SelectedItem) as ListBoxItem
            : ItemsControl.ContainerFromElement(PhoneApps, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (ShowWindowsAudioMenu(row, fromKeyboard)) e.Handled = true;
    }

    private bool ShowWindowsAudioMenu(ListBoxItem? row, bool anchorToRow)
    {
        if (_closed || WindowsLinkShowsWindows || CurrentWindowsPeer is not { } peer
            || row?.Content is not WindowsLinkQuickItem { Window: false } item) return false;
        row.IsSelected = true;
        if (row.ContextMenu?.IsOpen == true) return true;
        var app = item.App;
        bool shared = WindowsLinkHub.CanPullAudio(peer, app);
        var menu = WindowsLinkPlaybackMenu.Create(L10n.T("Phone.WindowsLinkDropOnlyHere"),
            WindowsLinkHub.RemoteExclusiveMode(peer, app), shared,
            (onlyHere, muteSpeaker) => RunWindowsLinkActionAsync(peer,
                () => WindowsLinkHub.PullAudioAsync(peer, app, _lifetime.Token, onlyHere, muteSpeaker)));
        menu.Placement = anchorToRow ? System.Windows.Controls.Primitives.PlacementMode.Bottom
            : System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.PlacementTarget = row;
        row.ContextMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(row.ContextMenu, menu)) row.ClearValue(ContextMenuProperty); };
        menu.IsOpen = true;
        return true;
    }

    private void SetPeerAppearance(bool showPeer)
    {
        if (_peerAppearance == showPeer) return;
        _peerAppearance = showPeer;
        PhoneSurface.SetResourceReference(Border.BackgroundProperty, showPeer ? "Theme.WindowBg" : "Theme.WindowBgAlpha");
        ConnectionText.IsHitTestVisible = !showPeer;
        DeviceCombo.ToolTip = showPeer ? ConnectionText.ToolTip : null;
    }
    private bool WindowsLinkShowsWindows => CurrentWindowsPeer != null && WindowsLinkWindowChoice?.IsChecked == true;
    private bool WindowsLinkCanReturn(WindowsLinkAppDrag drag) =>
        drag.CanReturn && CurrentWindowsPeer?.Id == drag.Peer.Id && WindowsLinkShowsWindows == drag.Window;

    private void WindowsLinkView_Changed(object sender, RoutedEventArgs e)
    {
        if (PhoneApps == null || CurrentWindowsPeer == null) return;
        CancelWindowsLinkCatalogRead();
        _windowsDragItem = null;
        _windowsLinkError = null;
        UpdateWindowsLinkSearchHint();
        ClearPcDropHighlight();
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
        if (e.ChangedButton != MouseButton.Left) return;
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
        _windowsAudioLoadFailed = false;
        _windowsWindowsLoadFailed = false;
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
        var service = WindowsLinkHub.Service;

        if (!peer.Connected)
        {
            CancelWindowsLinkCatalogRead();
            _windowsRemoteAudioApps = null;
            _windowsRemoteWindows = null;
            _windowsAudioLoadFailed = false;
            _windowsWindowsLoadFailed = false;
        }
        else if (IsVisible && _enableMedia
                 && (showWindows ? _windowsRemoteWindows == null && !_windowsWindowsLoadFailed
                                 : _windowsRemoteAudioApps == null && !_windowsAudioLoadFailed)
                 && _windowsCatalogRead == null)
        {
            _ = LoadWindowsLinkCatalogAsync(peer, force: false);
        }

        static (bool Window, string InstanceId, long Handle, int ProcessId, string Key, string Name, string Description) RowIdentity(
            WindowsLinkQuickItem row) =>
            (row.Window, row.App.InstanceId ?? "", row.App.WindowHandle, row.App.ProcessId, row.App.Key, row.Name, row.Description);

        var existing = new Dictionary<(bool, string, long, int, string, string, string), WindowsLinkQuickItem>();
        foreach (var old in _windowsQuickItems)
            existing.TryAdd(RowIdentity(old), old);

        WindowsLinkQuickItem BuildOrReuse(LinkRemoteApp app, bool isPlaying, bool window, string description)
        {
            string displayName = app.IsSystemAudio ? L10n.T("Phone.WindowsLinkDeviceAudioDesc")
                : app.Display is { } display
                ? string.Format(L10n.T("Phone.WindowsLinkDisplayNumber"), display.Number)
                : window ? app.WindowTitle ?? app.Name : app.Name;
            var key = (window, app.InstanceId ?? "", app.WindowHandle, app.ProcessId, app.Key, displayName, description);
            if (existing.TryGetValue(key, out var reused) && (app.IsSystemAudio || ReferenceEquals(reused.App, app)))
            {
                reused.IsPlaying = isPlaying;
                return reused;
            }
            return new WindowsLinkQuickItem(app, isPlaying, window, displayName, description);
        }

        if (showWindows)
        {
            var openWindows = (service?.Streams ?? [])
                .Where(s => s.Incoming && s.Kind == LinkMediaKind.Window
                    && string.Equals(s.PeerId, peer.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            _windowsQuickItems = (_windowsRemoteWindows ?? [])
                .Where(a => a.IsMonitor ? peer.Capabilities.HasFlag(LinkCapabilities.MonitorCapture) : a.HasWindow && a.WindowHandle != 0)
                .OrderByDescending(a => a.IsMonitor)
                .ThenBy(a => a.Display?.Number)
                .DistinctBy(a => a.InstanceId ?? FormattableString.Invariant($"hwnd:{a.WindowHandle:x}"), StringComparer.OrdinalIgnoreCase)
                .Select(a =>
                {
                    bool active = openWindows.Any(s => s.InstanceId != null && a.InstanceId != null
                        ? string.Equals(s.InstanceId, a.InstanceId, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(s.AppKey, a.Key, StringComparison.OrdinalIgnoreCase));
                    string description = a.ProcessName;
                    if (a.Display is { } display)
                    {
                        description = $"{display.Width} × {display.Height}";
                        if (display.IsPrimary) description += " · " + L10n.T("Phone.WindowsLinkPrimaryDisplay");
                    }
                    return BuildOrReuse(a, active, window: true, description);
                })
                .ToArray();
        }
        else
        {
            var activeIncoming = (service?.IncomingAudioSources ?? [])
                .Where(s => string.Equals(s.PeerId, peer.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var rawAudio = _windowsRemoteAudioApps ?? [];
            var duplicateProcessNames = rawAudio
                .Where(a => !string.IsNullOrWhiteSpace(a.ProcessName))
                .GroupBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            IEnumerable<LinkRemoteApp> sources = rawAudio.Where(a => !a.IsSystemAudio);
            if (peer.Connected && peer.Capabilities.HasFlag(LinkCapabilities.SystemAudio))
                sources = sources.Prepend(LinkRemoteApp.SystemAudio(peer.DisplayName));
            _windowsQuickItems = sources
                .Select(a =>
                {
                    bool active = a.IsPlaying || activeIncoming.Any(s => s.InstanceId != null && a.InstanceId != null
                        ? string.Equals(s.InstanceId, a.InstanceId, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(s.AppKey, a.Key, StringComparison.OrdinalIgnoreCase));
                    string desc = a.IsSystemAudio ? peer.DisplayName
                        : duplicateProcessNames.Contains(a.ProcessName) && a.ProcessId > 0
                        ? $"{a.ProcessName} · PID {a.ProcessId}"
                        : a.ProcessName;
                    return BuildOrReuse(a, active, window: false, desc);
                })
                .ToArray();
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
            if (audioOnly) _windowsAudioLoadFailed = false;
            else _windowsWindowsLoadFailed = false;
            _windowsActionPending = true;
            _windowsLinkError = null;
            UpdateWindowsLinkFooter();
        }
        try
        {
            var apps = await service.RequestCatalogAsync(peer.Id, includeIcons: true, read.Token,
                audioOnly: audioOnly, windowsOnly: !audioOnly, refresh: force);
            if (_closed || read.IsCancellationRequested || CurrentWindowsPeer?.Id != peer.Id) return;
            if (audioOnly)
            {
                _lastWindowsAudioRead = System.Diagnostics.Stopwatch.GetTimestamp();
                _windowsRemoteAudioApps = apps;
                _windowsAudioLoadFailed = false;
            }
            else
            {
                _windowsRemoteWindows = apps;
                _windowsWindowsLoadFailed = false;
            }
            _windowsLinkError = null;
            UpdateWindowsLinkRows();
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed && !read.IsCancellationRequested && CurrentWindowsPeer?.Id == peer.Id)
            {
                if (audioOnly) _windowsAudioLoadFailed = true;
                else _windowsWindowsLoadFailed = true;
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
        WindowsLinkAudioEmpty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (filtered.Length == 0) UpdatePeerEmptyState();
        if (PhoneApps.ItemsSource is not IReadOnlyList<WindowsLinkQuickItem> previous || !previous.SequenceEqual(filtered))
            PhoneApps.ItemsSource = filtered;
    }

    private void UpdatePeerEmptyState()
    {
        if (CurrentWindowsPeer is not { } peer || WindowsLinkAudioEmpty.Visibility != Visibility.Visible) return;
        bool windows = WindowsLinkShowsWindows;
        PeerEmptyIcon.Data = windows || !peer.Connected ? AutomationIcons.Monitor : AutomationIcons.Speaker;
        string key;
        if (!peer.Connected)
            key = peer.State == LinkPeerState.Connecting || _windowsActionPending
                ? "Phone.WindowsLinkStateConnecting" : "Phone.WindowsLinkRefreshToConnect";
        else if (windows ? _windowsWindowsLoadFailed : _windowsAudioLoadFailed) key = "Phone.WindowsLinkReadFailed";
        else if ((windows ? _windowsRemoteWindows : _windowsRemoteAudioApps) == null) key = "Tray.Loading";
        else if (!string.IsNullOrWhiteSpace(SearchBox.Text)) key = "Phone.WindowsLinkNoMatches";
        else key = windows ? "Phone.WindowsLinkNoWindows" : "Phone.NoDisplayedApps";
        PeerEmptyText.Text = L10n.T(key);
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

    private void WatchWindowsLatency(string? peerId)
    {
        if (_latencyPeer == peerId) return;
        _latencyWatch?.Cancel();
        _latencyWatch = null;
        _latencyPeer = peerId;
        ApplyConnectionQuality(null);
        if (peerId == null || WindowsLinkHub.Service is not { } service) return;
        var watch = _latencyWatch = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = MeasureWindowsLatencyAsync(service, peerId, watch);
    }

    private async Task MeasureWindowsLatencyAsync(WindowsLinkService service, string peerId, CancellationTokenSource watch)
    {
        try
        {
            while (!watch.IsCancellationRequested)
            {
                var ms = await service.MeasureRoundTripAsync(peerId, watch.Token);
                if (watch.IsCancellationRequested || _latencyWatch != watch) return;
                ApplyConnectionQuality(new(ms));
                await Task.Delay(TimeSpan.FromSeconds(5), watch.Token);
            }
        }
        catch (OperationCanceledException) when (watch.IsCancellationRequested) { }
        finally
        {
            if (_latencyWatch == watch) _latencyWatch = null;
            watch.Dispose();
        }
    }

    private void UpdateWindowsLinkFooter()
    {
        if (!WindowsLinkSelected || _closed) return;
        bool connected = CurrentWindowsPeer?.Connected == true;
        WatchWindowsLatency(connected && IsVisible ? CurrentWindowsPeer!.Id : null);
        ConnectionSignal.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        ConnectionUsbIcon.Visibility = Visibility.Collapsed;
        if (CurrentWindowsPeer is { } peer)
        {
            UpdatePeerEmptyState();
            ConnectionText.Text = _windowsLinkError != null ? L10n.T("Phone.StatusOperationFailed")
                : _windowsActionPending ? L10n.T("Phone.ProcessingStatus")
                : WindowsLinkPeerStatus.Text(peer);
            ConnectionText.ToolTip = _windowsLinkError
                ?? (peer.State == LinkPeerState.Failed ? WindowsLinkPeerStatus.FailureText(peer.Failure) : null)
                ?? ConnectionText.Text;
            DeviceCombo.ToolTip = ConnectionText.ToolTip;
            return;
        }
        ConnectionText.Text = _windowsLinkView?.FooterStatus ?? L10n.T(WindowsLinkHub.Service?.IsEnabled == true ? "Phone.WindowsLinkReady" : "Phone.Disconnected");
        ConnectionText.ToolTip = _windowsLinkView?.FooterToolTip ?? ConnectionText.Text;
    }
}
#endif

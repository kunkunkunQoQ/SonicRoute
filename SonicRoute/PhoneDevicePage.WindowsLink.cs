#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SonicRoute.WindowsLink;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    internal sealed class WindowsLinkAppItem : INotifyPropertyChanged
    {
        public LinkRemoteApp App { get; }
        public string Key => App.Key;
        public string Name => App.Name;
        public string Package => App.HasWindow && !string.IsNullOrWhiteSpace(App.ProcessName)
            ? $"{App.ProcessName} · {L10n.T("Phone.WindowsLinkWindowBadge")}"
            : App.ProcessName;
        public string Initial => App.IsSystemAudio ? "♫" : string.IsNullOrEmpty(Name) ? "♪" : Name[..1];
        private ImageSource? _icon;
        private bool _iconDecoded;

        public ImageSource? Icon
        {
            get
            {
                if (App.IsSystemAudio) return PhoneSourceIcon.MonitorImage;
                if (!Volatile.Read(ref _iconDecoded))
                {
                    _icon = AppIconService.DecodePng(App.IconPng);
                    Volatile.Write(ref _iconDecoded, true);
                }
                return _icon;
            }
        }

        public string Description => App.IsSystemAudio
            ? L10n.T("Phone.WindowsLinkDeviceAudioDesc")
            : App.HasWindow
                ? L10n.T("Phone.WindowsLinkRemoteWindowDesc")
                : string.IsNullOrWhiteSpace(App.ProcessName)
                    ? L10n.T("Phone.WindowsLinkRemoteAppDesc")
                    : $"{App.ProcessName} · {L10n.T("Phone.WindowsLinkRemoteAppDesc")}";

        public event PropertyChangedEventHandler? PropertyChanged;

        internal WindowsLinkAppItem(LinkRemoteApp app) => App = app;

        internal void RefreshLanguage()
        {
            PropertyChanged?.Invoke(this, new(nameof(Description)));
            PropertyChanged?.Invoke(this, new(nameof(Package)));
        }
    }

    internal sealed class WindowsLinkAppChoice : INotifyPropertyChanged
    {
        public WindowsLinkAppItem Item { get; }
        private bool _retained;
        private bool _smartVolume;

        public bool Retained
        {
            get => _retained;
            internal set
            {
                if (_retained == value) return;
                _retained = value;
                PropertyChanged?.Invoke(this, new(nameof(Retained)));
                PropertyChanged?.Invoke(this, new(nameof(ActionText)));
            }
        }

        public bool SmartVolume
        {
            get => _smartVolume;
            internal set
            {
                if (_smartVolume == value) return;
                _smartVolume = value;
                PropertyChanged?.Invoke(this, new(nameof(SmartVolume)));
            }
        }

        public string ActionText => L10n.T(Retained ? "Phone.RemoveFromQuickPanel" : "Phone.AddToQuickPanel");
        public event PropertyChangedEventHandler? PropertyChanged;

        internal WindowsLinkAppChoice(WindowsLinkAppItem item, bool retained, bool smartVolume = true)
        {
            Item = item;
            _retained = retained;
            _smartVolume = smartVolume;
        }

        internal void RefreshLanguage()
        {
            Item.RefreshLanguage();
            PropertyChanged?.Invoke(this, new(nameof(ActionText)));
        }
    }

    private WindowsLinkAppItem[] _windowsCatalog = [];
    private WindowsLinkAppChoice[] _windowsCatalogChoices = [];
    private string? _windowsCatalogPeerId;
    private CancellationTokenSource? _windowsLinkCatalogOp;
    private LinkPinnedApp[] _windowsPinnedSnapshot = [];
    private string? _windowsRetainedPeerId;
    private string _windowsOriginalName = "";
    private bool _suppressWindowsLinkInput;

    internal void RefreshWindowsLinkPeerState()
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshWindowsLinkPeerState));
            return;
        }
        if (SelectedWindowsLinkPeer == null) return;
        if (WindowsLinkHub.Service?.Peers.Any(p =>
                string.Equals(p.Id, SelectedWindowsLinkPeer, StringComparison.OrdinalIgnoreCase)) != true)
        {
            ShowConnection();
            return;
        }
        ProfileChanged();
    }

    private void WindowsLinkPageChanged()
    {
        if (_disposed || (IsLoaded && !_active)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(WindowsLinkPageChanged));
            return;
        }
        if (_disposed || (IsLoaded && !_active)) return;
        if (SelectedWindowsLinkPeer != null) RefreshWindowsLinkPeerState();
        else if (WindowsLinkCard.IsVisible && _windowsLinkView != null) _ = _windowsLinkView.RefreshAsync();
    }

    private void RefreshWindowsLinkDevicePage()
    {
        var service = WindowsLinkHub.Service ?? WindowsLinkHub.EnsureService();
        var peer = SelectedWindowsLinkPeer is { } peerId
            ? service.Peers.FirstOrDefault(p => string.Equals(p.Id, peerId, StringComparison.OrdinalIgnoreCase))
            : null;
        if (peer == null)
        {
            SelectedWindowsLinkPeer = null;
            ShowConnection();
            return;
        }

        if (_windowsRetainedPeerId != peer.Id && CatalogPanel.Visibility == Visibility.Visible)
        {
            CancelWindowsLinkCatalog();
            ReleaseCatalog();
        }

        DeviceName.Text = peer.DisplayName;
        string state = L10n.T(peer.Connected ? "Phone.ConnectedStatus" : "Phone.Disconnected");
        DeviceDescription.Text = string.IsNullOrWhiteSpace(peer.Address) ? state : $"{state} · {peer.Address}";

        ReconnectButton.Visibility = peer.Connected ? Visibility.Collapsed : Visibility.Visible;
        ReconnectButton.IsEnabled = !_busy;
        DisconnectButton.IsEnabled = peer.Connected && !_busy;
        DisconnectButton.Content = L10n.T("Phone.Disconnect");
        RemoveDeviceButton.IsEnabled = !_busy;
        AddAppsButton.IsEnabled = peer.Connected && !_busy;

        var settings = service.GetPeerSettings(peer.Id);
        _streamApplying = true;
        try
        {
            _streamLanguage = L10n.CurrentLanguage;
            StreamingCard.IsEnabled = !_busy;

            VideoEncoderCombo.SelectedValuePath = nameof(StreamOption.Value);
            VideoEncoderCombo.ItemsSource = new StreamOption[]
            {
                new(L10n.T("Phone.WindowsLinkRes720p"), "720"),
                new(L10n.T("Phone.WindowsLinkRes1080p"), "1080")
            };
            VideoEncoderCombo.SelectedValue = settings.VideoMaxHeight <= 720 ? "720" : "1080";
            UpdateCodecToolTip(VideoEncoderCombo, "Phone.StreamEncoder");

            const string windowsName = "Microsoft H.264 Video Decoder MFT";
            VideoDecoderCombo.SelectedValuePath = nameof(StreamOption.Value);
            VideoDecoderCombo.ItemsSource = new StreamOption[]
            {
                new(L10n.T("Phone.StreamDecoderAutomatic"), ""),
                new(string.Format(L10n.T("Phone.StreamDecoderAccelerated"), windowsName + " / D3D11"), AndroidAudio.AndroidStreamSettings.WindowsVideoDecoder),
                new(string.Format(L10n.T("Phone.StreamDecoderSoftware"), windowsName + " / CPU"), AndroidAudio.AndroidStreamSettings.SoftwareVideoDecoder)
            };
            VideoDecoderCombo.SelectedValue = settings.VideoDecoder ?? "";
            UpdateCodecToolTip(VideoDecoderCombo, "Phone.StreamDecoder");

            NumericOptions(VideoBitRateCombo, [4000000, 8000000, 16000000, 24000000, 32000000], settings.VideoBitRate, 1000000, " Mbps");
            NumericOptions(VideoFpsCombo, [15, 30, 60], settings.VideoMaxFps, 1, " fps");
        }
        finally
        {
            _streamApplying = false;
        }

        _suppressWindowsLinkInput = true;
        try
        {
            WindowsLinkAllowInputSwitch.IsChecked = settings.AllowRemoteInput;
            WindowsLinkAllowInputSwitch.IsEnabled = !_busy;
        }
        finally
        {
            _suppressWindowsLinkInput = false;
        }

        var pinned = service.GetPinnedApps(peer.Id);
        bool pinnedChanged = _windowsRetainedPeerId != peer.Id
            || !PinnedListsMatch(_windowsPinnedSnapshot, pinned);
        _windowsRetainedPeerId = peer.Id;
        _windowsPinnedSnapshot = pinned;

        if (pinnedChanged)
        {
            RetainedApps.ItemsSource = pinned.Select(p =>
            {
                var app = new LinkRemoteApp(p.Key, p.Name, p.ProcessName, 0, false, false, 0, null, DecodeBase64Icon(p.IconPngBase64));
                return new WindowsLinkAppChoice(new WindowsLinkAppItem(app), true, p.SmartVolume);
            }).ToArray();
        }
        else if (RetainedApps.ItemsSource is IEnumerable<WindowsLinkAppChoice> existing)
        {
            var smartByKey = pinned.ToDictionary(p => p.Key, p => p.SmartVolume, StringComparer.OrdinalIgnoreCase);
            foreach (var choice in existing)
            {
                if (smartByKey.TryGetValue(choice.Item.Key, out bool smart))
                    choice.SmartVolume = smart;
            }
        }

        RetainedCount.Text = string.Format(L10n.T("Phone.RetainedCountFormat"), pinned.Length);
        RetainedEmpty.Visibility = pinned.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (pinnedChanged && CatalogPanel.Visibility == Visibility.Visible)
            ApplyWindowsLinkCatalogFilter();
    }

    private static bool PinnedListsMatch(LinkPinnedApp[] left, LinkPinnedApp[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i].Key, right[i].Key, StringComparison.OrdinalIgnoreCase)
                || left[i].Name != right[i].Name
                || left[i].ProcessName != right[i].ProcessName
                || left[i].IconPngBase64 != right[i].IconPngBase64)
                return false;
        }
        return true;
    }

    private static byte[]? DecodeBase64Icon(string? b64)
    {
        if (string.IsNullOrWhiteSpace(b64)) return null;
        try { return Convert.FromBase64String(b64); }
        catch { return null; }
    }

    private void WindowsLinkStreamSettingsChanged()
    {
        if (_streamApplying || _disposed || SelectedWindowsLinkPeer is not { } peerId
            || WindowsLinkHub.Service is not { } service) return;
        if (VideoEncoderCombo.SelectedValue is not string resStr
            || VideoDecoderCombo.SelectedValue is not string decoder
            || VideoBitRateCombo.SelectedValue is not string bitRateStr
            || VideoFpsCombo.SelectedValue is not string fpsStr) return;

        var current = service.GetPeerSettings(peerId);
        int maxHeight = int.TryParse(resStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedRes) ? parsedRes : 1080;
        int bitRate = int.TryParse(bitRateStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedRate) ? parsedRate : current.VideoBitRate;
        int fps = int.TryParse(fpsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedFps) ? parsedFps : current.VideoMaxFps;

        var updated = (current with
        {
            VideoDecoder = NullIfAutomatic(decoder),
            VideoBitRate = bitRate,
            VideoMaxFps = fps,
            VideoMaxHeight = maxHeight
        }).Normalize();
        service.UpdatePeerSettings(peerId, updated);
    }

    private void WindowsLinkAllowInput_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressWindowsLinkInput || _disposed || SelectedWindowsLinkPeer is not { } peerId
            || WindowsLinkHub.Service is not { } service) return;
        var current = service.GetPeerSettings(peerId);
        service.UpdatePeerSettings(peerId, current with { AllowRemoteInput = WindowsLinkAllowInputSwitch.IsChecked == true });
    }

    private void CommitWindowsLinkRename()
    {
        if (_suppressName || _disposed || PhoneNameBox.Visibility != Visibility.Visible
            || SelectedWindowsLinkPeer is not { } peerId || WindowsLinkHub.Service is not { } service) return;
        service.Rename(peerId, PhoneNameBox.Text.Trim());
    }

    private Task ReconnectWindowsLinkPeerAsync()
    {
        if (SelectedWindowsLinkPeer is not { } peerId) return Task.CompletedTask;
        return RunAsync(token => WindowsLinkHub.ReconnectPeerAsync(peerId, token), "Phone.ConnectedStatus");
    }

    private Task DisconnectWindowsLinkPeerAsync()
    {
        if (SelectedWindowsLinkPeer is not { } peerId) return Task.CompletedTask;
        return RunAsync(async _ =>
        {
            if (WindowsLinkHub.Service is { } service)
                await service.DisconnectAsync(peerId);
        }, "Phone.StatusWifiDisconnectedSaved");
    }

    private void RemoveWindowsLinkPeer()
    {
        if (SelectedWindowsLinkPeer is not { } peerId || _busy) return;
        CancelWindowsLinkCatalog();
        WindowsLinkHub.Service?.Remove(peerId);
        SelectedWindowsLinkPeer = null;
        SetOperationStatusKey("Phone.StatusRemovedKeepAuth");
        ShowConnection();
    }

    private async void AddWindowsLinkAppsAsync()
    {
        if (_disposed || !_active || _busy || SelectedWindowsLinkPeer is not { } peerId) return;
        var peer = WindowsLinkHub.Service?.Peers.FirstOrDefault(p => string.Equals(p.Id, peerId, StringComparison.OrdinalIgnoreCase));
        if (peer is not { Connected: true })
        {
            SetOperationStatusKey("Phone.WindowsLinkNeedConnect");
            return;
        }
        SetCatalogVisible(true);
        CatalogSearch.Focus();
        await LoadWindowsLinkCatalogAsync(false);
    }

    private async Task LoadWindowsLinkCatalogAsync(bool refresh)
    {
        if (SelectedWindowsLinkPeer is not { } peerId || WindowsLinkHub.Service is not { } service) return;
        if (!refresh && _windowsCatalogPeerId == peerId && _windowsCatalog.Length > 0)
        {
            ApplyWindowsLinkCatalogFilter();
            return;
        }

        CancelWindowsLinkCatalog();
        var op = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _windowsLinkCatalogOp = op;
        SetCatalogStatus(CatalogStatusKind.Loading);
        try
        {
            var apps = await service.RequestCatalogAsync(peerId, includeIcons: true, op.Token);
            if (_disposed || op.IsCancellationRequested || SelectedWindowsLinkPeer != peerId) return;
            _windowsCatalog = apps
                .Where(a => !a.IsSystemAudio && !string.IsNullOrWhiteSpace(a.Key))
                .Select(a => new WindowsLinkAppItem(a))
                .ToArray();
            _windowsCatalogPeerId = peerId;
            _windowsCatalogChoices = _windowsCatalog.Select(item => new WindowsLinkAppChoice(item, false)).ToArray();
            service.UpdatePinnedMetadata(peerId, apps);
            ApplyWindowsLinkCatalogFilter();
        }
        catch (OperationCanceledException) when (op.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && !op.IsCancellationRequested) SetOperationStatusError(ex);
            if (_windowsCatalog.Length == 0) SetCatalogStatus(CatalogStatusKind.Empty);
        }
        finally
        {
            if (ReferenceEquals(_windowsLinkCatalogOp, op)) _windowsLinkCatalogOp = null;
            op.Dispose();
        }
    }

    private void ApplyWindowsLinkCatalogFilter()
    {
        if (CatalogApps == null || CatalogPanel.Visibility != Visibility.Visible || SelectedWindowsLinkPeer is not { } peerId) return;
        string query = CatalogSearch.Text.Trim();
        var pinned = (WindowsLinkHub.Service?.GetPinnedApps(peerId) ?? [])
            .Select(a => a.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in _windowsCatalogChoices)
            choice.Retained = pinned.Contains(choice.Item.Key);

        var filtered = _windowsCatalogChoices
            .Where(a => query.Length == 0
                || a.Item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || a.Item.Package.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (CatalogApps.ItemsSource is not IReadOnlyList<WindowsLinkAppChoice> previous || !previous.SequenceEqual(filtered))
            CatalogApps.ItemsSource = filtered;
        SetCatalogStatus(_windowsCatalog.Length == 0 ? CatalogStatusKind.Empty : CatalogStatusKind.Loaded);
    }

    private void CancelWindowsLinkCatalog()
    {
        var op = _windowsLinkCatalogOp;
        _windowsLinkCatalogOp = null;
        op?.Cancel();
        _windowsCatalog = [];
        _windowsCatalogChoices = [];
        _windowsCatalogPeerId = null;
    }
}
#endif

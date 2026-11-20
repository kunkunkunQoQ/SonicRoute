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
                if (App.IsSystemAudio) return PhoneSourceIcon.SystemAudioImage;
                if (!Volatile.Read(ref _iconDecoded))
                {
                    _icon = AppIconService.DecodePng(App.IconPng);
                    Volatile.Write(ref _iconDecoded, true);
                }
                return _icon;
            }
        }

        public string Description => string.Equals(Name, App.ProcessName, StringComparison.CurrentCultureIgnoreCase)
            ? L10n.T("Phone.WindowsLinkRemoteAppDesc") : App.ProcessName;

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
        public bool CanPin => false;
        public bool Retained => false;
        public string ActionText => "";
        private bool _smartVolume;

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

        public event PropertyChangedEventHandler? PropertyChanged;

        internal WindowsLinkAppChoice(WindowsLinkAppItem item, bool smartVolume)
        {
            Item = item;
            _smartVolume = smartVolume;
        }
    }

    private string? _windowsCatalogPeerId;
    private CancellationTokenSource? _windowsLinkCatalogOp;
    private string _windowsEmptyKey = "Phone.NoDisplayedApps";
    private string _windowsOriginalName = "";
    private bool _suppressWindowsLinkInput;
    private string? _removePeer;

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
        if (_disposed) return;
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

        UpdateWindowsFileCard(peer);
        UpdateWindowsDesktop(peer);
        DeviceName.Text = peer.DisplayName;
        string state = WindowsLinkPeerStatus.Text(peer);
        string? failure = peer.State == LinkPeerState.Failed ? WindowsLinkPeerStatus.FailureText(peer.Failure) : null;
        string addressPart = string.IsNullOrWhiteSpace(peer.Address) ? state : $"{state} · {peer.Address}";
        DeviceDescription.Text = string.IsNullOrWhiteSpace(failure) ? addressPart : $"{addressPart} · {failure}";

        ReconnectButton.Visibility = peer.Connected ? Visibility.Collapsed : Visibility.Visible;
        ReconnectButton.IsEnabled = !_busy && peer.State != LinkPeerState.Connecting;
        DisconnectButton.Visibility = peer.Connected ? Visibility.Visible : Visibility.Collapsed;
        DisconnectButton.IsEnabled = peer.Connected && !_busy;
        DisconnectButton.Content = L10n.T("Phone.Disconnect");
        RemoveDeviceButton.IsEnabled = !_busy;
        AddAppsButton.Visibility = Visibility.Collapsed;

        var settings = service.GetPeerSettings(peer.Id);
        _streamApplying = true;
        try
        {
            _streamLanguage = L10n.CurrentLanguage;
            StreamingCard.IsEnabled = !_busy;

            WindowsNativeResolutionHint.DataContext = peer;
            WindowsVideoResolutionCombo.Visibility = Visibility.Visible;
            WindowsVideoResolutionCombo.ItemsSource = new StreamOption[]
            {
                new(L10n.T("Phone.WindowsLinkRes720p"), "720"),
                new(L10n.T("Phone.WindowsLinkRes1080p"), "1080"),
                new(L10n.T("Phone.WindowsLinkResNative"), "0")
            };
            WindowsVideoResolutionCombo.SelectedValue = settings.VideoMaxHeight switch
            {
                0 => "0",
                <= 720 => "720",
                _ => "1080"
            };

            VideoEncoderCombo.SelectedValuePath = nameof(StreamOption.Value);
            VideoEncoderCombo.ItemsSource = new StreamOption[]
            {
                new(L10n.T("Phone.StreamAutomatic"), ""),
                new("H.264 (AVC)", "h264"),
                new("H.265 (HEVC)", "h265")
            };
            VideoEncoderCombo.SelectedValue = settings.VideoEncoder ?? "";
            UpdateCodecToolTip(VideoEncoderCombo, "Phone.StreamEncoder");

            const string windowsName = "Microsoft H.264 Video Decoder MFT";
            VideoDecoderCombo.SelectedValuePath = nameof(StreamOption.Value);
            VideoDecoderCombo.ItemsSource = new StreamOption[]
            {
                new(L10n.T("Phone.StreamDecoderAutomatic"), ""),
                new(string.Format(L10n.T("Phone.StreamDecoderAccelerated"), windowsName + " / D3D11"), AndroidAudio.AndroidStreamSettings.WindowsVideoDecoder),
                new(string.Format(L10n.T("Phone.StreamDecoderSoftware"), windowsName + " / CPU"), AndroidAudio.AndroidStreamSettings.SoftwareVideoDecoder),
                new(string.Format(L10n.T("Phone.StreamDecoderHevc"), "Microsoft HEVC Video Decoder MFT"), AndroidAudio.AndroidStreamSettings.WindowsHevcDecoder)
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

        RefreshWindowsTrust(service, peer);
        RefreshWindowsClipboard(service, peer.Id);
        WindowsReceiveFolderPath.Text = service.GetReceiveDirectory();

        if (!peer.Connected)
        {
            CancelWindowsLinkCatalog();
            RetainedApps.ItemsSource = null;
            _windowsEmptyKey = "Phone.WindowsLinkAppsOffline";
        }
        else if (_active && IsVisible && _windowsCatalogPeerId != peer.Id)
            _ = LoadWindowsAudioAppsAsync(peer.Id, false);
        UpdateWindowsAudioStatus();
    }

    private void UpdateWindowsAudioStatus()
    {
        int count = RetainedApps.Items.Count;
        RetainedCount.Text = count == 0 ? "" : string.Format(L10n.T("Phone.RetainedCountFormat"), count);
        WindowsAudioEmpty.Text = L10n.T(_windowsEmptyKey);
        WindowsAudioEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void WindowsLinkStreamSettingsChanged(object sender)
    {
        if (_streamApplying || _disposed || SelectedWindowsLinkPeer is not { } peerId
            || WindowsLinkHub.Service is not { } service) return;
        if (WindowsVideoResolutionCombo.SelectedValue is not string resStr
            || VideoEncoderCombo.SelectedValue is not string encoder
            || VideoDecoderCombo.SelectedValue is not string decoder
            || VideoBitRateCombo.SelectedValue is not string bitRateStr
            || VideoFpsCombo.SelectedValue is not string fpsStr) return;

        var current = service.GetPeerSettings(peerId);
        int maxHeight = int.TryParse(resStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedRes) ? parsedRes : 1080;
        int bitRate = int.TryParse(bitRateStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedRate) ? parsedRate : current.VideoBitRate;
        int fps = int.TryParse(fpsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedFps) ? parsedFps : current.VideoMaxFps;

        if (encoder == "h264" && decoder == AndroidAudio.AndroidStreamSettings.WindowsHevcDecoder
            || encoder == "h265" && decoder is (AndroidAudio.AndroidStreamSettings.WindowsVideoDecoder or AndroidAudio.AndroidStreamSettings.SoftwareVideoDecoder))
        {
            _streamApplying = true;
            try
            {
                if (ReferenceEquals(sender, VideoDecoderCombo)) VideoEncoderCombo.SelectedValue = encoder = "";
                else VideoDecoderCombo.SelectedValue = decoder = "";
            }
            finally { _streamApplying = false; }
        }
        var updated = (current with
        {
            VideoEncoder = NullIfAutomatic(encoder),
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

    private void WindowsClipboardStateChanged(string peerId)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => WindowsClipboardStateChanged(peerId))); return; }
        if (!_disposed && _active && SelectedWindowsLinkPeer == peerId && WindowsLinkHub.Service is { } service)
            RefreshWindowsClipboard(service, peerId);
    }

    private void RefreshWindowsClipboard(WindowsLinkService service, string peerId)
    {
        var state = service.GetClipboardState(peerId);
        ClipboardCard.ToolTip = string.Format(L10n.T("Phone.LinkClipboardScope"), WindowsLinkService.MaxClipboardBytes);
        _suppressClipboard = true;
        try
        {
            ClipboardSyncToggle.IsChecked = state.Enabled;
            ClipboardSyncToggle.IsEnabled = state.Enabled || (state.Connected && state.Supported);
        }
        finally { _suppressClipboard = false; }
        ClipboardStatus.Text = L10n.T(!state.Connected ? "Phone.LinkClipboardOffline" :
            !state.Supported ? "Phone.LinkClipboardUpdate" : state.Active ? "Phone.ClipboardConnected" :
            state.Enabled ? "Phone.LinkClipboardWaitPeer" : state.PeerEnabled ? "Phone.LinkClipboardReady" : "Phone.LinkClipboardOff");
    }

    private void AskRemoveWindowsPeer()
    {
        if (SelectedWindowsLinkPeer is not { } peer || _busy) return;
        _removePeer = peer;
        WindowsRemoveMessage.Text = string.Format(L10n.T("Phone.LinkRemoveConfirm"), DeviceName.Text);
        WindowsRemovePanel.Visibility = Visibility.Visible;
        WindowsRemovePanel.BringIntoView();
        WindowsRemoveCancel.Focus();
    }

    private void ClearWindowsRemovePrompt()
    {
        _removePeer = null;
        WindowsRemovePanel.Visibility = Visibility.Collapsed;
    }

    private void WindowsRemoveCancel_Click(object sender, RoutedEventArgs e)
    {
        ClearWindowsRemovePrompt();
        RemoveDeviceButton.Focus();
    }

    private void WindowsRemoveConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_removePeer is not { } peerId || SelectedWindowsLinkPeer != peerId || _busy ||
            WindowsLinkHub.Service is not { } service) return;
        try { service.Remove(peerId); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            WindowsRemoveMessage.Text = string.Format(L10n.T("Phone.LinkRemoveFailed"), DeviceName.Text);
            return;
        }
        ClearWindowsRemovePrompt();
        CancelWindowsLinkCatalog();
        SelectedWindowsLinkPeer = null;
        WindowsLinkConnectionChoice.IsChecked = true;
        SetOperationStatusKey("Phone.LinkRemoved");
        ShowConnection();
    }

    private void WindowsAudioCatalogChanged(string peerId)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => WindowsAudioCatalogChanged(peerId)));
            return;
        }
        if (_active && IsVisible && SelectedWindowsLinkPeer == peerId)
            _ = LoadWindowsAudioAppsAsync(peerId, false);
    }

    private async Task LoadWindowsAudioAppsAsync(string peerId, bool refresh)
    {
        if (!_active || !IsVisible || SelectedWindowsLinkPeer != peerId || WindowsLinkHub.Service is not { } service) return;
        if (_windowsCatalogPeerId != peerId) RetainedApps.ItemsSource = null;
        _windowsLinkCatalogOp?.Cancel();
        using var op = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _windowsLinkCatalogOp = op;
        _windowsCatalogPeerId = peerId;
        _windowsEmptyKey = "Tray.Loading";
        UpdateWindowsAudioStatus();
        try
        {
            var apps = await service.RequestCatalogAsync(peerId, includeIcons: true, op.Token, audioOnly: true, refresh: refresh);
            if (op.IsCancellationRequested || SelectedWindowsLinkPeer != peerId) return;
            ApplyWindowsAudioApps(service, peerId, apps);
        }
        catch (OperationCanceledException) when (op.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!op.IsCancellationRequested)
            {
                _windowsEmptyKey = "Phone.WindowsLinkReadFailed";
                UpdateWindowsAudioStatus();
            }
        }
        finally
        {
            if (ReferenceEquals(_windowsLinkCatalogOp, op)) _windowsLinkCatalogOp = null;
        }
    }

    private void ApplyWindowsAudioApps(WindowsLinkService service, string peerId, LinkRemoteApp[] apps)
    {
        RetainedApps.ItemsSource = apps.Where(app => !app.IsSystemAudio && app.ProcessId > 0
                && (app.HasAudioSession ?? app.IsPlaying) && !string.IsNullOrWhiteSpace(app.Key))
            .DistinctBy(app => app.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(app => new WindowsLinkAppChoice(new WindowsLinkAppItem(app), service.IsSmartVolumeEnabled(peerId, app.Key)))
            .ToArray();
        _windowsEmptyKey = "Phone.NoDisplayedApps";
        UpdateWindowsAudioStatus();
    }

    private void CancelWindowsLinkCatalog()
    {
        var op = _windowsLinkCatalogOp;
        _windowsLinkCatalogOp = null;
        op?.Cancel();
        _windowsCatalogPeerId = null;
    }
}
#endif

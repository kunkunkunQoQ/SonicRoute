#if SONICROUTE_PHONE
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

public partial class AirPlayReceiverView : System.Windows.Controls.UserControl
{
    private bool _watching, _busy;
    private bool _hevcAvailable, _checkedHevc, _updatingControls, _decodingArtwork;
    private AirPlayStatus? _status;
    private byte[]? _artworkTarget;
    private int _refreshQueued, _hasAudioSource;
    internal event Action? StatusUpdated;
    internal string FooterStatus => ReceiveStatusLabel.Text;
    internal void UseDeviceFooter() => ReceiverHeader.Visibility = Visibility.Collapsed;

    public AirPlayReceiverView()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateSubscription(IsVisible);
        Unloaded += (_, _) => UpdateSubscription(false);
        IsVisibleChanged += (_, _) => UpdateSubscription(IsLoaded && IsVisible);
        Refresh();
    }

    private void UpdateSubscription(bool visible)
    {
        if (_watching == visible) return;
        _watching = visible;
        if (visible)
        {
            AirPlayHost.Changed += ReceiverChanged;
            AirPlayHost.PreferencesChanged += SyncPreferences;
            L10n.Instance.PropertyChanged += LanguageChanged;
            SetDecoders();
            Refresh();
            if (!_checkedHevc) _ = ReadHevcSupportAsync();
        }
        else
        {
            AirPlayHost.Changed -= ReceiverChanged;
            AirPlayHost.PreferencesChanged -= SyncPreferences;
            L10n.Instance.PropertyChanged -= LanguageChanged;
            Volatile.Write(ref _status, null);
            UpdateArtwork(null);
        }
    }

    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { SetDecoders(); Refresh(); }

    private async Task ReadHevcSupportAsync()
    {
        _checkedHevc = true;
        _hevcAvailable = await Task.Run(() => MediaFoundationH264Decoder.SupportsHevc);
        if (_watching && _hevcAvailable) SetDecoders();
    }

    private void SetDecoders()
    {
        _updatingControls = true;
        try
        {
            string automatic = L10n.T("Phone.StreamDecoderAutomatic");
            MirrorDecoder.ItemsSource = _hevcAvailable ? new[] { automatic, "H.264 (AVC)", L10n.T("Phone.AirPlayHevcOption") }
                : new[] { automatic, "H.264 (AVC)" };
            SyncPreferences();
        }
        finally { _updatingControls = false; }
    }

    private void SyncPreferences()
    {
        bool wasUpdating = _updatingControls;
        _updatingControls = true;
        try
        {
            var (name, video, requireCode) = AirPlayHost.Preferences;
            if (!ReceiverName.IsKeyboardFocusWithin) ReceiverName.Text = name.Length == 0 ? AirPlayHost.DefaultName : name;
            RequireCode.IsChecked = requireCode;
            int selected = (int)video;
            MirrorDecoder.SelectedIndex = selected < MirrorDecoder.Items.Count ? selected : 0;
        }
        finally { _updatingControls = wasUpdating; }
    }

    private void Decoder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!CanEditPreferences || MirrorDecoder.SelectedIndex < 0) return;
        SavePreferences((AirPlayVideoPreference)MirrorDecoder.SelectedIndex);
    }

    private bool CanEditPreferences => _watching && !_updatingControls && ReceiverPreferences.IsEnabled;

    private void SavePreferences(AirPlayVideoPreference? choice = null)
    {
        string name = AirPlayIdentity.NormalizeName(ReceiverName.Text);
        var preferences = AirPlayHost.Preferences;
        if (name == AirPlayHost.DefaultName) name = "";
        AirPlayHost.SavePreferences(name, choice ?? preferences.Video, RequireCode.IsChecked == true);
        ReceiverName.Text = name.Length == 0 ? AirPlayHost.DefaultName : name;
    }

    private void RequireCode_Click(object sender, RoutedEventArgs e) { if (CanEditPreferences) SavePreferences(); }

    private void Name_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !CanEditPreferences) return;
        SavePreferences();
        e.Handled = true;
    }

    private void Name_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (CanEditPreferences) SavePreferences();
    }

    private void ReceiverChanged()
    {
        if (ReferenceEquals(Volatile.Read(ref _status), AirPlayHost.Status)
            && (Volatile.Read(ref _hasAudioSource) != 0) == (AirPlayHost.Source != null)) return;
        if (Dispatcher.CheckAccess()) { if (_watching) Refresh(); return; }
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_watching) Refresh();
        }));
    }

    private void Refresh()
    {
        var status = AirPlayHost.Status;
        Volatile.Write(ref _status, status);
        Volatile.Write(ref _hasAudioSource, AirPlayHost.Source != null ? 1 : 0);
        bool running = status.Stage is AirPlayStage.Waiting or AirPlayStage.Connected;
        AirPlayToggle.Content = L10n.T(running ? "Phone.StopReceiving" : "Phone.AirPlayStart");
        AirPlayToggle.Tag = running ? null : "Start";
        AirPlayToggle.IsEnabled = !_busy && status.Stage != AirPlayStage.Starting;
        ReceiverPreferences.IsEnabled = !_busy && !running && status.Stage != AirPlayStage.Starting;
        ReceiverPreferences.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        AirPlaySteps.Visibility = status.Stage == AirPlayStage.Connected ? Visibility.Collapsed : Visibility.Visible;
        MirrorDecoder.ToolTip = L10n.T("Phone.AirPlayDecoderHint");
        MirrorControls.Visibility = status.Mirroring ? Visibility.Visible : Visibility.Collapsed;
        MirrorTopmost.Content = L10n.T(status.Topmost ? "Phone.AirPlayUnpinWindow" : "Phone.AirPlayPinWindow");
        MirrorFullscreen.Content = L10n.T(status.Fullscreen ? "Phone.AirPlayExitFullscreen" : "Phone.AirPlayFullscreen");
        CodePanel.Visibility = status.Code != null ? Visibility.Visible : Visibility.Collapsed;
        ReceiveCode.Text = status.Code ?? "";
        ReceiveStatusLabel.Text = L10n.T(status.Stage switch
        {
            AirPlayStage.Starting => "Phone.ProcessingStatus",
            AirPlayStage.Waiting => "Phone.AirPlayWaitingShort",
            AirPlayStage.Connected => "Phone.ConnectedStatus",
            _ => "Phone.Disconnected"
        });
        ReceiveStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, status.ErrorKey != null ? "Theme.Fail" : status.Stage switch
        {
            AirPlayStage.Connected => "Theme.Success",
            AirPlayStage.Waiting or AirPlayStage.Starting => "Theme.Accent",
            _ => "Theme.TextSecondary"
        });
        AirPlayStatusText.Text = status.ErrorKey != null ? L10n.T(status.ErrorKey) : status.Stage switch
        {
            AirPlayStage.Waiting => status.Name,
            AirPlayStage.Connected => status.Mirroring
                ? string.Format(L10n.T(status.MirrorAudioUnavailable ? "Phone.AirPlayMirrorPictureOnly" : "Phone.AirPlayMirrorActive"), status.Sender)
                : status.Sender,
            _ => null
        };
        AirPlayStatusText.Visibility = string.IsNullOrWhiteSpace(AirPlayStatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
        AirPlayStatusText.SetResourceReference(TextBlock.ForegroundProperty, status.ErrorKey != null ? "Theme.Fail" : "Theme.TextSecondary");
        AirPlayStatusText.ToolTip = status.NetworkError is int code ? $"Winsock {code}" : null;
        RefreshTrack(status);
        StatusUpdated?.Invoke();
    }

    private void RefreshTrack(AirPlayStatus status)
    {
        var track = status.Stage == AirPlayStage.Connected ? status.Metadata : null;
        bool visible = track != null && (track.Title.Length > 0 || track.Artist.Length > 0 || track.Artwork != null);
        TrackPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TrackTitle.Text = track?.Title is { Length: > 0 } title ? title : status.Sender ?? "";
        TrackArtist.Text = track?.Artist ?? "";
        TrackArtist.Visibility = TrackArtist.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateArtwork(_watching && visible ? track?.Artwork : null);
    }

    private void UpdateArtwork(byte[]? artwork)
    {
        if (ReferenceEquals(_artworkTarget, artwork)) return;
        _artworkTarget = artwork;
        ArtworkImage.Source = null;
        ArtworkPlaceholder.Visibility = Visibility.Visible;
        if (artwork != null && !_decodingArtwork) _ = DecodeArtworkAsync();
    }

    private async Task DecodeArtworkAsync()
    {
        _decodingArtwork = true;
        try
        {
            while (_watching && _artworkTarget is { } artwork)
            {
                var image = await Task.Run(() => ReadArtwork(artwork));
                if (!ReferenceEquals(_artworkTarget, artwork)) continue;
                ArtworkImage.Source = image;
                ArtworkPlaceholder.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
                break;
            }
        }
        finally { _decodingArtwork = false; }
    }

    private static BitmapSource? ReadArtwork(byte[] artwork)
    {
        try { return PhoneAudioWindow.DecodeBoundedArtwork(artwork, maxSourceDimension: 2048); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException
            or System.IO.IOException or System.Runtime.InteropServices.COMException or FormatException)
        { return null; }
    }

    private void ShowMirror_Click(object sender, RoutedEventArgs e) => AirPlayHost.ShowMirror();
    private void Topmost_Click(object sender, RoutedEventArgs e) => AirPlayHost.SetMirrorTopmost(!AirPlayHost.Status.Topmost);
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => AirPlayHost.SetMirrorFullscreen(!AirPlayHost.Status.Fullscreen);

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_watching || _busy) return;
        bool running = AirPlayHost.Status.Stage is AirPlayStage.Waiting or AirPlayStage.Connected;
        if (!running && CanEditPreferences) SavePreferences();
        _busy = true;
        Refresh();
        try
        {
            if (running) await AirPlayHost.StopAsync();
            else await AirPlayHost.StartAsync();
        }
        finally
        {
            _busy = false;
            if (_watching) Refresh();
        }
    }
}
#endif

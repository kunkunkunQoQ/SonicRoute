#if SONICROUTE_PHONE
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

public partial class AirPlayReceiverView : System.Windows.Controls.UserControl
{
    private bool _watching, _busy;
    private bool _hevcAvailable, _checkingHevc, _updatingDecoders;
    private AirPlayStatus? _status;
    private int _refreshQueued;
    internal event Action? StatusUpdated;

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
            AirPlayHost.VideoPreferenceChanged += SyncDecoderChoice;
            L10n.Instance.PropertyChanged += LanguageChanged;
            SetDecoders();
            Refresh();
            if (!_checkingHevc) _ = ReadHevcSupportAsync();
        }
        else
        {
            AirPlayHost.Changed -= ReceiverChanged;
            AirPlayHost.VideoPreferenceChanged -= SyncDecoderChoice;
            L10n.Instance.PropertyChanged -= LanguageChanged;
        }
    }

    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { SetDecoders(); Refresh(); }

    private async Task ReadHevcSupportAsync()
    {
        _checkingHevc = true;
        _hevcAvailable = await Task.Run(() => MediaFoundationH264Decoder.SupportsHevc);
        if (_watching && _hevcAvailable) SetDecoders();
    }

    private void SetDecoders()
    {
        _updatingDecoders = true;
        try
        {
            string automatic = L10n.T("Phone.StreamDecoderAutomatic");
            MirrorDecoder.ItemsSource = _hevcAvailable ? new[] { automatic, "H.264 (AVC)", L10n.T("Phone.AirPlayHevcOption") }
                : new[] { automatic, "H.264 (AVC)" };
            SyncDecoderChoice();
        }
        finally { _updatingDecoders = false; }
    }

    private void SyncDecoderChoice()
    {
        bool wasUpdating = _updatingDecoders;
        _updatingDecoders = true;
        try
        {
            int selected = (int)AirPlayHost.VideoPreference;
            MirrorDecoder.SelectedIndex = selected < MirrorDecoder.Items.Count ? selected : 0;
        }
        finally { _updatingDecoders = wasUpdating; }
    }

    private void Decoder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_watching || _updatingDecoders || MirrorDecoder.SelectedIndex < 0) return;
        AirPlayHost.VideoPreference = (AirPlayVideoPreference)MirrorDecoder.SelectedIndex;
    }

    private void ReceiverChanged()
    {
        if (ReferenceEquals(Volatile.Read(ref _status), AirPlayHost.Status)) return;
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
        bool running = status.Stage is AirPlayStage.Waiting or AirPlayStage.Connected;
        AirPlayToggle.Content = L10n.T(running ? "Phone.StopReceiving" : "Phone.AirPlayStart");
        AirPlayToggle.IsEnabled = !_busy && status.Stage != AirPlayStage.Starting;
        MirrorDecoder.IsEnabled = !_busy && !running && status.Stage != AirPlayStage.Starting;
        MirrorDecoder.ToolTip = L10n.T("Phone.AirPlayDecoderHint");
        ShowMirror.Visibility = status.Mirroring ? Visibility.Visible : Visibility.Collapsed;
        AirPlayStatusText.Text = status.ErrorKey != null ? L10n.T(status.ErrorKey) : status.Stage switch
        {
            AirPlayStage.Starting => L10n.T("Phone.ProcessingStatus"),
            AirPlayStage.Waiting => string.Format(L10n.T("Phone.AirPlayWaiting"), status.Name),
            AirPlayStage.Connected => string.Format(L10n.T(status.MirrorAudioUnavailable ? "Phone.AirPlayMirrorPictureOnly"
                : status.Mirroring ? "Phone.AirPlayMirrorActive" : "Phone.AirPlayConnected"), status.Sender),
            _ => L10n.T("Phone.Disconnected")
        };
        AirPlayStatusText.ToolTip = status.NetworkError is int code ? $"Winsock {code}" : null;
        StatusUpdated?.Invoke();
    }

    private void ShowMirror_Click(object sender, RoutedEventArgs e) => AirPlayHost.ShowMirror();

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_watching || _busy) return;
        _busy = true;
        Refresh();
        try
        {
            if (AirPlayHost.Status.Stage is AirPlayStage.Waiting or AirPlayStage.Connected) await AirPlayHost.StopAsync();
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

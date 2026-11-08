#if SONICROUTE_PHONE
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SonicRoute.AndroidAudio;
using Button = System.Windows.Controls.Button;

namespace SonicRoute;

/// <summary>UI-thread state reconciliation; no polling, retries or duplicate audio starts.</summary>
internal sealed class VirtualDisplayPresentation(IVirtualDisplayClient client, AndroidAppDrag payload)
{
    private AndroidAudioSource? _source;
    private VirtualDisplayState _desired = client.State;
    private bool _closed, _dirty, _running;
    private Task _reconciliation = Task.CompletedTask;
    private VirtualDisplayStatusWindow? _status;
    internal bool Matches(AndroidAppDrag app) => AndroidDeviceIdentity.Matches(payload.Device, app.Device) && payload.App.Package == app.App.Package;
    internal async Task StopReceivingAsync()
    {
        _closed = true; _status?.Close(); _status = null;
        Queue(); await _reconciliation;
    }

    internal async Task WatchAsync()
    {
        client.StateChanged += Changed;
        Changed(client.State);
        try { await client.Completion; } catch { /* Client failure has its own notification. */ }
        finally
        {
            client.StateChanged -= Changed; _closed = true;
            _status?.Close(); _status = null;
            Queue(); await _reconciliation;
        }
    }
    private void Changed(VirtualDisplayState state)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => Changed(state)));
            return;
        }
        if (_closed) return;
        _desired = state;
        if (state is VirtualDisplayState.OnPhone or VirtualDisplayState.Recovering or VirtualDisplayState.Missing)
        {
            _status ??= new VirtualDisplayStatusWindow(client);
            _status.UpdateState(state);
        }
        else { _status?.Close(); _status = null; }
        Queue();
    }
    private void Queue()
    {
        _dirty = true;
        if (!_running) _reconciliation = ReconcileAsync();
    }
    private async Task ReconcileAsync()
    {
        _running = true;
        try
        {
            while (_dirty)
            {
                _dirty = false;
                bool receive = !_closed && _desired is VirtualDisplayState.Active or VirtualDisplayState.Starting or VirtualDisplayState.Unknown;
                try
                {
                    if (!receive && _source != null)
                    {
                        var released = _source; _source = null;
                        await AndroidAudioHost.Sources.ReleaseDisplayAudioAsync(released, payload.App.Package);
                    }
                    else if (receive && _source == null)
                        _source = await AndroidAudioHost.AddAudioAsync(payload.Device, payload.App, false, displayOwned: true);
                }
                catch (Exception ex) { Trace.WriteLine("Virtual display audio state: " + ex.Message); }
            }
        }
        finally { _running = false; }
    }
}

/// <summary>Reuses application theme/buttons. Target-process location events only while this notice is visible.</summary>
internal sealed class VirtualDisplayStatusWindow : VirtualDisplayOverlayWindow
{
    private readonly IVirtualDisplayClient _client;
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { Margin = new Thickness(0, 10, 0, 18), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _restore = new() { Content = L10n.T("Phone.DisplaySwitchToPc"), Padding = new Thickness(18, 8, 18, 8), HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
    private VirtualDisplayState _state;
    internal VirtualDisplayStatusWindow(IVirtualDisplayClient client) : base(client)
    {
        _client = client;
        SetResourceReference(BackgroundProperty, "Theme.SurfaceBg");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextPrimary");
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
        _restore.SetResourceReference(StyleProperty, "QuickPanelToolButton");
        _restore.SetResourceReference(ForegroundProperty, "Theme.TextPrimary");
        _restore.SetResourceReference(BackgroundProperty, "Theme.SurfaceBg");
        _restore.Click += (_, _) => { _restore.IsEnabled = false; _client.Restore(); };
        var body = new StackPanel { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(_title); body.Children.Add(_detail); body.Children.Add(_restore); Content = body;
        L10n.Instance.PropertyChanged += OnLanguageChanged;
        Closed += (_, _) => L10n.Instance.PropertyChanged -= OnLanguageChanged;
    }
    internal void UpdateState(VirtualDisplayState state)
    {
        _state = state;
        RefreshText();
        _restore.IsEnabled = state != VirtualDisplayState.Recovering;
        SetOverlayVisible(true);
    }
    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!IsClosed) RefreshText();
    }
    private void RefreshText()
    {
        _title.Text = _state switch { VirtualDisplayState.OnPhone => L10n.T("Phone.DisplayUsingOnPhone"), VirtualDisplayState.Recovering => L10n.T("Phone.DisplayRecovering"), _ => L10n.T("Phone.DisplayScreenPaused") };
        _detail.Text = _state == VirtualDisplayState.OnPhone ? L10n.T("Phone.DisplayUsingOnPhoneDetail") : _state == VirtualDisplayState.Recovering ? L10n.T("Phone.DisplayPleaseWait") : L10n.T("Phone.DisplayCanReopenHere");
        _restore.Content = _state == VirtualDisplayState.OnPhone ? L10n.T("Phone.DisplaySwitchToPc") : L10n.T("Phone.DisplayRestoreScreen");
    }
}
#endif

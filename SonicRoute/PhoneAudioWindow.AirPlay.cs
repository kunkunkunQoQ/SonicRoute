#if SONICROUTE_PHONE
using System.Threading;
using System.Windows;
using SonicRoute.AirPlay;

namespace SonicRoute;

public partial class PhoneAudioWindow
{
    internal static readonly object AirPlayChoice = new();
    private bool AirPlaySelected => ReferenceEquals(DeviceCombo?.SelectedItem, AirPlayChoice);

    private void UpdateDevicePane()
    {
        bool airPlay = AirPlaySelected;
        if (airPlay == (AirPlayPane.Visibility == Visibility.Visible)) return;
        if (airPlay)
        {
            _visibleRead?.Cancel();
            _catalogRead?.Cancel();
            StopMediaSubscription();
            _dragItem = null;
            ClearPcDropHighlight();
            PhoneApps.Visibility = PhoneEmptyPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (IsVisible && (_visibleRead == null || _visibleRead.IsCancellationRequested))
            {
                _visibleRead?.Dispose();
                _visibleRead = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            }
            PhoneApps.ClearValue(VisibilityProperty);
            ApplyFilter();
            Android_ReturnDragStateChanged();
        }
        DeviceMediaCard.Visibility = DeviceSearchCard.Visibility = airPlay ? Visibility.Collapsed : Visibility.Visible;
        DeviceKindIcon.Data = airPlay ? AutomationIcons.AirPlay : AutomationIcons.Phone;
        ConnectionText.Margin = new Thickness(airPlay ? 0 : 19, 0, 0, 0);
        ConnectionText.Width = airPlay ? 56 : 37;
        RefreshButton.Visibility = airPlay ? Visibility.Collapsed : Visibility.Visible;
        RefreshColumn.Width = new GridLength(airPlay ? 0 : 36);
        RefreshGap.Width = new GridLength(airPlay ? 0 : 4);
        AllowDrop = !airPlay;
        AirPlayPane.Visibility = airPlay ? Visibility.Visible : Visibility.Collapsed;
        if (airPlay) UpdateAirPlayFooter();
    }

    private void UpdateAirPlayFooter()
    {
        if (!AirPlaySelected || _closed) return;
        ConnectionSignal.Visibility = ConnectionUsbIcon.Visibility = Visibility.Collapsed;
        var status = AirPlayHost.Status;
        ConnectionText.Text = L10n.T(status.Stage switch
        {
            AirPlayStage.Connected => "Phone.ConnectedStatus",
            AirPlayStage.Waiting or AirPlayStage.Starting => "Phone.AirPlayWaitingShort",
            _ => "Phone.Disconnected"
        });
        ConnectionText.ToolTip = status.Sender ?? status.Name;
    }
}
#endif

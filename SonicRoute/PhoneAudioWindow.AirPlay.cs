#if SONICROUTE_PHONE
using System.Threading;
using System.Windows;
using SonicRoute.Core.Models;

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
        ConnectionText.Visibility = airPlay ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.Visibility = airPlay ? Visibility.Collapsed : Visibility.Visible;
        RefreshColumn.Width = new GridLength(airPlay ? 0 : 36);
        RefreshGap.Width = new GridLength(airPlay ? 0 : 4);
        AirPlayPane.Visibility = airPlay ? Visibility.Visible : Visibility.Collapsed;
        UpdateReturnDropText();
        if (airPlay) UpdateAirPlayFooter();
        else DeviceKindGrip.ToolTip = null;
    }

    private static bool IsAirPlayDrag(AudioAppInfo? app) => app?.ProcessName == AirPlayHost.AppKey;

    private bool CanDragAirPlayToWindows() => AirPlaySelected && AirPlayHost.Source != null;

    private void DragAirPlayToWindows()
    {
        if (!AirPlaySelected || AirPlayHost.Source is not { } source) return;
        var app = (App)System.Windows.Application.Current;
        var data = new System.Windows.DataObject(AirPlayHost.WindowsDragFormat, ApplicationAudio.Describe(source));
        try
        {
            var result = AppIconDragController.Run(DeviceKindGrip, data, PhoneSourceIcon.AirPlayImage, source.Name);
            if (result.Outside && AirPlayHost.Find(source.Id) != null) AirPlayHost.ShowMirror();
        }
        finally
        {
            app.QuickPanelInstance?.ClearAndroidDropHighlight();
            app.QuickPanelInstance?.CloseAfterFocusLeaves();
        }
    }

    private void UpdateReturnDropText() => UpdateDropCellText(_androidReturnDropZone,
        L10n.T(AirPlaySelected ? "Phone.AirPlayDropExit" : "Phone.ReturnToPhoneTitle"),
        L10n.T(AirPlaySelected ? "Phone.AirPlayDropExitDesc" : "Phone.ReturnToPhoneDesc"));

    private void UpdateAirPlayFooter()
    {
        if (!AirPlaySelected || _closed) return;
        ConnectionSignal.Visibility = ConnectionUsbIcon.Visibility = Visibility.Collapsed;
        DeviceKindGrip.ToolTip = AirPlayHost.Source != null ? L10n.T("Phone.AirPlayDragToWindows") : null;
    }
}
#endif

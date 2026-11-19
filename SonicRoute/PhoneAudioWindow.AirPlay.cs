#if SONICROUTE_PHONE
using System;
using System.Threading;
using System.Windows;
using SonicRoute.Core.Models;
using SonicRoute.WindowsLink;

namespace SonicRoute;

public partial class PhoneAudioWindow
{
    internal static readonly object AirPlayChoice = new();
    internal static readonly object WindowsLinkChoice = new();
    private bool AirPlaySelected => ReferenceEquals(DeviceCombo?.SelectedItem, AirPlayChoice);
    private bool WindowsLinkSelected => CurrentWindowsPeer != null || ReferenceEquals(DeviceCombo?.SelectedItem, WindowsLinkChoice);
    private bool ExternalDeviceSelected => AirPlaySelected || WindowsLinkSelected;
    private LinkPeer? CurrentWindowsPeer => DeviceCombo?.SelectedItem as LinkPeer;
    private WindowsLinkConnectView? _windowsLinkView;

    private void UpdateDevicePane()
    {
        bool airPlay = AirPlaySelected;
        bool windows = WindowsLinkSelected;
        var peer = CurrentWindowsPeer;
        if (_windowsLinkRowPeerId != peer?.Id)
        {
            ResetWindowsLinkRows();
            _windowsLinkRowPeerId = peer?.Id;
        }
        bool showWindowsPane = ReferenceEquals(DeviceCombo?.SelectedItem, WindowsLinkChoice);
        bool external = airPlay || windows;
        bool wasAirPlay = AirPlayPane.Visibility == Visibility.Visible;
        bool wasExternal = DeviceMediaCard.Visibility == Visibility.Collapsed;
        if (showWindowsPane)
        {
            if (_windowsLinkView == null)
            {
                _windowsLinkView = new WindowsLinkConnectView(compact: true);
                _windowsLinkView.UseDeviceFooter();
                _windowsLinkView.StatusUpdated += UpdateWindowsLinkFooter;
            }
            WindowsLinkPane.Content = _windowsLinkView;
        }

        AirPlayPane.Visibility = airPlay ? Visibility.Visible : Visibility.Collapsed;
        WindowsLinkPane.Visibility = showWindowsPane ? Visibility.Visible : Visibility.Collapsed;
        if (peer == null)
        {
            WindowsLinkSystemAudioButton.Visibility = Visibility.Collapsed;
            WindowsLinkAudioEmpty.Visibility = Visibility.Collapsed;
        }
        if (external)
        {
            if (!wasExternal)
            {
                _visibleRead?.Cancel();
                _catalogRead?.Cancel();
                StopMediaSubscription();
                _dragItem = null;
                _windowsDragItem = null;
            }
            if (!wasExternal || wasAirPlay != airPlay) ClearPcDropHighlight();
            PhoneEmptyPanel.Visibility = Visibility.Collapsed;
            PhoneApps.Visibility = peer != null ? Visibility.Visible : Visibility.Collapsed;
            if (peer != null) UpdateWindowsLinkRows();
            else
            {
                ResetWindowsLinkRows();
                PhoneApps.ItemsSource = null;
            }
        }
        else if (wasExternal)
        {
            _windowsDragItem = null;
            if (IsVisible && (_visibleRead == null || _visibleRead.IsCancellationRequested))
            {
                _visibleRead?.Dispose();
                _visibleRead = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            }
            PhoneApps.ClearValue(VisibilityProperty);
            ApplyFilter();
            Android_ReturnDragStateChanged();
        }
        DeviceMediaCard.Visibility = external ? Visibility.Collapsed : Visibility.Visible;
        WindowsLinkViewChoices.Visibility = peer != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateWindowsLinkSearchHint();
        DeviceSearchCard.Visibility = !external || peer != null ? Visibility.Visible : Visibility.Collapsed;
        DeviceKindIcon.Data = airPlay ? AutomationIcons.AirPlay : windows ? AutomationIcons.Monitor : AutomationIcons.Phone;
        bool showFooterAction = !external || peer != null;
        RefreshButton.Visibility = showFooterAction ? Visibility.Visible : Visibility.Collapsed;
        RefreshColumn.Width = new GridLength(showFooterAction ? 36 : 0);
        RefreshGap.Width = new GridLength(showFooterAction ? 4 : 0);
        if (windows) ConnectionSignal.Visibility = ConnectionUsbIcon.Visibility = Visibility.Collapsed;
        UpdateReturnDropText();
        if (airPlay) UpdateAirPlayFooter();
        else
        {
            DeviceKindGrip.ToolTip = null;
            UpdateConnectionStatusText();
        }
    }

    private static bool IsAirPlayDrag(AudioAppInfo? app) => app?.ProcessName == AirPlayHost.AppKey;

    private static bool CanSendWindowsLinkAudio(AudioAppInfo? app) =>
        LinkAudioCapture.IsSupported && app is { IsExternal: false, ProcessId: > 0 }
        && app.ProcessId != Environment.ProcessId
        && !string.Equals(app.ProcessName, "Idle", StringComparison.OrdinalIgnoreCase);

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

    private void UpdateReturnDropText(WindowsLinkAppDrag? returning = null)
    {
        returning ??= WindowsLinkHub.DragSource;
        if (returning != null && WindowsLinkCanReturn(returning))
        {
            UpdateDropCellText(_androidReturnDropZone,
                string.Format(L10n.T("Phone.WindowsLinkDropReturnTitle"), returning.Peer.DisplayName),
                L10n.T(returning.Window ? "Phone.WindowsLinkDropReturnDesc" : "Phone.ReturnToPhoneDesc"));
            return;
        }
        if (CurrentWindowsPeer is { } peer)
        {
            UpdateDropCellText(_androidReturnDropZone,
                string.Format(L10n.T("Phone.WindowsLinkDropSendTitle"), peer.DisplayName),
                L10n.T("Phone.WindowsLinkDropSendDesc"));
            return;
        }
        if (WindowsLinkSelected)
        {
            UpdateDropCellText(_androidReturnDropZone, L10n.T("Phone.WindowsLinkReady"), L10n.T("Phone.WindowsLinkSteps"));
            return;
        }
        UpdateDropCellText(_androidReturnDropZone, L10n.T(AirPlaySelected ? "Phone.AirPlayDropExit" : "Phone.ReturnToPhoneTitle"),
            L10n.T(AirPlaySelected ? "Phone.AirPlayDropExitDesc" : "Phone.ReturnToPhoneDesc"));
    }

    private void UpdateAirPlayFooter()
    {
        if (!AirPlaySelected || _closed) return;
        ConnectionSignal.Visibility = ConnectionUsbIcon.Visibility = Visibility.Collapsed;
        ConnectionText.Text = AirPlayReceiverPane.FooterStatus;
        ConnectionText.ToolTip = ConnectionText.Text;
        DeviceKindGrip.ToolTip = AirPlayHost.Source != null ? L10n.T("Phone.AirPlayDragToWindows") : null;
    }
}
#endif

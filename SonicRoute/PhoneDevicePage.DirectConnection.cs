#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    private sealed record SavedConnectionChoice(AndroidSavedConnection? Connection)
    {
        public string Label => Connection == null ? "" : Connection.Name == Connection.Address
            ? Connection.Address : $"{Connection.Name} · {Connection.Address}";
    }

    private readonly Func<CancellationToken, Task<IReadOnlyList<AndroidMdnsService>>> _discoverSavedDevices = AndroidMdnsDiscovery.DiscoverConnectionsAsync;
    private bool _attemptedSavedDeviceSearch;
    private bool _manualSavedDeviceSearch;
    private string _savedDeviceSearchStatus = "";
    private CancellationTokenSource? _savedDeviceSearchOperation;

    private bool CanSearchSavedDevices => !_disposed && _active && !_busy && IsLoaded
        && DirectConnectCard is { IsVisible: true } && IsConnectionPage;

    private void UpdateSavedDeviceSearch()
    {
        if (!_attemptedSavedDeviceSearch && CanSearchSavedDevices) _ = SearchSavedDevicesAsync(manual: false);
    }

    private async void SearchSavedDevices_Click(object sender, RoutedEventArgs e) => await SearchSavedDevicesAsync(manual: true);

    private async Task SearchSavedDevicesAsync(bool manual)
    {
        if (!CanSearchSavedDevices) return;
        if (manual)
        {
            _manualSavedDeviceSearch = true;
            _savedDeviceSearchStatus = "Phone.ProcessingStatus";
            RefreshSavedDeviceSearchStatus();
        }
        if (_savedDeviceSearchOperation != null) return;

        _attemptedSavedDeviceSearch = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _savedDeviceSearchOperation = operation;
        IReadOnlyList<AndroidSavedConnection> found = [];
        try
        {
            found = await Task.Run(async () =>
            {
                var services = await _discoverSavedDevices(operation.Token).ConfigureAwait(false);
                var candidates = AndroidAudioHost.Phone.FindWirelessConnectionCandidates(services, PhoneAudioWindowCache.Devices ?? []);
                return await AndroidWirelessConnection.VerifySavedConnectionsAsync(candidates, operation.Token).ConfigureAwait(false);
            }, operation.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or InvalidOperationException)
        { System.Diagnostics.Trace.WriteLine("Phone saved device discovery: " + ex.GetType().Name); }
        finally
        {
            if (ReferenceEquals(_savedDeviceSearchOperation, operation))
            {
                _savedDeviceSearchOperation = null;
                CompleteSavedDeviceSearch(found);
            }
        }
    }

    private void CompleteSavedDeviceSearch(IReadOnlyList<AndroidSavedConnection> found)
    {
        if (!CanSearchSavedDevices) return;
        bool manual = _manualSavedDeviceSearch;
        _manualSavedDeviceSearch = false;
        if (!manual && (ConnectAddressBox.IsKeyboardFocusWithin || ConnectAddressBox.Text.Length > 0)) return;

        if (found.Count > 0)
        {
            var selected = (SavedConnectDevices.SelectedItem as SavedConnectionChoice)?.Connection;
            var choices = found.Select(connection => new SavedConnectionChoice(connection))
                .Append(new SavedConnectionChoice(Connection: null)).ToArray();
            SavedConnectDevices.ItemsSource = choices;
            int index = selected == null ? -1 : Array.FindIndex(choices, c => c.Connection is { } connection
                && AndroidDeviceIdentity.MatchesSavedDevice(connection.Device, selected.Device));
            SavedConnectDevices.SelectedIndex = Math.Max(0, index);
            SavedConnectDevices.Visibility = Visibility.Visible;
            ConnectAddressBox.Visibility = Visibility.Collapsed;
        }
        else if (manual) ShowManualConnectAddress();

        _savedDeviceSearchStatus = manual && found.Count == 0 ? "Phone.SavedDevicesNotFound" : "";
        RefreshSavedDeviceSearchStatus();
    }

    private void ShowManualConnectAddress()
    {
        SavedConnectDevices.IsDropDownOpen = false;
        SavedConnectDevices.Visibility = Visibility.Collapsed;
        SavedConnectDevices.ItemsSource = null;
        ConnectAddressBox.Visibility = Visibility.Visible;
    }

    private void SavedConnectDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SavedConnectDevices.SelectedItem is not SavedConnectionChoice { Connection: null }) return;
        ShowManualConnectAddress();
        RefreshSavedDeviceSearchStatus();
        ConnectAddressBox.Focus();
    }

    private void DirectConnect_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (SavedDeviceSearchStatus == null) return;
        if (e.NewValue is true) UpdateSavedDeviceSearch();
        else ResetSavedDeviceSearch();
    }

    private void ResetSavedDeviceSearch()
    {
        StopSavedDeviceSearch();
        _attemptedSavedDeviceSearch = false;
        ShowManualConnectAddress();
        RefreshSavedDeviceSearchStatus();
    }

    private void StopSavedDeviceSearch()
    {
        _savedDeviceSearchOperation?.Cancel();
        _savedDeviceSearchOperation = null;
        _manualSavedDeviceSearch = false;
        _savedDeviceSearchStatus = "";
    }

    private void RefreshSavedDeviceSearchStatus()
    {
        SearchSavedDevicesButton.IsEnabled = !_busy && !_manualSavedDeviceSearch;
        ConnectButton.IsEnabled = !_busy && (!_manualSavedDeviceSearch || !SavedConnectDevices.IsVisible);
        SavedDeviceSearchStatus.Text = _savedDeviceSearchStatus.Length == 0 ? "" : L10n.T(_savedDeviceSearchStatus);
        SavedDeviceSearchStatus.Visibility = _savedDeviceSearchStatus.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var connection = (SavedConnectDevices.SelectedItem as SavedConnectionChoice)?.Connection;
        string address = ConnectAddressBox.Text.Trim();
        await RunAsync(async token =>
        {
            try
            {
                var result = connection is { } saved
                    ? await Wireless.ReconnectAsync(saved.Device, saved.Address, token)
                    : await Wireless.ConnectAsync(address, token);
                ApplyConnection(result, token);
            }
            catch (Exception ex) when (connection != null && !token.IsCancellationRequested && (ex is IOException or SocketException))
            {
                if (IsConnectionPage) ShowManualConnectAddress();
                throw;
            }
        }, "Phone.ConnectedStatus");
    }
}
#endif

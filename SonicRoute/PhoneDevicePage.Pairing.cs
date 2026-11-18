#if SONICROUTE_PHONE
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    private CancellationTokenSource? _qrOperation;
    private Task? _qrTask;
    private bool _qrRetry;
    private bool CanShowQr => !_disposed && _active && !_busy && IsLoaded && IsVisible
        && IsConnectionPage && ConnectionPanel.IsVisible && PairingCard.Visibility == Visibility.Visible;

    private void PairingLoaded(object sender, RoutedEventArgs e) { UpdateQrPairing(); UpdateStreamingVisibility(); }
    private void PairingUnloaded(object sender, RoutedEventArgs e) { StopQrPairing(); CancelEncoderRead(); }
    private void PairingVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { UpdateQrPairing(); UpdateStreamingVisibility(); }
    private void QrPair_Click(object sender, RoutedEventArgs e)
    {
        if (!CanShowQr) return;
        StopQrPairing();
        UpdateQrPairing();
    }

    private void UpdateQrPairing()
    {
        if (PairQrImage == null) return; // Visibility can change during InitializeComponent.
        if (!CanShowQr) { StopQrPairing(); return; }
        if (_qrOperation != null) return;
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _qrOperation = operation;
        _qrTask = WatchQrPairingAsync(operation);
    }

    private void StopQrPairing()
    {
        _qrOperation?.Cancel();
        if (PairQrImage == null) return;
        PairQrImage.Source = null;
        PairQrImage.Visibility = Visibility.Collapsed;
    }

    private void RefreshQrStatus() => QrStatus.SetCurrentValue(System.Windows.Controls.TextBlock.TextProperty,
        L10n.T(_qrRetry ? "Phone.QrRetryHint" : "Phone.QrRefreshHint"));

    private async Task WatchQrPairingAsync(CancellationTokenSource operation)
    {
        // One owned session; rapid refreshes wait for its cancellation/transport cleanup.
        await Task.Yield();
        CancellationToken token = operation.Token;
        int failures = 0;
        try
        {
            while (CanShowQr && !token.IsCancellationRequested)
            {
                try
                {
                    _qrRetry = false;
                    RefreshQrStatus();
                    var (qr, bitmap) = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        var qr = _createQr();
                        using var code = QRCodeGenerator.GenerateQrCode(qr.Payload, QRCodeGenerator.ECCLevel.M);
                        using var renderer = new PngByteQRCode(code);
                        using var stream = new ImageDataStream(renderer.GetGraphic(6));
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                        return (qr, bitmap);
                    }, token);
                    token.ThrowIfCancellationRequested();
                    if (!CanShowQr) return;
                    PairQrImage.Source = bitmap;
                    PairQrImage.Visibility = Visibility.Visible;
                    var wireless = Wireless;
                    // Network-interface discovery and its initial I/O must also stay off the UI thread.
                    var paired = await Task.Run(() => wireless.PairQrAsync(qr, token), token);
                    token.ThrowIfCancellationRequested();
                    if (!CanShowQr) return;
                    await RunAsync(async connectToken =>
                    {
                        SetOperationStatusKey("Phone.StatusPairedConnecting");
                        ApplyConnection(await wireless.FinishPairingAsync(paired, connectToken), connectToken);
                    }, "Phone.ConnectedStatus", fromQr: true);
                    return;
                }
                catch (TimeoutException ex) when (!token.IsCancellationRequested
                    && AndroidError.TryGetCode(ex, out var code) && code == AndroidErrorCode.QrExpired)
                {
                    // PairQrAsync owns the two-minute expiry. No UI timer or countdown is needed.
                    failures = 0;
                    StopQrImage();
                }
                catch (Exception) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("Phone QR pairing retry: " + ex.GetType().Name);
                    StopQrImage();
                    _qrRetry = true;
                    RefreshQrStatus();
                    failures = Math.Min(failures + 1, 4);
                    await Task.Delay(TimeSpan.FromSeconds(failures switch { 1 => 5, 2 => 15, 3 => 30, _ => 60 }), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_qrOperation, operation))
            {
                _qrOperation = null;
                _qrTask = null;
                StopQrImage();
            }
            operation.Dispose();
            if (CanShowQr) UpdateQrPairing();
        }
    }

    private void StopQrImage()
    {
        PairQrImage.Source = null;
        PairQrImage.Visibility = Visibility.Collapsed;
    }
}
#endif

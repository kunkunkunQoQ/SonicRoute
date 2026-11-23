using System;
using System.Windows;
#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Compat;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
#endif

namespace SonicRoute;

public partial class App
{
#if SONICROUTE_PHONE
    private PhoneClipboardSync? _phoneClipboard;
    private Task<PhoneClipboardSync>? _phoneClipboardLoad;
    private bool _usbArrivalWanted;
    private bool _usbArrivalScanRunning;
    private bool _usbArrivalRescanRequested;
    private CancellationTokenSource? _usbArrivalDebounce;
    private CancellationTokenSource? _usbArrivalShutdown;
    private Action<IReadOnlyCollection<AndroidDevice>>? _androidUsbChanged;
    private readonly Dictionary<string, AndroidDevice> _removedPanelUsbDevices = new(StringComparer.OrdinalIgnoreCase);
    private HwndSource? _usbSink;
    private IntPtr _usbRegistration;
    private DispatcherTimer? _usbPanelDebounce;
    internal bool AndroidUsbNotificationsAvailable => !_exiting && _activateSink is { IsDisposed: false };
    internal event Action<IReadOnlyCollection<AndroidDevice>>? AndroidUsbInterfaceChanged
    {
        add
        {
            Dispatcher.VerifyAccess();
            if (value == null || !AndroidUsbNotificationsAvailable) return;
            _androidUsbChanged += value;
            SyncUsbDeviceHook();
        }
        remove
        {
            Dispatcher.VerifyAccess();
            _androidUsbChanged -= value;
            if (_androidUsbChanged == null) DetachPanelUsbState();
            SyncUsbDeviceHook();
        }
    }

    private void SyncUsbDeviceHook()
    {
        bool wantPanel = _androidUsbChanged != null;
        bool wantClipboard = !_exiting && _phoneClipboard?.HasEnabledUsbTargets == true;
        _usbArrivalWanted = wantClipboard;
        if (!wantClipboard) DetachUsbClipboardState();
        bool wantAny = !_exiting && _activateSink is { IsDisposed: false } && (wantPanel || wantClipboard);
        if (wantAny)
        {
            if (wantClipboard && _usbArrivalShutdown == null)
                _usbArrivalShutdown = new CancellationTokenSource();
            if (_usbSink == null)
            {
                _usbSink = _activateSink!;
                _usbRegistration = WinUsbDeviceWatcher.RegisterAdbInterface(_usbSink.Handle);
                _usbSink.AddHook(SharedUsbDeviceChangeMessage);
            }
            return;
        }
        DetachSharedUsbHook();
    }

    private IntPtr SharedUsbDeviceChangeMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmDeviceChange = 0x0219, dbtDevNodesChanged = 0x0007, dbtDeviceArrival = 0x8000;
        if (message != wmDeviceChange) return IntPtr.Zero;
        bool adbChange = WinUsbDeviceWatcher.IsAdbInterfaceChange(message, wParam, lParam);
        if (_androidUsbChanged != null
            && (adbChange || (_usbRegistration == IntPtr.Zero && wParam == (IntPtr)dbtDevNodesChanged)))
        {
            if (adbChange && WinUsbDeviceWatcher.ReadChangedInterfacePath(lParam) is { } path)
            {
                const int removalComplete = 0x8004;
                if (wParam != (IntPtr)removalComplete) _removedPanelUsbDevices.Remove(path);
                else if (PhoneAudioWindowCache.Discovery.CurrentDevices?.FirstOrDefault(d =>
                    string.Equals(d.UsbInterfacePath, path, StringComparison.OrdinalIgnoreCase)) is { } removed)
                    _removedPanelUsbDevices[path] = removed;
            }
            if (_usbPanelDebounce == null)
            {
                _usbPanelDebounce = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
                _usbPanelDebounce.Tick += PublishPanelUsbChange;
            }
            _usbPanelDebounce.Stop();
            _usbPanelDebounce.Start();
        }
        if (_usbArrivalWanted)
        {
            int evt = unchecked((int)wParam.ToInt64());
            if (evt == dbtDeviceArrival || evt == dbtDevNodesChanged)
                QueueUsbClipboardArrivalScan();
        }
        return IntPtr.Zero;
    }
    private void PublishPanelUsbChange(object? sender, EventArgs args)
    {
        _usbPanelDebounce?.Stop();
        var observed = PhoneAudioWindowCache.Discovery.CurrentDevices;
        // A second edge can supersede an in-flight failed scan. Retain the first
        // removal until it is reflected in discovery or the interface reappears.
        foreach (var path in _removedPanelUsbDevices.Where(p => observed?.Any(device =>
            AndroidDeviceIdentity.SameAttachment(device, p.Value)) != true).Select(p => p.Key).ToArray())
            _removedPanelUsbDevices.Remove(path);
        _androidUsbChanged?.Invoke(_removedPanelUsbDevices.Values.ToArray());
    }
    private void DetachPanelUsbState()
    {
        _removedPanelUsbDevices.Clear();
        if (_usbPanelDebounce != null)
        {
            _usbPanelDebounce.Stop();
            _usbPanelDebounce.Tick -= PublishPanelUsbChange;
            _usbPanelDebounce = null;
        }
        _androidUsbChanged = null;
    }
    private void DetachSharedUsbHook()
    {
        DetachPanelUsbState();
        _usbArrivalWanted = false;
        DetachUsbClipboardState();
        if (_usbSink is { IsDisposed: false }) _usbSink.RemoveHook(SharedUsbDeviceChangeMessage);
        _usbSink = null;
        if (_usbRegistration != IntPtr.Zero)
        {
            WinUsbDeviceWatcher.UnregisterDeviceNotification(_usbRegistration);
            _usbRegistration = IntPtr.Zero;
        }
    }

    internal event Action? PhoneClipboardChanged;
    internal bool HasActivePhoneClipboard => _phoneClipboard?.HasActiveWork == true;
    private static string PhoneClipboardSettingsPath =>
        Path.Combine(Path.GetDirectoryName(ConfigService.ConfigPath)!, "phone-clipboard.json");
    private void StartPhoneClipboard()
    {
        string path = PhoneClipboardSettingsPath;
        if (!File.Exists(path)) return;
        _ = StartPhoneClipboardIfConfiguredAsync(path);
    }
    private async Task StartPhoneClipboardIfConfiguredAsync(string path)
    {
        bool configured = await Task.Run(() => HasConfiguredClipboardDevices(path)).ConfigureAwait(true);
        if (!configured || _exiting) return;
        _ = StartConfiguredPhoneClipboard();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task<PhoneClipboardSync> StartConfiguredPhoneClipboard() => PreparePhoneClipboardAsync();
    private static bool HasConfiguredClipboardDevices(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > 64 * 1024) return false;
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("Version", out var versionProp)
                || versionProp.ValueKind != JsonValueKind.Number
                || !versionProp.TryGetInt32(out int version)
                || version != 1)
                return false;
            if (!root.TryGetProperty("EnabledDevices", out var devicesProp)
                || devicesProp.ValueKind != JsonValueKind.Array)
                return false;
            int count = devicesProp.GetArrayLength();
            if (count == 0 || count > 32) return false;
            foreach (var item in devicesProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("Serial", out var serialProp) || serialProp.ValueKind != JsonValueKind.String)
                    continue;
                string? serial = serialProp.GetString();
                if (string.IsNullOrWhiteSpace(serial) || serial.Length > 512) continue;
                if (item.TryGetProperty("HardwareId", out var hwProp) && hwProp.ValueKind != JsonValueKind.Null)
                {
                    if (hwProp.ValueKind != JsonValueKind.String) continue;
                    if ((hwProp.GetString()?.Length ?? 0) > 128) continue;
                }
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
    internal Task<PhoneClipboardSync> PreparePhoneClipboardAsync() => _phoneClipboardLoad ??= LoadPhoneClipboardAsync();
    private async Task<PhoneClipboardSync> LoadPhoneClipboardAsync()
    {
        var sync = _phoneClipboard = new PhoneClipboardSync(PhoneClipboardSettingsPath, Dispatcher,
            new DesktopClipboardLink(_activateSink), () => new SonicRoute.AndroidAudio.AndroidClipboardClient(AndroidAudioHost.Adb),
            enabled => enabled.Any(d => d.IsWifi) || SonicRoute.AndroidAudio.AdbDevicePresence.HasPresentUsbDevices());
        sync.Changed += OnPhoneClipboardChanged;
        try { await sync.InitializeAsync(); }
        catch (OperationCanceledException) { }
        SyncUsbDeviceHook();
        return sync;
    }
    private void OnPhoneClipboardChanged()
    {
        SyncUsbDeviceHook();
        PhoneClipboardChanged?.Invoke();
    }
    private void DetachUsbClipboardState()
    {
        _usbArrivalRescanRequested = false;
        CancelUsbArrivalDebounce();
        if (_usbArrivalShutdown != null)
        {
            try { _usbArrivalShutdown.Cancel(); } catch (ObjectDisposedException) { }
            _usbArrivalShutdown.Dispose();
            _usbArrivalShutdown = null;
        }
    }
    private void CancelUsbArrivalDebounce()
    {
        if (_usbArrivalDebounce == null) return;
        try { _usbArrivalDebounce.Cancel(); } catch (ObjectDisposedException) { }
        _usbArrivalDebounce.Dispose();
        _usbArrivalDebounce = null;
    }
    private void QueueUsbClipboardArrivalScan()
    {
        if (_exiting || !_usbArrivalWanted || _usbSink == null || _phoneClipboard == null || !_phoneClipboard.ShouldScanOnUsbArrival())
            return;
        if (_usbArrivalScanRunning)
        {
            _usbArrivalRescanRequested = true;
            return;
        }
        CancelUsbArrivalDebounce();
        var cts = _usbArrivalDebounce = new CancellationTokenSource();
        _ = RunUsbClipboardArrivalScanAsync(cts);
    }
    private async Task RunUsbClipboardArrivalScanAsync(CancellationTokenSource debounceCts)
    {
        var shutdownToken = _usbArrivalShutdown?.Token ?? new CancellationToken(true);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(debounceCts.Token, shutdownToken);
            await Task.Delay(250, linked.Token);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_usbArrivalDebounce, debounceCts))
            {
                _usbArrivalDebounce = null;
                debounceCts.Dispose();
            }
            return;
        }
        if (ReferenceEquals(_usbArrivalDebounce, debounceCts))
        {
            _usbArrivalDebounce = null;
            debounceCts.Dispose();
        }
        if (_exiting || !_usbArrivalWanted || _usbSink == null || shutdownToken.IsCancellationRequested
            || _phoneClipboard == null || !_phoneClipboard.ShouldScanOnUsbArrival())
            return;
        if (_usbArrivalScanRunning)
        {
            _usbArrivalRescanRequested = true;
            return;
        }
        try
        {
            bool present = await Task.Run(SonicRoute.AndroidAudio.AdbDevicePresence.HasPresentUsbDevices, shutdownToken);
            if (!present || _exiting || !_usbArrivalWanted || _usbSink == null || shutdownToken.IsCancellationRequested
                || _phoneClipboard == null || !_phoneClipboard.ShouldScanOnUsbArrival())
                return;
            _usbArrivalScanRunning = true;
            try
            {
                await PhoneAudioWindowCache.ReadDevicesAsync(shutdownToken);
            }
            finally
            {
                _usbArrivalScanRunning = false;
            }
            if (_usbArrivalRescanRequested && !_exiting && _usbArrivalWanted && _usbSink != null && !shutdownToken.IsCancellationRequested)
            {
                _usbArrivalRescanRequested = false;
                QueueUsbClipboardArrivalScan();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("USB clipboard arrival check: " + ex.Message);
        }
    }
    internal bool PhoneClipboardEnabled(SonicRoute.AndroidAudio.AndroidDevice? device) => _phoneClipboard?.IsEnabled(device) == true;
    internal string PhoneClipboardStatus(SonicRoute.AndroidAudio.AndroidDevice? device) => _phoneClipboard?.Status(device) ?? L10n.T("Phone.ClipboardDefaultHint");
    internal void PhoneClipboardProfilesChanged() => _phoneClipboard?.ProfilesChanged();
    internal void ShowPhoneManagement()
    {
        ShowMainWindow();
        _mainWindow?.SelectPhoneManagement();
    }
    private PhoneAudioWindow? _phoneAudioWindow;
    private QuickPanelModernWindow? _openingPhonePanel;
    private bool _androidDisplayNotifications;
    private readonly Dictionary<SonicRoute.AndroidAudio.IVirtualDisplayClient, VirtualDisplayPresentation> _displayAudioWatches = new();
    internal bool KeepQuickPanelForAndroid => AndroidAudioHost.IsDragging
        || AppIconDragController.IsDesktopDragging || _phoneAudioWindow?.IsActive == true
        || _phoneAudioWindow?.IsKeyboardFocusWithin == true;
    internal bool IsPhoneAudioVisible => _phoneAudioWindow?.IsVisible == true;
    internal bool IsPhoneWindowOpen => _phoneAudioWindow != null || _openingPhonePanel != null;
    internal Rect? PhoneAudioRelativeBounds(QuickPanelModernWindow panel) => _phoneAudioWindow is { IsVisible: true } phone && phone.Owner == panel
        ? new Rect(phone.Left - panel.Left, phone.Top - panel.Top, phone.Width, phone.Height) : null;

    internal async Task OpenAndroidDisplayAsync(AndroidAppDrag payload, int screenX, int screenY)
    {
        if (_exiting) return;
        if (payload.App.IsDeviceAudio)
            payload = payload with { App = payload.App with { Name = ApplicationAudio.AndroidName(payload.App, payload.Device) } };
        try
        {
            payload = payload with { Device = await AndroidAudioHost.ResolveOperationDeviceAsync(payload.Device, AndroidErrorCode.DisplayPhoneNotConnected) };
            if (!_androidDisplayNotifications)
            {
                AndroidAudioHost.Displays.FailedError += AndroidDisplayFailedError;
                _androidDisplayNotifications = true;
            }
            var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(screenX, screenY)).WorkingArea;
            int x = Math.Max(area.Left, Math.Min(screenX - 210, area.Right - 440));
            int y = Math.Max(area.Top, Math.Min(screenY - 30, area.Bottom - 790));
            var display = await AndroidAudioHost.OpenDisplayAsync(payload.Device, payload.App, x, y);
            display.Activate();
            if (_exiting) return;
            if (!_displayAudioWatches.ContainsKey(display))
            {
                var watch = new VirtualDisplayPresentation(display, payload);
                _displayAudioWatches.Add(display, watch); _ = WatchDisplayAsync(display, watch);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("Android independent window: " + ex);
            if (!_exiting) _trayIcon?.ShowBalloonTip(5000, payload.App.Name, AndroidErrorTranslation.Format(ex), System.Windows.Forms.ToolTipIcon.Info);
        }
    }
    private async Task WatchDisplayAsync(SonicRoute.AndroidAudio.IVirtualDisplayClient display, VirtualDisplayPresentation watch)
    {
        try { await watch.WatchAsync(); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Display audio cleanup: " + ex.Message); }
        finally { _displayAudioWatches.Remove(display); }
    }
    internal async Task ReturnAndroidToPhoneAsync(AndroidAppDrag payload)
    {
        // Mark matching presentations stopped before closing their clients so delayed
        // startup/active notifications cannot briefly recapture audio on the computer.
        var watches = _displayAudioWatches.Values.Where(watch => watch.Matches(payload)).ToArray();
        await Task.WhenAll(watches.Select(watch => watch.StopReceivingAsync()));
        if (AndroidAudioHost.AreDisplaysCreated)
            await AndroidAudioHost.Displays.CloseApplicationAsync(payload.Device, payload.App.Package);
        if (AndroidAudioHost.IsCreated)
        {
            await AndroidAudioHost.Sources.RemoveApplicationAsync(payload.Device, payload.App.Package);
        }
    }

    private void AndroidDisplayFailedError(SonicRoute.AndroidAudio.AndroidApp? app, Exception ex)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => AndroidDisplayFailedError(app, ex))); return; }
        string detail = AndroidErrorTranslation.Format(ex);
        string body = app != null ? string.Format(L10n.T("Phone.DisplayErrorFormat"), app.Name, detail) : detail;
        if (!_exiting) _trayIcon?.ShowBalloonTip(5000, L10n.T("Phone.DisplayFallbackTitle"), body, System.Windows.Forms.ToolTipIcon.Info);
    }
    internal void TogglePhoneAudioWindow(QuickPanelModernWindow panel)
    {
        var config = ConfigService.Load();
        config.QuickPanelShowPhoneAudio = !config.QuickPanelShowPhoneAudio;
        ConfigService.QueueSave(config);
        panel.UpdatePhoneAudioButton();
        if (config.QuickPanelShowPhoneAudio) ShowPhoneAudioWindow(panel);
        else _phoneAudioWindow?.Close();
    }
    internal async void ShowPhoneAudioWindow(QuickPanelModernWindow panel)
    {
        if (!ConfigService.Load().QuickPanelShowPhoneAudio || !panel.IsVisible || panel.Left < -5000 || panel.Top < -5000) return;
        if (_phoneAudioWindow != null || ReferenceEquals(_openingPhonePanel, panel)) return;
        _openingPhonePanel = panel;
        try
        {
            await AndroidAudioHost.PreparePhoneAsync();
            await panel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Phone preferences: " + ex.Message); return; }
        finally
        {
            if (ReferenceEquals(_openingPhonePanel, panel))
            {
                _openingPhonePanel = null;
                // The owner may have closed while phone preferences were loading.
                ScheduleUiReclaim();
            }
        }
        if (_exiting || _phoneAudioWindow != null || !ConfigService.Load().QuickPanelShowPhoneAudio
            || !panel.IsVisible || panel.Left < -5000 || panel.Top < -5000) return;
        var phone = _phoneAudioWindow = new PhoneAudioWindow { Owner = panel };
        bool ownerClosing = false, closed = false, queued = false;
        void PositionPhone()
        {
            if (closed || ownerClosing) return;
            double height = panel.ActualHeight > 0 ? panel.ActualHeight : panel.Height;
            double width = panel.RootBorder.ActualWidth > 0 ? panel.RootBorder.ActualWidth : (panel.ActualWidth > 0 ? panel.ActualWidth : panel.Width);
            var rect = new Rect(-phone.Width - 6, 0, phone.Width, Math.Max(1, height));
            if (OsdPlacement.TryRelativeWorkArea(panel, out var area))
                rect = PhoneCompanionLayout.Arrange(new Rect(0,0,width,height), area, phone.Width);
            if (phone.Height != rect.Height) phone.Height = rect.Height;
            if (phone.Left != panel.Left + rect.Left) phone.Left = panel.Left + rect.Left;
            if (phone.Top != panel.Top + rect.Top) phone.Top = panel.Top + rect.Top;
            panel.RefreshCompanionPlacement();
        }
        void QueuePosition()
        {
            if (closed || queued) return;
            queued = true;
            _ = panel.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { queued = false; PositionPhone(); }));
        }
        EventHandler location = (_, _) => QueuePosition();
        SizeChangedEventHandler size = (_, _) => QueuePosition();
        System.Windows.DpiChangedEventHandler dpi = (_, _) => QueuePosition();
        System.ComponentModel.CancelEventHandler closing = (_, _) => { ownerClosing = true; phone.Close(); };
        panel.LocationChanged += location; panel.SizeChanged += size; panel.DpiChanged += dpi; panel.Closing += closing;
        phone.Deactivated += (_, _) => panel.CloseAfterFocusLeaves();
        phone.Closed += (_, _) =>
        {
            closed = true;
            panel.LocationChanged -= location; panel.SizeChanged -= size; panel.DpiChanged -= dpi; panel.Closing -= closing;
            if (ReferenceEquals(_phoneAudioWindow, phone)) _phoneAudioWindow = null;
            if (!ownerClosing) panel.RefreshCompanionPlacement();
            if (!ownerClosing && !_exiting)
            {
                var config = ConfigService.Load();
                if (config.QuickPanelShowPhoneAudio) { config.QuickPanelShowPhoneAudio = false; ConfigService.QueueSave(config); }
                panel.UpdatePhoneAudioButton();
            }
            ScheduleUiReclaim();
        };
        PositionPhone(); phone.Show(); panel.RefreshCompanionPlacement();
    }
    private Task BeginAudioShutdown()
    {
        if (_audioShutdown != null) return _audioShutdown;
        if (AndroidAudioHost.IsCreated) AndroidAudioHost.Sources.BeginShutdown();
        DetachSharedUsbHook();
        Task clipboard = Task.CompletedTask;
        if (_phoneClipboard != null)
        {
            _phoneClipboard.Changed -= OnPhoneClipboardChanged;
            // Detach the HWND hook on its owner; the remainder does not need the dispatcher.
            clipboard = _phoneClipboard.DisposeAsync().AsTask();
        }
        if (_androidDisplayNotifications) AndroidAudioHost.Displays.FailedError -= AndroidDisplayFailedError;
        return _audioShutdown = Task.Run(async () =>
        {
            await AppPlaybackRestore.StopAsync().ConfigureAwait(false);
            var tasks = new List<Task> { clipboard, AndroidAudioHost.StopMediaAsync(), LocalAppOutputMirrors.ShutdownAsync() };
            if (AndroidAudioHost.AreDisplaysCreated) tasks.Add(AndroidAudioHost.Displays.DisposeAsync().AsTask());
            if (AndroidAudioHost.IsCreated) tasks.Add(AndroidAudioHost.Sources.DisposeAsync().AsTask());
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            finally { AndroidAudioHost.FlushSettings(); }
        });
    }
#else
    private void StartPhoneClipboard() { }
    internal bool HasActivePhoneClipboard => false;
    internal bool IsPhoneWindowOpen => false;
    internal bool KeepQuickPanelForAndroid => false;
    internal bool IsPhoneAudioVisible => false;
    private Task BeginAudioShutdown() => _audioShutdown ??= StopLocalAudioAsync();
    private static async Task StopLocalAudioAsync()
    {
        await AppPlaybackRestore.StopAsync().ConfigureAwait(false);
        await LocalAppOutputMirrors.ShutdownAsync().ConfigureAwait(false);
    }
#endif
    private Task? _audioShutdown;
    private bool _audioShutdownWaited;
    private async Task WaitForAudioShutdownAsync()
    {
        _audioShutdownWaited = true;
        try
        {
            var shutdown = BeginAudioShutdown();
            if (!shutdown.IsCompleted)
            {
                using var timeout = new CancellationTokenSource();
                if (await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(8), timeout.Token)) != shutdown)
                {
                    System.Diagnostics.Trace.WriteLine("Audio shutdown deadline reached.");
                    return;
                }
                timeout.Cancel();
            }
            await shutdown;
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Audio shutdown: " + ex.Message); }
    }
    private void FinishAudioShutdown()
    {
        // Normal Quit awaited cleanup while WPF could still process messages. Direct
        // OS/session shutdown has a bounded fallback and must not wait indefinitely.
        if (_audioShutdownWaited) return;
        try
        {
            if (!BeginAudioShutdown().Wait(TimeSpan.FromSeconds(8)))
                System.Diagnostics.Trace.WriteLine("Audio shutdown deadline reached; cleanup still pending.");
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Audio shutdown: " + ex.Message); }
    }
}
#if SONICROUTE_PHONE
internal static class PhoneCompanionLayout
{
    internal static Rect Arrange(Rect owner, Rect work, double desiredWidth)
    {
        const double gap = 6;
        double width = Math.Min(desiredWidth, work.Width), height = Math.Min(owner.Height, work.Height);
        double left = owner.Left - gap - width, right = owner.Right + gap;
        double x = left >= work.Left ? left : right + width <= work.Right ? right
            : owner.Left - work.Left >= work.Right - owner.Right ? left : right;
        return new Rect(Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width)),
            Math.Clamp(owner.Top, work.Top, Math.Max(work.Top, work.Bottom - height)), width, height);
    }
}
#endif

using System;
using System.Windows;
#if SONICROUTE_PHONE
using SonicRoute.Core;
using SonicRoute.Core.Compat;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
#endif

namespace SonicRoute;

public partial class App
{
#if SONICROUTE_PHONE
    private PhoneClipboardSync? _phoneClipboard;
    private Task<PhoneClipboardSync>? _phoneClipboardLoad;
    internal event Action? PhoneClipboardChanged;
    private void StartPhoneClipboard()
    {
        if (System.IO.File.Exists(PhoneClipboardSync.SettingsPath)) _ = PreparePhoneClipboardAsync();
    }
    internal Task<PhoneClipboardSync> PreparePhoneClipboardAsync() => _phoneClipboardLoad ??= LoadPhoneClipboardAsync();
    private async Task<PhoneClipboardSync> LoadPhoneClipboardAsync()
    {
        var sync = _phoneClipboard = new PhoneClipboardSync(PhoneClipboardSync.SettingsPath, Dispatcher,
            new DesktopClipboardLink(_activateSink), () => new SonicRoute.AndroidAudio.AndroidClipboardClient(AndroidAudioHost.Adb));
        sync.Changed += OnPhoneClipboardChanged;
        try { await sync.InitializeAsync(); }
        catch (OperationCanceledException) { }
        return sync;
    }
    private void OnPhoneClipboardChanged() => PhoneClipboardChanged?.Invoke();
    internal bool PhoneClipboardEnabled(SonicRoute.AndroidAudio.AndroidDevice? device) => _phoneClipboard?.IsEnabled(device) == true;
    internal string PhoneClipboardStatus(SonicRoute.AndroidAudio.AndroidDevice? device) => _phoneClipboard?.Status(device) ?? "自动同步手机与电脑复制的文字";
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
    internal bool KeepQuickPanelForAndroid => AndroidAudioHost.IsDragging || AppIconDragController.IsDesktopDragging || _phoneAudioWindow?.IsActive == true
        || _phoneAudioWindow?.IsKeyboardFocusWithin == true;
    internal bool IsPhoneAudioVisible => _phoneAudioWindow?.IsVisible == true;
    internal Rect? PhoneAudioRelativeBounds(QuickPanelModernWindow panel) => _phoneAudioWindow is { IsVisible: true } phone && phone.Owner == panel
        ? new Rect(phone.Left - panel.Left, phone.Top - panel.Top, phone.Width, phone.Height) : null;

    internal async Task OpenAndroidDisplayAsync(AndroidAppDrag payload, int screenX, int screenY)
    {
        if (_exiting) return;
        try
        {
            if (!_androidDisplayNotifications)
            {
                AndroidAudioHost.Displays.Failed += AndroidDisplayFailed;
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
            if (!_exiting) _trayIcon?.ShowBalloonTip(5000, payload.App.Name, ex.Message, System.Windows.Forms.ToolTipIcon.Info);
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
            var sources = AndroidAudioHost.Sources.Sources.Where(source =>
                SonicRoute.AndroidAudio.AndroidDeviceIdentity.Matches(source.Device, payload.Device) && source.App.Package == payload.App.Package).ToArray();
            await Task.WhenAll(sources.Select(source => AndroidAudioHost.Sources.RemoveAsync(source.Id)));
        }
    }
    private void AndroidDisplayFailed(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => AndroidDisplayFailed(message))); return; }
        if (!_exiting) _trayIcon?.ShowBalloonTip(5000, "手机独立屏幕", message, System.Windows.Forms.ToolTipIcon.Info);
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
        finally { if (ReferenceEquals(_openingPhonePanel, panel)) _openingPhonePanel = null; }
        if (_exiting || _phoneAudioWindow != null || !ConfigService.Load().QuickPanelShowPhoneAudio
            || !panel.IsVisible || panel.Left < -5000 || panel.Top < -5000) return;
        var phone = _phoneAudioWindow = new PhoneAudioWindow { Owner = panel };
        bool ownerClosing = false, closed = false, queued = false;
        void PositionPhone()
        {
            if (closed || ownerClosing) return;
            double height = panel.ActualHeight > 0 ? panel.ActualHeight : panel.Height;
            double width = panel.ActualWidth > 0 ? panel.ActualWidth : panel.Width;
            var rect = new Rect(-phone.Width - 12, 0, phone.Width, Math.Max(1, height));
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
        };
        PositionPhone(); phone.Show(); panel.RefreshCompanionPlacement();
    }
    private void ShutdownAndroidAudio()
    {
        if (_phoneClipboard != null)
        {
            _phoneClipboard.Changed -= OnPhoneClipboardChanged;
            _phoneClipboard.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        AndroidAudioHost.StopMedia();
        if (_androidDisplayNotifications) AndroidAudioHost.Displays.Failed -= AndroidDisplayFailed;
        if (AndroidAudioHost.AreDisplaysCreated) AndroidAudioHost.Displays.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (AndroidAudioHost.IsCreated) AndroidAudioHost.Sources.DisposeAsync().AsTask().GetAwaiter().GetResult();
        AndroidAudioHost.FlushSettings();
    }
#else
    private void StartPhoneClipboard() { }
    internal bool KeepQuickPanelForAndroid => false;
    internal bool IsPhoneAudioVisible => false;
    private void ShutdownAndroidAudio() { }
#endif
}
#if SONICROUTE_PHONE
internal static class PhoneCompanionLayout
{
    internal static Rect Arrange(Rect owner, Rect work, double desiredWidth)
    {
        const double gap = 12;
        double width = Math.Min(desiredWidth, work.Width), height = Math.Min(owner.Height, work.Height);
        double left = owner.Left - gap - width, right = owner.Right + gap;
        double x = left >= work.Left ? left : right + width <= work.Right ? right
            : owner.Left - work.Left >= work.Right - owner.Right ? left : right;
        return new Rect(Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width)),
            Math.Clamp(owner.Top, work.Top, Math.Max(work.Top, work.Bottom - height)), width, height);
    }
}
#endif

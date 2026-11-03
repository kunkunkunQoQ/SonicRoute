#if SONICROUTE_PHONE
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
                        await AndroidAudioHost.Sources.ReleaseDisplayAudioAsync(released);
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
internal sealed class VirtualDisplayStatusWindow : Window
{
    private readonly IVirtualDisplayClient _client;
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { Margin = new Thickness(0, 10, 0, 18), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _restore = new() { Content = "切回电脑", Padding = new Thickness(18, 8, 18, 8), HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
    private readonly WinEventProc _location;
    private IntPtr _owner, _hook;
    private bool _closed, _queued;

    internal VirtualDisplayStatusWindow(IVirtualDisplayClient client)
    {
        _client = client; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Width = 300; Height = 200;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");
        SetResourceReference(BackgroundProperty, "Theme.SurfaceBg");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextPrimary");
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
        _restore.SetResourceReference(StyleProperty, "QuickPanelToolButton");
        _restore.SetResourceReference(ForegroundProperty, "Theme.TextPrimary");
        _restore.SetResourceReference(BackgroundProperty, "Theme.SurfaceBg");
        _restore.Click += (_, _) => { _restore.IsEnabled = false; _client.Restore(); };
        var body = new StackPanel { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(_title); body.Children.Add(_detail); body.Children.Add(_restore); Content = body;
        _location = (_, _, hwnd, objectId, _, _, _) => { if (hwnd == _owner && objectId == 0) QueuePosition(); };
        Closed += (_, _) =>
        {
            _closed = true; if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
            ((App)System.Windows.Application.Current).ReleaseClosedUiWindow(this);
        };
    }
    internal void UpdateState(VirtualDisplayState state)
    {
        _title.Text = state switch { VirtualDisplayState.OnPhone => "正在手机上使用", VirtualDisplayState.Recovering => "正在恢复画面", _ => "应用画面已暂停" };
        _detail.Text = state == VirtualDisplayState.OnPhone ? "手机退出后会尝试恢复到电脑" : state == VirtualDisplayState.Recovering ? "请稍候" : "可以重新打开到这个窗口";
        _restore.IsEnabled = state != VirtualDisplayState.Recovering;
        _restore.Content = state == VirtualDisplayState.OnPhone ? "切回电脑" : "恢复画面";
        if (!IsVisible && !_closed)
        {
            try
            {
                using var process = Process.GetProcessById(_client.ProcessId); _owner = process.MainWindowHandle;
                if (_owner == IntPtr.Zero) return;
                new WindowInteropHelper(this).Owner = _owner;
                _hook = SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, _location, (uint)_client.ProcessId, 0, 0);
                Show(); Position();
            }
            catch (ArgumentException) { Close(); }
            catch (InvalidOperationException) { Close(); }
        }
    }
    private void QueuePosition()
    {
        if (_closed || _queued) return; _queued = true;
        Dispatcher.BeginInvoke(new Action(() => { _queued = false; if (!_closed) Position(); }));
    }
    private void Position()
    {
        if (!GetClientRect(_owner, out var rect)) return;
        var point = new NativePoint(); if (!ClientToScreen(_owner, ref point)) return;
        int width = rect.Right, height = rect.Bottom;
        if (width <= 0 || height <= 0) return;
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, point.X, point.Y, width, height, 0x14);
    }
    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
#endif

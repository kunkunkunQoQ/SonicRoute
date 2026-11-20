#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using SonicRoute.WindowsLink;

namespace SonicRoute;

internal sealed class WindowsLinkClipboardSync : IDisposable
{
    private readonly WindowsLinkService _service;
    private readonly Dispatcher _dispatcher;
    private readonly IDesktopClipboardLink _desktop;
    private readonly Dictionary<LinkClipboardRoute, long> _baselines = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly object _incomingGate = new();
    private (LinkClipboardRoute Route, string Text, long Revision)? _incoming;
    private (LinkClipboardRoute[] Routes, long Revision)? _local;
    private bool _reading, _writing, _listening;
    private DispatcherTimer? _listenRetry;
    private int _listenFailures;
    private int _refreshQueued;
    private volatile bool _disposed;

    internal WindowsLinkClipboardSync(WindowsLinkService service, Dispatcher dispatcher, IDesktopClipboardLink desktop)
    {
        _service = service; _dispatcher = dispatcher; _desktop = desktop;
        _desktop.Changed += LocalChanged;
        _service.ClipboardStateChanged += StateChanged;
        _service.ClipboardTextReceived += Received;
    }

    private void StateChanged(string peer)
    {
        if (!_disposed && !_dispatcher.HasShutdownStarted && Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            _dispatcher.BeginInvoke((Action)Refresh);
    }

    private void Refresh()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        if (_disposed) return;
        var active = _service.ClipboardRoutes();
        foreach (var route in _baselines.Keys.Where(r => !active.Contains(r)).ToArray()) _baselines.Remove(route);
        if (_listening != (active.Length != 0))
        {
            try { _desktop.SetListening(active.Length != 0); _listening = active.Length != 0; }
            catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
            {
                Trace.WriteLine(ex.GetType().Name);
                if (active.Length != 0 && _listenRetry == null && _listenFailures < 3)
                {
                    int seconds = _listenFailures++ switch { 0 => 1, 1 => 3, _ => 8 };
                    _listenRetry = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(seconds) };
                    _listenRetry.Tick += RetryListening;
                    _listenRetry.Start();
                }
                return;
            }
        }
        StopListenerRetry();
        _listenFailures = 0;
        // Record only the sequence number on activation. Never read or send an earlier copy.
        foreach (var route in active)
            if (!_baselines.ContainsKey(route)) _baselines.Add(route, _desktop.Revision);
    }

    private void RetryListening(object? sender, EventArgs e)
    {
        StopListenerRetry();
        Refresh();
    }

    private void StopListenerRetry()
    {
        if (_listenRetry == null) return;
        _listenRetry.Stop();
        _listenRetry.Tick -= RetryListening;
        _listenRetry = null;
    }

    private void LocalChanged()
    {
        if (!_listening || _disposed) return;
        long revision = _desktop.Revision;
        var routes = _baselines.Where(e => e.Value != revision).Select(e => e.Key).ToArray();
        if (routes.Length == 0) return;
        foreach (var route in routes) _baselines[route] = revision;
        _local = (routes, revision);
        if (!_reading) _ = SendUpdatesAsync();
    }

    private async Task SendUpdatesAsync()
    {
        _reading = true;
        try
        {
            while (!_disposed && _local is { } next)
            {
                _local = null;
                if (!next.Routes.Any(_service.CanSyncClipboard) || _desktop.Revision != next.Revision) continue;
                string? text = await _desktop.ReadAsync(_stop.Token);
                if (string.IsNullOrEmpty(text) || _desktop.Revision != next.Revision) continue;
                foreach (var route in next.Routes)
                {
                    if (_disposed || _desktop.Revision != next.Revision) break;
                    try { await _service.SendClipboardTextAsync(route, text); }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ExternalException or ObjectDisposedException) { }
        finally { _reading = false; }
    }

    private void Received(LinkClipboardRoute route, string text)
    {
        lock (_incomingGate)
        {
            if (_disposed || _dispatcher.HasShutdownStarted) return;
            _incoming = (route, text, _desktop.Revision);
            if (_writing) return;
            _writing = true;
        }
        _dispatcher.BeginInvoke((Action)(() => _ = WriteUpdatesAsync()));
    }

    private async Task WriteUpdatesAsync()
    {
        while (true)
        {
            (LinkClipboardRoute Route, string Text, long Revision) next;
            lock (_incomingGate)
            {
                if (_disposed || _incoming == null) { _writing = false; return; }
                next = _incoming.Value;
                _incoming = null;
            }
            if (!_service.CanSyncClipboard(next.Route)) continue;
            if (!_listening) Refresh();
            if (!_listening) continue;
            try
            {
                var result = await _desktop.WriteIfCurrentAsync(next.Text, next.Revision,
                    () => !_disposed && _service.CanSyncClipboard(next.Route), _stop.Token);
                lock (_incomingGate)
                    if (result.Written && _incoming is { } newer && newer.Revision == next.Revision)
                        _incoming = (newer.Route, newer.Text, result.Revision);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ExternalException or ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.ClipboardStateChanged -= StateChanged;
        _service.ClipboardTextReceived -= Received;
        _stop.Cancel();
        lock (_incomingGate) _incoming = null;
        void Detach()
        {
            StopListenerRetry();
            _desktop.Changed -= LocalChanged;
            _baselines.Clear();
            _local = null;
            _desktop.Dispose();
        }
        if (_dispatcher.CheckAccess()) Detach();
        else if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke((Action)Detach);
        _stop.Dispose();
    }
}
#endif

#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute;

public interface IDesktopClipboardLink : IDisposable
{
    event Action? Changed;
    long Revision { get; }
    void SetListening(bool enabled);
    Task<string?> ReadAsync(CancellationToken token);
    Task<(bool Written, long Revision)> WriteAsync(string text, long expectedRevision, CancellationToken token);
}

/// <summary>Opt-in per-device text sharing. Views do not own active connections or clipboard history.</summary>
public sealed class PhoneClipboardSync(string filePath, Dispatcher dispatcher, IDesktopClipboardLink desktop,
    Func<IAndroidClipboardClient> createClient) : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<AndroidDevice>, bool>? _shouldScanOnStartup;
    internal PhoneClipboardSync(string filePath, Dispatcher dispatcher, IDesktopClipboardLink desktop,
        Func<IAndroidClipboardClient> createClient, Func<IReadOnlyList<AndroidDevice>, bool>? shouldScanOnStartup)
        : this(filePath, dispatcher, desktop, createClient)
    {
        _shouldScanOnStartup = shouldScanOnStartup;
    }
    internal static string SettingsPath => Path.Combine(Path.GetDirectoryName(ConfigService.ConfigPath)!, "phone-clipboard.json");
    private sealed record Saved(int Version, AndroidDevice[] EnabledDevices);
    private sealed class Session(AndroidDevice device, IAndroidClipboardClient client, CancellationToken lifetime)
    {
        internal readonly AndroidDevice Device = device;
        internal readonly IAndroidClipboardClient Client = client;
        internal readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal readonly Channel<(long Version, string Text, long RemoteRevision)> Writes = Channel.CreateBounded<(long, string, long)>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
        internal Task Completion = Task.CompletedTask;
        internal bool Ready, Finished;
        internal SessionState State = SessionState.Connecting;
    }
    private enum SessionState { Connecting, Connected, Disconnected, DesktopUnavailable }
    private static string SessionStatusText(SessionState state) => state switch
    {
        SessionState.Connected => L10n.T("Phone.ClipboardConnected"),
        SessionState.Disconnected => L10n.T("Phone.ClipboardDisconnected"),
        SessionState.DesktopUnavailable => L10n.T("Phone.ClipboardDesktopUnavailable"),
        _ => L10n.T("Phone.ClipboardConnecting")
    };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<AndroidDevice> _enabled = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly List<Task> _pending = new();
    private readonly object _saveGate = new();
    private Saved? _pendingSave;
    private Task _save = Task.CompletedTask;
    private bool _saving, _watching, _disposed, _desktopListening, _readPending, _reading, _writing, _saveError;
    private int _activeSessions;
    private long _version;
    private string? _lastText;
    private (string Text, long Version, long Revision)? _desktopWrite;
    private CancellationTokenSource? _clipboardWork;
    public event Action? Changed;
    internal bool HasActiveWork => !_disposed && (_desktopListening || Volatile.Read(ref _activeSessions) > 0);
    internal bool HasEnabledUsbTargets => !_disposed && _enabled.Any(d => !d.IsWifi);
    internal bool ShouldScanOnUsbArrival()
    {
        if (_disposed || !HasEnabledUsbTargets) return false;
        var devices = PhoneAudioWindowCache.Devices;
        if (devices == null) return true;
        foreach (var saved in _enabled)
        {
            if (saved.IsWifi) continue;
            var live = devices.FirstOrDefault(d => d.State == "device" && AndroidDeviceIdentity.Matches(d, saved));
            if (live == null) return true;
            string key = AndroidDeviceIdentity.Key(live);
            if (!_sessions.TryGetValue(key, out var session) || session.Finished || session.Stop.IsCancellationRequested)
                return true;
        }
        return false;
    }
    public bool IsEnabled(AndroidDevice? device) => device != null && _enabled.Any(d => AndroidDeviceIdentity.Matches(d, device));
    public string Status(AndroidDevice? device) => _saveError ? L10n.T("Phone.ClipboardSaveError") : device == null || !IsEnabled(device) ? L10n.T("Phone.ClipboardDefaultHint")
        : _sessions.TryGetValue(AndroidDeviceIdentity.Key(device), out var session) ? SessionStatusText(session.State) : L10n.T("Phone.ClipboardWaitingPhone");

    public async Task InitializeAsync()
    {
        var saved = await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(filePath) || new FileInfo(filePath).Length > 64 * 1024) return null;
                var value = JsonSerializer.Deserialize<Saved>(File.ReadAllText(filePath));
                if (value?.Version != 1 || value.EnabledDevices == null || value.EnabledDevices.Length > 32) return null;
                return value.EnabledDevices.Where(d => d != null && !string.IsNullOrWhiteSpace(d.Serial) && d.Serial.Length <= 512
                    && (d.HardwareId?.Length ?? 0) <= 128).DistinctBy(AndroidDeviceIdentity.Key).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        });
        if (_disposed) return;
        if (saved != null) _enabled.AddRange(saved);
        Reconcile(false);
        if (_enabled.Count > 0 && PhoneAudioWindowCache.Devices == null
            && (_shouldScanOnStartup == null
                || await Task.Run(() => _shouldScanOnStartup(_enabled.ToArray()), _lifetime.Token))
            && !_disposed && PhoneAudioWindowCache.Devices == null)
            try { await PhoneAudioWindowCache.ReadDevicesAsync(_lifetime.Token); }
            catch (Exception) when (!_disposed) { Changed?.Invoke(); }
    }
    public void SetEnabled(AndroidDevice device, bool enabled)
    {
        dispatcher.VerifyAccess();
        if (_disposed || enabled == IsEnabled(device)) return;
        if (enabled)
        {
            if (_enabled.Count >= 32) throw AndroidError.WithCode(new InvalidOperationException("最多同步32台手机。"), AndroidErrorCode.MaxClipboardPhones);
            _enabled.Add(device);
        }
        else _enabled.RemoveAll(d => AndroidDeviceIdentity.Matches(d, device));
        QueueSave(); Reconcile(true);
    }
    internal void ProfilesChanged()
    {
        if (!dispatcher.CheckAccess()) { Post(ProfilesChanged); return; }
        if (_disposed || !AndroidAudioHost.IsPhoneCreated || _enabled.Count == 0) return;
        bool changed = false;
        for (int i = _enabled.Count - 1; i >= 0; --i)
        {
            var saved = _enabled[i];
            if (AndroidAudioHost.Phone.IsRemoved(saved)) { _enabled.RemoveAt(i); changed = true; continue; }
            if (AndroidAudioHost.Phone.FindProfile(saved)?.LastDevice is { } current && !SameTransport(saved, current))
            { _enabled[i] = current; changed = true; }
        }
        if (changed) { QueueSave(); Reconcile(false); }
    }
    private static bool SameTransport(AndroidDevice left, AndroidDevice right) => left.Serial == right.Serial
        && AndroidDeviceIdentity.Key(left) == AndroidDeviceIdentity.Key(right);
    private void DevicesChanged() => Post(() => Reconcile(true));
    private void Post(Action action)
    {
        if (!_disposed && !dispatcher.HasShutdownStarted) _ = dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }
    private void Reconcile(bool retry)
    {
        if (_disposed) return;
        bool watch = _enabled.Count > 0;
        if (watch != _watching)
        {
            if (watch) PhoneAudioWindowCache.DevicesChanged += DevicesChanged;
            else PhoneAudioWindowCache.DevicesChanged -= DevicesChanged;
            _watching = watch;
        }
        var devices = PhoneAudioWindowCache.Devices ?? Array.Empty<AndroidDevice>();
        var targets = _enabled.Select(saved => devices.FirstOrDefault(d => d.State == "device" && AndroidDeviceIdentity.Matches(d, saved)))
            .Where(d => d != null).Cast<AndroidDevice>().DistinctBy(AndroidDeviceIdentity.Key).ToArray();
        foreach (var pair in _sessions.ToArray())
            if (!targets.Any(d => SameTransport(d, pair.Value.Device)))
            { _sessions.Remove(pair.Key); Cancel(pair.Value); }
        _pending.RemoveAll(task => task.IsCompleted);
        foreach (var device in targets)
        {
            string key = AndroidDeviceIdentity.Key(device);
            Task previous = Task.CompletedTask;
            if (_sessions.TryGetValue(key, out var existing))
            {
                if (!existing.Finished || !retry) continue;
                Cancel(existing); previous = existing.Completion;
            }
            var session = new Session(device, createClient(), _lifetime.Token);
            _sessions[key] = session;
            Interlocked.Increment(ref _activeSessions);
            session.Completion = Task.Run(async () =>
            {
                try
                {
                    try { await previous.ConfigureAwait(false); } catch { }
                    await RunAsync(key, session).ConfigureAwait(false);
                }
                finally { Interlocked.Decrement(ref _activeSessions); }
            });
            _pending.Add(session.Completion);
        }
        UpdateDesktopListener(); Changed?.Invoke();
    }
    private bool Current(string key, Session session) => !_disposed && _sessions.TryGetValue(key, out var current) && ReferenceEquals(current, session);
    private static void Cancel(Session session) { try { session.Stop.Cancel(); } catch (ObjectDisposedException) { } }
    private async Task RunAsync(string key, Session session)
    {
        Task watch = Task.CompletedTask, send = Task.CompletedTask;
        SessionState endState = SessionState.Disconnected;
        try
        {
            session.Stop.Token.ThrowIfCancellationRequested();
            watch = session.Client.WatchAsync(session.Device, () => Post(() =>
            {
                if (!Current(key, session) || session.Stop.IsCancellationRequested) return;
                session.Ready = true; session.State = SessionState.Connected;
                UpdateDesktopListener(); Changed?.Invoke();
            }), text => Post(() => { if (Current(key, session) && session.Ready && !session.Stop.IsCancellationRequested) AcceptText(text, session); }), session.Stop.Token);
            send = SendAsync(session);
            await await Task.WhenAny(watch, send).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Never log clipboard contents, protocol lines or payload-bearing exceptions. */ }
        finally
        {
            session.Stop.Cancel(); session.Writes.Writer.TryComplete();
            try { await Task.WhenAll(watch, send).ConfigureAwait(false); } catch { }
            Post(() =>
            {
                if (!Current(key, session)) return;
                session.Ready = false; session.Finished = true; if (session.State != SessionState.DesktopUnavailable) session.State = endState;
                UpdateDesktopListener(); Changed?.Invoke();
            });
            session.Stop.Dispose();
        }
    }
    private async Task SendAsync(Session session)
    {
        await foreach (var pending in session.Writes.Reader.ReadAllAsync(session.Stop.Token).ConfigureAwait(false))
            if (pending.Version == Interlocked.Read(ref _version))
                await session.Client.SendAsync(pending.Text, pending.RemoteRevision, session.Stop.Token).ConfigureAwait(false);
    }
    private void UpdateDesktopListener()
    {
        bool listening = !_disposed && _sessions.Values.Any(s => s.Ready && !s.Stop.IsCancellationRequested);
        if (_desktopListening == listening) return;
        if (listening) desktop.Changed += DesktopChanged;
        else desktop.Changed -= DesktopChanged;
        if (listening) _clipboardWork = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        else { _clipboardWork?.Cancel(); _clipboardWork?.Dispose(); _clipboardWork = null; }
        try { desktop.SetListening(listening); _desktopListening = listening; }
        catch (Exception)
        {
            desktop.Changed -= DesktopChanged;
            _clipboardWork?.Cancel(); _clipboardWork?.Dispose(); _clipboardWork = null;
            foreach (var session in _sessions.Values) { session.State = SessionState.DesktopUnavailable; Cancel(session); }
        }
        if (!listening) { _lastText = null; Interlocked.Increment(ref _version); _readPending = false; _desktopWrite = null; }
    }
    private void DesktopChanged()
    {
        if (!_desktopListening || _disposed) return;
        _readPending = true;
        if (!_reading && !_writing) _ = ReadDesktopAsync();
    }
    private async Task ReadDesktopAsync()
    {
        _reading = true;
        try
        {
            while (_readPending && !_disposed && _desktopListening)
            {
                _readPending = false; long revision = desktop.Revision, version = _version;
                var token = _clipboardWork?.Token ?? new CancellationToken(true);
                string? text = await desktop.ReadAsync(token);
                if (!_disposed && revision == desktop.Revision && version == _version && !_writing && text != null)
                    AcceptText(text, null);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Busy/delayed clipboard owners must not crash the dispatcher. */ }
        finally
        {
            _reading = false;
            if (_readPending && !_writing && !_disposed && _desktopListening) _ = ReadDesktopAsync();
        }
    }
    private void AcceptText(string text, Session? origin)
    {
        if (_disposed || !AndroidClipboardClient.IsSupported(text) || text == _lastText) return;
        _lastText = text; long version = Interlocked.Increment(ref _version);
        foreach (var session in _sessions.Values)
            if (session.Ready && !session.Stop.IsCancellationRequested && !ReferenceEquals(session, origin))
                session.Writes.Writer.TryWrite((version, text, session.Client.Revision));
        if (origin != null)
        {
            _desktopWrite = (text, version, desktop.Revision);
            if (!_writing) _ = WriteDesktopAsync();
        }
    }
    private async Task WriteDesktopAsync()
    {
        _writing = true;
        try
        {
            while (!_disposed && _desktopWrite is { } pending)
            {
                _desktopWrite = null;
                if (pending.Version != _version) continue;
                var token = _clipboardWork?.Token ?? new CancellationToken(true);
                var result = await desktop.WriteAsync(pending.Text, pending.Revision, token);
                // A newer phone event may have arrived while our own write held the clipboard.
                // Advance only our own revision, never a concurrent computer copy's revision.
                if (result.Written && _desktopWrite is { } next && next.Revision == pending.Revision)
                    _desktopWrite = next with { Revision = result.Revision };
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            _writing = false;
            if (_readPending && !_reading && !_disposed && _desktopListening) _ = ReadDesktopAsync();
        }
    }
    private void QueueSave()
    {
        lock (_saveGate)
        {
            _pendingSave = new(1, _enabled.Select(d => d with { Name = "", State = "offline", TransportId = null }).ToArray());
            if (_saving) return;
            _saving = true;
            _save = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(100).ConfigureAwait(false); // Coalesce actual setting edits, not a background timer.
                    while (true)
                    {
                        Saved snapshot; lock (_saveGate) snapshot = _pendingSave!;
                        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                        LanguageFiles.WriteAtomic(filePath, JsonSerializer.Serialize(snapshot));
                        lock (_saveGate) if (ReferenceEquals(snapshot, _pendingSave))
                        {
                            _saving = false;
                            Post(() => { if (_saveError) { _saveError = false; Changed?.Invoke(); } });
                            return;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { lock (_saveGate) _saving = false; Post(() => { _saveError = true; Changed?.Invoke(); }); }
            });
        }
    }
    public Task FlushAsync() { lock (_saveGate) return _save; }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_watching) PhoneAudioWindowCache.DevicesChanged -= DevicesChanged;
        desktop.Changed -= DesktopChanged; desktop.Dispose();
        _clipboardWork?.Cancel(); _clipboardWork?.Dispose(); _clipboardWork = null;
        _lifetime.Cancel();
        _lastText = null; _desktopWrite = null;
        foreach (var session in _sessions.Values) session.Writes.Writer.TryComplete();
        try { await Task.WhenAll(_pending.Append(FlushAsync())).ConfigureAwait(false); } catch { }
        _sessions.Clear(); _enabled.Clear(); _pending.Clear(); _lifetime.Dispose();
    }
}

/// <summary>Shares the application's existing hidden HWND. Clipboard work runs off the dispatcher.</summary>
internal sealed class DesktopClipboardLink(HwndSource? source) : IDesktopClipboardLink
{
    public event Action? Changed;
    private bool _listening;
    private IntPtr _handle;
    public long Revision => GetClipboardSequenceNumber();
    public void SetListening(bool enabled)
    {
        if (enabled == _listening) return;
        if (source == null) throw new InvalidOperationException("Clipboard window unavailable.");
        if (enabled)
        {
            _handle = source.Handle;
            source.AddHook(ClipboardMessage);
            if (!AddClipboardFormatListener(source.Handle)) { source.RemoveHook(ClipboardMessage); throw new ExternalException(); }
        }
        else { RemoveClipboardFormatListener(source.Handle); source.RemoveHook(ClipboardMessage); }
        _listening = enabled;
    }
    private IntPtr ClipboardMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    { if (message == 0x031D) Changed?.Invoke(); return IntPtr.Zero; }
    public async Task<string?> ReadAsync(CancellationToken token)
    {
        for (int attempt = 0; attempt < 4; ++attempt)
        {
            token.ThrowIfCancellationRequested();
            var read = await Task.Run<(bool Busy, string? Text)>(() =>
            {
                if (!OpenClipboard(IntPtr.Zero)) return (Busy: true, Text: (string?)null);
                try
                {
                    if (!IsClipboardFormatAvailable(13)) return (false, (string?)null);
                    IntPtr data = GetClipboardData(13);
                    long bytes = data == IntPtr.Zero ? 0 : (long)GlobalSize(data);
                    if (bytes is < 2 or > AndroidClipboardClient.MaxTextBytes * 2 + 2) return (false, (string?)null);
                    IntPtr pointer = GlobalLock(data); if (pointer == IntPtr.Zero) return (false, (string?)null);
                    try
                    {
                        string text = Marshal.PtrToStringUni(pointer, (int)(bytes / 2))!;
                        int end = text.IndexOf('\0'); if (end >= 0) text = text[..end];
                        return (false, AndroidClipboardClient.IsSupported(text) ? text : null);
                    }
                    finally { GlobalUnlock(data); }
                }
                finally { CloseClipboard(); }
            }, token);
            if (!read.Busy) return read.Text;
            await Task.Delay(25, token);
        }
        return null;
    }
    public async Task<(bool Written, long Revision)> WriteAsync(string text, long expectedRevision, CancellationToken token)
    {
        bool written = false; long revision = expectedRevision;
        bool completed = await ClipboardWriter.TryWriteTextAsync(text, value =>
        {
        token.ThrowIfCancellationRequested();
        if (!OpenClipboard(_handle)) throw new ExternalException();
        try
        {
            if (Revision != expectedRevision) return; // A newer computer copy wins while waiting for the clipboard lock.
            IntPtr data = GlobalAlloc(0x42, (UIntPtr)((value.Length + 1) * 2));
            if (data == IntPtr.Zero) throw new ExternalException();
            try
            {
                IntPtr pointer = GlobalLock(data); if (pointer == IntPtr.Zero) throw new ExternalException();
                try { Marshal.Copy((value + '\0').ToCharArray(), 0, pointer, value.Length + 1); }
                finally { GlobalUnlock(data); }
                token.ThrowIfCancellationRequested();
                if (!EmptyClipboard() || SetClipboardData(13, data) == IntPtr.Zero) throw new ExternalException();
                data = IntPtr.Zero; // Ownership transferred to Windows.
                written = true; revision = Revision;
            }
            finally { if (data != IntPtr.Zero) GlobalFree(data); }
        }
        finally { CloseClipboard(); }
        });
        return (completed && written, revision);
    }
    public void Dispose() { if (_listening) SetListening(false); Changed = null; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr data);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr data);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr data);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr data);
}
#endif

#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
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
    Task<(bool Written, long Revision)> WriteIfCurrentAsync(string text, long expectedRevision, Func<bool> current, CancellationToken token) =>
        current() ? WriteAsync(text, expectedRevision, token) : Task.FromResult((false, expectedRevision));
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
    private sealed record Saved(int Version, AndroidDevice[] EnabledDevices, AndroidDevice[]? ArchivedDevices = null);
    private sealed class Session(AndroidDevice device, IAndroidClipboardClient client, CancellationToken lifetime)
    {
        internal readonly AndroidDevice Device = device;
        internal readonly IAndroidClipboardClient Client = client;
        internal readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime,
            PhoneAudioWindowCache.Manager.OperationToken(device));
        internal readonly Channel<(long Version, string Text, long RemoteRevision)> Writes = Channel.CreateBounded<(long, string, long)>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
        internal Task Completion = Task.CompletedTask;
        internal CancellationTokenSource? RetryDelay;
        internal int RetryCount;
        internal long RemoteRevision;
        internal bool Ready, Finished;
        internal SessionState State = SessionState.Connecting;
    }
    private enum SessionState { Connecting, Connected, Disconnected, DesktopUnavailable, PermissionDenied }
    private static string SessionStatusText(SessionState state) => state switch
    {
        SessionState.Connected => L10n.T("Phone.ClipboardConnected"),
        SessionState.Disconnected => L10n.T("Phone.ClipboardDisconnected"),
        SessionState.DesktopUnavailable => L10n.T("Phone.ClipboardDesktopUnavailable"),
        SessionState.PermissionDenied => L10n.T("Phone.Err.ClipboardPermissionError"),
        _ => L10n.T("Phone.ClipboardConnecting")
    };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<AndroidDevice> _enabled = new();
    private readonly List<AndroidDevice> _archived = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly List<Task> _pending = new();
    private readonly object _saveGate = new();
    private Saved? _pendingSave;
    private Task _save = Task.CompletedTask;
    private bool _saving, _watching, _disposed, _desktopListening, _readPending, _reading, _writing, _saveError;
    private int _activeSessions;
    private long _version;
    private string? _lastText;
    private (string Text, long Version, long Revision, Session Origin)? _desktopWrite;
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
            var live = PhoneAudioWindowCache.GetOnlineDevice(saved);
            if (live == null) return true;
            string key = AndroidDeviceIdentity.Key(live);
            if (!_sessions.TryGetValue(key, out var session) || session.Finished || session.Stop.IsCancellationRequested)
                return true;
        }
        return false;
    }
    public bool IsEnabled(AndroidDevice? device) => device is { IsApp: false } && _enabled.Any(d => AndroidDeviceIdentity.MatchesSavedDevice(d, device));
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
                static AndroidDevice[] ValidDevices(AndroidDevice[] devices) => devices.Where(d => d is { IsApp: false } && !string.IsNullOrWhiteSpace(d.Serial)
                    && d.Serial.Length <= 512 && (d.HardwareId?.Length ?? 0) <= 128).DistinctBy(AndroidDeviceIdentity.Key).ToArray();
                return value with { EnabledDevices = ValidDevices(value.EnabledDevices), ArchivedDevices = ValidDevices(value.ArchivedDevices ?? []) };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        });
        if (_disposed) return;
        if (saved != null && (saved.EnabledDevices.Length > 0 || saved.ArchivedDevices is { Length: > 0 }))
        {
            await AndroidAudioHost.PreparePhoneAsync(_lifetime.Token);
            if (_disposed) return;
            _enabled.AddRange(saved.EnabledDevices);
            _archived.AddRange((saved.ArchivedDevices ?? []).Where(d => AndroidDeviceIdentity.StableKey(d) != null));
            if (UpdateProfilePreferences()) QueueSave();
        }
        Reconcile(false);
        var enabledAtStartup = _enabled.ToArray();
        if (enabledAtStartup.Length > 0 && PhoneAudioWindowCache.Devices == null
            && (_shouldScanOnStartup == null
                || await Task.Run(() => _shouldScanOnStartup(enabledAtStartup), _lifetime.Token))
            && !_disposed && PhoneAudioWindowCache.Devices == null)
            try { await PhoneAudioWindowCache.ReadDevicesAsync(_lifetime.Token); }
            catch (Exception) when (!_disposed) { Changed?.Invoke(); }
    }
    public void SetEnabled(AndroidDevice device, bool enabled)
    {
        dispatcher.VerifyAccess();
        if (_disposed || device.IsApp || enabled == IsEnabled(device) || enabled && AndroidAudioHost.IsPhoneCreated && AndroidAudioHost.Phone.IsRemoved(device)) return;
        if (enabled)
        {
            if (_enabled.Count >= 32) throw AndroidError.WithCode(new InvalidOperationException("最多同步32台手机。"), AndroidErrorCode.MaxClipboardPhones);
            _enabled.Add(device);
        }
        else _enabled.RemoveAll(d => AndroidDeviceIdentity.MatchesSavedDevice(d, device));
        QueueSave(); Reconcile(true);
    }
    internal void ProfilesChanged()
    {
        if (!dispatcher.CheckAccess()) { Post(ProfilesChanged); return; }
        if (!_disposed)
        {
            if (UpdateProfilePreferences()) QueueSave();
            Reconcile(false);
        }
    }

    private bool UpdateProfilePreferences()
    {
        if (!AndroidAudioHost.IsPhoneCreated || (_enabled.Count == 0 && _archived.Count == 0)) return false;
        var phone = AndroidAudioHost.Phone;
        bool changed = false;
        for (int i = _enabled.Count - 1; i >= 0; --i)
        {
            var saved = _enabled[i];
            if (phone.IsRemoved(saved))
            {
                _enabled.RemoveAt(i);
                if (AndroidDeviceIdentity.StableKey(saved) != null
                    && !_archived.Any(d => AndroidDeviceIdentity.SamePhysicalDevice(saved, d))) _archived.Add(saved);
                changed = true;
                continue;
            }
            if (phone.FindProfile(saved)?.LastDevice is { } current && !SameTransport(saved, current))
            { _enabled[i] = current; changed = true; }
        }
        for (int i = _archived.Count - 1; i >= 0; --i)
        {
            var saved = _archived[i];
            if (phone.FindProfile(saved)?.LastDevice is not { } current || !AndroidDeviceIdentity.SamePhysicalDevice(saved, current)
                || phone.IsRemoved(current)) continue;
            if (!IsEnabled(current))
            {
                if (_enabled.Count >= 32) continue;
                _enabled.Add(current);
            }
            _archived.RemoveAt(i);
            changed = true;
        }
        return changed;
    }
    private static bool SameTransport(AndroidDevice left, AndroidDevice right) => AndroidDeviceIdentity.SameTransport(left, right);
    private void DevicesChanged() => Post(() => Reconcile(true));
    private void Post(Action action)
    {
        if (!_disposed && !dispatcher.HasShutdownStarted) _ = dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }
    private AndroidDevice? FindOnlineTransport(AndroidDevice saved)
    {
        _sessions.TryGetValue(AndroidDeviceIdentity.Key(saved), out var current);
        if (current is { Finished: false } && !current.Stop.IsCancellationRequested
            && (PhoneAudioWindowCache.Manager.IsCurrent(current.Device) || PhoneAudioWindowCache.Manager.AwaitingDiscovery(current.Device)))
            return current.Device;
        return PhoneAudioWindowCache.Manager.Get(saved, current?.Device).Device is { IsApp: false } live ? live : null;
    }
    private void Reconcile(bool retry, Session? recovering = null)
    {
        if (_disposed) return;
        bool watch = _enabled.Count > 0;
        if (watch != _watching)
        {
            if (watch) PhoneAudioWindowCache.DevicesChanged += DevicesChanged;
            else PhoneAudioWindowCache.DevicesChanged -= DevicesChanged;
            _watching = watch;
        }
        var targets = _enabled.Select(FindOnlineTransport)
            .Where(d => d != null).Cast<AndroidDevice>().DistinctBy(AndroidDeviceIdentity.Key).ToArray();
        foreach (var pair in _sessions.ToArray())
            if (!targets.Any(d => AndroidDeviceIdentity.Key(d) == pair.Key))
            { _sessions.Remove(pair.Key); Cancel(pair.Value); }
        _pending.RemoveAll(task => task.IsCompleted);
        foreach (var device in targets)
        {
            string key = AndroidDeviceIdentity.Key(device);
            Task previous = Task.CompletedTask;
            _sessions.TryGetValue(key, out var existing);
            if (recovering != null && existing != recovering) continue;
            if (existing != null)
            {
                if (SameTransport(device, existing.Device) && (!existing.Finished || !retry)) continue;
                Cancel(existing); previous = existing.Completion;
            }
            var session = new Session(device, createClient(), _lifetime.Token) { RetryCount = recovering?.RetryCount + 1 ?? 0 };
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
    private static void Cancel(Session session)
    {
        session.RetryDelay?.Cancel();
        try { session.Stop.Cancel(); } catch (ObjectDisposedException) { }
    }
    private async Task RetryAfterDisconnectAsync(string key, Session session)
    {
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        session.RetryDelay = delay;
        try
        {
            int seconds = session.RetryCount switch { 0 => 1, 1 => 3, _ => 8 };
            await Task.Delay(TimeSpan.FromSeconds(seconds), delay.Token);
            if (!delay.IsCancellationRequested && Current(key, session)) Reconcile(true, session);
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
        finally { session.RetryDelay = null; }
    }
    private async Task RunAsync(string key, Session session)
    {
        Task watch = Task.CompletedTask, send = Task.CompletedTask;
        long readyAt = 0;
        bool retryable = false;
        SessionState endState = SessionState.Disconnected;
        try
        {
            session.Stop.Token.ThrowIfCancellationRequested();
            watch = session.Client.WatchAsync(session.Device, () =>
            {
                Interlocked.Exchange(ref readyAt, Stopwatch.GetTimestamp());
                long revision = session.Client.Revision;
                Post(() =>
                {
                    if (!Current(key, session) || session.Stop.IsCancellationRequested) return;
                    session.RemoteRevision = revision;
                    session.Ready = true; session.State = SessionState.Connected;
                    UpdateDesktopListener(); Changed?.Invoke();
                });
            }, text =>
            {
                // Capture with the event, not later on the dispatcher. A local copy must
                // not borrow the revision of a phone event the coordinator has not seen.
                long revision = session.Client.Revision;
                Post(() =>
                {
                    if (!Current(key, session) || !session.Ready || session.Stop.IsCancellationRequested) return;
                    session.RemoteRevision = revision;
                    AcceptText(text, session);
                });
            }, session.Stop.Token);
            send = SendAsync(session);
            await await Task.WhenAny(watch, send).ConfigureAwait(false);
            retryable = true;
        }
        catch (OperationCanceledException) { retryable = !session.Stop.IsCancellationRequested; }
        catch (Exception ex)
        {
            // Classify locally. Exceptions and protocol lines can contain clipboard text.
            if (AndroidError.TryGetCode(ex, out var code) && code == AndroidErrorCode.ClipboardPermissionError)
                endState = SessionState.PermissionDenied;
            else retryable = ex is IOException or SocketException;
        }
        finally
        {
            bool recover = retryable && !session.Stop.IsCancellationRequested;
            session.Stop.Cancel(); session.Writes.Writer.TryComplete();
            try { await Task.WhenAll(watch, send).ConfigureAwait(false); } catch { }
            Post(() =>
            {
                if (!Current(key, session)) return;
                session.Ready = false; session.Finished = true;
                if (readyAt != 0 && Stopwatch.GetElapsedTime(readyAt) >= TimeSpan.FromMinutes(1)) session.RetryCount = 0;
                bool desktopUnavailable = session.State == SessionState.DesktopUnavailable;
                if (!desktopUnavailable) session.State = endState;
                if ((recover || desktopUnavailable) && session.RetryCount < 3)
                {
                    session.State = SessionState.Connecting;
                    _ = RetryAfterDisconnectAsync(key, session);
                }
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
                session.Writes.Writer.TryWrite((version, text, session.RemoteRevision));
        if (origin != null)
        {
            _desktopWrite = (text, version, desktop.Revision, origin);
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
                var token = _clipboardWork?.Token ?? new CancellationToken(true);
                var result = await desktop.WriteIfCurrentAsync(pending.Text, pending.Revision,
                    () => pending.Version == Interlocked.Read(ref _version) && !pending.Origin.Stop.IsCancellationRequested, token);
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
            static AndroidDevice[] Snapshot(List<AndroidDevice> devices) => devices.Select(d => d with { Name = "", State = "offline", TransportId = null }).ToArray();
            _pendingSave = new(1, Snapshot(_enabled), _archived.Count == 0 ? null : Snapshot(_archived));
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
        _clipboardWork?.Cancel(); _clipboardWork?.Dispose(); _clipboardWork = null;
        desktop.Changed -= DesktopChanged; desktop.Dispose();
        _lifetime.Cancel();
        _lastText = null; _desktopWrite = null;
        foreach (var session in _sessions.Values) session.Writes.Writer.TryComplete();
        try { await Task.WhenAll(_pending.Append(FlushAsync())).ConfigureAwait(false); } catch { }
        _sessions.Clear(); _enabled.Clear(); _archived.Clear(); _pending.Clear(); _lifetime.Dispose();
    }
}

/// <summary>Shares the application's existing hidden HWND. Clipboard work runs off the dispatcher.</summary>
internal sealed partial class DesktopClipboardLink(HwndSource? source, int maxTextBytes = AndroidClipboardClient.MaxTextBytes) : IDesktopClipboardLink
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
            Listeners.GetValue(source, static window => new ClipboardListeners(window)).Subscribe(OnChanged);
        }
        else if (Listeners.TryGetValue(source, out var listeners)) listeners.Unsubscribe(OnChanged);
        _listening = enabled;
    }
    private void OnChanged() => Changed?.Invoke();
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
                    if (!IsClipboardFormatAvailable(13) || ExcludeFromSharing()) return (false, (string?)null);
                    IntPtr data = GetClipboardData(13);
                    long bytes = data == IntPtr.Zero ? 0 : (long)GlobalSize(data);
                    if (bytes < 2 || bytes > maxTextBytes * 2L + 2) return (false, (string?)null);
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
    public Task<(bool Written, long Revision)> WriteAsync(string text, long expectedRevision, CancellationToken token) =>
        WriteIfCurrentAsync(text, expectedRevision, static () => true, token);

    public async Task<(bool Written, long Revision)> WriteIfCurrentAsync(string text, long expectedRevision, Func<bool> current, CancellationToken token)
    {
        bool written = false; long revision = expectedRevision;
        bool completed = await ClipboardWriter.TryWriteTextAsync(text, value =>
        {
        token.ThrowIfCancellationRequested();
        if (_handle == IntPtr.Zero || !current()) return;
        if (!OpenClipboard(_handle)) throw new ExternalException();
        try
        {
            if (Revision != expectedRevision) return; // A newer local copy wins.
            if (Formats.Remote == 0 || Formats.Cloud == 0) return;
            IntPtr data = GlobalAlloc(0x42, (UIntPtr)((value.Length + 1) * 2));
            if (data == IntPtr.Zero) throw new ExternalException();
            IntPtr remoteFlag = IntPtr.Zero, cloudFlag = IntPtr.Zero;
            try
            {
                IntPtr pointer = GlobalLock(data); if (pointer == IntPtr.Zero) throw new ExternalException();
                try { Marshal.Copy((value + '\0').ToCharArray(), 0, pointer, value.Length + 1); }
                finally { GlobalUnlock(data); }
                // Prepare both zero DWORDs before clearing the previous clipboard contents.
                remoteFlag = GlobalAlloc(0x42, 4);
                cloudFlag = GlobalAlloc(0x42, 4);
                if (remoteFlag == IntPtr.Zero || cloudFlag == IntPtr.Zero) throw new ExternalException();
                token.ThrowIfCancellationRequested();
                if (!current()) return;
                if (!EmptyClipboard()) throw new ExternalException();
                if (!TransferClipboardData(Formats.Remote, ref remoteFlag) || !TransferClipboardData(Formats.Cloud, ref cloudFlag)
                    || !TransferClipboardData(13, ref data)) return;
                written = true; revision = Revision;
            }
            finally
            {
                if (data != IntPtr.Zero) GlobalFree(data);
                if (remoteFlag != IntPtr.Zero) GlobalFree(remoteFlag);
                if (cloudFlag != IntPtr.Zero) GlobalFree(cloudFlag);
            }
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

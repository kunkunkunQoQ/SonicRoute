#if SONICROUTE_PHONE
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute.Native.Product;

/// <summary>Opt-in Unicode text clipboard bridge with per-device persistence and anti-loop markers.</summary>
internal sealed partial class PhoneClipboard : IDisposable
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;
    private static readonly uint RemoteFormat = RegisterClipboardFormatW("SonicRoute.RemoteClipboard.v1");
    private static readonly uint ExcludeFormat = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint IgnoreViewerFormat = RegisterClipboardFormatW("Clipboard Viewer Ignore");
    private static readonly uint CloudFormat = RegisterClipboardFormatW("CanUploadToCloudClipboard");
    private static readonly uint HistoryFormat = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    private static readonly uint PasswordFormat = RegisterClipboardFormatW("application/x-kde-passwordManagerHint");
    private static readonly HashSet<string> PasswordManagers = new(StringComparer.OrdinalIgnoreCase)
    {
        "keepass", "keepassxc", "1password", "bitwarden", "enpass", "nordpass", "proton pass", "protonpass",
        "keeperpasswordmanager", "dashlane", "lastpass", "identities", "robotaskbaricon"
    };

    private readonly AdbClient _adb;
    private readonly NativeApplicationWindow _window;
    private readonly IMainThread _owner;
    private readonly string? _settingsPath;
    private readonly Func<AndroidDevice, bool>? _isRemoved;
    private readonly Func<AndroidDevice, AndroidDevice?>? _resolveOnline;
    private readonly List<AndroidDevice> _enabledDevices = new();
    private readonly object _saveGate = new();
    private Task _saveTask = Task.CompletedTask;
    private bool _saving;
    private AndroidDevice[]? _pendingSave;

    private CancellationTokenSource? _lifetime;
    private Channel<string>? _outgoing;
    private AndroidDevice? _device;
    private IAndroidClipboardClient? _client;
    private IDisposable? _retry;
    private string _status = "Off";
    private string? _lastText;
    private uint _lastWriteSequence;
    private int _enabled, _ownsClipboardInterest, _disposed, _readRetry;
    private bool _initialized, _saveError;

    internal event Action? Changed;

    internal PhoneClipboard(
        AdbClient adb,
        NativeApplicationWindow window,
        IMainThread owner,
        string? settingsPath = null,
        Func<AndroidDevice, bool>? isRemoved = null,
        Func<AndroidDevice, AndroidDevice?>? resolveOnline = null)
    {
        _adb = adb;
        _window = window;
        _owner = owner;
        _settingsPath = settingsPath;
        _isRemoved = isRemoved;
        _resolveOnline = resolveOnline;
    }

    internal bool Enabled => Volatile.Read(ref _enabled) != 0 || _enabledDevices.Count > 0;
    internal string Status => _saveError ? L10n.T("Phone.ClipboardSaveError") : Volatile.Read(ref _status);
    internal bool HasActiveWork => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _enabled) != 0;

    internal bool IsEnabled(AndroidDevice? device)
    {
        if (device == null) return false;
        lock (_enabledDevices)
            return _enabledDevices.Any(d => AndroidDeviceIdentity.MatchesSavedDevice(d, device));
    }

    internal string StatusFor(AndroidDevice? device)
    {
        if (_saveError) return L10n.T("Phone.ClipboardSaveError");
        if (device == null || !IsEnabled(device)) return L10n.T("Phone.ClipboardDefaultHint");
        if (Volatile.Read(ref _enabled) != 0 && _device is { } active && AndroidDeviceIdentity.MatchesSavedDevice(active, device))
            return Volatile.Read(ref _status);
        return L10n.T("Phone.ClipboardWaitingPhone");
    }

    internal static bool HasConfiguredDevices(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > 64 * 1024) return false;
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("Version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int ver) || ver != 1)
                return false;
            if (!root.TryGetProperty("EnabledDevices", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("Serial", out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    internal async Task InitializeAsync()
    {
        if (_initialized || Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(_settingsPath)) return;
        _initialized = true;
        var loaded = await Task.Run(() => LoadEnabledDevices(_settingsPath)).ConfigureAwait(false);
        if (Volatile.Read(ref _disposed) != 0 || loaded.Length == 0) return;

        _owner.TryPost(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            lock (_enabledDevices)
            {
                foreach (var d in loaded)
                {
                    if (_isRemoved?.Invoke(d) == true) continue;
                    if (_enabledDevices.Count >= 32) break;
                    if (!_enabledDevices.Any(x => AndroidDeviceIdentity.MatchesSavedDevice(x, d)))
                        _enabledDevices.Add(d);
                }
            }
            Reconcile();
            NotifyChanged();
        });
    }

    internal void Reconcile()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        AndroidDevice? target = null;
        lock (_enabledDevices)
        {
            for (int i = _enabledDevices.Count - 1; i >= 0; i--)
            {
                if (_isRemoved?.Invoke(_enabledDevices[i]) == true)
                    _enabledDevices.RemoveAt(i);
            }
            foreach (var saved in _enabledDevices)
            {
                var online = _resolveOnline?.Invoke(saved);
                if (online != null && string.Equals(online.State, "device", StringComparison.Ordinal))
                {
                    target = online;
                    break;
                }
            }
        }
        if (target != null)
            StartBridge(target);
        else if (Volatile.Read(ref _enabled) != 0)
            StopBridge(L10n.T("Phone.ClipboardWaitingPhone"));
    }

    internal void Enable(AndroidDevice device) => SetEnabled(device, true);

    internal void SetEnabled(AndroidDevice device, bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(device);
        if (enabled && _isRemoved?.Invoke(device) == true) return;

        bool listChanged = false;
        lock (_enabledDevices)
        {
            if (enabled)
            {
                if (!IsEnabled(device))
                {
                    if (_enabledDevices.Count >= 32)
                        throw AndroidError.WithCode(new InvalidOperationException("最多同步32台手机。"), AndroidErrorCode.MaxClipboardPhones);
                    _enabledDevices.Add(device);
                    listChanged = true;
                }
            }
            else
            {
                int removed = _enabledDevices.RemoveAll(d => AndroidDeviceIdentity.MatchesSavedDevice(d, device));
                listChanged = removed > 0;
            }
        }
        if (listChanged) QueueSave();

        if (enabled)
        {
            var target = _resolveOnline?.Invoke(device) ?? (string.Equals(device.State, "device", StringComparison.Ordinal) ? device : null);
            if (target != null) StartBridge(target);
            else SetStatus(L10n.T("Phone.ClipboardWaitingPhone"));
        }
        else if (_device is { } current && AndroidDeviceIdentity.MatchesSavedDevice(current, device))
        {
            StopBridge(L10n.T("Phone.ClipboardDefaultHint"));
            Reconcile();
        }
        else
        {
            NotifyChanged();
        }
    }

    internal void Disable()
    {
        bool listChanged;
        lock (_enabledDevices)
        {
            listChanged = _enabledDevices.Count > 0;
            _enabledDevices.Clear();
        }
        if (listChanged) QueueSave();
        StopBridge(L10n.T("Phone.ClipboardDefaultHint"));
    }

    internal void DisableForDevice(AndroidDevice device)
    {
        bool listChanged;
        lock (_enabledDevices)
            listChanged = _enabledDevices.RemoveAll(d => AndroidDeviceIdentity.MatchesSavedDevice(d, device)) > 0;
        if (listChanged) QueueSave();
        if (_device is { } current && AndroidDeviceIdentity.MatchesSavedDevice(current, device))
        {
            StopBridge(L10n.T("Phone.ClipboardDefaultHint"));
            Reconcile();
        }
    }

    private void StartBridge(AndroidDevice device)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Volatile.Read(ref _enabled) != 0 && _device is { } current && AndroidDeviceIdentity.SameTransport(current, device))
            return;
        StopBridge(null);

        _window.SetInterest(_window.Interest | ShellInterest.Clipboard);
        if (!_window.Interest.HasFlag(ShellInterest.Clipboard))
        {
            SetStatus(L10n.T("Phone.ClipboardDesktopUnavailable"));
            return;
        }
        _ownsClipboardInterest = 1;
        var lifetime = new CancellationTokenSource();
        var outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var client = new AndroidClipboardClient(_adb);
        _device = device;
        _client = client;
        _lifetime = lifetime;
        _outgoing = outgoing;
        _lastText = null;
        Volatile.Write(ref _enabled, 1);
        SetStatus(L10n.T("Phone.ClipboardConnecting"));
        _ = Task.Run(() => SendLoopAsync(client, outgoing.Reader, lifetime.Token));
        _ = Task.Run(() => WatchLoopAsync(device, client, outgoing.Writer, lifetime));
    }

    private void StopBridge(string? endStatus)
    {
        if (Interlocked.Exchange(ref _enabled, 0) == 0)
        {
            _retry?.Dispose(); _retry = null;
            if (endStatus != null) SetStatus(endStatus);
            return;
        }
        _retry?.Dispose(); _retry = null;
        _readRetry = 0;
        _outgoing?.Writer.TryComplete();
        try { _lifetime?.Cancel(); } catch (ObjectDisposedException) { }
        _lifetime = null;
        _outgoing = null;
        _client = null;
        _device = null;
        if (Interlocked.Exchange(ref _ownsClipboardInterest, 0) != 0)
            _window.SetInterest(_window.Interest & ~ShellInterest.Clipboard);
        if (endStatus != null) SetStatus(endStatus);
    }

    /// <summary>Called by the host after WM_CLIPBOARDUPDATE, on the host STA.</summary>
    internal void OnClipboardChanged()
    {
        if (Volatile.Read(ref _enabled) == 0) return;
        uint sequence = GetClipboardSequenceNumber();
        if (sequence != 0 && sequence == _lastWriteSequence) return;
        if (ExcludedFromSharing()) return;
        string? text = ReadUnicodeText();
        if (text == null)
        {
            if (IsClipboardFormatAvailable(CfUnicodeText) != 0 && _readRetry++ < 4)
                _retry = _owner.ScheduleOnce(TimeSpan.FromMilliseconds(60), OnClipboardChanged);
            return;
        }
        _readRetry = 0;
        if (!AndroidClipboardClient.IsSupported(text) || string.Equals(text, _lastText, StringComparison.Ordinal)) return;
        _lastText = text;
        _outgoing?.Writer.TryWrite(text);
    }

    private async Task WatchLoopAsync(AndroidDevice device, IAndroidClipboardClient client, ChannelWriter<string> outgoing,
        CancellationTokenSource lifetime)
    {
        CancellationToken token = lifetime.Token;
        int attempt = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    SetStatusFromWorker(L10n.T("Phone.ClipboardConnecting"), client);
                    await client.WatchAsync(device,
                        () =>
                        {
                            attempt = 0;
                            SetStatusFromWorker(L10n.T("Phone.ClipboardConnected"), client);
                        },
                        text =>
                        {
                            if (!token.IsCancellationRequested)
                                _owner.TryPost(() => ApplyPhoneText(text, client));
                        }, token).ConfigureAwait(false);
                    throw new IOException("Phone clipboard stream ended.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    if (AndroidError.TryGetCode(error, out var code) && code == AndroidErrorCode.ClipboardPermissionError)
                    {
                        SetStatusFromWorker(L10n.T("Phone.Err.ClipboardPermissionError"), client);
                        break;
                    }
                    if (attempt >= 3 || (error is not IOException and not SocketException))
                    {
                        SetStatusFromWorker(L10n.T("Phone.ClipboardDisconnected"), client);
                        break;
                    }
                    int seconds = attempt switch { 0 => 1, 1 => 3, _ => 8 };
                    attempt++;
                    SetStatusFromWorker(L10n.T("Phone.ClipboardConnecting"), client);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _lifetime, null, lifetime), lifetime))
            {
                _owner.TryPost(() =>
                {
                    if (ReferenceEquals(_client, client))
                    {
                        _client = null;
                        _device = null;
                        Volatile.Write(ref _enabled, 0);
                        if (Interlocked.Exchange(ref _ownsClipboardInterest, 0) != 0)
                            _window.SetInterest(_window.Interest & ~ShellInterest.Clipboard);
                        NotifyChanged();
                    }
                });
            }
            lifetime.Dispose();
        }
    }

    private async Task SendLoopAsync(IAndroidClipboardClient client, ChannelReader<string> reader, CancellationToken token)
    {
        try
        {
            await foreach (string text in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await client.SendAsync(text, client.Revision, token).ConfigureAwait(false);
                    _owner.TryPost(() =>
                    {
                        if (Volatile.Read(ref _enabled) != 0 && ReferenceEquals(_client, client))
                            SetStatus(L10n.T("Phone.ClipboardConnected"));
                    });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    _owner.TryPost(() =>
                    {
                        if (Volatile.Read(ref _enabled) != 0 && ReferenceEquals(_client, client))
                            SetStatus(L10n.T("Phone.ClipboardDisconnected"));
                    });
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ApplyPhoneText(string text, IAndroidClipboardClient client)
    {
        if (Volatile.Read(ref _enabled) == 0 || !ReferenceEquals(_client, client) || !AndroidClipboardClient.IsSupported(text)
            || string.Equals(text, _lastText, StringComparison.Ordinal)) return;
        uint sequence = GetClipboardSequenceNumber();
        string? current = ReadUnicodeText();
        if (string.Equals(current, text, StringComparison.Ordinal)) { _lastText = text; return; }
        if (!WriteUnicodeText(text, sequence)) { SetStatus(L10n.T("Phone.ClipboardDesktopUnavailable")); return; }
        _lastText = text;
        SetStatus(L10n.T("Phone.ClipboardConnected"));
    }

    private string? ReadUnicodeText()
    {
        if (OpenClipboard(_window.Handle) == 0) return null;
        try
        {
            if (IsClipboardFormatAvailable(CfUnicodeText) == 0) return null;
            nint memory = GetClipboardData(CfUnicodeText);
            nuint bytes = memory == 0 ? 0 : GlobalSize(memory);
            if (bytes < 2 || bytes > (nuint)(AndroidClipboardClient.MaxTextBytes * sizeof(char) + sizeof(char))) return null;
            nint pointer = GlobalLock(memory);
            if (pointer == 0) return null;
            try
            {
                int chars = (int)(bytes / 2);
                int length = 0;
                while (length < chars && Marshal.ReadInt16(pointer, length * 2) != 0) length++;
                string text = Marshal.PtrToStringUni(pointer, length) ?? "";
                return AndroidClipboardClient.IsSupported(text) ? text : null;
            }
            finally { GlobalUnlock(memory); }
        }
        finally { CloseClipboard(); }
    }

    private bool WriteUnicodeText(string text, uint? expectedSequence = null)
    {
        if (OpenClipboard(_window.Handle) == 0) return false;
        nint memory = 0;
        nint remoteFlag = 0;
        nint cloudFlag = 0;
        try
        {
            if (expectedSequence.HasValue && GetClipboardSequenceNumber() != expectedSequence.Value) return false;
            int bytes = checked((text.Length + 1) * sizeof(char));
            memory = GlobalAlloc(GmemMoveable | GmemZeroInit, (nuint)bytes);
            if (memory == 0) return false;
            nint pointer = GlobalLock(memory);
            if (pointer == 0) return false;
            try
            {
                char[] chars = text.ToCharArray();
                Marshal.Copy(chars, 0, pointer, chars.Length);
                Marshal.WriteInt16(pointer, chars.Length * sizeof(char), 0);
            }
            finally { GlobalUnlock(memory); }

            // Write anti-loop and cloud-exclusion markers matching WPF PhoneClipboardSync.
            if (RemoteFormat != 0) remoteFlag = GlobalAlloc(GmemMoveable | GmemZeroInit, 4);
            if (CloudFormat != 0) cloudFlag = GlobalAlloc(GmemMoveable | GmemZeroInit, 4);

            if (EmptyClipboard() == 0) return false;
            if (RemoteFormat != 0 && remoteFlag != 0)
            {
                if (SetClipboardData(RemoteFormat, remoteFlag) != 0) remoteFlag = 0;
            }
            if (CloudFormat != 0 && cloudFlag != 0)
            {
                if (SetClipboardData(CloudFormat, cloudFlag) != 0) cloudFlag = 0;
            }
            if (SetClipboardData(CfUnicodeText, memory) == 0) return false;
            memory = 0; // Windows owns the movable block after successful transfer.
            _lastWriteSequence = GetClipboardSequenceNumber();
            return true;
        }
        finally
        {
            if (memory != 0) GlobalFree(memory);
            if (remoteFlag != 0) GlobalFree(remoteFlag);
            if (cloudFlag != 0) GlobalFree(cloudFlag);
            CloseClipboard();
        }
    }

    private bool ExcludedFromSharing()
    {
        if (OpenClipboard(_window.Handle) == 0) return false;
        bool excluded;
        try
        {
            excluded = IsClipboardFormatAvailable(15) != 0 || IsClipboardFormatAvailable(RemoteFormat) != 0
                || IsClipboardFormatAvailable(ExcludeFormat) != 0 || IsClipboardFormatAvailable(IgnoreViewerFormat) != 0
                || IsClipboardFormatAvailable(PasswordFormat) != 0 || DeniesSharing(CloudFormat) || DeniesSharing(HistoryFormat);
        }
        finally { CloseClipboard(); }
        if (excluded) return true;
        nint clipboardOwner = GetClipboardOwner();
        if (clipboardOwner == 0 || GetWindowThreadProcessId(clipboardOwner, out uint pid) == 0 || pid == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return PasswordManagers.Contains(process.ProcessName);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return true; }
    }

    private static bool DeniesSharing(uint format)
    {
        if (format == 0 || IsClipboardFormatAvailable(format) == 0) return false;
        nint memory = GetClipboardData(format);
        if (memory == 0 || GlobalSize(memory) < 4) return true;
        nint pointer = GlobalLock(memory);
        if (pointer == 0) return true;
        try { return Marshal.ReadInt32(pointer) != 1; }
        finally { GlobalUnlock(memory); }
    }

    private void QueueSave()
    {
        if (string.IsNullOrEmpty(_settingsPath)) return;
        lock (_saveGate)
        {
            lock (_enabledDevices)
                _pendingSave = _enabledDevices.Select(d => d with { Name = "", State = "offline", TransportId = null }).ToArray();
            if (_saving) return;
            _saving = true;
            _saveTask = Task.Run(SaveLoopAsync);
        }
    }

    private async Task SaveLoopAsync()
    {
        try
        {
            await Task.Delay(100).ConfigureAwait(false);
            while (true)
            {
                AndroidDevice[] snapshot;
                lock (_saveGate) snapshot = _pendingSave!;
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath!)!);
                AtomicFile.Write(_settingsPath!, stream => WriteJson(stream, snapshot));
                lock (_saveGate)
                {
                    if (ReferenceEquals(snapshot, _pendingSave))
                    {
                        _saving = false;
                        _owner.TryPost(() =>
                        {
                            if (_saveError) { _saveError = false; NotifyChanged(); }
                        });
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_saveGate) _saving = false;
            _owner.TryPost(() => { _saveError = true; NotifyChanged(); });
        }
    }

    private static void WriteJson(Stream stream, AndroidDevice[] devices)
    {
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("Version", 1);
        writer.WritePropertyName("EnabledDevices");
        writer.WriteStartArray();
        foreach (var d in devices)
        {
            writer.WriteStartObject();
            writer.WriteString("Serial", d.Serial);
            writer.WriteString("Name", d.Name);
            writer.WriteString("State", d.State);
            writer.WriteBoolean("IsWifi", d.IsWifi);
            if (d.HardwareId != null) writer.WriteString("HardwareId", d.HardwareId);
            if (d.TransportId != null) writer.WriteString("TransportId", d.TransportId);
            if (d.IdentityFingerprint != null) writer.WriteString("IdentityFingerprint", d.IdentityFingerprint);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    private static AndroidDevice[] LoadEnabledDevices(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > 64 * 1024) return [];
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            if (!root.TryGetProperty("Version", out var ver) || ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out int version) || version != 1)
                return [];
            if (!root.TryGetProperty("EnabledDevices", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];
            var list = new List<AndroidDevice>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? serial = item.TryGetProperty("Serial", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
                if (string.IsNullOrWhiteSpace(serial) || serial.Length > 512) continue;
                string? hw = item.TryGetProperty("HardwareId", out var hp) && hp.ValueKind == JsonValueKind.String ? hp.GetString() : null;
                if (hw != null && hw.Length > 128) continue;
                bool isWifi = item.TryGetProperty("IsWifi", out var wp) && wp.ValueKind == JsonValueKind.True;
                string? fp = item.TryGetProperty("IdentityFingerprint", out var fpp) && fpp.ValueKind == JsonValueKind.String ? fpp.GetString() : null;
                list.Add(new AndroidDevice(serial, "", "offline", isWifi, hw) { IdentityFingerprint = fp });
                if (list.Count >= 32) break;
            }
            return list.DistinctBy(AndroidDeviceIdentity.Key).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    internal Task FlushAsync() { lock (_saveGate) return _saveTask; }

    private void SetStatusFromWorker(string status, IAndroidClipboardClient client) =>
        _owner.TryPost(() => { if (Volatile.Read(ref _enabled) != 0 && ReferenceEquals(_client, client)) SetStatus(status); });

    private void SetStatus(string status)
    {
        Volatile.Write(ref _status, status);
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        if (Volatile.Read(ref _disposed) == 0)
            Changed?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopBridge(null);
        try { FlushAsync().GetAwaiter().GetResult(); } catch { }
    }

    [LibraryImport("user32", EntryPoint = "OpenClipboard", SetLastError = true)] private static partial int OpenClipboard(nint owner);
    [LibraryImport("user32", EntryPoint = "CloseClipboard")] private static partial int CloseClipboard();
    [LibraryImport("user32", EntryPoint = "EmptyClipboard", SetLastError = true)] private static partial int EmptyClipboard();
    [LibraryImport("user32", EntryPoint = "GetClipboardData", SetLastError = true)] private static partial nint GetClipboardData(uint format);
    [LibraryImport("user32", EntryPoint = "SetClipboardData", SetLastError = true)] private static partial nint SetClipboardData(uint format, nint memory);
    [LibraryImport("user32", EntryPoint = "IsClipboardFormatAvailable")] private static partial int IsClipboardFormatAvailable(uint format);
    [LibraryImport("user32", EntryPoint = "GetClipboardSequenceNumber")] private static partial uint GetClipboardSequenceNumber();
    [LibraryImport("user32", EntryPoint = "GetClipboardOwner")] private static partial nint GetClipboardOwner();
    [LibraryImport("user32", EntryPoint = "GetWindowThreadProcessId")] private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
    [LibraryImport("user32", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterClipboardFormatW(string format);
    [LibraryImport("kernel32", EntryPoint = "GlobalAlloc", SetLastError = true)] private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32", EntryPoint = "GlobalFree", SetLastError = true)] private static partial nint GlobalFree(nint memory);
    [LibraryImport("kernel32", EntryPoint = "GlobalLock", SetLastError = true)] private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32", EntryPoint = "GlobalUnlock", SetLastError = true)] private static partial int GlobalUnlock(nint memory);
    [LibraryImport("kernel32", EntryPoint = "GlobalSize", SetLastError = true)] private static partial nuint GlobalSize(nint memory);
}
#endif

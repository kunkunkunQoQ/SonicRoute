#if SONICROUTE_PHONE
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using SonicRoute.AndroidAudio;

namespace SonicRoute.Native.Product;

/// <summary>Opt-in Unicode text clipboard bridge for one phone and the native host HWND.</summary>
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

    private readonly NativeApplicationWindow _window;
    private readonly IMainThread _owner;
    private CancellationTokenSource? _lifetime;
    private Channel<string>? _outgoing;
    private AndroidDevice? _device;
    private IAndroidClipboardClient? _client;
    private IDisposable? _retry;
    private string _status = "Off";
    private string? _lastText;
    private uint _lastWriteSequence;
    private int _enabled, _ownsClipboardInterest, _disposed, _readRetry;

    internal PhoneClipboard(AdbClient adb, NativeApplicationWindow window, IMainThread owner)
    {
        _window = window;
        _owner = owner;
        _adb = adb;
    }

    private readonly AdbClient _adb;
    internal bool Enabled => Volatile.Read(ref _enabled) != 0;
    internal string Status => Volatile.Read(ref _status);

    internal void Enable(AndroidDevice device)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(device);
        if (Enabled && _device is { } current && AndroidDeviceIdentity.Key(current) == AndroidDeviceIdentity.Key(device)) return;
        Disable();

        _window.SetInterest(_window.Interest | ShellInterest.Clipboard);
        if (!_window.Interest.HasFlag(ShellInterest.Clipboard))
        {
            SetStatus("Clipboard listener unavailable");
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
        SetStatus("Connecting");
        _ = Task.Run(() => SendLoopAsync(client, outgoing.Reader, lifetime.Token));
        _ = Task.Run(() => WatchLoopAsync(device, client, outgoing.Writer, lifetime));
    }

    internal void Disable()
    {
        if (Interlocked.Exchange(ref _enabled, 0) == 0)
        {
            _retry?.Dispose(); _retry = null;
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
        SetStatus("Off");
    }

    /// <summary>Called by the host after WM_CLIPBOARDUPDATE, on the host STA.</summary>
    internal void OnClipboardChanged()
    {
        if (!Enabled) return;
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
                    SetStatusFromWorker("Connecting", client);
                    await client.WatchAsync(device,
                        () => SetStatusFromWorker("Connected", client),
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
                    attempt = Math.Min(attempt + 1, 6);
                    SetStatusFromWorker(error is IOException ? "Disconnected · reconnecting" : "Clipboard unavailable · reconnecting", client);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << (attempt - 1))), token).ConfigureAwait(false);
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
                        SetStatus("Off");
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
                    _owner.TryPost(() => { if (Enabled && ReferenceEquals(_client, client)) SetStatus("Connected"); });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    _owner.TryPost(() => { if (Enabled && ReferenceEquals(_client, client)) SetStatus("Clipboard write failed"); });
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ApplyPhoneText(string text, IAndroidClipboardClient client)
    {
        if (!Enabled || !ReferenceEquals(_client, client) || !AndroidClipboardClient.IsSupported(text)
            || string.Equals(text, _lastText, StringComparison.Ordinal)) return;
        uint sequence = GetClipboardSequenceNumber();
        string? current = ReadUnicodeText();
        if (string.Equals(current, text, StringComparison.Ordinal)) { _lastText = text; return; }
        if (!WriteUnicodeText(text, sequence)) { SetStatus("PC clipboard unavailable"); return; }
        _lastText = text;
        SetStatus("Connected");
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
            if (EmptyClipboard() == 0 || SetClipboardData(CfUnicodeText, memory) == 0) return false;
            memory = 0; // Windows owns the movable block after successful transfer.
            _lastWriteSequence = GetClipboardSequenceNumber();
            return true;
        }
        finally
        {
            if (memory != 0) GlobalFree(memory);
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

    private void SetStatusFromWorker(string status, IAndroidClipboardClient client) =>
        _owner.TryPost(() => { if (Enabled && ReferenceEquals(_client, client)) SetStatus(status); });
    private void SetStatus(string status) => Volatile.Write(ref _status, status);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Disable();
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

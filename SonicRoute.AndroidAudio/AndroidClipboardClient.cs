using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SonicRoute.AndroidAudio;

public interface IAndroidClipboardClient
{
    long Revision { get; }
    Task WatchAsync(AndroidDevice device, Action ready, Action<string> changed, CancellationToken token);
    Task SendAsync(string text, long expectedRevision, CancellationToken token);
}

/// <summary>One explicitly enabled phone's event stream, using the existing ADB server and runtime.</summary>
public sealed class AndroidClipboardClient(AdbClient adb) : IAndroidClipboardClient
{
    public const int MaxTextBytes = 64 * 1024;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private IAdbShellSession? _session;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private long _revision;
    public long Revision => Interlocked.Read(ref _revision);
    public static bool IsSupported(string? text) => text != null && text.Length <= MaxTextBytes
        && Encoding.UTF8.GetByteCount(text) <= MaxTextBytes;
    public static bool IsOwnedCommand(string command, string id) => Regex.IsMatch(id, @"\A[0-9a-f]{32}\z")
        && command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("org.sonicroute.android.ClipboardBridge")
        && command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(id);
    public async Task WatchAsync(AndroidDevice device, Action ready, Action<string> changed, CancellationToken token)
    {
        await adb.PrepareRuntimeAsync("audio", token).ConfigureAwait(false);
        adb.ValidateCatalog();
        string id = Guid.NewGuid().ToString("N"), remote = "/data/local/tmp/sonicroute_clipboard_" + Guid.NewGuid().ToString("N") + ".jar";
        string? pid = null;
        Task<string>? stderr = null;
        StreamReader? errReader = null;
        try
        {
            await adb.RunAsync(token, "-s", device.Serial, "push", adb.CatalogFile, remote).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var session = await adb.OpenShellAsync(device.Serial,
                "echo SR_CLIPBOARD_PID:$$; CLASSPATH=" + remote + " exec app_process / org.sonicroute.android.ClipboardBridge " + id,
                redirectInput: true, token).ConfigureAwait(false);
            _session = session;
            _writer = new StreamWriter(session.InputStream, Utf8NoBom, bufferSize: 4096, leaveOpen: true) { NewLine = "\n" };
            errReader = new StreamReader(session.ErrorStream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            stderr = AndroidMediaSessionClient.ReadDiagnosticTailAsync(errReader);
            using var stdout = new StreamReader(session.OutputStream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            bool started = false;
            while (await stdout.ReadLineAsync(started ? token : startup.Token).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("SR_CLIPBOARD_PID:", StringComparison.Ordinal))
                { string value = line[17..].Trim(); if (Regex.IsMatch(value, @"\A[1-9]\d*\z")) pid = value; }
                else if (line.StartsWith('{'))
                {
                    if (line.Length > MaxTextBytes * 6 + 32) throw AndroidError.WithCode(new IOException("剪贴板消息过长。"), AndroidErrorCode.ClipboardMessageTooLong);
                    using var json = JsonDocument.Parse(line);
                    if (json.RootElement.TryGetProperty("error", out _)) throw AndroidError.WithCode(new IOException("手机暂时无法同步剪贴板，请检查系统权限。"), AndroidErrorCode.ClipboardPermissionError);
                    if (json.RootElement.TryGetProperty("revision", out var revision)) Interlocked.Exchange(ref _revision, revision.GetInt64());
                    if (json.RootElement.TryGetProperty("ready", out var value) && value.GetBoolean())
                    { started = true; startup.CancelAfter(Timeout.Infinite); ready(); }
                    else if (json.RootElement.TryGetProperty("text", out var text) && IsSupported(text.GetString())) changed(text.GetString()!);
                }
            }
            token.ThrowIfCancellationRequested();
            throw AndroidError.WithCode(new IOException("剪贴板连接已断开，刷新设备可重试。"), AndroidErrorCode.ClipboardDisconnected);
        }
        finally
        {
            var session = _session; _session = null;
            var writer = _writer; _writer = null;
            if (session != null)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await session.CloseInputAsync(close.Token).ConfigureAwait(false); } catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException) { }
                try { await session.WaitForExitAsync(close.Token).ConfigureAwait(false); }
                catch { try { session.Kill(); } catch { } }
            }
            writer?.Dispose();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try
            {
                if (pid != null)
                {
                    string command = await adb.RunAsync(cleanup.Token, "-s", device.Serial, "shell",
                        "if [ -r /proc/" + pid + "/cmdline ]; then cat /proc/" + pid + "/cmdline; fi").ConfigureAwait(false);
                    if (IsOwnedCommand(command.Replace('\0', ' '), id))
                        await adb.RunAsync(cleanup.Token, "-s", device.Serial, "shell", "kill", "-TERM", pid).ConfigureAwait(false);
                }
            }
            catch (Exception) { /* Transport may already be gone; never kill a shared process. */ }
            try { await adb.RunAsync(cleanup.Token, "-s", device.Serial, "shell", "rm", "-f", remote).ConfigureAwait(false); }
            catch (Exception) { }
            if (stderr != null) { try { await stderr.ConfigureAwait(false); } catch { } } // Drain but do not retain or log clipboard helper output.
            errReader?.Dispose();
            if (session != null) await session.DisposeAsync().ConfigureAwait(false);
        }
    }
    public async Task SendAsync(string text, long expectedRevision, CancellationToken token)
    {
        if (!IsSupported(text)) return;
        await _sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var writer = _writer ?? throw AndroidError.WithCode(new IOException("剪贴板尚未连接。"), AndroidErrorCode.ClipboardNotConnected);
            // The coordinator supplies one bounded writer per phone, rather than queued writes per copy.
            string line = JsonSerializer.Serialize(new { text, revision = expectedRevision });
            await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
            await writer.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }
}

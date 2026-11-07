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
    private Process? _process;
    private long _revision;
    public long Revision => Interlocked.Read(ref _revision);
    public static bool IsSupported(string? text) => text != null && text.Length <= MaxTextBytes
        && Encoding.UTF8.GetByteCount(text) <= MaxTextBytes;
    public static bool IsOwnedCommand(string command, string id) => Regex.IsMatch(id, @"\A[0-9a-f]{32}\z")
        && command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("org.sonicroute.android.ClipboardBridge")
        && command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(id);
    public async Task WatchAsync(AndroidDevice device, Action ready, Action<string> changed, CancellationToken token)
    {
        await adb.PrepareRuntimeAsync("audio", token).ConfigureAwait(false); adb.ValidateServer();
        string id = Guid.NewGuid().ToString("N"), remote = "/data/local/tmp/sonicroute_clipboard_" + Guid.NewGuid().ToString("N") + ".jar";
        string server = remote + ".server";
        string? pid = null;
        Task<string>? stderr = null;
        try
        {
            await Task.WhenAll(
                adb.RunAsync(token, "-s", device.Serial, "push", Path.Combine(adb.RuntimeDirectory, "sonicroute-catalog.jar"), remote),
                adb.RunAsync(token, "-s", device.Serial, "push", adb.ServerFile, server)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var process = adb.StartWithInput("-s", device.Serial, "shell",
                "echo SR_CLIPBOARD_PID:$$; CLASSPATH=" + remote + ":" + server + " exec app_process / org.sonicroute.android.ClipboardBridge " + id);
            _process = process;
            stderr = AndroidMediaSessionClient.ReadDiagnosticTailAsync(process.StandardError);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            bool started = false;
            while (await process.StandardOutput.ReadLineAsync(started ? token : startup.Token).ConfigureAwait(false) is { } line)
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
            var process = _process; _process = null;
            if (process != null)
            {
                try { process.StandardInput.Close(); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(close.Token).ConfigureAwait(false); }
                catch { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
            }
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
            try { await adb.RunAsync(cleanup.Token, "-s", device.Serial, "shell", "rm", "-f", remote, server).ConfigureAwait(false); }
            catch (Exception) { }
            if (stderr != null) { try { await stderr.ConfigureAwait(false); } catch { } } // Drain but do not retain or log clipboard helper output.
            process?.Dispose();
        }
    }
    public async Task SendAsync(string text, long expectedRevision, CancellationToken token)
    {
        if (!IsSupported(text)) return;
        var process = _process ?? throw AndroidError.WithCode(new IOException("剪贴板尚未连接。"), AndroidErrorCode.ClipboardNotConnected);
        // The coordinator supplies one bounded writer per phone, rather than queued writes per copy.
        string line = JsonSerializer.Serialize(new { text, revision = expectedRevision });
        await process.StandardInput.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }
}

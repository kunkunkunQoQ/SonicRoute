using System.Diagnostics;
using System.Net.Sockets;

namespace SonicRoute.AndroidAudio;

/// <summary>Owns only one server PID, unique jar/socket and exact ADB forward. Never runs scrcpy.exe.</summary>
public sealed class ScrcpyAdbTransport : IAudioTransport
{
    private readonly AdbClient _adb;
    private readonly Action<string> _log;
    private readonly string _scid = Random.Shared.Next(1, int.MaxValue).ToString("x8");
    private readonly CancellationTokenSource _logLifetime = new();
    private string? _serial, _remote;
    private int _port, _serverPid;
    private Process? _server;
    private TcpClient? _socket;
    private Task? _logs;
    private bool _disposed;
    public ScrcpyAdbTransport(AdbClient adb, Action<string>? log = null) { _adb = adb; _log = log ?? (_ => { }); }

    public async Task<Stream> ConnectAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, CancellationToken token)
    {
        _serial = device.Serial;
        await _adb.PrepareRuntimeAsync("audio", token).ConfigureAwait(false);
        _adb.ValidateServer();
        int uid = await _adb.ResolveUidAsync(device, app.Package, token).ConfigureAwait(false);
        if (uid != app.Uid) throw new IOException("应用 UID 已改变，请刷新列表后重新拖入。");
        string sdkText = await _adb.RunAsync(token, "-s", _serial, "shell", "getprop", "ro.build.version.sdk").ConfigureAwait(false);
        if (!int.TryParse(sdkText.Trim(), out int sdk) || sdk < 33) throw new NotSupportedException("应用音频捕获要求 Android 13 或更高；已验证的参考机型为 Android 16。");
        if (!File.Exists(_adb.ServerFile)) throw new FileNotFoundException("Missing verified scrcpy server.", _adb.ServerFile);
        _remote = "/data/local/tmp/sonicroute_audio_" + _scid + ".jar";
        await _adb.RunAsync(token, "-s", _serial, "push", _adb.ServerFile, _remote).ConfigureAwait(false);
        string forward = await _adb.RunAsync(token, "-s", _serial, "forward", "tcp:0", "localabstract:scrcpy_" + _scid).ConfigureAwait(false);
        if (!int.TryParse(forward.Trim(), out _port) || _port is < 1 or > 65535) throw new IOException("Invalid ADB forward port.");
        string command = "echo SR_PID:$$; CLASSPATH=" + _remote + " exec app_process / com.genymobile.scrcpy.Server 3.3.4 "
            + $"scid={_scid} log_level=info video=false audio=true control=false audio_codec=opus audio_source=playback "
            + $"audio_dup={keepPhoneAudio.ToString().ToLowerInvariant()} audio_target_packages={app.Package} audio_mix_mode=uid "
            + "tunnel_forward=true send_device_meta=false send_dummy_byte=true cleanup=false";
        _server = _adb.Start("-s", _serial, "shell", command);
        _logs = Task.WhenAll(ReadLogAsync(_server.StandardOutput), ReadLogAsync(_server.StandardError));
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
        connect.CancelAfter(TimeSpan.FromSeconds(12));
        while (true)
        {
            connect.Token.ThrowIfCancellationRequested();
            var socket = new TcpClient();
            try
            {
                await socket.ConnectAsync("127.0.0.1", _port, connect.Token).ConfigureAwait(false);
                byte[] dummy = new byte[1];
                await socket.GetStream().ReadExactlyAsync(dummy, connect.Token).ConfigureAwait(false);
                if (dummy[0] != 0) throw new InvalidDataException("Invalid scrcpy handshake.");
                _socket = socket;
                return socket.GetStream();
            }
            catch (Exception ex) when (ex is SocketException or EndOfStreamException)
            {
                socket.Dispose();
                if (_server.HasExited) throw new IOException("手机音频服务已退出；请查看日志。");
                await Task.Delay(100, connect.Token).ConfigureAwait(false); // startup only; no idle retries
            }
            catch { socket.Dispose(); throw; }
        }
    }

    private async Task ReadLogAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync(_logLifetime.Token).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("SR_PID:") && int.TryParse(line[7..].Trim(), out int pid)) Volatile.Write(ref _serverPid, pid);
                else _log(line.Length > 2048 ? line[..2048] : line);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _socket?.Dispose(); _socket = null; // EOF causes encoder stop and AudioPolicy release
        if (_server != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _server.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (_serverPid > 0 && _serial != null)
                {
                    try
                    {
                        string command = await _adb.RunAsync(cleanup.Token, "-s", _serial, "shell", "cat", $"/proc/{_serverPid}/cmdline").ConfigureAwait(false);
                        if (command.Contains("com.genymobile.scrcpy.Server", StringComparison.Ordinal) && command.Contains("scid=" + _scid, StringComparison.Ordinal))
                            await _adb.RunAsync(cleanup.Token, "-s", _serial, "shell", "kill", "-TERM", _serverPid.ToString()).ConfigureAwait(false);
                    }
                    catch (Exception ex) { _log("Owned server cleanup: " + ex.Message); }
                }
                try { if (!_server.HasExited) _server.Kill(true); } catch (InvalidOperationException) { }
            }
            _logLifetime.Cancel();
            if (_logs != null) { try { await _logs.ConfigureAwait(false); } catch (ObjectDisposedException) { } }
            _server.Dispose(); _server = null;
        }
        if (_serial != null)
        {
            if (_port != 0)
            {
                try { await _adb.RunAsync(cleanup.Token, "-s", _serial, "forward", "--remove", "tcp:" + _port).ConfigureAwait(false); }
                catch (Exception ex) { _log("Forward cleanup: " + ex.Message); }
            }
            if (_remote != null)
            {
                try { await _adb.RunAsync(cleanup.Token, "-s", _serial, "shell", "rm", "-f", _remote).ConfigureAwait(false); }
                catch (Exception ex) { _log("Temporary server cleanup: " + ex.Message); }
            }
        }
        _logLifetime.Dispose();
    }
}

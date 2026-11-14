using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SonicRoute.AndroidAudio;

/// <summary>Arguments sent to the remote Android video server when opening a stream session.</summary>
public sealed record VideoServerOptions(
    bool NewDisplay = true,
    bool MirrorDeviceDisplay = false,
    AndroidVirtualDisplaySize? InitialDisplaySize = null,
    string VideoCodec = "h264",
    int MaxFps = 30,
    int VideoBitRate = 4_000_000,
    string? VideoEncoder = null,
    bool Control = true,
    bool RawKeyEvents = false,
    bool SendDeviceMeta = true,
    bool SendDummyByte = true,
    bool TunnelForward = true,
    bool StartAppOnServer = true,
    string ServerClassName = "org.sonicroute.android.SonicRouteServer",
    string SocketPrefix = "sonicroute_");

/// <summary>Request descriptor passed to <see cref="IVideoStreamTransport"/>.</summary>
public sealed record VideoStreamOpenRequest(
    AndroidDevice Device,
    AndroidApp App,
    string Scid,
    VideoServerOptions Options);

/// <summary>
/// Handshaken video + control byte streams and remote process lifetime.
/// When <see cref="VideoServerOptions.SendDummyByte"/> is true, the transport has already consumed the
/// leading handshake byte so <see cref="VideoStream"/> begins directly at device metadata / codec header.
/// </summary>
public sealed class VideoStreamSession : IAsyncDisposable
{
    private readonly Func<ValueTask> _disposeAsync;
    private int _disposed;

    public VideoStreamSession(
        Stream videoStream,
        Stream? controlStream,
        Task serverCompletion,
        Action<Action<string>>? subscribeLogs,
        Func<ValueTask> disposeAsync)
    {
        VideoStream = videoStream ?? throw new ArgumentNullException(nameof(videoStream));
        ControlStream = controlStream;
        ServerCompletion = serverCompletion ?? Task.CompletedTask;
        _disposeAsync = disposeAsync ?? (() => ValueTask.CompletedTask);
        subscribeLogs?.Invoke(line => LogLineReceived?.Invoke(line));
    }

    public Stream VideoStream { get; }
    public Stream? ControlStream { get; }
    public Task ServerCompletion { get; }
    public event Action<string>? LogLineReceived;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        return _disposeAsync();
    }
}

/// <summary>
/// Abstract transport boundary for opening Android video and control channels.
/// Stage 2 binds over ADB local port forwarding; Stage 4 can swap in direct in-memory ADB socket streams
/// without modifying the H.264 decoder, D3D11 surface, or native Win32 window.
/// </summary>
public interface IVideoStreamTransport
{
    Task<VideoStreamSession> OpenAsync(VideoStreamOpenRequest request, CancellationToken cancellationToken);
}

public delegate Task<VideoStreamSession> VideoStreamTransportFactory(
    VideoStreamOpenRequest request,
    CancellationToken cancellationToken);

public sealed class DelegateVideoStreamTransport(VideoStreamTransportFactory factory) : IVideoStreamTransport
{
    public Task<VideoStreamSession> OpenAsync(VideoStreamOpenRequest request, CancellationToken cancellationToken)
        => factory(request, cancellationToken);
}

/// <summary>Video transport: pushes the MIT server jar if needed, launches app_process via IAdbTransport, and opens direct localabstract streams.</summary>
public sealed class AdbVideoStreamTransport : IVideoStreamTransport
{
    private const string RemoteVideoServerPath = "/data/local/tmp/sonicroute-video-server.jar";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private readonly AdbClient _adb;
    private readonly Func<CancellationToken, Task<string>> _localServerPathProvider;

    public AdbVideoStreamTransport(AdbClient adb)
        : this(adb, async ct =>
        {
            await adb.PrepareRuntimeAsync("audio", ct).ConfigureAwait(false);
            adb.ValidateCatalog();
            return adb.CatalogFile;
        })
    {
    }

    public AdbVideoStreamTransport(AdbClient adb, VirtualDisplayRuntime runtime)
        : this(adb, async ct =>
        {
            await adb.PrepareRuntimeAsync("audio", ct).ConfigureAwait(false);
            await Task.Run(runtime.Validate, ct).ConfigureAwait(false);
            return runtime.ServerFile;
        })
    {
    }

    public AdbVideoStreamTransport(AdbClient adb, Func<CancellationToken, Task<string>> localServerPathProvider)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _localServerPathProvider = localServerPathProvider ?? throw new ArgumentNullException(nameof(localServerPathProvider));
    }

    public static string GenerateScid()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        uint value = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]) & 0x7FFF_FFFFu;
        return value.ToString("x8", CultureInfo.InvariantCulture);
    }

    public async Task<VideoStreamSession> OpenAsync(VideoStreamOpenRequest request, CancellationToken cancellationToken)
    {
        if (request.Device.State != "device")
            throw AndroidError.WithCode(new IOException("手机尚未连接，请刷新设备后重试。"), AndroidErrorCode.DisplayPhoneNotConnected);
        if (!request.App.IsDeviceAudio)
            AdbClient.ValidatePackage(request.App.Package);

        string localServerPath = await _localServerPathProvider(cancellationToken).ConfigureAwait(false);
        await EnsureRemoteServerAsync(request.Device.Serial, localServerPath, cancellationToken).ConfigureAwait(false);

        string serial = request.Device.Serial;
        string scid = request.Scid;
        string socketPrefix = string.IsNullOrWhiteSpace(request.Options.SocketPrefix) ? "sonicroute_" : request.Options.SocketPrefix;
        string socketName = socketPrefix + scid;

        IAdbShellSession? server = null;
        Stream? videoStream = null;
        Stream? controlStream = null;
        int remoteServerPid = 0;
        Action<string>? logHandler = null;
        var logLock = new object();
        var recentLogs = new Queue<string>(32);
        var serverLifetimeCts = new CancellationTokenSource();

        void EmitLog(string line)
        {
            if (line.Length > 2048) line = line[..2048];
            lock (logLock)
            {
                if (recentLogs.Count >= 32) recentLogs.Dequeue();
                recentLogs.Enqueue(line);
            }
            logHandler?.Invoke(line);
        }

        try
        {
            string cmd = BuildServerShellCommand(RemoteVideoServerPath, request);
            server = await _adb.OpenShellAsync(serial, cmd, redirectInput: false, cancellationToken).ConfigureAwait(false);

            async Task ReadStreamLogsAsync(Stream source)
            {
                try
                {
                    using var reader = new StreamReader(source, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 2048, leaveOpen: true);
                    while (await reader.ReadLineAsync(serverLifetimeCts.Token).ConfigureAwait(false) is { } line)
                    {
                        if (line.StartsWith("SR_PID:", StringComparison.Ordinal)
                            && int.TryParse(line[7..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                        {
                            Volatile.Write(ref remoteServerPid, pid);
                        }
                        else
                        {
                            EmitLog(line);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }

            Task stdoutLogs = ReadStreamLogsAsync(server.OutputStream);
            Task stderrLogs = ReadStreamLogsAsync(server.ErrorStream);
            Task serverCompletion = WaitServerExitAsync(server, stdoutLogs, stderrLogs, serverLifetimeCts.Token);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));

            async Task<Stream> OpenSocketWithRetryAsync(bool expectDummyByte)
            {
                while (true)
                {
                    connectCts.Token.ThrowIfCancellationRequested();
                    Stream? candidate = null;
                    try
                    {
                        candidate = await _adb.OpenLocalAbstractAsync(serial, socketName, connectCts.Token).ConfigureAwait(false);
                        if (expectDummyByte)
                        {
                            byte[] dummy = new byte[1];
                            await candidate.ReadExactlyAsync(dummy, connectCts.Token).ConfigureAwait(false);
                            if (dummy[0] != 0) throw new InvalidDataException("Invalid SonicRoute video handshake byte.");
                        }
                        return candidate;
                    }
                    catch (Exception ex) when (ex is SocketException or EndOfStreamException or IOException)
                    {
                        if (candidate != null)
                        {
                            try { await candidate.DisposeAsync().ConfigureAwait(false); } catch { }
                        }
                        if (server.HasExited)
                        {
                            string tail = SnapshotLogs(logLock, recentLogs);
                            throw AndroidError.WithCode(
                                new IOException(string.IsNullOrWhiteSpace(tail) ? "手机视频服务已提前退出。" : $"手机视频服务已退出: {tail}"),
                                AndroidErrorCode.DisplayStartFailed);
                        }
                        await Task.Delay(80, connectCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        if (candidate != null)
                        {
                            try { await candidate.DisposeAsync().ConfigureAwait(false); } catch { }
                        }
                        throw;
                    }
                }
            }

            videoStream = await OpenSocketWithRetryAsync(request.Options.SendDummyByte).ConfigureAwait(false);

            if (request.Options.Control)
            {
                controlStream = await OpenSocketWithRetryAsync(expectDummyByte: false).ConfigureAwait(false);
            }

            async ValueTask DisposeAsync()
            {
                if (controlStream != null) { try { await controlStream.DisposeAsync().ConfigureAwait(false); } catch { } }
                if (videoStream != null) { try { await videoStream.DisposeAsync().ConfigureAwait(false); } catch { } }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                if (server != null)
                {
                    if (!server.HasExited)
                    {
                        int pid = Volatile.Read(ref remoteServerPid);
                        if (pid > 0)
                        {
                            try
                            {
                                string cmdline = await _adb.RunAsync(cleanup.Token, "-s", serial, "shell", "cat", $"/proc/{pid}/cmdline").ConfigureAwait(false);
                                if (IsOwnedServerCommand(cmdline.Replace('\0', ' '), scid))
                                    await _adb.RunAsync(cleanup.Token, "-s", serial, "shell", "kill", "-TERM", pid.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                            }
                            catch { }
                        }
                        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try { await server.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { try { server.Kill(); } catch { } }
                    }
                    serverLifetimeCts.Cancel();
                    try { await server.DisposeAsync().ConfigureAwait(false); } catch { }
                    try { await serverCompletion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                }
                serverLifetimeCts.Dispose();
            }

            return new VideoStreamSession(
                videoStream,
                controlStream,
                serverCompletion,
                handler => logHandler = handler,
                DisposeAsync);
        }
        catch
        {
            if (controlStream != null) { try { await controlStream.DisposeAsync().ConfigureAwait(false); } catch { } }
            if (videoStream != null) { try { await videoStream.DisposeAsync().ConfigureAwait(false); } catch { } }
            serverLifetimeCts.Cancel();
            if (server != null)
            {
                try { if (!server.HasExited) server.Kill(); } catch { }
                try { await server.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            serverLifetimeCts.Dispose();
            throw;
        }
    }

    private async Task EnsureRemoteServerAsync(string serial, string localPath, CancellationToken token)
    {
        var info = new FileInfo(localPath);
        if (!info.Exists)
            throw AndroidError.WithCode(new FileNotFoundException("缺少手机视频服务组件。", localPath), AndroidErrorCode.MissingDisplayRuntime);

        string check = await _adb.RunAsync(token, "-s", serial, "shell",
            $"ls -l {RemoteVideoServerPath} 2>/dev/null || echo MISSING").ConfigureAwait(false);
        string expectedLen = info.Length.ToString(CultureInfo.InvariantCulture);
        if (check.Contains("MISSING", StringComparison.Ordinal) || !Regex.IsMatch(check, $@"\b{expectedLen}\b"))
        {
            await _adb.RunAsync(token, "-s", serial, "push", localPath, RemoteVideoServerPath).ConfigureAwait(false);
            await _adb.RunAsync(token, "-s", serial, "shell", "chmod", "0644", RemoteVideoServerPath).ConfigureAwait(false);
        }
    }

    public static string BuildServerShellCommand(string remoteJarPath, VideoStreamOpenRequest request)
    {
        var opts = request.Options;
        var sb = new StringBuilder();
        string serverClass = string.IsNullOrWhiteSpace(opts.ServerClassName) ? "org.sonicroute.android.SonicRouteServer" : opts.ServerClassName;
        sb.Append("echo SR_PID:$$; CLASSPATH=").Append(remoteJarPath)
          .Append(" exec app_process / ").Append(serverClass).Append(" 3.3.4")
          .Append(" scid=").Append(request.Scid);
        if (string.Equals(serverClass, "org.sonicroute.android.SonicRouteServer", StringComparison.Ordinal))
        {
            string prefixArg = string.IsNullOrWhiteSpace(opts.SocketPrefix) ? "sonicroute" : opts.SocketPrefix.TrimEnd('_');
            if (!string.IsNullOrEmpty(prefixArg))
                sb.Append(" socket_prefix=").Append(prefixArg);
        }
        sb.Append(" log_level=info")
          .Append(" video=true")
          .Append(" video_codec=").Append(opts.VideoCodec)
          .Append(" audio=false")
          .Append(" clipboard_autosync=false")
          .Append(" tunnel_forward=").Append(opts.TunnelForward ? "true" : "false")
          .Append(" send_device_meta=").Append(opts.SendDeviceMeta ? "true" : "false")
          .Append(" send_dummy_byte=").Append(opts.SendDummyByte ? "true" : "false")
          .Append(" control=").Append(opts.Control ? "true" : "false")
          .Append(" cleanup=false")
          .Append(" max_fps=").Append(opts.MaxFps.ToString(CultureInfo.InvariantCulture))
          .Append(" video_bit_rate=").Append(opts.VideoBitRate.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(opts.VideoEncoder))
            sb.Append(" video_encoder=").Append(opts.VideoEncoder);
        if (opts.MirrorDeviceDisplay)
        {
            sb.Append(" display_id=0");
        }
        else if (opts.NewDisplay)
        {
            if (opts.InitialDisplaySize is { } size && size.IsValid())
                sb.Append(" new_display=").Append(size.Width.ToString(CultureInfo.InvariantCulture))
                  .Append('x').Append(size.Height.ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(" new_display");

            if (opts.StartAppOnServer && !request.App.IsDeviceAudio)
                sb.Append(" start_app=").Append(request.App.Package);
            sb.Append(" vd_system_decorations=false display_ime_policy=hide");
        }
        return sb.ToString();
    }

    internal static bool IsOwnedServerCommand(string command, string scid)
        => Regex.IsMatch(scid, @"\A[0-9a-fA-F]{8}\z")
        && (command.Contains("com.genymobile.scrcpy.Server", StringComparison.Ordinal)
            || command.Contains("org.sonicroute.android.SonicRouteServer", StringComparison.Ordinal))
        && Regex.IsMatch(command, @"(?:^|\s)scid=" + Regex.Escape(scid) + @"(?:\s|$)");

    private static async Task WaitServerExitAsync(IAdbShellSession process, Task stdout, Task stderr, CancellationToken token)
    {
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static string SnapshotLogs(object gate, Queue<string> logs)
    {
        lock (gate) return string.Join(" | ", logs);
    }
}

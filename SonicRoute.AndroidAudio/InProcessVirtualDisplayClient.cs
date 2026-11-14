using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace SonicRoute.AndroidAudio;

/// <summary>
/// Stage 2+ in-process Android virtual display &amp; mirror video client.
/// Replaces external <c>scrcpy.exe</c> + FFmpeg + SDL2 with Media Foundation H.264, D3D11 NV12 rendering,
/// WIC icon decoding, a dedicated native Win32 HWND, and an abstract <see cref="IVideoStreamTransport"/>.
/// </summary>
public sealed class InProcessVirtualDisplayClient : IVirtualDisplayClient
{
    private readonly AdbClient _adb;
    private readonly IVideoStreamTransport _transport;
    private readonly Func<AndroidAudioSettingsStore>? _settingsProvider;
    private readonly Queue<string> _logs = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? _sessionCts;
    private VideoStreamSession? _session;
    private NativeVideoWindow? _window;
    private D3D11VideoSurfaceRenderer? _renderer;
    private MediaFoundationH264Decoder? _decoder;
    private Channel<byte[]>? _controlOutbound;
    private Task _completion = Task.CompletedTask;

    private AndroidAudioSettingsStore? _settings;
    private AndroidDevice? _device;
    private string? _package;
    private bool _isDeviceAudio;
    private bool _firstFramePresented;
    private int _disposed;
    private VirtualDisplayState _state = VirtualDisplayState.Starting;

    public InProcessVirtualDisplayClient(
        AdbClient adb,
        Func<AndroidAudioSettingsStore>? settings = null)
        : this(adb, new AdbVideoStreamTransport(adb), settings)
    {
    }

    public InProcessVirtualDisplayClient(
        AdbClient adb,
        VirtualDisplayRuntime runtime,
        Func<AndroidAudioSettingsStore>? settings = null)
        : this(adb, new AdbVideoStreamTransport(adb, runtime), settings)
    {
    }

    public InProcessVirtualDisplayClient(
        AdbClient adb,
        IVideoStreamTransport transport,
        Func<AndroidAudioSettingsStore>? settings = null)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _settingsProvider = settings;
    }

    public Task Completion => _completion;
    public nint WindowHandle => _window?.WindowHandle ?? 0;
    public int ProcessId => WindowHandle != 0 ? Environment.ProcessId : 0;
    public VirtualDisplayState State => _state;
    public event Action<VirtualDisplayState>? StateChanged;

    public async Task StartAsync(AndroidDevice device, AndroidApp app, int x, int y, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (device.State != "device")
            throw AndroidError.WithCode(new IOException("手机尚未连接，请刷新设备后重试。"), AndroidErrorCode.DisplayPhoneNotConnected);

        if (!app.IsDeviceAudio)
        {
            int uid = await _adb.ResolveUidAsync(device, app.Package, token).ConfigureAwait(false);
            if (uid != app.Uid)
                throw AndroidError.WithCode(new IOException("应用身份已改变，请刷新手机应用列表。"), AndroidErrorCode.DisplayAppUidChanged);
        }

        token.ThrowIfCancellationRequested();
        _device = device;
        _package = app.Package;
        _isDeviceAudio = app.IsDeviceAudio;
        _settings = _settingsProvider?.Invoke();

        var saved = _settings?.Get(device, app.Package);
        var streaming = (_settings?.GetStreaming(device) ?? new()).Normalize();
        var windowSize = saved?.WindowSize;
        AndroidVirtualDisplaySize? displaySize = null;
        if (!app.IsDeviceAudio)
        {
            if (windowSize is { } wsInit && wsInit.IsValid())
            {
                var (fw, fh) = FlexResizeMath.AlignAndClamp(wsInit.Width, wsInit.Height);
                displaySize = new AndroidVirtualDisplaySize(fw, fh);
            }
            else if (saved?.DisplaySize is { } dsSaved && dsSaved.IsValid())
            {
                displaySize = dsSaved;
            }
        }

        var serverOpts = new VideoServerOptions(
            NewDisplay: !app.IsDeviceAudio,
            MirrorDeviceDisplay: app.IsDeviceAudio,
            InitialDisplaySize: displaySize is { } ds && ds.IsValid() ? ds : null,
            VideoCodec: "h264",
            MaxFps: streaming.MaxFps,
            VideoBitRate: streaming.VideoBitRate,
            VideoEncoder: streaming.VideoEncoder,
            Control: true,
            RawKeyEvents: app.Category == "games",
            SendDeviceMeta: true,
            SendDummyByte: true,
            TunnelForward: true,
            StartAppOnServer: !app.IsDeviceAudio,
            ServerClassName: "org.sonicroute.android.SonicRouteServer",
            SocketPrefix: "sonicroute_");

        var openRequest = new VideoStreamOpenRequest(device, app, AdbVideoStreamTransport.GenerateScid(), serverOpts);
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        try
        {
            _session = await _transport.OpenAsync(openRequest, token).ConfigureAwait(false);
            _session.LogLineReceived += OnServerLogLine;

            var streamHeader = await ScrcpyVideoStreamHeader.ReadAsync(_session.VideoStream, expectDeviceMeta: true, token).ConfigureAwait(false);
            if (streamHeader.CodecId == 0)
                throw AndroidError.WithCode(new IOException("手机端视频流未启用。"), AndroidErrorCode.DisplayOpenFailed);
            if (streamHeader.CodecId == 1)
                throw AndroidError.WithCode(new IOException("手机端视频配置失败；请检查编码器设置。"), AndroidErrorCode.DisplayOpenFailed);
            if (streamHeader.CodecId != ScrcpyVideoStreamHeader.CodecH264)
                throw AndroidError.WithCode(new IOException($"不支持的视频编码器标识: 0x{streamHeader.CodecId:X8}。"), AndroidErrorCode.DisplayOpenFailed);

            int initW = windowSize is { } ws && ws.IsValid() ? ws.Width : Math.Max(240, streamHeader.Width);
            int initH = windowSize is { } ws2 && ws2.IsValid() ? ws2.Height : Math.Max(240, streamHeader.Height);
            string aumid = BuildWindowAppUserModelId(AndroidDeviceIdentity.Key(device), app.Package);

            _window = new NativeVideoWindow(
                app.Name,
                isMirrorMode: app.IsDeviceAudio,
                preferText: app.Category != "games",
                app.IconPng,
                x, y,
                initW, initH,
                aumid);
            _window.UpdateContentSize(streamHeader.Width, streamHeader.Height);

            nint hwnd = await _window.CreateAsync(token).ConfigureAwait(false);
            _renderer = new D3D11VideoSurfaceRenderer(hwnd, _window.ClientWidth, _window.ClientHeight, isMirrorMode: app.IsDeviceAudio);
            _decoder = new MediaFoundationH264Decoder(_renderer.DeviceHandle, streamHeader.Width, streamHeader.Height);

            WireWindowAndControlEvents(streamHeader.Width, streamHeader.Height);

            _completion = RunSessionLoopsAsync(_session, _sessionCts.Token);

            using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            readyTimeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var reg = readyTimeout.Token.Register(() =>
                _ready.TrySetException(AndroidError.WithCode(
                    new TimeoutException("打开手机应用屏幕超时；请查看日志并重试。"),
                    AndroidErrorCode.DisplayOpenTimeout)));
            await _ready.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && !token.IsCancellationRequested)
                throw ConnectionError("连接手机屏幕失败。", AndroidErrorCode.DisplayOpenFailed);
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void WireWindowAndControlEvents(int initialFrameWidth, int initialFrameHeight)
    {
        var window = _window!;
        var renderer = _renderer!;

        _controlOutbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        window.PaintRequested += renderer.RenderLastFrame;
        window.ClientSizeChanged += renderer.Resize;
        window.FlexResizeRequested += (w, h) =>
        {
            byte[] packet = new byte[5];
            ScrcpyControlMessageWriter.WriteResizeDisplay(packet, w, h);
            EnqueueControl(packet);
        };
        window.WindowSizeCommitted += (w, h) =>
        {
            if (!_isDeviceAudio && _device != null && _package != null && !window.IsMaximized)
                _settings?.RememberWindow(_device, _package, new AndroidWindowSize(w, h));
        };
        window.CommitInitialWindowSize();
        window.SyncInitialFlexSize(initialFrameWidth, initialFrameHeight);
        window.RestoreRequested += SendRestoreStartApp;
        window.CloseRequested += () => _sessionCts?.Cancel();

        window.TouchEvent += (action, ptrId, x, y, sw, sh, pressure, actionBtn, buttons) =>
        {
            byte[] packet = new byte[32];
            ScrcpyControlMessageWriter.WriteInjectTouch(packet, action, ptrId, x, y, sw, sh, pressure, actionBtn, buttons);
            EnqueueControl(packet);
        };
        window.ScrollEvent += (x, y, sw, sh, hScroll, vScroll, buttons) =>
        {
            byte[] packet = new byte[21];
            ScrcpyControlMessageWriter.WriteInjectScroll(packet, x, y, sw, sh, hScroll, vScroll, buttons);
            EnqueueControl(packet);
        };
        window.KeyEvent += (action, keycode, repeat, meta) =>
        {
            byte[] packet = new byte[14];
            ScrcpyControlMessageWriter.WriteInjectKeycode(packet, action, keycode, repeat, meta);
            EnqueueControl(packet);
        };
        window.TextInput += text =>
        {
            if (!string.IsNullOrEmpty(text))
                EnqueueControl(ScrcpyControlMessageWriter.BuildInjectTextPacket(text));
        };
        window.MouseShortcut += (binding, action) =>
        {
            switch (binding)
            {
                case NativeWindowMouseBinding.Back:
                {
                    byte[] p = new byte[2];
                    ScrcpyControlMessageWriter.WriteBackOrScreenOn(p, action);
                    EnqueueControl(p);
                    break;
                }
                case NativeWindowMouseBinding.Home:
                {
                    byte[] p = new byte[14];
                    ScrcpyControlMessageWriter.WriteInjectKeycode(p, action, AndroidKeycodeMapper.AKeycodeHome, 0, 0);
                    EnqueueControl(p);
                    break;
                }
                case NativeWindowMouseBinding.AppSwitch:
                {
                    byte[] p = new byte[14];
                    ScrcpyControlMessageWriter.WriteInjectKeycode(p, action, AndroidKeycodeMapper.AKeycodeAppSwitch, 0, 0);
                    EnqueueControl(p);
                    break;
                }
                case NativeWindowMouseBinding.ExpandNotificationPanel when action == ScrcpyControlMessageWriter.KeyActionDown:
                    EnqueueControl([ScrcpyControlMessageWriter.TypeExpandNotificationPanel]);
                    break;
                case NativeWindowMouseBinding.ExpandSettingsPanel when action == ScrcpyControlMessageWriter.KeyActionDown:
                    EnqueueControl([ScrcpyControlMessageWriter.TypeExpandSettingsPanel]);
                    break;
            }
        };
        window.GamepadStateChanged += state =>
        {
            byte[] packet = new byte[14];
            ScrcpyControlMessageWriter.WriteInjectGamepadState(packet, state);
            EnqueueControl(packet);
        };
    }

    private void EnqueueControl(byte[] packet) => _controlOutbound?.Writer.TryWrite(packet);

    private void SendRestoreStartApp()
    {
        if (_isDeviceAudio)
        {
            byte[] p = new byte[2];
            ScrcpyControlMessageWriter.WriteSetDisplayPower(p, true);
            EnqueueControl(p);
        }
        else if (!string.IsNullOrWhiteSpace(_package))
        {
            EnqueueControl(ScrcpyControlMessageWriter.BuildStartAppPacket(_package));
        }
    }

    private async Task RunSessionLoopsAsync(VideoStreamSession session, CancellationToken ct)
    {
        Task videoTask = RunVideoLoopAsync(session.VideoStream, ct);
        Task controlSendTask = session.ControlStream != null
            ? RunControlSendLoopAsync(session.ControlStream, ct)
            : Task.CompletedTask;
        Task controlDrainTask = session.ControlStream != null
            ? ScrcpyControlMessageWriter.DrainDeviceMessagesAsync(session.ControlStream, ct)
            : Task.CompletedTask;

        try
        {
            await Task.WhenAny(videoTask, _window!.Closed).ConfigureAwait(false);
            _sessionCts?.Cancel();
            await Task.WhenAll(
                SuppressCancel(videoTask),
                SuppressCancel(controlSendTask),
                SuppressCancel(controlDrainTask)).ConfigureAwait(false);

            if (!_ready.Task.IsCompleted)
            {
                _ready.TrySetException(ConnectionError("视频流未正常首帧输出即结束。", AndroidErrorCode.DisplayConnectionEnded));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ready.TrySetException(ex);
            throw;
        }
        finally
        {
            await TeardownAsync().ConfigureAwait(false);
        }
    }

    private async Task RunVideoLoopAsync(Stream videoStream, CancellationToken ct)
    {
        var merger = new ScrcpyPacketMerger();
        byte[] header12 = new byte[12];
        byte[] packetBuf = new byte[256 * 1024];
        int lastWidth = 0;
        int lastHeight = 0;

        void OnFrameSizeChanged(int width, int height)
        {
            if (width <= 0 || height <= 0 || (width == lastWidth && height == lastHeight)) return;
            lastWidth = width;
            lastHeight = height;
            _window?.UpdateContentSize(width, height);
            RememberDisplaySize(width, height);
        }

        void MarkFirstFrameIfNeeded()
        {
            if (!_firstFramePresented)
            {
                _firstFramePresented = true;
                _window?.EnsureVisible();
                if (_isDeviceAudio) SetState(VirtualDisplayState.Active);
                else if (_state == VirtualDisplayState.Starting) SetState(VirtualDisplayState.Active);
                _ready.TrySetResult();
            }
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await videoStream.ReadExactlyAsync(header12, ct).ConfigureAwait(false);
                var hdr = ScrcpyVideoPacketHeader.Parse(header12);
                if (packetBuf.Length < hdr.PayloadLength)
                    packetBuf = new byte[Math.Max(hdr.PayloadLength, packetBuf.Length * 2)];

                await videoStream.ReadExactlyAsync(packetBuf.AsMemory(0, hdr.PayloadLength), ct).ConfigureAwait(false);

                var merged = merger.Process(packetBuf.AsSpan(0, hdr.PayloadLength), hdr.IsConfig);
                if (merged.IsEmpty) continue;

                _decoder!.DecodePacket(
                    merged,
                    hdr.PtsUs,
                    (tex, slice, codedW, codedH, cropX, cropY, cropW, cropH, matrix) =>
                    {
                        OnFrameSizeChanged(cropW, cropH);
                        _renderer!.PresentHardwareTexture(tex, slice, codedW, codedH, cropX, cropY, cropW, cropH, matrix);
                        MarkFirstFrameIfNeeded();
                    },
                    (nv12, stride, codedH, cropX, cropY, cropW, cropH, matrix) =>
                    {
                        OnFrameSizeChanged(cropW, cropH);
                        _renderer!.PresentSoftwareNv12(nv12, stride, codedH, cropX, cropY, cropW, cropH, matrix);
                        MarkFirstFrameIfNeeded();
                    });
            }
        }
        catch (EndOfStreamException) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_ready.Task.IsCompleted) _ready.TrySetException(ex);
            else Trace.WriteLine($"Video loop ended: {ex.Message}");
        }
    }

    private void RememberDisplaySize(int width, int height)
    {
        if (!_isDeviceAudio && _device != null && _package != null
            && _window is { WindowHandle: not 0, IsMaximized: false }
            && _sessionCts?.IsCancellationRequested != true)
            _settings?.RememberDisplay(_device, _package, new AndroidVirtualDisplaySize(width, height));
    }

    private async Task RunControlSendLoopAsync(Stream controlStream, CancellationToken ct)
    {
        var reader = _controlOutbound!.Reader;
        try
        {
            await foreach (byte[] packet in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await controlStream.WriteAsync(packet, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private void OnServerLogLine(string line)
    {
        lock (_logs)
        {
            if (_logs.Count >= 40) _logs.Dequeue();
            _logs.Enqueue(line);
        }

        if (VirtualDisplayClient.TryReadState(line) is { } state && (!_isDeviceAudio || state != VirtualDisplayState.Active || _firstFramePresented))
            SetState(state);
    }

    private void SetState(VirtualDisplayState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    public void Activate() => _window?.Activate();

    public void Restore()
    {
        if (_window != null && _window.WindowHandle != 0)
            _window.PostRestoreSignal();
        else
            SendRestoreStartApp();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sessionCts?.Cancel();
        try { await _completion.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); } catch { }
        await TeardownAsync().ConfigureAwait(false);
        _sessionCts?.Dispose();
    }

    private readonly SemaphoreSlim _teardownLock = new(1, 1);

    private async Task TeardownAsync()
    {
        await _teardownLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _controlOutbound?.Writer.TryComplete();
            var session = Interlocked.Exchange(ref _session, null);
            if (session != null)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            var decoder = Interlocked.Exchange(ref _decoder, null);
            decoder?.Dispose();

            var renderer = Interlocked.Exchange(ref _renderer, null);
            renderer?.Dispose();

            var window = Interlocked.Exchange(ref _window, null);
            if (window != null)
            {
                try { await window.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
        finally
        {
            _teardownLock.Release();
        }
    }

    private IOException ConnectionError(string message, AndroidErrorCode code)
    {
        lock (_logs)
        {
            if (_logs.Count > 0) Trace.WriteLine(string.Join(" | ", _logs));
        }
        return AndroidError.WithCode(new IOException(message), code);
    }

    private static async Task SuppressCancel(Task t)
    {
        try { await t.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    internal static string BuildWindowAppUserModelId(string? deviceKey, string? package)
    {
        const string prefix = "SonicRoute.Display.";
        const int maxLen = 128;

        string raw = $"{prefix}{SanitizeAumidSegment(deviceKey, "device")}.{SanitizeAumidSegment(package, "app")}";
        if (raw.Length <= maxLen) return raw;

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{deviceKey}\n{package}"))).AsSpan(0, 16).ToString();
        int keep = maxLen - 1 - hash.Length;
        return $"{raw[..keep].TrimEnd('.')}.{hash}";
    }

    private static string SanitizeAumidSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var sb = new StringBuilder(value.Length);
        foreach (char ch in value.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')
                sb.Append(ch);
            else if (ch == ':')
                sb.Append('.');
            else
                sb.Append('_');
        }
        string cleaned = sb.ToString().Trim('.');
        return cleaned.Length > 0 ? cleaned : fallback;
    }
}

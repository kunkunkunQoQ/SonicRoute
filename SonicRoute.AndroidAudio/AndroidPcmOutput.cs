using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SonicRoute.AndroidAudio;

/// <summary>One owned phone playback connection, shared by Windows loopback and incoming-phone renderers.</summary>
internal sealed class AndroidAudioOutputConnection : IAsyncDisposable
{
    private readonly AdbClient _adb;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _switch = new(1, 1);
    private readonly object _control = new(), _routeGate = new();
    private readonly string _socket = "sonicroute_out_" + Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _verified;
    private IReadOnlyList<AndroidOutputSink> _outputs = Array.Empty<AndroidOutputSink>();
    private Process? _helper;
    private TcpClient? _tcp;
    private CancellationTokenRegistration _cancelSocket;
    private Task? _stdout, _stderr;
    private string? _pid, _port, _lastError;
    private string _target;
    private int _disposed;
    private readonly Action<AndroidDevice, IReadOnlyList<AndroidOutputSink>>? _onOutputs;
    private readonly Action<string, int, string>? _onRouted;
    private string Remote => "/data/local/tmp/" + _socket + ".jar";
    public AndroidDevice Device { get; private set; }
    public Stream Stream => _tcp!.GetStream();
    public Task<Exception> Completion => _ended.Task;
    public Task WaitForRoutingAsync(CancellationToken token) => _verified!.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
    public int RoutedType { get; private set; }
    internal int BufferFrames { get; private set; }
    internal int StartThresholdFrames { get; private set; }
    internal string? LastRouteReport { get; private set; }
    internal (double PolicyMs, double PreferredMs, double ReleaseMs) SwitchSetup { get; private set; }
    public event Action<Exception>? Failed;

    private AndroidAudioOutputConnection(AdbClient adb, AndroidDevice device, string target, CancellationToken token,
        Action<AndroidDevice, IReadOnlyList<AndroidOutputSink>>? onOutputs, Action<string, int, string>? onRouted)
    {
        _adb = adb; Device = device; _target = target;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _verified = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _onOutputs = onOutputs; _onRouted = onRouted;
    }

    public static async Task<AndroidAudioOutputConnection> OpenAsync(AdbClient adb, AndroidDevice device, string target,
        CancellationToken token, Action<AndroidDevice, IReadOnlyList<AndroidOutputSink>>? onOutputs = null,
        Action<string, int, string>? onRouted = null)
    {
        var output = new AndroidAudioOutputConnection(adb, device, target, token, onOutputs, onRouted);
        try { await output.OpenCoreAsync().ConfigureAwait(false); return output; }
        catch { await output.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task OpenCoreAsync()
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(30));
        var token = startup.Token;
        await _adb.PrepareRuntimeAsync("audio", token).ConfigureAwait(false);
        _adb.ValidateServer();
        string catalog = Path.Combine(_adb.RuntimeDirectory, "sonicroute-catalog.jar");
        if (Device.State != "device") await RefreshDeviceAsync(token).ConfigureAwait(false);
        try { await PushRuntimeAsync().ConfigureAwait(false); }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            await RefreshDeviceAsync(token).ConfigureAwait(false);
            await PushRuntimeAsync().ConfigureAwait(false);
        }
        Task<string> forwarding = _adb.RunAsync(token, "-s", Device.Serial, "forward", "tcp:0", "localabstract:" + _socket);
        try
        {
            _helper = _adb.StartWithInput("-s", Device.Serial, "shell",
                "echo SR_PCOUT_PID:$$; CLASSPATH=" + Remote + ":" + Remote + ".server exec app_process / org.sonicroute.android.AudioOutputBridge play "
                + _socket + " '" + _target.Replace("'", "'\\''", StringComparison.Ordinal) + "'");
            _stdout = ReadOutputAsync(); _stderr = ReadErrorAsync();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        }
        finally
        {
            // Join even if helper startup fails, so DisposeAsync owns the exact forward.
            _port = (await forwarding.ConfigureAwait(false)).Trim();
        }
        if (!int.TryParse(_port, out int port) || port is < 1 or > 65535) throw AndroidError.WithCode(new IOException("手机音频端口转发失败。"), AndroidErrorCode.AudioPortForwardFailed);
        _tcp = new TcpClient { NoDelay = true, SendBufferSize = 8192, SendTimeout = 5000 };
        _cancelSocket = _lifetime.Token.UnsafeRegister(static socket => ((TcpClient)socket!).Dispose(), _tcp);
        await _tcp.ConnectAsync("127.0.0.1", port, token).ConfigureAwait(false);
        // Feed real-time PCM before awaiting hardware acknowledgement; the helper
        // keeps an unverified output muted, preventing default-device leakage.

        Task PushRuntimeAsync() => Task.WhenAll(
            _adb.RunAsync(token, "-s", Device.Serial, "push", catalog, Remote),
            _adb.RunAsync(token, "-s", Device.Serial, "push", _adb.ServerFile, Remote + ".server"));
    }

    private async Task RefreshDeviceAsync(CancellationToken token)
    {
        string key = AndroidDeviceIdentity.Key(Device);
        Device = (await _adb.DevicesAsync(token).ConfigureAwait(false)).FirstOrDefault(d => d.State == "device"
            && string.Equals(AndroidDeviceIdentity.Key(d), key, StringComparison.OrdinalIgnoreCase))
            ?? throw AndroidError.WithCode(new IOException("手机未连接或离线。"), AndroidErrorCode.PhoneOffline);
    }

    public async Task SwitchTargetAsync(string target, CancellationToken token)
    {
        await _switch.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Task verified;
            lock (_routeGate)
            {
                ThrowIfEnded();
                if (string.Equals(_target, target, StringComparison.OrdinalIgnoreCase)) return;
                if (!_outputs.Any(s => string.Equals(s.Key, target, StringComparison.OrdinalIgnoreCase)))
                    throw AndroidError.WithCode(new IOException("所选手机输出设备未连接。"), AndroidErrorCode.SelectedPhoneOutputDisconnected);
                _target = target;
                verified = (_verified = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            SendControl("target " + target);
            await verified.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }
        catch (TimeoutException ex) { Fail(ex); throw; }
        finally { _switch.Release(); }
    }

    public void SetPaused(bool paused) => SendControl(paused ? "mute 1" : "mute 0");
    private void SendControl(string command)
    {
        lock (_control)
        {
            ThrowIfEnded();
            _helper!.StandardInput.WriteLine(command);
            _helper.StandardInput.Flush();
        }
    }
    private void ThrowIfEnded()
    {
        if (_ended.Task.IsCompletedSuccessfully) throw AndroidError.WithCode(new IOException("手机音频输出已停止。", _ended.Task.Result), AndroidErrorCode.PhoneAudioOutputStopped);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private async Task ReadOutputAsync()
    {
        try
        {
            while (await _helper!.StandardOutput.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("SR_PCOUT_PID:", StringComparison.Ordinal))
                {
                    string candidate = line[13..].Trim();
                    if (candidate.Length <= 10 && int.TryParse(candidate, out int pid) && pid > 0) _pid = candidate;
                }
                else if (line.StartsWith('{'))
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    if (root.TryGetProperty("switchSetup", out var setup))
                        SwitchSetup = (setup.GetProperty("policyMs").GetDouble(), setup.GetProperty("preferredMs").GetDouble(),
                            setup.GetProperty("releaseMs").GetDouble());
                    if (root.TryGetProperty("outputs", out var outputs))
                    {
                        var sinks = AndroidOutputSink.ParseList(outputs);
                        lock (_routeGate) _outputs = sinks;
                        try { _onOutputs?.Invoke(Device, sinks); } catch { }
                    }
                    if (root.TryGetProperty("routed", out var routed))
                    {
                        LastRouteReport = routed.GetRawText();
                        string key = routed.GetProperty("key").GetString() ?? "";
                        int type = routed.GetProperty("type").GetInt32();
                        string name = routed.GetProperty("name").GetString() ?? "";
                        bool applied = routed.TryGetProperty("preferredApplied", out var p) && p.ValueKind == JsonValueKind.True;
                        bool accepted;
                        lock (_routeGate)
                        {
                            accepted = applied && string.Equals(key, _target, StringComparison.OrdinalIgnoreCase);
                            if (accepted)
                            {
                                RoutedType = type;
                                if (routed.TryGetProperty("bufferFrames", out var buffer)) BufferFrames = buffer.GetInt32();
                                if (routed.TryGetProperty("startThresholdFrames", out var threshold)) StartThresholdFrames = threshold.GetInt32();
                                _verified?.TrySetResult();
                            }
                        }
                        if (accepted) try { _onRouted?.Invoke(key, type, name); } catch { }
                    }
                    if (root.TryGetProperty("listening", out var listening) && listening.ValueKind == JsonValueKind.True) _ready.TrySetResult();
                    if (root.TryGetProperty("error", out var error)) throw error.GetString() is { Length: > 0 } errText ? new IOException(errText) : AndroidError.WithCode(new IOException("手机音频输出失败。"), AndroidErrorCode.PhoneAudioOutputFailed);
                }
            }
            if (!_lifetime.IsCancellationRequested) Fail(_lastError != null ? new IOException(_lastError) : AndroidError.WithCode(new IOException("手机音频输出辅助进程已退出。"), AndroidErrorCode.PhoneAudioHelperExited));
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task ReadErrorAsync()
    {
        try
        {
            while (await _helper!.StandardError.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
                _lastError = line.Length > 512 ? line[..512] : line;
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) { }
    }

    private void Fail(Exception error)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_ended.TrySetResult(error)) return;
        _ready.TrySetException(error);
        lock (_routeGate) _verified?.TrySetException(error);
        // The Windows loopback owner observes Completion instead of the renderer-ready task.
        _ = _ready.Task.Exception; _ = _verified?.Task.Exception;
        try { _tcp?.Dispose(); } catch { }
        try { Failed?.Invoke(error); } catch { }
    }
    internal void Abort(Exception error) => Fail(error);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _cancelSocket.Dispose(); _tcp?.Dispose();
        bool exited = _helper == null;
        if (_helper != null)
        {
            lock (_control) try { _helper.StandardInput.WriteLine("quit"); _helper.StandardInput.Flush(); _helper.StandardInput.Close(); } catch { }
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _helper.WaitForExitAsync(closing.Token).ConfigureAwait(false); exited = true; } catch { }
        }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (!exited && _pid != null)
        {
            try
            {
                string cmdline = await _adb.RunAsync(cleanup.Token, "-s", Device.Serial, "shell", "cat", "/proc/" + _pid + "/cmdline").ConfigureAwait(false);
                if (cmdline.Contains(_socket, StringComparison.Ordinal) && cmdline.Contains("org.sonicroute.android.AudioOutputBridge", StringComparison.Ordinal))
                    await _adb.RunAsync(cleanup.Token, "-s", Device.Serial, "shell", "kill", "-TERM", _pid).ConfigureAwait(false);
            }
            catch (Exception ex) { Trace.WriteLine("Phone output cleanup: " + ex.Message); }
        }
        if (_helper != null)
        {
            try { if (!_helper.HasExited) _helper.Kill(); } catch { }
            if (_stdout != null) try { await _stdout.ConfigureAwait(false); } catch { }
            if (_stderr != null) try { await _stderr.ConfigureAwait(false); } catch { }
            _helper.Dispose();
        }
        if (int.TryParse(_port, out int port) && port > 0)
            try { await _adb.RunAsync(cleanup.Token, "-s", Device.Serial, "forward", "--remove", "tcp:" + port).ConfigureAwait(false); } catch { }
        try { await _adb.RunAsync(cleanup.Token, "-s", Device.Serial, "shell", "rm", "-f", Remote, Remote + ".server").ConfigureAwait(false); } catch { }
        _lifetime.Dispose();
    }
}

/// <summary>Consumes the same post-gain PCM as WASAPI; no Windows PID, extra decoder or idle timer.</summary>
internal sealed class AndroidPhonePcmPlayer : IPcmPlayer
{
    private readonly AndroidPcmSource _source;
    private readonly AndroidAudioOutputConnection _output;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _available = new(0, 1);
    private readonly object _gate = new();
    private readonly Task _pump;
    private bool _playing;
    private int _disposed;
    private long _bytes, _nonSilentBytes;
    private Exception? _failure;
    public Exception? Failure => Volatile.Read(ref _failure);
    public (long Bytes, long NonSilentBytes, int RoutedType) Diagnostics =>
        (Interlocked.Read(ref _bytes), Interlocked.Read(ref _nonSilentBytes), _output.RoutedType);
    public event Action<Exception>? Failed;

    public AndroidPhonePcmPlayer(AdbClient adb, AndroidDevice device, AndroidPcmSource source, string sinkKey, CancellationToken token)
    {
        _source = source;
        _output = AndroidAudioOutputConnection.OpenAsync(adb, device, sinkKey, token).GetAwaiter().GetResult();
        _output.Failed += OnFailure;
        _source.DataAvailable += Wake;
        _pump = Task.Run(PumpAsync);
    }
    public Task SwitchTargetAsync(string key, CancellationToken token) => _output.SwitchTargetAsync(key, token);
    public Task WaitForRoutingAsync(CancellationToken token) => _output.WaitForRoutingAsync(token);
    public void Play()
    {
        _output.SetPaused(false);
        lock (_gate) _playing = true;
        Wake();
    }
    public void Stop()
    {
        lock (_gate) _playing = false;
        _output.SetPaused(true);
    }
    private void Wake()
    {
        if (Volatile.Read(ref _disposed) != 0 || !Volatile.Read(ref _playing)) return;
        try { if (_available.CurrentCount == 0) _available.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }
    private async Task PumpAsync()
    {
        byte[] floats = new byte[1920 * sizeof(float)], pcm = new byte[1920 * sizeof(short)];
        try
        {
            while (true)
            {
                await _available.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    int samples;
                    lock (_gate)
                    {
                        samples = _playing ? Math.Min(1920, _source.BufferedSamples) : 0;
                        if (samples == 0) break;
                        _source.Read(floats, 0, samples * sizeof(float));
                    }
                    bool nonSilent = ConvertPcm(floats, pcm, samples);
                    await _output.Stream.WriteAsync(pcm.AsMemory(0, samples * sizeof(short)), _lifetime.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _bytes, samples * sizeof(short));
                    if (nonSilent) Interlocked.Add(ref _nonSilentBytes, samples * sizeof(short));
                }
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { OnFailure(ex); }
    }
    private static bool ConvertPcm(byte[] floats, byte[] pcm, int samples)
    {
        var source = MemoryMarshal.Cast<byte, float>(floats.AsSpan(0, samples * sizeof(float)));
        var target = MemoryMarshal.Cast<byte, short>(pcm.AsSpan(0, samples * sizeof(short)));
        bool nonSilent = false;
        for (int i = 0; i < samples; i++)
        {
            target[i] = float.IsFinite(source[i]) ? (short)Math.Clamp((int)(source[i] * 32768f), short.MinValue, short.MaxValue) : (short)0;
            nonSilent |= target[i] != 0;
        }
        return nonSilent;
    }
    private void OnFailure(Exception error)
    {
        Volatile.Write(ref _failure, error);
        _output.Abort(error);
        try { Failed?.Invoke(error); } catch { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_gate) _playing = false;
        _source.DataAvailable -= Wake;
        _output.Failed -= OnFailure;
        _lifetime.Cancel();
        _output.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _pump.GetAwaiter().GetResult();
        _available.Dispose(); _lifetime.Dispose();
    }
}

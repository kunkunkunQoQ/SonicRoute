using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace SonicRoute.AndroidAudio;

/// <summary>One owned phone playback connection, shared by Windows loopback and incoming-phone renderers.</summary>
internal sealed class AndroidAudioOutputConnection : IAsyncDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private readonly AdbClient _adb;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _switch = new(1, 1);
    private readonly object _control = new(), _routeGate = new();
    private readonly string _socket = "sonicroute_out_" + Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _verified;
    private IReadOnlyList<AndroidOutputSink> _outputs = Array.Empty<AndroidOutputSink>();
    private IAdbShellSession? _helper;
    private StreamWriter? _writer;
    private Stream? _pcmStream;
    private CancellationTokenRegistration _cancelSocket;
    private Task? _stdout, _stderr;
    private string? _pid, _lastError;
    private string _target;
    private int _disposed;
    private readonly Action<AndroidDevice, IReadOnlyList<AndroidOutputSink>>? _onOutputs;
    private readonly Action<string, int, string>? _onRouted;
    private string Remote => "/data/local/tmp/" + _socket + ".jar";
    public AndroidDevice Device { get; private set; }
    public Stream Stream => _pcmStream!;
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

    public static Task<AndroidAudioOutputConnection> OpenAsync(AdbClient adb, AndroidDevice device, string target,
        CancellationToken token, Action<AndroidDevice, IReadOnlyList<AndroidOutputSink>>? onOutputs = null,
        Action<string, int, string>? onRouted = null) => OpenTimedAsync(adb, device, target, token, onOutputs, onRouted, null);

    internal static async Task<AndroidAudioOutputConnection> OpenTimedAsync(AdbClient adb, AndroidDevice device, string target,
        CancellationToken token, Action<AndroidDevice, IReadOnlyList<AndroidOutputSink>>? onOutputs,
        Action<string, int, string>? onRouted, AndroidOutputSwitchTiming? timing)
    {
        var output = new AndroidAudioOutputConnection(adb, device, target, token, onOutputs, onRouted);
        try { await output.OpenCoreAsync(timing).ConfigureAwait(false); return output; }
        catch { await output.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task OpenCoreAsync(AndroidOutputSwitchTiming? timing)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(30));
        var token = startup.Token;
        await _adb.PrepareRuntimeAsync("audio", token).ConfigureAwait(false);
        _adb.ValidateCatalog();
        if (Device.State != "device") await RefreshDeviceAsync(token).ConfigureAwait(false);
        using (timing?.Measure("output_jar"))
        {
            try { await PushRuntimeAsync().ConfigureAwait(false); }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                await RefreshDeviceAsync(token).ConfigureAwait(false);
                await PushRuntimeAsync().ConfigureAwait(false);
            }
        }

        using (timing?.Measure("output_helper_ready"))
        {
            _helper = await _adb.OpenShellAsync(Device.CommandTarget,
                "echo SR_PCOUT_PID:$$; CLASSPATH=" + Remote + " exec app_process / org.sonicroute.android.AudioOutputBridge play "
                + _socket + " '" + _target.Replace("'", "'\\''", StringComparison.Ordinal) + "'",
                redirectInput: true, token).ConfigureAwait(false);
            _writer = new StreamWriter(_helper.InputStream, Utf8NoBom, bufferSize: 1024, leaveOpen: true) { NewLine = "\n" };
            _stdout = ReadOutputAsync();
            _stderr = ReadErrorAsync();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        }

        try
        {
            using (timing?.Measure("output_socket"))
                _pcmStream = await _adb.OpenLocalAbstractAsync(Device.CommandTarget, _socket, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            throw AndroidError.WithCode(new IOException("手机音频通道建立失败。", ex), AndroidErrorCode.AudioPortForwardFailed);
        }
        _cancelSocket = _lifetime.Token.UnsafeRegister(static stream => { try { ((Stream)stream!).Dispose(); } catch { } }, _pcmStream);
        // Feed real-time PCM before awaiting hardware acknowledgement; the helper
        // keeps an unverified output muted, preventing default-device leakage.

        Task PushRuntimeAsync() => _adb.RunAsync(token, "-s", Device.CommandTarget, "push", _adb.CatalogFile, Remote);
    }

    private async Task RefreshDeviceAsync(CancellationToken token)
    {
        // An admitted helper is bound to one transport generation. Recovery is
        // coordinated by the connection manager, never by searching raw ADB here.
        if (Device.ConnectionTarget != null) throw AndroidError.WithCode(new IOException("手机连接已改变，请刷新设备。"), AndroidErrorCode.PhoneOffline);
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
                if (!string.Equals(target, AndroidOutputSink.Current.Key, StringComparison.OrdinalIgnoreCase)
                    && !_outputs.Any(s => string.Equals(s.Key, target, StringComparison.OrdinalIgnoreCase)))
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
            _writer!.WriteLine(command);
            _writer.Flush();
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
            using var reader = new StreamReader(_helper!.OutputStream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 2048, leaveOpen: true);
            while (await reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
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
                        var parsed = AndroidOutputSink.ParseList(outputs);
                        lock (_routeGate) _outputs = parsed;
                        try { _onOutputs?.Invoke(Device, parsed); } catch { }
                    }
                    if (root.TryGetProperty("routed", out var routed) && routed.ValueKind == JsonValueKind.Object)
                    {
                        LastRouteReport = routed.GetRawText();
                        string key = routed.TryGetProperty("key", out var k) ? (k.GetString() ?? "") : "";
                        int type = routed.TryGetProperty("type", out var t) && t.TryGetInt32(out int typeVal) ? typeVal : 0;
                        string name = routed.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                        bool applied = routed.TryGetProperty("preferredApplied", out var p) && p.ValueKind == JsonValueKind.True;
                        bool accepted;
                        TaskCompletionSource? verified = null;
                        lock (_routeGate)
                        {
                            accepted = applied && string.Equals(key, _target, StringComparison.OrdinalIgnoreCase);
                            if (accepted)
                            {
                                RoutedType = type;
                                if (routed.TryGetProperty("bufferFrames", out var bf) && bf.TryGetInt32(out int bfVal)) BufferFrames = bfVal;
                                if (routed.TryGetProperty("startThresholdFrames", out var st) && st.TryGetInt32(out int stVal)) StartThresholdFrames = stVal;
                                verified = _verified;
                            }
                        }
                        verified?.TrySetResult();
                        if (accepted) try { _onRouted?.Invoke(key, type, name); } catch { }
                    }
                    if (root.TryGetProperty("listening", out var listening) && listening.ValueKind == JsonValueKind.True) _ready.TrySetResult();
                    if (root.TryGetProperty("error", out var error))
                        throw AndroidError.WithCode(new IOException(error.GetString() ?? "手机音频输出失败。"), AndroidErrorCode.PhoneAudioOutputFailed);
                }
            }
            Fail(AndroidError.WithCode(new EndOfStreamException(string.IsNullOrEmpty(_lastError) ? "手机音频辅助进程已退出。" : _lastError), AndroidErrorCode.PhoneAudioHelperExited));
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested) { Fail(ex); }
        catch (OperationCanceledException) { }
    }

    private async Task ReadErrorAsync()
    {
        try
        {
            using var reader = new StreamReader(_helper!.ErrorStream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            while (await reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
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
        try { _pcmStream?.Dispose(); } catch { }
        try { Failed?.Invoke(error); } catch { }
    }
    internal void Abort(Exception error) => Fail(error);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _cancelSocket.Dispose();
        if (_pcmStream != null) { try { await _pcmStream.DisposeAsync().ConfigureAwait(false); } catch { } }
        bool exited = _helper == null;
        if (_helper != null)
        {
            lock (_control) try { _writer?.WriteLine("quit"); _writer?.Flush(); } catch { }
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _helper.CloseInputAsync(closing.Token).ConfigureAwait(false); } catch { }
            try { await _helper.WaitForExitAsync(closing.Token).ConfigureAwait(false); exited = true; } catch { }
        }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (!exited && _pid != null)
        {
            try
            {
                string cmdline = await _adb.RunAsync(cleanup.Token, "-s", Device.CommandTarget, "shell", "cat", "/proc/" + _pid + "/cmdline").ConfigureAwait(false);
                if (cmdline.Contains(_socket, StringComparison.Ordinal) && cmdline.Contains("org.sonicroute.android.AudioOutputBridge", StringComparison.Ordinal))
                    await _adb.RunAsync(cleanup.Token, "-s", Device.CommandTarget, "shell", "kill", "-TERM", _pid).ConfigureAwait(false);
            }
            catch (Exception ex) { Trace.WriteLine("Phone output cleanup: " + ex.Message); }
        }
        if (_helper != null)
        {
            try { if (!_helper.HasExited) _helper.Kill(); } catch { }
            if (_stdout != null) try { await _stdout.ConfigureAwait(false); } catch { }
            if (_stderr != null) try { await _stderr.ConfigureAwait(false); } catch { }
            try { _writer?.Dispose(); } catch (Exception ex) { Trace.WriteLine("Phone output writer cleanup: " + ex.Message); }
            try { await _helper.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { Trace.WriteLine("Phone output session cleanup: " + ex.Message); }
        }
        try { await _adb.RunAsync(cleanup.Token, "-s", Device.CommandTarget, "shell", "rm", "-f", Remote).ConfigureAwait(false); } catch { }
        _lifetime.Dispose();
    }
}

/// <summary>Consumes the same post-gain PCM as WASAPI; no Windows PID, extra decoder or idle timer.</summary>
internal sealed class AndroidPhonePcmPlayer : IPcmPlayer
{
    private static readonly byte[] RouteSilence = new byte[960 * AndroidPcmSource.Channels * sizeof(short)];
    private readonly AndroidPcmSource _source;
    private readonly AndroidAudioOutputConnection _output;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _connectionLifetime;
    private readonly SemaphoreSlim _available = new(0, 1);
    private readonly object _gate = new();
    private readonly Task _pump;
    private bool _playing;
    private int _disposed;
    private long _bytes, _nonSilentBytes;
    private Exception? _failure;
    public Exception? Failure => Volatile.Read(ref _failure);
    internal AndroidDevice Device => _output.Device;
    public (long Bytes, long NonSilentBytes, int RoutedType) Diagnostics =>
        (Interlocked.Read(ref _bytes), Interlocked.Read(ref _nonSilentBytes), _output.RoutedType);
    public event Action<Exception>? Failed;

    public AndroidPhonePcmPlayer(AdbClient adb, AndroidDevice device, AndroidPcmSource source, string sinkKey, CancellationToken token,
        Action<AndroidDevice, IReadOnlyList<AndroidOutputSink>>? onOutputs = null, CancellationToken managementToken = default,
        AndroidOutputSwitchTiming? timing = null)
    {
        _source = source;
        _connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, managementToken);
        try
        {
            _output = AndroidAudioOutputConnection.OpenTimedAsync(adb, device, sinkKey, _connectionLifetime.Token, onOutputs, null, timing)
                .GetAwaiter().GetResult();
            _output.Failed += OnFailure;
            _source.DataAvailable += Wake;
            _pump = Task.Run(PumpAsync);
        }
        catch
        {
            _source.DataAvailable -= Wake;
            if (_output != null)
            {
                _output.Failed -= OnFailure;
                try { _output.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            }
            _available.Dispose(); _lifetime.Dispose(); _connectionLifetime.Dispose();
            throw;
        }
    }
    public Task SwitchTargetAsync(string key, CancellationToken token) => _output.SwitchTargetAsync(key, token);
    public Task WaitForRoutingAsync(CancellationToken token) => _output.WaitForRoutingAsync(token);

    internal async Task VerifyBeforePlaybackAsync(CancellationToken token)
    {
        // AudioTrack needs a first write before Android can report the routed device.
        // Prime it with 20 ms of silence while the previous renderer still owns the PCM queue.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await _output.Stream.WriteAsync(RouteSilence, deadline.Token).ConfigureAwait(false);
        await WaitForRoutingAsync(deadline.Token).ConfigureAwait(false);
    }

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
        // Send immediately when a frame arrives. After a slow wireless ACK, send the
        // queued frames together so one round trip can carry more than 20 ms of audio.
        const int maxSamples = AndroidPcmSource.SampleRate * AndroidPcmSource.Channels * 80 / 1000;
        byte[] floats = new byte[maxSamples * sizeof(float)], pcm = new byte[maxSamples * sizeof(short)];
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
                        samples = _playing ? Math.Min(maxSamples, _source.BufferedSamples) : 0;
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
    internal static bool ConvertPcm(byte[] floats, byte[] pcm, int samples)
    {
        var source = MemoryMarshal.Cast<byte, float>(floats.AsSpan(0, samples * sizeof(float)));
        var target = MemoryMarshal.Cast<byte, short>(pcm.AsSpan(0, samples * sizeof(short)));
        bool nonSilent = false;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int width = Vector<float>.Count;
            for (; i <= samples - width * 2; i += width * 2)
            {
                var low = ConvertPcmVector(new Vector<float>(source[i..]));
                var high = ConvertPcmVector(new Vector<float>(source[(i + width)..]));
                var packed = Vector.Narrow(low, high);
                packed.CopyTo(target[i..]);
                nonSilent |= !Vector.EqualsAll(packed, Vector<short>.Zero);
            }
        }
        for (; i < samples; i++)
        {
            target[i] = float.IsFinite(source[i])
                ? (short)(int)Math.Clamp(source[i] * 32768f, short.MinValue, short.MaxValue) : (short)0;
            nonSilent |= target[i] != 0;
        }
        return nonSilent;
    }
    private static Vector<int> ConvertPcmVector(Vector<float> samples)
    {
        var scaled = samples * new Vector<float>(32768f);
        scaled = Vector.Min(Vector.Max(scaled, new Vector<float>(short.MinValue)), new Vector<float>(short.MaxValue));
        // Match the scalar path: NaN and infinities are silence; finite overflow clips.
        var finite = Vector.LessThan(Vector.Abs(samples), new Vector<float>(float.PositiveInfinity));
        return Vector.ConvertToInt32(Vector.ConditionalSelect(finite, scaled, Vector<float>.Zero));
    }
    private void OnFailure(Exception error)
    {
        if (Interlocked.CompareExchange(ref _failure, error, null) != null) return;
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
        try { _output.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        finally
        {
            try { _pump.GetAwaiter().GetResult(); }
            finally { _available.Dispose(); _lifetime.Dispose(); _connectionLifetime.Dispose(); }
        }
    }
}

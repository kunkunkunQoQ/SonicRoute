namespace SonicRoute.AndroidAudio;

/// <summary>A distinct external source, never a fabricated Windows process/session ID.</summary>
public sealed class AndroidAudioSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _stateGate = new(), _logGate = new();
    private readonly Func<IAudioTransport> _transportFactory;
    private readonly Func<AndroidPcmSource, string?, IPcmPlayer> _playerFactory;
    private readonly Queue<string> _logs = new();
    private AndroidAudioState _state = new(CaptureState.Idle);
    private CancellationTokenSource? _run;
    private IAudioTransport? _transport;
    private IPcmPlayer? _player;
    private AndroidPcmSource? _pcm;
    private Task? _receiveTask;
    private int _generation;
    private bool _disposed;
    private Exception? _renderFailure;
    private bool _requestedPhoneAudio, _capturePhoneAudio;
    private CancellationToken _sourceLifetime;
    public event Action<AndroidAudioState>? Changed;
    public AndroidAudioState State => Volatile.Read(ref _state);
    public AndroidPcmSource? Pcm => Volatile.Read(ref _pcm);
    public long PacketsDecoded => _receiver?.PacketsDecoded ?? 0;
    public long TimestampDiscontinuities => _receiver?.TimestampDiscontinuities ?? 0;
    public (long Bytes, long NonSilentBytes, int RoutedType)? PhoneOutputDiagnostics =>
        _player is AndroidPhonePcmPlayer phone ? phone.Diagnostics : null;
    private ScrcpyOpusReceiver? _receiver;

    public AndroidAudioSession(AdbClient adb)
    {
        _transportFactory = () => new ScrcpyAdbTransport(adb, Log);
        _playerFactory = (source, id) =>
        {
            if (!AndroidOutputIdentity.IsPhoneOutput(id)) return new WasapiPcmPlayer(source, id);
            var device = State.Device ?? throw new InvalidOperationException("手机音频会话尚未启动。");
            string sink = GetSourcePhoneSink(device, id!);
            return new AndroidPhonePcmPlayer(adb, device, source, sink, _run?.Token ?? default);
        };
    }
    public AndroidAudioSession(Func<IAudioTransport> transportFactory, Func<AndroidPcmSource, string?, IPcmPlayer> playerFactory)
    { _transportFactory = transportFactory; _playerFactory = playerFactory; }

    public async Task StartAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio = true, CancellationToken token = default)
    {
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _requestedPhoneAudio = keepPhoneAudio;
            _sourceLifetime = token;
            await StartCoreAsync(device, app, keepPhoneAudio && !AndroidOutputIdentity.IsPhoneOutput(State.OutputId), token).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private async Task StartCoreAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, CancellationToken token)
    {
        if (State.State is CaptureState.Starting or CaptureState.Receiving
            && State.Device?.Serial == device.Serial && State.App?.Package == app.Package && State.App.Uid == app.Uid
            && _capturePhoneAudio == keepPhoneAudio) return;
        await StopCoreAsync().ConfigureAwait(false);
        _capturePhoneAudio = keepPhoneAudio;
        int generation = ++_generation;
        _run = CancellationTokenSource.CreateLinkedTokenSource(token);
        _renderFailure = null;
        var runToken = _run.Token;
        Update(s => s with { State = CaptureState.Starting, Device = device, App = app, Error = null });
        Log($"Start {app.Package}, UID {app.Uid}, phone audio {keepPhoneAudio}");
        Task<IPcmPlayer>? preparingOutput = null;
        try
        {
            _transport = _transportFactory();
            _pcm = new AndroidPcmSource { Volume = State.Muted ? 0 : State.Volume / 100f, SmartVolume = State.SmartVolume };
            Task<Stream> connecting = _transport.ConnectAsync(device, app, keepPhoneAudio, runToken);
            if (AndroidOutputIdentity.IsPhoneOutput(State.OutputId))
            {
                var pcm = _pcm;
                string outputId = State.OutputId!;
                // Capture and phone playback need separate helpers, not sequential startup.
                preparingOutput = Task.Run(() => _playerFactory(pcm, outputId), runToken);
            }
            Stream stream = await connecting.ConfigureAwait(false);
            _player = await (preparingOutput ?? Task.Run(() => _playerFactory(_pcm, State.OutputId), runToken)).ConfigureAwait(false);
            AttachOutputFailure(_player, generation);
            _player.Play();
            _receiver = new ScrcpyOpusReceiver();
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _receiveTask = _receiver.ReceiveAsync(stream, _pcm, () =>
            {
                if (generation != Volatile.Read(ref _generation)) return;
                Update(s => s with { State = CaptureState.Receiving });
                first.TrySetResult();
            }, runToken);
            _ = ObserveReceiveAsync(_receiveTask, generation);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            Task completed = await Task.WhenAny(first.Task, _receiveTask).WaitAsync(startup.Token).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            if (_player is AndroidPhonePcmPlayer phone) await phone.WaitForRoutingAsync(runToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _run.Cancel();
            if (preparingOutput != null && _player == null)
            {
                // A factory may finish during cancellation. Join it before disposing its PCM
                // or lifetime so the owned helper/socket cannot escape failed startup.
                try { _player = await preparingOutput.ConfigureAwait(false); } catch { }
            }
            await StopCoreAsync().ConfigureAwait(false);
            Update(s => s with { State = CaptureState.Failed, Device = device, App = app, Error = ex.Message });
            Log("Start failed: " + ex.Message);
            throw;
        }
    }

    private async Task ObserveReceiveAsync(Task receive, int generation)
    {
        Exception? failure = null;
        try { await receive.ConfigureAwait(false); }
        catch (OperationCanceledException) { failure = Volatile.Read(ref _renderFailure); }
        catch (Exception ex) { failure = ex; }
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != _generation) return;
            var old = State;
            await StopCoreAsync().ConfigureAwait(false);
            Update(s => s with { State = CaptureState.Failed, Device = old.Device, App = old.App,
                Error = failure?.Message ?? "音频连接已停止，请检查手机连接后重新拖入。" });
            Log("Receive ended: " + State.Error);
        }
        finally { _operations.Release(); }
    }

    public async Task StopAsync()
    {
        try { _run?.Cancel(); } catch (ObjectDisposedException) { }
        await _operations.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _operations.Release(); }
    }
    private async Task StopCoreAsync()
    {
        ++_generation;
        _run?.Cancel();
        if (_transport != null) Update(s => s with { State = CaptureState.Stopping });
        if (_receiveTask != null) { try { await _receiveTask.ConfigureAwait(false); } catch { } }
        if (_player != null) { try { _player.Dispose(); } catch (Exception ex) { Log(ex.Message); } _player = null; }
        if (_transport != null)
        {
            try { await _transport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log("Transport cleanup: " + ex.Message); }
            finally { _transport = null; }
        }
        _pcm?.Clear(); _pcm = null;
        _run?.Dispose(); _run = null;
        _receiveTask = null; _receiver = null;
        Update(s => s with { State = CaptureState.Idle, Device = null, App = null, Error = null });
    }
    public void SetVolume(int percent)
    {
        int volume = Math.Clamp(percent, 0, 100);
        if (State.Volume == volume) return;
        ChangeVolume(volume);
    }
    public void SetMute(bool muted)
    {
        if (State.Muted == muted) return;
        ChangeMute(muted);
    }
    public void SetSmartVolume(bool enabled)
    {
        if (State.SmartVolume == enabled) return;
        ChangeSmartVolume(enabled);
    }
    // Keep the capturing delegates out of the no-change fast path.
    private void ChangeVolume(int volume) => Update(s => s with { Volume = volume });
    private void ChangeMute(bool muted) => Update(s => s with { Muted = muted });
    private void ChangeSmartVolume(bool enabled) => Update(s => s with { SmartVolume = enabled });
    public async Task SetOutputAsync(string? endpointId)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State.OutputId == endpointId) return;
            bool phoneOutput = AndroidOutputIdentity.IsPhoneOutput(endpointId);
            string? sink = phoneOutput && State.Device != null ? GetSourcePhoneSink(State.Device, endpointId!) : null;
            bool keepPhoneAudio = _requestedPhoneAudio && !phoneOutput;
            if (_pcm != null && _capturePhoneAudio != keepPhoneAudio)
            {
                // Disable native duplication before rendering back to a phone, otherwise
                // its default Bluetooth path would play an uncontrolled second copy.
                var old = State;
                Update(s => s with { OutputId = endpointId });
                try { await StartCoreAsync(old.Device!, old.App!, keepPhoneAudio, _sourceLifetime).ConfigureAwait(false); }
                catch { Update(s => s with { OutputId = old.OutputId }); throw; }
                return;
            }
            if (sink != null && _player is AndroidPhonePcmPlayer phone)
            {
                await phone.SwitchTargetAsync(sink, _run?.Token ?? default).ConfigureAwait(false);
                Update(s => s with { OutputId = endpointId });
                return;
            }
            if (_pcm != null)
            {
                IPcmPlayer next = await Task.Run(() => _playerFactory(_pcm, endpointId)).ConfigureAwait(false);
                try
                {
                    _player?.Stop(); next.Play();
                    if (next is AndroidPhonePcmPlayer output) await output.WaitForRoutingAsync(_run?.Token ?? default).ConfigureAwait(false);
                }
                catch { next.Dispose(); _player?.Play(); throw; }
                _player?.Dispose(); _player = next;
                AttachOutputFailure(next, _generation);
            }
            Update(s => s with { OutputId = endpointId });
        }
        finally { _operations.Release(); }
    }
    private static string GetSourcePhoneSink(AndroidDevice device, string endpointId)
    {
        if (!AndroidOutputIdentity.TryParseDeviceId(endpointId, out string phone, out string sink)
            || !string.Equals(phone, AndroidDeviceIdentity.Key(device), StringComparison.OrdinalIgnoreCase)
            || (sink != "speaker" && !sink.StartsWith("bt:", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("请选择此应用所在手机的输出设备。");
        return sink;
    }
    private AndroidAudioState Update(Func<AndroidAudioState, AndroidAudioState> change)
    {
        AndroidAudioState next;
        lock (_stateGate)
        {
            next = change(_state);
            if (next == _state) return _state;
            Volatile.Write(ref _state, next);
            if (_pcm is { } pcm)
            {
                pcm.SmartVolume = next.SmartVolume;
                pcm.Volume = next.Muted ? 0 : next.Volume / 100f;
            }
        }
        var listeners = Changed;
        if (listeners != null) foreach (Action<AndroidAudioState> listener in listeners.GetInvocationList())
            try { listener(next); } catch (Exception ex) { Log(ex.Message); }
        return next;
    }
    private void AttachOutputFailure(IPcmPlayer player, int generation)
    {
        void Failed(Exception error)
        {
            if (generation != Volatile.Read(ref _generation) || !ReferenceEquals(_player, player)) return;
            Volatile.Write(ref _renderFailure, error); Log("Audio output: " + error.Message);
            try { _run?.Cancel(); } catch (ObjectDisposedException) { }
        }
        if (player is WasapiPcmPlayer wasapi) wasapi.Failed += Failed;
        else if (player is AndroidPhonePcmPlayer phone)
        {
            phone.Failed += Failed;
            if (phone.Failure is { } error) Failed(error);
        }
    }
    public void Log(string line)
    {
        lock (_logGate)
        {
            while (_logs.Count >= 200) _logs.Dequeue();
            _logs.Enqueue(DateTimeOffset.Now.ToString("HH:mm:ss ") + line);
        }
    }
    public string GetLog() { lock (_logGate) return string.Join(Environment.NewLine, _logs); }
    public async ValueTask DisposeAsync() { _disposed = true; await StopAsync().ConfigureAwait(false); }
}

using System.Diagnostics;

namespace SonicRoute.AndroidAudio;

/// <summary>A distinct external source, never a fabricated Windows process/session ID.</summary>
public sealed class AndroidAudioSession : IAsyncDisposable, SonicRoute.AirPlay.IPhoneAudioSession, IMultiOutputAudioSession
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _stateGate = new(), _logGate = new();
    private readonly Func<IAudioTransport> _transportFactory;
    private readonly Func<string, AndroidDevice?>? _resolvePhone;
    private readonly Func<AndroidDevice, AndroidPcmSource, string?, CancellationToken, AndroidOutputSwitchTiming?, IPcmPlayer> _playerFactory;
    private readonly Func<Action, IDisposable>? _watchDefaultOutput;
    private IDisposable? _defaultOutputWatch;
    private int _defaultOutputRevision, _defaultOutputRefreshQueued;
    private readonly Queue<string> _logs = new();
    private AndroidAudioState _state = new(CaptureState.Idle);
    private volatile bool _hasSmartVolumePreference;
    private CancellationTokenSource? _run, _pendingRun;
    private IAudioTransport? _transport;
    internal Func<AndroidDevice, SrapSession>? AppSession { private get; set; }
    private SrapPcmCapture? _appCapture;
    private IPcmPlayer? _player;
    private AndroidPcmSource? _pcm;
    private readonly PcmOutputMirrors _mirrors;
    internal const string SourcePhoneOutputId = "@@ANDROID_NATIVE_OUTPUT@@";
    internal bool UsesSourcePhonePlayback => _capturePhoneAudio && State.State == CaptureState.Receiving;
    // Mode switches keep the same output lifetime while the capture passes through Idle/Starting again.
    internal bool CanChangeActiveOutputs => Volatile.Read(ref _outputRun) is { } run
        ? !run.IsCancellationRequested : State.State == CaptureState.Receiving;
    private readonly List<string?[]> _outputHistory = [];
    private CancellationTokenSource? _outputRun;
    private string[] _extraIds = [];
    private Task? _receiveTask;
    private int _generation;
    private bool _disposed;
    private Exception? _renderFailure;
    private bool _requestedPhoneAudio, _capturePhoneAudio, _initialKeepPhoneAudio, _pendingNativeOutput;
    private CancellationToken _sourceLifetime;
    public event Action<AndroidAudioState>? Changed;
    internal event Action<string?[]?>? OutputSelectionCommitted;
    internal event Action? PlaybackSelectionCommitted;
    public AndroidAudioState State => Volatile.Read(ref _state);
    public AndroidPcmSource? Pcm => Volatile.Read(ref _pcm);
    public long PacketsDecoded => _appCapture?.PacketsDecoded ?? _receiver?.PacketsDecoded ?? 0;
    public long TimestampDiscontinuities => _receiver?.TimestampDiscontinuities ?? 0;
    public (long Bytes, long NonSilentBytes, int RoutedType)? PhoneOutputDiagnostics =>
        _player is AndroidPhonePcmPlayer phone ? phone.Diagnostics : null;
    private IAndroidAudioReceiver? _receiver;
    private readonly Func<AndroidDevice, AndroidStreamSettings>? _streaming;

    public AndroidAudioSession(AdbClient adb, Func<AndroidDevice, AndroidStreamSettings>? streaming = null,
        Func<string, AndroidDevice?>? resolvePhone = null)
    {
        _mirrors = new(RemoveFailedOutputsAsync);
        _streaming = streaming;
        _resolvePhone = resolvePhone;
        _watchDefaultOutput = DefaultOutputNotifications.Subscribe;
        _transportFactory = () => new ScrcpyAdbTransport(adb, Log, streaming);
        _playerFactory = (device, source, id, token, timing) =>
        {
            return AndroidOutputIdentity.TryParseDeviceId(id, out _, out string sink)
                ? new AndroidPhonePcmPlayer(adb, device, source, sink, token, timing: timing)
                : new WasapiPcmPlayer(source, id);
        };
    }
    public AndroidAudioSession(Func<IAudioTransport> transportFactory, Func<AndroidPcmSource, string?, IPcmPlayer> playerFactory,
        Func<Action, IDisposable>? watchDefaultOutput = null, Func<AndroidDevice, AndroidStreamSettings>? streaming = null,
        Func<string, AndroidDevice?>? resolvePhone = null)
    {
        _mirrors = new(RemoveFailedOutputsAsync);
        _transportFactory = transportFactory;
        _playerFactory = (_, pcm, id, _, _) => playerFactory(pcm, id);
        _watchDefaultOutput = watchDefaultOutput;
        _streaming = streaming;
        _resolvePhone = resolvePhone;
    }

    internal void SetMirrorGainResolver(Func<AndroidDevice, AndroidApp, string, (int VolumePercent, bool Muted)?> resolve) =>
        _mirrors.InitialGain = id => State is { Device: { } device, App: { } app } ? resolve(device, app, id) : null;

    private IPcmPlayer CreatePlayer(AndroidDevice device, AndroidPcmSource pcm, string? id, CancellationToken token,
        AndroidOutputSwitchTiming? timing = null)
    {
        if (id == SourcePhoneOutputId) return NativePhonePlayback.Instance;
        using var phase = timing?.Measure("player_prepare");
        if (AndroidOutputIdentity.IsPhoneOutput(id)) device = ResolvePhoneOutput(device, id!).Device;
        return _playerFactory(device, pcm, id, token, timing);
    }

    private sealed class NativePhonePlayback : IPcmPlayer
    {
        internal static readonly NativePhonePlayback Instance = new();
        public void Play() { }
        public void Stop() { }
        public void Dispose() { }
    }

    public async Task StartAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio = true, CancellationToken token = default)
    {
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (device.IsApp) AppCapturePermission.Require(AppSession?.Invoke(device).Capabilities.Capture, app, keepPhoneAudio);
            _initialKeepPhoneAudio = keepPhoneAudio;
            bool nativeAlias = IsSourcePhoneCurrent(device, State.OutputId) || _extraIds.Any(id => IsSourcePhoneCurrent(device, id));
            if (nativeAlias)
            {
                string?[] renderers = new[] { State.OutputId }.Concat(_extraIds)
                    .Where(id => id != SourcePhoneOutputId && !IsSourcePhoneCurrent(device, id)).ToArray();
                var (primary, extras) = PcmOutputMirrors.Select(renderers.Length == 0 ? [SourcePhoneOutputId] : renderers, State.OutputId);
                Volatile.Write(ref _extraIds, extras);
                Update(s => s with { OutputId = primary });
            }
            _requestedPhoneAudio = nativeAlias || _pendingNativeOutput || State.OutputId == SourcePhoneOutputId || keepPhoneAudio;
            _pendingNativeOutput = false;
            _sourceLifetime = token;
            await StartCoreAsync(device, app, _requestedPhoneAudio && !IsSourcePhoneOutput(device, State.OutputId)
                && !_extraIds.Any(id => IsSourcePhoneOutput(device, id)), State.OutputId, token).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private async Task StartCoreAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, string? endpointId, CancellationToken token,
        bool warmPhoneRoute = false, bool preserveMirrors = false, AndroidOutputSwitchTiming? timing = null)
    {
        if (State.State is CaptureState.Starting or CaptureState.Receiving
            && State.Device?.Serial == device.Serial && State.App?.Package == app.Package && State.App.Uid == app.Uid
            && _capturePhoneAudio == keepPhoneAudio) return;
        var nextRun = CancellationTokenSource.CreateLinkedTokenSource(token);
        var runToken = nextRun.Token;
        Volatile.Write(ref _pendingRun, nextRun);
        Task<IPcmPlayer>? preparingOutput = null;
        AndroidPcmSource? warmPcm = null;
        var streaming = (_streaming?.Invoke(device) ?? new()).Normalize();
        try
        {
            if (warmPhoneRoute)
            {
                // Overlap route preparation with releasing the old duplicate capture.
                // The new PCM queue is empty until the old capture has been retired.
                warmPcm = new AndroidPcmSource(streaming.EffectiveAudioLatencyMode);
                preparingOutput = Task.Run(() => PreparePhoneOutputAsync(device, warmPcm, endpointId!, runToken, timing), runToken);
            }
            using (timing?.Measure("capture_retire")) await StopCoreAsync(preserveMirrors).ConfigureAwait(false);
            _outputRun ??= CancellationTokenSource.CreateLinkedTokenSource(token);
            _run = nextRun;
            Volatile.Write(ref _pendingRun, null);
            _capturePhoneAudio = keepPhoneAudio;
            int generation = ++_generation;
            _renderFailure = null;
            runToken.ThrowIfCancellationRequested();
            Update(s => s with { State = CaptureState.Starting, Device = device, App = app,
                SmartVolume = _hasSmartVolumePreference ? s.SmartVolume : !device.IsApp, Error = null, ErrorCode = null });
            if (device.IsApp)
            {
                var session = AppSession?.Invoke(device) ?? throw new SrapException("unsupported");
                AppCapturePermission.Require(session.Capabilities.Capture, app, keepPhoneAudio);
                _appCapture = new SrapPcmCapture(session, app);
                Log($"Start {app.Package}, playback capture, {SrapSession.PcmEncoding}, latency {streaming.AudioLatencyMode}");
            }
            else
            {
                Log($"Start {app.Package}, UID {app.Uid}, phone audio {keepPhoneAudio}, codec {streaming.AudioCodec}, latency {streaming.AudioLatencyMode}");
                _transport = _transportFactory();
            }
            lock (_stateGate)
            {
                _pcm = warmPcm ?? new AndroidPcmSource(streaming.EffectiveAudioLatencyMode);
                _pcm.LatencyMode = streaming.EffectiveAudioLatencyMode;
                _pcm.Volume = _state.Muted ? 0 : _state.Volume / 100f;
                _pcm.SmartVolume = _state.SmartVolume;
                _mirrors.SetLevels(_pcm.Volume, _pcm.SmartVolume);
            }
            _mirrors.SetLatencyMode(streaming.EffectiveAudioLatencyMode);
            if (string.IsNullOrEmpty(endpointId)) WatchDefaultOutput();
            Task<Stream>? connecting = _transport == null ? null : timing == null ? _transport.ConnectAsync(device, app, keepPhoneAudio, runToken)
                : ConnectTimedAsync(_transport, device, app, keepPhoneAudio, runToken, timing);
            if (preparingOutput == null && AndroidOutputIdentity.IsPhoneOutput(endpointId))
            {
                var pcm = _pcm;
                // Capture and phone playback need separate helpers, not sequential startup.
                preparingOutput = Task.Run(() => CreatePlayer(device, pcm, endpointId, runToken, timing), runToken);
            }
            Stream? stream = connecting == null ? null : await connecting.ConfigureAwait(false);
            _player = await (preparingOutput ?? Task.Run(() => CreatePlayer(device, _pcm, endpointId, runToken, timing), runToken)).ConfigureAwait(false);
            AttachOutputFailure(_player, generation);
            _player.Play();
            _mirrors.SetPrimary(_player);
            if (!preserveMirrors)
            {
                if (_extraIds.Length != 0)
                    await _mirrors.ApplyAsync(_extraIds, (pcm, id, ct) => CreatePlayer(device, pcm, id, ct),
                        static () => Task.CompletedTask, _outputRun.Token).ConfigureAwait(false);
                _pcm.Mirrors = _mirrors;
            }
            using (timing?.Measure("capture_first_pcm"))
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                startup.CancelAfter(TimeSpan.FromSeconds(15));
                var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_appCapture != null)
                    _receiveTask = _appCapture.ReceiveAsync(_pcm, () => first.TrySetResult(), runToken);
                else
                {
                    uint codecId = await ScrcpyAudioProtocol.ReadCodecIdAsync(stream!, startup.Token).ConfigureAwait(false);
                    _receiver = ScrcpyAudioProtocol.CreateReceiver(codecId, streaming.AudioDecoder);
                    _receiveTask = _receiver.ReceivePacketsAsync(stream!, _pcm, () => first.TrySetResult(), runToken);
                }
                _ = ObserveReceiveAsync(_receiveTask, generation);
                Task completed = await Task.WhenAny(first.Task, _receiveTask).WaitAsync(startup.Token).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
            if (_player is AndroidPhonePcmPlayer phone)
                using (timing?.Measure("phone_route")) await phone.WaitForRoutingAsync(runToken).ConfigureAwait(false);
            runToken.ThrowIfCancellationRequested();
            using (timing?.Measure("endpoint_commit"))
                Update(s => s with { State = CaptureState.Receiving, OutputId = endpointId });
        }
        catch (Exception ex)
        {
            nextRun.Cancel();
            if (preparingOutput != null && _player == null)
            {
                // A factory may finish during cancellation. Join it before disposing its PCM
                // or lifetime so the owned helper/socket cannot escape failed startup.
                try { (await preparingOutput.ConfigureAwait(false)).Dispose(); } catch { }
            }
            if (ReferenceEquals(_run, nextRun)) await StopCoreAsync(preserveMirrors).ConfigureAwait(false);
            else nextRun.Dispose();
            Update(s => s with { State = CaptureState.Failed, Device = device, App = app, Error = ex.Message, ErrorCode = AndroidError.TryGetCode(ex, out var code) ? code : null });
            Log("Start failed: " + ex.Message);
            throw;
        }
        finally { Volatile.Write(ref _pendingRun, null); }
    }

    private static async Task<Stream> ConnectTimedAsync(IAudioTransport transport, AndroidDevice device, AndroidApp app,
        bool keepPhoneAudio, CancellationToken token, AndroidOutputSwitchTiming timing)
    {
        using var phase = timing.Measure("capture_connect");
        return transport is ScrcpyAdbTransport scrcpy
            ? await scrcpy.ConnectAsync(device, app, keepPhoneAudio, token, timing).ConfigureAwait(false)
            : await transport.ConnectAsync(device, app, keepPhoneAudio, token).ConfigureAwait(false);
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
                Error = failure?.Message ?? "音频连接已停止，请检查手机连接后重新拖入。",
                ErrorCode = failure == null ? AndroidErrorCode.AudioConnectionStopped : AndroidError.TryGetCode(failure, out var code) ? code : null });
            Log("Receive ended: " + State.Error);
        }
        finally { _operations.Release(); }
    }

    public async Task StopAsync()
    {
        try { _outputRun?.Cancel(); } catch (ObjectDisposedException) { }
        try { Volatile.Read(ref _pendingRun)?.Cancel(); } catch (ObjectDisposedException) { }
        try { _run?.Cancel(); } catch (ObjectDisposedException) { }
        await _operations.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _operations.Release(); }
    }
    private async Task StopCoreAsync(bool preserveMirrors = false)
    {
        ++_generation;
        _run?.Cancel();
        if (!preserveMirrors)
        {
            _outputRun?.Cancel();
            _mirrors.Clear();
            _outputRun?.Dispose(); _outputRun = null;
        }
        StopWatchingDefaultOutput();
        if (_transport != null || _appCapture != null) Update(s => s with { State = CaptureState.Stopping });
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
        _receiveTask = null; _receiver = null; _appCapture = null;
        Update(s => s with { State = CaptureState.Idle, Device = null, App = null, Error = null, ErrorCode = null });
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
        if (_hasSmartVolumePreference && State.SmartVolume == enabled) return;
        ChangeSmartVolume(enabled);
    }
    // Keep the capturing delegates out of the no-change fast path.
    private void ChangeVolume(int volume) => Update(s => s with { Volume = volume });
    private void ChangeMute(bool muted) => Update(s => s with { Muted = muted });
    private void ChangeSmartVolume(bool enabled) => Update(s =>
    {
        _hasSmartVolumePreference = true;
        return s with { SmartVolume = enabled };
    });
    string?[] IMultiOutputAudioSession.GetOutputs() => [State.OutputId, .. Volatile.Read(ref _extraIds)];
    Task IMultiOutputAudioSession.SetOutputsAsync(IReadOnlyList<string?> outputs) => ApplyOutputsAsync(outputs.ToArray(), true);
    (int VolumePercent, bool Muted) IMultiOutputAudioSession.GetMirrorGain(string id) => _mirrors.GetGain(id);
    (int VolumePercent, bool Muted) IMultiOutputAudioSession.SetMirrorGain(string id, int? volumePercent, bool? muted, bool toggleMute) =>
        _mirrors.SetGain(id, volumePercent, muted, toggleMute);
    public Task SetOutputAsync(string? endpointId) => SetOutputsAsync([endpointId]);

    private Task SetOutputsAsync(IReadOnlyList<string?> outputs) => ApplyOutputsAsync(outputs.ToArray());
    internal Task RestoreOutputsAsync(string?[] desired, Func<bool> stillSelected) => ApplyOutputsAsync(desired, true, stillSelected);
    internal Task SetActiveOutputsAsync(string?[] outputs, Func<bool> stillActive, AndroidOutputSwitchTiming? timing) =>
        ApplyOutputsAsync(outputs, true, activeSelection: stillActive, timing: timing);

    private async Task ApplyOutputsAsync(string?[] desired, bool explicitSelection = false, Func<bool>? restore = null,
        Func<bool>? activeSelection = null, AndroidOutputSwitchTiming? timing = null)
    {
        timing?.Mark("request_start");
        using (timing?.Measure("session_queue")) await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (activeSelection != null && (!activeSelection() || !CanChangeActiveOutputs || State.State != CaptureState.Receiving))
                throw new OperationCanceledException();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (restore != null && !restore()) return;
            var previous = CurrentSelection();
            timing?.SetFrom(previous);
            using (timing?.Measure("outputs_apply"))
                await ApplyOutputsLockedAsync(desired, explicitSelection, timing).ConfigureAwait(false);
            using var commit = timing?.Measure("state_commit");
            if (restore == null && State.State == CaptureState.Receiving)
            {
                OutputSelectionCommitted?.Invoke(explicitSelection && desired.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                    ? CurrentSelection() : null);
                PlaybackSelectionCommitted?.Invoke();
            }
            if (State.State == CaptureState.Receiving && !previous.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(CurrentSelection()))
            {
                _outputHistory.Insert(0, previous);
                if (_outputHistory.Count > 8) _outputHistory.RemoveAt(8);
            }
        }
        finally
        {
            if (_pcm != null) _pcm.Mirrors = _mirrors;
            _operations.Release();
        }
    }

    private async Task ApplyOutputsLockedAsync(string?[] desired, bool explicitSelection, AndroidOutputSwitchTiming? timing = null)
    {
        var device = State.Device;
        if (device != null)
            for (int i = 0; i < desired.Length; i++)
                if (IsSourcePhoneCurrent(device, desired[i])) desired[i] = SourcePhoneOutputId;
        bool native = desired.Contains(SourcePhoneOutputId, StringComparer.OrdinalIgnoreCase);
        if (device?.IsApp == true && explicitSelection && !native) throw new SrapException("unsupported");
        var renderers = desired.Where(id => id != SourcePhoneOutputId).ToArray();
        if (renderers.Length == 0 && native) renderers = [SourcePhoneOutputId];
        var (primary, extras) = PcmOutputMirrors.Select(renderers, State.OutputId);
        bool oldNative = _requestedPhoneAudio;
        bool explicitMode = explicitSelection && _pcm != null || native;
        if (device?.IsApp == true) _requestedPhoneAudio = true;
        else if (explicitMode)
        {
            _requestedPhoneAudio = native;
        }
        else if (_pcm != null) _requestedPhoneAudio = _initialKeepPhoneAudio;
        try
        {
            // Enter the operation queue in call order, then open additional devices away from the UI thread.
            if (extras.Length != 0 && SynchronizationContext.Current != null)
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            bool extraSourcePhone = device != null && extras.Any(id => IsSourcePhoneOutput(device, id));
            if (device != null)
                foreach (var id in extras.Where(AndroidOutputIdentity.IsPhoneOutput)) ResolvePhoneOutput(device, id);
            if (device != null && _pcm != null && _outputRun != null)
                await _mirrors.ApplyAsync(extras, (pcm, id, ct) => CreatePlayer(device, pcm, id, ct, timing),
                    () => ChangePrimaryLockedAsync(primary, extraSourcePhone, timing), _outputRun.Token).ConfigureAwait(false);
            else await ChangePrimaryLockedAsync(primary, extraSourcePhone, timing).ConfigureAwait(false);
            bool changed = !extras.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_extraIds);
            Volatile.Write(ref _extraIds, extras);
            if (_pcm == null) _pendingNativeOutput = native;
            if (changed) Notify(State);
        }
        catch
        {
            _requestedPhoneAudio = oldNative;
            throw;
        }
    }

    internal string?[] CurrentSelection()
    {
        string?[] outputs = [State.OutputId, .. Volatile.Read(ref _extraIds)];
        return UsesSourcePhonePlayback && !outputs.Contains(SourcePhoneOutputId) ? [.. outputs, SourcePhoneOutputId] : outputs;
    }

    internal async Task<bool> IsFailedAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try { return State.State == CaptureState.Failed; }
        finally { _operations.Release(); }
    }

    private async Task RecoverExternalOutputAsync(IPcmPlayer failed, int generation, Exception reason)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || generation != _generation || !ReferenceEquals(_player, failed)) return;
            await RecoverOutputLockedAsync(State.OutputId, reason).ConfigureAwait(false);
        }
        finally
        {
            if (_pcm != null) _pcm.Mirrors = _mirrors;
            _operations.Release();
        }
    }

    internal async Task PhoneRemovedAsync(AndroidDevice device)
    {
        string lost = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), AndroidOutputSink.Current.Key);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            for (int i = _outputHistory.Count - 1; i >= 0; --i)
            {
                var choice = _outputHistory[i];
                if (!choice.Contains(lost, StringComparer.OrdinalIgnoreCase)) continue;
                var remainingChoice = choice.Where(id => !string.Equals(id, lost, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (remainingChoice.Length == 0) _outputHistory.RemoveAt(i);
                else _outputHistory[i] = remainingChoice;
            }
            if (!CurrentSelection().Contains(lost, StringComparer.OrdinalIgnoreCase)) return;
            await RecoverOutputLockedAsync(lost, new IOException("目标手机已移除。"), fallbackToDefault: true).ConfigureAwait(false);
            var remaining = CurrentSelection();
            OutputSelectionCommitted?.Invoke(remaining.Length > 1 && !remaining.Contains(lost, StringComparer.OrdinalIgnoreCase) ? remaining : null);
        }
        finally
        {
            if (_pcm != null) _pcm.Mirrors = _mirrors;
            _operations.Release();
        }
    }

    private async Task RecoverOutputLockedAsync(string? lost, Exception reason, bool fallbackToDefault = false)
    {
        bool Keep(string? id) => !string.Equals(id, lost, StringComparison.OrdinalIgnoreCase);
        var remaining = CurrentSelection().Where(Keep).ToArray();
        IEnumerable<string?[]> choices = remaining.Length != 0 ? new[] { remaining } : _outputHistory;
        if (fallbackToDefault) choices = choices.Append([null]);
        foreach (var choice in choices)
        {
            var usable = choice.Where(Keep).ToArray();
            if (usable.Length == 0) continue;
            try
            {
                await ApplyOutputsLockedAsync(usable, true).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (!_sourceLifetime.IsCancellationRequested) { Log("Output recovery: " + ex.Message); }
        }
        Volatile.Write(ref _renderFailure, reason);
        _run?.Cancel();
    }

    private async Task RemoveFailedOutputsAsync(Predicate<IPcmPlayer> match)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_mirrors.Remove(match)) return;
            var extras = _mirrors.IdsSnapshot();
            Volatile.Write(ref _extraIds, extras);
            if (_pcm != null && !_disposed && State.Device is { } device)
                await ChangePrimaryLockedAsync(State.OutputId, extras.Any(id => IsSourcePhoneOutput(device, id))).ConfigureAwait(false);
            Notify(State);
        }
        catch (Exception ex) { Log("Mirror output recovery: " + ex.Message); }
        finally
        {
            if (_pcm != null) _pcm.Mirrors = _mirrors;
            _operations.Release();
        }
    }

    private async Task ChangePrimaryLockedAsync(string? endpointId, bool extraSourcePhone, AndroidOutputSwitchTiming? timing = null)
    {
        var source = State.Device;
        (AndroidDevice Device, string Sink)? target = source != null && AndroidOutputIdentity.IsPhoneOutput(endpointId)
            ? ResolvePhoneOutput(source, endpointId!) : null;
        bool keepPhoneAudio = source?.IsApp == true || _requestedPhoneAudio && !extraSourcePhone && !AndroidDeviceIdentity.Matches(source, target?.Device);
        if (_pcm != null && _capturePhoneAudio != keepPhoneAudio)
        {
            // Keep capture exclusive when selecting a phone sink, so there is
            // no uncontrolled second copy on the phone's default output.
            var old = State;
            bool previousMode = _capturePhoneAudio;
            try { await StartCoreAsync(old.Device!, old.App!, keepPhoneAudio, endpointId, _sourceLifetime,
                warmPhoneRoute: target.HasValue, preserveMirrors: true, timing: timing).ConfigureAwait(false); }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException && !_sourceLifetime.IsCancellationRequested)
                {
                    try
                    {
                        using var recoveryPhase = timing?.Measure("capture_recover");
                        await StartCoreAsync(old.Device!, old.App!, previousMode, old.OutputId, _sourceLifetime, preserveMirrors: true).ConfigureAwait(false);
                    }
                    catch (Exception recovery) { Log("Previous capture recovery: " + recovery.Message); }
                }
                if (_pcm == null)
                {
                    var stopped = State;
                    await StopCoreAsync().ConfigureAwait(false);
                    Update(_ => stopped);
                }
                throw;
            }
            return;
        }
        if (string.Equals(State.OutputId, endpointId, StringComparison.OrdinalIgnoreCase)) return;
        if (target is { } output && _player is AndroidPhonePcmPlayer { Failure: null } phone && AndroidDeviceIdentity.Matches(output.Device, phone.Device))
        {
            var token = _run?.Token ?? default;
            using (timing?.Measure("phone_route")) await phone.SwitchTargetAsync(output.Sink, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            using (timing?.Measure("endpoint_commit")) Update(s => s with { OutputId = endpointId });
            return;
        }
        if (_pcm != null)
        {
            bool watching = _defaultOutputWatch != null;
            if (string.IsNullOrEmpty(endpointId)) WatchDefaultOutput();
            try
            {
                await ReplaceOutputAsync(endpointId, timing).ConfigureAwait(false);
            }
            catch { if (!watching) StopWatchingDefaultOutput(); throw; }
        }
        using (timing?.Measure("endpoint_commit")) Update(s => s with { OutputId = endpointId });
        if (!string.IsNullOrEmpty(endpointId)) StopWatchingDefaultOutput();
    }

    private async Task<IPcmPlayer> PreparePhoneOutputAsync(AndroidDevice device, AndroidPcmSource pcm, string endpointId, CancellationToken token,
        AndroidOutputSwitchTiming? timing)
    {
        IPcmPlayer player = CreatePlayer(device, pcm, endpointId, token, timing);
        try
        {
            if (player is AndroidPhonePcmPlayer phone)
                using (timing?.Measure("phone_route")) await phone.VerifyBeforePlaybackAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return player;
        }
        catch
        {
            try { player.Dispose(); } catch (Exception ex) { Log("Prepared output cleanup: " + ex.Message); }
            throw;
        }
    }

    private async Task ReplaceOutputAsync(string? endpointId, AndroidOutputSwitchTiming? timing = null)
    {
        var token = _run?.Token ?? default;
        IPcmPlayer next = await Task.Run(() => CreatePlayer(State.Device!, _pcm!, endpointId, token, timing), token).ConfigureAwait(false);
        bool switching = false;
        try
        {
            if (next is AndroidPhonePcmPlayer phone)
                using (timing?.Measure("phone_route")) await phone.VerifyBeforePlaybackAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            switching = true;
            _player?.Stop(); next.Play();
        }
        catch
        {
            try { next.Dispose(); } catch (Exception ex) { Log("Replacement output cleanup: " + ex.Message); }
            if (switching && !token.IsCancellationRequested)
            {
                try { _player?.Play(); } catch (Exception ex) { Log("Previous output recovery: " + ex.Message); }
            }
            throw;
        }
        var previous = _player;
        _player = next;
        _mirrors.SetPrimary(next);
        AttachOutputFailure(next, _generation);
        // The new renderer already owns playback. A driver failure while retiring
        // the old output must not orphan it or report a failed output selection.
        using (timing?.Measure("player_retire"))
            try { previous?.Dispose(); } catch (Exception ex) { Log("Previous output cleanup: " + ex.Message); }
    }

    private void WatchDefaultOutput()
    {
        if (_defaultOutputWatch != null || _watchDefaultOutput == null) return;
        try { _defaultOutputWatch = _watchDefaultOutput(DefaultOutputChanged); }
        catch (Exception ex) { Log("Default output notifications: " + ex.Message); }
    }

    private void StopWatchingDefaultOutput()
    {
        var watch = _defaultOutputWatch;
        _defaultOutputWatch = null;
        try { watch?.Dispose(); } catch (Exception ex) { Log("Default output notifications: " + ex.Message); }
    }

    private void DefaultOutputChanged()
    {
        Interlocked.Increment(ref _defaultOutputRevision);
        QueueDefaultOutputRefresh();
    }

    private void QueueDefaultOutputRefresh()
    {
        if (Interlocked.CompareExchange(ref _defaultOutputRefreshQueued, 1, 0) == 0)
            _ = Task.Run(RefreshDefaultOutputAsync);
    }

    private async Task RefreshDefaultOutputAsync()
    {
        int handled = 0;
        try
        {
            do
            {
                handled = Volatile.Read(ref _defaultOutputRevision);
                await _operations.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!_disposed && _defaultOutputWatch != null && _pcm != null && _player != null
                        && string.IsNullOrEmpty(State.OutputId) && _run?.IsCancellationRequested == false)
                    {
                        await ReplaceOutputAsync(null).ConfigureAwait(false);
                        Log("System output refreshed");
                    }
                }
                catch (Exception ex) { Log("Default output refresh: " + ex.Message); }
                finally { _operations.Release(); }
                // Only drain notifications received during the swap; no idle loop or timer.
            } while (handled != Volatile.Read(ref _defaultOutputRevision));
        }
        finally
        {
            Interlocked.Exchange(ref _defaultOutputRefreshQueued, 0);
            if (handled != Volatile.Read(ref _defaultOutputRevision)) QueueDefaultOutputRefresh();
        }
    }
    private static bool IsSourcePhoneOutput(AndroidDevice device, string? endpointId) =>
        AndroidOutputIdentity.TryParseDeviceId(endpointId, out string phone, out _)
        && string.Equals(phone, AndroidDeviceIdentity.Key(device), StringComparison.OrdinalIgnoreCase);

    private static bool IsSourcePhoneCurrent(AndroidDevice device, string? endpointId) =>
        AndroidOutputIdentity.IsPhoneCurrentOutput(endpointId) && IsSourcePhoneOutput(device, endpointId);

    private (AndroidDevice Device, string Sink) ResolvePhoneOutput(AndroidDevice source, string endpointId)
    {
        if (AndroidOutputIdentity.TryParseDeviceId(endpointId, out string phone, out string sink))
        {
            if (string.Equals(phone, AndroidDeviceIdentity.Key(source), StringComparison.OrdinalIgnoreCase)
                && (sink == "speaker" || sink.StartsWith("bt:", StringComparison.OrdinalIgnoreCase)))
            {
                var target = _resolvePhone == null ? source : _resolvePhone(phone);
                if (target is { State: "device" }) return (target, sink);
                throw AndroidError.WithCode(new IOException("目标手机未连接。"), AndroidErrorCode.PhoneOffline);
            }
            if (string.Equals(sink, AndroidOutputSink.Current.Key, StringComparison.OrdinalIgnoreCase))
            {
                var target = _resolvePhone?.Invoke(phone);
                if (target is { State: "device" } && string.Equals(phone, AndroidDeviceIdentity.Key(target), StringComparison.OrdinalIgnoreCase))
                    return (target, AndroidOutputSink.Current.Key);
                throw AndroidError.WithCode(new IOException("目标手机未连接。"), AndroidErrorCode.PhoneOffline);
            }
        }
        throw AndroidError.WithCode(new InvalidOperationException("请选择此应用所在手机的输出设备。"), AndroidErrorCode.SelectSourcePhoneOutput);
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
                _mirrors.SetLevels(pcm.Volume, pcm.SmartVolume);
            }
        }
        Notify(next);
        return next;
    }
    private void Notify(AndroidAudioState next)
    {
        var listeners = Changed;
        if (listeners != null) foreach (Action<AndroidAudioState> listener in listeners.GetInvocationList())
            try { listener(next); } catch (Exception ex) { Log(ex.Message); }
    }
    private void AttachOutputFailure(IPcmPlayer player, int generation)
    {
        void FailedExternal(Exception error) => _ = Task.Run(() => RecoverExternalOutputAsync(player, generation, error));
        void Failed(Exception error)
        {
            if (generation != Volatile.Read(ref _generation) || !ReferenceEquals(_player, player)) return;
            Volatile.Write(ref _renderFailure, error); Log("Audio output: " + error.Message);
            try { _run?.Cancel(); } catch (ObjectDisposedException) { }
        }
        if (player is WasapiPcmPlayer wasapi) wasapi.Failed += Failed;
        else if (player is AndroidPhonePcmPlayer phone)
        {
            phone.Failed += FailedExternal;
            if (phone.Failure is { } error) FailedExternal(error);
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

// One opt-in summary per user request. Overlapping stages are elapsed times, not additive.
internal sealed class AndroidOutputSwitchTiming
{
    private static readonly string? Destination = Environment.GetEnvironmentVariable("SONICROUTE_AUDIO_ROUTE_LOG");
    private static readonly object LogGate = new();
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, double> _stages = new(StringComparer.Ordinal);
    private string _from = "-";

    internal static AndroidOutputSwitchTiming? Create() => string.IsNullOrWhiteSpace(Destination) ? null : new();
    internal void Mark(string name) => Add(name, _start);
    internal Phase Measure(string name) => new(this, name, Stopwatch.GetTimestamp());
    internal void SetFrom(IEnumerable<string?> ids) => _from = Describe(ids);

    private void Add(string name, long started)
    {
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        lock (_stages) _stages[name] = _stages.GetValueOrDefault(name) + elapsed;
    }

    internal void Report(AndroidAudioSession session, string outcome, IEnumerable<string?> desired)
    {
        string stages;
        lock (_stages) stages = string.Join(' ', _stages.Select(stage => FormattableString.Invariant($"{stage.Key}={stage.Value:F2}")));
        string line = FormattableString.Invariant($"Audio route ({outcome}) {_from}->{Describe(desired)} ms total={Stopwatch.GetElapsedTime(_start).TotalMilliseconds:F2} {stages}");
        session.Log(line);
        Trace.WriteLine(line);
        if (Destination is { } path && Path.IsPathFullyQualified(path))
            _ = Task.Run(() =>
            {
                try { lock (LogGate) File.AppendAllText(path, line + Environment.NewLine); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { Trace.WriteLine("Audio route timing: " + ex.Message); }
            });
    }

    private static string Describe(IEnumerable<string?> ids) => string.Join('+', ids.Select(id => id switch
    {
        AndroidAudioSession.SourcePhoneOutputId => "native",
        _ when AndroidOutputIdentity.IsPhoneBluetoothOutput(id) => "bluetooth",
        _ when AndroidOutputIdentity.IsPhoneCurrentOutput(id) => "phone-current",
        _ when AndroidOutputIdentity.IsPhoneOutput(id) => "phone-speaker",
        _ => "pc"
    }));

    internal readonly struct Phase(AndroidOutputSwitchTiming owner, string name, long started) : IDisposable
    {
        public void Dispose() => owner.Add(name, started);
    }
}

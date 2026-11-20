using System.Buffers;
using System.Runtime.InteropServices;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;

namespace SonicRoute.WindowsLink;

internal static class LinkAudioCapture
{
    internal static Task CaptureAsync(int? processId, Action<ReadOnlyMemory<byte>> onPcm, Action onStarted, CancellationToken token) =>
        SharedProcessCapture.SubscribeAsync(processId, onPcm, onStarted, token);

    internal static async Task CaptureNativeAsync(int? processId, Action<ReadOnlyMemory<byte>> onPcm, Action onStarted, CancellationToken token)
    {
        using var destination = new PcmWriter(onPcm);
        await PcProcessLoopbackCapture.RunMtaCaptureLoopAsync(processId ?? Environment.ProcessId, destination, token,
            static () => { }, excludeProcess: processId == null, onStarted: onStarted).ConfigureAwait(false);
    }

    private sealed class PcmWriter(Action<ReadOnlyMemory<byte>> output) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => output(buffer.AsMemory(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

internal sealed record WindowsLinkAudioSource(
    Guid StreamId,
    string Id,
    string PeerId,
    string AppKey,
    string Name,
    byte[]? IconPng,
    LinkAudioPlayback Session)
{
    internal string? InstanceId { get; init; }
    internal bool IsRelayed { get; init; }
}

internal sealed partial class LinkAudioPlayback : IPhoneAudioSession, IMultiOutputAudioSession, IAsyncDisposable
{
    private readonly AndroidPcmSource _audio;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _routes = new(1, 1);
    private readonly Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer> _createPlayer;
    private readonly Func<Action, IDisposable> _watchDefault;
    private readonly CancellationToken _lifetime;
    private readonly Action _changed;
    private readonly Action<Exception?> _failed;
    internal Func<bool>? SourceAlreadyPlaying { get; init; }
    private IPcmPlayer? _player;
    private IDisposable? _defaultWatch;
    private AndroidAudioState _state;
    private string? _endpoint;
    private bool _disposed;
    private int _defaultQueued, _defaultVersion;

    internal LinkAudioPlayback(
        bool smartVolume,
        Action changed,
        Action<Exception?> failed,
        Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer>? createPlayer = null,
        Func<Action, IDisposable>? watchDefault = null,
        CancellationToken lifetime = default)
    {
        _mirrors = new PcmOutputMirrors(true, RemoveMirrorsAsync, static id => WindowsLinkOutputIdentity.TryParsePeerId(id, out _));
        _mirrors.SetLevels(1, smartVolume);
        _audio = new AndroidPcmSource { SmartVolume = smartVolume, PreserveQuietLevel = true, Mirrors = _mirrors };
        _state = new AndroidAudioState(CaptureState.Starting, SmartVolume: smartVolume);
        _changed = changed;
        _failed = failed;
        _createPlayer = createPlayer ?? ((pcm, endpoint, _) => new WasapiPcmPlayer(pcm, endpoint));
        _watchDefault = watchDefault ?? DefaultOutputNotifications.Subscribe;
        _lifetime = lifetime;
    }

    string?[] IMultiOutputAudioSession.GetOutputs() => GetOutputs();
    Task IMultiOutputAudioSession.SetOutputsAsync(IReadOnlyList<string?> outputs) => SetOutputsAsync(outputs);

    public AndroidAudioState State => Volatile.Read(ref _state);
    public AndroidPcmSource? Pcm => State.State == CaptureState.Receiving ? _audio : null;

    internal void Push(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty || _disposed) return;
        var data = MemoryMarshal.Cast<byte, short>(pcm);
        float[] scratch = ArrayPool<float>.Shared.Rent(data.Length);
        try
        {
            for (int i = 0; i < data.Length; i++) scratch[i] = data[i] * (1.0f / 32768f);
            var samples = scratch.AsSpan(0, data.Length);
            _audio.Write(samples);
        }
        finally { ArrayPool<float>.Shared.Return(scratch, clearArray: true); }
    }

    // A paused application can own an audio session without producing capture packets.
    internal void Start() => _ = Task.Run(EnsureInitialOutputAsync);

    private async Task EnsureInitialOutputAsync()
    {
        await _routes.WaitAsync().ConfigureAwait(false);
        try
        {
            string? endpoint;
            lock (_gate)
            {
                if (_disposed || _player != null) return;
                endpoint = _endpoint;
                if (endpoint == null) _defaultWatch ??= _watchDefault(DefaultChanged);
            }
            var player = _createPlayer(_audio, endpoint, _lifetime);
            try
            {
                if (player is AndroidPhonePcmPlayer phone)
                    await phone.VerifyBeforePlaybackAsync(_lifetime).ConfigureAwait(false);
                _lifetime.ThrowIfCancellationRequested();
                if (player is WasapiPcmPlayer wasapi)
                    await wasapi.PlayAndConfirmAsync(_lifetime).ConfigureAwait(false);
                else player.Play();
            }
            catch
            {
                DisposeQuietly(player);
                throw;
            }
            bool commit;
            lock (_gate)
            {
                commit = !_disposed && _player == null && string.Equals(_endpoint, endpoint, StringComparison.OrdinalIgnoreCase);
                if (commit)
                {
                    SetPlayer(player);
                    Volatile.Write(ref _state, _state with { State = CaptureState.Receiving });
                }
            }
            if (!commit)
            {
                DisposeQuietly(player);
                if (!_disposed && _player == null) _ = Task.Run(EnsureInitialOutputAsync);
                return;
            }
            _changed();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed) _failed(ex);
        }
        finally { _routes.Release(); }
    }

    public void SetVolume(int percent) => SetLevel(Math.Clamp(percent, 0, 100), null);

    public void SetMute(bool muted) => SetLevel(null, muted);

    internal void SetPlaybackLevel(float volume, bool muted) =>
        SetLevel(Math.Clamp((int)Math.Round(volume * 100f), 0, 100), muted);

    internal void SetSmartVolume(bool smartVolume)
    {
        _audio.SmartVolume = smartVolume;
        lock (_gate)
        {
            if (_disposed || _state.SmartVolume == smartVolume) return;
            _mirrors.SetLevels(_audio.Volume, smartVolume);
            Volatile.Write(ref _state, _state with { SmartVolume = smartVolume });
        }
        _changed();
    }

    private void SetLevel(int? volume, bool? muted)
    {
        lock (_gate)
        {
            if (_disposed || (volume ?? _state.Volume) == _state.Volume && (muted ?? _state.Muted) == _state.Muted) return;
            var next = _state with { Volume = volume ?? _state.Volume, Muted = muted ?? _state.Muted };
            _audio.Volume = next.Muted ? 0 : next.Volume / 100f;
            _mirrors.SetLevels(_audio.Volume, next.SmartVolume);
            Volatile.Write(ref _state, next);
        }
        _changed();
    }

    public Task SetOutputAsync(string? endpoint) => SetOutputsAsync([endpoint]);

    private async Task ChangeOutputAsync(string? endpoint, bool refreshDefault = false, Predicate<IPcmPlayer>? playerMatch = null)
    {
        await _routes.WaitAsync(_lifetime).ConfigureAwait(false);
        bool changed;
        try { changed = await ChangePrimaryLockedAsync(endpoint, refreshDefault, playerMatch).ConfigureAwait(false); }
        finally { _routes.Release(); }
        if (changed && !refreshDefault) _changed();
    }

    private async Task<bool> ChangePrimaryLockedAsync(string? endpoint, bool refreshDefault = false, Predicate<IPcmPlayer>? playerMatch = null, bool restorePrevious = true)
    {
        IPcmPlayer? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                if (refreshDefault || playerMatch != null) return false;
                throw new ObjectDisposedException(nameof(LinkAudioPlayback));
            }
            if (_player == null)
            {
                if (refreshDefault || playerMatch != null) return false;
                if (string.Equals(_endpoint, endpoint, StringComparison.OrdinalIgnoreCase)) return false;
                _endpoint = endpoint;
                if (endpoint != null) StopWatch();
                Volatile.Write(ref _state, _state with { OutputId = endpoint });
                return true;
            }
            if (refreshDefault ? _endpoint != null : string.Equals(endpoint, _endpoint, StringComparison.OrdinalIgnoreCase)) return false;
            if (playerMatch != null && !playerMatch(_player)) return false;
            previous = _player;
        }
        bool addedWatch = endpoint == null && _defaultWatch == null;
        if (addedWatch) _defaultWatch = _watchDefault(DefaultChanged);
        try { await ReplacePlayerAsync(previous, endpoint, restorePrevious).ConfigureAwait(false); }
        catch
        {
            if (addedWatch) StopWatch();
            throw;
        }
        lock (_gate)
        {
            _endpoint = endpoint;
            Volatile.Write(ref _state, _state with { OutputId = endpoint });
        }
        if (endpoint != null) StopWatch();
        return true;
    }

    private async Task ReplacePlayerAsync(IPcmPlayer previous, string? endpoint, bool restorePrevious)
    {
        var next = _createPlayer(_audio, endpoint, _lifetime);
        bool switching = false;
        try
        {
            if (next is AndroidPhonePcmPlayer phone)
                await phone.VerifyBeforePlaybackAsync(_lifetime).ConfigureAwait(false);
            _lifetime.ThrowIfCancellationRequested();
            switching = true;
            try { previous.Stop(); }
            catch (Exception ex) when (previous is AndroidPhonePcmPlayer)
            { System.Diagnostics.Trace.WriteLine("Windows Link previous phone output: " + ex.GetType().Name); }
            next.Play();
        }
        catch
        {
            DisposeQuietly(next);
            if (switching && restorePrevious && !_lifetime.IsCancellationRequested && previous is not AndroidPhonePcmPlayer { Failure: not null }
                && previous is not WindowsLinkService.RelayPlayer { Failure: not null })
            {
                try { previous.Play(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("Windows Link output recovery: " + ex.GetType().Name);
                    _failed(ex);
                }
            }
            throw;
        }
        SetPlayer(next);
        DisposeQuietly(previous);
    }

    private void SetPlayer(IPcmPlayer next)
    {
        Volatile.Write(ref _player, next);
        _mirrors.SetPrimary(next);
        if (next is IFailedPcmPlayer remote)
        {
            void Lost(Exception error) => _ = RecoverOutputAsync(p => ReferenceEquals(p, next), error: error);
            remote.Failed += Lost;
            if (next is WindowsLinkService.RelayPlayer { Failure: { } failure }) Lost(failure);
        }
        if (next is WasapiPcmPlayer wasapi)
        {
            wasapi.Failed += ex => _ = RecoverOutputAsync(p => ReferenceEquals(p, next), error: ex);
        }
        if (next is AndroidPhonePcmPlayer phone)
        {
            phone.Failed += _ => QueuePhoneFallback(phone);
            if (phone.Failure != null) QueuePhoneFallback(phone);
        }
    }

    private void QueuePhoneFallback(AndroidPhonePcmPlayer player)
    {
        if (ReferenceEquals(Volatile.Read(ref _player), player) && !_lifetime.IsCancellationRequested)
            _ = RecoverOutputAsync(current => ReferenceEquals(current, player));
    }

    internal async Task PhoneRemovedAsync(AndroidDevice device)
    {
        await RemoveMirrorsAsync(p => p is AndroidPhonePcmPlayer phone && AndroidDeviceIdentity.MatchesSavedDevice(device, phone.Device)).ConfigureAwait(false);
        await RecoverOutputAsync(current => current is AndroidPhonePcmPlayer p && AndroidDeviceIdentity.MatchesSavedDevice(device, p.Device),
            unavailablePhone: AndroidDeviceIdentity.Key(device)).ConfigureAwait(false);
    }

    private Task RecoverOutputAsync(Predicate<IPcmPlayer> playerMatch, Exception? error = null, string? unavailablePhone = null) =>
        Task.Run(async () =>
        {
            try
            {
                await _routes.WaitAsync(_lifetime).ConfigureAwait(false);
                try
                {
                    if (_disposed || _player == null || !playerMatch(_player)) return;
                    var selected = _mirrors.IdsSnapshot();
                    foreach (var candidate in selected)
                    {
                        try
                        {
                            var others = selected.Where(id => !StringComparer.OrdinalIgnoreCase.Equals(id, candidate)).ToArray();
                            await _mirrors.ApplyAsync(others, _createPlayer,
                                () => ChangePrimaryLockedAsync(candidate, restorePrevious: false), _lifetime).ConfigureAwait(false);
                            _changed();
                            return;
                        }
                        catch (Exception ex) when (!_lifetime.IsCancellationRequested) { error = ex; }
                    }
                    bool linkedOutput = AndroidOutputIdentity.IsPhoneOutput(_endpoint) || WindowsLinkOutputIdentity.IsWindowsLinkOutput(_endpoint);
                    if (linkedOutput && SourceAlreadyPlaying?.Invoke() != true)
                    {
                        string?[][] previous;
                        lock (_gate) previous = _previousOutputs.ToArray();
                        var unavailable = new HashSet<string?>(StringComparer.OrdinalIgnoreCase) { _endpoint };
                        foreach (var group in previous)
                        {
                            if (unavailablePhone != null)
                                foreach (var id in group)
                                    if (AndroidOutputIdentity.TryParseDeviceId(id, out var phone, out _) &&
                                        string.Equals(phone, unavailablePhone, StringComparison.OrdinalIgnoreCase)) unavailable.Add(id);
                            if (await RestoreOutputsAsync(group, unavailable).ConfigureAwait(false)) { _changed(); return; }
                        }
                    }
                    _failed(linkedOutput ? null : error);
                }
                finally { _routes.Release(); }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Windows Link output fallback: " + ex.GetType().Name);
                if (Volatile.Read(ref _player) is { } current && playerMatch(current)) _failed(ex);
            }
        });

    private void DefaultChanged()
    {
        Interlocked.Increment(ref _defaultVersion);
        QueueDefaultRefresh();
    }

    private void QueueDefaultRefresh()
    {
        if (Interlocked.Exchange(ref _defaultQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            int version = Volatile.Read(ref _defaultVersion);
            try { await ChangeOutputAsync(null, refreshDefault: true).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Windows Link default output: " + ex.GetType().Name); }
            finally
            {
                Interlocked.Exchange(ref _defaultQueued, 0);
                if (!_lifetime.IsCancellationRequested && version != Volatile.Read(ref _defaultVersion)) QueueDefaultRefresh();
            }
        });
    }

    private void StopWatch()
    {
        var watch = Interlocked.Exchange(ref _defaultWatch, null);
        DisposeQuietly(watch);
    }

    private static void DisposeQuietly(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Windows Link audio cleanup: " + ex.GetType().Name); }
    }

    public async ValueTask DisposeAsync()
    {
        await _routes.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                Volatile.Write(ref _state, _state with { State = CaptureState.Idle });
            }
            StopWatch();
            _mirrors.Clear();
            DisposeQuietly(Interlocked.Exchange(ref _player, null));
            _audio.Clear();
        }
        finally { _routes.Release(); }
    }
}

using System.Buffers;
using System.Runtime.InteropServices;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;

namespace SonicRoute.WindowsLink;

internal static class LinkAudioCapture
{
    internal static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    internal static async Task CaptureAsync(int? processId, Action<ReadOnlyMemory<byte>> onPcm, Action onStarted, CancellationToken token)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Windows build 20348 or later is required for process loopback.");
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
    LinkAudioPlayback Session);

internal sealed class LinkAudioPlayback : IPhoneAudioSession, IAsyncDisposable
{
    private readonly AndroidPcmSource _audio;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _routes = new(1, 1);
    private readonly Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer> _createPlayer;
    private readonly Func<Action, IDisposable> _watchDefault;
    private readonly CancellationToken _lifetime;
    private readonly Action _changed;
    private readonly Action<Exception?> _failed;
    private IPcmPlayer? _player;
    private IDisposable? _defaultWatch;
    private AndroidAudioState _state;
    private string? _endpoint;
    private bool _disposed;
    private int _received, _defaultQueued, _defaultVersion;

    internal LinkAudioPlayback(
        bool smartVolume,
        Action changed,
        Action<Exception?> failed,
        Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer>? createPlayer = null,
        Func<Action, IDisposable>? watchDefault = null,
        CancellationToken lifetime = default)
    {
        _audio = new AndroidPcmSource { SmartVolume = smartVolume };
        _state = new AndroidAudioState(CaptureState.Starting, SmartVolume: smartVolume);
        _changed = changed;
        _failed = failed;
        _createPlayer = createPlayer ?? ((pcm, endpoint, _) => new WasapiPcmPlayer(pcm, endpoint));
        _watchDefault = watchDefault ?? DefaultOutputNotifications.Subscribe;
        _lifetime = lifetime;
    }

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
            _audio.Write(scratch.AsSpan(0, data.Length));
        }
        finally { ArrayPool<float>.Shared.Return(scratch, clearArray: true); }

        if (Interlocked.Exchange(ref _received, 1) == 0)
            _ = Task.Run(EnsureInitialOutputAsync);
    }

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

    internal void SetPlayback(string? endpointId, float volume, bool muted)
    {
        int percent = Math.Clamp((int)Math.Round(volume * 100f), 0, 100);
        SetLevel(percent, muted);
        if (!string.Equals(Volatile.Read(ref _state).OutputId, endpointId, StringComparison.OrdinalIgnoreCase))
            _ = SetOutputAsync(endpointId);
    }

    internal void SetSmartVolume(bool smartVolume)
    {
        _audio.SmartVolume = smartVolume;
        lock (_gate)
        {
            if (_disposed || _state.SmartVolume == smartVolume) return;
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
            Volatile.Write(ref _state, next);
        }
        _changed();
    }

    public Task SetOutputAsync(string? endpoint) => Task.Run(() => ChangeOutputAsync(endpoint));

    private async Task ChangeOutputAsync(string? endpoint, bool refreshDefault = false, Predicate<AndroidPhonePcmPlayer>? phoneMatch = null)
    {
        await _routes.WaitAsync(_lifetime).ConfigureAwait(false);
        try
        {
            IPcmPlayer? previous;
            lock (_gate)
            {
                if (_disposed)
                {
                    if (refreshDefault || phoneMatch != null) return;
                    throw new ObjectDisposedException(nameof(LinkAudioPlayback));
                }
                if (_player == null)
                {
                    if (refreshDefault || phoneMatch != null) return;
                    if (string.Equals(_endpoint, endpoint, StringComparison.OrdinalIgnoreCase)) return;
                    _endpoint = endpoint;
                    if (endpoint != null) StopWatch();
                    Volatile.Write(ref _state, _state with { OutputId = endpoint });
                    goto NotifyChange;
                }
                if (refreshDefault ? _endpoint != null : string.Equals(endpoint, _endpoint, StringComparison.OrdinalIgnoreCase)) return;
                if (phoneMatch != null && (_player is not AndroidPhonePcmPlayer phone || !phoneMatch(phone))) return;
                previous = _player;
            }
            bool addedWatch = endpoint == null && _defaultWatch == null;
            if (addedWatch) _defaultWatch = _watchDefault(DefaultChanged);
            try { await ReplacePlayerAsync(previous, endpoint).ConfigureAwait(false); }
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
        }
        finally { _routes.Release(); }
    NotifyChange:
        if (!refreshDefault) _changed();
    }

    private async Task ReplacePlayerAsync(IPcmPlayer previous, string? endpoint)
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
            if (switching && !_lifetime.IsCancellationRequested && previous is not AndroidPhonePcmPlayer { Failure: not null })
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
        if (next is WasapiPcmPlayer wasapi)
        {
            wasapi.Failed += ex =>
            {
                if (ReferenceEquals(Volatile.Read(ref _player), next)) _failed(ex);
            };
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
            _ = ReturnToWindowsAsync(current => ReferenceEquals(current, player));
    }

    internal Task PhoneRemovedAsync(AndroidDevice device) =>
        ReturnToWindowsAsync(current => AndroidDeviceIdentity.MatchesSavedDevice(device, current.Device));

    private Task ReturnToWindowsAsync(Predicate<AndroidPhonePcmPlayer> phoneMatch) =>
        Task.Run(async () =>
        {
            try { await ChangeOutputAsync(null, phoneMatch: phoneMatch).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Windows Link phone output fallback: " + ex.GetType().Name);
                if (Volatile.Read(ref _player) is AndroidPhonePcmPlayer current && phoneMatch(current)) _failed(ex);
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
            DisposeQuietly(Interlocked.Exchange(ref _player, null));
            _audio.Clear();
        }
        finally { _routes.Release(); }
    }
}

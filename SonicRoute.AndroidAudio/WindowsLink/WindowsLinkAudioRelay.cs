using System.Buffers;
using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;

namespace SonicRoute.WindowsLink;

internal sealed record LinkExternalAudioDescriptor(string AppKey, string Name, bool SystemAudio = false, byte[]? IconPng = null);

internal sealed partial class WindowsLinkService
{
    private const LinkCapabilities RelayReceiver = LinkCapabilities.AudioReceive | LinkCapabilities.AudioReady;

    internal static bool CanReceiveExternalAudio(LinkPeer target) =>
        target.Connected && target.SupportsAudioRelay && target.Capabilities.HasFlag(RelayReceiver);

    internal bool CanRelayTo(WindowsLinkAudioSource source, LinkPeer target)
    {
        if (source.IsRelayed || !CanReceiveExternalAudio(target) || string.Equals(source.PeerId, target.Id, StringComparison.OrdinalIgnoreCase)) return false;
        lock (_gate) return _streams.TryGetValue(source.StreamId, out var track) && !track.Closing && track.AudioSource == source;
    }

    private LinkConnection RelayConnection(string targetId)
    {
        var link = Connected(targetId, LinkCapabilities.AudioReceive);
        if (!link.Hello.SupportsAudioRelay || !link.Hello.Capabilities.HasFlag(RelayReceiver))
            throw new NotSupportedException("Update the receiving computer to enable Windows Link audio relay.");
        return link;
    }

    private Track ReserveRelay(LinkConnection link, LinkMediaKind kind, string name, string? appKey, byte[]? icon, Guid? sourceId = null)
    {
        var track = Reserve(link, kind, name, false, appKey, iconPng: icon);
        track.Info = track.Info with { IsRelayed = true, InstanceId = $"r:{sourceId ?? track.Info.Id:N}" };
        track.RemotePlaybackReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return track;
    }

    internal async Task<ExternalAudioOutput> OpenExternalAudioOutputAsync(LinkExternalAudioDescriptor source, string peerId,
        Action<Exception> disconnected, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(disconnected);
        token.ThrowIfCancellationRequested();
        Track track;
        lock (_gate)
        {
            var link = RelayConnection(peerId);
            var kind = source.SystemAudio ? LinkMediaKind.SystemAudio : LinkMediaKind.AppAudio;
            track = ReserveRelay(link, kind, source.Name, source.AppKey, source.IconPng is { Length: <= MaxIconBytes } ? source.IconPng : null);
            track.ExternalOutput = new ExternalAudioOutput(this, link, track.Info.Id, disconnected);
        }
        await OpenTrackAsync(track, Task.CompletedTask, token).ConfigureAwait(false);
        return track.ExternalOutput.Start();
    }

    internal sealed class ExternalAudioOutput(WindowsLinkService service, LinkConnection link, Guid streamId,
        Action<Exception> disconnected) : IDisposable, IAsyncDisposable
    {
        private int _state; // 0 opening, 1 accepting packets, 2 closed
        internal Guid StreamId => streamId;

        internal ExternalAudioOutput Start() => Interlocked.CompareExchange(ref _state, 1, 0) == 0
            ? this : throw new IOException("The Windows Link receiver closed before audio playback started.");

        internal void Push(ReadOnlySpan<byte> pcm16)
        {
            if (Volatile.Read(ref _state) == 1) link.SendAudio(streamId, pcm16);
        }

        internal void PeerClosed()
        {
            if (Interlocked.Exchange(ref _state, 2) == 1)
            {
                try { disconnected(new IOException("The Windows Link receiver closed its stream.")); }
                catch (Exception ex) { service.Report(ex); }
            }
        }

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _state, 2) == 1
            ? new(service.StopStreamAsync(streamId)) : ValueTask.CompletedTask;
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private IPcmPlayer CreateIncomingPlayer(Track source, AndroidPcmSource pcm, string? endpoint, CancellationToken token)
    {
        if (WindowsLinkOutputIdentity.TryParsePeerId(endpoint, out string targetId))
            return new RelayPlayer(this, source.Info.Id, targetId, pcm, token);
        return _audioPlayerFactory?.Invoke(pcm, endpoint, token) ?? new WasapiPcmPlayer(pcm, endpoint);
    }

    internal sealed class RelayPlayer(WindowsLinkService service, Guid sourceId, string targetId, AndroidPcmSource pcm,
        CancellationToken token) : IPcmPlayer, IFailedPcmPlayer
    {
        private long _lastWritten;
        private Guid _outgoing;
        private volatile bool _playing, _disposed;
        private Exception? _failure;
        internal Exception? Failure => Volatile.Read(ref _failure);
        public event Action<Exception>? Failed;

        public void Play()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            // Player changes already run on a routing worker. Commit only after the third PC opens its player.
            if (_outgoing == Guid.Empty)
                _outgoing = service.OpenRelayAsync(sourceId, targetId, this, token).GetAwaiter().GetResult();
            if (Failure is { } error) throw new IOException("The relay output has closed.", error);
            _lastWritten = pcm.SamplesWritten;
            _playing = true;
        }

        public void Stop() => _playing = false;
        internal bool ReceivedPacket() => _playing && !_disposed && pcm.ConsumeRelayPacket(ref _lastWritten);
        internal void Disconnected()
        {
            _playing = false;
            var error = new IOException("The Windows Link relay receiver closed its stream.");
            if (!_disposed && Interlocked.CompareExchange(ref _failure, error, null) == null) Failed?.Invoke(error);
        }
        public void Dispose()
        {
            _disposed = true;
            _playing = false;
            if (_outgoing != Guid.Empty) service.ReleaseRelayAsync(_outgoing, this).GetAwaiter().GetResult();
        }
    }

    private async Task<Guid> OpenRelayAsync(Guid sourceId, string targetId, RelayPlayer player, CancellationToken token)
    {
        Track outgoing;
        lock (_gate)
        {
            if (!_streams.TryGetValue(sourceId, out var source) || source.Closing || source.AudioSource == null)
                throw new InvalidOperationException("The incoming audio stream has stopped.");
            if (source.Info.IsRelayed || string.Equals(source.Connection.Id, targetId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A relay cannot return to its source or be forwarded again.");
            var link = RelayConnection(targetId);
            var existing = Array.Find(source.RelayTargets, t => t.Connection == link && t.RelayPlayers.Length != 0 && !t.Closing);
            if (existing != null)
            {
                // Moving the same endpoint between primary and additional outputs preserves its single network stream.
                Volatile.Write(ref existing.RelayPlayers, [.. existing.RelayPlayers, player]);
                return existing.Info.Id;
            }
            outgoing = ReserveRelay(link, source.Info.Kind, source.Info.Name, source.Info.AppKey, source.Info.IconPng, source.Info.Id);
            outgoing.RelaySource = source;
            outgoing.RelayPlayers = [player];
            Volatile.Write(ref source.RelayTargets, [.. source.RelayTargets, outgoing]);
        }
        await OpenTrackAsync(outgoing, Task.CompletedTask, token).ConfigureAwait(false);
        return outgoing.Info.Id;
    }

    private Task ReleaseRelayAsync(Guid id, RelayPlayer player)
    {
        Track? route;
        lock (_gate)
        {
            if (!_streams.TryGetValue(id, out route) || route.Closing) return Task.CompletedTask;
            Volatile.Write(ref route.RelayPlayers, Array.FindAll(route.RelayPlayers, p => p != player));
            if (route.RelayPlayers.Length != 0) return Task.CompletedTask;
        }
        return RetireAsync(route, announce: true);
    }

    private void RelayStopped(Track route)
    {
        RelayPlayer[] listeners;
        lock (_gate)
        {
            var source = route.RelaySource!;
            Volatile.Write(ref source.RelayTargets, Array.FindAll(source.RelayTargets, t => t != route));
            listeners = route.RelayPlayers;
            Volatile.Write(ref route.RelayPlayers, []);
        }
        foreach (var listener in listeners) listener.Disconnected();
    }

    private static void ForwardRelayAudio(Track source, ReadOnlySpan<byte> packet)
    {
        var routes = Volatile.Read(ref source.RelayTargets);
        if (routes.Length == 0) return;
        float gain = source.Audio!.Pcm?.EffectiveGain ?? 0;
        byte[]? adjusted = null;
        ReadOnlySpan<byte> output = packet;
        try
        {
            foreach (var route in routes)
            {
                if (!route.Announced || !route.SendingMedia) continue;
                bool active = false;
                foreach (var player in Volatile.Read(ref route.RelayPlayers)) active |= player.ReceivedPacket();
                if (!active) continue;
                if (gain != 1 && adjusted == null)
                {
                    adjusted = ArrayPool<byte>.Shared.Rent(packet.Length);
                    var samples = MemoryMarshal.Cast<byte, short>(packet);
                    var result = MemoryMarshal.Cast<byte, short>(adjusted.AsSpan(0, packet.Length));
                    for (int i = 0; i < samples.Length; i++)
                        result[i] = (short)Math.Clamp(MathF.Round(samples[i] * gain), short.MinValue, short.MaxValue);
                    output = adjusted.AsSpan(0, packet.Length);
                }
                route.Connection.SendAudio(route.Info.Id, output);
            }
        }
        finally { if (adjusted != null) ArrayPool<byte>.Shared.Return(adjusted, clearArray: true); }
    }
}

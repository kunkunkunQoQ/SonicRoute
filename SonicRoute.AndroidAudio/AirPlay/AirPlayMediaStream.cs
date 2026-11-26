using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace SonicRoute.AirPlay;

internal sealed class AirPlayMediaStream : IAsyncDisposable
{
    private readonly AirPlayFormat _format;
    private readonly ChaCha20Poly1305? _cipher;
    private readonly Aes? _legacyCipher;
    private readonly byte[]? _legacyIv;
    private readonly AirPlayDecoder _decoder;
    private readonly AirPlayAudioOutput _output;
    private readonly IPAddress _peer;
    private readonly Socket _control;
    private readonly Socket? _udp;
    private readonly TcpListener? _tcp;
    private readonly CancellationTokenSource _stop;
    private readonly Task _run;
    private readonly object _gate = new();
    private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _playing;
    private int _flushRevision;
    private uint? _discardUntil, _anchorRtp;
    private long _anchorLocal;
    private ushort? _lastSequence;
    private ulong? _lastBufferedNonce;

    internal AirPlayMediaStream(AirPlayListenerPorts ports, int type, AirPlayFormat format, byte[] key, IPAddress local, IPAddress peer,
        AirPlayAudioOutput output, Action<bool> ended, CancellationToken token, byte[]? legacyIv = null)
    {
        Type = type; _format = format; _peer = peer; _output = output;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            if (legacyIv == null) _cipher = new ChaCha20Poly1305(key);
            else
            {
                _legacyCipher = Aes.Create();
                _legacyCipher.Key = key;
                _legacyIv = legacyIv;
            }
            _decoder = new AirPlayDecoder(format);
            _control = ports.BindUdp(local);
            if (type == 96) _udp = ports.BindUdp(local);
            else _tcp = ports.StartTcp(local, 2);
            _run = RunAsync(ended, _stop.Token);
        }
        catch
        {
            _tcp?.Stop(); _udp?.Dispose(); _control?.Dispose();
            _decoder?.Dispose(); _cipher?.Dispose(); _legacyCipher?.Dispose(); _stop.Dispose();
            throw;
        }
    }

    internal int Type { get; }
    internal int ControlPort => ((IPEndPoint)_control.LocalEndPoint!).Port;
    internal int DataPort => ((IPEndPoint)(_tcp?.LocalEndpoint ?? _udp!.LocalEndPoint!)).Port;

    internal void Record()
    {
        if (Type == 96) SetRate(true, null, 0);
    }

    internal void SetRate(bool playing, uint? rtp, long local)
    {
        lock (_gate)
        {
            _playing = playing; _anchorRtp = rtp; _anchorLocal = local;
            if (playing) _resume.TrySetResult();
            else
            {
                if (_resume.Task.IsCompleted) _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _output.Flush();
                _flushRevision++;
            }
        }
    }

    internal void Flush(uint? timestamp)
    {
        lock (_gate)
        {
            _discardUntil = timestamp;
            _output.Flush();
            _flushRevision++;
        }
    }

    private async Task RunAsync(Action<bool> ended, CancellationToken token)
    {
        Task control = DrainControlAsync(token);
        bool failed = false;
        try
        {
            byte[] packet = new byte[AirPlayDecoder.MaxPacket + 36];
            byte[] decrypted = new byte[AirPlayDecoder.MaxPacket];
            float[] samples = new float[8192];
            if (_tcp != null) await ReceiveTcpAsync(packet, decrypted, samples, token).ConfigureAwait(false);
            else await ReceiveUdpAsync(packet, decrypted, samples, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
            or System.Runtime.InteropServices.COMException or NotSupportedException or InvalidOperationException)
        {
            failed = !token.IsCancellationRequested && ex is not EndOfStreamException;
            if (failed) System.Diagnostics.Trace.WriteLine("AirPlay media: " + ex.GetType().Name);
        }
        finally
        {
            _stop.Cancel();
            try { await control.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
            ended(failed);
        }
    }

    private async Task DrainControlAsync(CancellationToken token)
    {
        byte[] packet = new byte[256];
        var sender = new SocketAddress(AddressFamily.InterNetwork);
        while (!token.IsCancellationRequested)
        {
            try { await _control.ReceiveFromAsync(packet.AsMemory(), SocketFlags.None, sender, token).ConfigureAwait(false); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset) { }
        }
    }

    private async Task ReceiveTcpAsync(byte[] packet, byte[] decrypted, float[] samples, CancellationToken token)
    {
        using var client = await AcceptPeerAsync(_tcp!, _peer, token).ConfigureAwait(false);
        var stream = client.GetStream();
        byte[] length = new byte[2];
        int revision = 0;
        while (!token.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
            int size = BinaryPrimitives.ReadUInt16BigEndian(length) - 2;
            if (size is < 37 || size > packet.Length) throw new InvalidDataException("Invalid buffered AirPlay packet length.");
            await stream.ReadExactlyAsync(packet.AsMemory(0, size), token).ConfigureAwait(false);
            int payloadSize = Decrypt(packet.AsSpan(0, size), decrypted);
            if (payloadSize == 0) continue;
            Task resume;
            lock (_gate) resume = _resume.Task;
            await resume.WaitAsync(token).ConfigureAwait(false);

            uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));
            var wait = BufferedDelay(timestamp);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
            // A TCP sender can send far ahead. Backpressure retains samples and caps latency/memory.
            const int target = AndroidAudio.AndroidPcmSource.SampleRate / 5;
            int queued;
            while ((queued = _output.BufferedSamples) > target)
            {
                lock (_gate) { if (!_playing) break; }
                int waitMs = Math.Clamp((queued - target) * 1000 / (2 * AndroidAudio.AndroidPcmSource.SampleRate), 12, 100);
                await Task.Delay(waitMs, token).ConfigureAwait(false);
            }
            if (_output.State.State == AndroidAudio.CaptureState.Starting && _output.HasSavedOutputs && Accepts(timestamp))
                await _output.PreparePlaybackAsync(token).ConfigureAwait(false);
            Publish(timestamp, decrypted.AsSpan(0, payloadSize), samples, ref revision);
        }
    }

    private TimeSpan BufferedDelay(uint timestamp)
    {
        lock (_gate)
        {
            if (_anchorRtp is not { } rtp || _anchorLocal == 0) return TimeSpan.Zero;
            int framesFromAnchor = unchecked((int)(timestamp - rtp));
            double seconds = (_anchorLocal - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency
                + framesFromAnchor / (double)_format.SampleRate - 0.075;
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 2));
        }
    }

    private async Task ReceiveUdpAsync(byte[] packet, byte[] decrypted, float[] samples, CancellationToken token)
    {
        int revision = 0;
        var sender = new SocketAddress(AddressFamily.InterNetwork);
        byte[] expectedAddress = _peer.GetAddressBytes();
        while (!token.IsCancellationRequested)
        {
            int size;
            try { size = await _udp!.ReceiveFromAsync(packet.AsMemory(), SocketFlags.None, sender, token).ConfigureAwait(false); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset) { continue; }
            if (!sender.Buffer.Span.Slice(4, 4).SequenceEqual(expectedAddress)) continue;
            int payloadSize = Decrypt(packet.AsSpan(0, size), decrypted);
            if (payloadSize == 0) continue;
            ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
            if (_lastSequence is ushort previous && unchecked((short)(sequence - previous)) <= 0) continue;
            _lastSequence = sequence;
            uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));
            if (_output.State.State == AndroidAudio.CaptureState.Starting && _output.HasSavedOutputs && Accepts(timestamp))
                await _output.PreparePlaybackAsync(token).ConfigureAwait(false);
            Publish(timestamp, decrypted.AsSpan(0, payloadSize), samples, ref revision);
        }
    }

    private bool Accepts(uint timestamp)
    {
        lock (_gate) return _playing && (_discardUntil is not uint cutoff || unchecked((int)(timestamp - cutoff)) >= 0);
    }

    private int Decrypt(ReadOnlySpan<byte> packet, Span<byte> output)
    {
        if (packet.Length < 12 || (Type == 96 && (packet[0] >> 6) != 2)) return 0;
        if (_legacyCipher != null)
        {
            var payload = packet[12..];
            if (payload.Length > output.Length) return 0;
            int blocks = payload.Length & ~15;
            if (blocks != 0) _legacyCipher.DecryptCbc(payload[..blocks], _legacyIv!, output[..blocks], PaddingMode.None);
            payload[blocks..].CopyTo(output[blocks..]);
            return payload.Length;
        }
        if (packet.Length <= 36 || packet.Length - 36 > output.Length) return 0;
        Span<byte> nonce = stackalloc byte[12];
        nonce[..4].Clear(); packet[^8..].CopyTo(nonce[4..]);
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(nonce[4..]);
        if (Type == 103 && _lastBufferedNonce == value) return 0;
        int size = packet.Length - 36;
        try
        {
            _cipher!.Decrypt(nonce, packet.Slice(12, size), packet.Slice(size + 12, 16), output[..size], packet.Slice(4, 8));
            if (Type == 103) _lastBufferedNonce = value;
            return size;
        }
        catch (AuthenticationTagMismatchException) { return 0; }
    }

    private void Publish(uint timestamp, ReadOnlySpan<byte> packet, float[] samples, ref int revision)
    {
        lock (_gate)
        {
            if (!_playing || _discardUntil is uint cutoff && unchecked((int)(timestamp - cutoff)) < 0) return;
            if (revision != _flushRevision) { _decoder.Flush(); revision = _flushRevision; }
            int length = _decoder.Decode(packet, samples);
            if (length > 0) _output.Write(samples.AsSpan(0, length), _format.SampleRate);
        }
    }

    internal static async ValueTask<TcpClient> AcceptPeerAsync(TcpListener listener, IPAddress peer, CancellationToken token)
    {
        while (true)
        {
            TcpClient client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            if (((IPEndPoint)client.Client.RemoteEndPoint!).Address.Equals(peer)) return client;
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _tcp?.Stop(); _udp?.Dispose(); _control.Dispose();
        try { await _run.ConfigureAwait(false); }
        finally { _decoder.Dispose(); _cipher?.Dispose(); _legacyCipher?.Dispose(); _stop.Dispose(); }
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SonicRoute.AirPlay;

// Timing belongs to a connected sender, so discovery and idle Windows sessions have no clock worker.
internal sealed class AirPlayClock : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task[] _tasks;
    private readonly Socket[] _sockets;
    private long _remoteAtObservation, _localAtObservation;
    private ushort _pendingSequence;
    private long _pendingLocal;
    private long _lastDelayRequest;
    private readonly object _clockGate = new();

    internal AirPlayClock(IPAddress local, IPAddress peer, bool ptp, string id, CancellationToken token, int remoteTimingPort = 0)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sockets = new List<Socket>(2);
        try
        {
            sockets.Add(Bind(local, ptp ? 319 : 0));
            if (ptp) sockets.Add(Bind(local, 320));
            _sockets = sockets.ToArray();
            if (ptp)
            {
                byte[] clock = Convert.FromHexString(id.Insert(6, "FFFE"));
                _tasks = [ReceivePtpAsync(_sockets[0], peer, clock, _stop.Token), ReceivePtpAsync(_sockets[1], peer, clock, _stop.Token)];
            }
            else if (remoteTimingPort != 0)
                _tasks = [ReceiveNtpAsync(_sockets[0], peer, _stop.Token), SendNtpAsync(_sockets[0], new IPEndPoint(peer, remoteTimingPort), _stop.Token)];
            else _tasks = [ReceiveNtpAsync(_sockets[0], peer, _stop.Token)];
        }
        catch
        {
            foreach (var socket in sockets) socket.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    internal int TimingPort => _sockets.Length == 1 ? ((IPEndPoint)_sockets[0].LocalEndPoint!).Port : 0;

    private static Socket Bind(IPAddress address, int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { socket.Bind(new IPEndPoint(address, port)); return socket; }
        catch { socket.Dispose(); throw; }
    }

    private static async Task ReceiveNtpAsync(Socket socket, IPAddress peer, CancellationToken token)
    {
        byte[] packet = new byte[64];
        EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try { result = await socket.ReceiveFromAsync(packet.AsMemory(), SocketFlags.None, endpoint, token).ConfigureAwait(false); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize) { continue; }
            if (result.ReceivedBytes != 32 || !((IPEndPoint)result.RemoteEndPoint).Address.Equals(peer)
                || packet[0] != 0x80 || (packet[1] & 0x7f) != 0x52) continue;
            packet[1] = 0xd3;
            packet.AsSpan(24, 8).CopyTo(packet.AsSpan(8));
            WriteNtp(packet.AsSpan(16), DateTime.UtcNow);
            WriteNtp(packet.AsSpan(24), DateTime.UtcNow);
            await socket.SendToAsync(packet.AsMemory(0, 32), SocketFlags.None, result.RemoteEndPoint, token).ConfigureAwait(false);
        }
    }

    private static async Task SendNtpAsync(Socket socket, IPEndPoint peer, CancellationToken token)
    {
        // Legacy mirror senders expect the receiver to initiate timing on the negotiated port.
        byte[] request = new byte[32];
        request[0] = 0x80;
        request[1] = 0xd2;
        request[3] = 7;
        while (!token.IsCancellationRequested)
        {
            WriteNtp(request.AsSpan(24), DateTime.UtcNow);
            try { await socket.SendToAsync(request.AsMemory(), SocketFlags.None, peer, token).ConfigureAwait(false); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.HostUnreachable) { }
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }
    }

    private static void WriteNtp(Span<byte> bytes, DateTime time)
    {
        long ticks = time.Ticks - new DateTime(1900, 1, 1).Ticks;
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)(ticks / TimeSpan.TicksPerSecond));
        BinaryPrimitives.WriteUInt32BigEndian(bytes[4..], (uint)((ticks % TimeSpan.TicksPerSecond << 32) / TimeSpan.TicksPerSecond));
    }

    private async Task ReceivePtpAsync(Socket socket, IPAddress peer, byte[] clock, CancellationToken token)
    {
        byte[] packet = new byte[256], request = new byte[44];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        var client = new IPEndPoint(peer, 319);
        ushort delaySequence = 0;
        while (!token.IsCancellationRequested)
        {
            var result = await socket.ReceiveFromAsync(packet.AsMemory(), SocketFlags.None, any, token).ConfigureAwait(false);
            long now = Stopwatch.GetTimestamp();
            if (result.ReceivedBytes < 44 || (packet[1] & 15) != 2 || !((IPEndPoint)result.RemoteEndPoint).Address.Equals(peer)) continue;
            byte type = (byte)(packet[0] & 15);
            ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(30));
            if (type == 0) // Sync, optionally followed by a precise origin timestamp.
            {
                lock (_clockGate)
                {
                    if ((packet[6] & 2) == 0) Observe(packet.AsSpan(34), now);
                    else { _pendingLocal = now; _pendingSequence = sequence; }
                }
                if (now - _lastDelayRequest < Stopwatch.Frequency) continue;
                _lastDelayRequest = now;
                request[0] = (byte)((packet[0] & 0xf0) | 1);
                request[1] = 2; request[3] = 44; request[4] = packet[4]; request[6] = 4;
                clock.CopyTo(request, 20); request[29] = 1;
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(30), ++delaySequence);
                request[32] = 1; request[33] = 0x7f;
                await socket.SendToAsync(request.AsMemory(), SocketFlags.None, client, token).ConfigureAwait(false);
            }
            else if (type == 8)
            {
                lock (_clockGate)
                    if (_pendingLocal != 0 && _pendingSequence == sequence) Observe(packet.AsSpan(34), _pendingLocal);
            }
        }
    }

    private void Observe(ReadOnlySpan<byte> packetTime, long observed)
    {
        long seconds = ((long)BinaryPrimitives.ReadUInt16BigEndian(packetTime) << 32)
            | BinaryPrimitives.ReadUInt32BigEndian(packetTime[2..]);
        uint ns = BinaryPrimitives.ReadUInt32BigEndian(packetTime[6..]);
        if (seconds >= long.MaxValue / 1_000_000_000 || ns >= 1_000_000_000) return;
        _remoteAtObservation = seconds * 1_000_000_000 + ns;
        _localAtObservation = observed;
    }

    internal long ToLocalTicks(long seconds, ulong fraction)
    {
        lock (_clockGate)
        {
            if (_localAtObservation == 0 || seconds < 0 || seconds >= long.MaxValue / 1_000_000_000) return 0;
            long ns = seconds * 1_000_000_000 + (long)(fraction * (1_000_000_000.0 / 18446744073709551616.0));
            double delta = (ns - _remoteAtObservation) / 1_000_000_000.0;
            // The anchor belongs to the current stream. Ignore clock epochs outside its window.
            return Math.Abs(delta) <= 30 ? _localAtObservation + (long)(delta * Stopwatch.Frequency) : 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var socket in _sockets) socket.Dispose();
        try { await Task.WhenAll(_tasks).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        _stop.Dispose();
    }
}

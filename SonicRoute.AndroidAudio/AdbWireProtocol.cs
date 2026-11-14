using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

namespace SonicRoute.AndroidAudio;

internal static class AdbCommands
{
    public const uint Sync = 0x434E5953; // "SYNC"
    public const uint Cnxn = 0x4E584E43; // "CNXN"
    public const uint Open = 0x4E45504F; // "OPEN"
    public const uint Okay = 0x59414B4F; // "OKAY"
    public const uint Clse = 0x45534C43; // "CLSE"
    public const uint Wrte = 0x45545257; // "WRTE"
    public const uint Auth = 0x48545541; // "AUTH"
    public const uint Stls = 0x534C5453; // "STLS"
}

internal static class AdbAuthType
{
    public const uint Token = 1;
    public const uint Signature = 2;
    public const uint RsaPublicKey = 3;
}

internal readonly record struct AdbPacketHeader(
    uint Command,
    uint Arg0,
    uint Arg1,
    uint DataLength,
    uint DataCheck,
    uint Magic)
{
    public const int WireSize = 24;

    public static AdbPacketHeader Create(uint command, uint arg0, uint arg1, ReadOnlySpan<byte> payload, bool computeChecksum = true)
    {
        uint check = computeChecksum ? ComputeChecksum(payload) : 0u;
        return new AdbPacketHeader(command, arg0, arg1, (uint)payload.Length, check, command ^ 0xFFFFFFFFu);
    }

    public static uint ComputeChecksum(ReadOnlySpan<byte> payload)
    {
        uint sum = 0;
        for (int i = 0; i < payload.Length; i++)
            sum += payload[i];
        return sum;
    }

    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], Command);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), Arg0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), Arg1);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12, 4), DataLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(16, 4), DataCheck);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(20, 4), Magic);
    }

    public static bool TryParse(ReadOnlySpan<byte> source, out AdbPacketHeader header)
    {
        if (source.Length < WireSize)
        {
            header = default;
            return false;
        }
        uint command = BinaryPrimitives.ReadUInt32LittleEndian(source[..4]);
        uint arg0 = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, 4));
        uint arg1 = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8, 4));
        uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12, 4));
        uint dataCheck = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(16, 4));
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(20, 4));
        header = new AdbPacketHeader(command, arg0, arg1, dataLen, dataCheck, magic);
        return magic == (command ^ 0xFFFFFFFFu);
    }
}

internal interface IAdbPacketLink : IAsyncDisposable
{
    bool SupportsTlsUpgrade { get; }
    string? PeerCertificateSha256 { get; }
    ValueTask<(AdbPacketHeader Header, int PayloadLength)> ReadPacketAsync(byte[] payloadBuffer, CancellationToken token);
    /// <summary>
    /// Reads one ADB packet into <paramref name="baselineBuffer"/> when the payload fits, or rents an exact-needed
    /// buffer from <see cref="ArrayPool{T}.Shared"/> returned in <c>RentedBuffer</c> for caller cleanup.
    /// </summary>
    async ValueTask<(AdbPacketHeader Header, int PayloadLength, byte[]? RentedBuffer)> ReadPacketPooledAsync(
        byte[] baselineBuffer,
        int maxPayloadBytes,
        CancellationToken token)
    {
        if (baselineBuffer.Length >= maxPayloadBytes)
        {
            var (header, len) = await ReadPacketAsync(baselineBuffer, token).ConfigureAwait(false);
            return (header, len, null);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(maxPayloadBytes);
        try
        {
            var (header, len) = await ReadPacketAsync(rented, token).ConfigureAwait(false);
            return (header, len, rented);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }
    ValueTask WritePacketAsync(AdbPacketHeader header, ReadOnlyMemory<byte> payload, CancellationToken token);
    Task UpgradeToTlsAsync(
        X509Certificate2 clientCertificate,
        Func<X509Certificate2?, bool>? serverCertificateValidator,
        string? targetHost,
        CancellationToken token);
}

internal sealed class StreamAdbPacketLink : IAdbPacketLink
{
    private readonly IDisposable? _owner;
    private readonly byte[] _rxHeaderBuf = new byte[AdbPacketHeader.WireSize];
    private Stream _stream;
    private int _disposed;

    public StreamAdbPacketLink(Stream stream, IDisposable? owner = null, bool supportsTlsUpgrade = true)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _owner = owner;
        SupportsTlsUpgrade = supportsTlsUpgrade;
    }

    public bool SupportsTlsUpgrade { get; }
    public string? PeerCertificateSha256 { get; private set; }

    public async ValueTask<(AdbPacketHeader Header, int PayloadLength)> ReadPacketAsync(byte[] payloadBuffer, CancellationToken token)
    {
        var (header, len, _) = await ReadPacketCoreAsync(payloadBuffer, payloadBuffer.Length, poolIfLarger: false, token).ConfigureAwait(false);
        return (header, len);
    }

    public ValueTask<(AdbPacketHeader Header, int PayloadLength, byte[]? RentedBuffer)> ReadPacketPooledAsync(
        byte[] baselineBuffer,
        int maxPayloadBytes,
        CancellationToken token)
        => ReadPacketCoreAsync(baselineBuffer, maxPayloadBytes, poolIfLarger: true, token);

    private async ValueTask<(AdbPacketHeader Header, int PayloadLength, byte[]? RentedBuffer)> ReadPacketCoreAsync(
        byte[] baselineBuffer,
        int maxPayloadBytes,
        bool poolIfLarger,
        CancellationToken token)
    {
        await ReadExactAsync(_stream, _rxHeaderBuf, token).ConfigureAwait(false);
        if (!AdbPacketHeader.TryParse(_rxHeaderBuf, out var header))
            throw new InvalidDataException($"Invalid ADB transport packet header magic (cmd=0x{header.Command:X8}, magic=0x{header.Magic:X8}).");

        int limit = poolIfLarger ? maxPayloadBytes : baselineBuffer.Length;
        if (header.DataLength > (uint)limit)
            throw new InvalidDataException($"ADB packet payload {header.DataLength} bytes exceeds buffer {limit} bytes.");

        int len = (int)header.DataLength;
        if (len == 0)
            return (header, 0, null);

        byte[]? rented = len > baselineBuffer.Length ? ArrayPool<byte>.Shared.Rent(len) : null;
        byte[] target = rented ?? baselineBuffer;
        try
        {
            await ReadExactAsync(_stream, target.AsMemory(0, len), token).ConfigureAwait(false);
            return (header, len, rented);
        }
        catch
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    public async ValueTask WritePacketAsync(AdbPacketHeader header, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (payload.Length <= 65536)
        {
            int total = AdbPacketHeader.WireSize + payload.Length;
            byte[] buf = ArrayPool<byte>.Shared.Rent(total);
            try
            {
                header.WriteTo(buf.AsSpan(0, AdbPacketHeader.WireSize));
                if (!payload.IsEmpty)
                    payload.Span.CopyTo(buf.AsSpan(AdbPacketHeader.WireSize));
                await _stream.WriteAsync(buf.AsMemory(0, total), token).ConfigureAwait(false);
                await _stream.FlushAsync(token).ConfigureAwait(false);
                return;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        byte[] headerBuf = new byte[AdbPacketHeader.WireSize];
        header.WriteTo(headerBuf);
        await _stream.WriteAsync(headerBuf, token).ConfigureAwait(false);
        await _stream.WriteAsync(payload, token).ConfigureAwait(false);
        await _stream.FlushAsync(token).ConfigureAwait(false);
    }

    public async Task UpgradeToTlsAsync(
        X509Certificate2 clientCertificate,
        Func<X509Certificate2?, bool>? serverCertificateValidator,
        string? targetHost,
        CancellationToken token)
    {
        if (!SupportsTlsUpgrade)
            throw new NotSupportedException("Underlying ADB packet link does not support STLS upgrade.");
        ArgumentNullException.ThrowIfNull(clientCertificate);

        var ssl = new SslStream(
            _stream,
            leaveInnerStreamOpen: false,
            userCertificateValidationCallback: (_, certificate, _, _) =>
            {
                if (certificate == null) return false;
                PeerCertificateSha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                if (serverCertificateValidator == null) return true;
                if (certificate is X509Certificate2 existingCert2)
                    return serverCertificateValidator(existingCert2);
                using var tempCert2 = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                return serverCertificateValidator(tempCert2);
            },
            userCertificateSelectionCallback: (_, _, _, _, _) => clientCertificate);

        try
        {
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = string.IsNullOrWhiteSpace(targetHost) ? "adb" : targetHost,
                ClientCertificates = new X509CertificateCollection { clientCertificate },
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            };
            await ssl.AuthenticateAsClientAsync(options, token).ConfigureAwait(false);
            _stream = ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
        try { _owner?.Dispose(); } catch { }
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n;
            try { n = await stream.ReadAsync(buffer[offset..], token).ConfigureAwait(false); }
            catch (Exception ex) when (token.IsCancellationRequested && ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            { throw new OperationCanceledException(token); }
            if (n == 0) throw new EndOfStreamException("ADB direct transport closed unexpectedly.");
            offset += n;
        }
    }
}

internal sealed class AdbMuxConnection : IAsyncDisposable
{
    public const uint VersionMin = 0x01000000;
    public const uint VersionSkipChecksum = 0x01000001;
    public const uint StlsVersion = 0x01000000;
    public const int LocalMaxPayload = 1024 * 1024;
    public const int MaxPayloadCap = 4 * 1024 * 1024;

    private const string HostBanner = "host::features=shell_v2,cmd,stat_v2,ls_v2,fixed_push_mkdir\0";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly IAdbPacketLink _link;
    private readonly ConcurrentDictionary<uint, AdbMuxStream> _streams = new();
    private readonly Channel<OutboundPacket> _outbound = Channel.CreateUnbounded<OutboundPacket>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _lifetime = new();
    private int _nextLocalId;
    private int _disposed;
    private int _disconnected;
    private Task? _readTask;
    private Task? _writeTask;

    private readonly record struct OutboundPacket(AdbPacketHeader Header, byte[]? PooledBuffer, int PayloadLength, TaskCompletionSource? Done);

    private AdbMuxConnection(
        IAdbPacketLink link,
        uint negotiatedVersion,
        int peerMaxPayload,
        string peerBanner,
        bool isTls)
    {
        _link = link;
        NegotiatedVersion = negotiatedVersion;
        SkipChecksum = negotiatedVersion >= VersionSkipChecksum;
        PeerMaxPayload = peerMaxPayload;
        PeerBanner = peerBanner;
        IsTls = isTls;
        ParseBanner(peerBanner, out string deviceType, out var props, out var features);
        DeviceType = deviceType;
        DeviceProperties = props;
        Features = features;
    }

    public uint NegotiatedVersion { get; }
    public bool SkipChecksum { get; }
    public int PeerMaxPayload { get; }
    public string PeerBanner { get; }
    public string DeviceType { get; }
    public IReadOnlyDictionary<string, string> DeviceProperties { get; }
    public IReadOnlyList<string> Features { get; }
    public bool IsTls { get; }
    public string? PeerCertificateSha256 => _link.PeerCertificateSha256;
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _disconnected) == 0;
    public bool HasActiveStreams => !_streams.IsEmpty;

    public event Action? Disconnected;

    public static async Task<AdbMuxConnection> ConnectAsync(
        IAdbPacketLink link,
        AdbKeyStore keyStore,
        Func<X509Certificate2?, bool>? serverCertificateValidator = null,
        string? targetHost = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(keyStore);

        byte[] baselineBuf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        byte[] bannerBytes = Utf8NoBom.GetBytes(HostBanner);
        try
        {
            await link.WritePacketAsync(
                AdbPacketHeader.Create(AdbCommands.Cnxn, VersionSkipChecksum, LocalMaxPayload, bannerBytes, computeChecksum: true),
                bannerBytes,
                token).ConfigureAwait(false);

            bool isTls = false;
            bool sentSignature = false;
            bool sentPublicKey = false;
            int authRounds = 0;

            while (true)
            {
                var (header, len, rented) = await link.ReadPacketPooledAsync(baselineBuf, MaxPayloadCap, token).ConfigureAwait(false);
                try
                {
                    byte[] payloadBuf = rented ?? baselineBuf;
                    switch (header.Command)
                    {
                        case AdbCommands.Stls:
                            if (isTls)
                                throw new InvalidDataException("Unexpected duplicate ADB STLS packet during TLS handshake.");
                            if (!link.SupportsTlsUpgrade)
                                throw new InvalidDataException("ADB peer requested STLS on a non-TLS transport.");

                            await link.WritePacketAsync(
                                AdbPacketHeader.Create(AdbCommands.Stls, StlsVersion, 0, ReadOnlySpan<byte>.Empty, computeChecksum: true),
                                ReadOnlyMemory<byte>.Empty,
                                token).ConfigureAwait(false);
                            await link.UpgradeToTlsAsync(keyStore.GetTlsClientCertificate(), serverCertificateValidator, targetHost, token).ConfigureAwait(false);
                            isTls = true;
                            // Per adb.exe client state machine, resend host CNXN after upgrading to TLS 1.3.
                            await link.WritePacketAsync(
                                AdbPacketHeader.Create(AdbCommands.Cnxn, VersionSkipChecksum, LocalMaxPayload, bannerBytes, computeChecksum: false),
                                bannerBytes,
                                token).ConfigureAwait(false);
                            break;

                        case AdbCommands.Auth:
                            if (header.Arg0 != AdbAuthType.Token)
                                throw new InvalidDataException($"Unexpected ADB AUTH subcommand {header.Arg0} from peer.");
                            if (++authRounds > 4)
                                throw new IOException("ADB device rejected RSA key authentication.");

                            if (!sentSignature)
                            {
                                byte[] signature = keyStore.SignAuthToken(payloadBuf.AsSpan(0, len));
                                await link.WritePacketAsync(
                                    AdbPacketHeader.Create(AdbCommands.Auth, AdbAuthType.Signature, 0, signature, computeChecksum: !isTls),
                                    signature,
                                    token).ConfigureAwait(false);
                                sentSignature = true;
                            }
                            else if (!sentPublicKey)
                            {
                                byte[] pubLine = Utf8NoBom.GetBytes(keyStore.GetPublicKeyLine() + "\0");
                                await link.WritePacketAsync(
                                    AdbPacketHeader.Create(AdbCommands.Auth, AdbAuthType.RsaPublicKey, 0, pubLine, computeChecksum: !isTls),
                                    pubLine,
                                    token).ConfigureAwait(false);
                                sentPublicKey = true;
                            }
                            else
                            {
                                throw new IOException("ADB device declined authentication after RSA public key was presented.");
                            }
                            break;

                        case AdbCommands.Cnxn:
                            uint negotiated = Math.Min(VersionSkipChecksum, Math.Max(VersionMin, header.Arg0));
                            int peerMax = header.Arg1 == 0 ? 4096 : Math.Clamp((int)Math.Min(header.Arg1, int.MaxValue), 4096, MaxPayloadCap);
                            string peerBanner = Utf8NoBom.GetString(payloadBuf, 0, len).TrimEnd('\0');
                            var conn = new AdbMuxConnection(link, negotiated, peerMax, peerBanner, isTls);
                            conn.StartLoops();
                            return conn;

                        case AdbCommands.Clse:
                            // Ignore stale CLSE packets emitted by a previous host session after USB reconnect.
                            break;

                        default:
                            throw new InvalidDataException($"Unexpected ADB packet 0x{header.Command:X8} before CNXN completed.");
                    }
                }
                finally
                {
                    if (rented != null)
                        ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(baselineBuf);
        }
    }

    private void StartLoops()
    {
        _writeTask = Task.Run(WriteLoopAsync);
        _readTask = Task.Run(ReadLoopAsync);
    }

    public async Task<AdbMuxStream> OpenStreamAsync(string destination, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsConnected) throw new IOException("ADB direct connection is closed.");
        ArgumentException.ThrowIfNullOrEmpty(destination);

        uint localId = unchecked((uint)Interlocked.Increment(ref _nextLocalId));
        if (localId == 0) localId = unchecked((uint)Interlocked.Increment(ref _nextLocalId));

        var stream = new AdbMuxStream(this, localId);
        if (!_streams.TryAdd(localId, stream))
            throw new InvalidOperationException("ADB stream ID collision.");

        byte[] destBytes = Utf8NoBom.GetBytes(destination + "\0");
        try
        {
            await SendPacketAsync(AdbCommands.Open, localId, 0, destBytes, token).ConfigureAwait(false);
            await stream.WaitOpenedAsync(token).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            _streams.TryRemove(localId, out _);
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<AdbShellResult> RunShellAsync(string command, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        return AdbSocketClient.RunShellWithOpenerAsync(OpenShellAsync, command, token);
    }

    public async Task<IAdbShellSession> OpenShellAsync(string command, bool redirectInput, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        AdbMuxStream stream;
        try
        {
            stream = await OpenStreamAsync("shell,v2,raw:" + command, token).ConfigureAwait(false);
            return AdbSocketClient.CreateV2ShellSession(
                stream,
                () => _ = stream.DisposeAsync(),
                () => stream.DisposeAsync());
        }
        catch (IOException) when (IsConnected && !token.IsCancellationRequested)
        {
            stream = await OpenStreamAsync("shell:" + command, token).ConfigureAwait(false);
            return AdbSocketClient.CreateV1ShellSession(
                stream,
                redirectInput,
                closeInputAction: null,
                () => _ = stream.DisposeAsync(),
                () => stream.DisposeAsync());
        }
    }

    public async Task PushAsync(string localPath, string remotePath, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(localPath);
        ArgumentException.ThrowIfNullOrEmpty(remotePath);
        if (!File.Exists(localPath)) throw new FileNotFoundException("Missing local file to push.", localPath);
        await using var stream = await OpenStreamAsync("sync:", token).ConfigureAwait(false);
        await AdbSocketClient.PushSyncOverStreamAsync(stream, localPath, remotePath, token).ConfigureAwait(false);
    }

    public async Task<Stream> OpenLocalAbstractAsync(string name, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return await OpenStreamAsync("localabstract:" + name, token).ConfigureAwait(false);
    }

    internal void PostControlPacket(uint command, uint arg0, uint arg1)
    {
        if (!IsConnected) return;
        var header = AdbPacketHeader.Create(command, arg0, arg1, ReadOnlySpan<byte>.Empty, computeChecksum: !SkipChecksum);
        _outbound.Writer.TryWrite(new OutboundPacket(header, null, 0, null));
    }

    internal async Task SendPacketAsync(uint command, uint arg0, uint arg1, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (!IsConnected) throw new IOException("ADB direct connection is closed.");
        token.ThrowIfCancellationRequested();

        byte[]? pooled = null;
        int len = payload.Length;
        if (len > 0)
        {
            pooled = ArrayPool<byte>.Shared.Rent(len);
            payload.Span.CopyTo(pooled);
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var header = AdbPacketHeader.Create(command, arg0, arg1, payload.Span, computeChecksum: !SkipChecksum);
        if (!_outbound.Writer.TryWrite(new OutboundPacket(header, pooled, len, done)))
        {
            if (pooled != null) ArrayPool<byte>.Shared.Return(pooled);
            throw new IOException("ADB direct connection outbound queue closed.");
        }

        await done.Task.WaitAsync(token).ConfigureAwait(false);
    }

    internal void UnregisterStream(uint localId) => _streams.TryRemove(localId, out _);

    private async Task WriteLoopAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var packet in _outbound.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    ReadOnlyMemory<byte> body = packet.PooledBuffer != null
                        ? packet.PooledBuffer.AsMemory(0, packet.PayloadLength)
                        : ReadOnlyMemory<byte>.Empty;
                    await _link.WritePacketAsync(packet.Header, body, _lifetime.Token).ConfigureAwait(false);
                    packet.Done?.TrySetResult();
                }
                catch (Exception ex)
                {
                    packet.Done?.TrySetException(ex);
                    throw;
                }
                finally
                {
                    if (packet.PooledBuffer != null)
                        ArrayPool<byte>.Shared.Return(packet.PooledBuffer);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            failure = new IOException("ADB direct connection closed.");
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            _outbound.Writer.TryComplete(failure);
            while (_outbound.Reader.TryRead(out var leftover))
            {
                if (leftover.PooledBuffer != null)
                    ArrayPool<byte>.Shared.Return(leftover.PooledBuffer);
                leftover.Done?.TrySetException(failure ?? new IOException("ADB direct connection closed."));
            }
            if (failure != null && Volatile.Read(ref _disposed) == 0)
                NotifyDisconnected(failure);
        }
    }

    private async Task ReadLoopAsync()
    {
        int maxAllowedPayload = Math.Max(LocalMaxPayload, PeerMaxPayload);
        byte[] baseline = ArrayPool<byte>.Shared.Rent(64 * 1024);
        Exception? failure = null;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var (header, len, rented) = await _link.ReadPacketPooledAsync(baseline, maxAllowedPayload, _lifetime.Token).ConfigureAwait(false);
                try
                {
                    ReadOnlySpan<byte> payload = (rented ?? baseline).AsSpan(0, len);
                    if (!SkipChecksum && header.DataCheck != AdbPacketHeader.ComputeChecksum(payload))
                        throw new InvalidDataException($"ADB packet payload checksum mismatch (cmd=0x{header.Command:X8}).");

                    switch (header.Command)
                    {
                        case AdbCommands.Okay:
                            if (_streams.TryGetValue(header.Arg1, out var okayStream))
                                okayStream.OnOkay(header.Arg0);
                            break;

                        case AdbCommands.Wrte:
                            if (_streams.TryGetValue(header.Arg1, out var wrteStream))
                                wrteStream.OnWrte(header.Arg0, payload);
                            else if (header.Arg0 != 0 && header.Arg1 != 0)
                                PostControlPacket(AdbCommands.Clse, header.Arg1, header.Arg0);
                            break;

                        case AdbCommands.Clse:
                            if (_streams.TryRemove(header.Arg1, out var closedStream))
                                closedStream.OnRemoteClose(header.Arg0);
                            break;

                        case AdbCommands.Open:
                            // Device-initiated reverse socket openings are refused cleanly with CLSE.
                            if (header.Arg0 != 0)
                                PostControlPacket(AdbCommands.Clse, 0, header.Arg0);
                            break;
                    }
                }
                finally
                {
                    if (rented != null)
                        ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(baseline);
            NotifyDisconnected(failure ?? new EndOfStreamException("ADB direct connection closed."));
        }
    }

    private void NotifyDisconnected(Exception reason)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        _outbound.Writer.TryComplete(reason);
        foreach (var kv in _streams)
        {
            if (_streams.TryRemove(kv.Key, out var stream))
                stream.OnTransportFault(reason);
        }
        _lifetime.Cancel();
        Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        NotifyDisconnected(new ObjectDisposedException(nameof(AdbMuxConnection)));
        try { await _link.DisposeAsync().ConfigureAwait(false); } catch { }
        if (_readTask != null) { try { await _readTask.ConfigureAwait(false); } catch { } }
        if (_writeTask != null) { try { await _writeTask.ConfigureAwait(false); } catch { } }
        _lifetime.Dispose();
    }

    private static void ParseBanner(
        string banner,
        out string deviceType,
        out Dictionary<string, string> properties,
        out string[] features)
    {
        deviceType = "device";
        properties = new Dictionary<string, string>(StringComparer.Ordinal);
        features = [];
        if (string.IsNullOrWhiteSpace(banner)) return;

        int sep = banner.IndexOf("::", StringComparison.Ordinal);
        string head = sep >= 0 ? banner[..sep].Trim() : banner.Trim();
        if (!string.IsNullOrEmpty(head))
            deviceType = head;
        if (sep < 0) return;

        string tail = banner[(sep + 2)..];
        foreach (string part in tail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq].Trim();
            string value = part[(eq + 1)..].Trim();
            properties[key] = value;
            if (key == "features" && value.Length > 0)
                features = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}

internal sealed class AdbMuxStream : Stream
{
    private const int HighWaterMarkBytes = 4 * 1024 * 1024;
    private const int LowWaterMarkBytes = 2 * 1024 * 1024;

    private readonly AdbMuxConnection _conn;
    private readonly uint _localId;
    private readonly Pipe _rxPipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false));
    private readonly Stream _rxReader;
    private readonly object _flowLock = new();
    private readonly object _writeStateLock = new();
    private readonly SemaphoreSlim _writeSerialize = new(1, 1);
    private readonly TaskCompletionSource _openTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private uint _remoteId;
    private bool _opened;
    private bool _hasWriteCredit;
    private TaskCompletionSource? _writeCreditTcs;
    private Exception? _fault;
    private long _bufferedBytes;
    private int _deferredOkayCount;
    private bool _receiveCompleted;
    private int _disposed;
    private int _clseSent;

    internal AdbMuxStream(AdbMuxConnection conn, uint localId)
    {
        _conn = conn;
        _localId = localId;
        _rxReader = _rxPipe.Reader.AsStream(leaveOpen: false);
    }

    public uint LocalId => _localId;
    public uint RemoteId => Volatile.Read(ref _remoteId);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    internal Task WaitOpenedAsync(CancellationToken token) => _openTcs.Task.WaitAsync(token);

    internal void OnOkay(uint remoteId)
    {
        bool sendLateClose = false;
        bool openedNow = false;
        TaskCompletionSource? creditWaiter = null;
        lock (_writeStateLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                sendLateClose = !_opened && remoteId != 0 && Interlocked.Exchange(ref _clseSent, 1) == 0;
            }
            else if (!_opened)
            {
                _remoteId = remoteId;
                _opened = true;
                _hasWriteCredit = true;
                openedNow = true;
            }
            else
            {
                _hasWriteCredit = true;
                creditWaiter = _writeCreditTcs;
                _writeCreditTcs = null;
            }
        }
        if (sendLateClose) _conn.PostControlPacket(AdbCommands.Clse, _localId, remoteId);
        if (openedNow) _openTcs.TrySetResult();
        creditWaiter?.TrySetResult();
    }

    internal void OnWrte(uint remoteId, ReadOnlySpan<byte> payload)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        uint targetRemote = RemoteId;
        if (targetRemote == 0) targetRemote = remoteId;

        bool sendOkay;
        lock (_flowLock)
        {
            if (_receiveCompleted || Volatile.Read(ref _disposed) != 0) return;
            if (!payload.IsEmpty)
            {
                Span<byte> dest = _rxPipe.Writer.GetSpan(payload.Length);
                payload.CopyTo(dest);
                _rxPipe.Writer.Advance(payload.Length);
                _bufferedBytes += payload.Length;
            }
            if (_bufferedBytes < HighWaterMarkBytes)
            {
                sendOkay = true;
            }
            else
            {
                _deferredOkayCount++;
                sendOkay = false;
            }
            // Completion uses the same gate: no writer operation may outlive it.
            _ = _rxPipe.Writer.FlushAsync().AsTask();
        }

        if (sendOkay && targetRemote != 0)
            _conn.PostControlPacket(AdbCommands.Okay, _localId, targetRemote);
    }

    internal void OnRemoteClose(uint remoteId)
    {
        Exception? openErr = null;
        TaskCompletionSource? creditWaiter = null;
        lock (_writeStateLock)
        {
            if (!_opened)
                openErr = new IOException("ADB remote refused or closed the requested service.");
            _fault ??= new EndOfStreamException("ADB stream closed by remote.");
            creditWaiter = _writeCreditTcs;
            _writeCreditTcs = null;
        }

        CompleteReceive();
        if (openErr != null) _openTcs.TrySetException(openErr);
        creditWaiter?.TrySetException(new EndOfStreamException("ADB stream closed by remote."));

        if (remoteId != 0 && Interlocked.Exchange(ref _clseSent, 1) == 0)
            _conn.PostControlPacket(AdbCommands.Clse, _localId, remoteId);
    }

    internal void OnTransportFault(Exception reason)
    {
        TaskCompletionSource? creditWaiter = null;
        bool wasOpened;
        lock (_writeStateLock)
        {
            wasOpened = _opened;
            _fault ??= reason;
            creditWaiter = _writeCreditTcs;
            _writeCreditTcs = null;
        }
        CompleteReceive(reason);
        if (!wasOpened) _openTcs.TrySetException(reason);
        creditWaiter?.TrySetException(reason);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _rxReader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0) ReleaseFlowCredits(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        byte[] temp = ArrayPool<byte>.Shared.Rent(Math.Max(1, buffer.Length));
        try
        {
            int n = ReadAsync(temp.AsMemory(0, buffer.Length), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            temp.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty) return;

        await _writeSerialize.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int offset = 0;
            int chunkMax = _conn.PeerMaxPayload;
            while (offset < buffer.Length)
            {
                int chunk = Math.Min(buffer.Length - offset, chunkMax);
                uint remote = await WaitAndConsumeWriteCreditAsync(cancellationToken).ConfigureAwait(false);
                await _conn.SendPacketAsync(AdbCommands.Wrte, _localId, remote, buffer.Slice(offset, chunk), cancellationToken).ConfigureAwait(false);
                offset += chunk;
            }
        }
        finally
        {
            _writeSerialize.Release();
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty) return;
        byte[] copy = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(copy);
            WriteAsync(copy.AsMemory(0, buffer.Length), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copy);
        }
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void ReleaseFlowCredits(int bytesConsumed)
    {
        int sendCount = 0;
        lock (_flowLock)
        {
            _bufferedBytes = Math.Max(0, _bufferedBytes - bytesConsumed);
            if (_deferredOkayCount > 0 && _bufferedBytes <= LowWaterMarkBytes)
            {
                sendCount = _deferredOkayCount;
                _deferredOkayCount = 0;
            }
        }
        uint remote = RemoteId;
        if (sendCount > 0 && remote != 0)
        {
            for (int i = 0; i < sendCount; i++)
                _conn.PostControlPacket(AdbCommands.Okay, _localId, remote);
        }
    }

    private async ValueTask<uint> WaitAndConsumeWriteCreditAsync(CancellationToken token)
    {
        TaskCompletionSource waiter;
        lock (_writeStateLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_fault != null) throw new IOException("ADB stream closed.", _fault);
            if (!_opened) throw new InvalidOperationException("ADB stream is not open.");
            if (_hasWriteCredit)
            {
                _hasWriteCredit = false;
                return _remoteId;
            }
            waiter = _writeCreditTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        await waiter.Task.WaitAsync(token).ConfigureAwait(false);
        lock (_writeStateLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_fault != null) throw new IOException("ADB stream closed.", _fault);
            _hasWriteCredit = false;
            return _remoteId;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseStreamCore();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        CloseStreamCore();
        try { await _rxReader.DisposeAsync().ConfigureAwait(false); } catch { }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void CloseStreamCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _conn.UnregisterStream(_localId);

        uint remote;
        TaskCompletionSource? creditWaiter;
        lock (_writeStateLock)
        {
            remote = _remoteId;
            _fault ??= new ObjectDisposedException(nameof(AdbMuxStream));
            creditWaiter = _writeCreditTcs;
            _writeCreditTcs = null;
        }

        CompleteReceive();
        _openTcs.TrySetException(new ObjectDisposedException(nameof(AdbMuxStream)));
        creditWaiter?.TrySetException(new ObjectDisposedException(nameof(AdbMuxStream)));

        if (remote != 0 && Interlocked.Exchange(ref _clseSent, 1) == 0)
            _conn.PostControlPacket(AdbCommands.Clse, _localId, remote);
    }

    private void CompleteReceive(Exception? reason = null)
    {
        lock (_flowLock)
        {
            if (_receiveCompleted) return;
            _receiveCompleted = true;
            _deferredOkayCount = 0;
            _rxPipe.Writer.Complete(reason);
        }
    }
}

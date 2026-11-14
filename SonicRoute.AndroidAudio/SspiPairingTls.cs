using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SonicRoute.AndroidAudio;

/// <summary>
/// P/Invokes Windows Schannel (<c>secur32.dll</c>) to establish the TLS 1.3 connection for Android
/// <c>_adb-tls-pairing._tcp</c>, export 64-byte RFC 5705/8446 keying material (<c>"adb-label"</c>),
/// and run the encrypted SPAKE2 + AES-128-GCM <c>PeerInfo</c> pairing exchange.
/// </summary>
internal static class SspiPairingTls
{
    private const string KeyingMaterialLabel = "adb-label";
    private const int KeyingMaterialSize = 64;

    public static async Task<string> PairAsync(
        string host,
        int port,
        string pairingPassword,
        AdbKeyStore keyStore,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pairingPassword))
            throw new ArgumentException("Pairing password cannot be empty.", nameof(pairingPassword));

        using var tcp = new TcpClient();
        using var reg = cancellationToken.Register(static s => { try { ((TcpClient)s!).Dispose(); } catch { } }, tcp);
        await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        tcp.NoDelay = true;

        var clientCert = keyStore.GetTlsClientCertificate();
        using var tls = await SspiTlsStream.AuthenticateAsClientAsync(
            tcp.GetStream(),
            clientCert,
            targetHost: host,
            cancellationToken).ConfigureAwait(false);

        byte[] ekm = tls.ExportKeyingMaterial(KeyingMaterialLabel, KeyingMaterialSize);
        byte[] pwBytes = Encoding.UTF8.GetBytes(pairingPassword);
        byte[] fullPassword = new byte[pwBytes.Length + ekm.Length];
        pwBytes.CopyTo(fullPassword, 0);
        ekm.CopyTo(fullPassword, pwBytes.Length);

        var spake = new Spake25519.HandshakeSession(isAlice: true, fullPassword);
        await WritePacketAsync(tls, Spake25519.PacketTypeSpake2Msg, spake.MyMessage, cancellationToken).ConfigureAwait(false);

        var (msgType, peerSpakeMsg) = await ReadPacketAsync(tls, maxPayloadBytes: 256, cancellationToken).ConfigureAwait(false);
        if (msgType != Spake25519.PacketTypeSpake2Msg)
            throw new IOException($"Unexpected pairing packet type {msgType}; expected SPAKE2_MSG.");

        byte[] outKey = spake.ProcessPeerMessage(peerSpakeMsg);
        using var cipher = new Spake25519.PairingAesCipher(outKey);

        byte[] myPeerInfo = Spake25519.EncodePeerInfo(Spake25519.PeerInfoRsaPubKey, keyStore.GetPublicKeyLine());
        byte[] encryptedMyInfo = cipher.Encrypt(myPeerInfo);
        await WritePacketAsync(tls, Spake25519.PacketTypePeerInfo, encryptedMyInfo, cancellationToken).ConfigureAwait(false);

        var (infoType, encryptedPeerInfo) = await ReadPacketAsync(tls, maxPayloadBytes: 16384, cancellationToken).ConfigureAwait(false);
        if (infoType != Spake25519.PacketTypePeerInfo)
            throw new IOException($"Unexpected pairing packet type {infoType}; expected PEER_INFO.");

        byte[] peerPlain = cipher.Decrypt(encryptedPeerInfo);
        var (_, guid) = Spake25519.DecodePeerInfo(peerPlain);
        string endpoint = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]:{port}"
            : $"{host}:{port}";
        return string.IsNullOrWhiteSpace(guid)
            ? $"Successfully paired to {endpoint}"
            : $"Successfully paired to {endpoint} [guid={guid}]";
    }

    internal static async Task<string> AcceptPairingServerAsync(
        Stream rawStream,
        X509Certificate2 serverCert,
        string expectedPairingPassword,
        string serverGuid,
        CancellationToken cancellationToken)
    {
        using var tls = await SspiTlsStream.AuthenticateAsServerAsync(rawStream, serverCert, cancellationToken).ConfigureAwait(false);
        byte[] ekm = tls.ExportKeyingMaterial(KeyingMaterialLabel, KeyingMaterialSize);
        byte[] pwBytes = Encoding.UTF8.GetBytes(expectedPairingPassword);
        byte[] fullPassword = new byte[pwBytes.Length + ekm.Length];
        pwBytes.CopyTo(fullPassword, 0);
        ekm.CopyTo(fullPassword, pwBytes.Length);

        var spake = new Spake25519.HandshakeSession(isAlice: false, fullPassword);
        await WritePacketAsync(tls, Spake25519.PacketTypeSpake2Msg, spake.MyMessage, cancellationToken).ConfigureAwait(false);

        var (msgType, peerSpakeMsg) = await ReadPacketAsync(tls, maxPayloadBytes: 256, cancellationToken).ConfigureAwait(false);
        if (msgType != Spake25519.PacketTypeSpake2Msg)
            throw new IOException($"Unexpected pairing packet type {msgType}; expected SPAKE2_MSG.");

        byte[] outKey = spake.ProcessPeerMessage(peerSpakeMsg);
        using var cipher = new Spake25519.PairingAesCipher(outKey);

        byte[] myPeerInfo = Spake25519.EncodePeerInfo(Spake25519.PeerInfoDeviceGuid, serverGuid);
        byte[] encryptedMyInfo = cipher.Encrypt(myPeerInfo);
        await WritePacketAsync(tls, Spake25519.PacketTypePeerInfo, encryptedMyInfo, cancellationToken).ConfigureAwait(false);

        var (infoType, encryptedPeerInfo) = await ReadPacketAsync(tls, maxPayloadBytes: 16384, cancellationToken).ConfigureAwait(false);
        if (infoType != Spake25519.PacketTypePeerInfo)
            throw new IOException($"Unexpected pairing packet type {infoType}; expected PEER_INFO.");

        byte[] peerPlain = cipher.Decrypt(encryptedPeerInfo);
        var (_, clientPubKey) = Spake25519.DecodePeerInfo(peerPlain);
        return clientPubKey;
    }

    private static async Task WritePacketAsync(Stream stream, byte type, byte[] payload, CancellationToken token)
    {
        byte[] header = new byte[6];
        header[0] = Spake25519.PairingPacketVersion;
        header[1] = type;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(2, 4), (uint)payload.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<(byte Type, byte[] Payload)> ReadPacketAsync(Stream stream, int maxPayloadBytes, CancellationToken token)
    {
        byte[] header = new byte[6];
        await ReadExactAsync(stream, header, token).ConfigureAwait(false);
        if (header[0] != Spake25519.PairingPacketVersion)
            throw new IOException($"Unsupported pairing packet version {header[0]}.");
        byte type = header[1];
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(2, 4));
        if (length == 0 || length > (uint)maxPayloadBytes)
            throw new IOException($"Invalid pairing packet length {length}.");
        byte[] payload = new byte[length];
        await ReadExactAsync(stream, payload, token).ConfigureAwait(false);
        return (type, payload);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException("Pairing stream closed prematurely.");
            offset += n;
        }
    }

    /// <summary>SSPI/SChannel stream supporting TLS 1.3 handshake + RFC 5705/8446 Keying Material export.</summary>
    internal sealed class SspiTlsStream : Stream
    {
        private const string SchannelPackage = "Microsoft Unified Security Protocol Provider";
        private const uint SCH_CREDENTIALS_VERSION = 5;
        private const uint SCH_CRED_NO_DEFAULT_CREDS = 0x00000010;
        private const uint SCH_CRED_MANUAL_CRED_VALIDATION = 0x00000008;
        private const uint SCH_CRED_NO_SERVERNAME_CHECK = 0x00000004;
        private const uint SCH_USE_STRONG_CRYPTO = 0x00400000;

        private const int SECPKG_CRED_INBOUND = 1;
        private const int SECPKG_CRED_OUTBOUND = 2;

        private const int ISC_REQ_SEQUENCE_DETECT = 0x00000008;
        private const int ISC_REQ_REPLAY_DETECT = 0x00000004;
        private const int ISC_REQ_CONFIDENTIALITY = 0x00000010;
        private const int ISC_REQ_ALLOCATE_MEMORY = 0x00000100;
        private const int ISC_REQ_STREAM = 0x00008000;
        private const int ISC_REQ_MANUAL_CRED_VALIDATION = 0x00080000;

        private const int ASC_REQ_SEQUENCE_DETECT = 0x00000008;
        private const int ASC_REQ_REPLAY_DETECT = 0x00000004;
        private const int ASC_REQ_CONFIDENTIALITY = 0x00000010;
        private const int ASC_REQ_ALLOCATE_MEMORY = 0x00000100;
        private const int ASC_REQ_STREAM = 0x00008000;

        private const int SEC_E_OK = 0;
        private const int SEC_I_CONTINUE_NEEDED = 0x00090312;
        private const int SEC_I_CONTEXT_EXPIRED = 0x00090317;
        private const int SEC_E_INCOMPLETE_MESSAGE = unchecked((int)0x80090318);
        private const int SEC_I_RENEGOTIATE = 0x00090321;

        private const int SECBUFFER_VERSION = 0;
        private const int SECBUFFER_EMPTY = 0;
        private const int SECBUFFER_DATA = 1;
        private const int SECBUFFER_TOKEN = 2;
        private const int SECBUFFER_EXTRA = 5;
        private const int SECBUFFER_STREAM_TRAILER = 6;
        private const int SECBUFFER_STREAM_HEADER = 7;

        private const uint SECPKG_ATTR_STREAM_SIZES = 4;
        private const uint SECPKG_ATTR_KEYING_MATERIAL_INFO = 0x6A;
        private const uint SECPKG_ATTR_KEYING_MATERIAL = 0x6B;

        private readonly Stream _inner;
        private readonly bool _isServer;
        private readonly string? _targetHost;
        private CredHandle _cred;
        private CtxtHandle _ctxt;
        private bool _hasCred;
        private bool _hasCtxt;
        private int _postHandshakeMessages;
        private SecPkgContext_StreamSizes _streamSizes;

        private byte[] _recvEncBuf = new byte[32768];
        private int _recvEncLen;
        private byte[] _decryptedBuf = Array.Empty<byte>();
        private int _decryptedPos;
        private int _decryptedLen;
        private bool _remoteEof;
        private bool _disposed;

        private SspiTlsStream(Stream inner, bool isServer, string? targetHost)
        {
            _inner = inner;
            _isServer = isServer;
            _targetHost = targetHost;
        }

        public static async Task<SspiTlsStream> AuthenticateAsClientAsync(
            Stream inner,
            X509Certificate2? clientCert,
            string? targetHost,
            CancellationToken token)
        {
            var tls = new SspiTlsStream(inner, isServer: false, targetHost);
            try
            {
                tls.AcquireCredentials(clientCert, isServer: false);
                await tls.HandshakeClientAsync(token).ConfigureAwait(false);
                tls.QueryStreamSizes();
                return tls;
            }
            catch
            {
                tls.Dispose();
                throw;
            }
        }

        public static async Task<SspiTlsStream> AuthenticateAsServerAsync(
            Stream inner,
            X509Certificate2 serverCert,
            CancellationToken token)
        {
            var tls = new SspiTlsStream(inner, isServer: true, targetHost: null);
            try
            {
                tls.AcquireCredentials(serverCert, isServer: true);
                await tls.HandshakeServerAsync(token).ConfigureAwait(false);
                tls.QueryStreamSizes();
                return tls;
            }
            catch
            {
                tls.Dispose();
                throw;
            }
        }

        public byte[] ExportKeyingMaterial(string label, int length)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_hasCtxt) throw new InvalidOperationException("TLS context is not initialized.");

            byte[] labelBytes = Encoding.ASCII.GetBytes(label + '\0');
            nint labelPtr = Marshal.AllocHGlobal(labelBytes.Length);
            try
            {
                Marshal.Copy(labelBytes, 0, labelPtr, labelBytes.Length);
                var info = new SecPkgContext_KeyingMaterialInfo
                {
                    cbLabel = (ushort)labelBytes.Length,
                    pszLabel = labelPtr,
                    cbContextValue = 0,
                    pbContextValue = 0,
                    cbKeyingMaterial = (uint)length
                };
                int setStatus = SetContextAttributesW(
                    ref _ctxt,
                    SECPKG_ATTR_KEYING_MATERIAL_INFO,
                    ref info,
                    (uint)Marshal.SizeOf<SecPkgContext_KeyingMaterialInfo>());
                if (setStatus != SEC_E_OK)
                    throw new IOException($"SetContextAttributes(SECPKG_ATTR_KEYING_MATERIAL_INFO) failed: 0x{setStatus:X8}");

                var km = new SecPkgContext_KeyingMaterial();
                int queryStatus = QueryContextAttributesW(ref _ctxt, SECPKG_ATTR_KEYING_MATERIAL, ref km);
                if (queryStatus != SEC_E_OK)
                    throw new IOException($"QueryContextAttributes(SECPKG_ATTR_KEYING_MATERIAL) failed: 0x{queryStatus:X8}");

                try
                {
                    if (km.cbKeyingMaterial != (uint)length || km.pbKeyingMaterial == 0)
                        throw new IOException($"Unexpected exported keying material length {km.cbKeyingMaterial}.");
                    byte[] result = new byte[length];
                    Marshal.Copy(km.pbKeyingMaterial, result, 0, length);
                    return result;
                }
                finally
                {
                    if (km.pbKeyingMaterial != 0) _ = FreeContextBuffer(km.pbKeyingMaterial);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(labelPtr);
            }
        }

        private void AcquireCredentials(X509Certificate2? cert, bool isServer)
        {
            nint certPtr = 0;
            nint certArrayPtr = 0;
            if (cert != null)
            {
                certPtr = cert.Handle;
                certArrayPtr = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(certArrayPtr, certPtr);
            }

            try
            {
                var cred = new SCH_CREDENTIALS
                {
                    dwVersion = SCH_CREDENTIALS_VERSION,
                    cCreds = cert != null ? 1u : 0u,
                    paCred = certArrayPtr,
                    // SCH_CREDENTIALS opts into TLS 1.3; no restrictions means OS defaults.
                    dwFlags = isServer
                        ? SCH_CRED_NO_DEFAULT_CREDS | SCH_USE_STRONG_CRYPTO
                        : SCH_CRED_NO_DEFAULT_CREDS | SCH_CRED_MANUAL_CRED_VALIDATION | SCH_CRED_NO_SERVERNAME_CHECK | SCH_USE_STRONG_CRYPTO
                };

                int status = AcquireCredentialsHandleW(
                    null,
                    SchannelPackage,
                    isServer ? SECPKG_CRED_INBOUND : SECPKG_CRED_OUTBOUND,
                    0,
                    ref cred,
                    0,
                    0,
                    out _cred,
                    out _);
                if (status != SEC_E_OK)
                    throw new IOException($"AcquireCredentialsHandleW failed: 0x{status:X8}");
                _hasCred = true;
            }
            finally
            {
                if (certArrayPtr != 0) Marshal.FreeHGlobal(certArrayPtr);
            }
        }

        private async Task HandshakeClientAsync(CancellationToken token)
        {
            int flags = ISC_REQ_SEQUENCE_DETECT | ISC_REQ_REPLAY_DETECT | ISC_REQ_CONFIDENTIALITY
                | ISC_REQ_ALLOCATE_MEMORY | ISC_REQ_STREAM | ISC_REQ_MANUAL_CRED_VALIDATION;

            while (true)
            {
                byte[]? inBytes = _recvEncLen > 0 ? _recvEncBuf[.._recvEncLen] : null;
                // Post-handshake TLS messages must continue the established context.
                int status = InvokeIsc(!_hasCtxt, inBytes, flags, out byte[]? outToken, out int extraLen);

                if (extraLen > 0)
                {
                    int keepStart = _recvEncLen - extraLen;
                    Buffer.BlockCopy(_recvEncBuf, keepStart, _recvEncBuf, 0, extraLen);
                    _recvEncLen = extraLen;
                }
                else if (status != SEC_E_INCOMPLETE_MESSAGE)
                {
                    _recvEncLen = 0;
                }

                if (outToken is { Length: > 0 })
                {
                    await _inner.WriteAsync(outToken, token).ConfigureAwait(false);
                    await _inner.FlushAsync(token).ConfigureAwait(false);
                }

                if (status == SEC_E_OK) break;
                if (status != SEC_I_CONTINUE_NEEDED && status != SEC_E_INCOMPLETE_MESSAGE)
                    throw new IOException($"SChannel client TLS handshake failed: 0x{status:X8}");

                if (status == SEC_E_INCOMPLETE_MESSAGE || _recvEncLen == 0)
                    await ReadMoreEncryptedAsync(token).ConfigureAwait(false);
            }
        }

        private async Task HandshakeServerAsync(CancellationToken token)
        {
            int flags = ASC_REQ_SEQUENCE_DETECT | ASC_REQ_REPLAY_DETECT | ASC_REQ_CONFIDENTIALITY
                | ASC_REQ_ALLOCATE_MEMORY | ASC_REQ_STREAM;

            while (true)
            {
                if (!_hasCtxt && _recvEncLen == 0)
                    await ReadMoreEncryptedAsync(token).ConfigureAwait(false);

                int status = InvokeAsc(!_hasCtxt, _recvEncBuf[.._recvEncLen], flags, out byte[]? outToken, out int extraLen);

                if (extraLen > 0)
                {
                    int keepStart = _recvEncLen - extraLen;
                    Buffer.BlockCopy(_recvEncBuf, keepStart, _recvEncBuf, 0, extraLen);
                    _recvEncLen = extraLen;
                }
                else if (status != SEC_E_INCOMPLETE_MESSAGE)
                {
                    _recvEncLen = 0;
                }

                if (outToken is { Length: > 0 })
                {
                    await _inner.WriteAsync(outToken, token).ConfigureAwait(false);
                    await _inner.FlushAsync(token).ConfigureAwait(false);
                }

                if (status == SEC_E_OK) break;
                if (status != SEC_I_CONTINUE_NEEDED && status != SEC_E_INCOMPLETE_MESSAGE)
                    throw new IOException($"SChannel server TLS handshake failed: 0x{status:X8}");

                if (status == SEC_E_INCOMPLETE_MESSAGE || _recvEncLen == 0)
                    await ReadMoreEncryptedAsync(token).ConfigureAwait(false);
            }
        }

        private int InvokeIsc(bool firstCall, byte[]? input, int flags, out byte[]? outToken, out int extraBytes)
        {
            outToken = null;
            extraBytes = 0;

            GCHandle inHandle = default;
            var inBuffers = new SecBuffer[2];
            var outBuffers = new SecBuffer[1];
            outBuffers[0] = new SecBuffer { cbBuffer = 0, BufferType = SECBUFFER_TOKEN, pvBuffer = 0 };

            try
            {
                if (input is { Length: > 0 })
                {
                    inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
                    inBuffers[0] = new SecBuffer { cbBuffer = input.Length, BufferType = SECBUFFER_TOKEN, pvBuffer = inHandle.AddrOfPinnedObject() };
                    inBuffers[1] = new SecBuffer { cbBuffer = 0, BufferType = SECBUFFER_EMPTY, pvBuffer = 0 };
                }

                var inDesc = input is { Length: > 0 } ? CreateDesc(inBuffers, out var inArr) : default;
                var outDesc = CreateDesc(outBuffers, out var outArr);
                try
                {
                    CtxtHandle newCtxt;
                    int status;
                    if (firstCall)
                    {
                        status = input != null
                            ? InitializeSecurityContextW(ref _cred, 0, _targetHost, flags, 0, 0, ref inDesc, 0, out newCtxt, ref outDesc, out _, out _)
                            : InitializeSecurityContextW(ref _cred, 0, _targetHost, flags, 0, 0, 0, 0, out newCtxt, ref outDesc, out _, out _);
                        if (status >= 0 || newCtxt.dwLower != 0 || newCtxt.dwUpper != 0)
                        {
                            _ctxt = newCtxt;
                            _hasCtxt = true;
                        }
                    }
                    else
                    {
                        status = input != null
                            ? InitializeSecurityContextW(ref _cred, ref _ctxt, _targetHost, flags, 0, 0, ref inDesc, 0, out newCtxt, ref outDesc, out _, out _)
                            : InitializeSecurityContextW(ref _cred, ref _ctxt, _targetHost, flags, 0, 0, 0, 0, out newCtxt, ref outDesc, out _, out _);
                    }

                    ReadDesc(outDesc, outBuffers);
                    if (outBuffers[0].pvBuffer != 0)
                    {
                        if (outBuffers[0].cbBuffer > 0)
                        {
                            outToken = new byte[outBuffers[0].cbBuffer];
                            Marshal.Copy(outBuffers[0].pvBuffer, outToken, 0, outToken.Length);
                        }
                        _ = FreeContextBuffer(outBuffers[0].pvBuffer);
                    }

                    if (input != null)
                    {
                        ReadDesc(inDesc, inBuffers);
                        if (inBuffers[1].BufferType == SECBUFFER_EXTRA)
                            extraBytes = inBuffers[1].cbBuffer;
                    }
                    return status;
                }
                finally
                {
                    FreeDesc(inDesc);
                    FreeDesc(outDesc);
                }
            }
            finally
            {
                if (inHandle.IsAllocated) inHandle.Free();
            }
        }

        private int InvokeAsc(bool firstCall, byte[] input, int flags, out byte[]? outToken, out int extraBytes)
        {
            outToken = null;
            extraBytes = 0;

            var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            var inBuffers = new SecBuffer[2];
            inBuffers[0] = new SecBuffer { cbBuffer = input.Length, BufferType = SECBUFFER_TOKEN, pvBuffer = inHandle.AddrOfPinnedObject() };
            inBuffers[1] = new SecBuffer { cbBuffer = 0, BufferType = SECBUFFER_EMPTY, pvBuffer = 0 };

            var outBuffers = new SecBuffer[1];
            outBuffers[0] = new SecBuffer { cbBuffer = 0, BufferType = SECBUFFER_TOKEN, pvBuffer = 0 };

            var inDesc = CreateDesc(inBuffers, out _);
            var outDesc = CreateDesc(outBuffers, out _);
            try
            {
                int status;
                if (firstCall)
                {
                    status = AcceptSecurityContext(
                        ref _cred,
                        0,
                        ref inDesc,
                        flags,
                        0,
                        out var newCtxt,
                        ref outDesc,
                        out _,
                        out _);
                    if (status >= 0 || newCtxt.dwLower != 0 || newCtxt.dwUpper != 0)
                    {
                        _ctxt = newCtxt;
                        _hasCtxt = true;
                    }
                }
                else
                {
                    status = AcceptSecurityContext(
                        ref _cred,
                        ref _ctxt,
                        ref inDesc,
                        flags,
                        0,
                        out _,
                        ref outDesc,
                        out _,
                        out _);
                }

                ReadDesc(outDesc, outBuffers);
                if (outBuffers[0].pvBuffer != 0)
                {
                    if (outBuffers[0].cbBuffer > 0)
                    {
                        outToken = new byte[outBuffers[0].cbBuffer];
                        Marshal.Copy(outBuffers[0].pvBuffer, outToken, 0, outToken.Length);
                    }
                    _ = FreeContextBuffer(outBuffers[0].pvBuffer);
                }
                ReadDesc(inDesc, inBuffers);
                if (inBuffers[1].BufferType == SECBUFFER_EXTRA)
                    extraBytes = inBuffers[1].cbBuffer;
                return status;
            }
            finally
            {
                FreeDesc(inDesc);
                FreeDesc(outDesc);
                inHandle.Free();
            }
        }

        private void QueryStreamSizes()
        {
            int status = QueryContextAttributesW(ref _ctxt, SECPKG_ATTR_STREAM_SIZES, ref _streamSizes);
            if (status != SEC_E_OK)
                throw new IOException($"QueryContextAttributes(SECPKG_ATTR_STREAM_SIZES) failed: 0x{status:X8}");
        }

        private async Task ReadMoreEncryptedAsync(CancellationToken token)
        {
            if (_recvEncLen == _recvEncBuf.Length)
                Array.Resize(ref _recvEncBuf, _recvEncBuf.Length * 2);
            int n = await _inner.ReadAsync(_recvEncBuf.AsMemory(_recvEncLen), token).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException("TLS transport closed during handshake.");
            _recvEncLen += n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.IsEmpty) return 0;

            while (true)
            {
                if (_decryptedPos < _decryptedLen)
                {
                    int copy = Math.Min(buffer.Length, _decryptedLen - _decryptedPos);
                    _decryptedBuf.AsSpan(_decryptedPos, copy).CopyTo(buffer.Span);
                    _decryptedPos += copy;
                    return copy;
                }
                if (_remoteEof) return 0;

                if (_recvEncLen == 0)
                {
                    int n = await _inner.ReadAsync(_recvEncBuf.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (n <= 0) { _remoteEof = true; return 0; }
                    _recvEncLen = n;
                }

                int status = TryDecrypt(_recvEncBuf, _recvEncLen, out byte[]? plain, out int extraLen);
                if (status == SEC_E_INCOMPLETE_MESSAGE)
                {
                    if (_recvEncLen == _recvEncBuf.Length) Array.Resize(ref _recvEncBuf, _recvEncBuf.Length * 2);
                    int n = await _inner.ReadAsync(_recvEncBuf.AsMemory(_recvEncLen), cancellationToken).ConfigureAwait(false);
                    if (n <= 0) { _remoteEof = true; return 0; }
                    _recvEncLen += n;
                    continue;
                }

                if (extraLen > 0)
                {
                    Buffer.BlockCopy(_recvEncBuf, _recvEncLen - extraLen, _recvEncBuf, 0, extraLen);
                    _recvEncLen = extraLen;
                }
                else
                {
                    _recvEncLen = 0;
                }

                if (status == SEC_I_CONTEXT_EXPIRED)
                {
                    _remoteEof = true;
                    return 0;
                }

                if (status == SEC_I_RENEGOTIATE)
                {
                    _postHandshakeMessages++;
                    // SChannel emits SEC_I_RENEGOTIATE for TLS 1.3 post-handshake messages such as NewSessionTicket.
                    if (_isServer) await HandshakeServerAsync(cancellationToken).ConfigureAwait(false);
                    else await HandshakeClientAsync(cancellationToken).ConfigureAwait(false);
                    QueryStreamSizes();
                    continue;
                }

                if (status != SEC_E_OK)
                    throw new IOException($"SChannel DecryptMessage failed: 0x{status:X8}");

                if (plain is { Length: > 0 })
                {
                    _decryptedBuf = plain;
                    _decryptedPos = 0;
                    _decryptedLen = plain.Length;
                }
            }
        }

        private int TryDecrypt(byte[] encBuf, int encLen, out byte[]? plain, out int extraLen)
        {
            plain = null;
            extraLen = 0;

            var handle = GCHandle.Alloc(encBuf, GCHandleType.Pinned);
            var buffers = new SecBuffer[4];
            buffers[0] = new SecBuffer { cbBuffer = encLen, BufferType = SECBUFFER_DATA, pvBuffer = handle.AddrOfPinnedObject() };
            buffers[1] = new SecBuffer { BufferType = SECBUFFER_EMPTY };
            buffers[2] = new SecBuffer { BufferType = SECBUFFER_EMPTY };
            buffers[3] = new SecBuffer { BufferType = SECBUFFER_EMPTY };

            var desc = CreateDesc(buffers, out _);
            try
            {
                int status = DecryptMessage(ref _ctxt, ref desc, 0, out _);
                ReadDesc(desc, buffers);
                foreach (var buf in buffers)
                {
                    if (buf.BufferType == SECBUFFER_DATA && buf.cbBuffer > 0 && buf.pvBuffer != 0)
                    {
                        plain = new byte[buf.cbBuffer];
                        Marshal.Copy(buf.pvBuffer, plain, 0, plain.Length);
                    }
                    else if (buf.BufferType == SECBUFFER_EXTRA)
                    {
                        extraLen = buf.cbBuffer;
                    }
                }
                return status;
            }
            finally
            {
                FreeDesc(desc);
                handle.Free();
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int maxChunk = (int)Math.Min(_streamSizes.cbMaximumMessage > 0 ? _streamSizes.cbMaximumMessage : 16384u, int.MaxValue);
            int offset = 0;
            while (offset < buffer.Length)
            {
                int chunk = Math.Min(buffer.Length - offset, maxChunk);
                byte[] frame = new byte[_streamSizes.cbHeader + chunk + _streamSizes.cbTrailer];
                buffer.Span.Slice(offset, chunk).CopyTo(frame.AsSpan((int)_streamSizes.cbHeader));

                int encryptedLen = EncryptChunk(frame, chunk);
                await _inner.WriteAsync(frame.AsMemory(0, encryptedLen), cancellationToken).ConfigureAwait(false);
                offset += chunk;
            }
        }

        private int EncryptChunk(byte[] frame, int dataLen)
        {
            var handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
            nint ptr = handle.AddrOfPinnedObject();
            var buffers = new SecBuffer[4];
            buffers[0] = new SecBuffer { cbBuffer = (int)_streamSizes.cbHeader, BufferType = SECBUFFER_STREAM_HEADER, pvBuffer = ptr };
            buffers[1] = new SecBuffer { cbBuffer = dataLen, BufferType = SECBUFFER_DATA, pvBuffer = ptr + (int)_streamSizes.cbHeader };
            buffers[2] = new SecBuffer { cbBuffer = (int)_streamSizes.cbTrailer, BufferType = SECBUFFER_STREAM_TRAILER, pvBuffer = ptr + (int)_streamSizes.cbHeader + dataLen };
            buffers[3] = new SecBuffer { BufferType = SECBUFFER_EMPTY };

            var desc = CreateDesc(buffers, out _);
            try
            {
                int status = EncryptMessage(ref _ctxt, 0, ref desc, 0);
                if (status != SEC_E_OK)
                    throw new IOException($"SChannel EncryptMessage failed: 0x{status:X8}");
                ReadDesc(desc, buffers);
                return buffers[0].cbBuffer + buffers[1].cbBuffer + buffers[2].cbBuffer;
            }
            finally
            {
                FreeDesc(desc);
                handle.Free();
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() => _inner.Flush();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_hasCtxt) { _ = DeleteSecurityContext(ref _ctxt); _hasCtxt = false; }
                if (_hasCred) { _ = FreeCredentialsHandle(ref _cred); _hasCred = false; }
            }
            base.Dispose(disposing);
        }

        private static SecBufferDesc CreateDesc(SecBuffer[] buffers, out GCHandle? _)
        {
            _ = null;
            int sz = Marshal.SizeOf<SecBuffer>();
            nint p = Marshal.AllocHGlobal(sz * buffers.Length);
            for (int i = 0; i < buffers.Length; i++)
                Marshal.StructureToPtr(buffers[i], p + i * sz, false);
            return new SecBufferDesc { ulVersion = SECBUFFER_VERSION, cBuffers = buffers.Length, pBuffers = p };
        }

        private static void ReadDesc(in SecBufferDesc desc, SecBuffer[] buffers)
        {
            int sz = Marshal.SizeOf<SecBuffer>();
            for (int i = 0; i < buffers.Length; i++)
                buffers[i] = Marshal.PtrToStructure<SecBuffer>(desc.pBuffers + i * sz);
        }

        private static void FreeDesc(in SecBufferDesc desc)
        {
            if (desc.pBuffers != 0) Marshal.FreeHGlobal(desc.pBuffers);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CredHandle { public nint dwLower; public nint dwUpper; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CtxtHandle { public nint dwLower; public nint dwUpper; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecBuffer { public int cbBuffer; public int BufferType; public nint pvBuffer; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecBufferDesc { public int ulVersion; public int cBuffers; public nint pBuffers; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SCH_CREDENTIALS
        {
            public uint dwVersion;
            public uint dwCredFormat;
            public uint cCreds;
            public nint paCred;
            public nint hRootStore;
            public uint cMappers;
            public nint aphMappers;
            public uint dwSessionLifespan;
            public uint dwFlags;
            public uint cTlsParameters;
            public nint pTlsParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecPkgContext_StreamSizes
        {
            public uint cbHeader;
            public uint cbTrailer;
            public uint cbMaximumMessage;
            public uint cBuffers;
            public uint cbBlockSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecPkgContext_KeyingMaterialInfo
        {
            public ushort cbLabel;
            public nint pszLabel;
            public ushort cbContextValue;
            public nint pbContextValue;
            public uint cbKeyingMaterial;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecPkgContext_KeyingMaterial
        {
            public uint cbKeyingMaterial;
            public nint pbKeyingMaterial;
        }

        [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
        private static extern int AcquireCredentialsHandleW(
            string? pszPrincipal,
            string pszPackage,
            int fCredentialUse,
            nint pvLogonId,
            ref SCH_CREDENTIALS pAuthData,
            nint pGetKeyFn,
            nint pvGetKeyArgument,
            out CredHandle phCredential,
            out long ptsExpiry);

        [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
        private static extern int InitializeSecurityContextW(
            ref CredHandle phCredential,
            nint phContext,
            string? pszTargetName,
            int fContextReq,
            int Reserved1,
            int TargetDataRep,
            nint pInput,
            int Reserved2,
            out CtxtHandle phNewContext,
            ref SecBufferDesc pOutput,
            out int pfContextAttr,
            out long ptsExpiry);

        [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
        private static extern int InitializeSecurityContextW(
            ref CredHandle phCredential,
            nint phContext,
            string? pszTargetName,
            int fContextReq,
            int Reserved1,
            int TargetDataRep,
            ref SecBufferDesc pInput,
            int Reserved2,
            out CtxtHandle phNewContext,
            ref SecBufferDesc pOutput,
            out int pfContextAttr,
            out long ptsExpiry);

        [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
        private static extern int InitializeSecurityContextW(
            ref CredHandle phCredential,
            ref CtxtHandle phContext,
            string? pszTargetName,
            int fContextReq,
            int Reserved1,
            int TargetDataRep,
            ref SecBufferDesc pInput,
            int Reserved2,
            out CtxtHandle phNewContext,
            ref SecBufferDesc pOutput,
            out int pfContextAttr,
            out long ptsExpiry);

        [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
        private static extern int InitializeSecurityContextW(
            ref CredHandle phCredential,
            ref CtxtHandle phContext,
            string? pszTargetName,
            int fContextReq,
            int Reserved1,
            int TargetDataRep,
            nint pInput,
            int Reserved2,
            out CtxtHandle phNewContext,
            ref SecBufferDesc pOutput,
            out int pfContextAttr,
            out long ptsExpiry);

        [DllImport("secur32.dll")]
        private static extern int AcceptSecurityContext(
            ref CredHandle phCredential,
            nint phContext,
            ref SecBufferDesc pInput,
            int fContextReq,
            int TargetDataRep,
            out CtxtHandle phNewContext,
            ref SecBufferDesc pOutput,
            out int pfContextAttr,
            out long ptsExpiry);

        [DllImport("secur32.dll")]
        private static extern int AcceptSecurityContext(
            ref CredHandle phCredential,
            ref CtxtHandle phContext,
            ref SecBufferDesc pInput,
            int fContextReq,
            int TargetDataRep,
            out CtxtHandle phNewContext,
            ref SecBufferDesc pOutput,
            out int pfContextAttr,
            out long ptsExpiry);

        [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetContextAttributesW(
            ref CtxtHandle phContext,
            uint ulAttribute,
            ref SecPkgContext_KeyingMaterialInfo pBuffer,
            uint cbBuffer);

        [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
        private static extern int QueryContextAttributesW(
            ref CtxtHandle phContext,
            uint ulAttribute,
            ref SecPkgContext_StreamSizes pBuffer);

        [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
        private static extern int QueryContextAttributesW(
            ref CtxtHandle phContext,
            uint ulAttribute,
            ref SecPkgContext_KeyingMaterial pBuffer);

        [DllImport("secur32.dll")]
        private static extern int EncryptMessage(ref CtxtHandle phContext, int fQOP, ref SecBufferDesc pMessage, uint MessageSeqNo);

        [DllImport("secur32.dll")]
        private static extern int DecryptMessage(ref CtxtHandle phContext, ref SecBufferDesc pMessage, uint MessageSeqNo, out uint pfQOP);

        [DllImport("secur32.dll")]
        private static extern int FreeContextBuffer(nint pvContextBuffer);

        [DllImport("secur32.dll")]
        private static extern int DeleteSecurityContext(ref CtxtHandle phContext);

        [DllImport("secur32.dll")]
        private static extern int FreeCredentialsHandle(ref CredHandle phCredential);
    }
}

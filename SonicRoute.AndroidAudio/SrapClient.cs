using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SonicRoute.AndroidAudio;

internal sealed class SrapException(string code, string? wireReason = null) : IOException("SonicRoute: " + code)
{
    internal string Code { get; } = code;
    internal string? WireReason { get; } = wireReason;
    internal bool PeerRevoked => WireReason == "peer_revoked";
}

// A per-profile-directory named CNG key survives restarts without exporting any private material.
internal sealed class SrapIdentity : IDisposable
{
    private readonly ECDsaCng signer;
    internal X509Certificate2 Certificate { get; }
    internal byte[] PublicKey { get; }
    internal byte[] Fingerprint { get; }

    internal SrapIdentity(string dataDirectory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant();
        string name = "SonicRoute.SRAP." + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root)));
        var provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
        using var key = OpenOrCreate();
        signer = new(key);
        try
        {
            PublicKey = signer.ExportSubjectPublicKeyInfo();
            Fingerprint = SHA256.HashData(PublicKey);
            var request = new CertificateRequest("CN=SonicRoute Windows", signer, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
            var now = DateTimeOffset.UtcNow;
            Certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(2));
        }
        catch { signer.Dispose(); throw; }

        CngKey OpenOrCreate()
        {
            if (CngKey.Exists(name, provider)) return CngKey.Open(name, provider);
            try
            {
                return CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
                {
                    Provider = provider, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing
                });
            }
            catch (CryptographicException) when (CngKey.Exists(name, provider)) { return CngKey.Open(name, provider); }
        }
    }

    internal byte[] Sign(byte[] transcript) => signer.SignData(transcript, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public void Dispose()
    {
        Certificate.Dispose();
        signer.Dispose();
    }
}

internal sealed partial class SrapClient(SrapIdentity identity, SslProtocols versions = SslProtocols.Tls12 | SslProtocols.Tls13)
{
    internal async Task<SrapSession> ConnectAsync(IPEndPoint endpoint, string fingerprint, CancellationToken token, int major = 1)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        var tls = await OpenAsync(endpoint, fingerprint, mutual: true, deadline.Token).ConfigureAwait(false);
        try
        {
            int minor = await HelloAsync(tls, major, deadline.Token).ConfigureAwait(false);
            var initial = await ReadAsync(tls, deadline.Token).ConfigureAwait(false);
            if (Text(initial, "type") != "capabilities" || Text(initial, "session") is not { Length: 32 } session
                || session.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f')))
                throw new SrapException("invalid_argument");
            return new(tls, session, SrapProtocol.ReadCapabilities(Body(initial)), minor);
        }
        catch (InvalidDataException)
        {
            tls.Dispose();
            throw new SrapException("invalid_argument");
        }
        catch { tls.Dispose(); throw; }
    }

    internal async Task<SrapSession> PairAsync(IPEndPoint endpoint, SrapInvite invite, string name, Action? pending, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(123));
        bool proofSent = false;
        try
        {
            using var start = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            start.CancelAfter(TimeSpan.FromSeconds(5));
            using var tls = await OpenAsync(endpoint, invite.Fingerprint, mutual: false, start.Token).ConfigureAwait(false);
            int minor = await HelloAsync(tls, 1, start.Token).ConfigureAwait(false);
            string inviteId = SrapInvite.Encode(invite.Id);
            string begin = RequestId();
            await WriteAsync(tls, "pair.begin", new() { ["inviteId"] = inviteId }, start.Token, begin).ConfigureAwait(false);
            var challenge = await ReadAsync(tls, start.Token).ConfigureAwait(false);
            Match(challenge, begin);
            var body = Body(challenge);
            if (Text(challenge, "type") == "pair.result") throw Error(body);
            if (Text(challenge, "type") != "pair.challenge" || Text(body, "inviteId") != inviteId)
                throw new SrapException("invalid_argument");
            byte[] transcript = PairTranscript(invite.Id, SrapInvite.Decode(Text(body, "challenge"), 32, 32),
                Convert.FromHexString(invite.Fingerprint), identity.Fingerprint, minor);
            string prove = RequestId();
            var signature = identity.Sign(transcript);
            proofSent = true;
            await WriteAsync(tls, "pair.prove", new()
            {
                ["inviteId"] = inviteId, ["secret"] = SrapInvite.Encode(invite.Secret), ["publicKey"] = SrapInvite.Encode(identity.PublicKey),
                ["signature"] = SrapInvite.Encode(signature), ["displayName"] = name.Length > 64 ? name[..64] : name
            }, deadline.Token, prove, sensitive: true).ConfigureAwait(false);
            var result = await PairResultAsync(tls, prove, deadline.Token).ConfigureAwait(false);
            if (result == "pending")
            {
                pending?.Invoke();
                result = await PairResultAsync(tls, prove, deadline.Token).ConfigureAwait(false);
            }
            if (result != "done") throw new SrapException("invalid_argument");
        }
        catch (IOException ex) when (ex is not SrapException && proofSent && !token.IsCancellationRequested)
        {
            // An authenticated session using the same keys recovers a committed pair
            // after a lost receipt. Do not submit a new proof or reuse the invitation.
            return await ConnectAsync(endpoint, invite.Fingerprint, token).ConfigureAwait(false);
        }
        return await ConnectAsync(endpoint, invite.Fingerprint, token).ConfigureAwait(false);
    }

    internal Task<SslStream> OpenAliasAsync(SrapAdbForward forward, string pin, CancellationToken token) =>
        OpenAsync(forward.Endpoint, pin, true, token, forward.Client);

    private Task<SslStream> OpenAsync(IPEndPoint endpoint, string pin, bool mutual, CancellationToken token, Socket? connected = null)
    {
        if (!SrapInvite.IsFingerprint(pin)) throw new SrapException("identity_conflict");
        byte[] expected = Convert.FromHexString(pin);
        return OpenTlsAsync(endpoint, mutual, fp => CryptographicOperations.FixedTimeEquals(expected, fp), token, connected);
    }

    private async Task<SslStream> OpenTlsAsync(IPEndPoint endpoint, bool mutual, Func<byte[], bool> acceptFingerprint,
        CancellationToken token, Socket? connected = null)
    {
        var tcp = connected ?? new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        SslStream? tls = null;
        try
        {
            if (connected == null) await tcp.ConnectAsync(endpoint, token).ConfigureAwait(false);
            else if (!IPAddress.Loopback.Equals(endpoint.Address) || !endpoint.Equals(tcp.RemoteEndPoint))
                throw new SrapException("identity_conflict");
            if (mutual && connected == null)
            {
                tcp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                tcp.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
                tcp.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                tcp.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            }
            tls = new SslStream(new NetworkStream(tcp, ownsSocket: true));
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = endpoint.Address.ToString(), EnabledSslProtocols = versions, AllowRenegotiation = false,
                AllowTlsResume = false, CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                CertificateChainPolicy = new X509ChainPolicy { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck },
                RemoteCertificateValidationCallback = (_, cert, _, _) => AcceptLeaf(cert)
            };
            if (mutual)
                options.LocalCertificateSelectionCallback = (_, _, _, remote, _) => AcceptLeaf(remote) ? identity.Certificate : null!;
            await tls.AuthenticateAsClientAsync(options, token).ConfigureAwait(false);
            if (mutual && !tls.IsMutuallyAuthenticated) throw new AuthenticationException("SRAP mutual authentication failed.");
            if (!IsAllowedSuite(tls.SslProtocol, tls.NegotiatedCipherSuite))
                throw new AuthenticationException("SRAP TLS suite is outside the allowlist.");
            return tls;
        }
        catch
        {
            if (tls != null) tls.Dispose(); else tcp.Dispose();
            throw;
        }

        bool AcceptLeaf(X509Certificate? certificate)
        {
            if (certificate is not X509Certificate2 leaf) return false;
            var now = DateTime.UtcNow;
            using var key = leaf.GetECDsaPublicKey();
            return key?.KeySize == 256 && leaf.NotBefore.ToUniversalTime() <= now && leaf.NotAfter.ToUniversalTime() >= now
                && key.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7"
                && acceptFingerprint(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        }
    }

    internal static bool IsAllowedSuite(SslProtocols version, TlsCipherSuite suite) => version switch
    {
        SslProtocols.Tls13 => suite is TlsCipherSuite.TLS_AES_128_GCM_SHA256 or TlsCipherSuite.TLS_AES_256_GCM_SHA384
            or TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        SslProtocols.Tls12 => suite is TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256
            or TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384 or TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        _ => false
    };

    internal static async Task<int> HelloAsync(SslStream tls, int major, CancellationToken token)
    {
        string id = RequestId();
        await WriteAsync(tls, "hello", new()
        {
            ["protocol"] = SrapProtocol.Name, ["major"] = major, ["minorMin"] = 0, ["minorMax"] = 0
        }, token, id).ConfigureAwait(false);
        var response = await ReadAsync(tls, token).ConfigureAwait(false);
        Match(response, id);
        return Text(response, "type") == "hello" && SrapProtocol.NegotiateMinor(Body(response), major, 0, 0) is { } minor
            ? minor : throw new SrapException("version_mismatch");
    }

    private static async Task<string> PairResultAsync(SslStream tls, string id, CancellationToken token)
    {
        var response = await ReadAsync(tls, token).ConfigureAwait(false);
        Match(response, id);
        var body = Body(response);
        if (Text(response, "type") != "pair.result") throw new SrapException("invalid_argument");
        return Text(body, "status") switch { "done" => "done", "pending" => "pending", "confirmed" => "confirmed",
            "error" => throw Error(body), _ => throw new SrapException("invalid_argument") };
    }

    private static void Match(JsonElement response, string id)
    {
        if (Text(response, "id") != id || response.TryGetProperty("session", out _)) throw new SrapException("invalid_argument");
    }

    internal static byte[] PairTranscript(byte[] invite, byte[] challenge, byte[] phone, byte[] pc, int minor)
    {
        byte[] bytes = new byte[127];
        "SRAP1/PAIR\0"u8.CopyTo(bytes);
        invite.CopyTo(bytes, 11);
        challenge.CopyTo(bytes, 27);
        phone.CopyTo(bytes, 59);
        pc.CopyTo(bytes, 91);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(123), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(125), (ushort)minor);
        return bytes;
    }

    internal static string RequestId() => Guid.NewGuid().ToString("D");
    internal static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    internal static JsonElement Body(JsonElement root) => root.GetProperty("body");
    internal static SrapException Error(JsonElement body) => new(Text(body, "error") switch
    {
        "invalid_argument" or "version_mismatch" or "not_paired" or "permission_required" or "permission_lost"
        or "identity_conflict" or "stale_session" or "busy" or "timeout" or "canceled" or "unsupported"
        or "not_implemented" or "target_unavailable" or "io_failure" => Text(body, "error")!,
        _ => "io_failure"
    }, Text(body, "reason") is { Length: <= 128 } reason ? reason : null);

    internal static async Task<JsonElement> ReadAsync(Stream stream, CancellationToken token, bool sessionError = true)
    {
        var frame = await SrapProtocol.ReadAsync(stream, token).ConfigureAwait(false) ?? throw new EndOfStreamException();
        using var doc = SrapProtocol.ParseControl(frame);
        var root = doc.RootElement;
        if (sessionError && Text(root, "type") == "event" && Text(Body(root), "name") == "session.error") throw Error(Body(root));
        return root.Clone();
    }

    internal static async Task WriteAsync(Stream stream, string type, JsonObject body, CancellationToken token,
        string? id = null, string? session = null, bool sensitive = false)
    {
        var writerBuffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(writerBuffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            if (id != null) writer.WriteString("id", id);
            if (session != null) writer.WriteString("session", session);
            writer.WritePropertyName("body");
            body.WriteTo(writer);
            writer.WriteEndObject();
        }
        var payload = writerBuffer.WrittenSpan.ToArray();
        var frame = SrapProtocol.Encode(new(SrapFrameType.Control, 0, payload));
        try { await stream.WriteAsync(frame, token).ConfigureAwait(false); }
        finally
        {
            if (sensitive)
            {
                CryptographicOperations.ZeroMemory(payload);
                CryptographicOperations.ZeroMemory(frame);
                writerBuffer.Clear();
            }
        }
    }
}

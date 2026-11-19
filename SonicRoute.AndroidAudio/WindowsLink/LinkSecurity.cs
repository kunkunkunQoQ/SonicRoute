using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Net.Security;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SonicRoute.Core;

namespace SonicRoute.WindowsLink;

internal sealed record LinkHello(string Name, int Port, LinkCapabilities Capabilities, bool Trusted, byte[] Commitment);
internal sealed record LinkSecure(SslStream Stream, string Id, LinkHello Hello);
internal sealed class LinkDuplicatePairingException() : IOException("Another connection is already pairing this Windows Link peer.");

internal static class LinkSecurity
{
    private static readonly SslApplicationProtocol Protocol = new("sonicroute-link/1");
    private const int HelloLimit = 1024;
    private const int NonceSize = 32;
    private const int ReconnectHeaderSize = 48;
    private const int ReconnectSignatureSize = 256;
    private const int ReconnectCertificateLimit = 1024;

    internal static X509Certificate2 LoadIdentity(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        string path = Path.Combine(stateDirectory, "identity.dpapi");
        byte[] pkcs12;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 16 * 1024)
                throw new InvalidDataException("Invalid Windows Link identity file.");
            pkcs12 = Protect(File.ReadAllBytes(path), encrypt: false);
        }
        else
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=SonicRoute Windows Link", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, false));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(15));
            pkcs12 = certificate.Export(X509ContentType.Pkcs12);
            try { AtomicFile.WriteAllBytes(path, Protect(pkcs12, encrypt: true)); }
            catch { CryptographicOperations.ZeroMemory(pkcs12); throw; }
        }

        try { return X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pkcs12); }
    }

    internal static string Id(X509Certificate certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    internal static bool IsId(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    internal static byte[] CreateReconnectRequest(X509Certificate2 identity, string targetId, int port)
    {
        byte[] certificate = identity.RawData;
        byte[] packet = new byte[ReconnectHeaderSize + ReconnectSignatureSize + certificate.Length];
        "SRWL"u8.CopyTo(packet);
        packet[4] = 1;
        packet[5] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), (ushort)port);
        Convert.FromHexString(targetId).CopyTo(packet, 8);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(40), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var key = identity.GetRSAPrivateKey()!;
        key.SignData(packet.AsSpan(0, ReconnectHeaderSize), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CopyTo(packet, ReconnectHeaderSize);
        certificate.CopyTo(packet, ReconnectHeaderSize + ReconnectSignatureSize);
        return packet;
    }

    internal static bool TryReadReconnectRequest(ReadOnlySpan<byte> packet, string localId, IPAddress address,
        Func<string, bool> trusted, out LinkReconnectRequest? request)
    {
        request = null;
        const int certificateOffset = ReconnectHeaderSize + ReconnectSignatureSize;
        if (packet.Length <= certificateOffset || packet.Length > certificateOffset + ReconnectCertificateLimit
            || !packet[..4].SequenceEqual("SRWL"u8) || packet[4] != 1 || packet[5] != 3
            || Convert.ToHexString(packet.Slice(8, 32)) != localId)
            return false;
        long issued = BinaryPrimitives.ReadInt64LittleEndian(packet[40..]);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int port = BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);
        if (port == 0 || issued < now - 60_000 || issued > now + 60_000) return false;
        var der = packet[certificateOffset..];
        string id = Convert.ToHexString(SHA256.HashData(der));
        if (!trusted(id)) return false;
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            using var key = certificate.GetRSAPublicKey();
            if (key == null || !key.VerifyData(packet[..ReconnectHeaderSize], packet.Slice(ReconnectHeaderSize, ReconnectSignatureSize),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return false;
        }
        catch (CryptographicException) { return false; }
        request = new LinkReconnectRequest(id, new IPEndPoint(address, port), issued);
        return true;
    }

    internal static async Task<LinkSecure> AuthenticateAsync(
        TcpClient socket, bool incoming, X509Certificate2 identity, string? expectedId, string name, int port,
        LinkCapabilities capabilities, Func<string, bool> trusted, Func<string, LinkHello, bool> allowPairing,
        Func<string, string, string, CancellationToken, Task<bool>> confirm,
        CancellationToken token)
    {
        string localId = Id(identity);
        string? remoteId = null;
        var stream = new SslStream(socket.GetStream(), false, (_, certificate, _, _) =>
        {
            if (certificate == null) return false;
            using var leaf = new X509Certificate2(certificate);
            if (DateTime.UtcNow < leaf.NotBefore.ToUniversalTime() || DateTime.UtcNow > leaf.NotAfter.ToUniversalTime()) return false;
            remoteId = Id(leaf);
            return !localId.Equals(remoteId, StringComparison.Ordinal)
                && (expectedId == null || expectedId.Equals(remoteId, StringComparison.OrdinalIgnoreCase));
        });

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            CancellationToken stage = deadline.Token;
            if (incoming)
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = identity,
                    ClientCertificateRequired = true,
                    ApplicationProtocols = [Protocol],
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    AllowRenegotiation = false
                }, stage).ConfigureAwait(false);
            else
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "SonicRoute Windows Link",
                    ClientCertificates = new X509CertificateCollection { identity },
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => identity,
                    ApplicationProtocols = [Protocol],
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    AllowRenegotiation = false
                }, stage).ConfigureAwait(false);

            if (remoteId == null || stream.NegotiatedApplicationProtocol != Protocol)
                throw new AuthenticationException("Windows Link identity or protocol mismatch.");

            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            bool knowsPeer = trusted(remoteId);
            var hello = new LinkHello(name, port, capabilities, knowsPeer, SHA256.HashData(nonce));
            await WriteHelloAsync(stream, hello, stage).ConfigureAwait(false);
            LinkHello remote = await ReadHelloAsync(stream, stage).ConfigureAwait(false);
            if (!knowsPeer || !remote.Trusted)
            {
                bool allowed = allowPairing(remoteId, remote);
                await stream.WriteAsync(new byte[] { allowed ? (byte)1 : (byte)0 }, stage).ConfigureAwait(false);
                byte[] decision = new byte[1];
                await stream.ReadExactlyAsync(decision, stage).ConfigureAwait(false);
                if (!allowed || decision[0] != 1) throw new LinkDuplicatePairingException();
                await stream.WriteAsync(nonce, stage).ConfigureAwait(false);
                byte[] remoteNonce = new byte[NonceSize];
                await stream.ReadExactlyAsync(remoteNonce, stage).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(remoteNonce), remote.Commitment))
                    throw new AuthenticationException("The Windows Link pairing proof is invalid.");

                byte[] proof = new byte[128];
                bool localFirst = string.CompareOrdinal(localId, remoteId) < 0;
                Convert.FromHexString(localFirst ? localId : remoteId).CopyTo(proof, 0);
                Convert.FromHexString(localFirst ? remoteId : localId).CopyTo(proof, 32);
                (localFirst ? nonce : remoteNonce).CopyTo(proof, 64);
                (localFirst ? remoteNonce : nonce).CopyTo(proof, 96);
                string code = (BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(proof)) % 100_000_000)
                    .ToString("D8", CultureInfo.InvariantCulture);
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                bool accepted = await confirm(remoteId, remote.Name, code, stage).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, stage).ConfigureAwait(false);
                byte[] confirmation = new byte[1];
                await stream.ReadExactlyAsync(confirmation, stage).ConfigureAwait(false);
                if (!accepted || confirmation[0] != 1)
                    throw new AuthenticationException("Windows Link pairing was declined.");
            }
            return new LinkSecure(stream, remoteId, remote);
        }
        catch { stream.Dispose(); throw; }
    }

    private static async Task WriteHelloAsync(SslStream stream, LinkHello hello, CancellationToken token)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(hello);
        byte[] packet = new byte[body.Length + 3];
        packet[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1), (ushort)body.Length);
        body.CopyTo(packet, 3);
        await stream.WriteAsync(packet, token).ConfigureAwait(false);
    }

    private static async Task<LinkHello> ReadHelloAsync(SslStream stream, CancellationToken token)
    {
        byte[] header = new byte[3];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
        if (header[0] != 1 || length is < 1 or > HelloLimit)
            throw new InvalidDataException("Unsupported Windows Link hello.");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        var hello = JsonSerializer.Deserialize<LinkHello>(body);
        if (hello == null || string.IsNullOrWhiteSpace(hello.Name) || hello.Name.Length > 64
            || hello.Name.Any(char.IsControl) || hello.Port is < 1 or > 65535 || hello.Commitment?.Length != NonceSize)
            throw new InvalidDataException("Invalid Windows Link peer details.");
        return hello;
    }

    private static unsafe byte[] Protect(byte[] input, bool encrypt)
    {
        fixed (byte* bytes = input)
        {
            var source = new DataBlob { Length = input.Length, Pointer = (nint)bytes };
            bool success = encrypt
                ? CryptProtectData(in source, 0, 0, 0, 0, 1, out var destination)
                : CryptUnprotectData(in source, 0, 0, 0, 0, 1, out destination);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return new ReadOnlySpan<byte>((void*)destination.Pointer, destination.Length).ToArray(); }
            finally
            {
                if (!encrypt) CryptographicOperations.ZeroMemory(new Span<byte>((void*)destination.Pointer, destination.Length));
                LocalFree(destination.Pointer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { internal int Length; internal nint Pointer; }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(in DataBlob input, nint description, nint entropy, nint reserved,
        nint prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(in DataBlob input, nint description, nint entropy, nint reserved,
        nint prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}

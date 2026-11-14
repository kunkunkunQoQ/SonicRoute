using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SonicRoute.Core;

namespace SonicRoute.AndroidAudio;

/// <summary>Loads or creates standard ~/.android/adbkey (PKCS#8 PEM) and AOSP RSAPublicKey (524B) keys for direct ADB authentication and TLS 1.3.</summary>
internal sealed class AdbKeyStore : IDisposable
{
    private const int ModulusBytes = 256;
    private const int ModulusWords = ModulusBytes / 4; // 64
    public const int AospPublicKeyBytes = 4 + 4 + ModulusBytes + ModulusBytes + 4; // 524

    private readonly string _directory;
    private readonly object _gate = new();
    private RSA? _rsa;
    private string? _publicKeyLine;
    private byte[]? _aospPublicKey;
    private X509Certificate2? _tlsCert;
    private bool _disposed;

    public AdbKeyStore(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android")
            : Path.GetFullPath(directory);
    }

    public string DirectoryPath => _directory;
    public string PrivateKeyPath => Path.Combine(_directory, "adbkey");
    public string PublicKeyPath => Path.Combine(_directory, "adbkey.pub");

    public byte[] SignAuthToken(ReadOnlySpan<byte> token20Bytes)
    {
        if (token20Bytes.Length != 20)
            throw new ArgumentException("ADB AUTH token must be 20 bytes.", nameof(token20Bytes));
        EnsureLoaded();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // AOSP adb signs the 20-byte AUTH token directly with NID_sha1 DigestInfo (RSA_sign(NID_sha1, token, 20, ...)), NOT SignData(SHA1).
            return _rsa!.SignHash(token20Bytes, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        }
    }

    public string GetPublicKeyLine()
    {
        EnsureLoaded();
        lock (_gate) return _publicKeyLine!;
    }

    public byte[] GetAospPublicKeyBlob()
    {
        EnsureLoaded();
        lock (_gate) return (byte[])_aospPublicKey!.Clone();
    }

    public string GetFingerprint() => AndroidWirelessConnection.PairingFingerprint(GetPublicKeyLine());

    public X509Certificate2 GetTlsClientCertificate()
    {
        EnsureLoaded();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tlsCert != null) return _tlsCert;
            var request = new CertificateRequest(
                "CN=SonicRoute-ADB-Client",
                _rsa!,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, critical: false)); // Client Authentication
            using var ephemeral = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(10));
            byte[] pfx = ephemeral.Export(X509ContentType.Pfx);
            // Exportable | UserKeySet ensures Schannel / NCrypt can access the private key during TLS 1.3 handshake cleanly.
            // Try EphemeralKeySet first if caller checks SslStream; Windows SChannel requires non-ephemeral or CNG-backed key for some SSPI paths,
            // so we keep the X509Certificate2 cached per AdbKeyStore lifetime and dispose it on Dispose().
            _tlsCert = X509CertificateLoader.LoadPkcs12(pfx, ReadOnlySpan<char>.Empty, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
            return _tlsCert;
        }
    }

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_rsa != null) return;

            if (File.Exists(PrivateKeyPath))
            {
                string pem = File.ReadAllText(PrivateKeyPath, Encoding.UTF8);
                var rsa = RSA.Create();
                try
                {
                    rsa.ImportFromPem(pem);
                    if (rsa.KeySize != 2048)
                        throw new CryptographicException($"Unsupported ADB RSA key size {rsa.KeySize}; expected 2048.");
                    byte[] aosp = EncodeAospPublicKey(rsa);
                    string line = BuildPublicKeyLine(aosp, PublicKeyPath);
                    if (!File.Exists(PublicKeyPath))
                    {
                        Directory.CreateDirectory(_directory);
                        AtomicFile.WriteAllText(PublicKeyPath, line + "\n");
                    }
                    _rsa = rsa;
                    _aospPublicKey = aosp;
                    _publicKeyLine = line;
                    return;
                }
                catch
                {
                    rsa.Dispose();
                    throw;
                }
            }

            Directory.CreateDirectory(_directory);
            var created = RSA.Create(2048);
            try
            {
                byte[] aosp = EncodeAospPublicKey(created);
                string pem = created.ExportPkcs8PrivateKeyPem().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
                string comment = (Environment.UserName + "@" + Environment.MachineName).Replace(' ', '_');
                string line = Convert.ToBase64String(aosp) + " " + comment;
                AtomicFile.WriteAllText(PublicKeyPath, line + "\n");
                AtomicFile.WriteAllText(PrivateKeyPath, pem);
                _rsa = created;
                _aospPublicKey = aosp;
                _publicKeyLine = line;
            }
            catch
            {
                created.Dispose();
                throw;
            }
        }
    }

    private static string BuildPublicKeyLine(byte[] aospBlob, string publicKeyPath)
    {
        if (File.Exists(publicKeyPath))
        {
            string existing = File.ReadAllText(publicKeyPath, Encoding.UTF8).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                string[] parts = existing.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && string.Equals(parts[0], Convert.ToBase64String(aospBlob), StringComparison.Ordinal))
                    return existing;
            }
        }
        string comment = (Environment.UserName + "@" + Environment.MachineName).Replace(' ', '_');
        return Convert.ToBase64String(aospBlob) + " " + comment;
    }

    /// <summary>Encodes an RSA-2048 public key into the exact 524-byte AOSP RSAPublicKey Montgomery format.</summary>
    public static byte[] EncodeAospPublicKey(RSA rsa)
    {
        var p = rsa.ExportParameters(includePrivateParameters: false);
        if (p.Modulus == null || p.Modulus.Length != ModulusBytes || p.Exponent == null || p.Exponent.Length is < 1 or > 4)
            throw new CryptographicException("AOSP ADB public key requires a 2048-bit RSA modulus and <=32-bit exponent.");

        byte[] buf = new byte[AospPublicKeyBytes];
        Span<byte> span = buf;
        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], ModulusWords);

        var n = new BigInteger(p.Modulus, isUnsigned: true, isBigEndian: true);
        if (n.IsEven) throw new CryptographicException("RSA modulus must be odd.");

        Span<byte> nLe = span.Slice(8, ModulusBytes);
        if (!n.TryWriteBytes(nLe, out int nWritten, isUnsigned: true, isBigEndian: false) || nWritten > ModulusBytes)
            throw new CryptographicException("Failed to serialize RSA modulus.");

        uint n0 = BinaryPrimitives.ReadUInt32LittleEndian(nLe[..4]);
        // Newton-Hensel modular inverse of odd n0 mod 2^32: inv * n0 == 1 (mod 2^32), then n0inv = -inv mod 2^32.
        uint inv = 1;
        for (int i = 0; i < 5; i++)
            inv = unchecked(inv * (2u - n0 * inv));
        uint n0inv = unchecked(0u - inv);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), n0inv);

        // rr = (2^(ModulusWords * 32))^2 mod n = (2^2048)^2 mod n
        BigInteger r = BigInteger.One << (ModulusWords * 32);
        BigInteger rr = BigInteger.ModPow(r, 2, n);
        Span<byte> rrLe = span.Slice(8 + ModulusBytes, ModulusBytes);
        if (!rr.TryWriteBytes(rrLe, out int rrWritten, isUnsigned: true, isBigEndian: false) || rrWritten > ModulusBytes)
            throw new CryptographicException("Failed to serialize Montgomery R^2.");

        uint exponent = 0;
        foreach (byte b in p.Exponent) exponent = (exponent << 8) | b;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8 + ModulusBytes * 2, 4), exponent);
        return buf;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _tlsCert?.Dispose();
            _tlsCert = null;
            _rsa?.Dispose();
            _rsa = null;
        }
    }
}

using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace SonicRoute.AndroidAudio;

/// <summary>
/// Pure C# BoringSSL-compatible SPAKE2 over Edwards25519 and AES-128-GCM pairing cipher
/// implementing Android 11+ wireless debugging pairing authentication.
/// </summary>
internal static class Spake25519
{
    public const int MessageSize = 32;
    public const int OutKeySize = 64;
    public const int PeerInfoSize = 8192;
    public const int AesGcmTagSize = 16;

    public const byte PairingPacketVersion = 1;
    public const byte PacketTypeSpake2Msg = 0;
    public const byte PacketTypePeerInfo = 1;

    public const byte PeerInfoRsaPubKey = 0;
    public const byte PeerInfoDeviceGuid = 1;

    private static readonly byte[] AliceName = Encoding.ASCII.GetBytes("adb pair client");
    private static readonly byte[] BobName = Encoding.ASCII.GetBytes("adb pair server");
    private static readonly byte[] AesKeyHkdfInfo = Encoding.ASCII.GetBytes("adb pairing_auth aes-128-gcm key");

    // Field prime p = 2^255 - 19 and prime subgroup order L = 2^252 + 27742317777372353535851937790883648493.
    internal static readonly BigInteger P = (BigInteger.One << 255) - 19;
    internal static readonly BigInteger L = (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = ComputeD();
    private static readonly BigInteger TwoD = Mod(2 * D);
    private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) >> 2, P);

    internal static readonly ExtPoint Identity = new(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);
    internal static readonly ExtPoint BasePoint = DecodePoint(Convert.FromHexString("5866666666666666666666666666666666666666666666666666666666666666"));
    // BoringSSL kSpakeM / kSpakeN (RFC 9382 edwards25519 generators).
    internal static readonly ExtPoint PointM = DecodePoint(Convert.FromHexString("d048032c6ea0b6d697ddc2e86bda85a33adac920f1bf18e1b0c6d166a5cecdaf"));
    internal static readonly ExtPoint PointN = DecodePoint(Convert.FromHexString("d3bfb518f44f3430f29d0c92af503865a1ed3281dc69b35dd868ba85f886c4ab"));

    private static BigInteger ComputeD()
    {
        BigInteger neg121665 = Mod(-121665);
        BigInteger inv121666 = ModInv(121666);
        return Mod(neg121665 * inv121666);
    }

    private static BigInteger Mod(BigInteger x)
    {
        BigInteger r = x % P;
        return r.Sign < 0 ? r + P : r;
    }

    private static BigInteger ModInv(BigInteger x) => BigInteger.ModPow(Mod(x), P - 2, P);

    internal readonly struct ExtPoint
    {
        public readonly BigInteger X, Y, Z, T;
        public ExtPoint(BigInteger x, BigInteger y, BigInteger z, BigInteger t) { X = x; Y = y; Z = z; T = t; }
    }

    internal static bool IsOnCurve(in ExtPoint p)
    {
        BigInteger zInv = ModInv(p.Z);
        BigInteger x = Mod(p.X * zInv);
        BigInteger y = Mod(p.Y * zInv);
        BigInteger x2 = Mod(x * x);
        BigInteger y2 = Mod(y * y);
        BigInteger lhs = Mod(y2 - x2);
        BigInteger rhs = Mod(1 + D * x2 * y2);
        return lhs == rhs;
    }

    internal static ExtPoint Add(in ExtPoint p1, in ExtPoint p2)
    {
        BigInteger y1mx1 = Mod(p1.Y - p1.X);
        BigInteger y2mx2 = Mod(p2.Y - p2.X);
        BigInteger a = Mod(y1mx1 * y2mx2);
        BigInteger y1px1 = Mod(p1.Y + p1.X);
        BigInteger y2px2 = Mod(p2.Y + p2.X);
        BigInteger b = Mod(y1px1 * y2px2);
        BigInteger c = Mod(TwoD * p1.T * p2.T);
        BigInteger d = Mod(2 * p1.Z * p2.Z);
        BigInteger e = Mod(b - a);
        BigInteger f = Mod(d - c);
        BigInteger g = Mod(d + c);
        BigInteger h = Mod(b + a);
        return new ExtPoint(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    internal static ExtPoint Negate(in ExtPoint p) => new(Mod(P - p.X), p.Y, p.Z, Mod(P - p.T));

    internal static ExtPoint ScalarMult(in ExtPoint point, BigInteger scalar)
    {
        if (scalar.Sign < 0) throw new ArgumentOutOfRangeException(nameof(scalar));
        ExtPoint acc = Identity;
        int bitLen = (int)scalar.GetBitLength();
        for (int bit = bitLen - 1; bit >= 0; bit--)
        {
            acc = Add(acc, acc);
            if (((scalar >> bit) & BigInteger.One) != BigInteger.Zero)
                acc = Add(acc, point);
        }
        return acc;
    }

    internal static byte[] EncodePoint(in ExtPoint p)
    {
        BigInteger zInv = ModInv(p.Z);
        BigInteger x = Mod(p.X * zInv);
        BigInteger y = Mod(p.Y * zInv);
        byte[] encoded = new byte[32];
        y.TryWriteBytes(encoded, out int written, isUnsigned: true, isBigEndian: false);
        if (written > 32) throw new CryptographicException("Invalid Edwards25519 Y coordinate.");
        if (!x.IsEven) encoded[31] |= 0x80;
        return encoded;
    }

    internal static ExtPoint DecodePoint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32) throw new CryptographicException("Invalid Edwards25519 point length.");
        Span<byte> copy = stackalloc byte[32];
        bytes.CopyTo(copy);
        int xSign = (copy[31] >> 7) & 1;
        copy[31] &= 0x7F;
        var y = new BigInteger(copy, isUnsigned: true, isBigEndian: false);
        if (y >= P) throw new CryptographicException("Invalid Edwards25519 point (y >= p).");

        BigInteger y2 = Mod(y * y);
        BigInteger u = Mod(y2 - 1);
        BigInteger v = Mod(D * y2 + 1);
        BigInteger v3 = Mod(v * v * v);
        BigInteger v7 = Mod(v3 * v3 * v);
        BigInteger uv7 = Mod(u * v7);
        BigInteger x = Mod(u * v3 * BigInteger.ModPow(uv7, (P - 5) >> 3, P));

        BigInteger vx2 = Mod(v * x * x);
        if (vx2 != u)
        {
            if (vx2 != Mod(-u))
                throw new CryptographicException("Point is not on Edwards25519 curve.");
            x = Mod(x * SqrtM1);
        }

        if (x.IsZero && xSign == 1)
            throw new CryptographicException("Invalid negative zero X coordinate.");
        if ((x.IsEven ? 0 : 1) != xSign)
            x = Mod(P - x);

        return new ExtPoint(x, y, BigInteger.One, Mod(x * y));
    }

    public static byte[] EncodePeerInfo(byte type, string asciiOrUtf8Text)
    {
        byte[] info = new byte[PeerInfoSize];
        info[0] = type;
        byte[] raw = Encoding.UTF8.GetBytes(asciiOrUtf8Text);
        if (raw.Length > PeerInfoSize - 1)
            throw new ArgumentException("PeerInfo payload exceeds 8191 bytes.", nameof(asciiOrUtf8Text));
        raw.CopyTo(info, 1);
        return info;
    }

    public static (byte Type, string Value) DecodePeerInfo(ReadOnlySpan<byte> peerInfo)
    {
        if (peerInfo.Length != PeerInfoSize)
            throw new InvalidDataException($"Invalid PeerInfo size {peerInfo.Length}; expected {PeerInfoSize}.");
        byte type = peerInfo[0];
        ReadOnlySpan<byte> data = peerInfo[1..];
        int nul = data.IndexOf((byte)0);
        if (nul >= 0) data = data[..nul];
        return (type, Encoding.UTF8.GetString(data));
    }

    internal sealed class HandshakeSession
    {
        private readonly bool _isAlice;
        private readonly byte[] _passwordHash;
        private readonly BigInteger _privateScalar;
        private readonly BigInteger _passwordScalar;
        private readonly byte[] _myMsg;
        private bool _processed;

        public HandshakeSession(bool isAlice, ReadOnlySpan<byte> password, ReadOnlySpan<byte> entropy64 = default)
        {
            if (password.IsEmpty) throw new ArgumentException("SPAKE2 password cannot be empty.", nameof(password));
            _isAlice = isAlice;
            _passwordHash = SHA512.HashData(password);
            _passwordScalar = new BigInteger(_passwordHash, isUnsigned: true, isBigEndian: false) % L;

            byte[] rand = entropy64.IsEmpty ? RandomNumberGenerator.GetBytes(64) : entropy64.ToArray();
            if (rand.Length != 64) throw new ArgumentException("SPAKE2 entropy must be 64 bytes.", nameof(entropy64));
            BigInteger xRaw = new BigInteger(rand, isUnsigned: true, isBigEndian: false) % L;
            // Matches BoringSSL ctx->private_key = sc_reduce(rand64) << 3 (kills cofactor-8 small subgroups).
            _privateScalar = xRaw << 3;

            ExtPoint baseTerm = ScalarMult(BasePoint, _privateScalar);
            ExtPoint mask = ScalarMult(isAlice ? PointM : PointN, _passwordScalar);
            _myMsg = EncodePoint(Add(baseTerm, mask));
        }

        public byte[] MyMessage => (byte[])_myMsg.Clone();

        public byte[] ProcessPeerMessage(ReadOnlySpan<byte> theirMsg)
        {
            if (_processed) throw new InvalidOperationException("SPAKE2 peer message already processed.");
            if (theirMsg.Length != MessageSize)
                throw new CryptographicException($"Invalid SPAKE2 message size {theirMsg.Length}.");
            _processed = true;

            ExtPoint theirPoint = DecodePoint(theirMsg);
            ExtPoint mask = ScalarMult(_isAlice ? PointN : PointM, _passwordScalar);
            ExtPoint unmasked = Add(theirPoint, Negate(mask));
            ExtPoint sharedK = ScalarMult(unmasked, _privateScalar);
            byte[] kBytes = EncodePoint(sharedK);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            AppendLengthPrefixed(sha, AliceName);
            AppendLengthPrefixed(sha, BobName);
            AppendLengthPrefixed(sha, _isAlice ? _myMsg : theirMsg);
            AppendLengthPrefixed(sha, _isAlice ? theirMsg : _myMsg);
            AppendLengthPrefixed(sha, kBytes);
            AppendLengthPrefixed(sha, _passwordHash);
            return sha.GetHashAndReset();
        }

        private static void AppendLengthPrefixed(IncrementalHash hash, ReadOnlySpan<byte> data)
        {
            Span<byte> lenBuf = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(lenBuf, (ulong)data.Length);
            hash.AppendData(lenBuf);
            hash.AppendData(data);
        }
    }

    internal sealed class PairingAesCipher : IDisposable
    {
        private readonly AesGcm _aes;
        private ulong _encSeq;
        private ulong _decSeq;
        private bool _disposed;

        public PairingAesCipher(ReadOnlySpan<byte> spakeOutKey)
        {
            byte[] key = new byte[16];
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                spakeOutKey,
                key,
                ReadOnlySpan<byte>.Empty,
                AesKeyHkdfInfo);
            _aes = new AesGcm(key, AesGcmTagSize);
        }

        public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            byte[] result = new byte[plaintext.Length + AesGcmTagSize];
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[..8], _encSeq++);
            _aes.Encrypt(nonce, plaintext, result.AsSpan(0, plaintext.Length), result.AsSpan(plaintext.Length, AesGcmTagSize));
            return result;
        }

        public byte[] Decrypt(ReadOnlySpan<byte> ciphertextWithTag)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ciphertextWithTag.Length < AesGcmTagSize)
                throw new CryptographicException("Encrypted pairing payload truncated.");
            byte[] plaintext = new byte[ciphertextWithTag.Length - AesGcmTagSize];
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[..8], _decSeq++);
            _aes.Decrypt(nonce, ciphertextWithTag[..^AesGcmTagSize], ciphertextWithTag[^AesGcmTagSize..], plaintext);
            return plaintext;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _aes.Dispose();
        }
    }
}

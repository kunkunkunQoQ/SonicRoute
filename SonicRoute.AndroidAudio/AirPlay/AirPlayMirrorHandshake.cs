using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using SonicRoute.AirPlay.Compatibility;
using static SonicRoute.AndroidAudio.Spake25519;

namespace SonicRoute.AirPlay;

// The legacy mirror exchange authenticates ephemeral keys; this receiver does not persist device credentials.
internal sealed class AirPlaySigningKey : IDisposable
{
    private readonly byte[] _expanded = SHA512.HashData(RandomNumberGenerator.GetBytes(32));
    private BigInteger _scalar;
    internal byte[] PublicKey { get; }

    internal AirPlaySigningKey()
    {
        Clamp(_expanded.AsSpan(0, 32));
        _scalar = Number(_expanded.AsSpan(0, 32));
        PublicKey = EncodePoint(ScalarMult(BasePoint, _scalar));
    }

    internal byte[] Sign(ReadOnlySpan<byte> message)
    {
        BigInteger nonce = Number(Hash(_expanded.AsSpan(32), message)) % L;
        byte[] point = EncodePoint(ScalarMult(BasePoint, nonce));
        BigInteger challenge = Number(Hash(point, PublicKey, message)) % L;
        byte[] signature = new byte[64];
        point.CopyTo(signature, 0);
        ((nonce + challenge * _scalar) % L).TryWriteBytes(signature.AsSpan(32), out _, isUnsigned: true);
        return signature;
    }

    internal static bool IsPublicKey(ReadOnlySpan<byte> key)
    {
        var point = DecodePoint(key);
        return !IsIdentity(point) && IsIdentity(ScalarMult(point, L));
    }

    internal static bool Verify(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (key.Length != 32 || signature.Length != 64) return false;
        BigInteger s = Number(signature[32..]);
        if (s >= L) return false;
        BigInteger h = Number(Hash(signature[..32], key, message)) % L;
        var point = Add(ScalarMult(BasePoint, s), Negate(ScalarMult(DecodePoint(key), h)));
        return CryptographicOperations.FixedTimeEquals(signature[..32], EncodePoint(point));
    }

    private static bool IsIdentity(in ExtPoint point) => point.X.IsZero && point.Y == point.Z;
    private static BigInteger Number(ReadOnlySpan<byte> value) => new(value, isUnsigned: true, isBigEndian: false);

    private static void Clamp(Span<byte> scalar)
    {
        scalar[0] &= 248;
        scalar[31] = (byte)((scalar[31] & 127) | 64);
    }

    // Montgomery ladder, RFC 7748. Used only during setup, off the UI and video threads.
    internal static byte[] Exchange(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> coordinate)
    {
        Span<byte> key = stackalloc byte[32];
        Span<byte> u = stackalloc byte[32];
        scalar.CopyTo(key); coordinate.CopyTo(u);
        Clamp(key);
        u[31] &= 127;
        BigInteger x = Number(u) % P, x2 = 1, z2 = 0, x3 = x, z3 = 1;
        bool swap = false;
        for (int bit = 254; bit >= 0; --bit)
        {
            bool next = (key[bit >> 3] & (1 << (bit & 7))) != 0;
            if (swap != next) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
            swap = next;
            BigInteger a = x2 + z2, b = x2 - z2, c = x3 + z3, d = x3 - z3;
            BigInteger aa = Mod(a * a), bb = Mod(b * b), e = aa - bb;
            BigInteger da = Mod(d * a), cb = Mod(c * b);
            x3 = Mod((da + cb) * (da + cb));
            z3 = Mod(x * (da - cb) * (da - cb));
            x2 = Mod(aa * bb);
            z2 = Mod(e * (aa + 121665 * e));
        }
        if (swap) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
        byte[] result = new byte[32];
        Mod(x2 * BigInteger.ModPow(z2, P - 2, P)).TryWriteBytes(result, out _, isUnsigned: true);
        CryptographicOperations.ZeroMemory(key);
        return result;
    }

    private static BigInteger Mod(BigInteger x) { var value = x % P; return value.Sign < 0 ? value + P : value; }

    internal static byte[] Hash(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, ReadOnlySpan<byte> third = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        hash.AppendData(first); hash.AppendData(second); hash.AppendData(third);
        return hash.GetHashAndReset();
    }

    public void Dispose() { CryptographicOperations.ZeroMemory(_expanded); _scalar = 0; }
}

internal sealed class AirPlayMirrorHandshake : IDisposable
{
    // Compatibility response records originally from airplayreceiver. See Compatibility/NOTICE.md.
    private static readonly string[] Replies =
    [
        "46504C59030102000000008202000F9F3F9E0A2521DBDF312AB2BFB29E8D232B6376A8C818701D22AE93D82737FEAF9DB4FDF41C2DBA9D1F49CAAABF6591AC1F7BC6F7E0663D21AFE01565953EAB81F418CEED095ADB7C3D0E254909A79831D49C3982973434FACB42C63A1CD911A6FE941A8A6D4A743B46C3A7649E44C78955E49D8155009549C4E2F7A3F6D5BA",
        "46504C5903010200000000820201CF32A25714B2524F8AA0AD7AF164E37BCF4424E200047EFC0AD67AFCD95DED1C2730BB591B962ED63A9C4DED88BA8FC78DE64D91CCFD5C7B56DA88E31F5CCEAFC7431995A01665A54E1939D25B94DB64B9E45D8D063E1E6AF07E9656162B0EFA404275EA5A44D9591C7256B9FBE6513898B80227721988571650942AD946688A",
        "46504C5903010200000000820202C169A352EEED35B18CDD9C58D64F16C1519A89EB5317BD0D4336CD68F638FF9D016A5B52B7FA9216B2B65482C78444118121A2C7FED83DB7119E9182AAD7D18C7063E2A457555910AF9E0EFC76347D164043807F581EE4FBE42CA9DEDC1B5EB2A3AA3D2ECD59E7EEE70B3629F22AFD161D877353DDB99ADC8E07006E56F850CE",
        "46504C59030102000000008202039001E1727E0F57F9F5880DB104A6257A23F5CFFF1ABBE1E93045251AFB97EB9FC0011EBE0F3A81DF5B691D76ACB2F7A5C708E3D328F56BB39DBDE5F29C8A17F481487E3AE863C678325422E6F78E166D18AA7FD636258BCE28726F661F738893CE44311E4BE6C0535193E5EF72E8686233729C227D820C999445D89246C8C359"
    ];
    private byte[]? _peerSigningKey, _verifyMessage, _sharedSecret, _keyRecord;
    private AirPlayAesCtr? _verifyCipher;
    private int _mode = -1;

    internal byte[]? BeginVerify(ReadOnlySpan<byte> request, AirPlaySigningKey identity)
    {
        if (_sharedSecret != null || request.Length != 68 || !request[..4].SequenceEqual("\x01\0\0\0"u8)
            || !AirPlaySigningKey.IsPublicKey(request[36..])) return null;
        _peerSigningKey = request[36..].ToArray();
        byte[] ephemeral = RandomNumberGenerator.GetBytes(32);
        try
        {
            byte[] @base = new byte[32]; @base[0] = 9;
            byte[] server = AirPlaySigningKey.Exchange(ephemeral, @base);
            var peer = request.Slice(4, 32);
            _sharedSecret = AirPlaySigningKey.Exchange(ephemeral, peer);
            if (_sharedSecret.AsSpan().IndexOfAnyExcept((byte)0) < 0) return null;
            _verifyCipher = AirPlayAesCtr.FromLabels("Pair-Verify-AES-Key"u8, "Pair-Verify-AES-IV"u8, _sharedSecret);
            Span<byte> message = stackalloc byte[64];
            server.CopyTo(message); peer.CopyTo(message[32..]);
            byte[] signature = identity.Sign(message);
            _verifyCipher.Apply(signature);
            _verifyMessage = [.. peer, .. server];
            return [.. server, .. signature];
        }
        finally { CryptographicOperations.ZeroMemory(ephemeral); }
    }

    internal bool FinishVerify(Span<byte> request)
    {
        if (_peerSigningKey == null || _verifyMessage == null || _verifyCipher == null
            || request.Length != 68 || request[..4].IndexOfAnyExcept((byte)0) >= 0) return false;
        try
        {
            _verifyCipher.Apply(request[4..]);
            return AirPlaySigningKey.Verify(_peerSigningKey, _verifyMessage, request[4..]);
        }
        finally { _verifyCipher.Dispose(); _verifyCipher = null; }
    }

    internal byte[]? ExchangeFairPlay(byte[] message)
    {
        if (message.Length is not (16 or 164) || !message.AsSpan(0, 6).SequenceEqual("FPLY\x03\x01"u8)
            || BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(8)) != message.Length - 12) return null;
        if (message.Length == 16 && message[6] == 1 && message[14] <= 3)
        {
            _mode = message[14]; _keyRecord = null;
            return Convert.FromHexString(Replies[_mode]);
        }
        if (message.Length == 164 && message[6] == 3 && message[12] == _mode)
        {
            _keyRecord = message;
            return [.. "FPLY\x03\x01\x04\0\0\0\0\x14"u8, .. message.AsSpan(144, 20)];
        }
        return null;
    }

    internal byte[]? UnwrapKey(byte[] ekey)
    {
        if (_keyRecord == null || ekey.Length != 72) return null;
        byte[] raw = new byte[16];
        new OmgHax().DecryptAesKey(_keyRecord, ekey, raw);
        if (_sharedSecret == null) return raw;
        byte[] hash = AirPlaySigningKey.Hash(raw, _sharedSecret);
        try { return hash[..16]; }
        finally { CryptographicOperations.ZeroMemory(hash); CryptographicOperations.ZeroMemory(raw); }
    }

    public void Dispose()
    {
        _verifyCipher?.Dispose();
        if (_sharedSecret != null) CryptographicOperations.ZeroMemory(_sharedSecret);
        if (_keyRecord != null) CryptographicOperations.ZeroMemory(_keyRecord);
    }
}

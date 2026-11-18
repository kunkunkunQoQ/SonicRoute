using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace SonicRoute.AirPlay;

// AirPlay uses SRP-6a for the transient HomeKit exchange and the legacy on-screen PIN exchange.
internal sealed class AirPlayPairing
{
    private static readonly Group Transient = new(HashAlgorithmName.SHA512, 5,
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7" +
        "EDEE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24" +
        "CF5F83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462" +
        "E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D22" +
        "61898FA051015728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB" +
        "3970F85A6E1E4C7ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC" +
        "86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D12" +
        "0A93AD2CAFFFFFFFFFFFFFFFF");
    private static class Legacy
    {
        internal static readonly Group Pin = new(HashAlgorithmName.SHA1, 2,
            "AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050A37329CBB4" +
            "A099ED8193E0757767A13DD52312AB4B03310DCD7F48A9DA04FD50E8083969EDB767B0CF60" +
            "95179A163AB3661A05FBD5FAAAE82918A9962F0B93B855F97993EC975EEAA80D740ADBF4FF" +
            "747359D041D5C33EA71D281E446B14773BCA97B43A23FB801676BD207A436C6481F1D2B907" +
            "8717461A5B9D32E688F87748544523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB37861" +
            "60279004E57AE6AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DB" +
            "FBB694B5C803D89F7AE435DE236D525F54759B65E372FCD68EF20FA7111F9E4AFF73");
    }

    private readonly Group _group;
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _userHash;
    private readonly BigInteger _secret, _verifier, _public;

    internal AirPlayPairing(string pin = "3939", string? legacyUser = null)
    {
        _group = legacyUser == null ? Transient : Legacy.Pin;
        if (legacyUser != null && _salt[0] == 0) _salt[0] = (byte)RandomNumberGenerator.GetInt32(1, 256);
        string user = legacyUser ?? "Pair-Setup";
        _userHash = _group.Hash(Encoding.UTF8.GetBytes(user));
        _secret = Number(RandomNumberGenerator.GetBytes(32)) + 1;
        byte[] password = Encoding.UTF8.GetBytes(user + ":" + pin);
        byte[] hash = _group.Hash(password);
        try { _verifier = BigInteger.ModPow(_group.Generator, Number(_group.Hash(_salt, hash)), _group.Modulus); }
        finally { CryptographicOperations.ZeroMemory(password); CryptographicOperations.ZeroMemory(hash); }
        _public = (_group.Multiplier * _verifier + BigInteger.ModPow(_group.Generator, _secret, _group.Modulus)) % _group.Modulus;
    }

    internal byte[] Salt => _salt;
    internal byte[] PublicKey => Bytes(_public);
    internal byte[] Challenge => Tlv((6, [2]), (2, _salt), (3, PublicKey));

    internal (byte[] Proof, byte[] Key)? Verify(byte[] bytes, byte[] proof)
    {
        if (bytes.Length == 0 || bytes.Length > _group.Width || proof.Length != _group.ProofPrefix.Length) return null;
        var client = Number(bytes);
        var modulus = _group.Modulus;
        if (client <= 0 || client >= modulus) return null;
        var a = Bytes(client);
        var b = PublicKey;
        var u = Number(_group.Hash(_group.Pad(a), _group.Pad(b)));
        if (u.IsZero) return null;
        var shared = Bytes(BigInteger.ModPow(client * BigInteger.ModPow(_verifier, u, modulus) % modulus, _secret, modulus));
        byte[] key = _group == Transient ? _group.Hash(shared) : [.. _group.Hash(shared, [0, 0, 0, 0]), .. _group.Hash(shared, [0, 0, 0, 1])];
        CryptographicOperations.ZeroMemory(shared);
        byte[] expected = _group.Hash(_group.ProofPrefix, _userHash, _salt, a, b, key);
        if (!CryptographicOperations.FixedTimeEquals(expected, proof))
        {
            CryptographicOperations.ZeroMemory(key);
            return null;
        }
        return (_group.Hash(a, proof, key), key);
    }

    internal static Dictionary<byte, byte[]> ReadTlv(ReadOnlySpan<byte> body)
    {
        if (body.Length > 2048) throw new InvalidDataException("Pairing message exceeds limit.");
        var values = new Dictionary<byte, byte[]>();
        while (!body.IsEmpty)
        {
            if (body.Length < 2 || body.Length < 2 + body[1]) throw new InvalidDataException("Invalid pairing TLV.");
            (byte id, int size) = (body[0], body[1]);
            byte[] part = body.Slice(2, size).ToArray();
            if (values.TryGetValue(id, out var previous)) part = [.. previous, .. part];
            values[id] = part;
            body = body[(size + 2)..];
        }
        return values;
    }

    internal static byte[] Tlv(params (byte Id, byte[] Value)[] values)
    {
        using var output = new MemoryStream();
        foreach (var (id, value) in values)
        {
            for (int offset = 0; offset < value.Length; offset += 255)
            {
                int count = Math.Min(value.Length - offset, 255);
                output.WriteByte(id);
                output.WriteByte((byte)count);
                output.Write(value, offset, count);
            }
        }
        return output.ToArray();
    }

    internal static bool IsState(Dictionary<byte, byte[]> values, byte state) =>
        values.TryGetValue(6, out var actual) && actual.Length == 1 && actual[0] == state;

    internal static bool IsTransient(Dictionary<byte, byte[]> values) =>
        values.TryGetValue(0, out var method) && method.AsSpan().SequenceEqual(new byte[] { 0 })
        && values.TryGetValue(19, out var flags) && flags.Length is 1 or 4
        && (flags[0] & 0x10) != 0;

    private static BigInteger Number(byte[] bytes) => new(bytes, isUnsigned: true, isBigEndian: true);
    private static byte[] Bytes(BigInteger value) => value.ToByteArray(isUnsigned: true, isBigEndian: true);
    private sealed class Group
    {
        private readonly HashAlgorithmName _algorithm;
        internal readonly int Width;
        internal readonly BigInteger Modulus, Generator, Multiplier;
        internal readonly byte[] ProofPrefix;

        internal Group(HashAlgorithmName algorithm, byte generator, string modulus)
        {
            _algorithm = algorithm;
            byte[] n = Convert.FromHexString(modulus), g = [generator];
            Width = n.Length;
            Modulus = Number(n);
            Generator = generator;
            Multiplier = Number(Hash(n, Pad(g)));
            ProofPrefix = Hash(n);
            var generatorHash = Hash(g);
            for (int i = 0; i < ProofPrefix.Length; ++i) ProofPrefix[i] ^= generatorHash[i];
        }

        internal byte[] Pad(byte[] bytes)
        {
            var padded = new byte[Width];
            bytes.CopyTo(padded.AsSpan(Width - bytes.Length));
            return padded;
        }

        internal byte[] Hash(params byte[][] parts)
        {
            using var hash = IncrementalHash.CreateHash(_algorithm);
            foreach (var part in parts) hash.AppendData(part);
            return hash.GetHashAndReset();
        }
    }
}

internal sealed class AirPlayControlCipher : IDisposable
{
    internal const int MaxFrame = 1024;
    private readonly ChaCha20Poly1305 _read, _write;
    private readonly byte[] _frame = new byte[MaxFrame + 18];
    private readonly byte[] _nonce = new byte[12];
    private ulong _received, _sent;

    internal AirPlayControlCipher(byte[] key)
    {
        _read = Derive(key, "Control-Write-Encryption-Key"u8);
        _write = Derive(key, "Control-Read-Encryption-Key"u8);
    }

    private static ChaCha20Poly1305 Derive(byte[] key, ReadOnlySpan<byte> purpose)
    {
        Span<byte> output = stackalloc byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA512, key, output, "Control-Salt"u8, purpose);
        try { return new ChaCha20Poly1305(output); }
        finally { CryptographicOperations.ZeroMemory(output); }
    }

    internal async ValueTask<int> ReadAsync(Stream stream, Memory<byte> destination, CancellationToken token)
    {
        await stream.ReadExactlyAsync(_frame.AsMemory(0, 2), token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(_frame);
        if (length == 0 || length > MaxFrame || length > destination.Length)
            throw new InvalidDataException("Invalid encrypted control frame length.");
        await stream.ReadExactlyAsync(_frame.AsMemory(2, length + 16), token).ConfigureAwait(false);
        BinaryPrimitives.WriteUInt64LittleEndian(_nonce.AsSpan(4), _received++);
        _read.Decrypt(_nonce, _frame.AsSpan(2, length), _frame.AsSpan(2 + length, 16), destination.Span[..length], _frame.AsSpan(0, 2));
        return length;
    }

    internal async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        while (!data.IsEmpty)
        {
            int length = Math.Min(MaxFrame, data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(_frame, (ushort)length);
            BinaryPrimitives.WriteUInt64LittleEndian(_nonce.AsSpan(4), _sent++);
            _write.Encrypt(_nonce, data.Span[..length], _frame.AsSpan(2, length), _frame.AsSpan(2 + length, 16), _frame.AsSpan(0, 2));
            await stream.WriteAsync(_frame.AsMemory(0, length + 18), token).ConfigureAwait(false);
            data = data[length..];
        }
    }

    public void Dispose()
    {
        _read.Dispose();
        _write.Dispose();
        CryptographicOperations.ZeroMemory(_frame);
    }
}

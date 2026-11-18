using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace SonicRoute.AirPlay;

// AirPlay's transient HomeKit exchange uses SRP-6a with the RFC 5054 3072-bit group.
// The standard transient code is public. Starting the receiver opens a temporary LAN session.
internal sealed class AirPlayPairing
{
    private const int GroupSize = 384;
    private static readonly byte[] Modulus = Convert.FromHexString(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7" +
        "EDEE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24" +
        "CF5F83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462" +
        "E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D22" +
        "61898FA051015728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB" +
        "3970F85A6E1E4C7ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC" +
        "86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D12" +
        "0A93AD2CAFFFFFFFFFFFFFFFF");
    private static readonly BigInteger N = Number(Modulus), G = 5;
    private static readonly BigInteger Multiplier = Number(Hash(Modulus, Pad([5])));
    private static readonly byte[] UserHash = SHA512.HashData("Pair-Setup"u8);
    private static readonly byte[] GroupProof = Xor(SHA512.HashData(Modulus), SHA512.HashData(new byte[] { 5 }));
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly BigInteger _secret, _verifier, _public;

    internal AirPlayPairing()
    {
        _secret = Number(RandomNumberGenerator.GetBytes(32)) + 1;
        var passwordHash = SHA512.HashData("Pair-Setup:3939"u8);
        _verifier = BigInteger.ModPow(G, Number(Hash(_salt, passwordHash)), N);
        _public = (Multiplier * _verifier + BigInteger.ModPow(G, _secret, N)) % N;
    }

    internal byte[] Challenge => Tlv((6, [2]), (2, _salt), (3, Bytes(_public)));

    internal (byte[] Proof, byte[] Key)? Verify(Dictionary<byte, byte[]> values)
    {
        if (!values.TryGetValue(3, out var bytes) || bytes.Length is 0 or > GroupSize
            || !values.TryGetValue(4, out var proof) || proof.Length != 64) return null;
        var client = Number(bytes);
        if (client <= 0 || client >= N) return null;
        var a = Bytes(client);
        var b = Bytes(_public);
        var u = Number(Hash(Pad(a), Pad(b)));
        if (u.IsZero) return null;
        var shared = BigInteger.ModPow(client * BigInteger.ModPow(_verifier, u, N) % N, _secret, N);
        byte[] key = SHA512.HashData(Bytes(shared));
        byte[] expected = Hash(GroupProof, UserHash, _salt, a, b, key);
        if (!CryptographicOperations.FixedTimeEquals(expected, proof))
        {
            CryptographicOperations.ZeroMemory(key);
            return null;
        }
        return (Tlv((6, [4]), (4, Hash(a, proof, key))), key);
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
    private static byte[] Pad(byte[] bytes)
    {
        var padded = new byte[GroupSize];
        bytes.CopyTo(padded.AsSpan(GroupSize - bytes.Length));
        return padded;
    }
    private static byte[] Hash(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var part in parts) hash.AppendData(part);
        return hash.GetHashAndReset();
    }
    private static byte[] Xor(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length; ++i) a[i] ^= b[i];
        return a;
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

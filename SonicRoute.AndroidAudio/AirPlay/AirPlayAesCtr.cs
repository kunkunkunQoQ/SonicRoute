using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace SonicRoute.AirPlay;

// A continuous AES-CTR keystream for AirPlay pairing and screen packets.
internal sealed class AirPlayAesCtr : IDisposable
{
    private readonly Aes _aes = Aes.Create();
    private readonly ICryptoTransform _encrypt;
    private readonly byte[] _counters = new byte[4096], _keyStream = new byte[4096];
    private ulong _counterHi, _counterLo;
    private int _used, _length;

    private AirPlayAesCtr(byte[] key, ReadOnlySpan<byte> iv)
    {
        try
        {
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _aes.Key = key;
            _encrypt = _aes.CreateEncryptor();
            _counterHi = BinaryPrimitives.ReadUInt64BigEndian(iv);
            _counterLo = BinaryPrimitives.ReadUInt64BigEndian(iv[8..]);
        }
        catch { _aes.Dispose(); throw; }
    }

    internal static AirPlayAesCtr FromLabels(ReadOnlySpan<byte> keyLabel, ReadOnlySpan<byte> ivLabel, ReadOnlySpan<byte> secret)
    {
        byte[] key = AirPlaySigningKey.Hash(keyLabel, secret);
        byte[] iv = AirPlaySigningKey.Hash(ivLabel, secret);
        byte[] aesKey = key[..16];
        try { return new AirPlayAesCtr(aesKey, iv.AsSpan(0, 16)); }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(iv);
            CryptographicOperations.ZeroMemory(aesKey);
        }
    }

    internal void Apply(Span<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_used == _length)
            {
                _length = Math.Min((data.Length + 15) & ~15, _counters.Length);
                for (int i = 0; i < _length; i += 16)
                {
                    BinaryPrimitives.WriteUInt64BigEndian(_counters.AsSpan(i), _counterHi);
                    BinaryPrimitives.WriteUInt64BigEndian(_counters.AsSpan(i + 8), _counterLo);
                    if (++_counterLo == 0) ++_counterHi;
                }
                _encrypt.TransformBlock(_counters, 0, _length, _keyStream, 0);
                _used = 0;
            }
            int count = Math.Min(data.Length, _length - _used), iByte = 0;
            if (Vector.IsHardwareAccelerated)
                for (; iByte <= count - Vector<byte>.Count; iByte += Vector<byte>.Count)
                    (new Vector<byte>(data[iByte..]) ^ new Vector<byte>(_keyStream.AsSpan(_used + iByte))).CopyTo(data[iByte..]);
            for (; iByte < count; ++iByte) data[iByte] ^= _keyStream[_used + iByte];
            _used += count;
            data = data[count..];
        }
    }

    public void Dispose() { _encrypt.Dispose(); _aes.Dispose(); CryptographicOperations.ZeroMemory(_keyStream); }
}

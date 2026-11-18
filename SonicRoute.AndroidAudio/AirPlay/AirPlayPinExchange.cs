using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SonicRoute.AirPlay;

// Legacy AirPlay seals each Ed25519 public key with AES-GCM and a 16-byte nonce.
// Windows CNG supports that nonce width. The managed AesGcm API accepts only 12 bytes.
internal static unsafe class AirPlayPinExchange
{
    internal static bool Confirm(byte[] sessionKey, byte[] clientCiphertext, byte[] clientTag, byte[] serverKey,
        byte[] clientKey, byte[] serverCiphertext, byte[] serverTag)
    {
        byte[] key = AirPlaySigningKey.Hash("Pair-Setup-AES-Key"u8, sessionKey);
        byte[] iv = AirPlaySigningKey.Hash("Pair-Setup-AES-IV"u8, sessionKey);
        try
        {
            Check(BCryptOpenAlgorithmProvider(out nint algorithm, "AES", null, 0));
            try
            {
                byte[] mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                Check(BCryptSetProperty(algorithm, "ChainingMode", mode, mode.Length, 0));
                Check(BCryptGenerateSymmetricKey(algorithm, out nint cipher, 0, 0, key, 16, 0));
                try
                {
                    iv[15]++;
                    if (!Transform(cipher, encrypt: false, iv, clientCiphertext, clientKey, clientTag)) return false;
                    if (!AirPlaySigningKey.IsPublicKey(clientKey)) return false;
                    iv[15]++;
                    return Transform(cipher, encrypt: true, iv, serverKey, serverCiphertext, serverTag);
                }
                finally { BCryptDestroyKey(cipher); }
            }
            finally { BCryptCloseAlgorithmProvider(algorithm, 0); }
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(iv); }
    }

    private static bool Transform(nint key, bool encrypt, byte[] iv, byte[] input, byte[] output, byte[] tag)
    {
        fixed (byte* nonce = iv, authenticationTag = tag)
        {
            var info = new AuthenticatedMode
            {
                Size = sizeof(AuthenticatedMode), Version = 1, Nonce = nonce, NonceSize = 16, Tag = authenticationTag, TagSize = tag.Length
            };
            int result = encrypt
                ? BCryptEncrypt(key, input, input.Length, ref info, 0, 0, output, output.Length, out int length, 0)
                : BCryptDecrypt(key, input, input.Length, ref info, 0, 0, output, output.Length, out length, 0);
            if (result == unchecked((int)0xC000A002)) return false; // STATUS_AUTH_TAG_MISMATCH
            Check(result);
            return length == output.Length;
        }
    }

    private static void Check(int status)
    {
        if (status < 0) throw new CryptographicException($"AirPlay pairing cipher returned 0x{status:X8}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthenticatedMode
    {
        internal int Size, Version;
        internal byte* Nonce;
        internal int NonceSize;
        internal nint AdditionalData;
        internal int AdditionalDataSize;
        internal byte* Tag;
        internal int TagSize;
        internal nint MacContext;
        internal int MacContextSize, AdditionalDataProcessed;
        internal ulong DataProcessed;
        internal int Flags;
    }

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptOpenAlgorithmProvider(out nint algorithm, string id, string? implementation, int flags);

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptSetProperty(nint handle, string property, byte[] value, int length, int flags);

    [DllImport("bcrypt.dll")]
    private static extern int BCryptGenerateSymmetricKey(nint algorithm, out nint key, nint keyObject, int objectSize, byte[] secret, int secretSize, int flags);

    [DllImport("bcrypt.dll")]
    private static extern int BCryptEncrypt(nint key, byte[] bytes, int length, ref AuthenticatedMode mode, nint iv, int ivSize,
        [Out] byte[] output, int capacity, out int written, int flags);

    [DllImport("bcrypt.dll")]
    private static extern int BCryptDecrypt(nint key, byte[] bytes, int length, ref AuthenticatedMode mode, nint iv, int ivSize,
        [Out] byte[] output, int capacity, out int written, int flags);

    [DllImport("bcrypt.dll")] private static extern int BCryptDestroyKey(nint key);
    [DllImport("bcrypt.dll")] private static extern int BCryptCloseAlgorithmProvider(nint algorithm, int flags);
}

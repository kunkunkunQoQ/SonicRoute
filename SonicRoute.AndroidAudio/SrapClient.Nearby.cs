using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;

namespace SonicRoute.AndroidAudio;

internal sealed partial class SrapClient
{
    internal async Task<SrapSession> PairNearbyAsync(IPEndPoint endpoint, string name,
        Func<string, CancellationToken, Task<bool>> confirmCode, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(122));
        using var start = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        start.CancelAfter(TimeSpan.FromSeconds(8));
        byte[]? phoneKey = null;
        // This connection can only exchange pairing frames. No local certificate, profile
        // or operation is exposed until the two users have authenticated this public key.
        using var tls = await OpenTlsAsync(endpoint, mutual: false, fp =>
        {
            if (phoneKey == null) phoneKey = fp;
            return CryptographicOperations.FixedTimeEquals(phoneKey, fp);
        }, start.Token).ConfigureAwait(false);
        if (phoneKey == null) throw new SrapException("identity_conflict");
        string pin = Convert.ToHexString(phoneKey);
        int minor = await HelloAsync(tls, 1, start.Token).ConfigureAwait(false);
        var nonce = RandomNumberGenerator.GetBytes(32);
        try
        {
            string begin = RequestId();
            await WriteAsync(tls, "pair.nearby.begin", new()
            {
                ["publicKey"] = SrapInvite.Encode(identity.PublicKey),
                ["commitment"] = SrapInvite.Encode(NearbyCommitment(identity.Fingerprint, nonce))
            }, start.Token, begin).ConfigureAwait(false);
            var reply = await ReadAsync(tls, start.Token).ConfigureAwait(false);
            Match(reply, begin);
            var body = Body(reply);
            if (Text(reply, "type") == "pair.result") throw Error(body);
            if (Text(reply, "type") != "pair.nearby.challenge") throw new SrapException("unsupported");
            var transcript = NearbyTranscript(SrapInvite.Decode(Text(body, "inviteId"), 16, 16),
                SrapInvite.Decode(Text(body, "challenge"), 32, 32), nonce, phoneKey, identity.Fingerprint, minor);
            string prove = RequestId();
            await WriteAsync(tls, "pair.nearby.prove", new()
            {
                ["nonce"] = SrapInvite.Encode(nonce), ["signature"] = SrapInvite.Encode(identity.Sign(transcript)),
                ["displayName"] = name.Length > 64 ? name[..64] : name
            }, start.Token, prove, sensitive: true).ConfigureAwait(false);
            if (await PairResultAsync(tls, prove, start.Token).ConfigureAwait(false) != "pending")
                throw new SrapException("invalid_argument");
            await ConfirmNearbyAsync(tls, prove, NearbyCode(transcript), confirmCode, deadline.Token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(nonce); }

        string finish = RequestId();
        await WriteAsync(tls, "pair.nearby.finish", new(), deadline.Token, finish).ConfigureAwait(false);
        try
        {
            if (await PairResultAsync(tls, finish, deadline.Token).ConfigureAwait(false) != "done")
                throw new SrapException("invalid_argument");
        }
        catch (IOException ex) when (ex is not SrapException && !deadline.IsCancellationRequested)
        {
            // Both parties approved. A fresh pinned mTLS session can recover a lost final receipt.
        }
        tls.Dispose();
        return await ConnectAsync(endpoint, pin, deadline.Token).ConfigureAwait(false);
    }

    private static async Task ConfirmNearbyAsync(SslStream tls, string request, string code,
        Func<string, CancellationToken, Task<bool>> confirmCode, CancellationToken token)
    {
        using var interaction = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remote = PairResultAsync(tls, request, interaction.Token);
        var local = confirmCode(code, interaction.Token).WaitAsync(interaction.Token);
        try
        {
            if (await Task.WhenAny(local, remote).ConfigureAwait(false) == remote)
                RequireConfirmed(await remote.ConfigureAwait(false));
            if (!await local.ConfigureAwait(false)) throw new SrapException("canceled");
            RequireConfirmed(await remote.ConfigureAwait(false));
        }
        finally
        {
            interaction.Cancel();
            // Observe both tasks when rejection, timeout or navigation canceled the peer wait.
            try { await Task.WhenAll((Task)local, remote).ConfigureAwait(false); } catch { }
        }

        static void RequireConfirmed(string state)
        {
            if (state != "confirmed") throw new SrapException("invalid_argument");
        }
    }

    internal static byte[] NearbyCommitment(byte[] pcFingerprint, byte[] nonce)
    {
        if (pcFingerprint.Length != 32 || nonce.Length != 32) throw new ArgumentException("Invalid nearby commitment length.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SRAP1/NEARBY/COMMIT\0"u8);
        hash.AppendData(pcFingerprint);
        hash.AppendData(nonce);
        return hash.GetHashAndReset();
    }

    internal static byte[] NearbyTranscript(byte[] invite, byte[] challenge, byte[] nonce, byte[] phone, byte[] pc, int minor)
    {
        if (invite.Length != 16 || challenge.Length != 32 || nonce.Length != 32 || phone.Length != 32
            || pc.Length != 32 || (uint)minor > ushort.MaxValue) throw new ArgumentException("Invalid nearby transcript.");
        ReadOnlySpan<byte> prefix = "SRAP1/NEARBY/VERIFY\0"u8;
        byte[] result = new byte[prefix.Length + 16 + 32 * 4 + 4];
        using var output = new MemoryStream(result, writable: true);
        output.Write(prefix);
        output.Write(invite);
        output.Write(challenge);
        output.Write(nonce);
        output.Write(phone);
        output.Write(pc);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(result.Length - 4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(result.Length - 2), (ushort)minor);
        return result;
    }

    internal static string NearbyCode(byte[] transcript) =>
        (BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(transcript)) % 100_000_000u)
            .ToString("D8", CultureInfo.InvariantCulture);
}

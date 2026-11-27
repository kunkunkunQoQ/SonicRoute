using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static SonicRoute.AndroidAudio.SrapClient;

namespace SonicRoute.AndroidAudio;

internal sealed partial class SrapSession
{
    internal const string AliasCapability = "identity.alias.adb";
    private readonly int minor;
    private AliasExchange? aliasExchange;

    // Only the authenticated main-session reader may complete either phase.
    private sealed class AliasExchange : IDisposable
    {
        internal readonly string Id = RequestId();
        internal readonly TaskCompletionSource<JsonElement> Begun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<JsonElement> Proven = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CancellationTokenSource Invalidated = new();
        internal bool ProofSent;
        private bool closed;
        internal string? ErrorCode;

        internal void Response(JsonElement body)
        {
            lock (this)
                if (!closed && !Begun.TrySetResult(body)) Fail("identity_conflict");
        }

        internal void Proof(JsonElement body)
        {
            lock (this)
                if (!closed && (!ProofSent || Text(body, "requestId") != Id || !Proven.TrySetResult(body)))
                    Fail("identity_conflict");
        }

        internal void Fail(string code)
        {
            lock (this)
            {
                if (closed || ErrorCode != null) return;
                ErrorCode = code;
                Invalidated.Cancel();
            }
        }

        public void Dispose()
        {
            lock (this)
            {
                closed = true;
                Invalidated.Dispose();
            }
        }
    }

    internal static byte[] AliasProfileRef(string profileId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("SRAP1/PROFILE\0" + profileId));

    internal static byte[] AliasTranscript(byte role, byte[] bindId, byte[] challenge, byte[] phone, byte[] pc,
        byte[] profile, byte[] attachment, byte[] parent, int port, int minor)
    {
        if (role is not (1 or 2) || bindId.Length != 16 || challenge.Length != 32 || phone.Length != 32
            || pc.Length != 32 || profile.Length != 32 || attachment.Length != 16 || parent.Length != 16
            || port is < 1 or > ushort.MaxValue || minor is < 0 or > ushort.MaxValue)
            throw new SrapException("invalid_argument");
        byte[] bytes = new byte[195];
        "SRAP1/ALIAS\0"u8.CopyTo(bytes);
        bytes[12] = role;
        bindId.CopyTo(bytes, 13);
        challenge.CopyTo(bytes, 29);
        phone.CopyTo(bytes, 61);
        pc.CopyTo(bytes, 93);
        profile.CopyTo(bytes, 125);
        attachment.CopyTo(bytes, 157);
        parent.CopyTo(bytes, 173);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(189), (ushort)port);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(191), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(193), (ushort)minor);
        return bytes;
    }

    internal async Task BindAdbAliasAsync(SrapIdentity identity, string fingerprint, string profileId,
        Func<int, CancellationToken, Task<SrapAdbForward>> openForward, Action validate, Action<Action> commit,
        CancellationToken cancel)
    {
        if (!Capabilities.Allows(AliasCapability)) throw new SrapException("unsupported");
        long revision = Capabilities.Revision;
        using var exchange = new AliasExchange();
        if (Interlocked.CompareExchange(ref aliasExchange, exchange, null) != null) throw new SrapException("busy");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel, token, exchange.Invalidated.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(65));
        var ct = deadline.Token;
        long expires = long.MaxValue;
        bool started = false, committed = false;
        void Check()
        {
            if (exchange.ErrorCode is { } code) throw new SrapException(code);
            if (token.IsCancellationRequested) throw new SrapException("stale_session");
            if (!Capabilities.Allows(AliasCapability)) throw new SrapException("permission_lost");
            if (Capabilities.Revision != revision) throw new SrapException("stale_session");
            if (Environment.TickCount64 >= expires) throw new SrapException("timeout");
            ct.ThrowIfCancellationRequested();
            validate();
        }
        try
        {
            Check();
            using var phoneKey = (stream.RemoteCertificate as X509Certificate2)?.GetECDsaPublicKey();
            using var pcKey = (stream.LocalCertificate as X509Certificate2)?.GetECDsaPublicKey();
            byte[] phonePin = Convert.FromHexString(fingerprint);
            if (phoneKey == null || pcKey == null
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(phoneKey.ExportSubjectPublicKeyInfo()), phonePin)
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(pcKey.ExportSubjectPublicKeyInfo()), identity.Fingerprint))
                throw new SrapException("identity_conflict");
            byte[] attachment = RandomNumberGenerator.GetBytes(16), profile = AliasProfileRef(profileId);
            string encodedAttachment = SrapInvite.Encode(attachment), encodedProfile = SrapInvite.Encode(profile);
            started = true;
            await WriteAsync("request", new()
            {
                ["op"] = "alias.bind.begin", ["capabilityRevision"] = revision,
                ["params"] = new JsonObject { ["profileRef"] = encodedProfile, ["attachment"] = encodedAttachment }
            }, exchange.Id, ct).ConfigureAwait(false);
            var response = await exchange.Begun.Task.WaitAsync(ct).ConfigureAwait(false);
            if (Text(response, "status") == "error") throw Error(response);
            if (Text(response, "status") != "done" || !response.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("port", out var portField)
                || !portField.TryGetInt32(out int port) || port is < 1 or > 65535
                || !result.TryGetProperty("expiresInMs", out var timeout) || !timeout.TryGetInt32(out int ms) || ms != 30000)
                throw new SrapException("invalid_argument");
            expires = Environment.TickCount64 + ms;
            deadline.CancelAfter(ms);
            string? encodedId = Text(result, "bindId");
            byte[] bindId = SrapInvite.Decode(encodedId, 16, 16);
            byte[] challenge = SrapInvite.Decode(Text(result, "challenge"), 32, 32);
            var transcript = AliasTranscript(1, bindId, challenge, phonePin, identity.Fingerprint, profile, attachment,
                Convert.FromHexString(sessionId), port, minor);
            Check();
            await using (var forward = await openForward(port, ct).ConfigureAwait(false))
            {
                using var tls = await new SrapClient(identity).OpenAliasAsync(forward, fingerprint, ct).ConfigureAwait(false);
                if (await HelloAsync(tls, 1, ct).ConfigureAwait(false) != minor) throw new SrapException("version_mismatch");
                Check();
                string proveId = RequestId();
                lock (exchange) exchange.ProofSent = true;
                await SrapClient.WriteAsync(tls, "alias.bind.prove", new()
                {
                    ["bindId"] = encodedId, ["profileRef"] = encodedProfile, ["attachment"] = encodedAttachment,
                    ["parentSession"] = sessionId, ["signature"] = SrapInvite.Encode(identity.Sign(transcript))
                }, ct, proveId).ConfigureAwait(false);
                using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var forwardEnd = ReadAliasEndAsync(tls, proveId, read.Token);
                try
                {
                    await Task.WhenAny(forwardEnd, exchange.Proven.Task).WaitAsync(ct).ConfigureAwait(false);
                    if (forwardEnd.IsCompleted) await forwardEnd.ConfigureAwait(false);
                    var proof = await exchange.Proven.Task.WaitAsync(ct).ConfigureAwait(false);
                    transcript[12] = 2;
                    if (Text(proof, "bindId") != encodedId || Text(proof, "profileRef") != encodedProfile
                        || Text(proof, "attachment") != encodedAttachment || !phoneKey.VerifyData(transcript,
                            SrapInvite.Decode(Text(proof, "signature"), 8, 72), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                        throw new SrapException("identity_conflict");
                }
                finally
                {
                    read.Cancel();
                    try { await forwardEnd.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (read.IsCancellationRequested) { }
                }
            }
            lock (exchange)
            {
                Check();
                commit(Check); // The store repeats the generation checks inside its durable transaction.
                committed = true;
            }
        }
        catch (FormatException) { throw new SrapException("invalid_argument"); }
        catch (CryptographicException) { throw new SrapException("identity_conflict"); }
        catch (OperationCanceledException)
        {
            throw new SrapException(exchange.ErrorCode ?? (token.IsCancellationRequested ? "stale_session" : cancel.IsCancellationRequested ? "canceled" : "timeout"));
        }
        finally
        {
            Interlocked.CompareExchange(ref aliasExchange, null, exchange);
            if (started && !committed && !token.IsCancellationRequested)
            {
                using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
                cleanup.CancelAfter(TimeSpan.FromSeconds(1));
                try { await WriteAsync("cancel", new() { ["requestId"] = exchange.Id }, null, cleanup.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { Dispose(); }
            }
        }
    }

    private static async Task ReadAliasEndAsync(SslStream stream, string id, CancellationToken token)
    {
        var frame = await SrapProtocol.ReadAsync(stream, token).ConfigureAwait(false);
        if (frame == null) return;
        using var document = SrapProtocol.ParseControl(frame);
        var message = document.RootElement;
        if (Text(message, "type") != "alias.bind.result" || Text(message, "id") != id || message.TryGetProperty("session", out _)
            || Text(Body(message), "status") != "error") throw new SrapException("invalid_argument");
        throw Error(Body(message));
    }
}

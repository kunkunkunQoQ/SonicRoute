using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapAliasChecksAsync(string directory, string fixture)
    {
        Directory.CreateDirectory(directory);
        var vector = File.ReadLines(fixture).Where(l => !l.StartsWith('#') && l.Contains('='))
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        byte[] Field(string key) => Convert.FromHexString(vector[key]);
        foreach (byte role in new byte[] { 1, 2 })
            Check(SrapSession.AliasTranscript(role, Field("bindId"), Field("challenge"), Field("androidPin"), Field("windowsPin"),
                Field("profileRef"), Field("attachment"), Field("parentSession"), int.Parse(vector["port"]), int.Parse(vector["minor"]))
                .SequenceEqual(Field(role == 1 ? "windowsTranscript" : "androidTranscript")), $"2B 195-byte signature input, role {role}");

        using var identity = new SrapIdentity(directory);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=SonicRoute Alias Fixture", signer, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        using var cert = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        CryptographicOperations.ZeroMemory(pfx);
        byte[] phonePin = SHA256.HashData(signer.ExportSubjectPublicKeyInfo());
        string pin = Convert.ToHexString(phonePin);
        var adb = new AndroidDevice("fixture", "Same model", "device", false, "fixture", "usb-123")
            { ConnectionTarget = AdbTransportTarget.CreateDirect("usb-123"), IdentityFingerprint = "fixture-proof" };
        Check(AdbClient.SupportsAliasForward(adb) && !AdbClient.SupportsAliasForward(adb with { ConnectionTarget = null })
            && !AdbClient.SupportsAliasForward(adb with { ConnectionTarget = AdbTransportTarget.CreateSocket("1", "test") }),
            "only an owned ADB attachment enables the private forward");
        var statePath = Path.Combine(directory, "alias-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new AndroidPhoneStore(statePath);
        store.RememberDevice(adb);
        string id = store.Profile.Id;
        store.Rename("Preserved ADB");
        store.Retain(new("example.adb", "ADB", 12345, ""));
        var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult<IReadOnlyList<AndroidDevice>>([adb]));
        discovery.Publish([adb]);
        await using var transport = new SrapPhoneTransport(store, directory);
        var manager = new AndroidConnectionManager(store, () => discovery, app: transport);
        string appId = store.RegisterApp(pin, IPEndPoint.Parse("127.0.0.1:12345"), store.ManagementRevision).Id;
        store.Retain(new("example.app", "App", 0, ""));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var ct = deadline.Token;
        using var mainListener = new TcpListener(IPAddress.Loopback, 0);
        mainListener.Start();
        var endpoint = (IPEndPoint)mainListener.LocalEndpoint;
        string parent = Guid.NewGuid().ToString("N");
        async Task<SslStream> AcceptHello(TcpListener listener, bool primary)
        {
            var socket = await listener.AcceptSocketAsync(ct);
            var tls = new SslStream(new NetworkStream(socket, ownsSocket: true));
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck, AllowTlsResume = false,
                    RemoteCertificateValidationCallback = (_, leaf, _, _) =>
                    {
                        using var key = (leaf as X509Certificate2)?.GetECDsaPublicKey();
                        return key != null && SHA256.HashData(key.ExportSubjectPublicKeyInfo()).SequenceEqual(identity.Fingerprint);
                    }
                }, ct);
                var hello = await SrapClient.ReadAsync(tls, ct);
                await SrapClient.WriteAsync(tls, "hello", new() { ["protocol"] = SrapProtocol.Name, ["major"] = 1,
                    ["minorMin"] = 0, ["minorMax"] = 0 }, ct, SrapClient.Text(hello, "id"));
                if (primary)
                    await SrapClient.WriteAsync(tls, "capabilities", new() { ["capabilityRevision"] = 1,
                        ["entries"] = new JsonArray(new JsonObject { ["id"] = SrapSession.AliasCapability, ["state"] = "available" }) },
                        ct, session: parent);
                return tls;
            }
            catch { tls.Dispose(); throw; }
        }
        var accept = AcceptHello(mainListener, true);
        using var session = await new SrapClient(identity).ConnectAsync(endpoint, pin, ct);
        using var main = await accept;
        var app = transport.Admit(store.Profile, session);
        Check(manager.IsCurrent(app) && manager.IsCurrent(adb) && store.Profiles.Count == 2, "pair leaves two independent admitted profiles");
        long seq = 0;
        foreach (var scenario in new[] { "wrong signature", "stale profile", "accepted" })
        {
            using var aliasListener = new TcpListener(IPAddress.Loopback, 0);
            aliasListener.Start();
            int port = ((IPEndPoint)aliasListener.LocalEndpoint).Port;
            var ready = AcceptHello(aliasListener, false);
            long rev = store.LibraryRevision;
            int localPort = 0;
            var binding = session.BindAdbAliasAsync(identity, pin, id, async (number, token) =>
            {
                Check(number == port, "challenge binds the selected remote port");
                var remote = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await remote.ConnectAsync(IPAddress.Loopback, port, token);
                var forward = await SrapAdbForward.OpenAsync(new NetworkStream(remote, ownsSocket: true), token);
                localPort = forward.Endpoint.Port;
                return forward;
            }, () => { }, validate => store.MergeVerifiedAppAlias(id, appId, pin, rev, true, validate, null), ct);
            var begin = await SrapClient.ReadAsync(main, ct);
            var parameters = SrapClient.Body(begin).GetProperty("params");
            var profileRef = parameters.GetProperty("profileRef").GetString()!;
            var attachment = parameters.GetProperty("attachment").GetString()!;
            byte[] bindId = RandomNumberGenerator.GetBytes(16), challenge = RandomNumberGenerator.GetBytes(32);
            await SrapClient.WriteAsync(main, "response", new() { ["status"] = "done", ["result"] = new JsonObject
            {
                ["bindId"] = SrapInvite.Encode(bindId), ["challenge"] = SrapInvite.Encode(challenge), ["port"] = port, ["expiresInMs"] = 30000
            } }, ct, SrapClient.Text(begin, "id"), parent);
            using var proofStream = await ready;
            var proof = await SrapClient.ReadAsync(proofStream, ct);
            var body = SrapClient.Body(proof);
            var transcript = SrapSession.AliasTranscript(1, bindId, challenge, phonePin, identity.Fingerprint,
                SrapInvite.Decode(profileRef, 32, 32), SrapInvite.Decode(attachment, 16, 16), Convert.FromHexString(parent), port, 0);
            using var pcKey = identity.Certificate.GetECDsaPublicKey();
            Check(SrapClient.Text(proof, "type") == "alias.bind.prove" && !proof.TryGetProperty("session", out _)
                && SrapClient.Text(body, "parentSession") == parent && SrapClient.Text(body, "attachment") == attachment
                && pcKey!.VerifyData(transcript, SrapInvite.Decode(SrapClient.Text(body, "signature"), 8, 72), HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence), "mTLS forward checks role-one DER proof and parent session");
            if (scenario == "stale profile") store.Rename("changed while awaiting proof");
            transcript[12] = scenario == "wrong signature" ? (byte)1 : (byte)2;
            var ev = new JsonObject { ["type"] = "event", ["session"] = parent, ["seq"] = ++seq, ["body"] = new JsonObject
            {
                ["name"] = "alias.bind.proof", ["bindId"] = SrapInvite.Encode(bindId), ["requestId"] = SrapClient.Text(begin, "id"),
                ["profileRef"] = profileRef, ["attachment"] = attachment,
                ["signature"] = SrapInvite.Encode(signer.SignData(transcript, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            } };
            await main.WriteAsync(SrapProtocol.Encode(new(SrapFrameType.Control, 0, JsonSerializer.SerializeToUtf8Bytes(ev))), ct);
            string? error = null;
            try { await binding; }
            catch (SrapException ex) { error = ex.Code; }
            if (scenario != "accepted")
            {
                var canceled = await SrapClient.ReadAsync(main, ct);
                Check(error == (scenario == "stale profile" ? "stale_session" : "identity_conflict") && store.Profiles.Count == 2
                    && SrapClient.Text(canceled, "type") == "cancel" && SrapClient.Text(SrapClient.Body(canceled), "requestId") == SrapClient.Text(begin, "id"),
                    scenario + " preserves both profiles and cancels only the original request");
            }
            else Check(error == null && store.Profiles.Count == 1 && store.Profile.Id == id && store.Profile.Name == "Preserved ADB"
                && store.Profile.RetainedApps.Count == 2 && store.FindProfile(app)?.Id == id, "verified transaction merges pins while preserving ADB ID and name");
            using var probe = new TcpClient();
            bool released = false;
            try { await probe.ConnectAsync(IPAddress.Loopback, localPort, ct); } catch (SocketException) { released = true; }
            Check(released, "private IPv4 forward was removed after " + scenario);
        }
        Check(!manager.IsCurrent(app), "old App-only attachment cannot run work after migration");
        transport.Close(appId);
        var reconnect = AcceptHello(mainListener, true);
        using var fresh = await new SrapClient(identity).ConnectAsync(endpoint, pin, ct);
        using var server = await reconnect;
        var linked = transport.Admit(store.Profile, fresh);
        Check(manager.Resolve(adb) == adb, "both channels available uses ADB by default");
        manager.SelectAppTransport(id, pin, true);
        Check(manager.Resolve(adb) == linked && manager.OnlineDevices.Count == 1 && AndroidDeviceIdentity.Key(linked) == AndroidDeviceIdentity.Key(adb)
            && new AndroidPhoneStore(statePath).Profile.PreferAppTransport, "App preference persists and both channels share the original output key");
        discovery.Publish([]);
        Check(manager.Resolve(adb) == linked, "ADB loss keeps the linked App online");
        discovery.Publish([adb, adb with { ConnectionTarget = AdbTransportTarget.CreateDirect("usb-234"), TransportId = "usb-234" }]);
        Check(manager.Get(adb).State == AndroidManagedConnectionState.IdentityConflict, "ambiguous two-device evidence prohibits even preferred App fallback");
        discovery.Publish([adb]);
        transport.RevokeTrust(id, pin);
        Check(manager.Resolve(adb) == adb && !manager.IsCurrent(linked) && !store.Profile.ManuallyDisconnected, "revoking App trust preserves independent ADB connectivity");
        manager.SelectAppTransport(id, pin, null);
        Check(new AndroidPhoneStore(statePath).Profile is { AppPublicKeySha256: null, PreferAppTransport: false }
            && store.Profile.Id == id, "explicit unlink forgets the local binding and keeps the ADB profile");
        Console.WriteLine($"PASS {_checks} SRAP alias checks");
    }
}

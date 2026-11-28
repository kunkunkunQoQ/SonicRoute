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
        using var cert = SrapFixtureCertificate(signer);
        byte[] phonePin = SHA256.HashData(signer.ExportSubjectPublicKeyInfo());
        string pin = Convert.ToHexString(phonePin);
        var direct = new AndroidDevice("fixture", "Same model", "device", false, "fixture", "usb-123")
            { ConnectionTarget = AdbTransportTarget.CreateDirect("usb-123"), IdentityFingerprint = "fixture-proof" };
        Check(AdbClient.SupportsAliasForward(direct) && !AdbClient.SupportsAliasForward(direct with { ConnectionTarget = null })
            && !AdbClient.SupportsAliasForward(direct with { ConnectionTarget = AdbTransportTarget.CreateSocket("1", "test") }),
            "an unqualified ADB serial or mismatched transport id cannot open a proof socket");
        await using var shared = new FakeAdbServer();
        var socket = new AdbSocketClient(shared.EndPoint);
        await using var client = new AdbClient(directory, socket);
        var adb = (await client.DevicesAsync(default)).Single();
        Check(AdbClient.SupportsAliasForward(adb) && AdbTransportTarget.TrySocketOwner(adb, out _),
            "a verified daemon generation and exact external ADB transport id can open a proof socket");
        var statePath = Path.Combine(directory, "alias-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new AndroidPhoneStore(statePath);
        store.RememberDevice(adb);
        string id = store.Profile.Id;
        store.Rename("Preserved ADB");
        var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult<IReadOnlyList<AndroidDevice>>([adb]));
        discovery.Publish([adb]);
        await using var transport = new SrapPhoneTransport(store, directory);
        var manager = new AndroidConnectionManager(store, () => discovery, app: transport, adb: client);
        string appId = store.RegisterApp(pin, IPEndPoint.Parse("127.0.0.1:12345"), store.ManagementRevision).Id;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var ct = deadline.Token;
        using var mainListener = new TcpListener(IPAddress.Loopback, 0);
        mainListener.Start();
        var endpoint = (IPEndPoint)mainListener.LocalEndpoint;
        string parent = Guid.NewGuid().ToString("N");
        Task<SslStream> AcceptHello(TcpListener listener, bool primary) => AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint,
            primary ? parent : null, new JsonArray(new JsonObject { ["id"] = SrapSession.AliasCapability, ["state"] = "available" }), ct);
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
            Task binding;
            if (scenario == "accepted")
            {
                shared.AliasListener = (IPEndPoint)aliasListener.LocalEndpoint;
                binding = manager.BindAppAsync(id, adb, appId, app, true, ct);
            }
            else binding = session.BindAdbAliasAsync(identity, pin, id, async (number, token) =>
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
                && store.FindProfile(app)?.Id == id, "verified transaction links the authenticated peer while preserving ADB ID and name");
            if (scenario == "accepted")
                Check(shared.Commands.Count(c => c == "tcp:" + port) == 1
                    && shared.Requests.Contains("host:transport-id:" + adb.TransportId) && shared.Requests.Contains("host:track-devices-l")
                    && !shared.Requests.Any(c => c.Contains("forward") || c.Contains("kill")),
                    "normal binding uses one approved loopback service on the chosen shared transport and leaves the daemon's forwards alone");
            else
            {
                using var probe = new TcpClient();
                bool released = false;
                try { await probe.ConnectAsync(IPAddress.Loopback, localPort, ct); } catch (SocketException) { released = true; }
                Check(released, "private IPv4 forward was removed after " + scenario);
            }
        }
        Check(!manager.IsCurrent(app), "old App-only attachment cannot run work after migration");
        transport.Close(appId);
        var reconnect = AcceptHello(mainListener, true);
        using var fresh = await new SrapClient(identity).ConnectAsync(endpoint, pin, ct);
        using var server = await reconnect;
        var linked = transport.Admit(store.Profile, fresh);
        Check(manager.Resolve(adb) == adb, "both channels available uses ADB by default");
        var operations = new SrapOperationAdapter(new AdbOperationAdapter(client), transport, manager.IsCurrent);
        var pinned = new AndroidApp("example.saved", "Saved ADB app", 10080, "media");
        const string folder = "/sdcard/Download/ADB";
        store.Retain(pinned);
        store.SetSmartVolume(pinned.Package, false);
        store.SetReceiveFolder(id, folder);
        var adbFiles = operations.OpenFiles(adb);
        var adbClipboard = operations.OpenClipboard(adb);
        Check(operations.CanWriteFiles(adb) && operations.CanSyncClipboard(adb) && operations.FileRoot(adb) == "/sdcard"
            && store.Profile.QuickPanelApps.Single() == pinned && !store.IsSmartVolumeEnabled(adb, pinned.Package)
            && store.Profile.ReceiveFolder == folder, "ADB owns file, clipboard, pin, smart-volume and folder settings");

        var unknownUid = new AndroidApp("example.app", "App", 0, "");
        var verifiedApp = await manager.ResolveDisplayAppAsync(adb, unknownUid, (selected, package, _) =>
            Task.FromResult<IReadOnlyList<AndroidApp>>(selected == adb && package == unknownUid.Package ? [unknownUid with { Uid = 10200 }] : []), ct);
        Check(verifiedApp.Package == unknownUid.Package && verifiedApp.Uid == 10200,
            "a live App package obtains its actual UID from the exact bound ADB attachment before interactive launch");
        bool staleCatalog = false;
        try
        {
            await manager.ResolveDisplayAppAsync(adb, unknownUid, (_, _, _) =>
            {
                discovery.Publish([]);
                return Task.FromResult<IReadOnlyList<AndroidApp>>([unknownUid with { Uid = 10200 }]);
            }, ct);
        }
        catch (SrapException ex) when (ex.Code == "stale_session") { staleCatalog = true; }
        discovery.Publish([adb]);
        Check(staleCatalog, "a UID read completing after ADB loss cannot open the package");
        var adbLifetime = manager.OperationToken(adb);
        manager.SelectAppTransport(id, pin, true);
        Check(adbLifetime.IsCancellationRequested && !operations.CanSyncClipboard(linked) && !operations.CanWriteFiles(linked)
            && operations.FileRoot(linked) == null && !operations.CanSyncClipboard(adb) && !operations.CanWriteFiles(adb),
            "switching one bound phone to App cancels old ADB work and hides files and clipboard on both handles");
        async Task Rejected(Func<Task> action, string reason, string label)
        {
            try { await action(); }
            catch (SrapException ex) when (ex.Code == reason) { Check(true, label); return; }
            throw new InvalidOperationException(label);
        }
        await Rejected(() => operations.OpenFiles(linked).ListAsync(folder, ct), "unsupported", "App files cannot fall back to ADB");
        await Rejected(() => operations.OpenClipboard(linked).SendAsync("fixture", 0, ct), "unsupported", "App clipboard cannot fall back to ADB");
        await Rejected(() => operations.EncodersAsync(linked, ct), "unsupported", "ADB encoder choices stay on the ADB transport");
        await Rejected(() => adbFiles.ListAsync(folder, ct), "stale_session", "an old ADB browser refuses work after a switch");
        await Rejected(() => adbClipboard.SendAsync("fixture", 0, ct), "stale_session", "an old ADB clipboard refuses work after a switch");
        store.Remove(pinned.Package);
        store.Retain(pinned with { Package = "example.app.only" });
        store.SetSmartVolume(pinned.Package, true);
        store.SetReceiveFolder(id, "/sdcard/Download/Other");
        await store.FlushAsync();
        var preserved = new AndroidPhoneStore(statePath).Profile;
        Check(preserved.RetainedApps.Single() == pinned && preserved.DisabledSmartVolumeApps.Contains(pinned.Package)
            && preserved.ReceiveFolder == folder && !store.IsSmartVolumeEnabled(linked, pinned.Package),
            "App preference changes cannot overwrite the bound phone's ADB pins, gain or receive folder");
        Check(manager.VerifiedAdbForDisplay(linked) == adb && manager.VerifiedAdbForDisplay(app) == null,
            "the interactive-window switch resolves only the live, cryptographically linked ADB profile");
        Check(manager.Resolve(adb) == linked && manager.OnlineDevices.Count == 1 && AndroidDeviceIdentity.Key(linked) == AndroidDeviceIdentity.Key(adb)
            && new AndroidPhoneStore(statePath).Profile.PreferAppTransport, "App preference persists and both channels share the original output key");
        discovery.Publish([]);
        Check(manager.VerifiedAdbForDisplay(linked) == null, "an offline ADB profile offers no interactive-window switch");
        Check(manager.Resolve(adb) == linked, "ADB loss keeps the linked App online");
        discovery.Publish([adb, adb with { ConnectionTarget = AdbTransportTarget.CreateDirect("usb-234"), TransportId = "usb-234" }]);
        Check(manager.Get(adb).State == AndroidManagedConnectionState.IdentityConflict, "ambiguous two-device evidence prohibits even preferred App fallback");
        Check(manager.VerifiedAdbForDisplay(linked) == null, "ambiguous ADB devices offer no interactive-window switch");
        discovery.Publish([adb]);
        transport.RevokeTrust(id, pin);
        Check(manager.Resolve(adb) == adb && !manager.IsCurrent(linked) && !store.Profile.ManuallyDisconnected, "revoking App trust preserves independent ADB connectivity");
        store.SetSmartVolume(pinned.Package, true);
        Check(operations.CanWriteFiles(adb) && operations.CanSyncClipboard(adb) && store.IsSmartVolumeEnabled(adb, pinned.Package)
            && store.Profile.QuickPanelApps.Single() == pinned && store.Profile.ReceiveFolder == folder,
            "returning to ADB restores the saved ADB pane and editable smart volume");
        manager.SelectAppTransport(id, pin, null);
        Check(new AndroidPhoneStore(statePath).Profile is { AppPublicKeySha256: null, PreferAppTransport: false }
            && store.Profile.Id == id, "explicit unlink forgets the local binding and keeps the ADB profile");
        await using var lostServer = new FakeAdbServer();
        var lostSocket = new AdbSocketClient(lostServer.EndPoint);
        await using var lostClient = new AdbClient(directory, lostSocket);
        var selected = (await lostClient.DevicesAsync(ct)).Single();
        await using var lease = await lostClient.CaptureAliasAttachmentAsync(selected, ct);
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.Disconnected += () => gone.TrySetResult();
        lostServer.EnqueueTrackUpdate("fake-usb-1\tdevice model:Fake_Phone transport_id:8\n");
        await gone.Task.WaitAsync(ct);
        bool rejected = false;
        try { await using var _ = await lease.OpenForwardAsync(1, ct); }
        catch (SrapException e) when (e.Code == "stale_session") { rejected = true; }
        Check(rejected && !lease.IsConnected && !lostServer.Commands.Any(cmd => cmd.StartsWith("tcp:")),
            "a replacement transport id on the same serial retires the binding without opening another device");
        Console.WriteLine($"PASS {_checks} SRAP alias checks");
    }

    private static async Task SrapLiveAliasAsync(string directory, string transportId, string preference)
    {
        if (!ulong.TryParse(transportId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out ulong number) || number == 0) throw new ArgumentException("A numeric, current ADB transport ID is required.");
        bool keepAdb = preference switch { "keep-adb" => true, "keep-app" => false, _ => throw new ArgumentException("Choose keep-adb or keep-app for setting conflicts.") };
        string path = Path.Combine(directory, "android-phone.json");
        if (!File.Exists(path)) throw new FileNotFoundException("The existing paired Native Full profile is required.", path);
        var store = new AndroidPhoneStore(path);
        var settings = new AndroidAudioSettingsStore(Path.Combine(directory, "android-audio.json"));
        await using var adb = new AdbClient(directory, new AdbSocketClient());
        await using var lan = new SrapPhoneTransport(store, directory);
        using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = overall.Token;
        Console.WriteLine("STAGE verify_adb_transport " + transportId);
        var raw = await adb.DevicesAsync(ct, deduplicate: false);
        var device = raw.Single(d => d.TransportId == transportId && d.State == "device");
        if (!AdbClient.SupportsAliasForward(device) || !AdbTransportTarget.TrySocketOwner(device, out _)
            || AndroidConnectionManager.IsAmbiguous(device, raw) || store.IsRemoved(device) || store.HasConflictingIdentity(device))
            throw new SrapException("identity_conflict");
        if (store.FindProfile(device)?.AppPublicKeySha256 != null)
        { Console.WriteLine("SKIP already linked; a new proof was not requested."); return; }
        var paired = store.Profiles.Single(p => p.LastDevice?.IsApp == true && p.LastAppAddress != null && !p.AppTrustRevoked && !p.ManuallyDisconnected);
        store.ObserveDevices([device]);
        await store.FlushAsync();
        var saved = store.FindProfile(device) ?? throw new SrapException("stale_session");
        var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult(raw));
        discovery.Publish(raw);
        var manager = new AndroidConnectionManager(store, () => discovery, settings.MigrateConnectionPreferences, lan, adb, settings.MigrateConnectionPreferences);
        Console.WriteLine("STAGE trusted_reconnect");
        var phone = await lan.ReconnectAsync(paired, ct);
        var session = lan.Session(phone) ?? throw new SrapException("stale_session");
        session.RequestSent += LogSrapRequest;
        Console.WriteLine("STAGE request_phone_link_confirmation");
        await manager.BindAppAsync(saved.Id, device, paired.Id, phone, keepAdb, ct);
        var linked = new AndroidPhoneStore(path).Profiles.Single(p => p.Id == saved.Id);
        Check(linked.AppPublicKeySha256 == session.PeerFingerprint && linked.LastDevice?.IsApp == false
            && !store.Profiles.Any(p => p.Id == paired.Id), "real shared ADB and phone app proofs committed to the selected ADB profile");
        Console.WriteLine("STAGE verify_linked_app_operations");
        var connected = await lan.ReconnectAsync(linked, ct);
        var fresh = lan.Session(connected) ?? throw new SrapException("stale_session");
        fresh.RequestSent += LogSrapRequest;
        manager.SelectAppTransport(linked.Id, phone.Serial, true);
        try
        {
            Check(manager.Resolve(device) == connected && new AndroidPhoneStore(path).Profiles.Single(p => p.Id == linked.Id).PreferAppTransport,
                "the linked app is selected with a saved preference");
            var outputs = await fresh.RequestAsync("audio.devices.list", "audio.devices", new(), ct);
            Check(outputs.GetProperty("outputs").GetArrayLength() > 0, "the selected linked app returned its negotiated audio devices");
        }
        finally { manager.SelectAppTransport(linked.Id, phone.Serial, linked.PreferAppTransport); }
        Check(manager.Resolve(device) == device, "the original ADB transport is restored");
    }
}

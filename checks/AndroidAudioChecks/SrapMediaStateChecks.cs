using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task<bool?> SrapReadPackageStateAsync(SrapSession session, string package, CancellationToken token)
    {
        if (package is not ("org.sonicroute.app" or "bin.mt.plus")) throw new ArgumentException("Expected a test player.");
        if (!session.Capabilities.Entries.TryGetValue("media.control", out var cap) ||
            cap is not { State: "available", Scope: "active_media" } || cap.Limits?.GetValueOrDefault("packageState") != "1")
            return null;
        var result = await session.RequestAsync("media.get", "media.control",
            new() { ["packageName"] = package, ["fields"] = "state" }, token);
        if (result.EnumerateObject().Count() != 2 || SrapClient.Text(result, "packageName") != package ||
            !result.TryGetProperty("playing", out var playing) || playing.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new SrapException("invalid_argument");
        return playing.GetBoolean();
    }

    private static async Task SrapVerifyMediaStateAsync(SrapSession session, string package, CancellationToken token)
    {
        bool? state = await SrapReadPackageStateAsync(session, package, token);
        if (state == null) { Console.WriteLine("SKIP scoped media state unavailable"); return; }
        Console.WriteLine($"MEDIA_STATE packageName={package} playing={(state.Value ? "true" : "false")}");
        Check(!session.Token.IsCancellationRequested, "the selected phone player returned its state on the authenticated connection");
    }

    private static async Task ControlOwnedMediaFixtureAsync(SrapSession session, CancellationToken token)
    {
        const string package = "org.sonicroute.app";
        Console.WriteLine("MEDIA_FIXTURE_KEY_REQUIRED");
        string? key = await Console.In.ReadLineAsync(token);
        if (!Guid.TryParseExact(key, "D", out var id) || id.ToString("D") != key) throw new IOException("Expected the locally identified test session.");
        foreach (var action in new[] { "pause", "play" })
        {
            var answer = await session.RequestAsync("media.control", "media.control", new() { ["sessionKey"] = key, ["action"] = action }, token);
            var player = answer.GetProperty("player");
            bool playing = action == "play";
            Check(SrapClient.Text(player, "package") == package && SrapClient.Text(player, "sessionKey") == key &&
                player.GetProperty("playing").GetBoolean() == playing &&
                await SrapReadPackageStateAsync(session, package, token) == playing,
                "Android confirmed the remote " + action + " on the scoped test WAV and independently returned its new state");
        }
    }

    private static async Task SrapMediaStateFixtureAsync(string directory, string vectors)
    {
        Directory.CreateDirectory(directory);
        using var identity = new SrapIdentity(directory);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = SrapFixtureCertificate(signer);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = deadline.Token;
        string parent = Guid.NewGuid().ToString("N"), pin = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
        JsonObject Capability(bool scoped) => new() { ["id"] = "media.control", ["state"] = "available", ["scope"] = "active_media",
            ["limits"] = scoped ? new JsonObject { ["packageState"] = "1" } : new JsonObject() };
        var accept = AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint, parent, [Capability(true)], ct);
        using var session = await new SrapClient(identity).ConnectAsync((IPEndPoint)listener.LocalEndpoint, pin, ct);
        using var peer = await accept;
        var packages = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(vectors).Where(row => row.Length > 0 && row[0] != '#'))
        {
            string[] fields = line.Split('|');
            if (fields.Length != 3 || !packages.Add(fields[0])) throw new InvalidDataException("Invalid package-state fixture.");
            using var expected = JsonDocument.Parse(fields[1]);
            var result = JsonNode.Parse(fields[2])!.AsObject();
            var read = SrapReadPackageStateAsync(session, fields[0], ct);
            var message = await SrapClient.ReadAsync(peer, ct);
            var body = SrapClient.Body(message);
            Check(SrapClient.Text(body, "op") == "media.get" && JsonElement.DeepEquals(expected.RootElement, body.GetProperty("params")),
                "the read-only client requests only the negotiated package and state fields");
            await SrapClient.WriteAsync(peer, "response", new() { ["status"] = "done", ["result"] = result }, ct, SrapClient.Text(message, "id"), parent);
            Check(await read == (bool)result["playing"]!, "the phone's exact package and playing value match the shared sample");
        }
        Check(packages.SetEquals(["org.sonicroute.app", "bin.mt.plus"]), "both permitted real-phone test players are covered");
        var initialRevision = session.Capabilities.Revision;
        var changing = SrapReadPackageStateAsync(session, "org.sonicroute.app", ct);
        var firstRead = await SrapClient.ReadAsync(peer, ct);
        JsonObject Stale() => new() { ["status"] = "error", ["error"] = "stale_session", ["reason"] = "capability_revision_mismatch" };
        await SrapClient.WriteAsync(peer, "response", Stale(), ct, SrapClient.Text(firstRead, "id"), parent);
        await SrapClient.WriteAsync(peer, "capabilities", new() { ["capabilityRevision"] = initialRevision + 1,
            ["entries"] = new JsonArray(Capability(true)) }, ct, session: parent);
        var secondRead = await SrapClient.ReadAsync(peer, ct);
        var retryBody = SrapClient.Body(secondRead);
        Check(SrapClient.Text(firstRead, "id") != SrapClient.Text(secondRead, "id") &&
            retryBody.GetProperty("capabilityRevision").GetInt64() == initialRevision + 1 &&
            JsonElement.DeepEquals(SrapClient.Body(firstRead).GetProperty("params"), retryBody.GetProperty("params")),
            "a stale package-only read waits for a newer revision and repeats the same scoped request");
        await SrapClient.WriteAsync(peer, "response", new() { ["status"] = "done", ["result"] = new JsonObject {
            ["packageName"] = "org.sonicroute.app", ["playing"] = true } }, ct, SrapClient.Text(secondRead, "id"), parent);
        Check(await changing == true, "the fresh revision supplies the test player's state");
        var nextRevision = initialRevision + 2;
        foreach (string op in new[] { "media.get", "media.control" })
        {
            var parameters = op == "media.get" ? new JsonObject { ["watchId"] = Guid.NewGuid().ToString("D") } :
                new JsonObject { ["sessionKey"] = Guid.NewGuid().ToString("D"), ["action"] = "pause" };
            var oneShot = session.RequestAsync(op, "media.control", parameters, ct);
            var request = await SrapClient.ReadAsync(peer, ct);
            await SrapClient.WriteAsync(peer, "response", Stale(), ct, SrapClient.Text(request, "id"), parent);
            await SrapClient.WriteAsync(peer, "capabilities", new() { ["capabilityRevision"] = nextRevision++,
                ["entries"] = new JsonArray(Capability(true)) }, ct, session: parent);
            bool stale = false;
            try { await oneShot.WaitAsync(TimeSpan.FromSeconds(2), ct); }
            catch (SrapException ex) when (ex.WireReason == "capability_revision_mismatch") { stale = true; }
            Check(stale && SrapClient.Text(SrapClient.Body(request), "op") == op,
                "a player write or subscription returns its original stale result");
        }
        var bad = SrapReadPackageStateAsync(session, "bin.mt.plus", ct);
        var badRequest = await SrapClient.ReadAsync(peer, ct);
        await SrapClient.WriteAsync(peer, "response", new() { ["status"] = "done", ["result"] = new JsonObject {
            ["packageName"] = "org.sonicroute.app", ["playing"] = true } }, ct, SrapClient.Text(badRequest, "id"), parent);
        bool rejected = false;
        try { await bad; } catch (SrapException e) when (e.Code == "invalid_argument") { rejected = true; }
        Check(rejected, "a reply for a different package never reports a successful state");
        var revisionChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() => revisionChanged.TrySetResult();
        session.CapabilitiesChanged += Changed;
        try
        {
            await SrapClient.WriteAsync(peer, "capabilities", new() { ["capabilityRevision"] = session.Capabilities.Revision + 1,
                ["entries"] = new JsonArray(Capability(false)) }, ct, session: parent);
            await revisionChanged.Task.WaitAsync(ct);
        }
        finally { session.CapabilitiesChanged -= Changed; }
        Check(await SrapReadPackageStateAsync(session, "org.sonicroute.app", ct) == null,
            "a legacy media capability skips the check without requesting a full player list");
        Console.WriteLine($"PASS {_checks} SRAP scoped media checks");
    }
}

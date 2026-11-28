using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapPcListenChecksAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = SrapFixtureCertificate(signer);
        using var identity = new SrapIdentity(directory);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        string parent = Guid.NewGuid().ToString("N"), pin = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
        string first = Guid.NewGuid().ToString("N"), second = Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = stop.Token;
        JsonArray Caps(string? id)
        {
            var limits = new JsonObject { ["pcm"] = SrapSession.PcmEncoding, ["pcListenInitiate"] = "1" };
            if (id != null) limits["pcListenRequest"] = id;
            return [new JsonObject { ["id"] = "audio.play", ["state"] = "available", ["scope"] = "owned_track", ["limits"] = limits },
                new JsonObject { ["id"] = "audio.route.owned", ["state"] = "available" }];
        }
        var accept = AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint, parent, Caps(first), ct);
        using var session = await new SrapClient(identity).ConnectAsync(endpoint, pin, ct);
        using var remote = await accept;
        string path = Path.Combine(directory, parent + ".json");
        var store = new AndroidPhoneStore(path);
        var profile = store.RegisterApp(pin, endpoint, store.ManagementRevision);
        await using var transport = new SrapPhoneTransport(store, directory);
        int captures = 0, ended = 0;
        async Task Capture(Stream output, CancellationToken token)
        {
            Interlocked.Increment(ref captures);
            try
            {
                await output.WriteAsync(new byte[] { 10, 0, 10, 0 }, token);
                await Task.Delay(Timeout.Infinite, token);
            }
            finally { Interlocked.Increment(ref ended); }
        }
        await using var listening = new SrapPcListening(store, transport, Capture);
        store.Changed += listening.Reconcile;
        transport.Changed += listening.Reconcile;
        var device = transport.Admit(profile, session);
        long revision = 1;
        async Task Capabilities(string? request)
        {
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long version = ++revision;
            void Changed() { if (session.Capabilities.Revision >= version) observed.TrySetResult(); }
            session.CapabilitiesChanged += Changed;
            try
            {
                await SrapClient.WriteAsync(remote, "capabilities", new() { ["capabilityRevision"] = version, ["entries"] = Caps(request) }, ct, session: parent);
                await observed.Task.WaitAsync(ct);
            }
            finally { session.CapabilitiesChanged -= Changed; }
        }
        async Task<JsonElement> Read(string op, string? state = null, string? request = null)
        {
            var message = await SrapClient.ReadAsync(remote, ct);
            var body = SrapClient.Body(message);
            var parameters = body.GetProperty("params");
            Check(SrapClient.Text(body, "op") == op && (state == null || SrapClient.Text(parameters, "state") == state)
                && (request == null || SrapClient.Text(parameters, "request") == request), "phone listening RPC " + op + " " + state);
            return message;
        }
        Task Done(JsonElement message, JsonObject? result = null) => SrapClient.WriteAsync(remote, "response",
            new() { ["status"] = "done", ["result"] = result ?? new JsonObject() }, ct, SrapClient.Text(message, "id"), parent);
        int sequence = 0;
        async Task<uint> OpenFromPhone(string request, bool alreadyRequested = false)
        {
            int prior = captures;
            if (!alreadyRequested) await Capabilities(request);
            var start = await Read("audio.play.start");
            var parameters = SrapClient.Body(start).GetProperty("params");
            uint id = parameters.GetProperty("streamId").GetUInt32();
            Check(SrapClient.Text(parameters, "listenRequest") == request && SrapClient.Text(parameters, "targetKey") == "current",
                "only exact lower-case phone request opens a bound current-output track");
            await Done(start, new() { ["encoding"] = SrapSession.PcmEncoding, ["credit"] = 4, ["streamId"] = id });
            var route = await Read("audio.route.set");
            string routeId = SrapClient.Text(route, "id")!;
            await SrapClient.WriteAsync(remote, "response", new() { ["status"] = "pending" }, ct, routeId, parent);
            Check(captures == prior, "capture waits for verified phone routing");
            var routed = new JsonObject { ["type"] = "event", ["session"] = parent, ["seq"] = ++sequence, ["body"] = new JsonObject
                { ["name"] = "audio.routed", ["requestId"] = routeId, ["streamId"] = id, ["targetKey"] = "current",
                    ["actualKey"] = "speaker", ["verified"] = true } };
            await remote.WriteAsync(SrapProtocol.Encode(new(SrapFrameType.Control, 0, JsonSerializer.SerializeToUtf8Bytes(routed))), ct);
            var data = await SrapProtocol.ReadAsync(remote, ct);
            Check(data is { Type: SrapFrameType.Data } && data.StreamId == id && data.Payload[12..].SequenceEqual(new byte[] { 10, 0, 10, 0 }),
                "one requested stream carries fixture PCM");
            return id;
        }

        await Done(await Read("audio.listen.offer", SrapPcListening.Supported ? "needs_authorization" : "unsupported"));
        if (SrapPcListening.Supported)
        {
            var initial = listening.Pending(profile.Id) ?? throw new InvalidOperationException("Expected a local authorization prompt.");
            Check(initial.Request == first && !listening.Answer(initial with { Request = second }, true) && captures == 0 &&
                listening.Answer(initial, false), "only the current request can be answered and explicit rejection keeps audio private");
        }
        await Done(await Read("audio.listen.status", "declined", first));
        Check(captures == 0 && !profile.AllowsPcListening, "no WASAPI worker before explicit authorization");
        if (!SrapPcListening.Supported) return;
        await Capabilities(new string('A', 32));
        var asked = new TaskCompletionSource<PcListeningPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
        void NewPrompt(PcListeningPrompt p) { if (p.Request == second) asked.TrySetResult(p); }
        listening.PermissionRequested += NewPrompt;
        await Capabilities(second);
        var shown = await asked.Task.WaitAsync(ct);
        Check(shown.ProfileId == profile.Id && shown.Fingerprint == pin && captures == 0 && listening.Answer(shown, true),
            "explicit approval of this attachment and request persists authorization before capture");
        listening.PermissionRequested -= NewPrompt;
        await Done(await Read("audio.listen.offer", "available"));
        Check(new AndroidPhoneStore(path).Profile.AllowsPcListening, "exact authorized key persisted before offer");
        string fallback = Path.Combine(directory, "fallback-" + parent + ".json");
        File.Copy(path, fallback + ".bak");
        Check(!new AndroidPhoneStore(fallback).Profile.AllowsPcListening, "backup recovery never revives a withdrawn audio grant");
        uint stream = await OpenFromPhone(second, alreadyRequested: true);
        listening.SetPermission(profile.Id, pin, false);
        await Done(await Read("stream.stop"), new() { ["streamId"] = stream, ["stopped"] = true });
        await Done(await Read("audio.listen.status", "stopped", second));
        await Done(await Read("audio.listen.offer", "needs_authorization"));
        Check(captures == 1 && !listening.HasActiveWork && !new AndroidPhoneStore(path).Profile.AllowsPcListening,
            "revocation cancels capture and closes the track before returning to idle");
        listening.SetPermission(profile.Id, pin, true);
        await Done(await Read("audio.listen.offer", "available"));
        string third = Guid.NewGuid().ToString("N");
        stream = await OpenFromPhone(third);
        await Capabilities(null);
        await Done(await Read("stream.stop"), new() { ["streamId"] = stream, ["stopped"] = true });
        await Done(await Read("audio.listen.status", "stopped", third));
        Check(ended == 2 && !listening.HasActiveWork, "withdrawn or expired phone request retires the worker and AudioTrack");
        listening.SetPermission(profile.Id, pin, false);
        await Done(await Read("audio.listen.offer", "needs_authorization"));
        foreach (bool match in new[] { false, true })
        {
            var initiated = listening.StartOnPhoneAsync(device, ct);
            var message = await Read("audio.listen.request");
            string ticket = SrapClient.Text(SrapClient.Body(message).GetProperty("params"), "request")!;
            await Capabilities(ticket);
            Check(captures == 2 && listening.Pending(profile.Id) == null, "a published PC request alone cannot open an audio stream");
            await Done(message, new() { ["request"] = match ? ticket : first, ["state"] = "requested" });
            if (!match)
            {
                await Done(await Read("audio.listen.status", "stopped", ticket));
                bool rejected = false;
                try { await initiated; } catch (SrapException e) when (e.Code == "invalid_argument") { rejected = true; }
                Check(rejected && !listening.HasActiveWork, "a wrong echo cannot claim the computer's one-time permission");
            }
            else
            {
                await initiated;
                stream = await OpenFromPhone(ticket, alreadyRequested: true);
                Check(listening.StoppableRequest(device) == ticket && !store.Profile.AllowsPcListening, "PC started current authenticated phone without storing a grant");
                await listening.StopOnPhoneAsync(device, ticket);
                await Done(await Read("stream.stop"), new() { ["streamId"] = stream, ["stopped"] = true });
                await Done(await Read("audio.listen.status", "stopped", ticket));
            }
            await Capabilities(null);
        }
        listening.SetPermission(profile.Id, pin, true);
        await Done(await Read("audio.listen.offer", "available"));
        await OpenFromPhone(Guid.NewGuid().ToString("N"));
        transport.Close(profile.Id);
        await listening.DisposeAsync();
        Check(ended == 4, "disconnect joins the active capture worker");
        store.RevokeAppTrust(profile.Id, pin);
        var rebound = store.RegisterApp(pin, endpoint, store.ManagementRevision);
        Check(!rebound.AllowsPcListening, "explicit re-pair after revoked trust requires a new grant");
        store.SetPcListening(rebound.Id, pin, true);
        store.RemoveDevice(rebound.Id);
        var reopened = new AndroidPhoneStore(path);
        Check(!reopened.RegisterApp(pin, endpoint, reopened.ManagementRevision).AllowsPcListening,
            "removed phone cannot restore its saved listening permission");
        store.Changed -= listening.Reconcile;
        transport.Changed -= listening.Reconcile;
    }
}

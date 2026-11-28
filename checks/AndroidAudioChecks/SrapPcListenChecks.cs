using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapPcListenChecksAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await CheckPairedListeningAsync(directory, forceUnsupported: false);
        await CheckPairedListeningAsync(directory, forceUnsupported: true);
    }

    private static async Task CheckPairedListeningAsync(string directory, bool forceUnsupported)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = SrapFixtureCertificate(signer);
        using var identity = new SrapIdentity(directory);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        string parent = Guid.NewGuid().ToString("N");
        string pin = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
        string path = Path.Combine(directory, parent + ".json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var ct = timeout.Token;
        var store = new AndroidPhoneStore(path);
        var profile = store.RegisterApp(pin, endpoint, store.ManagementRevision);
        await using var transport = new SrapPhoneTransport(store, directory);
        int captures = 0;
        var finished = Channel.CreateUnbounded<int>();
        byte[] samples = [10, 0, 10, 0];
        async Task Capture(Stream output, CancellationToken token)
        {
            int number = Interlocked.Increment(ref captures);
            try
            {
                await output.WriteAsync(samples, token);
                await Task.Delay(Timeout.Infinite, token);
            }
            finally { finished.Writer.TryWrite(number); }
        }
        await using var listening = new SrapPcListening(store, transport, Capture, systemCaptureSupported: forceUnsupported ? false : null);
        store.Changed += listening.Reconcile;
        transport.Changed += listening.Reconcile;
        Check(!listening.HasActiveWork && captures == 0 && store.HasUniqueAppOwner(profile.Id, pin),
            "registering a paired fingerprint alone starts no audio source");

        static JsonArray Caps(string? request = null, string playScope = "owned_track", string? misplacedRequest = null)
        {
            var playback = new JsonObject { ["pcm"] = SrapSession.PcmEncoding, ["pcListenInitiate"] = "1" };
            if (request != null) playback["pcListenRequest"] = request;
            var route = new JsonObject();
            if (misplacedRequest != null) route["pcListenRequest"] = misplacedRequest;
            return [
                new JsonObject { ["id"] = "audio.play", ["state"] = "available", ["scope"] = playScope, ["limits"] = playback },
                new JsonObject { ["id"] = "audio.route.owned", ["state"] = "available", ["scope"] = "owned_track", ["limits"] = route },
                new JsonObject { ["id"] = "audio.devices", ["state"] = "available", ["scope"] = "device" }
            ];
        }

        var accept = AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint, parent, Caps(), ct);
        using var originalSession = await new SrapClient(identity).ConnectAsync(endpoint, pin, ct);
        using var originalPeer = await accept;
        var session = originalSession;
        var remote = originalPeer;
        await Reject(() => Task.FromResult(transport.Admit(profile with { AppPublicKeySha256 = new string('0', 64) }, session)), "stale_session");
        var device = transport.Admit(profile, session);
        long revision = 1;
        int sequence = 0;

        async Task Publish(JsonArray entries)
        {
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long version = ++revision;
            void Changed() { if (session.Capabilities.Revision >= version) observed.TrySetResult(); }
            session.CapabilitiesChanged += Changed;
            try
            {
                await SrapClient.WriteAsync(remote, "capabilities", new() { ["capabilityRevision"] = version, ["entries"] = entries }, ct, session: parent);
                await observed.Task.WaitAsync(ct);
            }
            finally { session.CapabilitiesChanged -= Changed; }
        }

        async Task<JsonElement> Read(string op, string? state = null, string? request = null)
        {
            var wire = await SrapClient.ReadAsync(remote, ct);
            var body = SrapClient.Body(wire);
            var parameters = body.GetProperty("params");
            string? actual = SrapClient.Text(body, "op"), actualState = SrapClient.Text(parameters, "state");
            Check(SrapClient.Text(wire, "session") == parent && actual == op && (state == null || actualState == state)
                && (request == null || SrapClient.Text(parameters, "request") == request),
                $"current TLS request {op} {state} (received {actual} {actualState})");
            return wire;
        }

        Task Done(JsonElement message, JsonObject? result = null) => SrapClient.WriteAsync(remote, "response",
            new() { ["status"] = "done", ["result"] = result ?? new JsonObject() }, ct, SrapClient.Text(message, "id"), parent);

        // A reply on the existing TLS connection marks progress without a sleep or a timed, partial TLS read.
        async Task CheckIdle(string reason, int expectedCaptures, string? mayDecline = null)
        {
            var barrier = session.RequestAsync("audio.devices.list", "audio.devices", new(), ct);
            JsonElement message = await SrapClient.ReadAsync(remote, ct);
            var body = SrapClient.Body(message);
            if (mayDecline != null && SrapClient.Text(body, "op") == "audio.listen.status")
            {
                var parameters = body.GetProperty("params");
                Check(SrapClient.Text(parameters, "request") == mayDecline && SrapClient.Text(parameters, "state") == "declined",
                    "unsupported request can only be declined");
                await Done(message);
                message = await SrapClient.ReadAsync(remote, ct);
                body = SrapClient.Body(message);
            }
            Check(SrapClient.Text(message, "session") == parent && SrapClient.Text(body, "op") == "audio.devices.list",
                reason + " sent no track-start request");
            await Done(message, new() { ["outputs"] = new JsonArray() });
            await barrier;
            Check(!listening.HasActiveWork && listening.StoppableRequest(device) == null && Volatile.Read(ref captures) == expectedCaptures, reason);
        }

        async Task<uint> OpenTrack(string request)
        {
            int previous = Volatile.Read(ref captures);
            var start = await Read("audio.play.start");
            var parameters = SrapClient.Body(start).GetProperty("params");
            uint id = parameters.GetProperty("streamId").GetUInt32();
            Check(SrapClient.Text(parameters, "listenRequest") == request && SrapClient.Text(parameters, "targetKey") == "current",
                "one authenticated request is bound to this owned track and the phone's current output");
            await Done(start, new() { ["encoding"] = SrapSession.PcmEncoding, ["credit"] = 4, ["streamId"] = id });
            var route = await Read("audio.route.set");
            var routeParams = SrapClient.Body(route).GetProperty("params");
            Check(routeParams.GetProperty("streamId").GetUInt32() == id && SrapClient.Text(routeParams, "targetKey") == "current",
                "route verification targets the exact new stream");
            string routeId = SrapClient.Text(route, "id")!;
            await SrapClient.WriteAsync(remote, "response", new() { ["status"] = "pending" }, ct, routeId, parent);
            Check(Volatile.Read(ref captures) == previous, "source stays closed until the route is verified");
            var routed = new JsonObject { ["type"] = "event", ["session"] = parent, ["seq"] = ++sequence, ["body"] = new JsonObject
                { ["name"] = "audio.routed", ["requestId"] = routeId, ["streamId"] = id, ["targetKey"] = "current",
                    ["actualKey"] = "speaker", ["verified"] = true } };
            await remote.WriteAsync(SrapProtocol.Encode(new(SrapFrameType.Control, 0, JsonSerializer.SerializeToUtf8Bytes(routed))), ct);
            var frame = await SrapProtocol.ReadAsync(remote, ct);
            Check(frame is { Type: SrapFrameType.Data } && frame.StreamId == id && frame.Payload[12..].SequenceEqual(samples)
                && Volatile.Read(ref captures) == previous + 1 && listening.StoppableRequest(device) == request,
                "only the exact four synthetic PCM bytes reach the requested stream");
            return id;
        }

        async Task TrackStopped(string request, uint stream, int captureNumber)
        {
            var close = await Read("stream.stop");
            Check(SrapClient.Body(close).GetProperty("params").GetProperty("streamId").GetUInt32() == stream, "stop closes the exact active track");
            await Done(close, new() { ["streamId"] = stream, ["stopped"] = true });
            await Done(await Read("audio.listen.status", "stopped", request));
            Check(await finished.Reader.ReadAsync(ct) == captureNumber && !listening.HasActiveWork, "the active source is canceled and joined");
        }

        static async Task Reject(Func<Task> action, string code)
        {
            try { await action(); }
            catch (SrapException ex) when (ex.Code == code) { Check(true, "refused operation with " + code); return; }
            Check(false, "expected refusal " + code);
        }

        bool hardware = !forceUnsupported && SrapPcListening.Supported;
        await Done(await Read("audio.listen.offer", hardware ? "available" : "unsupported"));
        await CheckIdle("admission and offer leave the source idle", 0);
        Check(listening.CanStartOnPhone(device) == hardware, "start is gated by actual host support and the admitted device");
        if (!hardware)
        {
            string unusable = Guid.NewGuid().ToString("N");
            await Reject(() => listening.StartOnPhoneAsync(device, ct), "unsupported");
            await Publish(Caps(unusable));
            await CheckIdle("unsupported host keeps the source closed even on an explicit phone request", 0, unusable);
            return;
        }

        string first = Guid.NewGuid().ToString("N"), second = Guid.NewGuid().ToString("N");
        await Publish(Caps(new string('A', 32)));
        await CheckIdle("uppercase request is ignored", 0);
        await Publish(Caps(misplacedRequest: first));
        await CheckIdle("request advertised on the route capability is ignored", 0);
        await Publish(Caps(second, playScope: "system_media"));
        await Done(await Read("audio.listen.offer", "unsupported"));
        await CheckIdle("an unrelated playback scope cannot open the source", 0, second);
        await Publish(Caps(second));
        await Done(await Read("audio.listen.offer", "available"));
        await CheckIdle("restoring owned-track capability cannot revive an earlier request", 0);
        await Publish(Caps());
        await Publish(Caps(first));
        uint stream = await OpenTrack(first);
        await Publish(Caps(first));
        await Reject(() => listening.StartOnPhoneAsync(device, ct), "busy");
        await Reject(() => listening.StopOnPhoneAsync(device, second), "stale_session");
        await Publish(Caps());
        await TrackStopped(first, stream, 1);
        await Publish(Caps(first));
        await CheckIdle("a withdrawn request and its repeated publication cannot reopen the source", 1);
        await Publish(Caps());

        foreach (bool match in new[] { false, true })
        {
            var initiated = listening.StartOnPhoneAsync(device, ct);
            var message = await Read("audio.listen.request");
            string ticket = SrapClient.Text(SrapClient.Body(message).GetProperty("params"), "request")!;
            Check(Guid.TryParseExact(ticket, "N", out _) && ticket == ticket.ToLowerInvariant(), "desktop generates a scoped request ID");
            await Publish(Caps(ticket));
            Check(!listening.HasActiveWork && Volatile.Read(ref captures) == 1,
                "publishing the desktop ticket alone does not open the source before an exact echo");
            await Done(message, new() { ["request"] = match ? ticket : first, ["state"] = "requested" });
            if (!match)
            {
                await Done(await Read("audio.listen.status", "stopped", ticket));
                await Reject(() => initiated, "invalid_argument");
                await CheckIdle("mismatched desktop echo starts no track", 1);
            }
            else
            {
                await initiated;
                stream = await OpenTrack(ticket);
                await listening.StopOnPhoneAsync(device, ticket);
                await TrackStopped(ticket, stream, 2);
            }
            await Publish(Caps());
        }

        string interrupted = Guid.NewGuid().ToString("N");
        await Publish(Caps(interrupted));
        await OpenTrack(interrupted);
        var oldDevice = device;
        transport.Close(profile.Id);
        Check(await finished.Reader.ReadAsync(ct) == 3 && !listening.CanStartOnPhone(oldDevice),
            "disconnect cancels the active source and invalidates the old device handle");

        parent = Guid.NewGuid().ToString("N");
        revision = 1;
        sequence = 0;
        accept = AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint, parent, Caps(interrupted), ct);
        using var newSession = await new SrapClient(identity).ConnectAsync(endpoint, pin, ct);
        using var newPeer = await accept;
        session = newSession;
        remote = newPeer;
        device = transport.Admit(profile, session);
        await Done(await Read("audio.listen.offer", "available"));
        await CheckIdle("reconnection with a stale request in its first snapshot starts no capture", 3);
        await Reject(() => listening.StartOnPhoneAsync(oldDevice, ct), "unsupported");
        await Reject(() => listening.StopOnPhoneAsync(oldDevice, interrupted), "stale_session");
        await Publish(Caps());
        string last = Guid.NewGuid().ToString("N");
        await Publish(Caps(last));
        await OpenTrack(last);
        transport.RevokeTrust(profile.Id, pin);
        Check(await finished.Reader.ReadAsync(ct) == 4 && !listening.CanStartOnPhone(device) && !listening.HasActiveWork
            && new AndroidPhoneStore(path).Profiles.Single(p => p.Id == profile.Id).AppTrustRevoked,
            "revoking this paired fingerprint retires the source and persists loss of trust");
        await Reject(() => listening.StartOnPhoneAsync(device, ct), "unsupported");
        store.Changed -= listening.Reconcile;
        transport.Changed -= listening.Reconcile;
    }
}

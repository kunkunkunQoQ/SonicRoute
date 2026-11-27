using System.Buffers.Binary;
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
    private static X509Certificate2 SrapFixtureCertificate(ECDsa signer)
    {
        var request = new CertificateRequest("CN=SonicRoute Loopback Fixture", signer, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    private static async Task<SslStream> AcceptSrapFixtureAsync(TcpListener listener, X509Certificate2 cert, byte[] expectedPc,
        string? session, JsonArray caps, CancellationToken ct)
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
                    return key != null && SHA256.HashData(key.ExportSubjectPublicKeyInfo()).SequenceEqual(expectedPc);
                }
            }, ct);
            var hello = await SrapClient.ReadAsync(tls, ct);
            await SrapClient.WriteAsync(tls, "hello", new() { ["protocol"] = SrapProtocol.Name, ["major"] = 1,
                ["minorMin"] = 0, ["minorMax"] = 0 }, ct, SrapClient.Text(hello, "id"));
            if (session != null) await SrapClient.WriteAsync(tls, "capabilities", new() { ["capabilityRevision"] = 1,
                ["entries"] = caps }, ct, session: session);
            return tls;
        }
        catch { tls.Dispose(); throw; }
    }

    private static async Task SrapPcmChecksAsync(string directory, string fixture)
    {
        Directory.CreateDirectory(directory);
        foreach (string line in File.ReadLines(fixture).Where(l => !l.StartsWith('#') && l.Contains('|')))
        {
            var f = line.Split('|');
            var wire = Convert.FromHexString(f[3]);
            byte[] samples = f[0] == "0" ? [0, 0, 0, 0, 1, 0, 255, 255] : [0, 128, 255, 127];
            byte[] bytes = new byte[12 + samples.Length];
            SrapPcmOutput.EncodePacket(bytes, uint.Parse(f[0]), ulong.Parse(f[1]), samples);
            Check(samples.Length / 4 == int.Parse(f[2]) && SrapProtocol.Encode(new(SrapFrameType.Data, 1, bytes)).SequenceEqual(wire),
                $"PCM wire vector {f[0]}");
        }
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = SrapFixtureCertificate(signer);
        using var identity = new SrapIdentity(directory);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string pin = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo())), parent = Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = stop.Token;
        var accept = AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint, parent, [
            new JsonObject { ["id"] = "audio.play", ["state"] = "available", ["scope"] = "owned_track",
                ["limits"] = new JsonObject { ["pcm"] = SrapSession.PcmEncoding } },
            new JsonObject { ["id"] = "audio.route.owned", ["state"] = "available" }], ct);
        using var session = await new SrapClient(identity).ConnectAsync((IPEndPoint)listener.LocalEndpoint, pin, ct);
        using var remote = await accept;
        long seq = 0;
        async Task Event(JsonObject body)
        {
            var frame = new JsonObject { ["type"] = "event", ["session"] = parent, ["seq"] = ++seq, ["body"] = body };
            await remote.WriteAsync(SrapProtocol.Encode(new(SrapFrameType.Control, 0, JsonSerializer.SerializeToUtf8Bytes(frame))), ct);
        }
        var device = new AndroidDevice(pin, "phone", "device", true) { IsApp = true };
        var opening = SrapPcmOutput.OpenAsync(session, device, "speaker", ct);
        var begin = await SrapClient.ReadAsync(remote, ct);
        var param = SrapClient.Body(begin).GetProperty("params");
        uint id = param.GetProperty("streamId").GetUInt32();
        Check(id % 2 == 1 && SrapClient.Text(param, "encoding") == SrapSession.PcmEncoding &&
            SrapClient.Text(SrapClient.Body(begin), "op") == "audio.play.start", "explicit PCM request with odd stream ID");
        await SrapClient.WriteAsync(remote, "response", new() { ["status"] = "done", ["result"] = new JsonObject {
            ["encoding"] = SrapSession.PcmEncoding, ["credit"] = 4, ["streamId"] = id } }, ct, SrapClient.Text(begin, "id"), parent);
        var route = await SrapClient.ReadAsync(remote, ct);
        string routeId = SrapClient.Text(route, "id")!;
        await SrapClient.WriteAsync(remote, "response", new() { ["status"] = "pending" }, ct, routeId, parent);
        Check(!opening.IsCompleted, "pending route cannot authorize exclusive playback");
        await Event(new() { ["name"] = "audio.routed", ["requestId"] = routeId, ["streamId"] = id, ["targetKey"] = "speaker",
            ["actualKey"] = "speaker", ["verified"] = true });
        await using var output = await opening;
        foreach (bool mute in new[] { true, false })
        {
            var change = Task.Run(() => output.SetPaused(mute), ct);
            var request = await SrapClient.ReadAsync(remote, ct);
            var body = SrapClient.Body(request);
            var p = body.GetProperty("params");
            Check(SrapClient.Text(body, "op") == "volume.set" && SrapClient.Text(p, "scope") == "owned_track"
                && p.GetProperty("streamId").GetUInt32() == id && p.GetProperty("trackMuted").GetBoolean() == mute
                && !p.TryGetProperty("trackGainPercent", out _), "muting or unmuting PCM preserves the current track gain");
            await SrapClient.WriteAsync(remote, "response", new() { ["status"] = "done", ["result"] = new JsonObject
                { ["streamId"] = id, ["trackGainPercent"] = 37, ["trackMuted"] = mute } }, ct, SrapClient.Text(request, "id"), parent);
            await change;
        }
        var samplesToSend = new byte[5 * 960 * 4];
        RandomNumberGenerator.Fill(samplesToSend);
        var write = output.Stream.WriteAsync(samplesToSend, ct).AsTask();
        async Task ReadPacket(int packet)
        {
            var frame = await SrapProtocol.ReadAsync(remote, ct) ?? throw new EndOfStreamException();
            Check(frame.Type == SrapFrameType.Data && frame.StreamId == id && frame.Payload.Length == 3852
                && BinaryPrimitives.ReadUInt32BigEndian(frame.Payload) == packet
                && BinaryPrimitives.ReadUInt64BigEndian(frame.Payload.AsSpan(4)) == (ulong)packet * 960
                && frame.Payload.AsSpan(12).SequenceEqual(samplesToSend.AsSpan((packet % 5) * 3840, 3840)), $"PCM packet {packet} preserves samples and frame position");
        }
        for (int i = 0; i < 4; ++i) await ReadPacket(i);
        Check(!write.IsCompleted, "four-frame credit window blocks the fifth packet");
        await Event(new() { ["name"] = "audio.credit", ["streamId"] = id, ["nextSeq"] = 3 });
        await ReadPacket(4);
        await write;
        var blocked = output.Stream.WriteAsync(samplesToSend, ct).AsTask();
        await ReadPacket(5);
        await ReadPacket(6);
        Check(!blocked.IsCompleted, "merged credit unlocks only the acknowledged number of packets");
        await Event(new() { ["name"] = "stream.canceled", ["streamId"] = id });
        var ended = await output.Completion.WaitAsync(ct);
        try { await blocked; } catch (Exception ex) when (ex is OperationCanceledException or SrapException) { }
        Check(ended is SrapException { Code: "canceled" } && !blocked.IsCompletedSuccessfully, "peer cancellation wakes the blocked sender");
        var closing = output.DisposeAsync().AsTask();
        var end = await SrapClient.ReadAsync(remote, ct);
        Check(SrapClient.Text(SrapClient.Body(end), "op") == "stream.stop" && !closing.IsCompleted, "normal cleanup waits for stream.stop readback");
        await SrapClient.WriteAsync(remote, "response", new() { ["status"] = "done", ["result"] = new JsonObject {
            ["streamId"] = id, ["stopped"] = true } }, ct, SrapClient.Text(end, "id"), parent);
        await closing;
        Check(((SrapPcmOutput)output).StopConfirmed && !session.Token.IsCancellationRequested, "confirmed stop preserves its main session");
        Console.WriteLine($"PASS {_checks} SRAP PCM checks");
    }

    private static async Task SrapLivePcmAsync(string directory, string requestedTarget)
    {
        Directory.CreateDirectory(directory);
        using var pcIdentity = new SrapIdentity(directory);
        Console.WriteLine("PUBLIC_PIN " + Convert.ToHexString(pcIdentity.Fingerprint));
        var store = new AndroidPhoneStore(Path.Combine(directory, "android-phone.json"));
        await using var transport = new SrapPhoneTransport(store, directory);
        using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = overall.Token;
        AndroidDevice device;
        if (store.Profiles.SingleOrDefault(p => p.LastAppAddress != null && !p.AppTrustRevoked) is { } saved)
        {
            Console.WriteLine("STAGE trusted_reconnect");
            device = await transport.ReconnectAsync(saved, ct);
        }
        else
        {
            Console.WriteLine("INVITE");
            var invite = await Console.In.ReadLineAsync(ct) ?? throw new EndOfStreamException();
            Console.WriteLine("STAGE pair_and_establish_session");
            device = await transport.PairAsync(invite, null, () => Console.WriteLine("PAIR_PENDING"), ct, "SonicRoute Interop");
        }
        var session = transport.Session(device) ?? throw new SrapException("stale_session");
        session.RequestSent += LogSrapRequest;
        Check(session.SupportsPcm, "phone advertises owned s16le-48000-stereo-v1 PCM and verified routing");
        var devices = await session.RequestAsync("audio.devices.list", "audio.devices", new(), ct);
        var outputs = AndroidOutputSink.ParseList(devices.GetProperty("outputs"));
        string target = requestedTarget switch
        {
            "bluetooth" => outputs.Single(o => o.Kind == AndroidOutputSinkKind.Bluetooth).Key,
            "current" or "speaker" => requestedTarget,
            _ => throw new ArgumentException("Choose current, speaker, or bluetooth.")
        };
        string? routed = null;
        var output = (SrapPcmOutput)await SrapPcmOutput.OpenAsync(session, device, target, ct, (key, _, _) => routed = key);
        try
        {
            var actual = outputs.FirstOrDefault(o => o.Key == routed);
            Check(actual != null && (target == "current" || routed == target), "Android confirmed the selected physical output before transmitting PC PCM");
            Console.WriteLine($"AUDIO_OUTPUT kind={actual!.Kind} type={actual.AndroidDeviceType}");
            // A quiet 800 ms stereo tone, with a short fade at each end.
            const int frames = 38400;
            var data = new byte[frames * 4];
            for (int i = 0; i < frames; ++i)
            {
                double fade = Math.Min(1d, Math.Min(i, frames - 1 - i) / 480d);
                short value = (short)(1600 * fade * Math.Sin(2 * Math.PI * 440 * i / 48000d));
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(4 * i), value);
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(4 * i + 2), value);
            }
            Console.WriteLine("STAGE send_40_pcm_packets");
            await output.WriteAsync(data, ct);
            Check(await output.WaitForConsumptionAsync(ct) == 40, "Android acknowledged 40 packets / 38400 stereo frames written to AudioTrack");
        }
        finally { await output.DisposeAsync(); }
        Check(output.StopConfirmed && !session.Token.IsCancellationRequested, "Android confirmed the track stopped while the main session stayed connected");
    }
}

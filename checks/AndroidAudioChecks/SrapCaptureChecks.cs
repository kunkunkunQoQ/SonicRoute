using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapCaptureChecksAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        const string package = "example.capture";
        var app = new AndroidApp(package, "Shared app", 0, "");
        JsonArray Capabilities(string state = "available", string selected = package, string grant = "1") =>
        [
            new JsonObject { ["id"] = "audio.capture.app", ["state"] = state, ["scope"] = "other_app:" + selected,
                ["reason"] = state == "available" ? "authorized" : "media_projection_required",
                ["limits"] = new JsonObject { ["mode"] = "playback_capture", ["pcm"] = SrapSession.PcmEncoding, ["grant"] = grant } },
            new JsonObject { ["id"] = "volume.device", ["state"] = "available" }
        ];
        using var identity = new SrapIdentity(directory);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = SrapFixtureCertificate(signer);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var ct = deadline.Token;
        string parent = Guid.NewGuid().ToString("N"), pin = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
        var accept = AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint, parent, Capabilities(), ct);
        using var session = await new SrapClient(identity).ConnectAsync((IPEndPoint)listener.LocalEndpoint, pin, ct);
        using var remote = await accept;
        var device = new AndroidDevice(pin, "Capture peer", "device", true) { IsApp = true };
        var capture = new AndroidAudioSession(() => throw new Exception("An App capture invoked ADB"), (_, _) => new FakePlayer())
            { AppSession = _ => session };
        capture.SetSmartVolume(false);
        await using var owned = capture;
        async Task<JsonElement> Request(string op)
        {
            while (true)
            {
                var message = await SrapClient.ReadAsync(remote, ct);
                var body = SrapClient.Body(message);
                if (SrapClient.Text(message, "type") == "cancel" && body.TryGetProperty("requestId", out _)) continue;
                if (SrapClient.Text(message, "type") != "request" || SrapClient.Text(body, "op") != op)
                    throw new InvalidDataException("Expected " + op);
                return message;
            }
        }
        Task Reply(JsonElement message, JsonObject result) => SrapClient.WriteAsync(remote, "response",
            new() { ["status"] = "done", ["result"] = result }, ct, SrapClient.Text(message, "id"), parent);
        async Task<JsonElement> Open(uint id)
        {
            var begin = await Request("audio.capture.start");
            var p = SrapClient.Body(begin).GetProperty("params");
            if (SrapClient.Text(p, "appKey") != package || SrapClient.Text(p, "encoding") != SrapSession.PcmEncoding
                || !p.GetProperty("keepPhoneAudio").GetBoolean() || p.TryGetProperty("streamId", out _))
                throw new InvalidDataException("Wrong package or capture mode.");
            await Reply(begin, new() { ["streamId"] = id, ["encoding"] = SrapSession.PcmEncoding, ["credit"] = 4 });
            var credit = await Request("audio.capture.credit");
            var value = SrapClient.Body(credit).GetProperty("params");
            if (value.GetProperty("streamId").GetUInt32() != id || value.GetProperty("consumedPackets").GetUInt64() != 0)
                throw new InvalidDataException("Invalid initial capture credit.");
            return credit;
        }
        Task Ack(JsonElement request, uint id, ulong total) => Reply(request, new() { ["streamId"] = id, ["consumedPackets"] = total });
        async Task AckConsumed(uint id, uint total)
        {
            var credit = await Request("audio.capture.credit");
            var p = SrapClient.Body(credit).GetProperty("params");
            Check(p.GetProperty("streamId").GetUInt32() == id && p.GetProperty("consumedPackets").GetUInt32() == total,
                $"capture credits merge after {total} packets are written to PCM");
            await Ack(credit, id, total);
        }
        async Task ConfirmStop(uint id)
        {
            var end = await Request("stream.stop");
            if (SrapClient.Body(end).GetProperty("params").GetProperty("streamId").GetUInt32() != id)
                throw new InvalidDataException("Stopped the wrong stream.");
            await Reply(end, new() { ["streamId"] = id, ["stopped"] = true });
        }
        async Task Data(uint id, uint seq, ulong position, byte[] samples)
        {
            var bytes = new byte[12 + samples.Length];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, seq);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(4), position);
            samples.CopyTo(bytes, 12);
            await remote.WriteAsync(SrapProtocol.Encode(new(SrapFrameType.Data, id, bytes)), ct);
        }
        static async Task Denied(Func<Task> action, string code, string label)
        {
            try { await action(); } catch (SrapException e) when (e.Code == code) { Check(true, label); return; }
            throw new Exception("FAIL " + label);
        }

        await Denied(() => capture.StartAsync(device, app, false, ct), "unsupported", "exclusive capture rejected before touching ADB or the wire");
        await Denied(() => capture.StartAsync(device, AndroidApp.DeviceAudio, true, ct), "unsupported", "whole-device capture rejected");
        await Denied(() => capture.StartAsync(device, app with { Package = "example.other" }, true, ct), "permission_required",
            "capture grant is restricted to the exact package");
        var existingData = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (session.ExpectData(42, data => existingData.TrySetResult(data.Length)))
        {
            var collision = new SrapPcmCapture(session, app);
            var rejected = collision.ReceiveAsync(new AndroidPcmSource(), () => { }, ct);
            var begin = await Request("audio.capture.start");
            await Reply(begin, new() { ["streamId"] = 42, ["encoding"] = SrapSession.PcmEncoding, ["credit"] = 4 });
            await Denied(() => rejected, "busy", "an existing stream ID cannot be claimed by a new capture");
            await remote.WriteAsync(SrapProtocol.Encode(new(SrapFrameType.Data, 42, [1])), ct);
            Check(await existingData.Task.WaitAsync(ct) == 1 && !session.Token.IsCancellationRequested && !collision.StopConfirmed,
                "the existing owner and the TLS session survive the rejected capture");
        }
        var start = capture.StartAsync(device, app, true, ct);
        var initial = await Open(2);
        await Data(2, 0, 0, [0, 0, 0, 0]);
        await Data(2, 1, 1, [0, 192, 0, 96, 0, 0, 0, 0]);
        var get = session.ReadVolumeAsync(ct);
        var volume = await Request("volume.get");
        await Reply(volume, new() { ["percent"] = 19 });
        Check((await get).GetProperty("percent").GetInt32() == 19 && !start.IsCompleted,
            "TLS reader dispatches other operations while DATA precedes initial credit response");
        await Ack(initial, 2, 0);
        await AckConsumed(2, 2);
        await start;
        Check(capture.State.State == CaptureState.Receiving && capture.UsesSourcePhonePlayback && capture.PacketsDecoded == 2,
            "the ordinary Android audio session opens non-exclusive App PCM");
        var pcm = capture.Pcm!;
        byte[] rendered = new byte[6 * sizeof(float)];
        pcm.Read(rendered, 0, rendered.Length);
        Check(pcm.SamplesWritten == 6 && pcm.NonSilentSamplesWritten == 2
            && MemoryMarshal.Cast<byte, float>(rendered).SequenceEqual([0f, 0f, -0.5f, 0.75f, 0f, 0f]),
            "the existing PCM source receives signed stereo and preserves silent samples");
        await Data(2, 2, 3, [0, 0, 0, 0]); await Data(2, 3, 4, [0, 0, 0, 0]);
        await AckConsumed(2, 4);
        await capture.SetOutputAsync("windows-output-fixture");
        Check(capture.PacketsDecoded == 4 && ReferenceEquals(pcm, capture.Pcm) && capture.UsesSourcePhonePlayback,
            "changing the Windows endpoint retains the same authorized capture");
        await Denied(() => ((IMultiOutputAudioSession)capture).SetOutputsAsync([null]), "unsupported",
            "an explicit output selection cannot suppress the phone");
        var stop = capture.StopAsync();
        await ConfirmStop(2);
        await stop;
        Check(capture.State.State == CaptureState.Idle && !session.Token.IsCancellationRequested, "confirmed stop leaves the paired session connected");

        uint id = 2;
        long revision = 1;
        foreach (string scenario in new[] { "window", "sequence", "position", "alignment", "oversize", "ack", "idle timeout", "remote cancel", "scope change", "grant replaced", "grant withdrawn" })
        {
            id += 2;
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var receiver = new SrapPcmCapture(session, app);
            var sink = new AndroidPcmSource();
            var receiving = receiver.ReceiveAsync(sink, () => { }, pending.Token);
            var first = await Open(id);
            if (scenario == "window")
            {
                for (uint p = 0; p < 5; ++p) await Data(id, p, p, [0, 0, 0, 0]);
            }
            else if (scenario == "ack") await Ack(first, id, 1);
            else
            {
                await Ack(first, id, 0);
                switch (scenario)
                {
                    case "sequence": await Data(id, 1, 0, [0, 0, 0, 0]); break;
                    case "position": await Data(id, 0, 1, [0, 0, 0, 0]); break;
                    case "alignment": await Data(id, 0, 0, [1, 2, 3]); break;
                    case "oversize": await Data(id, 0, 0, new byte[961 * 4]); break;
                    case "idle timeout": break;
                    case "remote cancel":
                        var ev = new JsonObject { ["type"] = "event", ["seq"] = 1, ["session"] = parent,
                            ["body"] = new JsonObject { ["name"] = "stream.canceled", ["streamId"] = id } };
                        await remote.WriteAsync(SrapProtocol.Encode(new(SrapFrameType.Control, 0, JsonSerializer.SerializeToUtf8Bytes(ev))), ct);
                        break;
                    default:
                        await SrapClient.WriteAsync(remote, "capabilities", new() { ["capabilityRevision"] = ++revision,
                            ["entries"] = scenario switch { "scope change" => Capabilities(selected: "example.other"),
                                "grant replaced" => Capabilities(grant: "2"), _ => Capabilities("needs_authorization") } },
                            ct, session: parent);
                        break;
                }
            }
            await ConfirmStop(id);
            string expected = scenario switch { "scope change" or "grant withdrawn" or "grant replaced" => "permission_lost",
                "remote cancel" => "canceled", "idle timeout" => "timeout", _ => "invalid_argument" };
            await Denied(() => receiving, expected, "capture ends on " + scenario);
            Check(receiver.StopConfirmed && sink.SamplesWritten == 0 && !session.Token.IsCancellationRequested,
                scenario + " releases only the affected stream");
            if (scenario is "scope change" or "grant replaced")
            {
                var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Wake() { if (session.Capabilities.Capture is { Package: package, Grant: "1" }) refreshed.TrySetResult(); }
                session.CapabilitiesChanged += Wake;
                try
                {
                    await SrapClient.WriteAsync(remote, "capabilities", new() { ["capabilityRevision"] = ++revision, ["entries"] = Capabilities() }, ct, session: parent);
                    await refreshed.Task.WaitAsync(ct);
                }
                finally { session.CapabilitiesChanged -= Wake; }
            }
        }
        Check(session.Capabilities.Capture is { Package: null, Reason: "media_projection_required" }, "the phone's authorization requirement remains visible");
        Console.WriteLine($"PASS {_checks} SRAP capture checks");
    }
}

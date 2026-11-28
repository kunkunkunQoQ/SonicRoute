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
    private readonly record struct CaptureFixturePeer(Stream Wire, string SessionId);

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
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        string parent = Guid.NewGuid().ToString("N"), pin = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
        var accept = AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint, parent, Capabilities(), ct);
        using var session = await new SrapClient(identity).ConnectAsync(endpoint, pin, ct);
        using var remote = await accept;
        var device = new AndroidDevice(pin, "Capture peer", "device", true) { IsApp = true };
        using var secondSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondCert = SrapFixtureCertificate(secondSigner);
        string secondParent = Guid.NewGuid().ToString("N");
        string secondPin = Convert.ToHexString(SHA256.HashData(secondSigner.ExportSubjectPublicKeyInfo()));
        var acceptSecond = AcceptSrapFixtureAsync(listener, secondCert, identity.Fingerprint, secondParent, Capabilities(), ct);
        using var secondSession = await new SrapClient(identity).ConnectAsync(endpoint, secondPin, ct);
        using var secondRemote = await acceptSecond;
        var secondPeer = new CaptureFixturePeer(secondRemote, secondParent);
        var secondDevice = new AndroidDevice(secondPin, "Second capture peer", "device", true) { IsApp = true };
        var sessions = new Dictionary<string, SrapSession> { [pin] = session, [secondPin] = secondSession };
        SrapSession SessionFor(AndroidDevice phone) => sessions[phone.Serial];
        AndroidAudioSession CreateCapture() => new(() => throw new Exception("An App capture invoked ADB"), (_, _) => new FakePlayer())
            { AppSession = SessionFor };
        await using var directCapture = CreateCapture();
        var store = new AndroidPhoneStore(Path.Combine(directory, parent + ".json"));
        var profile = store.RegisterApp(pin, endpoint, store.ManagementRevision);
        await using var sources = new AndroidAudioManager(CreateCapture, null, store.IsSmartVolumeEnabled,
            mayCapture: null, lifetime: ct, appCapture: phone => SessionFor(phone).Capabilities.Capture);
        store.Changed += () => sources.SyncSmartVolume(store.IsSmartVolumeEnabled);
        async Task<JsonElement> Request(string op, CaptureFixturePeer? peer = null)
        {
            while (true)
            {
                var message = await SrapClient.ReadAsync(peer?.Wire ?? remote, ct);
                var body = SrapClient.Body(message);
                if (SrapClient.Text(message, "type") == "cancel" && body.TryGetProperty("requestId", out _)) continue;
                if (SrapClient.Text(message, "type") != "request" || SrapClient.Text(body, "op") != op)
                    throw new InvalidDataException("Expected " + op);
                return message;
            }
        }
        Task Reply(JsonElement message, JsonObject result, CaptureFixturePeer? peer = null) => SrapClient.WriteAsync(peer?.Wire ?? remote, "response",
            new() { ["status"] = "done", ["result"] = result }, ct, SrapClient.Text(message, "id"), peer?.SessionId ?? parent);
        async Task<JsonElement> Open(uint id, bool deviceMedia = false, CaptureFixturePeer? peer = null)
        {
            var begin = await Request("audio.capture.start", peer);
            var p = SrapClient.Body(begin).GetProperty("params");
            bool scoped = deviceMedia ? SrapClient.Text(p, "scope") == "device_media" && !p.TryGetProperty("appKey", out _)
                : SrapClient.Text(p, "appKey") == package && !p.TryGetProperty("scope", out _);
            if (!scoped || SrapClient.Text(p, "encoding") != SrapSession.PcmEncoding || !p.GetProperty("keepPhoneAudio").GetBoolean()
                || p.EnumerateObject().Count() != 3)
                throw new InvalidDataException("Wrong package or capture mode.");
            await Reply(begin, new() { ["streamId"] = id, ["encoding"] = SrapSession.PcmEncoding, ["credit"] = 4 }, peer);
            var credit = await Request("audio.capture.credit", peer);
            var value = SrapClient.Body(credit).GetProperty("params");
            if (value.GetProperty("streamId").GetUInt32() != id || value.GetProperty("consumedPackets").GetUInt64() != 0)
                throw new InvalidDataException("Invalid initial capture credit.");
            return credit;
        }
        Task Ack(JsonElement request, uint id, ulong total, CaptureFixturePeer? peer = null) =>
            Reply(request, new() { ["streamId"] = id, ["consumedPackets"] = total }, peer);
        async Task AckConsumed(uint id, uint total, CaptureFixturePeer? peer = null)
        {
            var credit = await Request("audio.capture.credit", peer);
            var p = SrapClient.Body(credit).GetProperty("params");
            Check(p.GetProperty("streamId").GetUInt32() == id && p.GetProperty("consumedPackets").GetUInt32() == total,
                $"capture credits merge after {total} packets are written to PCM");
            await Ack(credit, id, total, peer);
        }
        async Task ConfirmStop(uint id, CaptureFixturePeer? peer = null)
        {
            var end = await Request("stream.stop", peer);
            if (SrapClient.Body(end).GetProperty("params").GetProperty("streamId").GetUInt32() != id)
                throw new InvalidDataException("Stopped the wrong stream.");
            await Reply(end, new() { ["streamId"] = id, ["stopped"] = true }, peer);
        }
        async Task Data(uint id, uint seq, ulong position, byte[] samples, CaptureFixturePeer? peer = null)
        {
            var bytes = new byte[12 + samples.Length];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, seq);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(4), position);
            samples.CopyTo(bytes, 12);
            await (peer?.Wire ?? remote).WriteAsync(SrapProtocol.Encode(new(SrapFrameType.Data, id, bytes)), ct);
        }
        static async Task Denied(Func<Task> action, string code, string label)
        {
            try { await action(); } catch (SrapException e) when (e.Code == code) { Check(true, label); return; }
            throw new Exception("FAIL " + label);
        }

        await Denied(() => directCapture.StartAsync(device, app, false, ct), "unsupported", "exclusive capture rejected before touching ADB or the wire");
        await Denied(() => directCapture.StartAsync(device, AndroidApp.DeviceAudio, true, ct), "unsupported", "whole-device capture rejected");
        await Denied(() => directCapture.StartAsync(device, app with { Package = "example.other" }, true, ct), "permission_required",
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
        Check(!store.AppSmartVolumeEnabled && !store.IsSmartVolumeEnabled(device, app.Package), "shared App Smart Volume starts disabled");
        store.SetAppSmartVolume(true);
        var start = sources.AddAsync(device, app);
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
        var firstSource = await start;
        var capture = firstSource.Session;
        Check(capture.State.SmartVolume && capture.Pcm?.SmartVolume == true, "App capture starts with the shared Smart Volume setting");
        store.SetAppSmartVolume(false);
        Check(capture.State.State == CaptureState.Receiving && capture.UsesSourcePhonePlayback && capture.PacketsDecoded == 2,
            "the ordinary Android audio session opens non-exclusive App PCM");
        var pcm = capture.Pcm!;
        byte[] rendered = new byte[6 * sizeof(float)];
        pcm.Read(rendered, 0, rendered.Length);
        Check(pcm.SamplesWritten == 6 && pcm.NonSilentSamplesWritten == 2
            && MemoryMarshal.Cast<byte, float>(rendered).SequenceEqual([0f, 0f, -0.5f, 0.75f, 0f, 0f]),
            "the existing PCM source receives signed stereo and preserves silent samples");
        byte[] stereo = [0, 96, 0, 160]; // +0.75, -0.75 as PCM16 LE
        await Data(2, 2, 3, stereo);
        await Data(2, 3, 4, stereo);
        await AckConsumed(2, 4);
        capture.SetVolume(25);
        store.SetAppSmartVolume(true);

        var secondProfile = store.RegisterApp(secondPin, endpoint, store.ManagementRevision);
        var startSecond = sources.AddAsync(secondDevice, app);
        var secondCredit = await Open(2, peer: secondPeer);
        await Ack(secondCredit, 2, 0, secondPeer);
        await Data(2, 0, 0, stereo, secondPeer);
        await Data(2, 1, 1, stereo, secondPeer);
        await AckConsumed(2, 2, secondPeer);
        var secondSource = await startSecond;
        var secondCapture = secondSource.Session;
        var secondPcm = secondCapture.Pcm!;
        secondCapture.SetVolume(25);
        Check(profile.Id != secondProfile.Id && firstSource.Id != secondSource.Id && sources.Sources.Count == 2
            && capture.State.SmartVolume && secondCapture.State.SmartVolume,
            "a newly registered App identity starts with the same setting as the active App source");

        static (float Left, float Right) ReadFrame(AndroidPcmSource source)
        {
            byte[] frame = new byte[2 * sizeof(float)];
            source.Read(frame, 0, frame.Length);
            return (BitConverter.ToSingle(frame), BitConverter.ToSingle(frame, sizeof(float)));
        }
        static bool IsAttenuated((float Left, float Right) frame) =>
            frame.Left is > 0f and < 0.1875f && frame.Right == -frame.Left;

        Check(IsAttenuated(ReadFrame(pcm)) && IsAttenuated(ReadFrame(secondPcm)),
            "enabled Smart Volume attenuates stereo samples on both App outputs");
        store.SetAppSmartVolume(false);
        Check(ReadFrame(pcm) == (0.1875f, -0.1875f) && ReadFrame(secondPcm) == (0.1875f, -0.1875f)
            && ReferenceEquals(capture.Pcm, pcm) && ReferenceEquals(secondCapture.Pcm, secondPcm)
            && capture.State.State == CaptureState.Receiving && secondCapture.State.State == CaptureState.Receiving
            && capture.UsesSourcePhonePlayback && secondCapture.UsesSourcePhonePlayback,
            "disabling Smart Volume restores linear gain on both live captures without restarting them");
        store.SetAppSmartVolume(true);
        Check(pcm.SmartVolume && secondPcm.SmartVolume, "enabling Smart Volume again updates both existing App sources");
        store.SetAppSmartVolume(false);
        var stopSecond = sources.RemoveAsync(secondSource.Id);
        await ConfirmStop(2, secondPeer);
        await stopSecond;
        await capture.SetOutputAsync("windows-output-fixture");
        Check(capture.PacketsDecoded == 4 && ReferenceEquals(pcm, capture.Pcm) && capture.UsesSourcePhonePlayback,
            "changing the Windows endpoint retains the same authorized capture");
        await Denied(() => ((IMultiOutputAudioSession)capture).SetOutputsAsync([null]), "unsupported",
            "an explicit output selection cannot suppress the phone");
        var multi = (IMultiOutputAudioSession)capture;
        await multi.SetOutputsAsync(AppCapturePermission.WithLocalPlayback(device, [null]));
        Check(capture.State.OutputId == null && multi.GetOutputs().Contains(null) && capture.UsesSourcePhonePlayback && ReferenceEquals(pcm, capture.Pcm),
            "switching to the Windows system default reads back the implicit phone output and the same PCM capture");
        await multi.SetOutputsAsync(AppCapturePermission.WithLocalPlayback(device, ["second-windows-renderer"]));
        Check(capture.State.OutputId == "second-windows-renderer" && multi.GetOutputs().Contains("second-windows-renderer")
            && capture.UsesSourcePhonePlayback && ReferenceEquals(pcm, capture.Pcm), "switching Windows renderers keeps the same grant and reports the selected sink");
        var stop = sources.RemoveAsync(firstSource.Id);
        await ConfirmStop(2);
        await stop;
        Check(capture.State.State == CaptureState.Idle && !session.Token.IsCancellationRequested, "confirmed stop leaves the paired session connected");
        await store.FlushAsync();
        var reloaded = new AndroidPhoneStore(store.FilePath);
        Check(!reloaded.AppSmartVolumeEnabled && reloaded.Profiles.Count == 2
            && !reloaded.IsSmartVolumeEnabled(device, app.Package) && !reloaded.IsSmartVolumeEnabled(secondDevice, app.Package),
            "both App identities read the shared disabled setting after reopening the store");

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
        var deviceGrant = new JsonObject { ["id"] = "audio.capture.device", ["state"] = "available", ["scope"] = "device_media",
            ["limits"] = new JsonObject { ["mode"] = "playback_capture", ["pcm"] = SrapSession.PcmEncoding, ["grant"] = "9", ["autoplay"] = "pc_output_v1" } };
        async Task Publish(JsonArray entries)
        {
            long next = ++revision;
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed() { if (session.Capabilities.Revision == next) seen.TrySetResult(); }
            session.CapabilitiesChanged += Changed;
            try
            {
                await SrapClient.WriteAsync(remote, "capabilities", new() { ["capabilityRevision"] = next, ["entries"] = entries }, ct, session: parent);
                await seen.Task.WaitAsync(ct);
            }
            finally { session.CapabilitiesChanged -= Changed; }
        }
        var both = Capabilities();
        both.Add(deviceGrant.DeepClone());
        await Publish(both);
        Check(session.Capabilities.Capture == null, "two available capture scopes fail closed");
        await Denied(() => directCapture.StartAsync(device, AndroidApp.DeviceAudio, true, ct), "unsupported", "ambiguous whole-device grant rejected");
        var wrongScope = deviceGrant.DeepClone();
        wrongScope["scope"] = "other_app:" + package;
        await Publish([wrongScope]);
        Check(session.Capabilities.Capture == null, "the device capability cannot grant a package or an unknown scope");
        await Publish([deviceGrant]);
        Check(session.Capabilities.Capture is { IsDeviceMedia: true, Package: null, Grant: "9", PlayOnComputer: true }, "explicit device grant identifies no package");
        await Denied(() => directCapture.StartAsync(device, app, true, ct), "permission_required", "a whole-device grant cannot impersonate a chosen package");
        await Denied(() => directCapture.StartAsync(device, AndroidApp.DeviceAudio, false, ct), "unsupported", "device capture always keeps phone playback");
        var whole = directCapture.StartAsync(device, AndroidApp.DeviceAudio, true, ct);
        var firstCredit = await Open(56, deviceMedia: true);
        await Ack(firstCredit, 56, 0);
        await Data(56, 0, 0, [0, 32, 0, 224]);
        await whole;
        Check(directCapture.Pcm!.NonSilentSamplesWritten == 2 && directCapture.UsesSourcePhonePlayback, "device-scoped PCM reaches the Windows player and retains local playback");
        var endWhole = directCapture.StopAsync();
        await ConfirmStop(56);
        await endWhole;
        Check(directCapture.State.State == CaptureState.Idle && !session.Token.IsCancellationRequested, "device capture stops its own stream without closing TLS");
        await Publish([new JsonObject { ["id"] = "catalog.apps", ["state"] = "available", ["scope"] = "launcher_apps" }]);
        await using var transport = new SrapPhoneTransport(store, directory);
        var peer = transport.Admit(profile, session);
        await using var adb = new AdbOperationAdapter(new AdbClient(new AndroidRuntimeBundle(Path.Combine(directory, "unused-adb"))));
        var operations = new SrapOperationAdapter(adb, transport, active => ReferenceEquals(active, peer));
        JsonObject Catalog(bool icons)
        {
            var catalog = new JsonObject { ["apps"] = new JsonArray(
                new JsonObject { ["appKey"] = package, ["label"] = "Fixture player" },
                new JsonObject { ["appKey"] = "example.another", ["label"] = "Another app" }) };
            if (icons) catalog["iconFormat"] = "png48-v1";
            return catalog;
        }
        var full = operations.AppsAsync(peer, ct);
        await Reply(await Request("catalog.list"), Catalog(true));
        Check((await full).Count == 2 && (await full).All(item => item.IconPng == null), "loading the complete authenticated catalog returns names without per-package image requests");
        const string fixturePng = "iVBORw0KGgoAAAANSUhEUgAAADAAAAAwCAYAAABXAvmHAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAABTSURBVGhD7c8xDQAgAMAwFGEHUYiGHwMLSY8--zbmXudn4w2_MVAzUDNQM1AzUDNQM1AzUDNQM1AzUDNQM1AzUDNQM1AzUDNQM1AzUDNQM1AzULtVrIgPJJToPQAAAABJRU5ErkJggg";
        var one = operations.AppsAsync(peer, ct, [package]);
        var iconRequest = await Request("catalog.icon");
        var wanted = SrapClient.Body(iconRequest).GetProperty("params");
        Check(wanted.EnumerateObject().Count() == 1 && SrapClient.Text(wanted, "appKey") == package,
            "single-package lookup requests exactly the authorized visible package icon");
        await Reply(iconRequest, new() { ["appKey"] = package, ["format"] = "png48-v1", ["png"] = fixturePng });
        var row = (await one).Single();
        Check(row.Name == "Fixture player" && row.IconPng is { Length: > 0 } png &&
            WicIconLoader.TryDecodePngToBgra32(png, 48, 48, out var pixels, out int width, out int height) &&
            width == 48 && height == 48 && pixels.Length == 48 * 48 * 4 && pixels[3] == 255,
            "the authenticated source name and bounded 48x48 PNG decode to real renderer pixels");
        var legacy = operations.AppsAsync(peer, ct);
        await Reply(await Request("catalog.list"), Catalog(false));
        await legacy;
        Check((await operations.AppsAsync(peer, ct, [package])).Single() is { Name: "Fixture player", IconPng: null },
            "an older catalog returns the real label without an unsupported icon request");
        await store.FlushAsync();
        Console.WriteLine($"PASS {_checks} SRAP capture checks");
    }
}

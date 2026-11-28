using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapUiFixtureAsync(string dataDirectory, bool sharing = false)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(dataDirectory));
        string profilePath = Path.Combine(directory.FullName, "android-phone.json");
        if (directory.Name != "data" || directory.Parent?.Name != "Full" ||
            directory.Parent.Parent?.Parent?.Name != "SonicRoute.NativeCandidate" || File.Exists(profilePath))
            throw new ArgumentException("The UI fixture requires a fresh isolated Native Full profile.");
        directory.Create();
        await using var adb = new FakeAdbServer();
        if (adb.EndPoint.Port < 49152) throw new IOException("Expected a dynamic loopback port.");
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = SrapFixtureCertificate(signer);
        using var identity = new SrapIdentity(directory.FullName);
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var store = new AndroidPhoneStore(profilePath);
        var profile = store.RegisterApp(Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo())),
            (IPEndPoint)server.LocalEndpoint, store.ManagementRevision);
        store.SelectProfile(profile.Id);
        store.Rename("演示手机");
        store.Retain(new AndroidApp("org.sonicroute.demo.player", "音乐播放器", 0, ""));
        await store.FlushAsync();
        Console.WriteLine($"UI_FIXTURE_READY adbPort={adb.EndPoint.Port}");
        Console.Out.Flush();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var token = lifetime.Token;
        var run = ServeAsync();
        var stop = Task.Run(Console.ReadLine);
        await Task.WhenAny(stop, run);
        lifetime.Cancel();
        try { await run; } catch (OperationCanceledException) when (token.IsCancellationRequested) { }

        async Task ServeAsync()
        {
            while (!token.IsCancellationRequested)
            {
                string session = Guid.NewGuid().ToString("N");
                long revision = 1;
                int screenRequests = 0, computerRequests = 0;
                string? phoneRequest = null;
                bool screen = false, sound = false;
                JsonArray Capabilities()
                {
                    var play = Cap("audio.play", "owned_track");
                    if (sharing) play["limits"]!["pcListenInitiate"] = "1";
                    if (phoneRequest != null) play["limits"]!["pcListenRequest"] = phoneRequest;
                    var display = Cap("display.mirror", "one_projection", !screen);
                    if (screen) display["limits"] = new JsonObject { ["grant"] = "123", ["codec"] = "h264", ["autoview"] = "pc_viewer_v1" };
                    var caps = new JsonArray(Cap("audio.devices", "device"), Cap("volume.device", "system_media"), Cap("catalog.apps", "launchable"),
                        play, Cap("audio.route.owned", "owned_track"), Cap("audio.capture.app", "other_app", true), display);
                    if (sharing) caps.Add(Cap("sharing.prompt", "local_confirmation"));
                    if (sound) caps.Add(new JsonObject { ["id"] = "audio.capture.device", ["state"] = "available", ["scope"] = "device_media",
                        ["limits"] = new JsonObject { ["grant"] = "124", ["pcm"] = SrapSession.PcmEncoding, ["mode"] = "playback_capture", ["autoplay"] = "pc_output_v1" } });
                    return caps;
                }
                using var peer = await AcceptSrapFixtureAsync(server, certificate, identity.Fingerprint, session, Capabilities(), token);
                Console.WriteLine("UI_FIXTURE_AUTHENTICATED");
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var request = await SrapClient.ReadAsync(peer, token);
                        string? op = SrapClient.Text(SrapClient.Body(request), "op");
                        if (op == null) continue;
                        var parameters = SrapClient.Body(request).GetProperty("params");
                        string? kind = SrapClient.Text(parameters, "kind");
                        bool update = false, pending = false;
                        string error = "unsupported";
                        JsonObject? result = op switch
                        {
                            "volume.get" => new() { ["scope"] = "system_media", ["percent"] = 40 },
                            "audio.devices.list" => new() { ["outputs"] = new JsonArray(new JsonObject { ["key"] = "current" },
                                new JsonObject { ["key"] = "speaker", ["kind"] = "speaker", ["type"] = 2 },
                                new JsonObject { ["key"] = "bt:demo", ["kind"] = "bluetooth", ["name"] = "演示耳机", ["type"] = 8 }) },
                            "catalog.list" => new() { ["apps"] = new JsonArray(new JsonObject { ["appKey"] = "org.sonicroute.demo.player", ["label"] = "音乐播放器" },
                                new JsonObject { ["appKey"] = "org.sonicroute.demo.radio", ["label"] = "网络电台" }) },
                            _ => null
                        };
                        if (sharing && op == "audio.listen.offer") result = new();
                        if (sharing && op == "audio.listen.request")
                        {
                            if (computerRequests++ == 0) error = "permission_required";
                            else
                            {
                                phoneRequest = SrapClient.Text(parameters, "request");
                                result = new() { ["request"] = phoneRequest, ["state"] = "requested" };
                                update = true;
                            }
                        }
                        if (sharing && op == "audio.play.start" && phoneRequest != null &&
                            SrapClient.Text(parameters, "listenRequest") == phoneRequest)
                            result = new() { ["streamId"] = parameters.GetProperty("streamId").GetUInt32(),
                                ["encoding"] = SrapSession.PcmEncoding, ["credit"] = SrapPcmOutput.Window };
                        if (sharing && op == "audio.route.set") pending = true;
                        if (sharing && op == "stream.stop") result = new() {
                            ["streamId"] = parameters.GetProperty("streamId").GetUInt32(), ["stopped"] = true };
                        if (sharing && op == "sharing.request" && kind is "screen" or "audio")
                        {
                            if (kind == "screen" && screenRequests++ == 0) error = "busy";
                            else
                            {
                                result = new() { ["kind"] = kind, ["state"] = "awaiting_local_consent" };
                                screen = kind == "screen";
                                sound = !screen;
                                update = true;
                            }
                        }
                        if (sharing && op == "audio.capture.start")
                        {
                            sound = false;
                            phoneRequest = Guid.NewGuid().ToString("N");
                            update = true;
                        }
                        if (sharing && op == "audio.listen.status")
                        {
                            if (SrapClient.Text(parameters, "request") == phoneRequest)
                            {
                                phoneRequest = null;
                                update = true;
                            }
                            result = new();
                        }
                        var reply = pending ? new JsonObject { ["status"] = "pending" }
                            : result == null ? new JsonObject { ["status"] = "error", ["error"] = error }
                            : new JsonObject { ["status"] = "done", ["result"] = result };
                        await SrapClient.WriteAsync(peer, "response", reply, token, SrapClient.Text(request, "id"), session);
                        if (update) await SrapClient.WriteAsync(peer, "capabilities", new() { ["capabilityRevision"] = ++revision,
                            ["entries"] = Capabilities() }, token, session: session);
                        File.AppendAllText(Path.Combine(directory.FullName, "fixture-ops.log"), op + " " + (kind ?? SrapClient.Text(parameters, "state")) +
                            " " + (pending ? "pending" : result == null ? error : "done") + Environment.NewLine);
                        Console.WriteLine("UI_FIXTURE_OPERATION " + op);
                    }
                }
                catch (IOException) when (!token.IsCancellationRequested) { }
            }
        }

        static JsonObject Cap(string id, string scope, bool needsPermission = false) => new()
        {
            ["id"] = id, ["scope"] = scope, ["state"] = needsPermission ? "needs_authorization" : "available",
            ["reason"] = needsPermission ? "projection_required" : "supported",
            ["limits"] = id == "audio.play" ? new JsonObject { ["pcm"] = SrapSession.PcmEncoding } : new JsonObject()
        };
    }
}

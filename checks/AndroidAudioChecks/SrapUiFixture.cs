using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapUiFixtureAsync(string dataDirectory)
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
                using var peer = await AcceptSrapFixtureAsync(server, certificate, identity.Fingerprint, session, [
                    Cap("audio.devices", "device"), Cap("volume.device", "system_media"), Cap("catalog.apps", "launchable"),
                    Cap("audio.play", "owned_track"), Cap("audio.route.owned", "owned_track"),
                    Cap("audio.capture.app", "other_app", true), Cap("display.mirror", "one_projection", true)], token);
                Console.WriteLine("UI_FIXTURE_AUTHENTICATED");
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var request = await SrapClient.ReadAsync(peer, token);
                        string? op = SrapClient.Text(SrapClient.Body(request), "op");
                        if (op == null) continue;
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
                        var reply = result == null ? new JsonObject { ["status"] = "error", ["error"] = new JsonObject { ["code"] = "unsupported" } }
                            : new JsonObject { ["status"] = "done", ["result"] = result };
                        await SrapClient.WriteAsync(peer, "response", reply, token, SrapClient.Text(request, "id"), session);
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

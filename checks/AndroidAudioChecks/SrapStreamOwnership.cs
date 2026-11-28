using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapScreenOwnershipAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var identity = new SrapIdentity(directory);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = SrapFixtureCertificate(signer);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        string parent = Guid.NewGuid().ToString("N"), pin = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
        var accept = AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint, parent, [
            new JsonObject { ["id"] = "display.mirror", ["state"] = "available", ["scope"] = "one_projection",
                ["limits"] = new JsonObject { ["codec"] = "h264", ["grant"] = "9", ["maxUnit"] = "1048576" } },
            new JsonObject { ["id"] = "volume.device", ["state"] = "available" }], ct);
        using var session = await new SrapClient(identity).ConnectAsync((IPEndPoint)listener.LocalEndpoint, pin, ct);
        using var remote = await accept;
        async Task<JsonElement> Read(string op)
        {
            var message = await SrapClient.ReadAsync(remote, ct);
            if (SrapClient.Text(message, "type") != "request" || SrapClient.Text(SrapClient.Body(message), "op") != op)
                throw new InvalidDataException("Expected " + op + ", got " + (SrapClient.Text(SrapClient.Body(message), "op") ?? SrapClient.Text(message, "type")));
            return message;
        }
        Task Reply(JsonElement message, JsonObject result) => SrapClient.WriteAsync(remote, "response",
            new() { ["status"] = "done", ["result"] = result }, ct, SrapClient.Text(message, "id"), parent);
        async Task AssertControlConnection()
        {
            var query = session.ReadVolumeAsync(ct);
            await Reply(await Read("volume.get"), new() { ["percent"] = 27 });
            Check((await query).GetProperty("percent").GetInt32() == 27 && !session.Token.IsCancellationRequested,
                "independent operations still work on the same authenticated connection");
        }
            var delivered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var existing = session.ExpectData(2, p => delivered.TrySetResult(p.Length));
            var view = session.ViewScreenAsync("Unopened fixture window", ct);
            var start = await Read("display.start");
            await Reply(start, new() { ["streamId"] = 2, ["width"] = 16, ["height"] = 16, ["codec"] = "h264" });
            bool busy = false;
            try { await view; } catch (SrapException e) when (e.Code == "busy") { busy = true; }
            await session.CloseScreenAsync(ct);
            Check(busy, "a colliding screen ID cannot take an already registered stream");
            await remote.WriteAsync(SrapProtocol.Encode(new(SrapFrameType.Data, 2, [7])), ct);
            await AssertControlConnection();
            Check(await delivered.Task.WaitAsync(ct) == 1, "the pre-existing stream still receives its own data");
            var bad = session.ViewScreenAsync("Unopened fixture window", ct);
            var second = await Read("display.start");
            await Reply(second, new() { ["streamId"] = 4, ["width"] = 1, ["height"] = 16, ["codec"] = "h264" });
            try { await bad; } catch (SrapException e) when (e.Code == "invalid_argument") { }
            var stop = await Read("stream.stop");
            var closing = session.CloseScreenAsync(ct);
            await Reply(stop, new() { ["streamId"] = 6, ["stopped"] = true });
            bool refused = false;
            try { await closing; } catch (SrapException e) when (e.Code == "io_failure") { refused = true; }
            var fallback = await SrapClient.ReadAsync(remote, ct);
            Check(refused && SrapClient.Text(fallback, "type") == "cancel" && SrapClient.Body(fallback).GetProperty("streamId").GetUInt32() == 4,
                "a different stream's stop receipt fails the close and cancels only the viewer's own ID");
            await AssertControlConnection();
        Console.WriteLine($"PASS {_checks} SRAP screen ownership checks");
    }
}

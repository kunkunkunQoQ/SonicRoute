using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapVerifySoakAsync(SrapSession session, int seconds, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        var ct = linked.Token;
        int samples = seconds <= 1800 ? Math.Clamp(1 + seconds / 30, 3, 7)
            : 1 + (int)Math.Ceiling(seconds / 600d);
        var latency = new double[samples];
        var elapsed = Stopwatch.StartNew();
        int completedSamples = 0;
        using var lost = session.Token.Register(() => Console.WriteLine(
            $"SOAK_DISCONNECTED elapsedMs={elapsed.ElapsedMilliseconds} completedSamples={Volatile.Read(ref completedSamples)}"));
        for (int i = 0; i < samples; i++)
        {
            var due = TimeSpan.FromSeconds(seconds * (double)i / (samples - 1)) - elapsed.Elapsed;
            if (due > TimeSpan.Zero) await Task.Delay(due, ct);
            var caps = session.Capabilities;
            bool volume = caps.Allows("volume.device"), devices = caps.Allows("audio.devices");
            if (!volume && !devices) throw new SrapException("unsupported");
            bool readVolume = volume && (!devices || i % 2 == 0);
            var timer = Stopwatch.StartNew();
            if (readVolume)
            {
                var result = await session.ReadVolumeAsync(ct);
                if (SrapClient.Text(result, "scope") != "system_media" || !result.GetProperty("percent").TryGetInt32(out int value)
                    || value is < 0 or > 100) throw new InvalidDataException("Invalid system media readback.");
            }
            else
            {
                var result = await session.RequestAsync("audio.devices.list", "audio.devices", new(), ct);
                if (!result.TryGetProperty("outputs", out var array) || array.ValueKind != JsonValueKind.Array
                    || !array.EnumerateArray().Any(o => SrapClient.Text(o, "key") is { Length: > 0 }))
                    throw new InvalidDataException("No readable phone output.");
            }
            latency[i] = timer.Elapsed.TotalMilliseconds;
            Volatile.Write(ref completedSamples, i + 1);
            Console.WriteLine($"SOAK_SAMPLE number={i + 1} elapsedMs={elapsed.ElapsedMilliseconds} operation={(readVolume ? "volume.get" : "audio.devices.list")} requestMs={latency[i]:F1} revision={session.Capabilities.Revision}");
            if (i != 0) continue;
            var skip = new CancellationToken(canceled: true);
            bool canceled = false;
            try { await session.ReadVolumeAsync(skip); } catch (OperationCanceledException) { canceled = true; }
            if (!canceled) throw new InvalidDataException("The local read cancellation was ignored.");
            Console.WriteLine("SOAK_LOCAL_CANCEL before dispatch");
        }
        ct.ThrowIfCancellationRequested();
        Array.Sort(latency);
        Console.WriteLine($"SOAK_RESULT elapsedMs={elapsed.ElapsedMilliseconds} samples={samples} minMs={latency[0]:F1} medianMs={latency[samples / 2]:F1} maxMs={latency[^1]:F1} wifiRoaming=unmeasured");
        Check(!session.Completion.IsCompleted && elapsed.Elapsed >= TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(20),
            "the same authenticated session returned valid readbacks throughout the requested interval");
    }

    private static async Task SrapSoakFixtureAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var identity = new SrapIdentity(directory);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = SrapFixtureCertificate(signer);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        string parent = Guid.NewGuid().ToString("N"), pin = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
        var accept = AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint, parent, [
            new JsonObject { ["id"] = "audio.devices", ["state"] = "available" },
            new JsonObject { ["id"] = "volume.device", ["state"] = "available" }], ct);
        using var session = await new SrapClient(identity).ConnectAsync((IPEndPoint)listener.LocalEndpoint, pin, ct);
        using var peer = await accept;
        var run = SrapVerifySoakAsync(session, 3, ct);
        foreach (var op in new[] { "volume.get", "audio.devices.list", "volume.get" })
        {
            var request = await SrapClient.ReadAsync(peer, ct);
            Check(SrapClient.Text(SrapClient.Body(request), "op") == op && SrapClient.Text(request, "session") == parent,
                "soak sends only the scheduled reads on the original mutual TLS connection");
            JsonObject result = op == "volume.get" ? new() { ["scope"] = "system_media", ["percent"] = 25 }
                : new() { ["outputs"] = new JsonArray(new JsonObject { ["key"] = "current" }) };
            await SrapClient.WriteAsync(peer, "response", new() { ["status"] = "done", ["result"] = result },
                ct, SrapClient.Text(request, "id"), parent);
        }
        await run;
        Console.WriteLine($"PASS {_checks} SRAP soak fixture checks");
    }
}

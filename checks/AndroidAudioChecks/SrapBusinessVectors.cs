using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private sealed record BusinessVector(string Expected, JsonElement Meta, SrapFrame Frame, byte[] Wire);

    private static async Task SrapBusinessVectorsAsync(string directory, string fixtures)
    {
        var vectors = new Dictionary<string, BusinessVector>(StringComparer.Ordinal);
        foreach (string row in File.ReadLines(fixtures).Where(line => line.Length != 0 && !line.StartsWith('#')))
        {
            var fields = row.Split('|');
            if (fields[0].StartsWith("file.", StringComparison.Ordinal)) continue;
            if (fields.Length != 5) throw new InvalidDataException("Invalid business fixture row.");
            using var metadata = JsonDocument.Parse(fields[2]);
            var meta = metadata.RootElement.Clone();
            var bytes = Convert.FromHexString(fields[3]);
            var wire = Convert.FromHexString(fields[4]);
            using var input = new MemoryStream(wire);
            var frame = await SrapProtocol.ReadAsync(input, CancellationToken.None) ?? throw new EndOfStreamException();
            bool data = fields[1] == "data";
            Check(frame.Type == (data ? SrapFrameType.Data : SrapFrameType.Control)
                && frame.StreamId == (data ? meta.GetProperty("streamId").GetUInt32() : 0)
                && input.Position == input.Length && frame.Payload.SequenceEqual(bytes) && SrapProtocol.Encode(frame).SequenceEqual(wire),
                fields[0] + " decodes and re-encodes the exact shared wire bytes");
            if (!data)
            {
                using var doc = SrapProtocol.ParseControl(frame);
                var body = SrapClient.Body(doc.RootElement);
                Check(SrapClient.Text(doc.RootElement, "type") == "request" && body.GetProperty("capabilityRevision").GetInt32() == 7
                    && SrapClient.Text(body, "op") == SrapClient.Text(meta, "op")
                    && JsonElement.DeepEquals(body.GetProperty("params"), meta.GetProperty("params")), fields[0] + " control parameters match the fixture");
            }
            vectors.Add(fields[0], new(fields[1], meta, frame, wire));
        }
        var screen = vectors["display.annexb"];
        var assembler = new ScreenFrameAssembler();
        assembler.Begin();
        assembler.Append(screen.Frame.Payload.AsMemory(0, 3));
        assembler.Append(screen.Frame.Payload.AsMemory(3));
        var config = assembler.Complete(screen.Meta.GetProperty("size").GetInt32());
        Check(screen.Expected == "data" && screen.Frame.StreamId % 2 == 0 && screen.Meta.GetProperty("seq").GetInt32() == 0
            && screen.Meta.GetProperty("ptsUs").GetInt64() == 0 && screen.Meta.GetProperty("config").GetBoolean()
            && !screen.Meta.GetProperty("key").GetBoolean() && config.Span.SequenceEqual(BuildTestBaselineH264_64x64().ConfigNal),
            "the shared screen chunks reconstruct the existing decoder fixture's Annex B SPS and PPS");

        Directory.CreateDirectory(directory);
        using var identity = new SrapIdentity(directory);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = SrapFixtureCertificate(signer);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        string parent = Guid.NewGuid().ToString("N"), pin = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
        var accepting = AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint, parent, [
            new JsonObject { ["id"] = "volume.device", ["state"] = "available", ["scope"] = "system_media",
                ["limits"] = new JsonObject { ["indexWrite"] = "1" } }], ct);
        using var session = await new SrapClient(identity).ConnectAsync((IPEndPoint)listener.LocalEndpoint, pin, ct);
        using var peer = await accepting;
        async Task<JsonElement> Read(string op)
        {
            var message = await SrapClient.ReadAsync(peer, ct);
            if (SrapClient.Text(SrapClient.Body(message), "op") != op) throw new InvalidDataException("Expected " + op);
            return message;
        }
        Task Reply(JsonElement message, JsonObject result) => SrapClient.WriteAsync(peer, "response",
            new() { ["status"] = "done", ["result"] = result }, ct, SrapClient.Text(message, "id"), parent);

        var typed = vectors["volume.index"];
        var expectedParams = typed.Meta.GetProperty("params");
        int index = expectedParams.GetProperty("streamIndex").GetInt32();
        var setting = session.SetVolumeIndexAsync(index, ct);
        var request = await Read("volume.set");
        var actualParams = SrapClient.Body(request).GetProperty("params");
        Check(typed.Expected == "accept" && JsonElement.DeepEquals(actualParams, expectedParams), "the negotiated Windows volume setter emits the shared exact-index parameters");
        foreach (var key in new[] { "volume.mixed", "volume.scope" })
        {
            var invalidVolume = vectors[key];
            Check(invalidVolume.Expected == "reject" && !JsonElement.DeepEquals(actualParams, invalidVolume.Meta.GetProperty("params")),
                key + " cannot be produced by the typed system volume setter");
        }
        await Reply(request, new() { ["scope"] = "system_media", ["streamIndex"] = index, ["minIndex"] = 0, ["maxIndex"] = 150 });
        Check((await setting).GetProperty("streamIndex").GetInt32() == index, "the exact volume index is confirmed");
        foreach (var kind in new[] { "screen", "audio" })
        {
            var vector = vectors["sharing." + kind];
            var parameters = vector.Meta.GetProperty("params");
            Check(vector.Expected == "accept" && SrapClient.Text(vector.Meta, "op") == "sharing.request"
                && parameters.EnumerateObject().Count() == 1 && SrapClient.Text(parameters, "kind") == kind,
                "the local " + kind + " prompt contains only its intended kind");
        }
        var mixed = vectors["sharing.mixed"];
        var listen = vectors["listen.request"];
        Check(mixed.Expected == "reject" && mixed.Meta.GetProperty("params").TryGetProperty("uid", out _),
            "a remote UID cannot broaden a local audio prompt");
        Check(listen.Expected == "accept" && SrapClient.Text(listen.Meta, "op") == "audio.listen.request"
            && SrapClient.Text(listen.Meta.GetProperty("params"), "request") == string.Concat(Enumerable.Repeat("01", 16)),
            "the shared listening request carries the single-use token");
        Console.WriteLine($"PASS {_checks} SRAP business vector checks");
    }
}

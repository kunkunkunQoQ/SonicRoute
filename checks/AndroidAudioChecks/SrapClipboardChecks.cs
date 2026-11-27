using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapClipboardChecksAsync(string directory, string fixture)
    {
        Directory.CreateDirectory(directory);
        foreach (string line in File.ReadLines(fixture).Where(row => row.Length != 0 && row[0] != '#'))
        {
            string[] f = line.Split('|');
            byte[] bytes = Convert.FromHexString(f[2]);
            Check(bytes.Length == int.Parse(f[1]) && SrapProtocol.Encode(new(SrapFrameType.Data, uint.Parse(f[0]), bytes))
                .SequenceEqual(Convert.FromHexString(f[3])), "fixed clipboard DATA bytes");
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = SrapFixtureCertificate(signer);
        using var identity = new SrapIdentity(directory);
        string pin = Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
        var store = new AndroidPhoneStore(Path.Combine(directory, "clipboard-" + Guid.NewGuid().ToString("N") + ".json"));
        await using var transport = new SrapPhoneTransport(store, directory);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        store.RegisterApp(pin, endpoint, store.ManagementRevision);
        string parent = Guid.NewGuid().ToString("N");
        var accept = AcceptSrapFixtureAsync(listener, cert, identity.Fingerprint, parent,
            [new JsonObject { ["id"] = "clipboard.sync", ["state"] = "available", ["scope"] = "focused_text" }], ct);
        using var session = await new SrapClient(identity).ConnectAsync(endpoint, pin, ct);
        using var remote = await accept;
        var device = transport.Admit(store.Profile, session);
        var client = new SrapClipboardClient(transport);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = Channel.CreateUnbounded<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watching = client.WatchAsync(device, () => ready.TrySetResult(), value => received.Writer.TryWrite(value), stop.Token);
        async Task<JsonElement> Request(string op)
        {
            var wire = await SrapClient.ReadAsync(remote, ct);
            Check(SrapClient.Text(wire, "session") == parent && SrapClient.Text(SrapClient.Body(wire), "op") == op, "session-scoped " + op);
            return wire;
        }
        Task Respond(JsonElement wire, string status, JsonObject? result = null, string? error = null) =>
            SrapClient.WriteAsync(remote, "response", new() { ["status"] = status, ["result"] = result, ["error"] = error }, ct,
                SrapClient.Text(wire, "id"), parent);
        var begin = await Request("clipboard.watch");
        string watch = SrapClient.Text(SrapClient.Body(begin).GetProperty("params"), "watchId")!;
        await Respond(begin, "done", new() { ["revision"] = 3 });
        await ready.Task.WaitAsync(ct);
        long seq = 0;
        async Task Changed(long revision, uint dataId, string? value)
        {
            byte[]? bytes = value == null ? null : SrapClipboardClient.StrictUtf8.GetBytes(value);
            var body = new JsonObject { ["name"] = bytes == null ? "clipboard.revision" : "clipboard.changed",
                ["watchId"] = watch, ["revision"] = revision };
            if (bytes != null) { body["byteLength"] = bytes.Length; body["streamId"] = dataId; }
            var env = new JsonObject { ["type"] = "event", ["session"] = parent, ["seq"] = seq++, ["body"] = body };
            await remote.WriteAsync(SrapProtocol.Encode(new(SrapFrameType.Control, 0, JsonSerializer.SerializeToUtf8Bytes(env))), ct);
            if (bytes is { Length: > 0 }) await remote.WriteAsync(SrapProtocol.Encode(new(SrapFrameType.Data, dataId, bytes)), ct);
        }
        await Changed(4, 2, "phone 同步🌟");
        Check(await received.Reader.ReadAsync(ct) == "phone 同步🌟" && client.Revision == 4, "UTF-8 phone data and revision");
        var sending = client.SendAsync("PC fixture", client.Revision, ct);
        var set = await Request("clipboard.set");
        var fields = SrapClient.Body(set).GetProperty("params");
        uint id = fields.GetProperty("streamId").GetUInt32();
        Check(id % 2 == 1 && fields.GetProperty("expectedRevision").GetInt64() == 4 &&
            SrapClient.Text(fields, "watchId") == watch && !fields.TryGetProperty("text", out _), "write metadata only");
        await Respond(set, "pending");
        var data = (await SrapProtocol.ReadAsync(remote, ct))!;
        Check(data.Type == SrapFrameType.Data && data.StreamId == id && SrapClipboardClient.StrictUtf8.GetString(data.Payload) == "PC fixture", "write body on its claimed stream");
        Check(!sending.IsCompleted, "send waits for platform readback");
        await Respond(set, "done", new() { ["revision"] = 5 });
        await sending;
        await Changed(6, 4, "embedded sonicroute://pair?fixture");
        await Changed(7, 0, null);
        await Changed(8, 0, "");
        Check(await received.Reader.ReadAsync(ct) == "" && client.Revision == 8, "invitation and revision-only changes never reach the desktop clipboard");
        bool refused = false;
        try { await client.SendAsync("stale", 5, ct); } catch (SrapException e) when (e.Code == "stale_session") { refused = true; }
        Check(refused, "stale version rejected before sending");
        stop.Cancel();
        var end = await Request("clipboard.unwatch");
        await Respond(end, "done", new());
        try { await watching; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Check(!received.Reader.TryRead(out _), "detached watch released");
    }
}

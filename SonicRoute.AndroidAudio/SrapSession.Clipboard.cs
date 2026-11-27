using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using static SonicRoute.AndroidAudio.SrapClient;

namespace SonicRoute.AndroidAudio;

internal sealed partial class SrapSession
{
    internal bool AllowsFocusedClipboard => Capabilities.Entries.TryGetValue("clipboard.sync", out var entry)
        && entry.State == "available" && entry.Scope == "focused_text";

    internal async Task<long> WriteClipboardAsync(string watchId, long expected, byte[] bytes, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel, token);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        if (!await requestSlots.WaitAsync(0, deadline.Token).ConfigureAwait(false)) throw new SrapException("busy");
        string id = RequestId();
        var responses = Channel.CreateBounded<JsonElement>(2);
        requests[id] = responses;
        bool sent = false;
        try
        {
            if (!AllowsFocusedClipboard) throw new SrapException("permission_lost");
            uint dataId = AllocateStreamId();
            await WriteAsync("request", new()
            {
                ["op"] = "clipboard.set", ["capabilityRevision"] = Capabilities.Revision,
                ["params"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["watchId"] = watchId, ["streamId"] = dataId, ["byteLength"] = bytes.Length, ["expectedRevision"] = expected
                }
            }, id, deadline.Token).ConfigureAwait(false);
            sent = true;
            var reply = await responses.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
            if (bytes.Length != 0)
            {
                if (Text(reply, "status") == "error") throw Error(reply);
                if (Text(reply, "status") != "pending" || !AllowsFocusedClipboard) throw new SrapException("stale_session");
                await WriteDataFrameAsync(dataId, bytes, deadline.Token).ConfigureAwait(false);
                reply = await responses.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
            }
            if (Text(reply, "status") == "error") throw Error(reply);
            if (Text(reply, "status") != "done" || !reply.TryGetProperty("result", out var result)
                || !result.TryGetProperty("revision", out var revision) || !revision.TryGetInt64(out long next) || next <= expected)
                throw new SrapException("invalid_argument");
            return next;
        }
        catch (OperationCanceledException) when (sent && !token.IsCancellationRequested)
        {
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
            cleanup.CancelAfter(TimeSpan.FromSeconds(1));
            try { await WriteAsync("cancel", new() { ["requestId"] = id }, null, cleanup.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { Dispose(); }
            throw;
        }
        finally
        {
            requests.TryRemove(id, out _);
            requestSlots.Release();
        }
    }
}

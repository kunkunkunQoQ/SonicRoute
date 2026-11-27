using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace SonicRoute.AndroidAudio;

internal sealed class SrapMediaClient(SrapPhoneTransport transport) : IPhoneMediaSession
{
    private sealed record Player(string Key, AndroidMediaState State);
    private sealed record Selection(AndroidDevice Device, SrapSession Session, Player Player);
    private Selection? selection;

    // This transport has no latency probe. The existing panel hides an unknown signal level.
    public event Action<AndroidConnectionQuality>? ConnectionQualityChanged { add { } remove { } }

    public async Task WatchAsync(AndroidDevice device, Action<AndroidMediaState> changed, CancellationToken token, bool measureLatency = true)
    {
        var session = transport.Session(device) ?? throw new SrapException("stale_session");
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        var wake = Channel.CreateBounded<bool>(1);
        void Refresh() => wake.Writer.TryWrite(true);
        var empty = new AndroidMediaState("", "", "", false, 0) { Outputs = [] };
        string watchId = Guid.NewGuid().ToString("D");
        session.MediaChanged += Refresh;
        session.CapabilitiesChanged += Refresh;
        try
        {
            bool first = true;
            do
            {
                if (!ReferenceEquals(session, transport.Session(device))) throw new SrapException("stale_session");
                if (!session.Capabilities.Allows("media.control")) throw new SrapException("permission_lost");
                JsonObject parameters = first ? new() { ["watchId"] = watchId } : new();
                JsonElement snapshot;
                try { snapshot = await session.RequestAsync("media.get", "media.control", parameters, watching.Token).ConfigureAwait(false); }
                catch (SrapException error) when (error.Code == "stale_session" && session.Capabilities.Allows("media.control")
                    && ReferenceEquals(session, transport.Session(device)))
                {
                    // A concurrent capability change can invalidate a read. Retry that read once.
                    snapshot = await session.RequestAsync("media.get", "media.control", parameters, watching.Token).ConfigureAwait(false);
                }
                first = false;
                if (!snapshot.TryGetProperty("players", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 16)
                    throw new SrapException("invalid_argument");
                var players = items.EnumerateArray().Select(ParsePlayer).ToArray();
                var primary = players.FirstOrDefault(p => p.State.Playing) ?? players.FirstOrDefault();
                var state = primary == null ? empty : primary.State with
                {
                    PlayingPackages = players.Where(p => p.State.Playing).Select(p => p.State.Package).Distinct().ToArray()
                };
                Volatile.Write(ref selection, primary == null ? null : new(device, session, primary));
                changed(state);
            } while (await wake.Reader.ReadAsync(watching.Token).ConfigureAwait(false));
        }
        finally
        {
            Volatile.Write(ref selection, null);
            session.MediaChanged -= Refresh;
            session.CapabilitiesChanged -= Refresh;
            if (!session.Token.IsCancellationRequested)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await session.RequestAsync("media.unwatch", null, new() { ["watchId"] = watchId }, cleanup.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ChannelClosedException or ObjectDisposedException) { }
            }
        }
    }

    public Task SendAsync(string command, CancellationToken token)
    {
        var current = GetSelection();
        string action = command switch
        {
            "toggle" when current.Player.State.CanToggle => current.Player.State.Playing ? "pause" : "play",
            "next" when current.Player.State.CanNext => "next",
            "previous" when current.Player.State.CanPrevious => "previous",
            _ => throw new SrapException("unsupported")
        };
        return ControlAsync(current, action, null, token);
    }

    public Task SeekAsync(string package, long positionMs, CancellationToken token)
    {
        var current = GetSelection();
        if (current.Player.State.Package != package) throw new SrapException("stale_session");
        if (!current.Player.State.CanSeek) throw new SrapException("unsupported");
        return ControlAsync(current, "seek", Math.Clamp(positionMs, 0, current.Player.State.DurationMs), token);
    }

    private Selection GetSelection()
    {
        var current = Volatile.Read(ref selection) ?? throw new SrapException("target_unavailable");
        if (!ReferenceEquals(transport.Session(current.Device), current.Session)) throw new SrapException("stale_session");
        return current;
    }

    private static async Task ControlAsync(Selection current, string action, long? position, CancellationToken token)
    {
        var parameters = new JsonObject { ["sessionKey"] = current.Player.Key, ["action"] = action };
        if (position.HasValue) parameters["position"] = position.Value;
        var reply = await current.Session.RequestAsync("media.control", "media.control", parameters, token).ConfigureAwait(false);
        if (!reply.TryGetProperty("player", out var result)) throw new SrapException("invalid_argument");
        var after = ParsePlayer(result);
        if (after.Key != current.Player.Key || after.State.Package != current.Player.State.Package) throw new SrapException("stale_session");
        if ((action == "play" && !after.State.Playing) || (action == "pause" && after.State.Playing))
            throw new SrapException("io_failure");
    }

    private static Player ParsePlayer(JsonElement element)
    {
        string? key = SrapClient.Text(element, "sessionKey");
        if (key == null || !Guid.TryParseExact(key, "D", out var id) || id.ToString("D") != key)
            throw new SrapException("invalid_argument");
        var state = AndroidMediaState.Parse(element) with { Outputs = [] };
        if (!state.HasSession) throw new SrapException("invalid_argument");
        return new(key, state);
    }
}

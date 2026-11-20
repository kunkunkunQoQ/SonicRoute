using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SonicRoute.WindowsLink;

internal sealed record LinkFullTrustState(bool Enabled, bool Supported, string? LocalCode, bool PeerCodeReady,
    bool Submitted, bool PeerVerified, DateTimeOffset? ExpiresAt);

internal sealed class LinkTrustExchange
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);
    internal readonly long Started = Stopwatch.GetTimestamp();
    internal readonly DateTimeOffset ExpiresAt = DateTimeOffset.UtcNow + Lifetime;
    internal Guid LocalId, RemoteId, Attempt;
    internal string? LocalCode;
    internal int WrongGuesses;
    internal bool Submitted, PeerVerified;
    internal Timer? Expiration;
    internal TaskCompletionSource<bool>? Answer;
}

internal sealed partial class WindowsLinkService
{
    private bool _trustSavePending;
    internal event Action<string>? FullTrustChanged;

    private void NotifyFullTrust(string peerId)
    {
        try { FullTrustChanged?.Invoke(peerId); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    // Called under the service gate before detaching or replacing an authenticated connection.
    private bool HasTrustSessionState(LinkConnection? link) =>
        link != null && (link.TrustExchange != null || link.RemoteTrustGrant != Guid.Empty
            && _saved.GetValueOrDefault(link.Id)?.FullTrustGrant == link.RemoteTrustGrant);

    // Revocation always takes effect in memory. The next normal peer save retries the complete snapshot.
    private Exception? ClearFullTrustGrant(SavedPeer saved)
    {
        if (saved.FullTrustGrant == Guid.Empty && !_trustSavePending) return null;
        var cleared = saved with { FullTrustGrant = Guid.Empty };
        try { SavePeer(saved.Id, cleared); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _saved[saved.Id] = cleared;
            _trustSavePending = true;
            return ex;
        }
        return null;
    }

    public LinkFullTrustState GetFullTrustState(string peerId)
    {
        lock (_gate)
        {
            LoadSavedPeers();
            var saved = _saved.GetValueOrDefault(peerId);
            var link = _connections.GetValueOrDefault(peerId);
            var round = link == null ? null : CurrentTrustExchange(link);
            return new(saved != null && saved.FullTrustGrant != Guid.Empty,
                ((link?.Hello.Capabilities ?? saved?.Capabilities ?? 0) & LinkCapabilities.FullTrust) != 0,
                round?.LocalCode, round != null && round.RemoteId != Guid.Empty,
                round?.Submitted == true, round?.PeerVerified == true, round?.ExpiresAt);
        }
    }

    public bool IsFullyTrusted(string peerId)
    {
        lock (_gate) return _connections.TryGetValue(peerId, out var link) && IsFullyTrusted(link);
    }

    private bool CanDeliverWindowInput(Track track)
    {
        lock (_gate) return _connections.GetValueOrDefault(track.Connection.Id) == track.Connection
            && track.Connection.IsOpen && (!track.UsesPeerInputPermission
                || _saved.GetValueOrDefault(track.Connection.Id)?.Settings?.AllowRemoteInput == true
                || IsFullyTrusted(track.Connection));
    }

    private bool IsFullyTrusted(LinkConnection link)
    {
        lock (_gate) return _run != null && link.IsOpen && _connections.GetValueOrDefault(link.Id) == link
            && link.RemoteTrustGrant != Guid.Empty && _saved.GetValueOrDefault(link.Id)?.FullTrustGrant == link.RemoteTrustGrant;
    }

    // Only a local user action generates a code. The peer receives the challenge ID, never the displayed code.
    public Task BeginFullTrustAsync(string peerId, CancellationToken token = default)
    {
        Task written;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            var link = Connected(peerId, LinkCapabilities.FullTrust);
            if (_saved[peerId].FullTrustGrant != Guid.Empty) return Task.CompletedTask;
            var round = CurrentTrustExchange(link) ?? StartTrustExchange(link);
            if (round.LocalId != Guid.Empty) return Task.CompletedTask;
            round.LocalId = Guid.NewGuid();
            round.LocalCode = RandomNumberGenerator.GetInt32(100_000_000).ToString("D8", CultureInfo.InvariantCulture);
            written = SendTrustFrame(link, LinkMessage.TrustBegin, round.LocalId, []);
        }
        NotifyFullTrust(peerId);
        Notify();
        return written.WaitAsync(token);
    }

    public async Task<bool> SubmitFullTrustCodeAsync(string peerId, string code, CancellationToken token = default)
    {
        if (code == null || code.Length != 8 || !code.All(char.IsAsciiDigit)) return false;
        Task<bool> answer;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            var link = Connected(peerId, LinkCapabilities.FullTrust);
            if (IsFullyTrusted(link)) return true;
            var round = CurrentTrustExchange(link);
            if (round?.LocalCode == null || round.RemoteId == Guid.Empty) return false;
            if (round.Submitted) return true;
            if (round.Answer != null) return false;
            var body = new byte[40];
            round.RemoteId.TryWriteBytes(body.AsSpan(0, 16));
            round.LocalId.TryWriteBytes(body.AsSpan(16, 16));
            Encoding.ASCII.GetBytes(code, body.AsSpan(32));
            round.Attempt = Guid.NewGuid();
            round.Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            answer = round.Answer.Task;
            _ = SendTrustFrame(link, LinkMessage.TrustTry, round.Attempt, body);
        }
        return await answer.WaitAsync(token).ConfigureAwait(false);
    }

    public async Task RevokeFullTrustAsync(string peerId, CancellationToken token = default)
    {
        Task written = Task.CompletedTask;
        Exception? saveFailure;
        bool changed;
        lock (_gate)
        {
            LoadSavedPeers();
            if (!_saved.TryGetValue(peerId, out var saved)) return;
            // Revoke on this device before attempting to contact the peer, including when it is offline.
            changed = saved.FullTrustGrant != Guid.Empty || _connections.GetValueOrDefault(peerId)?.TrustExchange != null;
            saveFailure = ClearFullTrustGrant(saved);
            if (_connections.TryGetValue(peerId, out var link))
            {
                CloseTrustExchange(link);
                link.RemoteTrustGrant = Guid.Empty;
                if (link.IsOpen && link.Hello.Capabilities.HasFlag(LinkCapabilities.FullTrust))
                    written = SendTrustFrame(link, LinkMessage.TrustRevoke, Guid.Empty, []);
            }
        }
        RevokeFullTrustAccess(peerId);
        if (changed) NotifyFullTrust(peerId);
        Notify();
        await written.WaitAsync(token).ConfigureAwait(false);
        if (saveFailure != null) throw new IOException("Trust was disabled, but Windows Link could not save the change.", saveFailure);
    }

    private void RevokeFullTrustAccess(string peerId)
    {
        List<ILinkInput> inputs = [];
        lock (_gate)
        {
            if (_saved.GetValueOrDefault(peerId)?.Settings?.AllowRemoteInput != true && !IsFullyTrusted(peerId))
                foreach (var track in _streams.Values)
                    if (track.UsesPeerInputPermission && !track.Closing && track.Connection.Id.Equals(peerId, StringComparison.OrdinalIgnoreCase)
                        && track.DetachInput() is { } input)
                    {
                        track.Info = track.Info with { AllowsInput = false };
                        inputs.Add(input);
                    }
        }
        foreach (var input in inputs) input.Dispose();
        ClosePeerFileBrowsers(peerId);
        RevokeTrustedFileReceives(peerId);
    }

    // Writes enter the TLS control queue synchronously under _gate, preserving state/challenge ordering.
    private static async Task SendTrustFrame(LinkConnection link, LinkMessage type, Guid id, byte[] body)
    {
        try { await link.SendAsync(type, id, body).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        { link.Abort(); }
    }

    private LinkTrustExchange StartTrustExchange(LinkConnection link)
    {
        // A new local/remote round may start as the previous round's one-shot expiration is being dispatched.
        if (link.TrustExchange != null) CancelTrustExchange(link);
        var round = new LinkTrustExchange();
        link.TrustExchange = round;
        // Exactly one wake per pending exchange. An idle paired link has no trust timers.
        round.Expiration = new Timer(_ =>
        {
            lock (_gate)
            {
                if (link.TrustExchange != round) return;
                CancelTrustExchange(link);
            }
            NotifyFullTrust(link.Id);
            Notify();
        }, null, LinkTrustExchange.Lifetime, Timeout.InfiniteTimeSpan);
        return round;
    }

    private static void CloseTrustExchange(LinkConnection link)
    {
        var round = link.TrustExchange;
        link.TrustExchange = null;
        if (round == null) return;
        round.Expiration?.Dispose();
        round.Answer?.TrySetResult(false);
        round.LocalCode = null;
    }

    private static void CancelTrustExchange(LinkConnection link)
    {
        if (link.TrustExchange is not { } round) return;
        Guid id = round.LocalId != Guid.Empty ? round.LocalId : round.RemoteId;
        CloseTrustExchange(link);
        if (id != Guid.Empty && link.IsOpen) _ = SendTrustFrame(link, LinkMessage.TrustCancel, id, []);
    }

    private static LinkTrustExchange? CurrentTrustExchange(LinkConnection link)
    {
        return link.TrustExchange is { } round && Stopwatch.GetElapsedTime(round.Started) < LinkTrustExchange.Lifetime
            ? round : null;
    }

    private void HandleFullTrust(LinkConnection link, LinkMessage type, Guid id, ReadOnlySpan<byte> body)
    {
        bool revoked = false;
        bool changed, permissionChanged;
        Exception? saveFailure = null;
        lock (_gate)
        {
            if (_connections.GetValueOrDefault(link.Id) != link || !link.IsOpen || !_saved.TryGetValue(link.Id, out var saved))
                return;
            if (!link.Hello.Capabilities.HasFlag(LinkCapabilities.FullTrust))
                throw new InvalidDataException("Unexpected Windows Link trust negotiation.");
            var previous = GetFullTrustState(link.Id);
            bool previouslyActive = IsFullyTrusted(link);
            var round = CurrentTrustExchange(link);
            switch (type)
            {
                case LinkMessage.TrustState:
                    var remoteGrant = new Guid(body);
                    if (remoteGrant == link.RemoteTrustGrant && (saved.FullTrustGrant == Guid.Empty || saved.FullTrustGrant == remoteGrant))
                        return;
                    link.RemoteTrustGrant = remoteGrant;
                    if (saved.FullTrustGrant != Guid.Empty && saved.FullTrustGrant != link.RemoteTrustGrant)
                    {
                        saveFailure = ClearFullTrustGrant(saved);
                        revoked = true;
                    }
                    break;
                case LinkMessage.TrustRevoke:
                    CloseTrustExchange(link);
                    link.RemoteTrustGrant = Guid.Empty;
                    if (saved.FullTrustGrant != Guid.Empty)
                    {
                        saveFailure = ClearFullTrustGrant(saved);
                        revoked = true;
                    }
                    break;
                case LinkMessage.TrustBegin:
                    if (saved.FullTrustGrant != Guid.Empty) return;
                    round ??= StartTrustExchange(link);
                    // A peer cannot reset the local challenge or its attempt limit by announcing new IDs.
                    if (round.RemoteId != Guid.Empty) return;
                    round.RemoteId = id;
                    break;
                case LinkMessage.TrustTry:
                    if (round?.LocalCode == null || new Guid(body[..16]) != round.LocalId
                        || round.RemoteId == Guid.Empty || new Guid(body.Slice(16, 16)) != round.RemoteId) return;
                    bool accepted = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(round.LocalCode), body[32..]);
                    _ = SendTrustFrame(link, LinkMessage.TrustAnswer, id, [accepted ? (byte)1 : (byte)0]);
                    if (!accepted)
                    {
                        if (++round.WrongGuesses >= 3) CancelTrustExchange(link);
                        break;
                    }
                    round.PeerVerified = true;
                    CompleteFullTrust(link, round, saved);
                    break;
                case LinkMessage.TrustAnswer:
                    if (round?.Answer == null || round.Attempt != id || body[0] > 1) return;
                    round.Submitted = body[0] == 1;
                    var answer = round.Answer;
                    round.Answer = null;
                    round.Attempt = Guid.Empty;
                    try
                    {
                        CompleteFullTrust(link, round, saved);
                        answer.TrySetResult(round.Submitted);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        answer.TrySetException(ex);
                        link.Abort();
                        throw;
                    }
                    break;
                case LinkMessage.TrustCancel:
                    if (round == null || (round.LocalId != id && round.RemoteId != id)) return;
                    CloseTrustExchange(link);
                    break;
            }
            permissionChanged = previouslyActive != IsFullyTrusted(link);
            changed = permissionChanged || previous != GetFullTrustState(link.Id);
        }
        if (revoked) RevokeFullTrustAccess(link.Id);
        if (changed) NotifyFullTrust(link.Id);
        if (saveFailure != null) Report(new IOException("Trust was disabled, but Windows Link could not save the change.", saveFailure));
        Notify();
    }

    private void CompleteFullTrust(LinkConnection link, LinkTrustExchange round, SavedPeer saved)
    {
        if (!round.Submitted || !round.PeerVerified) return;
        // Both endpoints derive the same enrollment ID. Reconnect requires a matching durable ID on BOTH devices.
        Span<byte> nonces = stackalloc byte[32];
        bool localFirst = round.LocalId.CompareTo(round.RemoteId) <= 0;
        (localFirst ? round.LocalId : round.RemoteId).TryWriteBytes(nonces[..16]);
        (localFirst ? round.RemoteId : round.LocalId).TryWriteBytes(nonces[16..]);
        var grant = new Guid(SHA256.HashData(nonces).AsSpan(0, 16));
        SavePeer(link.Id, saved with { FullTrustGrant = grant });
        CloseTrustExchange(link);
        _ = SendTrustFrame(link, LinkMessage.TrustState, Guid.Empty, grant.ToByteArray());
    }
}

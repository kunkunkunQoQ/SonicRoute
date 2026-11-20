using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using SonicRoute.Core;

namespace SonicRoute.WindowsLink;

internal sealed partial class WindowsLinkService
{
    private sealed class RetryWork : IDisposable
    {
        internal readonly CancellationTokenSource Stop = new();
        internal readonly SemaphoreSlim Wake = new(0, 1);
        internal Task Task { get; set; } = Task.CompletedTask;
        internal bool Dialing;

        internal void Signal() { if (Wake.CurrentCount == 0) Wake.Release(); }
        public void Dispose() { Wake.Dispose(); Stop.Dispose(); }
    }

    internal async Task<LinkResolvedAddress> ResolveDirectAddressAsync(string address, CancellationToken token, string? expectedPeerId = null)
    {
        if (!LinkDirectAddress.TryParse(address, out var target))
            throw new ArgumentException("Enter a host name or IP address with an optional TCP port.", nameof(address));
        lock (_gate)
        {
            string? id = expectedPeerId ?? MatchDirectAddressLocked(target, []);
            if (id != null && _connections.GetValueOrDefault(id)?.IsOpen == true) return new(target, [], id);
        }
        var endpoints = await target.ResolveAsync(token).ConfigureAwait(false);
        int localPort;
        lock (_gate) localPort = _run?.Port ?? port;
        if (target.Port == localPort)
        {
            endpoints = endpoints.Where(ep => !LinkDirectAddress.IsLocal(ep.Address)).ToArray();
            if (endpoints.Length == 0) throw new LinkSelfConnectionException();
        }
        lock (_gate)
        {
            string? id = expectedPeerId ?? MatchDirectAddressLocked(target, endpoints);
            return new(target, endpoints, id != null && _connections.GetValueOrDefault(id)?.IsOpen == true ? id : null);
        }
    }

    private string? MatchDirectAddressLocked(LinkDirectAddress target, IPEndPoint[] endpoints)
    {
        var matches = _saved.Values.Where(p => p.DirectAddress == target.Canonical).Take(2).ToArray();
        if (matches.Length == 0)
            matches = _saved.Values.Where(p => p.Address == target.Canonical || TryAddress(p.Address, out var known)
                && endpoints.Contains(known)).Take(2).ToArray();
        if (matches.Length > 1) throw new LinkAmbiguousAddressException();
        return matches.FirstOrDefault()?.Id;
    }

    public async Task ConnectAsync(string address, string? expectedPeerId = null, CancellationToken token = default)
    {
        if (expectedPeerId != null && !LinkSecurity.IsId(expectedPeerId))
            throw new ArgumentException("Invalid Windows Link peer identity.", nameof(expectedPeerId));
        var target = await ResolveDirectAddressAsync(address, token, expectedPeerId).ConfigureAwait(false);
        await ConnectResolvedAsync(target, expectedPeerId, token).ConfigureAwait(false);
    }

    internal Task ConnectResolvedAsync(LinkResolvedAddress target, string? expectedPeerId, CancellationToken token)
    {
        LinkRun run = GetRun();
        string? savedId;
        lock (_gate)
        {
            // A typed address can refer to a saved peer. Pin its TLS identity on recovery.
            savedId = expectedPeerId == null ? MatchDirectAddressLocked(target.Target, target.Endpoints)
                : _saved.ContainsKey(expectedPeerId) ? expectedPeerId : null;
        }
        if (savedId != null)
        {
            if (IsConnected(savedId)) return Task.CompletedTask;
            return StartManualConnection(run, savedId, active => ReconnectSavedAsync(run, savedId, active, target), token);
        }
        if (expectedPeerId != null && IsConnected(expectedPeerId)) return Task.CompletedTask;

        async Task DialEndpointAsync(IPEndPoint endpoint, CancellationToken active, string? direct)
        {
            try { await DialAsync(run, endpoint, expectedPeerId, permitPairing: true, active, directAddress: direct).ConfigureAwait(false); }
            catch (LinkDuplicatePairingException) { }
        }

        async Task Connect(CancellationToken active)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(active, run.Token);
            active = linked.Token;
            Exception? failure = null;
            foreach (var endpoint in target.Endpoints)
            {
                try
                {
                    await DialEndpointAsync(endpoint, active, target.Target.Canonical).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (!active.IsCancellationRequested && ex is SocketException or OperationCanceledException)
                { failure = ex; }
            }
            if (expectedPeerId != null && !active.IsCancellationRequested)
            {
                if (IsConnected(expectedPeerId)) return;
                IPEndPoint[] fallbacks;
                lock (_gate)
                {
                    fallbacks = FreshOffersLocked(expectedPeerId)
                        .OrderByDescending(item => AddressRankLocked(item.Offer.Endpoint))
                        .ThenByDescending(item => item.ExpiresAt)
                        .Select(item => item.Offer.Endpoint)
                        .Where(ep => !target.Endpoints.Contains(ep))
                        .Distinct()
                        .ToArray();
                }
                foreach (var alt in fallbacks)
                {
                    if (IsConnected(expectedPeerId)) return;
                    try
                    {
                        await DialEndpointAsync(alt, active, null).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception altEx) when (!active.IsCancellationRequested
                        && altEx is SocketException or OperationCanceledException) { }
                }
            }
            active.ThrowIfCancellationRequested();
            throw new LinkConnectionFailureException(ClassifyFailure(failure), "The Windows Link address is unreachable.", failure);
        }

        return expectedPeerId == null ? run.Track(() => Connect(token))
            : StartManualConnection(run, expectedPeerId, Connect, token);
    }

    public async Task ReconnectPeerAsync(string peerId, CancellationToken token = default)
    {
        await EnableAsync(token).ConfigureAwait(false);
        if (IsConnected(peerId)) return;
        LinkRun run = GetRun();
        await StartManualConnection(run, peerId, active => ReconnectSavedAsync(run, peerId, active), token).ConfigureAwait(false);
    }

    private Task StartManualConnection(LinkRun run, string id, Func<CancellationToken, Task> connect, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task work;
        lock (_gate)
        {
            if (_run != run) return Task.FromCanceled(new CancellationToken(true));
            if (run.ManualConnections.TryGetValue(id, out var existing)) return existing.Task.WaitAsync(token);
            var retry = StopRetryLocked(run, id);
            var attempt = new RetryWork();
            run.ManualConnections.Add(id, attempt);
            _failures.Remove(id);
            work = attempt.Task = run.Track(async () =>
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, run.Token, attempt.Stop.Token);
                    if (retry != null) await retry.WaitAsync(linked.Token).ConfigureAwait(false);
                    await connect(linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsConnectionFailure(ex))
                {
                    if (!token.IsCancellationRequested && !attempt.Stop.IsCancellationRequested)
                        SetConnectionFailure(run, id, ClassifyFailure(ex), ex);
                    throw;
                }
                finally
                {
                    lock (_gate)
                    {
                        if (run.ManualConnections.GetValueOrDefault(id) == attempt) run.ManualConnections.Remove(id);
                        attempt.Dispose();
                    }
                    Notify();
                    ScheduleRetry(run, id);
                }
            });
        }
        Notify();
        return work;
    }

    private async Task<bool> RequestReverseAsync(LinkRun run, string id, IEnumerable<IPEndPoint> endpoints,
        TimeSpan wait, CancellationToken token)
    {
        if (run.Lan == null || _identity is not { } identity) return false;
        var addresses = endpoints.Select(e => e.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).Distinct().ToArray();
        if (addresses.Length == 0) return false;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe() { if (IsConnected(id)) connected.TrySetResult(); }
        Changed += Observe;
        try
        {
            Observe();
            if (connected.Task.IsCompleted) return true;
            byte[] request = LinkSecurity.CreateReconnectRequest(identity, id, run.Port);
            foreach (var address in addresses)
                await run.Lan.RequestReconnectAsync(request, address, token).ConfigureAwait(false);
            await connected.Task.WaitAsync(wait, token).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return IsConnected(id); }
        finally { Changed -= Observe; }
    }

    private async Task ReconnectSavedAsync(LinkRun run, string id, CancellationToken token, LinkResolvedAddress? requested = null)
    {
        var tried = new HashSet<IPEndPoint>();
        Exception? failure = await TryCandidatesAsync(run, id, tried, permitPairing: true, token, requested).ConfigureAwait(false);
        if (IsConnected(id)) return;
        if (ClassifyFailure(failure) == LinkConnectFailure.PairingRejected)
            throw failure!;
        if (run.Lan != null)
        {
            await SearchAsync(token).ConfigureAwait(false);
            failure = await TryCandidatesAsync(run, id, tried, permitPairing: true, token).ConfigureAwait(false) ?? failure;
            if (IsConnected(id)) return;
            if (ClassifyFailure(failure) == LinkConnectFailure.PairingRejected)
                throw failure!;
            if (await RequestReverseAsync(run, id, tried, TimeSpan.FromSeconds(6), token).ConfigureAwait(false)) return;
        }

        token.ThrowIfCancellationRequested();
        throw new LinkConnectionFailureException(ClassifyFailure(failure), "Neither computer completed the Windows Link connection.", failure);
    }

    private async Task<Exception?> TryCandidatesAsync(LinkRun run, string id, HashSet<IPEndPoint> tried,
        bool permitPairing, CancellationToken token, LinkResolvedAddress? requested = null)
    {
        bool reverseFirst;
        IPEndPoint[] candidates;
        bool advertised;
        string? directAddress;
        lock (_gate)
        {
            if (_run != run || !_saved.TryGetValue(id, out var saved))
                throw new InvalidOperationException("This Windows Link device is no longer paired.");
            candidates = ConnectionCandidatesLocked(id, saved);
            directAddress = saved.DirectAddress;
            reverseFirst = permitPairing && tried.Count == 0 && saved.LastConnectionIncoming && run.Lan != null;
            advertised = FreshOffersLocked(id).Any();
        }

        Exception? failure = null;
        if ((requested == null || requested.Endpoints.Length == 0) && tried.Count == 0 && LinkDirectAddress.TryParse(directAddress, out var direct))
        {
            try { requested = new(direct, await direct.ResolveAsync(token).ConfigureAwait(false), null); }
            catch (SocketException ex) { failure = ex; }
        }
        if (requested != null)
            candidates = requested.Endpoints.Concat(candidates).Distinct().ToArray();
        if (reverseFirst && requested == null && await RequestReverseAsync(run, id, candidates, TimeSpan.FromSeconds(1), token).ConfigureAwait(false))
            return null;
        foreach (var endpoint in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (IsConnected(id)) return null;
            if (!tried.Add(endpoint)) continue;
            try
            {
                await run.Outbound.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    string? authenticatedAddress = requested?.Endpoints.Contains(endpoint) == true ? requested.Target.Canonical : null;
                    await DialAsync(run, endpoint, id, permitPairing, token, tcpTimeoutSeconds: 3, directAddress: authenticatedAddress).ConfigureAwait(false);
                    if (IsConnected(id)) return null;
                }
                finally { run.Outbound.Release(); }
            }
            catch (LinkDuplicatePairingException) when (!token.IsCancellationRequested)
            {
                if (IsConnected(id)) return null;
            }
            catch (LinkAuthenticationFailureException ex) when (!token.IsCancellationRequested)
            {
                var authError = new LinkConnectionFailureException(ex.Reason, ex.Message, ex);
                if (ex.Reason == LinkConnectFailure.PairingRejected) return authError;
                failure = authError;
            }
            catch (Exception ex) when (IsConnectionFailure(ex) && !token.IsCancellationRequested)
            {
                LinkConnectFailure kind = ClassifyFailure(ex);
                if (!advertised && kind == LinkConnectFailure.TcpUnreachable) kind = LinkConnectFailure.SavedAddressUnavailable;
                if (kind == LinkConnectFailure.IdentityMismatch || ClassifyFailure(failure) != LinkConnectFailure.IdentityMismatch)
                    failure = new LinkConnectionFailureException(kind, ex.Message, ex);
            }
        }
        return failure;
    }

    private bool IsConnected(string id)
    {
        lock (_gate) return _connections.GetValueOrDefault(id)?.IsOpen == true;
    }

    private static bool IsConnectionFailure(Exception e) =>
        e is IOException or AuthenticationException or SocketException or OperationCanceledException or JsonException or InvalidDataException;

    internal static LinkConnectFailure ClassifyFailure(Exception? error) => error switch
    {
        LinkConnectionFailureException failed => failed.Reason,
        LinkAuthenticationFailureException failed => failed.Reason,
        SocketException or OperationCanceledException => LinkConnectFailure.TcpUnreachable,
        AuthenticationException => LinkConnectFailure.IdentityMismatch,
        JsonException or InvalidDataException => LinkConnectFailure.Incompatible,
        null => LinkConnectFailure.SavedAddressUnavailable,
        _ => LinkConnectFailure.Unknown
    };

    private void SetConnectionFailure(LinkRun run, string id, LinkConnectFailure reason, Exception error)
    {
        lock (_gate)
        {
            if (_run != run || _connections.GetValueOrDefault(id)?.IsOpen == true
                || (!_saved.ContainsKey(id) && !_offers.ContainsKey(id))
                || _failures.GetValueOrDefault(id) == reason) return;
            _failures[id] = reason;
        }
        Report(error);
        Notify();
    }

    private void HandleReconnectRequest(LinkRun run, string localId, ReadOnlyMemory<byte> packet, IPAddress address)
    {
        if (!LinkSecurity.TryReadReconnectRequest(packet.Span, localId, address, Trusts, out var request)) return;
        var reverse = request!;
        lock (_gate)
        {
            if (_run != run || !_saved.ContainsKey(reverse.Id) || _connections.GetValueOrDefault(reverse.Id)?.IsOpen == true)
                return;
            if (run.ReverseDials.TryGetValue(reverse.Id, out var previous)
                && (reverse.Issued <= previous.Issued || Stopwatch.GetElapsedTime(previous.AcceptedAt) < TimeSpan.FromSeconds(5)))
                return;
            run.ReverseDials[reverse.Id] = (reverse.Issued, Stopwatch.GetTimestamp());
            _ = run.Track(async () =>
            {
                try { await DialAsync(run, reverse.Endpoint, reverse.Id, permitPairing: false, run.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or AuthenticationException or SocketException or OperationCanceledException or JsonException)
                { }
            });
        }
    }

    internal async Task<double?> MeasureRoundTripAsync(string peerId, CancellationToken token)
    {
        LinkConnection? link;
        lock (_gate) link = _connections.GetValueOrDefault(peerId);
        if (link == null) return null;
        var ms = await link.MeasureRoundTripAsync(token).ConfigureAwait(false);
        lock (_gate) return _connections.GetValueOrDefault(peerId) == link ? ms : null;
    }

    internal (long Sent, long Received) ReadTraffic(string peerId)
    {
        lock (_gate)
        {
            var link = _connections.GetValueOrDefault(peerId);
            return (link?.SentBytes ?? 0, link?.ReceivedBytes ?? 0);
        }
    }

    internal static bool TryAddress(string? address, out IPEndPoint? endpoint)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            endpoint = null;
            return false;
        }
        if (IPAddress.TryParse(address, out var ip))
        {
            endpoint = new IPEndPoint(ip, 46916);
            return !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any);
        }
        return IPEndPoint.TryParse(address, out endpoint) && endpoint.Port > 0
            && !endpoint.Address.Equals(IPAddress.Any) && !endpoint.Address.Equals(IPAddress.IPv6Any);
    }

    private async Task DialAsync(LinkRun run, IPEndPoint endpoint, string? expected, bool permitPairing, CancellationToken token,
        int tcpTimeoutSeconds = 5, string? directAddress = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Token, token);
        var client = new TcpClient(endpoint.AddressFamily);
        if (permitPairing)
            lock (_gate) run.Dialing[endpoint] = run.Dialing.GetValueOrDefault(endpoint) + 1;
        try
        {
            using (var dial = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
            {
                dial.CancelAfter(TimeSpan.FromSeconds(tcpTimeoutSeconds));
                await client.ConnectAsync(endpoint, dial.Token).ConfigureAwait(false);
            }
            if (!await EstablishAsync(run, client, incoming: false, expected, permitPairing, linked.Token, directAddress).ConfigureAwait(false))
                client.Dispose();
        }
        catch { client.Dispose(); throw; }
        finally
        {
            if (permitPairing)
                lock (_gate)
                {
                    if (run.Dialing[endpoint] == 1) run.Dialing.Remove(endpoint);
                    else run.Dialing[endpoint]--;
                }
        }
    }

    private async Task AcceptAsync(LinkRun run)
    {
        while (!run.Token.IsCancellationRequested)
        {
            TcpClient client = await run.Listener.AcceptTcpClientAsync(run.Token).ConfigureAwait(false);
            if (!run.Handshakes.Wait(0))
            {
                client.Dispose();
                continue;
            }
            Task job = run.Track(async () =>
            {
                bool added = false;
                try { added = await EstablishAsync(run, client, incoming: true, null, true, run.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is AuthenticationException or OperationCanceledException or IOException
                    or SocketException or JsonException or ObjectDisposedException) { }
                finally
                {
                    if (!added) client.Dispose();
                    run.Handshakes.Release();
                }
            });
            if (job.IsCanceled) { client.Dispose(); run.Handshakes.Release(); }
        }
    }

    private async Task<bool> EstablishAsync(LinkRun run, TcpClient client, bool incoming, string? expected,
        bool permitPairing, CancellationToken token, string? directAddress = null)
    {
        client.NoDelay = true;
        client.SendBufferSize = 64 * 1024;
        long trustRevision;
        lock (_gate) trustRevision = run.TrustRevision;
        var endpoint = (IPEndPoint)client.Client.RemoteEndPoint!;
        IPAddress remoteIp = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        var identity = _identity!;
        string localId = LinkSecurity.Id(identity);
        LinkSecure secure = await LinkSecurity.AuthenticateAsync(client, incoming, identity, expected, _name, run.Port,
            LocalCapabilities, Trusts, (id, hello) =>
            {
                if (!permitPairing) return false;
                lock (_gate)
                {
                    if (_pairing.Values.Any(p => p.PeerId == id) || _connections.GetValueOrDefault(id)?.IsOpen == true) return false;
                    return !incoming || string.CompareOrdinal(localId, id) > 0
                        || !run.Dialing.ContainsKey(new IPEndPoint(remoteIp, hello.Port));
                }
            }, permitPairing ? ConfirmAsync : (_, _, _, _) => Task.FromResult(false), permitPairing, token,
            supportsClipboardSync: true).ConfigureAwait(false);
        bool transferred = false;
        try
        {
            bool preferred = incoming == (string.CompareOrdinal(localId, secure.Id) > 0);
            LinkConnection? retired = null;
            bool resetTrust;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_run != run) return false;
                if (run.TrustRevision != trustRevision && (_paused.Contains(secure.Id) || !_saved.ContainsKey(secure.Id)))
                    return false;
                if (!_saved.ContainsKey(secure.Id) && (!permitPairing || !secure.PairingVerified)) return false;
                if (_connections.TryGetValue(secure.Id, out var existing))
                {
                    if (existing.IsOpen && (!existing.RecentlyOpened || !existing.RemoteAddress.Equals(remoteIp)
                        || existing.Preferred || !preferred))
                    {
                        // A new DNS alias can reach the same authenticated peer. Keep its active streams.
                        if (_saved.TryGetValue(secure.Id, out var activePeer) && activePeer.DirectAddress == null && directAddress != null)
                            SavePeer(secure.Id, activePeer with { DirectAddress = directAddress });
                        return false;
                    }
                    retired = existing;
                }
                else if (_connections.Count >= MaximumPeers)
                    throw new IOException("The Windows Link peer limit has been reached.");

                _saved.TryGetValue(secure.Id, out var remembered);
                var saved = new SavedPeer(secure.Id, secure.Hello.Name, remembered?.Alias,
                    new IPEndPoint(remoteIp, incoming ? secure.Hello.Port : endpoint.Port).ToString(), secure.Hello.Capabilities, true,
                    remembered?.Settings, remembered?.PinnedApps, remembered?.DisabledSmartVolumeApps, incoming,
                    secure.PairingVerified ? Guid.Empty : remembered?.FullTrustGrant ?? Guid.Empty,
                    directAddress ?? remembered?.DirectAddress, !secure.PairingVerified && remembered?.ClipboardEnabled == true,
                    secure.Hello.SupportsClipboardSync);
                resetTrust = HasTrustSessionState(retired) || remembered != null && remembered.FullTrustGrant != saved.FullTrustGrant;
                SavePeer(saved.Id, saved);
                _paused.Remove(secure.Id);
                _failures.Remove(secure.Id);
                run.Retrying.GetValueOrDefault(secure.Id)?.Signal();
                var connection = new LinkConnection(client, secure, preferred, remoteIp, run.Token);
                _connections[connection.Id] = connection;
                if (saved.ClipboardEnabled && saved.SupportsClipboardSync) SetClipboardConsentLocked(connection, true);
                if (secure.Hello.Capabilities.HasFlag(LinkCapabilities.FullTrust))
                    _ = SendTrustFrame(connection, LinkMessage.TrustState, Guid.Empty, saved.FullTrustGrant.ToByteArray());
                _ = run.Track(() => ServeAsync(run, connection));
                transferred = true;
            }
            retired?.Abort();
            NotifyClipboardState(secure.Id);
            if (resetTrust) NotifyFullTrust(secure.Id);
            Notify();
            return true;
        }
        finally { if (!transferred) secure.Stream.Dispose(); }
    }

    private async Task ServeAsync(LinkRun run, LinkConnection connection)
    {
        try
        {
            await connection.RunAsync(async (type, id, body) =>
            {
                if (type == LinkMessage.Bye)
                {
                    lock (_gate)
                    {
                        if (_connections.GetValueOrDefault(connection.Id) == connection)
                        {
                            _paused.Add(connection.Id);
                            _failures.Remove(connection.Id);
                            run.TrustRevision++;
                            StopRetryLocked(run, connection.Id);
                            if (_saved.TryGetValue(connection.Id, out var saved) && saved.AutoConnect)
                            {
                                try { SavePeer(connection.Id, saved with { AutoConnect = false }); }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.WriteLine(ex); }
                            }
                        }
                    }
                    connection.Abort();
                    return;
                }
                if (type is LinkMessage.ClipboardState or LinkMessage.ClipboardText)
                    DispatchClipboard(connection, type, id, body.Span);
                else if (type is >= LinkMessage.TrustState and <= LinkMessage.TrustRevoke)
                    HandleFullTrust(connection, type, id, body.Span);
                else if (type is >= LinkMessage.BrowseOpen and <= LinkMessage.BrowsePreparing)
                    DispatchFileBrowser(connection, type, id, body);
                else if (type is >= LinkMessage.FileOffer and <= LinkMessage.FileError or LinkMessage.FolderOffer)
                    DispatchFile(connection, type, id, body);
                else
                    await DispatchMediaAsync(connection, type, id, body).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException
            or ObjectDisposedException or JsonException or AuthenticationException or InvalidDataException) { }
        finally
        {
            bool removed, resetTrust;
            lock (_gate)
            {
                removed = _connections.GetValueOrDefault(connection.Id) == connection;
                resetTrust = removed && HasTrustSessionState(connection);
                CloseTrustExchange(connection);
                if (removed) _connections.Remove(connection.Id);
            }
            if (resetTrust) NotifyFullTrust(connection.Id);
            if (removed) NotifyClipboardState(connection.Id);
            CloseFileBrowsers(connection);
            CancelConnectionRequests(connection);
            await CloseConnectionFilesAsync(connection).ConfigureAwait(false);
            await CloseConnectionStreamsAsync(connection).ConfigureAwait(false);
            if (removed)
            {
                lock (_gate)
                {
                    if (_run == run) WakeDiscoveryLocked(run);
                }
                Notify();
                ScheduleRetry(run, connection.Id);
            }
        }
    }

    private bool Trusts(string id)
    {
        lock (_gate) return _saved.ContainsKey(id);
    }

    private async Task<bool> ConfirmAsync(string peerId, string label, string code, CancellationToken token)
    {
        var handler = PairingRequested;
        if (handler == null) return false;
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new LinkPairing(Guid.NewGuid(), label, code);
        lock (_gate)
        {
            if (_pairing.Count >= 2 || _pairing.Values.Any(p => p.PeerId == peerId)) return false;
            _pairing.Add(request.Id, (peerId, result));
        }
        try
        {
            handler(request);
            return await result.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _pairing.Remove(request.Id);
            PairingEnded?.Invoke(request.Id);
        }
    }

    public void AcceptPairing(Guid id, bool accepted)
    {
        lock (_gate) if (_pairing.TryGetValue(id, out var pending)) pending.Result.TrySetResult(accepted);
    }

    public async Task DisconnectAsync(string peerId)
    {
        LinkConnection? connection;
        bool resetTrust;
        lock (_gate)
        {
            if (_saved.TryGetValue(peerId, out var saved))
            {
                SavePeer(peerId, saved with { AutoConnect = false });
                _paused.Add(peerId);
            }
            resetTrust = HasTrustSessionState(_connections.GetValueOrDefault(peerId));
            connection = DetachPeerLocked(peerId);
        }
        if (resetTrust) NotifyFullTrust(peerId);
        Notify();
        if (connection != null)
        {
            NotifyClipboardState(peerId);
            await SendDisconnectAsync(connection).ConfigureAwait(false);
        }
    }

    private LinkConnection? DetachPeerLocked(string peerId)
    {
        if (_run is { } run)
        {
            run.TrustRevision++;
            StopRetryLocked(run, peerId);
            run.ManualConnections.GetValueOrDefault(peerId)?.Stop.Cancel();
            run.ReverseDials.Remove(peerId);
        }
        _failures.Remove(peerId);
        foreach (var request in _pairing.Values)
            if (string.Equals(request.PeerId, peerId, StringComparison.OrdinalIgnoreCase))
                request.Result.TrySetResult(false);
        _connections.Remove(peerId, out var connection);
        return connection;
    }

    private static async Task SendDisconnectAsync(LinkConnection connection)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await connection.SendAsync(LinkMessage.Bye, Guid.Empty, [], deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        finally { connection.Abort(); }
    }

    public void Remove(string peerId)
    {
        bool resetTrust;
        lock (_gate)
        {
            LoadSavedPeers();
            resetTrust = _saved.TryGetValue(peerId, out var saved) && saved.FullTrustGrant != Guid.Empty
                || HasTrustSessionState(_connections.GetValueOrDefault(peerId));
            SavePeer(peerId, null);
            _paused.Remove(peerId);
            _offers.Remove(peerId);
            var connection = DetachPeerLocked(peerId);
            if (_run is { } run)
            {
                run.OfferRetryWakeAt.Remove(peerId);
                if (connection != null) _ = run.Track(() => SendDisconnectAsync(connection));
                if (run.Lan != null) run.ProbeRetryAt = Stopwatch.GetTimestamp() + ClockTicks(0.5);
                RequestProbeLocked(run);
            }
        }
        if (resetTrust) NotifyFullTrust(peerId);
        NotifyClipboardState(peerId);
        Notify();
    }

    public void Rename(string peerId, string alias)
    {
        lock (_gate)
        {
            LoadSavedPeers();
            if (!_saved.TryGetValue(peerId, out var saved)) return;
            SavePeer(peerId, saved with { Alias = Label(alias, "") });
        }
        Notify();
    }

    public LinkPeerSettings GetPeerSettings(string peerId)
    {
        lock (_gate)
        {
            LoadSavedPeers();
            return (_saved.GetValueOrDefault(peerId)?.Settings ?? new LinkPeerSettings()).Normalize();
        }
    }

    public void UpdatePeerSettings(string peerId, LinkPeerSettings settings)
    {
        var normalized = settings.Normalize();
        List<Track>? revokeInputs = null;
        lock (_gate)
        {
            LoadSavedPeers();
            if (!_saved.TryGetValue(peerId, out var saved)) return;
            if (saved.Settings == normalized) return;
            SavePeer(peerId, saved with { Settings = normalized });
            if (!normalized.AllowRemoteInput && !IsFullyTrusted(peerId))
            {
                revokeInputs = _streams.Values
                    .Where(t => !t.Closing && !t.Info.Incoming && t.Info.Kind == LinkMediaKind.Window
                        && string.Equals(t.Info.PeerId, peerId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var track in revokeInputs)
                    track.Info = track.Info with { AllowsInput = false };
            }
        }
        if (revokeInputs != null)
        {
            foreach (var track in revokeInputs)
                track.DetachInput()?.Dispose();
        }
        Notify();
    }

    public LinkPinnedApp[] GetPinnedApps(string peerId)
    {
        lock (_gate)
        {
            LoadSavedPeers();
            return _saved.GetValueOrDefault(peerId)?.PinnedApps?.ToArray() ?? [];
        }
    }

    public void PinApp(string peerId, LinkRemoteApp app) => PinManyApps(peerId, [app]);

    public void PinManyApps(string peerId, IEnumerable<LinkRemoteApp> apps)
    {
        bool changed = false;
        lock (_gate)
        {
            LoadSavedPeers();
            if (!_saved.TryGetValue(peerId, out var saved)) return;
            var list = saved.PinnedApps?.ToList() ?? [];
            foreach (var app in apps)
            {
                if (string.IsNullOrWhiteSpace(app.Key) || app.IsSystemAudio) continue;
                string? iconB64 = EncodeIconBase64(app.IconPng);
                int index = list.FindIndex(item => string.Equals(item.Key, app.Key, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var existing = list[index];
                    var updated = existing with
                    {
                        Name = Label(app.Name, existing.Name),
                        ProcessName = Label(app.ProcessName, existing.ProcessName),
                        IconPngBase64 = iconB64 ?? existing.IconPngBase64
                    };
                    if (updated != existing) { list[index] = updated; changed = true; }
                }
                else if (list.Count < SavedLimit)
                {
                    list.Add(new LinkPinnedApp(app.Key.Trim(), Label(app.Name, app.Key), Label(app.ProcessName, app.Key), iconB64, SmartVolume: true));
                    changed = true;
                }
            }
            if (!changed) return;
            SavePeer(peerId, saved with { PinnedApps = list });
        }
        Notify();
    }

    public void UnpinApp(string peerId, string appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey)) return;
        lock (_gate)
        {
            LoadSavedPeers();
            if (!_saved.TryGetValue(peerId, out var saved) || saved.PinnedApps is not { Count: > 0 }) return;
            var list = saved.PinnedApps.ToList();
            if (list.RemoveAll(item => string.Equals(item.Key, appKey, StringComparison.OrdinalIgnoreCase)) == 0) return;
            SavePeer(peerId, saved with { PinnedApps = list });
        }
        Notify();
    }

    public void SetAppSmartVolume(string peerId, string appKey, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(appKey) || appKey.Length > 128) return;
        appKey = appKey.Trim();
        List<LinkAudioPlayback>? activeAudio = null;
        bool savedChanged = false;
        lock (_gate)
        {
            LoadSavedPeers();
            if (!_saved.TryGetValue(peerId, out var saved)) return;
            var disabled = new HashSet<string>(saved.DisabledSmartVolumeApps ?? [], StringComparer.OrdinalIgnoreCase);
            savedChanged = enabled ? disabled.Remove(appKey) : disabled.Add(appKey);
            if (savedChanged)
            {
                var pins = saved.PinnedApps?.Select(pin => string.Equals(pin.Key, appKey, StringComparison.OrdinalIgnoreCase)
                    ? pin with { SmartVolume = enabled } : pin).ToList();
                SavePeer(peerId, saved with { PinnedApps = pins, DisabledSmartVolumeApps = disabled.ToList() });
            }
            activeAudio = _streams.Values
                .Where(t => !t.Closing && t.Info.Incoming && t.Info.Kind != LinkMediaKind.Window
                    && string.Equals(t.Info.PeerId, peerId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Info.AppKey, appKey, StringComparison.OrdinalIgnoreCase)
                    && t.Audio != null)
                .Select(t => t.Audio!)
                .ToList();
        }
        if (activeAudio != null)
        {
            foreach (var audio in activeAudio) audio.SetSmartVolume(enabled);
        }
        if (savedChanged || activeAudio?.Count > 0) Notify();
    }

    public bool IsSmartVolumeEnabled(string peerId, string? appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey)) return true;
        lock (_gate)
        {
            LoadSavedPeers();
            return _saved.GetValueOrDefault(peerId)?.DisabledSmartVolumeApps?.Contains(appKey, StringComparer.OrdinalIgnoreCase) != true;
        }
    }

    public void UpdatePinnedMetadata(string peerId, IReadOnlyList<LinkRemoteApp> catalog)
    {
        if (catalog.Count == 0) return;
        bool changed = false;
        lock (_gate)
        {
            LoadSavedPeers();
            if (!_saved.TryGetValue(peerId, out var saved) || saved.PinnedApps is not { Count: > 0 }) return;
            var byKey = catalog.Where(a => !string.IsNullOrWhiteSpace(a.Key))
                .DistinctBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);
            var list = saved.PinnedApps.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                if (!byKey.TryGetValue(list[i].Key, out var remote)) continue;
                string? iconB64 = EncodeIconBase64(remote.IconPng) ?? list[i].IconPngBase64;
                var updated = list[i] with
                {
                    Name = Label(remote.Name, list[i].Name),
                    ProcessName = Label(remote.ProcessName, list[i].ProcessName),
                    IconPngBase64 = iconB64
                };
                if (updated != list[i]) { list[i] = updated; changed = true; }
            }
            if (!changed) return;
            SavePeer(peerId, saved with { PinnedApps = list });
        }
        Notify();
    }

    private static string? EncodeIconBase64(byte[]? png)
    {
        if (png is not { Length: > 0 and <= MaxIconBytes }) return null;
        string b64 = Convert.ToBase64String(png);
        return b64.Length <= MaxCatalogIconBase64Chars ? b64 : null;
    }

    // Called under the service gate. Publish the snapshot only after its atomic write succeeds.
    private void SavePeer(string id, SavedPeer? value)
    {
        if (_saved.GetValueOrDefault(id) == value && !_trustSavePending) return;
        var saved = new Dictionary<string, SavedPeer>(_saved, StringComparer.OrdinalIgnoreCase);
        if (value == null) saved.Remove(id);
        else
        {
            if (!saved.ContainsKey(id) && saved.Count >= SavedLimit)
                throw new IOException("Windows Link has 64 saved devices. Remove a device before pairing.");
            saved[id] = value;
        }
        AtomicFile.WriteAllText(_trustFile, JsonSerializer.Serialize(saved.Values));
        _saved = saved;
        _trustSavePending = false;
    }

    private void RetrySaved(LinkRun run)
    {
        string[] ids;
        lock (_gate) ids = _saved.Keys.ToArray();
        foreach (string id in ids) ScheduleRetry(run, id);
    }

    private void ScheduleRetry(LinkRun run, string id)
    {
        lock (_gate)
        {
            if (!ShouldRetryLocked(run, id) || run.Retrying.ContainsKey(id)) return;
            var retry = new RetryWork();
            run.Retrying.Add(id, retry);
            retry.Task = run.Track(() => ReconnectAsync(run, id, retry));
        }
    }

    private bool ShouldRetryLocked(LinkRun run, string id) =>
        _run == run && _saved.GetValueOrDefault(id)?.AutoConnect == true
        && !_paused.Contains(id) && !run.ManualConnections.ContainsKey(id)
        && _connections.GetValueOrDefault(id)?.IsOpen != true;

    private static Task? StopRetryLocked(LinkRun run, string id)
    {
        if (!run.Retrying.TryGetValue(id, out var retry)) return null;
        if (!retry.Stop.IsCancellationRequested) retry.Stop.Cancel();
        return retry.Task;
    }

    private async Task ReconnectAsync(LinkRun run, string id, RetryWork work)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Token, work.Stop.Token);
        var delay = TimeSpan.Zero;
        int attempts = 0;
        bool stopRetrying = false;
        try
        {
            while (!linked.IsCancellationRequested)
            {
                if (delay > TimeSpan.Zero)
                {
                    bool woken = await work.Wake.WaitAsync(delay, linked.Token).ConfigureAwait(false);
                    if (woken)
                    {
                        attempts = 0;
                        delay = TimeSpan.Zero;
                    }
                }
                lock (_gate)
                {
                    if (!ShouldRetryLocked(run, id)) { stopRetrying = true; return; }
                    work.Dialing = true;
                }
                Notify();
                Exception? failure;
                try { failure = await TryCandidatesAsync(run, id, [], permitPairing: false, linked.Token).ConfigureAwait(false); }
                catch (InvalidOperationException) when (!IsConnected(id)) { stopRetrying = true; return; }
                finally
                {
                    lock (_gate) work.Dialing = false;
                    Notify();
                }
                if (IsConnected(id)) { stopRetrying = true; return; }
                if (failure != null && !linked.IsCancellationRequested)
                {
                    var reason = ClassifyFailure(failure);
                    SetConnectionFailure(run, id, reason, failure);
                    if (reason is LinkConnectFailure.PairingRejected or LinkConnectFailure.IdentityMismatch or LinkConnectFailure.Incompatible)
                    {
                        stopRetrying = true;
                        return;
                    }
                }

                attempts++;
                lock (_gate)
                {
                    bool hasFreshOffer = FreshOffersLocked(id).Any();
                    if (run.Lan != null && ((!hasFreshOffer && attempts >= 4) || (hasFreshOffer && attempts >= 8)))
                    {
                        stopRetrying = true;
                        WakeDiscoveryLocked(run);
                        return;
                    }
                }

                double next = delay.TotalSeconds switch
                {
                    0 => 1,
                    1 => 3.5,
                    3.5 => 8.5,
                    8.5 => 20,
                    20 => 45,
                    _ => 90
                };
                delay = TimeSpan.FromSeconds(next);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                if (run.Retrying.GetValueOrDefault(id) == work) run.Retrying.Remove(id);
                work.Dispose();
            }
            if (!stopRetrying && !work.Stop.IsCancellationRequested && !run.Token.IsCancellationRequested)
                ScheduleRetry(run, id);
        }
    }
}

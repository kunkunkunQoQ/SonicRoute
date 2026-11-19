using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using SonicRoute.Core;

namespace SonicRoute.WindowsLink;

internal sealed partial class WindowsLinkService
{
    public Task ConnectAsync(string address, string? expectedPeerId = null, CancellationToken token = default)
    {
        if (!TryAddress(address, out var endpoint))
            throw new ArgumentException("Enter an IP address with an optional TCP port.", nameof(address));
        if (expectedPeerId != null && !LinkSecurity.IsId(expectedPeerId))
            throw new ArgumentException("Invalid Windows Link peer identity.", nameof(expectedPeerId));
        LinkRun run = GetRun();
        lock (_gate)
        {
            run.Dialing[endpoint!] = run.Dialing.GetValueOrDefault(endpoint!) + 1;
            return run.Track(async () =>
            {
                try { await DialAsync(run, endpoint!, expectedPeerId, permitPairing: true, token).ConfigureAwait(false); }
                catch (LinkDuplicatePairingException) { }
                finally
                {
                    lock (_gate)
                    {
                        if (run.Dialing[endpoint!] == 1) run.Dialing.Remove(endpoint!);
                        else run.Dialing[endpoint!]--;
                    }
                }
            });
        }
    }

    public async Task ReconnectPeerAsync(string peerId, CancellationToken token = default)
    {
        await EnableAsync(token).ConfigureAwait(false);
        LinkRun run = GetRun();
        await run.Track(() => ReconnectPeerAsync(run, peerId, token)).ConfigureAwait(false);
    }

    private async Task ReconnectPeerAsync(LinkRun run, string peerId, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Token, token);
        CancellationToken active = linked.Token;
        string savedAddress;
        lock (_gate)
        {
            if (!_saved.TryGetValue(peerId, out var saved))
                throw new InvalidOperationException("This Windows Link device is no longer paired.");
            savedAddress = saved.Address;
        }

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerOffer = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialOffer = GetFreshOffer(peerId);
        void Observe()
        {
            lock (_gate)
                if (_connections.GetValueOrDefault(peerId)?.IsOpen == true) connected.TrySetResult();
            if (GetFreshOffer(peerId) is { } endpoint && !endpoint.Equals(initialOffer))
                newerOffer.TrySetResult(endpoint);
        }

        Exception? failure = null;
        var tried = new HashSet<IPEndPoint>();
        async Task<bool> TryDialAsync(IPEndPoint? endpoint)
        {
            if (connected.Task.IsCompleted) return true;
            if (endpoint == null || !tried.Add(endpoint)) return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(active);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            // An inbound connection can complete while the direct TCP attempt is still blocked.
            Task attempt = DialAsync(run, endpoint, peerId, permitPairing: false, deadline.Token);
            try
            {
                if (await Task.WhenAny(attempt, connected.Task).ConfigureAwait(false) == connected.Task)
                    await deadline.CancelAsync().ConfigureAwait(false);
                await attempt.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or SocketException
                or JsonException || ex is OperationCanceledException && !active.IsCancellationRequested)
            {
                failure = ex;
            }
            Observe();
            return connected.Task.IsCompleted;
        }

        Changed += Observe;
        try
        {
            Observe();
            TryAddress(savedAddress, out var savedEndpoint);
            if (await TryDialAsync(initialOffer ?? savedEndpoint).ConfigureAwait(false)) return;
            if (run.Lan != null)
            {
                await SendDiscoveryAsync(run).ConfigureAwait(false);
                if (!newerOffer.Task.IsCompleted && !connected.Task.IsCompleted)
                    await Task.WhenAny(newerOffer.Task, connected.Task, Task.Delay(TimeSpan.FromSeconds(1), active)).ConfigureAwait(false);
                active.ThrowIfCancellationRequested();
                if (await TryDialAsync(GetFreshOffer(peerId)).ConfigureAwait(false)) return;
            }
            if (await TryDialAsync(savedEndpoint).ConfigureAwait(false)) return;
            if (run.Lan != null && _identity is { } identity)
            {
                byte[] request = LinkSecurity.CreateReconnectRequest(identity, peerId, run.Port);
                foreach (var address in tried.Select(endpoint => endpoint.Address).Distinct())
                    await run.Lan.RequestReconnectAsync(request, address, active).ConfigureAwait(false);
                try
                {
                    await connected.Task.WaitAsync(TimeSpan.FromSeconds(6), active).ConfigureAwait(false);
                    return;
                }
                catch (TimeoutException) { }
            }
            Observe();
            if (connected.Task.IsCompleted) return;
            throw new IOException("Neither computer completed the Windows Link connection.", failure);
        }
        finally { Changed -= Observe; }
    }

    private IPEndPoint? GetFreshOffer(string peerId)
    {
        lock (_gate)
            return _offers.TryGetValue(peerId, out var entry) && Stopwatch.GetElapsedTime(entry.At) < TimeSpan.FromMinutes(2)
                ? entry.Offer.Endpoint : null;
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

    private async Task DialAsync(LinkRun run, IPEndPoint endpoint, string? expected, bool permitPairing, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Token, token);
        var client = new TcpClient(endpoint.AddressFamily);
        try
        {
            using (var dial = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
            {
                dial.CancelAfter(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(endpoint, dial.Token).ConfigureAwait(false);
            }
            if (!await EstablishAsync(run, client, incoming: false, expected, permitPairing, linked.Token).ConfigureAwait(false))
                client.Dispose();
        }
        catch { client.Dispose(); throw; }
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
        bool permitPairing, CancellationToken token)
    {
        client.NoDelay = true;
        client.SendBufferSize = 64 * 1024;
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
                    if (_pairing.Values.Any(p => p.PeerId == id)) return false;
                    return !incoming || string.CompareOrdinal(localId, id) > 0
                        || !run.Dialing.ContainsKey(new IPEndPoint(remoteIp, hello.Port));
                }
            }, permitPairing ? ConfirmAsync : (_, _, _, _) => Task.FromResult(false), token).ConfigureAwait(false);
        bool transferred = false;
        try
        {
            bool preferred = incoming == (string.CompareOrdinal(localId, secure.Id) > 0);
            LinkConnection? retired = null;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_run != run || !permitPairing && !_saved.ContainsKey(secure.Id)) return false;
                if (_connections.TryGetValue(secure.Id, out var existing))
                {
                    if (existing.IsOpen && (existing.Preferred || !preferred || !existing.RecentlyOpened))
                        return false;
                    retired = existing;
                }
                else if (_connections.Count >= MaximumPeers)
                    throw new IOException("The Windows Link peer limit has been reached.");

                _saved.TryGetValue(secure.Id, out var remembered);
                var saved = new SavedPeer(secure.Id, secure.Hello.Name, remembered?.Alias,
                    new IPEndPoint(remoteIp, secure.Hello.Port).ToString(), secure.Hello.Capabilities, true,
                    remembered?.Settings, remembered?.PinnedApps);
                SavePeer(saved.Id, saved);
                _paused.Remove(secure.Id);
                var connection = new LinkConnection(client, secure, preferred, run.Token);
                _connections[connection.Id] = connection;
                _ = run.Track(() => ServeAsync(run, connection));
                transferred = true;
            }
            retired?.Abort();
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
                        if (_connections.GetValueOrDefault(connection.Id) == connection) _paused.Add(connection.Id);
                    connection.Abort();
                    return;
                }
                await DispatchMediaAsync(connection, type, id, body).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException
            or ObjectDisposedException or JsonException or AuthenticationException or InvalidDataException) { }
        finally
        {
            bool removed;
            lock (_gate)
            {
                removed = _connections.GetValueOrDefault(connection.Id) == connection;
                if (removed) _connections.Remove(connection.Id);
            }
            CancelConnectionRequests(connection);
            await CloseConnectionStreamsAsync(connection).ConfigureAwait(false);
            if (removed) { Notify(); ScheduleRetry(run, connection.Id); }
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
        lock (_gate)
        {
            if (_saved.TryGetValue(peerId, out var saved)) SavePeer(peerId, saved with { AutoConnect = false });
            _paused.Add(peerId);
            _connections.TryGetValue(peerId, out connection);
        }
        if (connection == null) return;
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
        lock (_gate)
        {
            SavePeer(peerId, null);
            _offers.Remove(peerId);
            _run?.ReverseDials.Remove(peerId);
        }
        _ = DisconnectAsync(peerId);
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
            if (!normalized.AllowRemoteInput)
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
        if (string.IsNullOrWhiteSpace(appKey)) return;
        List<LinkAudioPlayback>? activeAudio = null;
        bool savedChanged = false;
        lock (_gate)
        {
            LoadSavedPeers();
            if (!_saved.TryGetValue(peerId, out var saved)) return;
            var list = saved.PinnedApps?.ToList() ?? [];
            int index = list.FindIndex(item => string.Equals(item.Key, appKey, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && list[index].SmartVolume != enabled)
            {
                list[index] = list[index] with { SmartVolume = enabled };
                SavePeer(peerId, saved with { PinnedApps = list });
                savedChanged = true;
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
            var pinned = _saved.GetValueOrDefault(peerId)?.PinnedApps;
            var match = pinned?.FirstOrDefault(item => string.Equals(item.Key, appKey, StringComparison.OrdinalIgnoreCase));
            return match?.SmartVolume ?? true;
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
        if (_saved.GetValueOrDefault(id) == value) return;
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
            if (_run != run || _saved.GetValueOrDefault(id)?.AutoConnect != true || _paused.Contains(id)
                || _connections.GetValueOrDefault(id)?.IsOpen == true || !run.Retrying.Add(id)) return;
            _ = run.Track(() => ReconnectAsync(run, id));
        }
    }

    private async Task ReconnectAsync(LinkRun run, string id)
    {
        try
        {
            foreach (int delay in new[] { 0, 1000, 3500, 8500 })
            {
                if (delay != 0) await Task.Delay(delay, run.Token).ConfigureAwait(false);
                string address;
                lock (_gate)
                {
                    if (_run != run || !_saved.TryGetValue(id, out var saved) || !saved.AutoConnect
                        || _paused.Contains(id) || _connections.GetValueOrDefault(id)?.IsOpen == true) return;
                    address = _offers.GetValueOrDefault(id).Offer?.Endpoint.ToString() ?? saved.Address;
                }
                if (!TryAddress(address, out var endpoint)) return;
                try { await DialAsync(run, endpoint!, id, permitPairing: false, run.Token).ConfigureAwait(false); return; }
                catch (Exception ex) when (ex is IOException or AuthenticationException or SocketException or OperationCanceledException) { }
            }
        }
        finally { lock (_gate) run.Retrying.Remove(id); }
    }
}

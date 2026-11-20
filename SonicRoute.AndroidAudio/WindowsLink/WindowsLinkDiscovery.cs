using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SonicRoute.WindowsLink;

internal sealed partial class WindowsLinkService
{
    private const int MaxAddressesPerPeer = 4;
    private static readonly long SearchWindow = ClockTicks(1.25);
    private static readonly long ProbeGap = ClockTicks(0.25);
    private int _discoveryObservers;

    private sealed record TimedOffer(LinkOffer Offer, long ExpiresAt);

    public Task DiscoverAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
            RequestProbeLocked(GetRun());
        return Task.CompletedTask;
    }

    public async Task SearchAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        LinkRun run;
        TaskCompletionSource search;
        lock (_gate)
        {
            run = GetRun();
            if (run.Lan == null) return;
            if (run.Search == null)
            {
                long now = Stopwatch.GetTimestamp();
                run.Search = new(TaskCreationOptions.RunContinuationsAsynchronously);
                run.SearchEndsAt = now + SearchWindow;
                run.SearchRetryAt = now + ClockTicks(0.5);
                RequestProbeLocked(run);
            }
            run.SearchWaiters++;
            search = run.Search;
        }
        try { await search.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(run.Search, search) && --run.SearchWaiters == 0)
                {
                    run.Search = null;
                    run.SearchRetryAt = 0;
                    search.TrySetCanceled();
                    if (_run == run) WakeDiscoveryLocked(run);
                }
            }
        }
    }

    public IDisposable ObserveDiscovery()
    {
        lock (_gate)
        {
            if (_discoveryObservers++ == 0 && _run is { } run)
                RequestProbeLocked(run);
        }
        return new DiscoveryObserver(this);
    }

    private sealed class DiscoveryObserver(WindowsLinkService owner) : IDisposable
    {
        private WindowsLinkService? _owner = owner;

        public void Dispose()
        {
            var service = Interlocked.Exchange(ref _owner, null);
            if (service == null) return;
            lock (service._gate)
                if (--service._discoveryObservers == 0 && service._run is { } run)
                    WakeDiscoveryLocked(run);
        }
    }

    public void OnNetworkResume()
    {
        lock (_gate)
        {
            if (_run is not { Lan: not null } run) return;
            run.NetworkRefreshAt = Stopwatch.GetTimestamp() + ClockTicks(0.65);
            WakeDiscoveryLocked(run);
        }
    }

    private void NetworkChanged(object? sender, EventArgs e) => OnNetworkResume();
    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => OnNetworkResume();

    private bool HasDisconnectedAutoConnectPeerLocked(LinkRun run) =>
        _saved.Values.Any(p => p.AutoConnect && !_paused.Contains(p.Id)
            && _connections.GetValueOrDefault(p.Id)?.IsOpen != true);

    private static void RequestProbeLocked(LinkRun run)
    {
        run.ProbeRequested = true;
        WakeDiscoveryLocked(run);
    }

    private static void WakeDiscoveryLocked(LinkRun run)
    {
        if (run.DiscoveryWake.CurrentCount == 0) run.DiscoveryWake.Release();
    }

    private async Task DiscoveryLoopAsync(LinkRun run)
    {
        try
        {
            while (!run.Token.IsCancellationRequested)
            {
                long now = Stopwatch.GetTimestamp();
                long deadline;
                bool send, refresh, expired;
                lock (_gate)
                {
                    if (_run != run) return;
                    expired = ExpireOffersLocked(now, out deadline);
                    if (run.ProbeRetryAt != 0)
                    {
                        if (now >= run.ProbeRetryAt)
                        {
                            run.ProbeRetryAt = 0;
                            run.ProbeRequested = true;
                        }
                        else deadline = Math.Min(deadline, run.ProbeRetryAt);
                    }
                    if (run.Search is { } search)
                    {
                        if (now >= run.SearchEndsAt)
                        {
                            run.Search = null;
                            run.SearchRetryAt = 0;
                            run.SearchWaiters = 0;
                            search.TrySetResult();
                        }
                        else
                        {
                            deadline = Math.Min(deadline, run.SearchEndsAt);
                            if (run.SearchRetryAt != 0)
                            {
                                if (now >= run.SearchRetryAt)
                                {
                                    run.SearchRetryAt = 0;
                                    run.ProbeRequested = true;
                                }
                                else deadline = Math.Min(deadline, run.SearchRetryAt);
                            }
                        }
                    }

                    refresh = run.NetworkRefreshAt != 0 && now >= run.NetworkRefreshAt;
                    if (refresh) run.NetworkRefreshAt = 0;
                    else if (run.NetworkRefreshAt != 0) deadline = Math.Min(deadline, run.NetworkRefreshAt);

                    bool needsPeriodicProbe = _discoveryObservers > 0 || HasDisconnectedAutoConnectPeerLocked(run);
                    send = false;
                    if (run.Lan != null)
                    {
                        if (refresh || run.LastProbe == 0)
                            send = true;
                        else if (run.ProbeRequested)
                        {
                            long nextProbe = run.LastProbe + ProbeGap;
                            if (now >= nextProbe) send = true;
                            else deadline = Math.Min(deadline, nextProbe);
                        }
                        else if (needsPeriodicProbe)
                        {
                            long nextProbe = run.LastProbe + ClockTicks(_discoveryObservers > 0 ? 20 : 60);
                            if (now >= nextProbe) send = true;
                            else deadline = Math.Min(deadline, nextProbe);
                        }
                    }

                    if (send)
                    {
                        run.LastProbe = now;
                        run.ProbeRequested = false;
                    }
                }

                if (expired) Notify();
                if (refresh) RefreshNetwork(run);
                if (send)
                {
                    await run.Lan!.DiscoverAsync(run.Token).ConfigureAwait(false);
                    continue;
                }

                var delay = deadline == long.MaxValue ? Timeout.InfiniteTimeSpan
                    : TimeSpan.FromSeconds(Math.Max(0.001, (deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency));
                await run.DiscoveryWake.WaitAsync(delay, run.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (run.Token.IsCancellationRequested) { }
    }

    private void RefreshNetwork(LinkRun run)
    {
        IPAddress? address;
        try { address = run.Lan!.RefreshInterfaces(); }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException)
        {
            Report(ex);
            return;
        }

        bool changed;
        LinkConnection[] activeLinks;
        lock (_gate)
        {
            if (_run != run) return;
            string next = DisplayAddress(discoveryBind?.Address ?? address, run.Port);
            changed = run.Address != next;
            run.Address = next;
            foreach (var entry in _offers.ToArray())
            {
                if (entry.Value.RemoveAll(item => run.Lan.RankAddress(item.Offer.Endpoint.Address) == 0) > 0)
                    changed = true;
                if (entry.Value.Count == 0) _offers.Remove(entry.Key);
            }
            foreach (var retry in run.Retrying.Values) retry.Signal();
            activeLinks = _connections.Values.Where(c => c.IsOpen).ToArray();
        }
        foreach (var link in activeLinks) link.ProbeHealth();
        if (changed) Notify();
        RetrySaved(run);
    }

    private void Found(LinkRun run, LinkOffer offer) => FoundOffer(run, offer, isQuery: false);

    private void FoundOffer(LinkRun run, LinkOffer offer, bool isQuery)
    {
        bool changed, newAddress, shouldWakeRetry;
        lock (_gate)
        {
            if (_run != run) return;
            long now = Stopwatch.GetTimestamp();
            _saved.TryGetValue(offer.Id, out var saved);
            var previous = BestOfferLocked(offer.Id, saved);
            if (!_offers.TryGetValue(offer.Id, out var addresses))
            {
                if (_offers.Count >= SavedLimit)
                {
                    string evicted = _offers.MinBy(entry => entry.Value.Max(item => item.ExpiresAt)).Key;
                    _offers.Remove(evicted);
                    if (!_saved.ContainsKey(evicted)) _failures.Remove(evicted);
                }
                _offers.Add(offer.Id, addresses = []);
            }
            int index = addresses.FindIndex(item => item.Offer.Endpoint.Equals(offer.Endpoint));
            newAddress = index < 0 || addresses[index].ExpiresAt <= now;
            var item = new TimedOffer(offer, now + _offerLifetime);
            if (index >= 0) addresses[index] = item;
            else
            {
                addresses.Add(item);
                if (addresses.Count > MaxAddressesPerPeer)
                    addresses.Remove(addresses.OrderBy(candidate => AddressRankLocked(candidate.Offer.Endpoint))
                        .ThenBy(candidate => candidate.ExpiresAt).First());
            }
            bool clearedFailure = _failures.TryGetValue(offer.Id, out var prevFailure)
                && prevFailure is LinkConnectFailure.SavedAddressUnavailable or LinkConnectFailure.DiscoveryTimeout
                && _failures.Remove(offer.Id);
            changed = clearedFailure || !Equals(previous, BestOfferLocked(offer.Id, saved));
            shouldWakeRetry = newAddress;
            if (!shouldWakeRetry && ShouldRetryLocked(run, offer.Id))
            {
                run.OfferRetryWakeAt.TryGetValue(offer.Id, out long lastWake);
                if (isQuery || lastWake == 0 || Stopwatch.GetElapsedTime(lastWake) >= TimeSpan.FromSeconds(5))
                {
                    run.OfferRetryWakeAt[offer.Id] = now;
                    shouldWakeRetry = true;
                }
            }
            if (shouldWakeRetry) run.Retrying.GetValueOrDefault(offer.Id)?.Signal();
            if (newAddress) WakeDiscoveryLocked(run);
        }
        if (changed) Notify();
        if (shouldWakeRetry) ScheduleRetry(run, offer.Id);
    }

    private bool ExpireOffersLocked(long now, out long deadline)
    {
        deadline = long.MaxValue;
        bool expired = false;
        foreach (var entry in _offers.ToArray())
        {
            if (entry.Value.RemoveAll(item => item.ExpiresAt <= now) > 0) expired = true;
            if (entry.Value.Count == 0)
            {
                _offers.Remove(entry.Key);
                if (!_saved.ContainsKey(entry.Key)) _failures.Remove(entry.Key);
            }
            else deadline = Math.Min(deadline, entry.Value.Min(item => item.ExpiresAt));
        }
        return expired;
    }

    // The device row prefers its last authenticated address only while that address is still advertised.
    private LinkOffer? BestOfferLocked(string id, SavedPeer? saved) =>
        FreshOffersLocked(id)
            .OrderByDescending(item => string.Equals(item.Offer.Endpoint.ToString(), saved?.Address, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => AddressRankLocked(item.Offer.Endpoint))
            .ThenBy(item => item.Offer.Endpoint.ToString(), StringComparer.Ordinal)
            .Select(item => item.Offer).FirstOrDefault();

    private IEnumerable<TimedOffer> FreshOffersLocked(string id)
    {
        long now = Stopwatch.GetTimestamp();
        return _offers.TryGetValue(id, out var addresses) ? addresses.Where(item => item.ExpiresAt > now) : [];
    }

    private IPEndPoint[] ConnectionCandidatesLocked(string id, SavedPeer saved)
    {
        TryAddress(saved.Address, out var known);
        var candidates = FreshOffersLocked(id)
            .OrderByDescending(item => AddressRankLocked(item.Offer.Endpoint))
            .ThenByDescending(item => Equals(item.Offer.Endpoint, known))
            .ThenByDescending(item => item.ExpiresAt)
            .Select(item => item.Offer.Endpoint);
        return (known == null ? candidates : candidates.Append(known)).Distinct().ToArray();
    }

    private int AddressRankLocked(IPEndPoint endpoint) => _run?.Lan?.RankAddress(endpoint.Address) ?? 0;

    private static long ClockTicks(double seconds) => (long)(Stopwatch.Frequency * seconds);
    private static string DisplayAddress(IPAddress? address, int port) => address == null ? "" : new IPEndPoint(address, port).ToString();
}

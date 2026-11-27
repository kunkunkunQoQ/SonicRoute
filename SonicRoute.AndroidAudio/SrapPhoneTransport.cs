using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Channels;

namespace SonicRoute.AndroidAudio;

/// <summary>Owns the App transport's authenticated attachments, never device preferences or ADB authority.</summary>
internal sealed class SrapPhoneTransport : IAsyncDisposable
{
    private sealed record Attachment(AndroidDevice Device, string Fingerprint, SrapSession Session);
    private readonly AndroidPhoneStore store;
    private readonly Lazy<SrapIdentity> identity;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken token;
    private readonly SemaphoreSlim dialing = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<string, Attachment> online = new(StringComparer.Ordinal);
    private readonly HashSet<(string Id, string Fingerprint)> revoked = [];
    private readonly HashSet<Task> monitors = [];
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(1);
    private CancellationTokenSource? browsing;
    private Task? worker;
    private Task? browser;
    private bool started;
    private bool closed;
    internal event Action? Changed;

    internal SrapPhoneTransport(AndroidPhoneStore store, string directory)
    {
        this.store = store;
        identity = new(() => new(directory));
        token = lifetime.Token;
    }

    internal void Start()
    {
        lock (gate)
        {
            if (closed || started) return;
            started = true;
            store.Changed += StoreChanged;
        }
        StoreChanged();
    }

    internal AndroidDevice? Resolve(AndroidPhoneProfile profile)
    {
        if (profile.AppPublicKeySha256 is not { } pin || !store.HasUniqueAppOwner(profile.Id, pin)) return null;
        lock (gate)
            return !profile.ManuallyDisconnected && !profile.AppTrustRevoked && online.TryGetValue(profile.Id, out var attachment)
                && profile.AppPublicKeySha256 == attachment.Fingerprint && !attachment.Session.Token.IsCancellationRequested
                ? attachment.Device : null;
    }

    internal SrapSession? Session(AndroidDevice device)
    {
        var profile = store.FindProfile(device);
        if (profile == null || profile.ManuallyDisconnected || profile.AppTrustRevoked || !store.HasUniqueAppOwner(profile.Id, device.Serial)) return null;
        lock (gate)
            return online.TryGetValue(profile.Id, out var live) && ReferenceEquals(live.Device, device)
                && !live.Session.Token.IsCancellationRequested ? live.Session : null;
    }

    internal static Task<IReadOnlyList<AndroidMdnsService>> DiscoverAsync(CancellationToken token) =>
        AndroidMdnsDiscovery.DiscoverAppsAsync(token);

    internal async Task<AndroidDevice> PairAsync(string text, IPEndPoint? selected, Action? pending, CancellationToken cancel, string? displayName = null)
    {
        using var invite = SrapInvite.Parse(text);
        using var effective = CancellationTokenSource.CreateLinkedTokenSource(cancel, token);
        await dialing.WaitAsync(effective.Token).ConfigureAwait(false);
        try
        {
            long revision = store.ManagementRevision;
            var endpoint = invite.Endpoint ?? selected;
            if (endpoint == null)
            {
                var candidates = (await DiscoverAsync(effective.Token).ConfigureAwait(false))
                    .Where(s => s.App?.Pairing == true && IPEndPoint.TryParse(s.Address, out _)).DistinctBy(s => s.App!.Instance).ToArray();
                if (candidates.Length != 1) throw new SrapException(candidates.Length == 0 ? "unavailable" : "busy");
                endpoint = IPEndPoint.Parse(candidates[0].Address);
            }
            var client = new SrapClient(identity.Value);
            var session = await client.PairAsync(endpoint, invite, displayName ?? "SonicRoute · " + Environment.MachineName, pending, effective.Token).ConfigureAwait(false);
            try
            {
                effective.Token.ThrowIfCancellationRequested();
                var profile = store.RegisterApp(invite.Fingerprint, endpoint, revision);
                lock (gate) revoked.Remove((profile.Id, invite.Fingerprint));
                return Admit(profile, session);
            }
            catch { session.Dispose(); throw; }
        }
        finally { dialing.Release(); }
    }

    internal async Task<AndroidDevice> ReconnectAsync(AndroidPhoneProfile profile, CancellationToken cancel)
    {
        using var effective = CancellationTokenSource.CreateLinkedTokenSource(cancel, token);
        await dialing.WaitAsync(effective.Token).ConfigureAwait(false);
        try
        {
            var current = store.Profiles.FirstOrDefault(p => p.Id == profile.Id);
            if (current?.LastDevice == null || current.AppTrustRevoked || current.AppPublicKeySha256 == null
                || !store.HasUniqueAppOwner(current.Id, current.AppPublicKeySha256))
                throw new SrapException("not_paired");
            if (Resolve(current) is { } attached) return attached;
            long revision = store.ManagementRevision;
            var (session, endpoint) = await DialAsync(current, null, effective.Token).ConfigureAwait(false);
            try
            {
                effective.Token.ThrowIfCancellationRequested();
                if (store.ManagementRevision != revision) throw new SrapException("stale_session");
                if (current.ManuallyDisconnected) store.SetDisconnected(current.LastDevice, false);
                var active = store.FindProfile(current.LastDevice) ?? throw new SrapException("stale_session");
                var device = Admit(active, session);
                store.RememberAppAddress(active.Id, active.AppPublicKeySha256!, endpoint);
                return device;
            }
            catch { session.Dispose(); throw; }
        }
        finally { dialing.Release(); }
    }

    internal void Close(string id)
    {
        Attachment? attachment;
        lock (gate) online.Remove(id, out attachment);
        if (attachment == null) return;
        attachment.Session.Dispose();
        Changed?.Invoke();
    }

    internal void RevokeTrust(string id, string fingerprint)
    {
        lock (gate) revoked.Add((id, fingerprint));
        Close(id);
        store.RevokeAppTrust(id, fingerprint);
    }

    internal Task BindAliasAsync(SrapSession session, string pin, string profileId, SrapAdbAttachment attachment,
        Action validate, Action<Action> commit, CancellationToken cancel) =>
        session.BindAdbAliasAsync(identity.Value, pin, profileId, attachment.OpenForwardAsync, validate, commit, cancel);

    internal AndroidDevice Admit(AndroidPhoneProfile profile, SrapSession session)
    {
        var current = store.Profiles.FirstOrDefault(p => p.Id == profile.Id);
        if (current?.LastDevice == null || current.AppPublicKeySha256 is not { } pin || pin != profile.AppPublicKeySha256
            || pin != session.PeerFingerprint || !store.HasUniqueAppOwner(current.Id, pin)
            || current.AppTrustRevoked || current.ManuallyDisconnected || session.Token.IsCancellationRequested)
            throw new SrapException("stale_session");
        var device = new AndroidDevice(pin, current.LastDevice.Name, "device", true)
        {
            IsApp = true, ConnectionTarget = Guid.NewGuid().ToString("N"),
            BoundAdbKey = current.LastDevice.IsApp ? null : AndroidDeviceIdentity.Key(current.LastDevice)
        };
        var attachment = new Attachment(device, current.AppPublicKeySha256!, session);
        Attachment? prior;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (revoked.Contains((current.Id, attachment.Fingerprint))) throw new SrapException("not_paired");
            online.Remove(current.Id, out prior);
            online.Add(current.Id, attachment);
            session.CapabilitiesChanged += OnCapabilities;
            monitors.RemoveWhere(t => t.IsCompleted);
            monitors.Add(Task.Run(() => MonitorAsync(current.Id, attachment)));
        }
        prior?.Session.Dispose();
        // An operation racing admission can only use the attachment if the current
        // persisted profile also admits it. Close a retired attachment immediately.
        if (store.FindProfile(device) is not { ManuallyDisconnected: false, AppTrustRevoked: false } saved
            || Resolve(saved) != device)
        {
            session.Dispose();
            throw new SrapException("stale_session");
        }
        Changed?.Invoke();
        return device;
    }

    private void OnCapabilities() => Changed?.Invoke();

    private async Task MonitorAsync(string id, Attachment attachment)
    {
        try
        {
            string? error = await attachment.Session.Completion.ConfigureAwait(false);
            lock (gate)
            {
                if (!online.TryGetValue(id, out var current) || !ReferenceEquals(current, attachment)) return;
                online.Remove(id);
            }
            if (error == "permission_lost")
            {
                try { RevokeTrust(id, attachment.Fingerprint); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.WriteLine("Failed to persist revoked SRAP trust."); }
            }
            Changed?.Invoke();
            Signal();
        }
        finally { attachment.Session.CapabilitiesChanged -= OnCapabilities; }
    }

    private async Task<(SrapSession Session, IPEndPoint Endpoint)> DialAsync(AndroidPhoneProfile profile,
        Func<Task<IReadOnlyList<AndroidMdnsService>>>? discovery, CancellationToken cancel)
    {
        string fingerprint = profile.AppPublicKeySha256!;
        lock (gate)
            if (revoked.Contains((profile.Id, fingerprint))) throw new SrapException("not_paired");
        var client = new SrapClient(identity.Value);
        IPEndPoint? last = profile.LastAppAddress == null ? null : IPEndPoint.Parse(profile.LastAppAddress);
        if (last?.Port > 0 && await TryEndpoint(last).ConfigureAwait(false) is { } direct) return (direct, last);
        var candidates = await (discovery?.Invoke() ?? DiscoverAsync(cancel)).ConfigureAwait(false);
        foreach (var endpoint in candidates.Take(16).Select(c => IPEndPoint.TryParse(c.Address, out var address) ? address : null)
            .OfType<IPEndPoint>().Where(e => !e.Equals(last)).Distinct().Take(8))
            if (await TryEndpoint(endpoint).ConfigureAwait(false) is { } session) return (session, endpoint);
        throw new SrapException("unavailable");

        async Task<SrapSession?> TryEndpoint(IPEndPoint endpoint)
        {
            try { return await client.ConnectAsync(endpoint, fingerprint, cancel).ConfigureAwait(false); }
            catch (SrapException error) when (error.Code == "not_paired" || error.Code == "permission_lost" && error.PeerRevoked)
            {
                RevokeTrust(profile.Id, fingerprint); // only after pin-verified TLS and an explicit session error
                throw;
            }
            catch (Exception error) when (!cancel.IsCancellationRequested
                && error is SocketException or IOException or AuthenticationException or OperationCanceledException)
            { return null; }
        }
    }

    private bool Eligible(AndroidPhoneProfile profile) => profile.LastDevice != null && profile.AppPublicKeySha256 is { } pin
        && !profile.ManuallyDisconnected && !profile.AppTrustRevoked && store.HasUniqueAppOwner(profile.Id, pin);

    private void StoreChanged()
    {
        var trusted = store.Profiles.Where(Eligible).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        string[] retire;
        lock (gate)
        {
            if (closed || !started) return;
            retire = online.Keys.Where(id => !trusted.Contains(id)).ToArray();
            if (trusted.Count != 0 && browsing == null)
            {
                browsing = CancellationTokenSource.CreateLinkedTokenSource(token);
                var browseToken = browsing.Token;
                browser = Task.Run(() => BrowseAsync(browseToken));
                worker ??= Task.Run(RestoreAsync);
                NetworkChange.NetworkAddressChanged += NetworkChanged;
            }
            else if (trusted.Count == 0 && browsing != null)
            {
                NetworkChange.NetworkAddressChanged -= NetworkChanged;
                browsing.Cancel();
                browsing.Dispose();
                browsing = null;
            }
        }
        foreach (string id in retire) Close(id);
        Signal();
    }

    private void NetworkChanged(object? sender, EventArgs e) => Signal();
    private void Signal() => wake.Writer.TryWrite(true);

    private async Task BrowseAsync(CancellationToken cancel)
    {
        try
        {
            await foreach (var _ in AndroidMdnsDiscovery.WatchAppsAsync(cancel).ConfigureAwait(false)) Signal();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    private async Task RestoreAsync()
    {
        try
        {
            await foreach (var _ in wake.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var missing = store.Profiles.Where(p => Eligible(p) && Resolve(p) == null).ToArray();
                if (missing.Length == 0) continue;
                await dialing.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    Task<IReadOnlyList<AndroidMdnsService>>? candidates = null;
                    foreach (var saved in missing)
                    {
                        var profile = store.Profiles.FirstOrDefault(p => p.Id == saved.Id);
                        if (profile == null || !Eligible(profile) || Resolve(profile) != null) continue;
                        using var burst = CancellationTokenSource.CreateLinkedTokenSource(token);
                        burst.CancelAfter(TimeSpan.FromSeconds(14));
                        try
                        {
                            var (session, endpoint) = await DialAsync(profile, () => candidates ??= DiscoverAsync(token), burst.Token).ConfigureAwait(false);
                            try { Admit(profile, session); }
                            catch { session.Dispose(); throw; }
                            store.RememberAppAddress(profile.Id, profile.AppPublicKeySha256!, endpoint);
                        }
                        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException
                            or System.Security.Cryptography.CryptographicException or OperationCanceledException) { }
                    }
                }
                finally { dialing.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        Task? discover;
        lock (gate)
        {
            if (closed) return;
            closed = true;
            store.Changed -= StoreChanged;
            if (browsing != null) NetworkChange.NetworkAddressChanged -= NetworkChanged;
            pending = worker;
            discover = browser;
            lifetime.Cancel();
            browsing?.Dispose();
            foreach (var attachment in online.Values) attachment.Session.Dispose();
            online.Clear();
        }
        if (pending != null) await pending.ConfigureAwait(false);
        if (discover != null) await discover.ConfigureAwait(false);
        await dialing.WaitAsync().ConfigureAwait(false);
        Task[] waiting;
        lock (gate) waiting = monitors.ToArray();
        await Task.WhenAll(waiting).ConfigureAwait(false);
        if (identity.IsValueCreated) identity.Value.Dispose();
        lifetime.Dispose();
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute.WindowsLink;

internal sealed partial class WindowsLinkService(
    string stateDirectory,
    string? name = null,
    int port = 46916,
    bool discovery = true,
    Func<bool, CancellationToken, Task<LinkRemoteApp[]>>? localCatalogProvider = null,
    Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer>? audioPlayerFactory = null,
    Func<int, IPcProcessSilenceLease?>? acquireLocalSilence = null)
    : IAsyncDisposable
{
    private const int MaximumPeers = 4;
    private const int SavedLimit = 64;
    private const int MaxIconBytes = 6 * 1024;
    private const int MaxCatalogIconBase64Chars = 12 * 1024;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly string _name = Label(name ?? Environment.MachineName, "Windows PC");
    private readonly string _trustFile = Path.Combine(stateDirectory, "trusted.json");
    private readonly Func<bool, CancellationToken, Task<LinkRemoteApp[]>>? _localCatalogProvider = localCatalogProvider;
    private readonly Func<AndroidPcmSource, string?, CancellationToken, IPcmPlayer>? _audioPlayerFactory = audioPlayerFactory;
    private readonly Dictionary<string, (LinkOffer Offer, long At)> _offers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinkConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, (string PeerId, TaskCompletionSource<bool> Result)> _pairing = new();
    private readonly HashSet<string> _paused = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SavedPeer> _saved = new(StringComparer.OrdinalIgnoreCase);
    private bool _savedLoaded;
    private X509Certificate2? _identity;
    private LinkRun? _run;
    private bool _disposed;

    private sealed record SavedPeer(
        string Id,
        string Name,
        string? Alias,
        string Address,
        LinkCapabilities Capabilities,
        bool AutoConnect,
        LinkPeerSettings? Settings = null,
        List<LinkPinnedApp>? PinnedApps = null);

    private sealed class LinkRun
    {
        private readonly object _gate = new();
        private readonly HashSet<Task> _tasks = new();
        private readonly CancellationTokenSource _stop = new();
        internal readonly SemaphoreSlim Handshakes = new(8, 8);
        internal readonly HashSet<string> Retrying = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<IPEndPoint, int> Dialing = new();
        internal readonly Dictionary<string, (long Issued, long AcceptedAt)> ReverseDials = new(StringComparer.OrdinalIgnoreCase);
        internal TcpListener Listener { get; }
        internal LinkDiscovery? Lan { get; }
        internal string Address { get; }
        internal int Port { get; }
        internal CancellationToken Token { get; }

        internal LinkRun(TcpListener listener, LinkDiscovery? lan, string address)
        {
            Listener = listener;
            Lan = lan;
            Address = address;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Token = _stop.Token;
        }

        internal Task Track(Func<Task> action)
        {
            lock (_gate)
            {
                if (Token.IsCancellationRequested) return Task.FromCanceled(Token);
                Task task = Task.Run(action);
                _tasks.Add(task);
                _ = task.ContinueWith(t =>
                {
                    _ = t.Exception;
                    lock (_gate) _tasks.Remove(t);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return task;
            }
        }

        internal async Task StopAsync()
        {
            _stop.Cancel();
            Listener.Stop();
            Lan?.Dispose();
            Task[] pending;
            lock (_gate) pending = _tasks.ToArray();
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (Exception) { /* Individual jobs report their errors at the point of use. */ }
            Handshakes.Dispose();
            _stop.Dispose();
        }
    }

    public event Action? Changed;
    public event Action<LinkPairing>? PairingRequested;
    public event Action<Guid>? PairingEnded;
    public event Action<Exception>? Error;
    public bool IsEnabled { get { lock (_gate) return _run != null; } }
    public bool HasActiveWork { get { lock (_gate) return _connections.Count > 0 || _streams.Count > 0; } }
    public string LocalAddress { get { lock (_gate) return _run?.Address ?? ""; } }
    public string LocalName => _name;

    public LinkPeer[] Peers
    {
        get
        {
            lock (_gate)
            {
                var fresh = _offers.Values.Where(o => Stopwatch.GetElapsedTime(o.At) < TimeSpan.FromMinutes(2))
                    .ToDictionary(o => o.Offer.Id, o => o.Offer, StringComparer.OrdinalIgnoreCase);
                return _saved.Keys.Concat(fresh.Keys).Concat(_connections.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(id =>
                    {
                        _saved.TryGetValue(id, out var saved);
                        fresh.TryGetValue(id, out var seen);
                        _connections.TryGetValue(id, out var connected);
                        bool active = connected?.IsOpen == true;
                        return new LinkPeer(id, connected?.Hello.Name ?? saved?.Name ?? seen!.Name, saved?.Alias,
                            active ? saved?.Address : seen?.Endpoint.ToString() ?? saved?.Address, saved != null,
                            active || seen != null, active,
                            connected?.Hello.Capabilities ?? seen?.Capabilities ?? saved?.Capabilities ?? LinkCapabilities.None);
                    })
                    .OrderByDescending(peer => peer.Connected).ThenByDescending(peer => peer.Trusted)
                    .ThenBy(peer => peer.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
        }
    }

    private LinkCapabilities LocalCapabilities =>
        LinkCapabilities.AudioReceive | LinkCapabilities.AudioReady | LinkCapabilities.WindowReceive | LinkCapabilities.RemoteBrowse | LinkCapabilities.AudioCatalog
        | (LinkAudioCapture.IsSupported ? LinkCapabilities.ProcessAudio | LinkCapabilities.SystemAudio : 0)
        | (LinkAudioCapture.IsSupported && acquireLocalSilence != null ? LinkCapabilities.DestinationOnly : 0)
        | (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) ? LinkCapabilities.WindowCapture | LinkCapabilities.WindowInput : 0);

    public async Task EnableAsync(CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run != null) return;
            await Task.Run(() => Start(token), token).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
        Notify();
    }

    private void Start(CancellationToken token)
    {
        lock (_gate) LoadSavedPeers();
        _identity ??= LinkSecurity.LoadIdentity(stateDirectory);

        token.ThrowIfCancellationRequested();
        var listener = new TcpListener(discovery ? IPAddress.IPv6Any : IPAddress.Loopback, port);
        LinkDiscovery? lan = null;
        try
        {
            if (discovery) listener.Server.DualMode = true;
            listener.Start(16);
            int actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var displayIp = discovery
                ? (LinkDiscovery.SelectDisplayLanAddress() ?? AirPlayDiscovery.ReadInterfaces().FirstOrDefault()?.Address)
                : null;
            if (discovery) lan = new LinkDiscovery(LinkSecurity.Id(_identity), _name, actualPort, LocalCapabilities);
            var run = new LinkRun(listener, lan, new IPEndPoint(displayIp ?? IPAddress.Loopback, actualPort).ToString());
            lock (_gate) _run = run;
            _ = run.Track(() => AcceptAsync(run));
            if (lan != null)
            {
                string localId = LinkSecurity.Id(_identity);
                _ = run.Track(() => lan.ListenAsync(offer => Found(run, offer),
                    (packet, address) => HandleReconnectRequest(run, localId, packet, address), run.Token));
                NetworkChange.NetworkAddressChanged += NetworkChanged;
                _ = run.Track(() => SendDiscoveryAsync(run));
            }
            RetrySaved(run);
        }
        catch { listener.Stop(); lan?.Dispose(); throw; }
    }

    internal void RestoreSavedPeers()
    {
        try { lock (_gate) LoadSavedPeers(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Trace.WriteLine($"Windows Link saved devices: {ex.Message}");
        }
    }

    // Called with _gate held. Reading saved devices does not enable the listener.
    private void LoadSavedPeers()
    {
        if (_savedLoaded) return;
        if (File.Exists(_trustFile))
        {
            if (new FileInfo(_trustFile).Length > 1024 * 1024)
                throw new InvalidDataException("The Windows Link trust file is too large.");
            var records = JsonSerializer.Deserialize<SavedPeer[]>(File.ReadAllText(_trustFile)) ?? [];
            _saved = records.Where(p => p != null && LinkSecurity.IsId(p.Id)
                && TryAddress(p.Address, out _) && !string.IsNullOrWhiteSpace(p.Name)).Take(SavedLimit)
                .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(SanitizeSavedPeer)
                .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        }
        _savedLoaded = true;
    }

    private static SavedPeer SanitizeSavedPeer(SavedPeer peer)
    {
        LinkPeerSettings? settings = peer.Settings?.Normalize();
        List<LinkPinnedApp>? pinned = null;
        if (peer.PinnedApps is { Count: > 0 })
        {
            pinned = peer.PinnedApps
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Key) && !string.IsNullOrWhiteSpace(a.Name))
                .DistinctBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
                .Take(SavedLimit)
                .Select(a => a with
                {
                    Key = a.Key.Trim(),
                    Name = Label(a.Name, a.Key.Trim()),
                    ProcessName = Label(a.ProcessName, a.Key.Trim()),
                    IconPngBase64 = a.IconPngBase64 is { Length: > 0 and <= MaxCatalogIconBase64Chars } ? a.IconPngBase64 : null
                })
                .ToList();
        }
        return peer with { Settings = settings, PinnedApps = pinned };
    }

    public async Task DisableAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
        Notify();
    }

    private async Task StopAsync()
    {
        LinkRun? run;
        lock (_gate)
        {
            run = _run;
            if (run == null) return;
            _run = null;
            _offers.Clear();
            _paused.Clear();
        }
        if (run.Lan != null) NetworkChange.NetworkAddressChanged -= NetworkChanged;
        await run.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopAsync().ConfigureAwait(false);
            _identity?.Dispose();
            _identity = null;
        }
        finally { _lifecycle.Release(); }
    }

    public Task DiscoverAsync(CancellationToken token = default)
    {
        LinkRun run = GetRun();
        return run.Track(() => SendDiscoveryAsync(run)).WaitAsync(token);
    }

    private void NetworkChanged(object? sender, EventArgs e)
    {
        LinkRun? run;
        lock (_gate) run = _run;
        if (run == null) return;
        _ = run.Track(() => SendDiscoveryAsync(run));
        RetrySaved(run);
    }

    private static async Task SendDiscoveryAsync(LinkRun run)
    {
        if (run.Lan == null) return;
        try { await run.Lan.DiscoverAsync(run.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or NetworkInformationException) { }
    }

    private void Found(LinkRun run, LinkOffer offer)
    {
        bool changed;
        lock (_gate)
        {
            if (_run != run) return;
            changed = !_offers.TryGetValue(offer.Id, out var previous) || previous.Offer != offer
                || Stopwatch.GetElapsedTime(previous.At) >= TimeSpan.FromMinutes(2);
            if (_offers.Count >= SavedLimit && !_offers.ContainsKey(offer.Id))
                _offers.Remove(_offers.MinBy(item => item.Value.At).Key);
            _offers[offer.Id] = (offer, Stopwatch.GetTimestamp());
        }
        if (changed) Notify();
        ScheduleRetry(run, offer.Id);
    }

    private LinkRun GetRun()
    {
        lock (_gate) return _run ?? throw new InvalidOperationException("Enable Windows Link first.");
    }

    private static string Label(string? value, string fallback)
    {
        string result = new((value ?? "").Where(c => !char.IsControl(c)).Take(64).ToArray());
        return string.IsNullOrWhiteSpace(result) ? fallback : result.Trim();
    }

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    private void Report(Exception error)
    {
        try { Error?.Invoke(error); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }
}

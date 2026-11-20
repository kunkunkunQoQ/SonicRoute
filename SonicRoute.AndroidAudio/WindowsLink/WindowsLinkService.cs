using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
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
    Func<int, IPcProcessSilenceLease?>? acquireLocalSilence = null,
    TimeSpan? offerLifetime = null,
    IPEndPoint? discoveryBind = null,
    IPEndPoint? discoveryTarget = null)
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
    private readonly Dictionary<string, List<TimedOffer>> _offers = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _offerLifetime = (long)((offerLifetime ?? TimeSpan.FromMinutes(2)).TotalSeconds * Stopwatch.Frequency);
    private readonly Dictionary<string, LinkConnectFailure> _failures = new(StringComparer.OrdinalIgnoreCase);
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
        List<LinkPinnedApp>? PinnedApps = null,
        List<string>? DisabledSmartVolumeApps = null,
        bool LastConnectionIncoming = false,
        Guid FullTrustGrant = default,
        string? DirectAddress = null,
        bool ClipboardEnabled = false,
        bool SupportsClipboardSync = false);

    private sealed class LinkRun
    {
        private readonly object _gate = new();
        private readonly HashSet<Task> _tasks = new();
        private readonly CancellationTokenSource _stop = new();
        internal readonly SemaphoreSlim Handshakes = new(8, 8);
        internal readonly Dictionary<string, RetryWork> Retrying = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, RetryWork> ManualConnections = new(StringComparer.OrdinalIgnoreCase);
        internal readonly SemaphoreSlim Outbound = new(4, 4);
        internal readonly SemaphoreSlim DiscoveryWake = new(0, 1);
        internal long LastProbe;
        internal long NetworkRefreshAt;
        internal long SearchRetryAt;
        internal long ProbeRetryAt;
        internal bool ProbeRequested = true;
        internal long SearchEndsAt;
        internal int SearchWaiters;
        internal TaskCompletionSource? Search;
        internal long TrustRevision;
        internal readonly Dictionary<IPEndPoint, int> Dialing = new();
        internal readonly Dictionary<string, (long Issued, long AcceptedAt)> ReverseDials = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, long> OfferRetryWakeAt = new(StringComparer.OrdinalIgnoreCase);
        internal TcpListener Listener { get; }
        internal LinkDiscovery? Lan { get; }
        internal string Address { get; set; }
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
            Outbound.Dispose();
            DiscoveryWake.Dispose();
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
    public LinkLocalAddress[] GetLocalAddresses()
    {
        string preferred;
        int listenPort;
        lock (_gate)
        {
            if (_run == null) return [];
            preferred = _run.Address;
            listenPort = _run.Port;
        }
        var addresses = new Dictionary<string, LinkLocalAddress>(StringComparer.Ordinal);
        if (discovery)
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                try
                {
                    foreach (var entry in adapter.GetIPProperties().UnicastAddresses)
                    {
                        var ip = entry.Address;
                        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
                            || ip.IsIPv6Multicast || ip.IsIPv6LinkLocal) continue;
                        string address = new IPEndPoint(ip, listenPort).ToString();
                        addresses.TryAdd(address, new LinkLocalAddress(adapter.Name, address));
                    }
                }
                catch (NetworkInformationException) { }
            }
        }
        if (preferred.Length > 0) addresses.TryAdd(preferred, new LinkLocalAddress("", preferred));
        return addresses.Values.OrderByDescending(entry => entry.Address == preferred)
            .ThenBy(entry => entry.Adapter, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.Address, StringComparer.Ordinal).ToArray();
    }
    public string LocalName => _name;

    public LinkPeer[] Peers
    {
        get
        {
            lock (_gate)
            {
                return _saved.Keys.Concat(_offers.Keys).Concat(_connections.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(id =>
                    {
                        _saved.TryGetValue(id, out var saved);
                        var seen = BestOfferLocked(id, saved);
                        _connections.TryGetValue(id, out var connected);
                        bool active = connected?.IsOpen == true;
                        if (saved == null && seen == null && !active) return null;
                        var failure = _failures.GetValueOrDefault(id);
                        var state = active ? LinkPeerState.Connected
                            : _run?.ManualConnections.ContainsKey(id) == true || _run?.Retrying.GetValueOrDefault(id)?.Dialing == true
                                ? LinkPeerState.Connecting
                            : saved != null && (!saved.AutoConnect || _paused.Contains(id)) ? LinkPeerState.DisconnectedByUser
                            : failure != LinkConnectFailure.None ? LinkPeerState.Failed
                            : seen != null ? (saved == null ? LinkPeerState.Discovered : LinkPeerState.Online)
                            : LinkPeerState.Offline;
                        return new LinkPeer(id, connected?.Hello.Name ?? saved?.Name ?? seen!.Name, saved?.Alias,
                            active ? saved?.Address : seen?.Endpoint.ToString() ?? saved?.Address, saved != null,
                            active || seen != null, active,
                            connected?.Hello.Capabilities ?? seen?.Capabilities ?? saved?.Capabilities ?? LinkCapabilities.None)
                        { State = state, Failure = failure, SupportsNativeVideo = active && connected!.Hello.SupportsNativeVideo,
                            SupportsFileBrowse = active && connected!.Hello.SupportsFileBrowse,
                            SupportsSourcePlayback = active && connected!.Hello.SupportsSourcePlayback,
                            SupportsAudioRelay = active && connected!.Hello.SupportsAudioRelay };
                    })
                    .OfType<LinkPeer>()
                    .OrderByDescending(peer => peer.Connected).ThenByDescending(peer => peer.Trusted)
                    .ThenBy(peer => peer.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
        }
    }

    internal Func<CancellationToken, bool> ProbeAudioCapture { get; init; } = PcProcessLoopbackCapture.CanInitializeProcessLoopback;
    internal Func<int?, Action<ReadOnlyMemory<byte>>, Action, CancellationToken, Task> CaptureAudio { get; init; } = LinkAudioCapture.CaptureAsync;
    private volatile bool _canSendAudio;
    internal bool CanSendAudio => _canSendAudio;
    internal Func<bool>? ProbeExclusiveCapture { get; init; }
    internal Func<bool>? ProbeSpeakerFallback { get; init; }
    internal Func<int, bool>? ProbeAppSpeaker { get; init; }
    internal Func<int, IPcProcessSilenceLease?>? AcquireSpeakerFallback { get; init; }
    internal Func<IPcProcessSilenceLease?>? AcquireSystemSpeakerSilence { get; init; }
    private readonly object _exclusiveProbeGate = new();
    private long _lastExclusiveProbe;
    private volatile WindowsLinkExclusiveMode _localAudioRoute;
    internal WindowsLinkExclusiveMode AudioIsolation => _localAudioRoute;
    internal bool CanSendExclusively => _localAudioRoute.HasFlag(WindowsLinkExclusiveMode.DedicatedOutput);
    internal bool CanUseSpeakerFallback => _localAudioRoute.HasFlag(WindowsLinkExclusiveMode.MuteSourceSpeaker);

    internal void RefreshExclusiveCapability(bool force = false)
    {
        lock (_exclusiveProbeGate)
        {
            if (!force && _lastExclusiveProbe != 0 && Stopwatch.GetElapsedTime(_lastExclusiveProbe) < TimeSpan.FromSeconds(2)) return;
            var route = WindowsLinkExclusiveMode.Unavailable;
            if (CanSendAudio)
            {
                if (acquireLocalSilence != null && IsAvailable(ProbeExclusiveCapture, defaultValue: true))
                    route |= WindowsLinkExclusiveMode.DedicatedOutput;
                if (AcquireSpeakerFallback != null && IsAvailable(ProbeSpeakerFallback, defaultValue: false))
                    route |= WindowsLinkExclusiveMode.MuteSourceSpeaker;
            }
            _localAudioRoute = route;
            _lastExclusiveProbe = Stopwatch.GetTimestamp();
        }

        static bool IsAvailable(Func<bool>? probe, bool defaultValue)
        {
            try { return probe?.Invoke() ?? defaultValue; }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                Trace.WriteLine(ex);
                return false;
            }
        }
    }

    private LinkCapabilities _videoCapabilities;
    private LinkCapabilities LocalCapabilities =>
        _videoCapabilities | LinkCapabilities.FileTransfer | LinkCapabilities.FullTrust |
        LinkCapabilities.AudioReceive | LinkCapabilities.AudioReady | LinkCapabilities.WindowReceive | LinkCapabilities.RemoteBrowse | LinkCapabilities.AudioCatalog
        | (CanSendAudio ? LinkCapabilities.ProcessAudio | LinkCapabilities.SystemAudio | LinkCapabilities.WindowAudio : 0)
        | (CanSendExclusively ? LinkCapabilities.DestinationOnly : 0)
        | (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)
            ? LinkCapabilities.WindowCapture | LinkCapabilities.WindowInput | LinkCapabilities.MonitorCapture : 0);

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

        _canSendAudio = ProbeAudioCapture(token);
        RefreshExclusiveCapability(force: true);
        _videoCapabilities = (LinkVideoEncoder.SupportsHevc ? LinkCapabilities.HevcEncode : 0)
            | (MediaFoundationH264Decoder.SupportsHevc ? LinkCapabilities.HevcDecode : 0);
        token.ThrowIfCancellationRequested();
        var bindAddress = Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any;
        var listener = new TcpListener(discovery ? bindAddress : IPAddress.Loopback, port);
        LinkDiscovery? lan = null;
        try
        {
            if (listener.Server.AddressFamily == AddressFamily.InterNetworkV6 && Socket.OSSupportsIPv4)
                listener.Server.DualMode = true;
            listener.Start(16);
            int actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (discovery && Socket.OSSupportsIPv4)
                lan = new LinkDiscovery(LinkSecurity.Id(_identity), _name, actualPort, LocalCapabilities, discoveryBind, discoveryTarget);
            var displayIp = lan != null ? lan.RefreshInterfaces() : discovery ? LinkDiscovery.SelectDisplayLanAddress() : null;
            string address = discovery
                ? DisplayAddress(discoveryBind?.Address ?? displayIp, actualPort)
                : new IPEndPoint(IPAddress.Loopback, actualPort).ToString();
            var run = new LinkRun(listener, lan, address);
            lock (_gate) _run = run;
            _ = run.Track(() => AcceptAsync(run));
            if (lan != null)
            {
                string localId = LinkSecurity.Id(_identity);
                _ = run.Track(() => lan.ListenAsync((offer, isQuery) => FoundOffer(run, offer, isQuery),
                    (packet, address) => HandleReconnectRequest(run, localId, packet, address), run.Token));
                NetworkChange.NetworkAddressChanged += NetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
            }
            _ = run.Track(() => DiscoveryLoopAsync(run));
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
        var disabled = (peer.DisabledSmartVolumeApps ?? pinned?.Where(app => !app.SmartVolume).Select(app => app.Key).ToList() ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key) && key.Length <= 128)
            .Select(key => key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? direct = LinkDirectAddress.TryParse(peer.DirectAddress, out var target) ? target.Canonical : null;
        return peer with { Settings = settings, PinnedApps = pinned, DisabledSmartVolumeApps = disabled, DirectAddress = direct };
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
            _failures.Clear();
            run.Search?.TrySetCanceled();
        }
        if (run.Lan != null)
        {
            NetworkChange.NetworkAddressChanged -= NetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        }
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

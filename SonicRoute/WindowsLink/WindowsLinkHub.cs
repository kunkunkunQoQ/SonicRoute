#if SONICROUTE_PHONE
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.WindowsLink;

namespace SonicRoute;

internal sealed record WindowsLinkAppDrag(LinkPeer Peer, LinkRemoteApp App, bool Window = false)
{
    internal const string Format = "SonicRoute.WindowsLinkApp.v1";
    internal bool CanReturn { get; init; }
}

internal static class WindowsLinkHub
{
    private const int MaxIconCache = 64;
    private static WindowsLinkService? _service;
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim CatalogGate = new(1);
    private static long _catalogRevision;
    private static readonly Dictionary<(LinkCatalogScope Scope, bool Icons), (long Revision, long At, LinkRemoteApp[] Apps)> LocalCatalogs = new();
    private static readonly Dictionary<Guid, LinkPairing> PendingPairings = new();
    private static readonly ConcurrentDictionary<string, byte[]?> IconPngCache = new(StringComparer.OrdinalIgnoreCase);
    private static LinkPeer[] _lastPeers = [];
    private static string[] _lastConnectedOutputs = [];
    private static string[] _lastIncoming = [];
    private static string[] _lastOutgoing = [];
    private static LinkMedia[] _lastNativeAppRoutes = [];
    private static string _lastLocalAddress = "";
    private static bool _lastEnabled;
    private static WindowsLinkAppDrag? _dragSource;
    private static WindowsLinkCatalogNotifications? _catalogNotifications;
    private static WindowsLinkClipboardSync? _clipboard;

    internal static event Action? Changed;
    internal static event Action? MediaChanged;
    internal static event Action? DragStateChanged;
    internal static event Action<string>? RemoteCatalogChanged;
    internal static event Action<string>? FullTrustChanged;
    internal static event Action<string>? ClipboardStateChanged;

    internal static WindowsLinkService? Service { get { lock (Gate) return _service; } }
    internal static LinkPairing[] Pairings { get { lock (Gate) return PendingPairings.Values.ToArray(); } }
    internal static bool HasIncomingAudio => Service?.IncomingAudioSources.Length > 0;
    internal static WindowsLinkAudioSource[] IncomingAudioSources => Service?.IncomingAudioSources ?? [];

    internal static bool IsDragging => DragSource != null;

    internal static WindowsLinkAppDrag? DragSource
    {
        get => _dragSource;
        set
        {
            if (ReferenceEquals(_dragSource, value)) return;
            _dragSource = value;
            DragStateChanged?.Invoke();
        }
    }

    internal static bool CanPullAudio(LinkPeer peer, LinkRemoteApp app) =>
        peer.Connected && (peer.Capabilities & RequiredAudioCapability(app)) != 0;

    private static LinkCapabilities RequiredAudioCapability(LinkRemoteApp app) =>
        app.IsSystemAudio ? LinkCapabilities.SystemAudio : LinkCapabilities.ProcessAudio;

    internal static WindowsLinkAudioSource? FindIncomingAudio(string? sourceId) => Service?.FindIncomingAudio(sourceId);

    internal static string? GetPcAppOutput(int processId, out bool destinationOnly)
    {
        var stream = Service?.FindOutgoingAppAudio(processId);
        destinationOnly = stream?.DestinationOnly == true;
        return stream == null ? null : WindowsLinkOutputIdentity.FormatDeviceId(stream.PeerId);
    }

    internal static IEnumerable<AudioDeviceInfo> GetOutputDevices()
    {
        if (Service is not { IsEnabled: true, CanSendAudio: true } service) return [];
        string format = L10n.T("Phone.WindowsLinkOutputFormat");
        return service.Peers
            .Where(p => p.Connected && (p.Capabilities & LinkCapabilities.AudioReceive) != 0)
            .Select(p => new AudioDeviceInfo
            {
                Id = WindowsLinkOutputIdentity.FormatDeviceId(p.Id),
                DisplayName = string.Format(format, p.DisplayName),
                Flow = EDataFlow.eRender
            });
    }

    internal static WindowsLinkExclusiveMode LocalExclusiveMode(LinkPeer peer)
    {
        if (!peer.Connected || !peer.Capabilities.HasFlag(LinkCapabilities.AudioReady)
            || Service is not { IsEnabled: true, CanSendAudio: true } service) return WindowsLinkExclusiveMode.Unavailable;
        return service.AudioIsolation;
    }

    internal static WindowsLinkExclusiveMode RemoteExclusiveMode(LinkPeer peer, LinkRemoteApp app)
    {
        if (app.IsSystemAudio || !CanPullAudio(peer, app)) return WindowsLinkExclusiveMode.Unavailable;
        var mode = app.SupportsExclusivePlayback(peer.Capabilities) ? WindowsLinkExclusiveMode.DedicatedOutput : WindowsLinkExclusiveMode.Unavailable;
        if (app.CanMuteSourceSpeaker && peer.Capabilities.HasFlag(LinkCapabilities.AudioReady))
            mode |= WindowsLinkExclusiveMode.MuteSourceSpeaker;
        return mode;
    }

    internal static Task<(bool Success, int HResult, string Message)> RoutePcAppToPeerAsync(int processId, string deviceId,
        bool destinationOnly = false, bool allowSpeakerMute = false, bool keepOtherPeers = false) =>
        Task.Run(async () =>
        {
            if (!WindowsLinkOutputIdentity.TryParsePeerId(deviceId, out string peerId))
                return (false, -1, L10n.T("Phone.StatusOperationFailed"));
            if (processId <= 0)
                return (false, -1, L10n.T("Phone.Err.InvalidProcessId"));
            if (Service is not { IsEnabled: true } service)
                return (false, -1, L10n.T("Phone.StatusOperationFailed"));
            if (!service.CanSendAudio)
                return (false, -1, string.Format(L10n.T("Phone.WindowsLinkAudioUnavailable"), Environment.MachineName));
            try
            {
                if (!keepOtherPeers)
                {
                    var native = new AudioAppInfo { ProcessId = (uint)processId };
                    if (destinationOnly)
                        return await AppPlaybackOutputs.SetAsync(native, [deviceId], deviceId, allowSpeakerMute).ConfigureAwait(false);
                    string original = AppPlaybackOutputs.PreviousLocalOutput(processId);
                    return await AppPlaybackOutputs.SetAsync(native, [original, deviceId], original, allowSpeakerMute).ConfigureAwait(false);
                }
                var app = AudioService.GetApps(false).FirstOrDefault(a => !a.IsExternal && a.ProcessId == (uint)processId);
                string processName = app?.ProcessName ?? TryProcessName(processId) ?? $"pid:{processId}";
                string label = app != null ? AppDisplayName.Get(app) : processName;
                byte[]? iconPng = GetOrEncodeLocalIcon(processId, processName, app?.IconPath);
                await service.SendAudioAsync(peerId, processId, label, appKey: processName, iconPng: iconPng,
                    destinationOnly: destinationOnly, allowSpeakerMute: allowSpeakerMute).ConfigureAwait(false);
                bool confirmed = service.Peers.Any(p => p.Id == peerId && (p.Capabilities & LinkCapabilities.AudioReady) != 0);
                return (true, 0, L10n.T(confirmed ? "Phone.WindowsLinkPlaybackReady" : "Phone.WindowsLinkPlaybackUnconfirmed"));
            }
            catch (Exception ex)
            {
                ExceptionDiagnostics.Record("WindowsLink.SendAudio", ex);
                return (false, ex.HResult, ex.Message);
            }
        });

    internal static void StopAllPcAppsToPeers()
    {
        if (Service is { } service) _ = service.StopAllOutgoingAudioAsync();
    }

    internal static async Task PullAudioAsync(LinkPeer peer, LinkRemoteApp app, CancellationToken token = default, bool destinationOnly = false, bool allowSpeakerMute = false)
    {
        var service = EnsureService();
        await service.EnableAsync(token).ConfigureAwait(false);
        var current = service.Peers.FirstOrDefault(p => string.Equals(p.Id, peer.Id, StringComparison.OrdinalIgnoreCase));
        if (current is { Connected: true } && !CanPullAudio(current, app))
            throw new NotSupportedException(string.Format(L10n.T("Phone.WindowsLinkAudioUnavailable"), current.DisplayName));
        await service.RequestPullAudioAsync(peer.Id, app, token, destinationOnly, allowSpeakerMute).ConfigureAwait(false);
    }

    internal static async Task ReconnectPeerAsync(string peerId, CancellationToken token)
    {
        var service = EnsureService();
        if (!service.Peers.Any(peer => peer.Trusted && string.Equals(peer.Id, peerId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(L10n.T("Phone.Err.PhoneRemovedAddFirst"));
        try { await service.ReconnectPeerAsync(peerId, token).ConfigureAwait(false); }
        catch (IOException ex) when (ex is not LinkConnectionFailureException)
        {
            throw new LinkConnectionFailureException(WindowsLinkService.ClassifyFailure(ex),
                L10n.T("Phone.WindowsLinkReconnectFailed"), ex);
        }
    }

    internal static async Task<(Guid StreamId, bool AudioAvailable)> OpenDisplayNearAsync(WindowsLinkAppDrag payload, int screenX, int screenY, CancellationToken token = default)
    {
        var service = EnsureService();
        await service.EnableAsync(token).ConfigureAwait(false);
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(screenX, screenY)).WorkingArea;
        int x = Math.Max(area.Left, Math.Min(screenX - 210, area.Right - 820));
        int y = Math.Max(area.Top, Math.Min(screenY - 30, area.Bottom - 500));
        return await service.OpenWindowWithAudioAsync(payload.Peer.Id, payload.App, x, y, token).ConfigureAwait(false);
    }

    internal static async Task ReturnToPeerAsync(string peerId, string appKey, bool window = false, string? instanceId = null)
    {
        if (Service is not { } service) return;
        await service.StopPeerAppStreamsAsync(peerId, appKey, window, instanceId).ConfigureAwait(false);
    }

    internal static WindowsLinkService EnsureService()
    {
        WindowsLinkService service;
        lock (Gate)
        {
            if (_service != null) return _service;
            service = CreateService();
            service.RestoreSavedPeers();
            service.Changed += ServiceChanged;
            service.FullTrustChanged += ServiceFullTrustChanged;
            service.ClipboardStateChanged += ServiceClipboardStateChanged;
            service.RemoteCatalogChanged += id => RemoteCatalogChanged?.Invoke(id);
            service.PairingRequested += PairingRequested;
            service.PairingEnded += PairingEnded;
            WindowsLinkFileUi.Attach(service);
            if (System.Windows.Application.Current is App app)
                _clipboard = new WindowsLinkClipboardSync(service, app.Dispatcher,
                    new DesktopClipboardLink(app.ClipboardNotificationWindow, WindowsLinkService.MaxClipboardBytes));
            service.Error += error => ExceptionDiagnostics.Record("WindowsLink.Media", error);
            _service = service;
        }
        return service;
    }

    internal static string StateDirectory => Path.Combine(
        Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonicRoute"),
        "WindowsLink");

    private static WindowsLinkService CreateService() =>
        new(StateDirectory, audioPlayerFactory: CreatePlayer, acquireLocalSilence: AndroidAudioHost.AcquireWindowsLinkSilenceLease)
        {
            ScopedCatalogProvider = BuildScopedLocalCatalogAsync,
            ProbeExclusiveCapture = AndroidAudioHost.HasWindowsLinkSilenceEndpoint,
            ProbeSpeakerFallback = AndroidAudioHost.HasWindowsLinkSpeakerFallback,
            ProbeAppSpeaker = AndroidAudioHost.CanMuteAppSpeaker,
            AcquireSpeakerFallback = AndroidAudioHost.AcquireWindowsLinkSpeakerFallback,
            AcquireSystemSpeakerSilence = AndroidAudioHost.AcquireSystemSpeakerSilence,
            CanShareWindow = window => ConfigService.IsPanelAppVisible(window.ProcessName)
        };

    private static IPcmPlayer CreatePlayer(AndroidPcmSource pcm, string? endpoint, CancellationToken token) =>
        AndroidOutputIdentity.IsPhoneOutput(endpoint)
            ? AndroidAudioHost.CreatePhonePcmPlayer(pcm, endpoint!, token)
            : new WasapiPcmPlayer(pcm, endpoint);

    internal static void NotifyLocalCatalogChanged()
    {
        Interlocked.Increment(ref _catalogRevision);
        Service?.NotifyLocalCatalogChanged();
    }

    private static async Task<LinkRemoteApp[]> BuildScopedLocalCatalogAsync(LinkCatalogScope scope, bool includeIcons, CancellationToken token)
    {
        await CatalogGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long revision = Volatile.Read(ref _catalogRevision);
            var hidden = ConfigService.GetHiddenPanelAppsSnapshot();
            if (scope == LinkCatalogScope.Audio)
                return await ReadLocalScopeAsync(scope, includeIcons, revision, hidden, token).ConfigureAwait(false);
            if (scope == LinkCatalogScope.Windows)
                return await ReadLocalScopeAsync(scope, includeIcons, revision, hidden, token).ConfigureAwait(false);

            var audio = await ReadLocalScopeAsync(LinkCatalogScope.Audio, includeIcons, revision, hidden, token).ConfigureAwait(false);
            var windows = await ReadLocalScopeAsync(LinkCatalogScope.Windows, includeIcons, revision, hidden, token).ConfigureAwait(false);
            var groups = audio.Concat(windows).GroupBy(app => app.Key, StringComparer.OrdinalIgnoreCase);
            return groups.Select(group =>
            {
                var app = group.First();
                var window = group.FirstOrDefault(item => item.HasWindow);
                // The legacy combined row carries its representative window's owning PID.
                return window == null ? app with { InstanceId = null } : app with
                {
                    HasWindow = true,
                    WindowHandle = window.WindowHandle,
                    WindowTitle = window.WindowTitle,
                    ProcessId = window.ProcessId,
                    InstanceId = null
                };
            }).ToArray();
        }
        finally { CatalogGate.Release(); }
    }

    private static async Task<LinkRemoteApp[]> ReadLocalScopeAsync(LinkCatalogScope scope, bool icons, long revision,
        HashSet<string> hidden, CancellationToken token)
    {
        if (LocalCatalogs.TryGetValue((scope, icons), out var cached) && cached.Revision == revision
            && System.Diagnostics.Stopwatch.GetElapsedTime(cached.At) < TimeSpan.FromSeconds(2))
            return cached.Apps;

        LinkRemoteApp[] apps;
        if (scope == LinkCatalogScope.Audio)
        {
            var audioApps = await AudioService.GetAppsAsync(refresh: false).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            apps = audioApps.Where(app => !app.IsExternal && app.ProcessId > 0 && app.ProcessId != Environment.ProcessId
                    && !string.IsNullOrWhiteSpace(app.ProcessName) && !hidden.Contains(app.ProcessName))
                .DistinctBy(app => app.ProcessId).Take(48).Select(app =>
                {
                    int pid = (int)app.ProcessId;
                    string name = app.ProcessName!;
                    long started = LinkWindowCapture.ReadProcess(pid).StartTicks;
                    return new LinkRemoteApp(name, AppDisplayName.Get(app), name, pid, app.HasActiveSession, false, 0, null,
                        icons ? GetOrEncodeLocalIcon(pid, name, app.IconPath) : null)
                    {
                        HasAudioSession = true,
                        InstanceId = LinkRemoteApp.AudioInstanceId(pid, started)
                    };
                }).ToArray();
        }
        else
        {
            apps = LinkWindowCapture.GetWindows().Where(w => WindowVisible(w, hidden)).Take(48).Select(w =>
            {
                string name = w.ProcessName ?? $"pid:{w.ProcessId}";
                return new LinkRemoteApp(name, AppDisplayName.Get(name, w.Name), name, w.ProcessId, false,
                    true, w.Handle, w.Name, icons ? GetOrEncodeLocalIcon(w.ProcessId, name, null) : null)
                {
                    HasAudioSession = false,
                    InstanceId = w.InstanceId
                };
            }).ToArray();
        }
        LocalCatalogs[(scope, icons)] = (revision, System.Diagnostics.Stopwatch.GetTimestamp(), apps);
        return apps;
    }

    private static bool WindowVisible(LinkWindow window, HashSet<string> hidden) =>
        window.ProcessName == null ? hidden.Count == 0 : !hidden.Contains(window.ProcessName);

    private static string? TryProcessName(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return string.IsNullOrWhiteSpace(proc.ProcessName) ? null : proc.ProcessName;
        }
        catch { return null; }
    }

    private static byte[]? GetOrEncodeLocalIcon(int pid, string processName, string? iconPath)
    {
        if (IconPngCache.TryGetValue(processName, out var cached)) return cached;
        byte[]? png = EncodeIconPng(AppIconService.GetIconForApp(pid, processName, iconPath));
        if (IconPngCache.Count >= MaxIconCache) IconPngCache.Clear();
        IconPngCache[processName] = png;
        return png;
    }

    private static byte[]? EncodeIconPng(ImageSource? source)
    {
        if (source is not BitmapSource bmp) return null;
        try
        {
            if (bmp.PixelWidth > 32 || bmp.PixelHeight > 32)
            {
                double scale = 32d / Math.Max(bmp.PixelWidth, bmp.PixelHeight);
                bmp = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            byte[] bytes = ms.ToArray();
            return bytes.Length is > 0 and <= 6 * 1024 ? bytes : null;
        }
        catch { return null; }
    }

    private static void ServiceClipboardStateChanged(string peerId) => ClipboardStateChanged?.Invoke(peerId);

    private static void ServiceFullTrustChanged(string peerId) => FullTrustChanged?.Invoke(peerId);

    private static void ServiceChanged()
    {
        bool peersChanged, outputsChanged, mediaChanged;
        (int Pid, string Id, bool Offline)[] ended;
        lock (Gate)
        {
            if (_service == null) return;
            var peers = _service.Peers;
            bool enabled = _service.IsEnabled;
            string localAddress = _service.LocalAddress;
            var streams = _service.Streams;
            var nativeRoutes = streams.Where(s => s.Kind == LinkMediaKind.AppAudio && !s.Incoming && !s.IsRelayed && s.ProcessId > 0).ToArray();
            ended = _lastNativeAppRoutes.Where(s => !nativeRoutes.Any(n => n.Id == s.Id))
                .Select(s => (s.ProcessId!.Value, WindowsLinkOutputIdentity.FormatDeviceId(s.PeerId),
                    enabled && peers.Any(p => p.Id == s.PeerId && p.Trusted && !p.Connected && p.State != LinkPeerState.DisconnectedByUser))).ToArray();
            _lastNativeAppRoutes = nativeRoutes;

            string[] connectedOutputs = enabled
                ? peers.Where(p => p.Connected && (p.Capabilities & LinkCapabilities.AudioReceive) != 0)
                    .Select(p => $"{p.Id}:{p.DisplayName}").ToArray()
                : [];
            string[] incoming = _service.IncomingAudioSources
                .Select(s =>
                {
                    var st = s.Session.State;
                    bool only = streams.Any(stream => stream.Id == s.StreamId && stream.DestinationOnly);
                    return $"{s.Id}:{s.Name}:{st.State}:{st.Volume}:{st.Muted}:{only}:{string.Join(',', s.Session.GetOutputs())}";
                }).ToArray();
            string[] outgoing = streams
                .Where(s => s.Kind == LinkMediaKind.Window || !s.Incoming && s.Kind == LinkMediaKind.AppAudio)
                .Select(s => $"{s.Id}:{s.PeerId}:{s.ProcessId}:{s.DestinationOnly}").ToArray();

            peersChanged = _lastEnabled != enabled || _lastLocalAddress != localAddress || !_lastPeers.SequenceEqual(peers);
            outputsChanged = !_lastConnectedOutputs.SequenceEqual(connectedOutputs);
            mediaChanged = !_lastIncoming.SequenceEqual(incoming) || !_lastOutgoing.SequenceEqual(outgoing);

            _lastEnabled = enabled;
            if (enabled && (peers.Any(peer => peer.Connected && peer.Capabilities.HasFlag(LinkCapabilities.AudioCatalog))
                || streams.Any(stream => !stream.Incoming && stream.DestinationOnly)))
                _catalogNotifications ??= WindowsLinkCatalogNotifications.Acquire();
            else if (_catalogNotifications != null)
            {
                _ = _catalogNotifications.ReleaseAsync();
                _catalogNotifications = null;
            }
            _lastLocalAddress = localAddress;
            _lastPeers = peers;
            _lastConnectedOutputs = connectedOutputs;
            _lastIncoming = incoming;
            _lastOutgoing = outgoing;
        }

        foreach (var route in ended) AppPlaybackOutputs.OutputEnded(route.Pid, route.Id, route.Offline);
        if (peersChanged) Changed?.Invoke();
        if (outputsChanged || mediaChanged) MediaChanged?.Invoke();
        if (outputsChanged) ApplicationAudio.NotifyOutputsChanged();
        else if (mediaChanged) ApplicationAudio.NotifyChanged();
    }

    private static void PairingRequested(LinkPairing pairing)
    {
        lock (Gate) PendingPairings[pairing.Id] = pairing;
    }

    private static void PairingEnded(Guid id)
    {
        lock (Gate) PendingPairings.Remove(id);
    }

    internal static void TrimForIdle() => IconPngCache.Clear();

    internal static async Task ShutdownAsync()
    {
        WindowsLinkService? service;
        WindowsLinkCatalogNotifications? notifications;
        lock (Gate)
        {
            service = _service;
            _service = null;
            _clipboard?.Dispose();
            _clipboard = null;
            notifications = _catalogNotifications;
            _catalogNotifications = null;
            PendingPairings.Clear();
            _lastPeers = [];
            _lastConnectedOutputs = [];
            _lastIncoming = [];
            _lastOutgoing = [];
            _lastNativeAppRoutes = [];
            _lastLocalAddress = "";
            _lastEnabled = false;
        }
        IconPngCache.Clear();
        Interlocked.Increment(ref _catalogRevision);
        if (notifications != null) await notifications.ReleaseAsync().ConfigureAwait(false);
        if (service == null) return;
        service.Changed -= ServiceChanged;
        service.FullTrustChanged -= ServiceFullTrustChanged;
        service.ClipboardStateChanged -= ServiceClipboardStateChanged;
        service.PairingRequested -= PairingRequested;
        service.PairingEnded -= PairingEnded;
        WindowsLinkFileUi.Detach(service);
        await service.DisposeAsync().ConfigureAwait(false);
    }
}
#endif

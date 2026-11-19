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
}

internal static class WindowsLinkHub
{
    private const int MaxIconCache = 64;
    private static WindowsLinkService? _service;
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, LinkPairing> PendingPairings = new();
    private static readonly ConcurrentDictionary<string, byte[]?> IconPngCache = new(StringComparer.OrdinalIgnoreCase);
    private static LinkPeer[] _lastPeers = [];
    private static string[] _lastConnectedOutputs = [];
    private static string[] _lastIncoming = [];
    private static string[] _lastOutgoing = [];
    private static bool _lastEnabled;
    private static WindowsLinkAppDrag? _dragSource;

    internal static event Action? Changed;
    internal static event Action? MediaChanged;
    internal static event Action? DragStateChanged;
    internal static event Action<string>? RemoteCatalogChanged;

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

    internal static string? TryGetPcAppOutput(int processId)
    {
        if (Service is not { IsEnabled: true } service) return null;
        return service.FindOutgoingAudioPeerForProcess(processId) is { } peerId
            ? WindowsLinkOutputIdentity.FormatDeviceId(peerId)
            : null;
    }

    internal static IEnumerable<AudioDeviceInfo> GetOutputDevices()
    {
        if (!LinkAudioCapture.IsSupported || Service is not { IsEnabled: true } service) return [];
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

    internal static Task<(bool Success, int HResult, string Message)> RoutePcAppToPeerAsync(int processId, string deviceId, bool destinationOnly = false) =>
        Task.Run(async () =>
        {
            if (!WindowsLinkOutputIdentity.TryParsePeerId(deviceId, out string peerId))
                return (false, -1, L10n.T("Phone.StatusOperationFailed"));
            if (processId <= 0)
                return (false, -1, L10n.T("Phone.Err.InvalidProcessId"));
            if (!LinkAudioCapture.IsSupported)
                return (false, -1, string.Format(L10n.T("Phone.WindowsLinkAudioUnavailable"), Environment.MachineName));
            if (Service is not { IsEnabled: true } service)
                return (false, -1, L10n.T("Phone.StatusOperationFailed"));
            try
            {
                string? existingPeer = service.FindOutgoingAudioPeerForProcess(processId);
                if (existingPeer != null && !string.Equals(existingPeer, peerId, StringComparison.OrdinalIgnoreCase))
                    await service.StopOutgoingAudioForProcessAsync(processId).ConfigureAwait(false);

                var app = AudioService.GetApps(false).FirstOrDefault(a => !a.IsExternal && a.ProcessId == (uint)processId);
                string processName = app?.ProcessName ?? TryProcessName(processId) ?? $"pid:{processId}";
                string label = app != null ? AppDisplayName.Get(app) : processName;
                byte[]? iconPng = GetOrEncodeLocalIcon(processId, processName, app?.IconPath);
                await service.SendAudioAsync(peerId, processId, label, appKey: processName, iconPng: iconPng,
                    destinationOnly: destinationOnly).ConfigureAwait(false);
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

    internal static async Task PullAudioAsync(LinkPeer peer, LinkRemoteApp app, CancellationToken token = default, bool destinationOnly = false)
    {
        var service = EnsureService();
        await service.EnableAsync(token).ConfigureAwait(false);
        var current = service.Peers.FirstOrDefault(p => string.Equals(p.Id, peer.Id, StringComparison.OrdinalIgnoreCase));
        if (current is { Connected: true } && !CanPullAudio(current, app))
            throw new NotSupportedException(string.Format(L10n.T("Phone.WindowsLinkAudioUnavailable"), current.DisplayName));
        await service.RequestPullAudioAsync(peer.Id, app, token, destinationOnly).ConfigureAwait(false);
    }

    internal static async Task ReconnectPeerAsync(string peerId, CancellationToken token)
    {
        var service = EnsureService();
        if (!service.Peers.Any(peer => peer.Trusted && string.Equals(peer.Id, peerId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(L10n.T("Phone.Err.PhoneRemovedAddFirst"));
        try { await service.ReconnectPeerAsync(peerId, token).ConfigureAwait(false); }
        catch (IOException ex)
        {
            ExceptionDiagnostics.Record("WindowsLink.Reconnect", ex);
            throw new IOException(L10n.T("Phone.WindowsLinkReconnectFailed"), ex);
        }
    }

    internal static async Task OpenDisplayAsync(WindowsLinkAppDrag payload, int x, int y, CancellationToken token = default)
    {
        var service = EnsureService();
        await service.EnableAsync(token).ConfigureAwait(false);
        await service.RequestPullWindowAsync(payload.Peer.Id, payload.App, x, y, token).ConfigureAwait(false);
    }

    internal static async Task ReturnToPeerAsync(string peerId, string appKey, bool window = false)
    {
        if (Service is not { } service) return;
        await service.StopPeerAppStreamsAsync(peerId, appKey, window).ConfigureAwait(false);
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
            service.RemoteCatalogChanged += id => RemoteCatalogChanged?.Invoke(id);
            service.PairingRequested += PairingRequested;
            service.PairingEnded += PairingEnded;
            service.Error += error => ExceptionDiagnostics.Record("WindowsLink.Media", error);
            _service = service;
        }
        return service;
    }

    private static WindowsLinkService CreateService()
    {
        string stateDirectory = Path.Combine(
            Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonicRoute"),
            "WindowsLink");
        return new WindowsLinkService(
            stateDirectory,
            localCatalogProvider: BuildLocalCatalogAsync,
            audioPlayerFactory: CreatePlayer,
            acquireLocalSilence: AndroidAudioHost.AcquireWindowsLinkSilenceLease);
    }

    private static IPcmPlayer CreatePlayer(AndroidPcmSource pcm, string? endpoint, CancellationToken token) =>
        AndroidOutputIdentity.IsPhoneOutput(endpoint)
            ? AndroidAudioHost.CreatePhonePcmPlayer(pcm, endpoint!, token)
            : new WasapiPcmPlayer(pcm, endpoint);

    private static async Task<LinkRemoteApp[]> BuildLocalCatalogAsync(bool audioOnly, CancellationToken token)
    {
        var audioApps = await AudioService.GetAppsAsync(refresh: false).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        int selfPid = Environment.ProcessId;
        var hiddenApps = ConfigService.GetHiddenPanelAppsSnapshot();

        var byKey = new Dictionary<string, LinkRemoteApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in audioApps)
        {
            token.ThrowIfCancellationRequested();
            if (app.IsExternal || app.ProcessId == 0 || app.ProcessId == selfPid
                || string.IsNullOrWhiteSpace(app.ProcessName) || hiddenApps.Contains(app.ProcessName)) continue;
            int pid = (int)app.ProcessId;
            string procName = app.ProcessName;
            string label = AppDisplayName.Get(app);
            byte[]? icon = GetOrEncodeLocalIcon(pid, procName, app.IconPath);
            if (!byKey.TryGetValue(procName, out var existing))
            {
                byKey[procName] = new LinkRemoteApp(procName, label, procName, pid, app.HasActiveSession,
                    false, 0, null, icon) { HasAudioSession = true };
            }
            else if (app.HasActiveSession && !existing.IsPlaying)
            {
                byKey[procName] = existing with { ProcessId = pid, IsPlaying = true, IconPng = icon ?? existing.IconPng };
            }
        }

        if (audioOnly) return byKey.Values.ToArray();

        foreach (var w in LinkWindowCapture.GetWindows())
        {
            if (w.ProcessId <= 0 || w.ProcessId == selfPid) continue;
            string? processName = TryProcessName(w.ProcessId);
            // If a process cannot be identified, its window cannot be checked against the exclusion list.
            if (processName == null ? hiddenApps.Count != 0 : hiddenApps.Contains(processName)) continue;
            string procName = processName ?? $"pid:{w.ProcessId}";
            if (byKey.TryGetValue(procName, out var existing))
            {
                if (!existing.HasWindow)
                    byKey[procName] = existing with { HasWindow = true, WindowHandle = w.Handle, WindowTitle = w.Name };
            }
            else
            {
                byte[]? icon = GetOrEncodeLocalIcon(w.ProcessId, procName, null);
                byKey[procName] = new LinkRemoteApp(procName, w.Name, procName, w.ProcessId, false,
                    true, w.Handle, w.Name, icon) { HasAudioSession = false };
            }
        }

        return byKey.Values
            .OrderByDescending(a => a.IsPlaying)
            .ThenByDescending(a => a.HasWindow)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(48)
            .ToArray();
    }

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

    private static void ServiceChanged()
    {
        bool peersChanged, outputsChanged, mediaChanged;
        lock (Gate)
        {
            if (_service == null) return;
            var peers = _service.Peers;
            bool enabled = _service.IsEnabled;
            var streams = _service.Streams;

            string[] connectedOutputs = enabled
                ? peers.Where(p => p.Connected && (p.Capabilities & LinkCapabilities.AudioReceive) != 0)
                    .Select(p => $"{p.Id}:{p.DisplayName}").ToArray()
                : [];
            string[] incoming = _service.IncomingAudioSources
                .Select(s =>
                {
                    var st = s.Session.State;
                    return $"{s.Id}:{s.Name}:{st.State}:{st.Volume}:{st.Muted}:{st.OutputId}";
                }).ToArray();
            string[] outgoing = streams
                .Where(s => s.Kind == LinkMediaKind.Window || !s.Incoming && s.Kind == LinkMediaKind.AppAudio)
                .Select(s => $"{s.Id}:{s.PeerId}:{s.ProcessId}:{s.DestinationOnly}").ToArray();

            peersChanged = _lastEnabled != enabled || !_lastPeers.SequenceEqual(peers);
            outputsChanged = !_lastConnectedOutputs.SequenceEqual(connectedOutputs);
            mediaChanged = !_lastIncoming.SequenceEqual(incoming) || !_lastOutgoing.SequenceEqual(outgoing);

            _lastEnabled = enabled;
            _lastPeers = peers;
            _lastConnectedOutputs = connectedOutputs;
            _lastIncoming = incoming;
            _lastOutgoing = outgoing;
        }

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
        lock (Gate)
        {
            service = _service;
            _service = null;
            PendingPairings.Clear();
            _lastPeers = [];
            _lastConnectedOutputs = [];
            _lastIncoming = [];
            _lastOutgoing = [];
            _lastEnabled = false;
        }
        IconPngCache.Clear();
        if (service == null) return;
        service.Changed -= ServiceChanged;
        service.PairingRequested -= PairingRequested;
        service.PairingEnded -= PairingEnded;
        await service.DisposeAsync().ConfigureAwait(false);
    }
}
#endif

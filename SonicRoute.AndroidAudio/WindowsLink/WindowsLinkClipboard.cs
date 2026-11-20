using System.Diagnostics;
using System.Text;

namespace SonicRoute.WindowsLink;

internal sealed record LinkClipboardState(bool Enabled, bool PeerEnabled, bool Supported, bool Connected)
{
    internal bool Active => Enabled && PeerEnabled && Supported && Connected;
}

internal readonly record struct LinkClipboardRoute(string PeerId, Guid Local, Guid Remote);

internal sealed partial class WindowsLinkService
{
    internal const int MaxClipboardBytes = LinkConnection.MaxAudioPacket - 16;
    private static readonly UTF8Encoding ClipboardEncoding = new(false, true);
    internal event Action<string>? ClipboardStateChanged;
    internal event Action<LinkClipboardRoute, string>? ClipboardTextReceived;

    internal LinkClipboardState GetClipboardState(string peerId)
    {
        lock (_gate)
        {
            LoadSavedPeers();
            var saved = _saved.GetValueOrDefault(peerId);
            var link = _connections.GetValueOrDefault(peerId);
            return new(saved?.ClipboardEnabled == true, link != null && link.RemoteClipboardConsent != Guid.Empty,
                link?.Hello.SupportsClipboardSync ?? saved?.SupportsClipboardSync ?? false, _run != null && link?.IsOpen == true);
        }
    }

    internal void SetClipboardEnabled(string peerId, bool enabled)
    {
        lock (_gate)
        {
            LoadSavedPeers();
            if (!_saved.TryGetValue(peerId, out var saved) || saved.ClipboardEnabled == enabled) return;
            var link = _connections.GetValueOrDefault(peerId);
            if (enabled && !(link?.Hello.SupportsClipboardSync ?? saved.SupportsClipboardSync))
                throw new NotSupportedException("This computer does not support clipboard sync.");
            SavePeer(peerId, saved with { ClipboardEnabled = enabled });
            if (link is { IsOpen: true } && link.Hello.SupportsClipboardSync) SetClipboardConsentLocked(link, enabled);
        }
        NotifyClipboardState(peerId);
    }

    private static void SetClipboardConsentLocked(LinkConnection link, bool enabled)
    {
        link.LocalClipboardConsent = enabled ? Guid.NewGuid() : Guid.Empty;
        _ = AnnounceConsentAsync(link, link.LocalClipboardConsent);
    }

    private static async Task AnnounceConsentAsync(LinkConnection link, Guid consent)
    {
        try { await link.SendAsync(LinkMessage.ClipboardState, consent, []).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Closing the connection also invalidates both halves of a grant whose update could not be sent.
            link.Abort();
        }
    }

    internal LinkClipboardRoute[] ClipboardRoutes()
    {
        lock (_gate)
        {
            if (_run == null) return [];
            return _connections.Values.Where(l => l.IsOpen && l.LocalClipboardConsent != Guid.Empty && l.RemoteClipboardConsent != Guid.Empty)
                .Select(l => new LinkClipboardRoute(l.Id, l.LocalClipboardConsent, l.RemoteClipboardConsent)).ToArray();
        }
    }

    internal bool CanSyncClipboard(LinkClipboardRoute route)
    {
        lock (_gate) return MatchesClipboardRoute(_connections.GetValueOrDefault(route.PeerId), route);
    }

    private bool MatchesClipboardRoute(LinkConnection? link, LinkClipboardRoute route) =>
        _run != null && link is { IsOpen: true } && route.Local != Guid.Empty && route.Remote != Guid.Empty
        && link.LocalClipboardConsent == route.Local && link.RemoteClipboardConsent == route.Remote;

    internal Task SendClipboardTextAsync(LinkClipboardRoute route, string text)
    {
        if (text.Length is 0 or > MaxClipboardBytes || text.Contains('\0')) return Task.CompletedTask;
        int length;
        try { length = ClipboardEncoding.GetByteCount(text); }
        catch (EncoderFallbackException) { return Task.CompletedTask; }
        if (length > MaxClipboardBytes) return Task.CompletedTask;
        lock (_gate)
        {
            var link = _connections.GetValueOrDefault(route.PeerId);
            if (!MatchesClipboardRoute(link, route)) return Task.CompletedTask;
            byte[] data = new byte[16 + length];
            route.Local.TryWriteBytes(data);
            ClipboardEncoding.GetBytes(text, data.AsSpan(16));
            return link!.SendAsync(LinkMessage.ClipboardText, route.Remote, data);
        }
    }

    private void DispatchClipboard(LinkConnection link, LinkMessage type, Guid id, ReadOnlySpan<byte> data)
    {
        LinkClipboardRoute route;
        lock (_gate)
        {
            if (_run == null || _connections.GetValueOrDefault(link.Id) != link || !link.Hello.SupportsClipboardSync) return;
            if (type == LinkMessage.ClipboardState)
            {
                if (link.RemoteClipboardConsent == id) return;
                link.RemoteClipboardConsent = id;
                route = default;
            }
            else
            {
                route = new(link.Id, id, new Guid(data[..16]));
                if (!MatchesClipboardRoute(link, route)) return;
            }
        }
        if (type == LinkMessage.ClipboardState) { NotifyClipboardState(link.Id); return; }
        string text;
        try { text = ClipboardEncoding.GetString(data[16..]); }
        catch (DecoderFallbackException) { return; }
        if (!text.Contains('\0')) ClipboardTextReceived?.Invoke(route, text);
    }

    private void NotifyClipboardState(string peerId)
    {
        try { ClipboardStateChanged?.Invoke(peerId); }
        catch (Exception ex) { Trace.WriteLine(ex.GetType().Name); }
    }
}

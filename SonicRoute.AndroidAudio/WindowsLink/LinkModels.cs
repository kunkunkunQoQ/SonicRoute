using System.Net;

namespace SonicRoute.WindowsLink;

[Flags]
internal enum LinkCapabilities : ushort
{
    None = 0,
    AudioReceive = 1,
    SystemAudio = 2,
    ProcessAudio = 4,
    WindowReceive = 8,
    WindowCapture = 16,
    WindowInput = 32,
    RemoteBrowse = 64,
    AudioReady = 128,
    DestinationOnly = 256,
    AudioCatalog = 512
}

internal enum LinkMediaKind : byte
{
    SystemAudio = 1,
    AppAudio = 2,
    Window = 3
}

internal sealed record LinkPeer(string Id, string Name, string? Alias, string? Address,
    bool Trusted, bool Online, bool Connected, LinkCapabilities Capabilities)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;
}

internal sealed record LinkPairing(Guid Id, string Name, string Code);
internal sealed record LinkWindow(nint Handle, string Name, int ProcessId);

internal sealed record LinkMedia(Guid Id, string PeerId, LinkMediaKind Kind, string Name,
    bool Incoming, float Volume, bool Muted, string? OutputId, bool AllowsInput,
    string? AppKey = null, int? ProcessId = null, byte[]? IconPng = null, bool DestinationOnly = false);

internal sealed record LinkRemoteApp(
    string Key,
    string Name,
    string ProcessName,
    int ProcessId,
    bool IsPlaying,
    bool HasWindow,
    long WindowHandle,
    string? WindowTitle,
    byte[]? IconPng)
{
    public bool? HasAudioSession { get; init; }

    internal const string SystemAudioKey = "sonicroute:system-audio";

    internal static LinkRemoteApp SystemAudio(string deviceName) =>
        new(SystemAudioKey, deviceName, SystemAudioKey, 0, false, false, 0, null, null);

    internal bool IsSystemAudio => string.Equals(Key, SystemAudioKey, StringComparison.OrdinalIgnoreCase);
}

internal sealed record LinkPinnedApp(
    string Key,
    string Name,
    string ProcessName,
    string? IconPngBase64 = null,
    bool SmartVolume = true);

internal sealed record LinkPeerSettings(
    bool AllowRemoteInput = false,
    string? VideoDecoder = null,
    int VideoBitRate = 8_000_000,
    int VideoMaxFps = 30,
    int VideoMaxHeight = 1080)
{
    internal LinkPeerSettings Normalize() => this with
    {
        VideoDecoder = VideoDecoder is AndroidAudio.AndroidStreamSettings.WindowsVideoDecoder
            or AndroidAudio.AndroidStreamSettings.SoftwareVideoDecoder ? VideoDecoder : null,
        VideoBitRate = VideoBitRate is >= 1_000_000 and <= 50_000_000 ? VideoBitRate : 8_000_000,
        VideoMaxFps = VideoMaxFps is >= 15 and <= 60 ? VideoMaxFps : 30,
        VideoMaxHeight = VideoMaxHeight is >= 360 and <= 1080 ? VideoMaxHeight : 1080
    };

    internal LinkVideoOptions VideoOptions()
    {
        var normalized = Normalize();
        return new LinkVideoOptions(normalized.VideoMaxHeight, normalized.VideoMaxFps, normalized.VideoBitRate);
    }
}

internal sealed record LinkVideoOptions(int MaxHeight, int MaxFps, int BitRate)
{
    internal static readonly LinkVideoOptions Default = new(1080, 30, 8_000_000);

    internal LinkVideoOptions Clamp() => new(
        MaxHeight is >= 360 and <= 1080 ? MaxHeight : 1080,
        MaxFps is >= 15 and <= 60 ? MaxFps : 30,
        BitRate is >= 1_000_000 and <= 50_000_000 ? BitRate : 8_000_000);
}

internal sealed record LinkPullRequest(
    LinkMediaKind Kind,
    string? AppKey,
    int? ProcessId,
    long? WindowHandle,
    int VideoBitRate = 0,
    int VideoMaxFps = 0,
    int VideoMaxHeight = 0,
    bool DestinationOnly = false);

internal sealed record LinkPullResponse(bool Accepted, Guid StreamId, string? Error);

internal static class WindowsLinkOutputIdentity
{
    internal const string Prefix = "winlink-out:";

    internal static bool IsWindowsLinkOutput(string? deviceId) =>
        !string.IsNullOrEmpty(deviceId) && deviceId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    internal static string FormatDeviceId(string peerId) => Prefix + peerId;

    internal static bool TryParsePeerId(string? deviceId, out string peerId)
    {
        peerId = "";
        if (!IsWindowsLinkOutput(deviceId)) return false;
        peerId = deviceId![Prefix.Length..];
        return LinkSecurity.IsId(peerId);
    }
}

internal delegate void LinkBgraFrame(ReadOnlySpan<byte> pixels, int width, int height, int stride);

internal enum LinkMessage : byte
{
    Open = 1,
    Close = 2,
    Audio = 3,
    Video = 4,
    Volume = 5,
    Input = 6,
    Ping = 7,
    Pong = 8,
    Bye = 9,
    CatalogRequest = 10,
    CatalogResponse = 11,
    PullRequest = 12,
    PullResponse = 13,
    AudioReady = 14,
    CatalogChanged = 15
}

internal sealed record LinkFrame(LinkMessage Type, Guid StreamId, byte[] Payload);

internal sealed record LinkOffer(string Id, string Name, IPEndPoint Endpoint, LinkCapabilities Capabilities);
internal sealed record LinkReconnectRequest(string Id, IPEndPoint Endpoint, long Issued);

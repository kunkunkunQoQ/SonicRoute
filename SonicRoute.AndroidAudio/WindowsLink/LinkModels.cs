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
    AudioCatalog = 512,
    HevcEncode = 1024,
    HevcDecode = 2048,
    FileTransfer = 4096,
    MonitorCapture = 8192,
    WindowAudio = 16384,
    FullTrust = 32768
}

[Flags]
internal enum WindowsLinkExclusiveMode { Unavailable = 0, DedicatedOutput = 1, MuteSourceSpeaker = 2, DedicatedOrSpeaker = DedicatedOutput | MuteSourceSpeaker }

internal enum LinkMediaKind : byte
{
    SystemAudio = 1,
    AppAudio = 2,
    Window = 3
}

internal enum LinkPeerState
{
    Discovered,
    Offline,
    Online,
    Connecting,
    Connected,
    DisconnectedByUser,
    Failed
}

internal enum LinkConnectFailure
{
    None,
    DiscoveryTimeout,
    TcpUnreachable,
    PairingRejected,
    IdentityMismatch,
    SavedAddressUnavailable,
    Incompatible,
    Unknown
}

internal sealed record LinkPeer(string Id, string Name, string? Alias, string? Address,
    bool Trusted, bool Online, bool Connected, LinkCapabilities Capabilities)
{
    public LinkPeerState State { get; init; } = Connected ? LinkPeerState.Connected
        : Online ? (Trusted ? LinkPeerState.Online : LinkPeerState.Discovered) : LinkPeerState.Offline;
    public LinkConnectFailure Failure { get; init; }
    public bool SupportsNativeVideo { get; init; }
    public bool SupportsFileBrowse { get; init; }
    public bool SupportsSourcePlayback { get; init; }
    public bool SupportsAudioRelay { get; init; }
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;
}

internal sealed record LinkPairing(Guid Id, string Name, string Code);
internal abstract record LinkCaptureTarget(nint Handle, string Name)
{
    internal abstract string InstanceId { get; }
}

internal interface ILinkInput : IDisposable
{
    void Apply(ReadOnlySpan<byte> packet);
}

internal sealed record LinkWindow(nint Handle, string Name, int ProcessId) : LinkCaptureTarget(Handle, Name)
{
    internal string? ProcessName { get; init; }
    internal uint ThreadId { get; init; }
    internal long ProcessStartTicks { get; init; }
    internal override string InstanceId => FormattableString.Invariant($"w:{Handle.ToInt64():x}:{ProcessId:x}:{ThreadId:x}:{ProcessStartTicks:x}");
}

internal sealed record LinkRemoteDisplay(int Number, int Width, int Height, bool IsPrimary);

internal sealed record LinkMonitor(nint Handle, string Name, string DeviceId, LinkRemoteDisplay Display) : LinkCaptureTarget(Handle, Name)
{
    internal string Key => LinkRemoteApp.MonitorKeyPrefix + Name;
    internal override string InstanceId => FormattableString.Invariant($"m:{Handle.ToInt64():x}:{DeviceId}");
    internal LinkRemoteApp ToCatalogEntry() => new(Key, Name, "", 0, false, false, 0, null, null)
        { Display = Display, InstanceId = InstanceId, HasAudioSession = false };
}

internal enum LinkCatalogScope { Apps, Audio, Windows }

internal sealed record LinkMedia(Guid Id, string PeerId, LinkMediaKind Kind, string Name,
    bool Incoming, float Volume, bool Muted, string? OutputId, bool AllowsInput,
    string? AppKey = null, int? ProcessId = null, byte[]? IconPng = null, bool DestinationOnly = false)
{
    internal bool IsRelayed { get; init; }
    internal string? InstanceId { get; init; }
    internal bool Hevc { get; init; }
    internal bool NativeResolution { get; init; }
    internal int VideoWidth { get; init; }
    internal int VideoHeight { get; init; }
}

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
    public bool? CanPlayExclusively { get; init; }
    public bool CanMuteSourceSpeaker { get; init; }
    public string? InstanceId { get; init; }
    public LinkRemoteDisplay? Display { get; init; }
    internal bool IsMonitor => Display != null;
    internal const string MonitorKeyPrefix = "sonicroute:monitor:";

    internal bool SupportsExclusivePlayback(LinkCapabilities capabilities) =>
        !IsSystemAudio && (CanPlayExclusively ?? capabilities.HasFlag(LinkCapabilities.DestinationOnly));

    internal static string AudioInstanceId(int pid, long processStartTicks) =>
        FormattableString.Invariant($"a:{pid:x}:{processStartTicks:x}");

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
    int VideoMaxHeight = 1080,
    string? VideoEncoder = null)
{
    internal LinkPeerSettings Normalize() => this with
    {
        VideoDecoder = VideoDecoder is AndroidAudio.AndroidStreamSettings.WindowsVideoDecoder
            or AndroidAudio.AndroidStreamSettings.SoftwareVideoDecoder or AndroidAudio.AndroidStreamSettings.WindowsHevcDecoder ? VideoDecoder : null,
        VideoEncoder = VideoEncoder is "h264" or "h265" ? VideoEncoder : null,
        VideoBitRate = VideoBitRate is >= 1_000_000 and <= 50_000_000 ? VideoBitRate : 8_000_000,
        VideoMaxFps = VideoMaxFps is >= 15 and <= 60 ? VideoMaxFps : 30,
        VideoMaxHeight = LinkVideoOptions.NormalizeHeight(VideoMaxHeight)
    };

    // A missing request field means a legacy H.264 receiver. "auto" permits the sender
    // to apply its saved encoder preference, while still defaulting to H.264.
    internal string RequestedVideoCodec(LinkCapabilities local, LinkCapabilities remote)
    {
        if (!local.HasFlag(LinkCapabilities.HevcDecode) || !remote.HasFlag(LinkCapabilities.HevcEncode)
            || VideoDecoder is AndroidAudio.AndroidStreamSettings.SoftwareVideoDecoder or AndroidAudio.AndroidStreamSettings.WindowsVideoDecoder
            || VideoEncoder == "h264") return "h264";
        return VideoDecoder == AndroidAudio.AndroidStreamSettings.WindowsHevcDecoder || VideoEncoder == "h265" ? "h265" : "auto";
    }

    internal LinkVideoOptions VideoOptions()
    {
        var normalized = Normalize();
        return new LinkVideoOptions(normalized.VideoMaxHeight, normalized.VideoMaxFps, normalized.VideoBitRate, normalized.VideoEncoder);
    }
}

internal sealed record LinkVideoOptions(int MaxHeight, int MaxFps, int BitRate, string? Encoder = null)
{
    internal static readonly LinkVideoOptions Default = new(1080, 30, 8_000_000);
    internal bool IsNative => MaxHeight == 0;
    internal const int MaxFramePixels = 16_777_216;
    internal const int MaxFrameSide = 8192;
    internal static int NormalizeHeight(int height) => height is 0 or (>= 360 and <= 1080) ? height : 1080;
    internal static bool ValidFrameSize(int width, int height) =>
        width is >= 16 and <= MaxFrameSide && height is >= 16 and <= MaxFrameSide
        && ((width | height) & 1) == 0 && (long)width * height <= MaxFramePixels;
    // The Windows HEVC transform can terminate the process on an out-of-range portrait height.
    internal static bool ValidHevcFrameSize(int width, int height) =>
        ValidFrameSize(width, height) && width <= 4096 && height <= 2304;

    internal void RequirePeerSupport(LinkHello hello)
    {
        if (IsNative && !hello.SupportsNativeVideo)
            throw new NotSupportedException("Native resolution requires an updated Windows Link peer. Select 1080p or update the other computer.");
    }

    internal (int Width, int Height) FrameSize(int sourceWidth, int sourceHeight, bool reduced)
    {
        if (IsNative)
        {
            int width = sourceWidth & ~1, height = sourceHeight & ~1;
            if (!ValidFrameSize(width, height))
                throw new NotSupportedException($"Windows Link cannot encode the native frame size {sourceWidth} × {sourceHeight}.");
            return (width, height);
        }
        int maxWidth = MaxHeight >= 1080 ? 1920 : 1280;
        double scale = Math.Min(1d, Math.Min((reduced ? 640 : maxWidth) / (double)sourceWidth,
            (reduced ? 360 : MaxHeight) / (double)sourceHeight));
        return (Math.Max(16, (int)(sourceWidth * scale) & ~1), Math.Max(16, (int)(sourceHeight * scale) & ~1));
    }

    internal LinkVideoOptions Clamp() => new(
        NormalizeHeight(MaxHeight),
        MaxFps is >= 15 and <= 60 ? MaxFps : 30,
        BitRate is >= 1_000_000 and <= 50_000_000 ? BitRate : 8_000_000,
        Encoder is "h265" or "h264" ? Encoder : null);
}

internal sealed record LinkPullRequest(
    LinkMediaKind Kind,
    string? AppKey,
    int? ProcessId,
    long? WindowHandle,
    int VideoBitRate = 0,
    int VideoMaxFps = 0,
    int VideoMaxHeight = 0,
    bool DestinationOnly = false,
    string? InstanceId = null,
    string? VideoCodec = null,
    Guid? WindowStream = null,
    bool AllowSpeakerMute = false,
    bool NativeResolution = false)
{
    internal LinkVideoOptions VideoOptions(LinkHello hello, string? encoder)
    {
        // Prior versions sent zero for an omitted preset. Native mode requires an explicit request.
        if (NativeResolution && (VideoMaxHeight != 0 || !hello.SupportsNativeVideo))
            throw new InvalidDataException("Invalid native Windows Link resolution request.");
        return new LinkVideoOptions(VideoMaxHeight == 0 && !NativeResolution ? 1080 : VideoMaxHeight,
            VideoMaxFps, VideoBitRate, encoder).Clamp();
    }
}

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
    CatalogChanged = 15,
    FileOffer = 16,
    FileAccept = 17,
    FileReject = 18,
    FileChunk = 19,
    FileAck = 20,
    FileEnd = 21,
    FileComplete = 22,
    FileCancel = 23,
    FileError = 24,
    TrustState = 25,
    TrustBegin = 26,
    TrustTry = 27,
    TrustAnswer = 28,
    TrustCancel = 29,
    TrustRevoke = 30,
    FolderOffer = 31,
    BrowseOpen = 32,
    BrowseList = 33,
    BrowseDownload = 34,
    BrowseReply = 35,
    BrowseClose = 36,
    BrowseCancel = 37,
    BrowsePreparing = 38,
    ClipboardState = 39,
    ClipboardText = 40,
    SourcePlayback = 41,
    SourcePlaybackReply = 42
}

internal sealed record LinkFrame(LinkMessage Type, Guid StreamId, byte[] Payload);

internal sealed record LinkOffer(string Id, string Name, IPEndPoint Endpoint, LinkCapabilities Capabilities);
internal sealed record LinkReconnectRequest(string Id, IPEndPoint Endpoint, long Issued);

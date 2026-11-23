using System.Text.Json;

namespace SonicRoute.AndroidAudio;

public enum AndroidOutputSinkKind { Speaker, Bluetooth, Current }

public sealed record AndroidOutputSink(
    string Key,
    AndroidOutputSinkKind Kind,
    int AndroidDeviceId,
    int AndroidDeviceType,
    string Name,
    string? Address = null)
{
    public static readonly AndroidOutputSink DefaultSpeaker = new("speaker", AndroidOutputSinkKind.Speaker, 0, 2, "手机扬声器");
    public static readonly AndroidOutputSink Current = new("current", AndroidOutputSinkKind.Current, 0, 0, "手机当前输出");

    public static IReadOnlyList<AndroidOutputSink> ParseList(JsonElement element)
    {
        var raw = new List<AndroidOutputSink>();
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray().Take(16))
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string key = item.TryGetProperty("key", out var k) ? (k.GetString() ?? "").Trim() : "";
                if (key.Length is 0 or > 128) continue;
                if (string.Equals(key, Current.Key, StringComparison.OrdinalIgnoreCase)) continue;
                string kindText = item.TryGetProperty("kind", out var kd) ? (kd.GetString() ?? "").Trim() : "";
                var kind = string.Equals(kindText, "bluetooth", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("bt:", StringComparison.OrdinalIgnoreCase)
                    ? AndroidOutputSinkKind.Bluetooth
                    : AndroidOutputSinkKind.Speaker;
                if (kind == AndroidOutputSinkKind.Speaker) key = "speaker";
                int id = item.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out int parsedId) ? parsedId : 0;
                int type = item.TryGetProperty("type", out var typeProp) && typeProp.TryGetInt32(out int parsedType) ? parsedType : (kind == AndroidOutputSinkKind.Speaker ? 2 : 8);
                string rawName = item.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "";
                if (rawName.Length > 96) rawName = rawName[..96];
                string name = kind == AndroidOutputSinkKind.Speaker
                    ? "手机扬声器"
                    : (string.IsNullOrWhiteSpace(rawName) ? "蓝牙音频设备" : rawName);
                string? address = item.TryGetProperty("address", out var addr) ? (addr.GetString() ?? "").Trim() : null;
                if (string.IsNullOrEmpty(address) || string.Equals(address, "00:00:00:00:00:00", StringComparison.OrdinalIgnoreCase))
                    address = null;
                else if (address.Length > 64)
                    address = address[..64];
                raw.Add(new AndroidOutputSink(key, kind, id, type, name, address));
            }
        }
        return NormalizeList(raw);
    }

    public static IReadOnlyList<AndroidOutputSink> NormalizeList(IEnumerable<AndroidOutputSink>? outputs)
    {
        var list = new List<AndroidOutputSink>();
        if (outputs != null)
        {
            foreach (var sink in outputs)
            {
                if (sink == null || sink.Kind == AndroidOutputSinkKind.Current || string.IsNullOrWhiteSpace(sink.Key)) continue;
                string? address = string.IsNullOrWhiteSpace(sink.Address)
                    || string.Equals(sink.Address.Trim(), "00:00:00:00:00:00", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : sink.Address.Trim();
                var normalized = sink.Kind == AndroidOutputSinkKind.Speaker
                    ? sink with { Key = "speaker", Name = string.IsNullOrWhiteSpace(sink.Name) ? "手机扬声器" : sink.Name.Trim(), Address = null }
                    : sink with
                    {
                        Key = sink.Key.Trim(),
                        Name = string.IsNullOrWhiteSpace(sink.Name) ? "蓝牙音频设备" : sink.Name.Trim(),
                        Address = address
                    };

                if (normalized.Kind == AndroidOutputSinkKind.Speaker)
                {
                    if (!list.Any(s => s.Kind == AndroidOutputSinkKind.Speaker))
                        list.Insert(0, normalized);
                    continue;
                }

                int existingIdx = list.FindIndex(s =>
                    s.Kind == AndroidOutputSinkKind.Bluetooth &&
                    (string.Equals(s.Key, normalized.Key, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(s.Name, normalized.Name, StringComparison.OrdinalIgnoreCase)));
                if (existingIdx >= 0)
                {
                    var existing = list[existingIdx];
                    bool preferNew = (existing.AndroidDeviceType == 7 && normalized.AndroidDeviceType != 7)
                        || (existing.Address == null && normalized.Address != null && normalized.AndroidDeviceType != 7);
                    if (preferNew)
                        list[existingIdx] = normalized;
                    continue;
                }
                list.Add(normalized);
            }
        }
        if (!list.Any(s => s.Kind == AndroidOutputSinkKind.Speaker))
            list.Insert(0, DefaultSpeaker);
        return Array.AsReadOnly(list.ToArray());
    }
}

public static class AndroidOutputIdentity
{
    public const string Prefix = "android-out:";

    public static bool IsPhoneOutput(string? deviceId) =>
        !string.IsNullOrEmpty(deviceId) && deviceId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsPhoneBluetoothOutput(string? deviceId) =>
        TryParseDeviceId(deviceId, out _, out string sinkKey) && sinkKey.StartsWith("bt:", StringComparison.OrdinalIgnoreCase);

    public static bool IsPhoneCurrentOutput(string? deviceId) =>
        TryParseDeviceId(deviceId, out _, out string sinkKey) && string.Equals(sinkKey, AndroidOutputSink.Current.Key, StringComparison.OrdinalIgnoreCase);

    public static string FormatDeviceId(string phoneKey, string sinkKey) =>
        $"{Prefix}{phoneKey}:{sinkKey}";

    public static bool TryParseDeviceId(string? deviceId, out string phoneKey, out string sinkKey)
    {
        phoneKey = "";
        sinkKey = "";
        if (!IsPhoneOutput(deviceId)) return false;
        string rest = deviceId![Prefix.Length..];
        if (rest.EndsWith(":speaker", StringComparison.OrdinalIgnoreCase) && rest.Length > 8)
        {
            phoneKey = rest[..^8];
            sinkKey = "speaker";
            return true;
        }
        int btIndex = rest.IndexOf(":bt:", StringComparison.OrdinalIgnoreCase);
        if (btIndex > 0 && btIndex + 4 < rest.Length)
        {
            phoneKey = rest[..btIndex];
            sinkKey = rest[(btIndex + 1)..];
            return true;
        }
        int lastColon = rest.LastIndexOf(':');
        if (lastColon <= 0 || lastColon >= rest.Length - 1) return false;
        phoneKey = rest[..lastColon];
        sinkKey = rest[(lastColon + 1)..];
        return true;
    }
}

public sealed record AndroidDevice(string Serial, string Name, string State, bool IsWifi, string? HardwareId = null, string? TransportId = null)
{
    // Secondary evidence distinguishes devices that report the same hardware serial.
    public string? IdentityFingerprint { get; init; }
    // A process-local transport selector, never persisted or reconstructed from a saved profile.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ConnectionTarget { get; init; }
    // Windows PnP identifies an attachment by its interface path, independently of the ADB serial.
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? UsbInterfacePath { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string CommandTarget => ConnectionTarget ?? Serial;
    public override string ToString() => State is "device" or "unauthorized"
        ? $"{Name} · {(IsWifi ? "Wi-Fi" : "USB")}" + (State == "unauthorized" ? " · 待授权" : "")
        : $"{Name} · 未连接";
}

public static class AndroidDeviceIdentity
{
    // A removal must match the specific connection, even when a phone reuses its address or USB path.
    internal static bool SameAttachment(AndroidDevice first, AndroidDevice second) => ReferenceEquals(first, second)
        || first.ConnectionTarget != null && first.ConnectionTarget == second.ConnectionTarget;

    // Wireless endpoint ports and discovery suffixes may change. Never identify a phone by its model or IP.
    public static string Key(AndroidDevice device)
    {
        if (!string.IsNullOrWhiteSpace(device.HardwareId)) return "device:" + device.HardwareId;
        string serial = device.Serial;
        int suffix = serial.IndexOf("._adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase);
        if (suffix > 4 && serial.StartsWith("adb-", StringComparison.OrdinalIgnoreCase))
        {
            int separator = serial.LastIndexOf('-', suffix - 1);
            if (separator > 4) return "device:" + serial[4..separator];
        }
        return (device.IsWifi ? "endpoint:" : "device:") + serial;
    }
    public static bool Matches(AndroidDevice? left, AndroidDevice? right) => left != null && right != null
        && (SamePhysicalDevice(left, right) || SameTransport(left, right));

    internal static string? StableKey(AndroidDevice? device)
    {
        if (device == null) return null;
        string key = Key(device);
        return key.StartsWith("device:", StringComparison.Ordinal) && key.Length > "device:".Length ? key : null;
    }

    internal static bool ConflictingEvidence(AndroidDevice? left, AndroidDevice? right) => left != null && right != null
        && ((StableKey(left) is { } a && StableKey(right) is { } b && a != b)
            || (left.IdentityFingerprint is { } x && right.IdentityFingerprint is { } y && x != y));

    internal static bool SamePhysicalDevice(AndroidDevice? left, AndroidDevice? right) =>
        StableKey(left) is { } key && string.Equals(key, StableKey(right), StringComparison.Ordinal)
        && !ConflictingEvidence(left, right);

    internal static bool SameTransport(AndroidDevice? left, AndroidDevice? right) => left != null && right != null
        && left.Serial == right.Serial && left.TransportId == right.TransportId
        && !ConflictingEvidence(left, right)
        && (left.ConnectionTarget == null || right.ConnectionTarget == null || left.ConnectionTarget == right.ConnectionTarget);

    internal static bool MatchesSavedDevice(AndroidDevice? saved, AndroidDevice device)
    {
        if (saved == null || ConflictingEvidence(saved, device)) return false;
        string? savedKey = StableKey(saved), currentKey = StableKey(device);
        return savedKey != null && currentKey != null ? savedKey == currentKey : saved.Serial == device.Serial;
    }

    // A display/drag/profile may hold an older record. Resolve it against the shared
    // live list, preferring verified physical identity over an endpoint-only alias.
    internal static AndroidDevice? FindOnlineDevice(AndroidDevice? saved, IEnumerable<AndroidDevice>? devices)
    {
        if (saved == null || devices == null) return null;
        AndroidDevice? best = null;
        foreach (var candidate in devices)
        {
            if (candidate.State != "device" || !MatchesSavedDevice(saved, candidate)) continue;
            if (best == null || (SamePhysicalDevice(saved, candidate) && !SamePhysicalDevice(saved, best))
                || (SamePhysicalDevice(saved, candidate) == SamePhysicalDevice(saved, best)
                    && candidate.Serial == saved.Serial && best.Serial != saved.Serial)) best = candidate;
        }
        return best;
    }

    internal static AndroidDevice? WirelessServiceIdentity(AndroidMdnsService service) =>
        service.Type.Equals(AndroidWirelessConnection.ConnectType, StringComparison.OrdinalIgnoreCase)
            ? new AndroidDevice($"{service.Name}.{AndroidWirelessConnection.ConnectType}", "", "offline", true)
            : null;

    internal static bool MatchesWirelessService(AndroidDevice saved, AndroidMdnsService service)
    {
        if (!saved.IsWifi || WirelessServiceIdentity(service) is not { } advertised) return false;
        string serial = saved.Serial.TrimEnd('.');
        return serial.Equals(advertised.Serial, StringComparison.OrdinalIgnoreCase)
            || serial.Equals(advertised.Serial + ".local", StringComparison.OrdinalIgnoreCase)
            || Key(saved).Equals(Key(advertised), StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<AndroidDevice> Distinct(IReadOnlyList<AndroidDevice> devices) => devices
        .GroupBy(Key, StringComparer.Ordinal).SelectMany(group => group.Any(d =>
            group.Any(other => ConflictingEvidence(d, other)) || d.ConnectionTarget != null && AndroidConnectionManager.IsAmbiguous(d, devices))
            ? group : group.OrderByDescending(d => d.State == "device")
                .ThenByDescending(d => d.Serial.Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase)).Take(1)).ToArray();
}

public sealed record AndroidApp(string Package, string Name, int Uid, string Category, byte[]? IconPng = null, int PlatformCategory = -1)
{
    // A deliberate device source, never inferred from a missing app or unresolved UID.
    // ':' cannot occur in an Android package, so this identity cannot enter the app catalog.
    public const string DeviceAudioPackage = "sonicroute:device-audio";
    public static AndroidApp DeviceAudio { get; } = new(DeviceAudioPackage, "整机", -1, "device");
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDeviceAudio => Package == DeviceAudioPackage && Uid == -1;
    public override string ToString() => Name;
}

public enum CaptureState { Idle, Starting, Receiving, Stopping, Failed }

public enum AndroidErrorCode
{
    ServerVerifyFailed,
    MissingRuntime,
    AuthorizeAdbFirst,
    AppOrUidUnavailable,
    SharedUidUnsupported,
    AudioSessionNotStarted,
    AudioConnectionStopped,
    SelectSourcePhoneOutput,
    ClipboardMessageTooLong,
    ClipboardPermissionError,
    ClipboardDisconnected,
    ClipboardNotConnected,
    MediaConnectionEnded,
    MediaNotConnected,
    AudioPortForwardFailed,
    PhoneOffline,
    SelectedPhoneOutputDisconnected,
    PhoneAudioOutputStopped,
    PhoneAudioOutputFailed,
    PhoneAudioHelperExited,
    MaxSavedPhones,
    InvalidAddress,
    InvalidPairCode,
    PairFailed,
    QrExpired,
    ConnectFailed,
    ConnectNotReady,
    PairedButNotConnected,
    UsbReconnectHint,
    SavedWifiAddressExpired,
    DeviceNotConnectedWifiHint,
    DeviceNotReadyConfirmAuth,
    MissingAdbPublicKey,
    InvalidAdbPublicKey,
    UnpairPhoneOffline,
    UnpairIdentityUnavailable,
    UnpairNotConfirmed,
    UsbDisconnectHint,
    AppUidChanged,
    Android13Required,
    AudioServiceExited,
    AudioCaptureNotEstablished,
    MissingDisplayRuntime,
    InvalidDisplayRuntime,
    InvalidDisplayControlRuntime,
    DisplayPhoneNotConnected,
    DisplayAppUidChanged,
    DisplayStartFailed,
    DisplayOpenFailed,
    DisplayOpenTimeout,
    DisplayConnectionEnded,
    PhoneRemovedAddFirst,
    ReconnectNoDeviceFound,
    MaxClipboardPhones,
    InvalidProcessId,
    InvalidPhoneOutputId,
    InvalidPhoneOutputDevice,
    NoAudioRouteOrEnded,
    PairTimedOut,
    UsbInterfaceInUse,
    UsbInterfaceUnavailable,
    UnsupportedAudioFormat,
    InvalidAudioStream
}

public static class AndroidError
{
    private const string CodeKey = "AndroidErrorCode";

    public static T WithCode<T>(T exception, AndroidErrorCode code) where T : Exception
    {
        exception.Data[CodeKey] = (int)code;
        return exception;
    }

    public static bool TryGetCode(Exception? exception, out AndroidErrorCode code)
    {
        for (var cur = exception; cur != null; cur = cur.InnerException)
        {
            if (cur.Data[CodeKey] is int raw && Enum.IsDefined(typeof(AndroidErrorCode), raw))
            {
                code = (AndroidErrorCode)raw;
                return true;
            }
            if (cur.Data[CodeKey] is AndroidErrorCode typed)
            {
                code = typed;
                return true;
            }
        }
        code = default;
        return false;
    }
}

public sealed record AndroidAudioState(CaptureState State, AndroidDevice? Device = null, AndroidApp? App = null,
    string? Error = null, int Volume = 100, bool Muted = false, string? OutputId = null, bool SmartVolume = true, AndroidErrorCode? ErrorCode = null)
{
    public string? SourceId => Device is null || App is null ? null : $"android:{Device.Serial}:{App.Package}:{App.Uid}";
}

public interface IAudioTransport : IAsyncDisposable
{
    Task<Stream> ConnectAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, CancellationToken cancellationToken);
}

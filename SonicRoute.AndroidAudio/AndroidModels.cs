using System.Text.Json;

namespace SonicRoute.AndroidAudio;

public enum AndroidOutputSinkKind { Speaker, Bluetooth }

public sealed record AndroidOutputSink(
    string Key,
    AndroidOutputSinkKind Kind,
    int AndroidDeviceId,
    int AndroidDeviceType,
    string Name,
    string? Address = null)
{
    public static readonly AndroidOutputSink DefaultSpeaker = new("speaker", AndroidOutputSinkKind.Speaker, 0, 2, "手机扬声器");

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
                if (sink == null || string.IsNullOrWhiteSpace(sink.Key)) continue;
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
    public override string ToString() => $"{Name} · {(IsWifi ? "Wi-Fi" : "USB")}" +
        (State == "device" ? "" : State == "unauthorized" ? " · 待授权" : " · 未连接");
}

public static class AndroidDeviceIdentity
{
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
        && (StringComparer.Ordinal.Equals(left.Serial, right.Serial) || StringComparer.Ordinal.Equals(Key(left), Key(right)));
    public static IReadOnlyList<AndroidDevice> Distinct(IReadOnlyList<AndroidDevice> devices) => devices
        .GroupBy(Key, StringComparer.Ordinal).Select(group => group.OrderByDescending(d => d.State == "device")
            .ThenByDescending(d => d.Serial.Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase)).First()).ToArray();
}

public sealed record AndroidApp(string Package, string Name, int Uid, string Category, byte[]? IconPng = null, int PlatformCategory = -1)
{
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
    NoAudioRouteOrEnded
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

public interface IPcmPlayer : IDisposable
{
    void Play();
    void Stop();
}

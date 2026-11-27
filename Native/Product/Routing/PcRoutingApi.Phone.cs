#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

// The shared playback transaction owns a capture per Windows process. These bindings give
// its Android sinks access to the same managed connections and players as the phone panel.
internal static partial class PcRoutingApi
{
    internal static ExternalAudioBridge? Phones { get; set; }

    internal static Endpoint[] GetOutputDevices() => Phones?.GetPhoneOutputs(includeCurrent: true) ?? [];

    internal static AndroidPhonePcmPlayer CreatePhonePcmPlayer(AndroidPcmSource pcm, string id, CancellationToken token) =>
        Phones?.ExistingPhone?.CreatePhonePcmPlayer(pcm, id, token)
        ?? throw AndroidError.WithCode(new IOException("Phone offline."), AndroidErrorCode.PhoneOffline);

    internal static void PhonePlaybackFailed(AndroidDevice device) => Phones?.ExistingPhone?.OnPcPlaybackDisconnected(device);

    internal static string? TryGetPcAppPhoneOutput(int pid) => Phones?.GetPhoneRoute(pid, out _);

    internal static bool StopPcAppToPhone(int pid, bool restorePreviousEndpoint) =>
        Phones?.StopPcAppToPhone(pid, restorePreviousEndpoint) == true;

    internal static (bool Success, int HResult, string Message) RoutePcAppToPhone(int pid, string id) =>
        Phones?.RoutePcAppToPhone(pid, id, silenceLocal: true) ?? (false, -1, L10n.T("Phone.Err.PhoneOffline"));

    internal static string? GetPersistentOutputId(string id)
    {
        if (!IsPhoneOutputDevice(id)) return id;
        if (!AndroidOutputIdentity.TryParseDeviceId(id, out string key, out string sink)) return null;
        if (key.StartsWith("device:", StringComparison.Ordinal) && key.Length > 7) return id;
        if (key.StartsWith("app:", StringComparison.Ordinal) && SrapInvite.IsFingerprint(key[4..])) return id;
        if (!key.StartsWith("endpoint:", StringComparison.Ordinal)) return null;
        var device = Phones?.ExistingPhone?.Connections.ResolveKey(key);
        return AndroidDeviceIdentity.StableKey(device) is { } stable ? AndroidOutputIdentity.FormatDeviceId(stable, sink) : null;
    }

    internal static string? ResolvePersistentOutputId(string id)
    {
        if (!IsPhoneOutputDevice(id)) return id;
        if (!AndroidOutputIdentity.TryParseDeviceId(id, out var key, out var sink) || Phones?.ExistingPhone is not { } phone)
            return null;
        var online = phone.Connections.ResolveKey(key);
        if (online == null || !phone.Operations.CanPlayPcm(online) || phone.Store.FindProfile(online) is not { } profile) return null;
        if (AndroidOutputIdentity.IsPhoneCurrentOutput(id))
            return AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(online), AndroidOutputSink.Current.Key);
        var available = profile.OutputSinks.FirstOrDefault(s => string.Equals(s.Key, sink, StringComparison.OrdinalIgnoreCase));
        return available == null ? null : AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(online), available.Key);
    }

    internal static Task PrepareSavedPhoneOutputsAsync(IEnumerable<string> ids, CancellationToken token) =>
        Phones?.PrepareSavedPhoneOutputsAsync(ids, token) ?? Task.CompletedTask;
}
#endif

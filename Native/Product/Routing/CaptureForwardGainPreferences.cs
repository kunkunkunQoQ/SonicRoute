using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute.Native.Product;

internal static class CaptureForwardGainPreferences
{
    internal static readonly object Gate = new();

    internal static string NormalizeOutputId(string id) => PcRoutingApi.IsSystemDefault(id)
        ? PcRoutingApi.SystemDefaultDeviceId : AudioPolicyConfig.UnpackDeviceId(id);

    internal static string? PersistentOutputId(string id)
    {
        string normalized = NormalizeOutputId(id);
#if SONICROUTE_PHONE
        return PcRoutingApi.GetPersistentOutputId(normalized);
#else
        return normalized;
#endif
    }

    internal static MirrorGainState Read(string? appKey, string? stableOutputId) => appKey != null && stableOutputId != null
        ? ConfigService.Load().GetCaptureForwardGain(appKey, stableOutputId)
        : MirrorGainState.Default;

    internal static void Save(string appKey, string stableOutputId, MirrorGainState gain)
    {
        var config = ConfigService.Load();
        if (config.SetCaptureForwardGain(appKey, stableOutputId, gain)) ConfigService.QueueSave(config);
    }
}

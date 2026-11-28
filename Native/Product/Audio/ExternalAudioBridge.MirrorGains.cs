#if SONICROUTE_PHONE
using System.Runtime.CompilerServices;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.Core.Models;

namespace SonicRoute.Native.Product;

internal sealed partial class ExternalAudioBridge
{
    private static readonly StringComparer MirrorIds = StringComparer.OrdinalIgnoreCase;
    private readonly ConditionalWeakTable<IMultiOutputAudioSession, Dictionary<string, int>> _liveMirrorLimits = new();

    internal MirrorGainState GetMirrorGain(AudioAppInfo app, string sinkId)
    {
        lock (CaptureForwardGainPreferences.Gate)
        {
            if (FindSession(app.ExternalSourceId) is IMultiOutputAudioSession multi && FindMirrorId(app, multi, sinkId) is { } id)
                return ReadMirrorGain(multi, id, StableMirrorSourceKey(app.ExternalPreferenceKey),
                    CaptureForwardGainPreferences.PersistentOutputId(id));
            if (app.IsExternalOffline && FindPreference(app) is { } preference
                && FindMirrorId(app, preference.Outputs, sinkId) is { } savedId)
                return CaptureForwardGainPreferences.Read(StableMirrorSourceKey(preference.Key),
                    CaptureForwardGainPreferences.PersistentOutputId(savedId));
            return MirrorGainState.Default;
        }
    }

    internal MirrorGainState SetMirrorGain(AudioAppInfo app, string sinkId, int? volumePercent, bool? muted,
        bool toggleMute, int? maximumVolumePercent, bool persist)
    {
        lock (CaptureForwardGainPreferences.Gate)
        {
            if (FindSession(app.ExternalSourceId) is not IPhoneAudioSession { State.State: CaptureState.Receiving } session
                || session is not IMultiOutputAudioSession multi || FindMirrorId(app, multi, sinkId) is not { } id)
                return MirrorGainState.Default;
            string? appKey = StableMirrorSourceKey(app.ExternalPreferenceKey);
            string? outputId = CaptureForwardGainPreferences.PersistentOutputId(id);
            var next = ReadMirrorGain(multi, id, appKey, outputId)
                .WithChange(volumePercent, muted, toggleMute, maximumVolumePercent);
            var applied = multi.SetMirrorGain(id, next.VolumePercent, next.Muted);
            next = new MirrorGainState(applied.VolumePercent, applied.Muted, next.MaximumVolumePercent);
            if (persist && appKey != null && outputId != null)
            {
                if (_liveMirrorLimits.TryGetValue(multi, out var limits)) limits.Remove(id);
                CaptureForwardGainPreferences.Save(appKey, outputId, next);
            }
            else if (maximumVolumePercent.HasValue)
                _liveMirrorLimits.GetValue(multi, static _ => new(MirrorIds))[id] = next.MaximumVolumePercent;
            return next;
        }
    }

    private string? FindMirrorId(AudioAppInfo app, IMultiOutputAudioSession multi, string sinkId) =>
        FindMirrorId(app, multi.GetOutputs(), sinkId);

    private string? FindMirrorId(AudioAppInfo app, string?[] outputs, string sinkId)
    {
        if (MirrorIds.Equals(sinkId, AndroidAudioSession.SourcePhoneOutputId)) return null;
        string requested = CaptureForwardGainPreferences.NormalizeOutputId(AddressedOutput(app, sinkId) ?? sinkId);
        for (int i = 1; i < outputs.Length; i++)
            if (outputs[i] is { } id && !MirrorIds.Equals(id, AndroidAudioSession.SourcePhoneOutputId)
                && MirrorIds.Equals(CaptureForwardGainPreferences.NormalizeOutputId(id), requested)) return id;
        return null;
    }

    private MirrorGainState ReadMirrorGain(IMultiOutputAudioSession multi, string id, string? appKey, string? outputId)
    {
        int maximum = _liveMirrorLimits.TryGetValue(multi, out var limits) && limits.TryGetValue(id, out int liveLimit)
            ? liveLimit : CaptureForwardGainPreferences.Read(appKey, outputId).MaximumVolumePercent;
        var gain = multi.GetMirrorGain(id);
        return new(gain.VolumePercent, gain.Muted, maximum);
    }

    internal static (int VolumePercent, bool Muted)? ResolveSavedMirrorGain(string? appKey, string sinkId)
    {
        string? stableKey = StableMirrorSourceKey(appKey);
        if (stableKey == null) return null;
        var gain = CaptureForwardGainPreferences.Read(stableKey, CaptureForwardGainPreferences.PersistentOutputId(sinkId));
        return gain == MirrorGainState.Default ? null : (gain.VolumePercent, gain.Muted);
    }

    // A sender name can be shared by unrelated devices. AirPlay restores routes only by sender ID.
    private static string? StableMirrorSourceKey(string? key) =>
        key?.StartsWith(ExternalAudioPreference.AirPlayPrefix + "name:", StringComparison.Ordinal) == true ? null : key;
}
#endif

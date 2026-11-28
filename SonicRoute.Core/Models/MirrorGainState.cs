using System.Text.Json.Serialization;
using SonicRoute.Core.Compat;

namespace SonicRoute.Core.Models;

public readonly record struct MirrorGainState
{
    public const int MinimumSupportedVolumePercent = 1;
    public const int DefaultMaximumVolumePercent = 100;
    public const int MaximumSupportedVolumePercent = 300;

    public static MirrorGainState Default { get; } = new(DefaultMaximumVolumePercent, false);
    public int VolumePercent { get; }
    public bool Muted { get; }
    public int MaximumVolumePercent { get; }

    [JsonConstructor]
    public MirrorGainState(int volumePercent = DefaultMaximumVolumePercent, bool muted = false,
        int maximumVolumePercent = DefaultMaximumVolumePercent)
    {
        MaximumVolumePercent = MathEx.Clamp(maximumVolumePercent, MinimumSupportedVolumePercent, MaximumSupportedVolumePercent);
        VolumePercent = MathEx.Clamp(volumePercent, 0, MaximumVolumePercent);
        Muted = muted;
    }

    public MirrorGainState WithChange(int? volumePercent, bool? muted, bool toggleMute, int? maximumVolumePercent) =>
        new(volumePercent ?? VolumePercent, toggleMute ? !Muted : muted ?? Muted, maximumVolumePercent ?? MaximumVolumePercent);
}

#if SONICROUTE_PHONE
using System;

namespace SonicRoute;

internal static partial class AndroidAudioHost
{
    internal static bool IsHardwareRenderBus(string? adapter)
    {
        int start = adapter?.IndexOf(@"\\?\", StringComparison.Ordinal) ?? -1;
        if (start < 0) return false;
        string bus = adapter![(start + 4)..].Split('#', 2)[0];
        return bus.Equals("USB", StringComparison.OrdinalIgnoreCase) || bus.Equals("HDAUDIO", StringComparison.OrdinalIgnoreCase)
            || bus.Equals("INTELAUDIO", StringComparison.OrdinalIgnoreCase) || bus.Equals("PCI", StringComparison.OrdinalIgnoreCase)
            || bus.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) || bus.Equals("ACPI", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsMonitorOrInput(string? name) => name != null && (
        name.Contains("Microphone", StringComparison.OrdinalIgnoreCase) || name.Contains("麦克风", StringComparison.Ordinal)
        || name.Contains("麥克風", StringComparison.Ordinal) || name.Contains("Sidetone", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Side Tone", StringComparison.OrdinalIgnoreCase) || name.Contains("侧音", StringComparison.Ordinal)
        || name.Contains("側音", StringComparison.Ordinal) || name.EndsWith(" Input", StringComparison.OrdinalIgnoreCase)
        || name.Contains(" Input (", StringComparison.OrdinalIgnoreCase));

    // A player can briefly close its old session before reopening it on either endpoint.
    internal static bool WaitForPlaybackMigration(bool wasActive, Func<(bool TargetSafe, bool TargetActive, bool OtherActive)> observe, Action wait)
    {
        const int maxSamples = 16, stableSamples = 4;
        int consecutive = 0;
        for (int sample = 0; sample < maxSamples; sample++)
        {
            var state = observe();
            if (!state.TargetSafe) return false;
            wasActive |= state.TargetActive || state.OtherActive;
            if (!state.OtherActive && (!wasActive || state.TargetActive))
            {
                if (++consecutive == stableSamples) return true;
            }
            else consecutive = 0;
            if (sample + 1 < maxSamples) wait();
        }
        return false;
    }

    internal static bool WaitForOriginalSpeaker(string expected, bool requirePlayback,
        Func<(string? Endpoint, bool Active)> observe, Action wait)
    {
        int matches = 0;
        for (int sample = 0; sample < 20; sample++)
        {
            var current = observe();
            if (string.Equals(current.Endpoint, expected, StringComparison.OrdinalIgnoreCase) && (!requirePlayback || current.Active))
            {
                if (!requirePlayback || ++matches == 3) return true;
            }
            else matches = 0;
            if (sample < 19) wait();
        }
        return false;
    }
}
#endif

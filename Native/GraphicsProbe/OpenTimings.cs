using System.Diagnostics;

namespace SonicRoute.GraphicsProbe;

// Calls compile out of the regular probe. No profiling timer runs on an open UI.
internal static class OpenTimings
{
    [Conditional("GRAPHICS_OPEN_PROFILE")]
    internal static void Begin(ref long ticks) => ticks=Stopwatch.GetTimestamp();
    [Conditional("GRAPHICS_OPEN_PROFILE")]
    internal static void Mark(ref long ticks,string operation)
    {
        long end=Stopwatch.GetTimestamp();
        Console.WriteLine($"open.{operation}={Stopwatch.GetElapsedTime(ticks,end).TotalMilliseconds:F3}ms");
        ticks=Stopwatch.GetTimestamp();
    }
}

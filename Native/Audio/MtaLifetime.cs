using System.Runtime.InteropServices;

namespace SonicRoute.Native.Audio;

// COM objects retained between bounded operations outlive the thread that discovered them.
internal sealed partial class MtaLifetime : IDisposable
{
    private static int _active;
    internal static int Active => Volatile.Read(ref _active);
    private nint _cookie;
    [LibraryImport("ole32.dll")] private static partial int CoIncrementMTAUsage(out nint cookie);
    [LibraryImport("ole32.dll")] private static partial int CoDecrementMTAUsage(nint cookie);
    internal MtaLifetime()
    {
        Com.Check(CoIncrementMTAUsage(out _cookie), "CoIncrementMTAUsage");
        Interlocked.Increment(ref _active);
    }
    public void Dispose()
    {
        nint cookie = Interlocked.Exchange(ref _cookie, 0);
        if (cookie == 0) return;
        CoDecrementMTAUsage(cookie);
        Interlocked.Decrement(ref _active);
    }
}

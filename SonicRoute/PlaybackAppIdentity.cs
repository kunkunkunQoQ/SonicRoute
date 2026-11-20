using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SonicRoute;

internal readonly record struct PlaybackAppIdentity(string Key, long Created)
{
    internal static PlaybackAppIdentity? Read(int pid)
    {
        if (pid <= 0) return null;
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !GetProcessTimes(process, out long created, out _, out _, out _)) return null;
        // Package updates can change the executable directory; the application ID survives the update.
        var buffer = new StringBuilder(1024);
        uint size = (uint)buffer.Capacity;
        try
        {
            if (GetApplicationUserModelId(process, ref size, buffer) == 0 && buffer.Length != 0)
                return new PlaybackAppIdentity("app:" + buffer, created);
        }
        catch (EntryPointNotFoundException) { }
        int length = buffer.Capacity;
        if (!QueryFullProcessImageName(process, 0, buffer, ref length))
        {
            if (Marshal.GetLastWin32Error() != 122) return null;
            length = buffer.EnsureCapacity(32768);
            if (!QueryFullProcessImageName(process, 0, buffer, ref length)) return null;
        }
        return new PlaybackAppIdentity("exe:" + buffer, created);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint size, StringBuilder id);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
}

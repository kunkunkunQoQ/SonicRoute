using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SonicRoute.AndroidAudio;

internal static class AudioProcessTree
{
    internal static bool DescendsFrom(int child, int ancestor)
    {
        long newest = long.MaxValue;
        for (int depth = 0; child > 0 && depth < 64; depth++)
        {
            using var handle = OpenProcess(0x1000, false, child); // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle.IsInvalid || !GetProcessTimes(handle, out long born, out _, out _, out _) || born > newest)
                return false;
            if (child == ancestor) return true;
            if (NtQueryInformationProcess(handle, 0, out var info, Marshal.SizeOf<BasicProcessInfo>(), out _) < 0
                || info.ParentId <= 0 || info.ParentId > int.MaxValue || info.ParentId == child) return false;
            newest = born;
            child = (int)info.ParentId;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicProcessInfo { internal nint Status, Peb, Affinity, Priority, ProcessId, ParentId; }
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited,
        out long kernelTime, out long userTime);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass,
        out BasicProcessInfo info, int size, out int returned);
}

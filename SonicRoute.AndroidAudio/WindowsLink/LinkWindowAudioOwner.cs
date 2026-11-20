using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SonicRoute.WindowsLink;

internal readonly record struct LinkWindowAudioOwner(int Pid, string Name, string InstanceId)
{
    internal static LinkWindowAudioOwner Resolve(LinkWindow window)
    {
        if (LinkWindowCapture.ReadWindow(window.Handle)?.InstanceId != window.InstanceId)
            throw new IOException("The shared window's process changed.");
        int pid = window.ProcessId;
        string? name = window.ProcessName;
        if (name == null) throw new IOException("Windows could not identify the shared window's audio process.");
        if (name.ToLowerInvariant() is "applicationframehost" or "explorer" or "svchost" or "dllhost" or "rundll32"
            or "shellexperiencehost" or "runtimebroker" or "systemsettingsadminflows" or "conhost" or "openconsole")
        {
            // ApplicationFrameHost shares a process across unrelated UWP applications.
            // Attribute its documented CoreWindow child to this HWND, or leave the viewer silent.
            var owners = new HashSet<int>();
            var windowClass = new StringBuilder(96);
            EnumChildWindows(window.Handle, (child, _) =>
            {
                if (GetClassName(child, windowClass, windowClass.Capacity) == 0
                    || windowClass.ToString() != "Windows.UI.Core.CoreWindow") return true;
                if (GetWindowThreadProcessId(child, out int childPid) != 0 && childPid > 0 && childPid != pid)
                    owners.Add(childPid);
                return owners.Count <= 1;
            }, 0);
            if (owners.Count != 1) throw new IOException("This shared host window has no unique application audio process.");
            pid = owners.Single();
        }
        var current = LinkWindowCapture.ReadProcess(pid);
        if (pid == Environment.ProcessId || current.Name == null
            || pid == window.ProcessId && (current.StartTicks != window.ProcessStartTicks
                || !string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The shared window's audio process changed.");
        return new(pid, current.Name, LinkRemoteApp.AudioInstanceId(pid, current.StartTicks));
    }

    internal static bool SameTree(int first, int second) => DescendsFrom(first, second) || DescendsFrom(second, first);

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
    private delegate bool EnumerateWindow(nint handle, nint state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumerateWindow callback, nint state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited,
        out long kernelTime, out long userTime);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass,
        out BasicProcessInfo info, int size, out int returned);
}

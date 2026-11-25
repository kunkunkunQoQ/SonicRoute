using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

// A development-only startup hook for the frozen WPF executable. It reuses the WPF
// activation HWND on its own dispatcher and sleeps until an explicit sample request.
internal static class StartupHook
{
    private static int message;
    private static long messages;
    private static readonly long[] Snapshot = new long[7];

    public static void Initialize()
    {
        string? instance = Environment.GetEnvironmentVariable("SONICROUTE_INSTANCE_ID");
        string? directory = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY");
        if (instance is null || !instance.StartsWith("PerfWpf_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(instance["PerfWpf_".Length..], "N", out _) ||
            directory is null || !Path.IsPathFullyQualified(directory) ||
            !File.Exists(Path.Combine(directory, instance + ".fixture")))
            throw new InvalidOperationException("The measurement hook requires an isolated fixture.");
        string suffix = Environment.GetEnvironmentVariable("SONICROUTE_MEASURE_EDITION") switch
        {
            "Full" => "_Full",
            "Lite" => "",
            _ => throw new InvalidOperationException("Unknown measurement edition.")
        };

        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            nint hwnd = FindWindowW(null, "SonicRoute_" + instance + suffix + "_ActivateSink");
            GetWindowThreadProcessId(hwnd, out uint process);
            if (hwnd == 0 || process != Environment.ProcessId || HwndSource.FromHwnd(hwnd) is not HwndSource source)
                throw new InvalidOperationException("Isolated activation sink was not ready.");
            message = (int)RegisterWindowMessageW(instance + ".sample");
            if (message == 0) throw new InvalidOperationException("RegisterWindowMessage failed.");
            source.AddHook(Sample);
            ComponentDispatcher.ThreadFilterMessage += CountMessage;
            File.WriteAllText(Path.Combine(directory, "wpf.ready"), Environment.ProcessId.ToString());
        }));
    }

    private static void CountMessage(ref MSG msg, ref bool handled) => messages++;

    private static nint Sample(nint hwnd, int id, nint command, nint unused, ref bool handled)
    {
        if (id != message) return 0;
        handled = true;
        if (command == 100)
        {
            var info = GC.GetGCMemoryInfo();
            Snapshot[0] = GC.GetTotalAllocatedBytes(true);
            Snapshot[1] = GC.GetTotalMemory(false);
            Snapshot[2] = info.HeapSizeBytes;
            Snapshot[3] = GC.CollectionCount(0);
            Snapshot[4] = GC.CollectionCount(1);
            Snapshot[5] = GC.CollectionCount(2);
            Snapshot[6] = messages;
            return 1;
        }
        if (command == 101)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
            return 1;
        }
        return (uint)(int)command < Snapshot.Length ? (nint)Snapshot[(int)command] : -1;
    }

    [DllImport("user32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] private static extern nint FindWindowW(string? className, string title);
    [DllImport("user32.dll", ExactSpelling=true)] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] private static extern uint RegisterWindowMessageW(string name);
}

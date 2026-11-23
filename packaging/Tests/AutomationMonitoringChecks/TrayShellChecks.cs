namespace SonicRoute;

internal sealed partial class TrayWheelService
{
    private sealed class ShellWindow(string name, IntPtr parent, RECT bounds)
    {
        internal readonly string Name = name;
        internal readonly IntPtr Parent = parent;
        internal RECT Bounds = bounds;
    }
    private static readonly Dictionary<IntPtr, ShellWindow> Windows = new();
    private static IntPtr _unreadableClass;
    private static RECT Box(int x, int y, int w, int h) => new() { Left = x, Top = y, Right = x + w, Bottom = y + h };
    private static void Window(int handle, string name, int parent, RECT bounds)
        => Windows[new IntPtr(handle)] = new ShellWindow(name, new IntPtr(parent), bounds);
    internal static void RestoreShell() => CreateShell(0, 0, false);
    private static void CreateShell(int generation, int primaryX, bool children)
    {
        Windows.Clear();
        Window(generation + 1, "Shell_TrayWnd", 0, Box(primaryX - 100, 0, 200, 100));
        Window(generation + 3, "TrayNotifyWnd", generation + 1, Box(primaryX, 0, 100, 100));
        if (children) CreateOtherAreas(generation);
    }
    private static void CreateOtherAreas(int generation)
    {
        Window(generation + 2, "Shell_SecondaryTrayWnd", 0, Box(-160, 0, 150, 80));
        Window(generation + 4, "NotifyContainerOverflowWindow", generation + 1, Box(110, 100, 30, 30));
        Window(generation + 5, "TrayNotifyWnd", generation + 2, Box(-110, 0, 100, 80));
    }

    private static IntPtr FindWindow(string? name, string? text)
        => FindWindowEx(IntPtr.Zero, IntPtr.Zero, name!, text);
    private static IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string? text)
    {
        bool eligible = after == IntPtr.Zero;
        foreach (var item in Windows)
        {
            if (eligible && item.Value.Parent == parent && item.Value.Name == name) return item.Key;
            if (item.Key == after) eligible = true;
        }
        return IntPtr.Zero;
    }
    private static bool GetWindowRect(IntPtr window, out RECT rect)
    {
        if (Windows.TryGetValue(window, out var item)) { rect = item.Bounds; return true; }
        rect = default; return false;
    }
    private static uint GetClassLong(IntPtr window, int index)
    {
        if (index != -32 || window == _unreadableClass || !Windows.TryGetValue(window, out var item)) return 0;
        return item.Name switch
        {
            "Shell_TrayWnd" => 101, "Shell_SecondaryTrayWnd" => 102, "TrayNotifyWnd" => 103,
            "NotifyContainerOverflowWindow" => 104, _ => 105
        };
    }
    private static uint GetWindowThreadProcessId(IntPtr window, out uint pid)
    {
        bool found = Windows.ContainsKey(window);
        pid = !found ? 0u : window.ToInt64() >= 100 ? 500u : 200u;
        return found ? pid + 1 : 0;
    }
    private static IntPtr GetParent(IntPtr window) => Windows.TryGetValue(window, out var item) ? item.Parent : IntPtr.Zero;
    private static IntPtr GetAncestor(IntPtr window, uint flags)
    {
        if (flags != 2 || !Windows.TryGetValue(window, out _)) return IntPtr.Zero;
        while (GetParent(window) is var parent && parent != IntPtr.Zero && Windows.ContainsKey(parent)) window = parent;
        return window;
    }
    private static IntPtr WindowFromPoint(POINT pt)
    {
        foreach (var item in Windows.Reverse())
        {
            var r = item.Value.Bounds;
            if (PtInRect(ref r, pt)) return item.Key;
        }
        return IntPtr.Zero;
    }
    internal void CheckNotificationArea(Action<bool, string> check)
    {
        CreateShell(0, 0, false);
        check(HitTest(50, 50) && !HitTest(120, 110) && !HitTest(-50, 30), "first wheel can see a primary taskbar without optional Explorer windows");
        CreateOtherAreas(0);
        check(HitTest(50, 50) && HitTest(120, 110) && HitTest(-50, 30) && !HitTest(200, 150),
            "notification region finds overflow and negative-coordinate secondary areas that appeared after the first wheel");
        Window(30, "Shell_SecondaryTrayWnd", 0, Box(-500, 0, 200, 100));
        Window(31, "TrayNotifyWnd", 30, Box(-380, 0, 80, 100));
        Window(32, "Browser", 0, Box(-700, 0, 150, 180));
        check(HitTest(-340, 50) && !HitTest(-440, 50) && !HitTest(-590, 50),
            "third-screen notification area responds while the rest of its taskbar and a web page stay outside");
        Windows[new IntPtr(1)].Bounds = Box(900, 0, 200, 100);
        Windows[new IntPtr(3)].Bounds = Box(1000, 0, 100, 100);
        check(!HitTest(50, 50) && HitTest(1050, 50), "a taskbar moving off its old location uses its current bounds immediately");

        Windows[new IntPtr(1)].Bounds = Box(450, 500, 350, 100);
        Window(3, "UnrelatedWindow", 1, Box(500, 500, 100, 100));
        Window(6, "TrayNotifyWnd", 1, Box(700, 500, 100, 100));
        check(!HitTest(550, 550) && HitTest(750, 550), "a recycled handle belonging to an unrelated class cannot turn page scrolls into tray adjustments");
        Window(6, "TrayNotifyWnd", 999, Box(500, 500, 100, 100));
        Window(7, "TrayNotifyWnd", 1, Box(700, 500, 100, 100));
        check(!HitTest(550, 550) && HitTest(750, 550), "a recycled matching child class must still belong to the correct taskbar");
        CreateShell(100, 300, true);
        check(!HitTest(750, 550) && HitTest(350, 50) && HitTest(-50, 30),
            "replacement Explorer windows are rediscovered without retaining old coordinates");
        _unreadableClass = new IntPtr(103);
        try
        {
            check(HitTest(350, 50) && !HitTest(550, 550),
                "a fresh class lookup still uses current bounds when window metadata cannot be cached");
        }
        finally { _unreadableClass = IntPtr.Zero; }
    }
}

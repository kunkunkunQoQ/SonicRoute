using System.Runtime.InteropServices;

namespace SonicRoute;

// The only substitute for Win32 in the routing checks: the product installs its
// actual callback here and receives packets with the native MSLLHOOKSTRUCT layout.
internal static class MouseInputBoundary
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Packet
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal IntPtr ExtraInfo;
    }
    internal static readonly IntPtr Next = new(23);
    private static Func<int, IntPtr, IntPtr, IntPtr>? _callback;
    internal static int Installs, Uninstalls, Forwards, PayloadReads, ModifierReads;
    internal static IntPtr Install(Func<int, IntPtr, IntPtr, IntPtr> callback)
    {
        if (_callback != null) throw new InvalidOperationException("A second hook was installed");
        _callback = callback;
        return new IntPtr(++Installs);
    }
    internal static bool Uninstall(IntPtr handle)
    {
        if (_callback == null || handle != new IntPtr(Installs)) throw new InvalidOperationException("Wrong hook was removed");
        _callback = null;
        Uninstalls++;
        return true;
    }
    internal static IntPtr Forward() { Forwards++; return Next; }
    internal static T ReadPacket<T>(IntPtr pointer) where T : struct
    {
        PayloadReads++;
        return Marshal.PtrToStructure<T>(pointer);
    }
    internal static bool IsDown(int vk)
    {
        if (vk == 0x11) ModifierReads++; // First query of each GetPressedMods call.
        return KeyBoundary.Down.Contains(vk);
    }
    internal static IntPtr Wheel(int delta, int x = 50, int y = 50) => Deliver(0x020A, delta, x, y);
    internal static IntPtr Deliver(int message, int highWord = 0, int x = 50, int y = 50)
    {
        if (_callback == null) return Next;
        var packet = new Packet { X = x, Y = y, Data = unchecked((uint)(highWord << 16)) };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Packet>());
        try
        {
            Marshal.StructureToPtr(packet, pointer, false);
            return _callback(0, new IntPtr(message), pointer);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}

internal static class MouseRoutingChecks
{
    internal static void Run(App app, TrayWheelService wheel, MouseHotkeyHook mouse, Action<bool, string> check)
    {
        var actions = new List<string>();
        mouse.HotkeyPressed += actions.Add;
        var bindings = new Dictionary<string, string>
        {
            ["Ctrl+Alt+WheelUp"] = "up", ["Ctrl+Alt+WheelDown"] = "down", ["Ctrl+XButton1"] = "side", ["MButton"] = "middle"
        };
        try
        {
            check(mouse.IsInstalled && MouseInputBoundary.Installs == 1, "tray alone installs one global mouse callback");
            mouse.Reload(bindings);
            mouse.Reload(bindings);
            check(MouseInputBoundary.Installs == 1 && MouseInputBoundary.Uninstalls == 0,
                "enabling and reloading mouse shortcuts reuse the tray registration");
            int payloads = MouseInputBoundary.PayloadReads, mods = MouseInputBoundary.ModifierReads, hits = wheel.IdentityReads;
            int forwards = MouseInputBoundary.Forwards;
            for (int i = 0; i < 20; i++) MouseInputBoundary.Deliver(0x0200);
            check(MouseInputBoundary.Forwards == forwards + 20 && MouseInputBoundary.PayloadReads == payloads
                && MouseInputBoundary.ModifierReads == mods && wheel.IdentityReads == hits,
                "twenty ordinary movements forward immediately with no packet, key or tray lookup");

            KeyBoundary.Down.UnionWith(new[] { 0x11, 0x12 });
            app.IsHotkeyCaptureActive = true;
            check(MouseInputBoundary.Wheel(120) == MouseInputBoundary.Next && MouseInputBoundary.Deliver(0x020B, 1) == MouseInputBoundary.Next
                && actions.Count == 0 && !wheel.Pending && MouseInputBoundary.ModifierReads == mods
                && MouseInputBoundary.PayloadReads == payloads, "shortcut capture receives wheel and side button before either global action");
            app.IsHotkeyCaptureActive = false;
            forwards = MouseInputBoundary.Forwards;
            check(MouseInputBoundary.Wheel(120) == new IntPtr(1) && actions.SequenceEqual(new[] { "up" })
                && !wheel.Pending && wheel.IdentityReads == hits && MouseInputBoundary.Forwards == forwards
                && MouseInputBoundary.ModifierReads == mods + 1 && MouseInputBoundary.PayloadReads == payloads + 1,
                "Ctrl+Alt wheel is decoded once, matched once, swallowed once and never enters tray hit testing");
            KeyBoundary.Down.Clear();
            mods = MouseInputBoundary.ModifierReads; payloads = MouseInputBoundary.PayloadReads;
            check(MouseInputBoundary.Wheel(-120, 500, 500) == MouseInputBoundary.Next && !wheel.Pending && actions.Count == 1
                && MouseInputBoundary.ModifierReads == mods + 1 && MouseInputBoundary.PayloadReads == payloads + 1,
                "ordinary browser wheel with active global bindings remains available to the foreground page");
            mods = MouseInputBoundary.ModifierReads;
            check(MouseInputBoundary.Wheel(30) == MouseInputBoundary.Next && wheel.Pending
                && MouseInputBoundary.ModifierReads == mods + 1, "tray adjustment forwards the original message and reuses the modifier snapshot");
            wheel.DiscardPendingWheel();

            check(wheel.TrySetPaused(true) && mouse.IsInstalled && MouseInputBoundary.Uninstalls == 0,
                "pausing tray volume keeps the same installed callback while shortcuts remain");
            KeyBoundary.Down.UnionWith(new[] { 0x11, 0x12 });
            check(MouseInputBoundary.Wheel(-120) == new IntPtr(1) && actions.SequenceEqual(new[] { "up", "down" }),
                "Ctrl+Alt wheel down continues to trigger exactly once when tray volume is paused");
            KeyBoundary.Down.Remove(0x12);
            check(MouseInputBoundary.Deliver(0x020B, 1) == new IntPtr(1) && actions.Last() == "side"
                && MouseInputBoundary.Deliver(0x020B, 2) == MouseInputBoundary.Next, "matched side button is swallowed while unbound side button forwards");
            KeyBoundary.Down.Clear();
            check(MouseInputBoundary.Deliver(0x0207) == new IntPtr(1) && actions.Last() == "middle", "middle button shortcut still matches");
            mouse.Reload(Array.Empty<KeyValuePair<string, string>>());
            check(!mouse.IsInstalled && MouseInputBoundary.Uninstalls == 1, "disabling the final consumer uninstalls the only hook");
            wheel.TrySetPaused(false);
            mods = MouseInputBoundary.ModifierReads;
            check(mouse.IsInstalled && MouseInputBoundary.Installs == 2 && MouseInputBoundary.Wheel(120, 500, 500) == MouseInputBoundary.Next
                && MouseInputBoundary.ModifierReads == mods, "tray-only browser scrolling skips modifier reads outside the notification area");
        }
        finally
        {
            mouse.HotkeyPressed -= actions.Add;
            KeyBoundary.Down.Clear();
            app.IsHotkeyCaptureActive = false;
        }
    }
}

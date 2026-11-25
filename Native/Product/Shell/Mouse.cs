using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Win = SonicRoute.Native.Product.ShellNative;

namespace SonicRoute.Native.Product;

internal enum MouseGesture { Middle, Side1, Side2, WheelUp, WheelDown }
internal readonly record struct MouseChord(MouseGesture Gesture, uint Modifiers);

internal sealed unsafe partial class NativeApplicationWindow
{
    private readonly Dictionary<MouseChord, int> mouseBindings = new();
    private readonly int[] wheelDelta = new int[4];
    private readonly nint[] wheelForeground = new nint[4];
    private bool wheelEnabled, wheelEverywhere, wheelPosted;
    private int boundGestures;
    private long mouseGeneration;
    private nint mouseHook;
    internal bool CapturingHotkey { get; set; }

    internal bool ConfigureMouse(IReadOnlyDictionary<MouseChord, int> bindings, bool trayWheel, bool everywhere)
    {
        VerifyOwner();
        if (bindings.Count > 256) throw new ArgumentOutOfRangeException(nameof(bindings));
        bool needed = bindings.Count > 0 || trayWheel;
        if (needed && mouseHook == 0)
        {
            mouseHook = Win.SetWindowsHookExW(14, &MouseProc, module, 0);
            if (mouseHook == 0) { Report(LastError("WH_MOUSE_LL")); return false; }
        }
        if (!needed) StopMouse();
        mouseBindings.Clear();
        boundGestures = 0;
        ++mouseGeneration;
        foreach (var pair in bindings)
        {
            mouseBindings.Add(pair.Key, pair.Value);
            boundGestures |= 1 << (int)pair.Key.Gesture;
        }
        wheelEnabled = trayWheel;
        wheelEverywhere = everywhere;
        if (!trayWheel) Array.Clear(wheelDelta);
        return true;
    }

    private void StopMouse()
    {
        if (mouseHook != 0) { Win.UnhookWindowsHookEx(mouseHook); mouseHook = 0; }
        wheelEnabled = false;
        ++mouseGeneration;
        boundGestures = 0;
        mouseBindings.Clear();
        Array.Clear(wheelDelta);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint MouseProc(int code, nuint message, nint data)
    {
        var host = current;
        if (code == 0 && host != null && !host.closing && !host.CapturingHotkey && data != 0
            && message is 0x207 or 0x20a or 0x20b)
        {
            try { if (host.OnMouse(message, *(Win.MouseData*)data)) return 1; }
            catch (Exception ex) { host.Report(ex); }
        }
        return Win.CallNextHookEx(0, code, message, data);
    }

    private bool OnMouse(nuint message, Win.MouseData data)
    {
        int high = (short)(data.Data >> 16);
        MouseGesture gesture;
        switch (message)
        {
            case 0x207: gesture = MouseGesture.Middle; break;
            case 0x20a when high != 0: gesture = high > 0 ? MouseGesture.WheelUp : MouseGesture.WheelDown; break;
            case 0x20b when high is 1 or 2: gesture = high == 1 ? MouseGesture.Side1 : MouseGesture.Side2; break;
            default: return false;
        }
        uint? mods = null;
        if ((boundGestures & (1 << (int)gesture)) != 0)
        {
            mods = PressedModifiers();
            if (mouseBindings.TryGetValue(new(gesture, mods.Value), out int id))
            {
                long generation = mouseGeneration;
                return TryPost(() => { if (generation == mouseGeneration) Raise(ShellSignalKind.Hotkey, id); });
            }
        }
        if (message != 0x20a || !wheelEnabled || !IsOverTray(data.Point)) return false;
        int bucket = (mods ?? PressedModifiers()) switch { 1 => 1, 5 => 2, 4 => 3, _ => 0 };
        if (!wheelPosted && !TryPost(DrainWheel)) return false;
        wheelPosted = true;
        // Wheel deltas are additive. Match the product's four modifier groups without queueing each tick.
        wheelDelta[bucket] = (int)Math.Clamp((long)wheelDelta[bucket] + high, int.MinValue, int.MaxValue);
        wheelForeground[bucket] = Win.GetForegroundWindow();
        return false;  // tray wheel observes, bound mouse hotkeys consume.
    }

    private void DrainWheel()
    {
        wheelPosted = false;
        for (int i = 0; i < wheelDelta.Length; i++)
        {
            int delta = wheelDelta[i];
            wheelDelta[i] = 0;
            if (delta != 0 && wheelEnabled)
                Raise(ShellSignalKind.TrayWheel, delta, wheelForeground[i], i switch { 1 => 1, 2 => 5, 3 => 4, _ => 0 });
        }
    }

    private static uint PressedModifiers()
    {
        uint value = 0;
        if (Win.GetAsyncKeyState(0x12) < 0) value |= 1;
        if (Win.GetAsyncKeyState(0x11) < 0) value |= 2;
        if (Win.GetAsyncKeyState(0x10) < 0) value |= 4;
        if (Win.GetAsyncKeyState(0x5b) < 0 || Win.GetAsyncKeyState(0x5c) < 0) value |= 8;
        return value;
    }
}

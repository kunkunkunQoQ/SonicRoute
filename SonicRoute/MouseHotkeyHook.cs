using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace SonicRoute
{
    /// <summary>
    /// 鼠标快捷键与托盘滚轮调音共用一个低级鼠标钩子 WH_MOUSE_LL。
    /// RegisterHotKey 对鼠标按键（XButton1/2、MButton、滚轮）支持不可靠（实测侧键注册后不触发），
    /// 因此所有含鼠标键/滚轮的绑定统一走低级鼠标钩子，键盘绑定仍用 RegisterHotKey。
    /// 支持：MButton（中键）、XButton1 / XButton2（侧键）、WheelUp / WheelDown（滚轮），
    /// 均可带 Ctrl / Alt / Shift / Win 修饰键，也支持无修饰键的单鼠标键。
    /// </summary>
    public sealed class MouseHotkeyHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const uint WM_MOUSEWHEEL = 0x020A;
        private const uint WM_MBUTTONDOWN = 0x0207;
        private const uint WM_XBUTTONDOWN = 0x020B;

        private const uint MOD_ALT = 0x1;
        private const uint MOD_CONTROL = 0x2;
        private const uint MOD_SHIFT = 0x4;
        private const uint MOD_WIN = 0x8;

        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;      // Alt
        private const int VK_SHIFT = 0x10;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [Flags]
        private enum MouseKey { None = 0, Middle = 1, Side1 = 2, Side2 = 4, WheelUp = 8, WheelDown = 16 }

        private readonly LowLevelMouseProc _proc;
        private IntPtr _hook;
        private readonly Dictionary<(MouseKey Key, uint Mods), string> _bindings = new();
        private MouseKey _boundKeys;
        public bool IsInstalled => _hook != IntPtr.Zero;
        internal TrayWheelService? TrayWheel { get; private set; }
        private bool _disposed;

        public event Action<string>? HotkeyPressed;

        public MouseHotkeyHook()
        {
            _proc = MouseProc;
        }

        /// <summary>替换鼠标绑定集合（combo → action）。combo 形如 "Ctrl+XButton1"、"WheelUp"。</summary>
        public void Reload(IEnumerable<KeyValuePair<string, string>> bindings)
        {
            _boundKeys = MouseKey.None;
            _bindings.Clear();
            if (_disposed) return;
            foreach (var kv in bindings)
            {
                if (!TryParseMouseCombo(kv.Key, out var key, out uint mods) || _bindings.ContainsKey((key, mods))) continue;
                _bindings.Add((key, mods), kv.Value);
                _boundKeys |= key;
            }
            TryUpdateRegistration(TrayWheel != null || _bindings.Count != 0, out _);
        }

        internal bool TrySetTrayWheel(TrayWheelService? trayWheel, out int error)
        {
            if (_disposed) { error = 6; return false; } // ERROR_INVALID_HANDLE
            if (!TryUpdateRegistration(trayWheel != null || _bindings.Count != 0, out error)) return false;
            TrayWheel = trayWheel;
            return true;
        }

        private bool TryUpdateRegistration(bool needed, out int error)
        {
            error = 0;
            if (needed == IsInstalled) return true;
            if (needed)
            {
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
                if (_hook != IntPtr.Zero) return true;
                error = Marshal.GetLastWin32Error();
                return false;
            }
            if (!UnhookWindowsHookEx(_hook))
            {
                error = Marshal.GetLastWin32Error();
                if (error != 1404) return false; // Windows may already have removed the hook.
            }
            _hook = IntPtr.Zero;
            return true;
        }

        internal bool IsRegistered(string combo, string action) => IsInstalled && !_disposed
            && TryParseMouseCombo(combo, out var key, out uint mods)
            && _bindings.TryGetValue((key, mods), out string? registered) && registered == action;

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            uint msg = (uint)wParam.ToInt64();
            if (nCode < 0 || (msg != WM_MOUSEWHEEL && msg != WM_MBUTTONDOWN && msg != WM_XBUTTONDOWN) || _disposed)
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            try
            {
                if (System.Windows.Application.Current is App app && app.IsHotkeyCaptureActive)
                    return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                MouseKey key;
                int delta = 0;
                POINT point = default;
                if (msg == WM_MOUSEWHEEL)
                {
                    if (TrayWheel == null && (_boundKeys & (MouseKey.WheelUp | MouseKey.WheelDown)) == 0)
                        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    delta = (short)(data.mouseData >> 16);
                    if (delta == 0) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                    key = delta > 0 ? MouseKey.WheelUp : MouseKey.WheelDown;
                    point = data.pt;
                }
                else if (msg == WM_XBUTTONDOWN)
                {
                    if ((_boundKeys & (MouseKey.Side1 | MouseKey.Side2)) == 0)
                        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    key = (data.mouseData >> 16) switch { 1 => MouseKey.Side1, 2 => MouseKey.Side2, _ => MouseKey.None };
                }
                else key = MouseKey.Middle;

                uint? mods = null;
                if ((_boundKeys & key) != 0)
                {
                    mods = GetPressedMods();
                    if (_bindings.TryGetValue((key, mods.Value), out var action))
                    {
                        HotkeyPressed?.Invoke(action);
                        return new IntPtr(1);
                    }
                }
                if (msg == WM_MOUSEWHEEL && TrayWheel is { } tray && tray.HitTest(point.x, point.y))
                    tray.QueueWheel(delta, mods ?? GetPressedMods());
            }
            catch { /* Never let an exception escape the native input callback. */ }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        /// <summary>拆分快捷键组合串（'+' 分隔）：去空段 + 逐段 Trim。
        /// net48 无 StringSplitOptions.TrimEntries（.NET 5+），也无 Split(char, StringSplitOptions) 重载。</summary>
        private static string[] SplitCombo(string combo) =>
            combo.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                 .Select(s => s.Trim())
                 .Where(s => s.Length > 0)
                 .ToArray();

        private static bool TryParseMouseCombo(string combo, out MouseKey key, out uint mods)
        {
            var parts = SplitCombo(combo);
            key = MouseKey.None;
            mods = 0;
            if (parts.Length < 1) return false;

            foreach (var p in parts.Take(parts.Length - 1))
            {
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": mods |= MOD_CONTROL; break;
                    case "alt": mods |= MOD_ALT; break;
                    case "shift": mods |= MOD_SHIFT; break;
                    case "win": mods |= MOD_WIN; break;
                    default: return false;
                }
            }
            key = parts[parts.Length - 1].ToLowerInvariant() switch
            {
                "mbutton" => MouseKey.Middle,
                "xbutton1" => MouseKey.Side1,
                "xbutton2" => MouseKey.Side2,
                "wheelup" => MouseKey.WheelUp,
                "wheeldown" => MouseKey.WheelDown,
                _ => MouseKey.None
            };
            return key != MouseKey.None;
        }

        internal static uint GetPressedMods()
        {
            uint m = 0;
            if (IsDown(VK_CONTROL)) m |= MOD_CONTROL;
            if (IsDown(VK_MENU)) m |= MOD_ALT;
            if (IsDown(VK_SHIFT)) m |= MOD_SHIFT;
            if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) m |= MOD_WIN;
            return m;
        }

        private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public void Dispose()
        {
            _disposed = true;
            _bindings.Clear();
            _boundKeys = MouseKey.None;
            TrayWheel = null;
            TryUpdateRegistration(false, out _);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}

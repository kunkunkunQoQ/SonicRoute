using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SonicRoute
{
    /// <summary>
    /// 鼠标快捷键（低级鼠标钩子 WH_MOUSE_LL）。
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
        private const uint WM_XBUTTONUP = 0x020C;

        private const uint MOD_ALT = 0x1;
        private const uint MOD_CONTROL = 0x2;
        private const uint MOD_SHIFT = 0x4;
        private const uint MOD_WIN = 0x8;

        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;      // Alt
        private const int VK_SHIFT = 0x10;
        private const int VK_LWIN = 0x5B;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private readonly LowLevelMouseProc _proc;
        private IntPtr _hook;
        private readonly Dictionary<string, string> _actionsByCombo = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public event Action<string>? HotkeyPressed;

        public MouseHotkeyHook()
        {
            _proc = MouseProc;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        }

        /// <summary>替换鼠标绑定集合（combo → action）。combo 形如 "Ctrl+XButton1"、"WheelUp"。</summary>
        public void Reload(IEnumerable<KeyValuePair<string, string>> bindings)
        {
            _actionsByCombo.Clear();
            foreach (var kv in bindings)
                _actionsByCombo[kv.Key] = kv.Value;
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_disposed && _actionsByCombo.Count > 0)
            {
                try
                {
                    var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    uint msg = (uint)wParam.ToInt64();

                    // 只有按下事件参与匹配（滚轮/中键无"按下"语义，直接处理；XButton 需按下）
                    if (msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN || msg == WM_MOUSEWHEEL)
                    {
                        string? pressed = null;
                        if (msg == WM_MBUTTONDOWN) pressed = "MButton";
                        else if (msg == WM_XBUTTONDOWN)
                        {
                            int btn = (int)((ms.mouseData >> 16) & 0xFFFF);
                            pressed = btn switch { 1 => "XButton1", 2 => "XButton2", _ => null };
                        }
                        else if (msg == WM_MOUSEWHEEL)
                        {
                            int delta = (short)((ms.mouseData >> 16) & 0xFFFF);
                            pressed = delta > 0 ? "WheelUp" : "WheelDown";
                        }

                        if (pressed != null)
                        {
                            uint mods = GetPressedMods();
                            foreach (var combo in _actionsByCombo.Keys)
                            {
                                if (ComboMatches(combo, mods, pressed))
                                {
                                    HotkeyPressed?.Invoke(_actionsByCombo[combo]);
                                    return new IntPtr(1); // 吞掉事件，避免同时触发系统默认行为（如侧键=浏览器后退）
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // 钩子回调内不允许抛异常
                }
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        /// <summary>检查组合串（"Ctrl+XButton1"）在当前修饰键状态下是否匹配按下的键。</summary>
        private static bool ComboMatches(string combo, uint pressedMods, string pressedKey)
        {
            var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 1) return false;

            uint needMods = 0;
            foreach (var p in parts[..^1])
            {
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": needMods |= MOD_CONTROL; break;
                    case "alt": needMods |= MOD_ALT; break;
                    case "shift": needMods |= MOD_SHIFT; break;
                    case "win": needMods |= MOD_WIN; break;
                    default: return false;
                }
            }
            if (needMods != pressedMods) return false;
            return string.Equals(parts[^1], pressedKey, StringComparison.OrdinalIgnoreCase);
        }

        private static uint GetPressedMods()
        {
            uint m = 0;
            if (IsDown(VK_CONTROL)) m |= MOD_CONTROL;
            if (IsDown(VK_MENU)) m |= MOD_ALT;
            if (IsDown(VK_SHIFT)) m |= MOD_SHIFT;
            if (IsDown(VK_LWIN)) m |= MOD_WIN;
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
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
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

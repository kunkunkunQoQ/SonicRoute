using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SonicRoute
{
    /// <summary>
    /// 全局快捷键服务：键盘绑定走 RegisterHotKey（隐藏宿主窗口承载消息循环），
    /// 鼠标键/滚轮绑定（MButton / XButton1 / XButton2 / WheelUp / WheelDown，可带修饰键）
    /// 走低级鼠标钩子（RegisterHotKey 对鼠标键支持不可靠）。
    /// 支持：组合键（Ctrl+Alt+1）、单键（F2 / A / 5）、F 区 F1-F24。
    /// 配置为空的动作不注册（也不回退默认），用于"按 Esc 清除绑定"。
    /// </summary>
    public sealed class HotkeyService : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x1;
        private const uint MOD_CONTROL = 0x2;
        private const uint MOD_SHIFT = 0x4;
        private const uint MOD_WIN = 0x8;

        private readonly Window _host;
        private readonly HwndSource _source;
        private readonly Dictionary<int, string> _actionById = new();
        private readonly MouseHotkeyHook _mouseHook;
        private int _nextId = 1;

        public event Action<string>? HotkeyPressed;

        /// <summary>动作 → 实际注册生效的组合键（"" 或缺失表示该动作未注册成功）。
        /// 供设置页显示"被其他程序占用"等冲突状态。</summary>
        public Dictionary<string, string> RegistrationStatus { get; } = new();

        public HotkeyService()
        {
            _host = new Window
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                Width = 0,
                Height = 0,
                Left = -10000,
                Top = -10000
            };
            _host.Show();
            _host.Hide();
            _source = HwndSource.FromHwnd(new WindowInteropHelper(_host).Handle)!;
            _source.AddHook(WndProc);

            _mouseHook = new MouseHotkeyHook();
            _mouseHook.HotkeyPressed += action => HotkeyPressed?.Invoke(action);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && _actionById.TryGetValue(wParam.ToInt32(), out var action))
            {
                HotkeyPressed?.Invoke(action);
                handled = true;
            }
            return IntPtr.Zero;
        }

        /// <summary>按 config 重新注册全部快捷键（先注销旧的）。
        /// 空串/缺失 = 不绑定（跳过，不回退默认）；
        /// 含鼠标键/滚轮的绑定 → 低级鼠标钩子；其余 → RegisterHotKey（被占用自动回退默认组合）。</summary>
        public void Reload(IReadOnlyDictionary<string, string> hotkeys)
        {
            UnregisterAll();
            RegistrationStatus.Clear();

            var mouseBindings = new List<KeyValuePair<string, string>>();
            var defaults = HotkeyActions.Defaults;

            foreach (var kv in hotkeys)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue; // Esc 清除 = 不绑定任何东西

                if (IsMouseCombo(kv.Value))
                {
                    mouseBindings.Add(new KeyValuePair<string, string>(kv.Value, kv.Key));
                    RegistrationStatus[kv.Key] = kv.Value;
                    continue;
                }

                if (TryRegister(kv.Key, kv.Value)) continue;
                if (defaults.TryGetValue(kv.Key, out var def)
                    && !string.Equals(def, kv.Value, StringComparison.OrdinalIgnoreCase))
                {
                    TryRegister(kv.Key, def); // 配置组合冲突/无效 → 回退默认
                }
            }

            _mouseHook.Reload(mouseBindings);
        }

        /// <summary>组合串最后一段是否为鼠标键/滚轮（这类绑定必须走低级鼠标钩子）。</summary>
        private static bool IsMouseCombo(string combo)
        {
            var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 1) return false;
            return parts[^1].ToLowerInvariant() switch
            {
                "mbutton" or "xbutton1" or "xbutton2" or "wheelup" or "wheeldown" => true,
                _ => false
            };
        }

        private bool TryRegister(string action, string? combo)
        {
            if (string.IsNullOrWhiteSpace(combo)) return false;
            if (!TryParse(combo, out uint mods, out uint vk)) return false;
            int id = _nextId++;
            if (RegisterHotKey(_source.Handle, id, mods, vk))
            {
                _actionById[id] = action;
                RegistrationStatus[action] = combo;
                return true;
            }
            return false;
        }

        private void UnregisterAll()
        {
            foreach (var id in _actionById.Keys)
                UnregisterHotKey(_source.Handle, id);
            _actionById.Clear();
        }

        public void Dispose()
        {
            UnregisterAll();
            _mouseHook.Dispose();
            _source.RemoveHook(WndProc);
            _host.Close();
        }

        internal static bool TryParse(string combo, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 1) return false;

            foreach (var p in parts[..^1])
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

            var key = parts[^1];
            if (key.Length == 1 && key[0] is >= '0' and <= '9') vk = 0x30u + (uint)(key[0] - '0');
            else if (key.Length == 1 && key[0] is >= 'A' and <= 'Z') vk = 0x41u + (uint)(key[0] - 'A');
            else if (key.Length == 1 && key[0] is >= 'a' and <= 'z') vk = 0x41u + (uint)(key[0] - 'a');
            else
            {
                vk = key.ToLowerInvariant() switch
                {
                    "space" => 0x20,
                    "tab" => 0x09,
                    "enter" => 0x0D,
                    "up" => 0x26,
                    "down" => 0x28,
                    "left" => 0x25,
                    "right" => 0x27,
                    "mbutton" => 0x04,   // 鼠标中键（键盘路径不使用，见 IsMouseCombo）
                    "xbutton1" => 0x05,  // 鼠标侧键（键盘路径不使用）
                    "xbutton2" => 0x06,
                    "f1" => 0x70,
                    "f2" => 0x71,
                    "f3" => 0x72,
                    "f4" => 0x73,
                    "f5" => 0x74,
                    "f6" => 0x75,
                    "f7" => 0x76,
                    "f8" => 0x77,
                    "f9" => 0x78,
                    "f10" => 0x79,
                    "f11" => 0x7A,
                    "f12" => 0x7B,
                    "f13" => 0x7C,
                    "f14" => 0x7D,
                    "f15" => 0x7E,
                    "f16" => 0x7F,
                    "f17" => 0x80,
                    "f18" => 0x81,
                    "f19" => 0x82,
                    "f20" => 0x83,
                    "f21" => 0x84,
                    "f22" => 0x85,
                    "f23" => 0x86,
                    "f24" => 0x87,
                    _ => 0
                };
            }
            return vk != 0;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}

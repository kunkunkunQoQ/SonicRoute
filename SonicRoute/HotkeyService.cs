using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SonicRoute
{
    /// <summary>
    /// Windows 全局快捷键（RegisterHotKey）。内部维护一个隐藏宿主窗口承载消息循环，
    /// 应用启动即生效，与主窗口是否显示无关。
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
        /// 某动作配置的组合被其他程序占用（RegisterHotKey 失败）时，自动回退到该动作的默认
        /// 组合，保证快捷键功能始终可用；实际生效的组合记录在 RegistrationStatus 供界面提示。</summary>
        public void Reload(IReadOnlyDictionary<string, string> hotkeys)
        {
            UnregisterAll();
            RegistrationStatus.Clear();
            var defaults = HotkeyActions.Defaults;
            foreach (var kv in hotkeys)
            {
                if (TryRegister(kv.Key, kv.Value)) continue;
                if (defaults.TryGetValue(kv.Key, out var def)
                    && !string.Equals(def, kv.Value, StringComparison.OrdinalIgnoreCase))
                {
                    TryRegister(kv.Key, def); // 配置组合冲突/无效 → 回退默认
                }
            }
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
            _source.RemoveHook(WndProc);
            _host.Close();
        }

        internal static bool TryParse(string combo, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) return false;

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

using System.Collections.Generic;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SonicRoute
{
    /// <summary>快捷键动作定义与组合键解析。</summary>
    public static class HotkeyActions
    {
        public const string ActSwitchOutput = "切换当前应用快捷设备";
        public const string ActMute = "静音当前应用";
        public const string ActVolUp = "增大当前应用音量";
        public const string ActVolDown = "减小当前应用音量";
        public const string ActPanel = "打开快速面板";

        public static readonly string[] All =
        {
            ActVolUp,
            ActVolDown,
            ActSwitchOutput,
            ActMute,
            ActPanel
        };

        public static readonly Dictionary<string, string> Defaults = new()
        {
            [ActVolUp] = "Ctrl+Alt+Up",
            [ActVolDown] = "Ctrl+Alt+Down",
            [ActSwitchOutput] = "Ctrl+Alt+D",
            [ActMute] = "Ctrl+Shift+M",   // Ctrl+Alt+M 常被其他程序注册（本机实测被占用），改用 Ctrl+Shift+M
            [ActPanel] = "Ctrl+Alt+Space",
        };

        /// <summary>动作的本地化显示名。</summary>
        public static string DisplayName(string action) => action switch
        {
            ActSwitchOutput => L10n.T("Act.SwitchOutput"),
            ActMute => L10n.T("Act.Mute"),
            ActVolUp => L10n.T("Act.VolUp"),
            ActVolDown => L10n.T("Act.VolDown"),
            ActPanel => L10n.T("Act.Panel"),
            _ => action
        };

        /// <summary>把按键事件格式化为 "Ctrl+Alt+1" 形式；缺修饰键或纯修饰键返回 null。</summary>
        public static string? Format(KeyEventArgs e)
        {
            var mods = new List<string>();
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods.Add("Ctrl");
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods.Add("Alt");
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods.Add("Shift");
            if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods.Add("Win");
            if (mods.Count == 0) return null;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
                Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Escape)
                return null;

            string keyName = key == Key.Space ? "Space" : key.ToString();
            return string.Join("+", mods) + "+" + keyName;
        }
    }
}

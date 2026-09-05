using System.Collections.Generic;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;

namespace SonicRoute
{
    /// <summary>快捷键动作定义与组合键解析。</summary>
    public static class HotkeyActions
    {
        public const string ActSwitchOutput = "切换当前应用快捷设备";
        public const string ActMute = "静音当前应用";
        public const string ActMuteInput = "麦克风静音当前应用";
        public const string ActVolUp = "增大当前应用音量";
        public const string ActVolDown = "减小当前应用音量";
        public const string ActPanel = "打开快速面板";
        /// <summary>切换全局应用的输出设备（所有有音频会话的应用切到下一个保留设备）。</summary>
        public const string ActSwitchAllOutput = "切换全局应用输出设备";
        /// <summary>一键还原全部应用输出/输入设备为系统默认（所有运行进程 + 音频会话应用）。</summary>
        public const string ActResetAllApps = "还原全部应用默认设备";
        /// <summary>切换全局应用的输入设备（跟随麦克风选项，同 ActSwitchInput）。</summary>
        public const string ActSwitchAllInput = "切换全局应用输入设备";
        /// <summary>切换系统默认输出设备（改的是系统默认，非按应用路由）。</summary>
        public const string ActSetDefaultOutput = "切换系统默认输出设备";
        /// <summary>切换系统默认输入设备（无需开启麦克风选项，始终可用）。</summary>
        public const string ActSetDefaultInput = "切换系统默认输入设备";
        /// <summary>麦克风选项开启后才显示/注册的动作：切换当前应用的录音（输入）设备。</summary>
        public const string ActSwitchInput = "切换当前应用麦克风设备";

        public static readonly string[] All =
        {
            ActVolUp,
            ActVolDown,
            ActSwitchOutput,
            ActMute,
            ActMuteInput,
            ActSwitchAllOutput,
            ActSwitchAllInput,
            ActResetAllApps,
            ActSetDefaultOutput,
            ActSetDefaultInput,
            ActSwitchInput,
            ActPanel
        };

        /// <summary>快捷键分组：L10n 分类键 → 该组动作（用于快捷键设置页分类展示）。</summary>
        public static readonly (string L10nKey, string[] Actions)[] Groups =
        {
            ("Hk.CatVolume", new[] { ActVolUp, ActVolDown, ActMute, ActMuteInput }),
            ("Hk.CatApp", new[] { ActSwitchOutput, ActSwitchInput }),
            ("Hk.CatGlobal", new[] { ActSwitchAllOutput, ActSwitchAllInput, ActResetAllApps, ActSetDefaultOutput, ActSetDefaultInput }),
            ("Hk.CatUi", new[] { ActPanel }),
        };

        public static readonly Dictionary<string, string> Defaults = new()
        {
            [ActVolUp] = "Ctrl+Alt+Up",
            [ActVolDown] = "Ctrl+Alt+Down",
            [ActSwitchOutput] = "Ctrl+Alt+D",
            [ActMute] = "Ctrl+Shift+M",   // Ctrl+Alt+M 常被其他程序注册（本机实测被占用），改用 Ctrl+Shift+M
            [ActMuteInput] = "Ctrl+Shift+N",      // 麦克风静音，与扬声器静音 Ctrl+Shift+M 区分
            [ActSwitchAllOutput] = "Ctrl+Alt+Shift+O", // 切换全局应用输出设备
            [ActSwitchAllInput] = "Ctrl+Alt+Shift+I",  // 切换全局应用输入设备
            [ActResetAllApps] = "Ctrl+Alt+Shift+R",     // 还原全部应用默认设备
            [ActSetDefaultOutput] = "Ctrl+Alt+O", // 切换系统默认输出设备
            [ActSetDefaultInput] = "Ctrl+Alt+I",  // 切换系统默认输入设备（无需麦克风选项）
            [ActSwitchInput] = "Ctrl+Alt+Shift+D", // 麦克风选项开启后：切换当前应用麦克风设备
            [ActPanel] = "Ctrl+Alt+Space",
        };

        /// <summary>动作的本地化显示名。</summary>
        public static string DisplayName(string action) => action switch
        {
            ActSwitchOutput => L10n.T("Act.SwitchOutput"),
            ActMute => L10n.T("Act.Mute"),
            ActMuteInput => L10n.T("Act.MuteInput"),
            ActVolUp => L10n.T("Act.VolUp"),
            ActVolDown => L10n.T("Act.VolDown"),
            ActPanel => L10n.T("Act.Panel"),
            ActResetAllApps => L10n.T("Act.ResetAllApps"),
            ActSwitchAllOutput => L10n.T("Act.SwitchAllOutput"),
            ActSwitchAllInput => L10n.T("Act.SwitchAllInput"),
            ActSetDefaultOutput => L10n.T("Act.SetDefaultOutput"),
            ActSetDefaultInput => L10n.T("Act.SetDefaultInput"),
            ActSwitchInput => L10n.T("Act.SwitchInput"),
            _ => action
        };

        /// <summary>
        /// 把按键事件格式化为 "Ctrl+Alt+1" / "F2" / "A" 形式。
        /// 支持：无修饰键的单键（含 F 区 F1-F24）、带修饰键组合；
        /// 纯修饰键 / Esc（保留给取消）返回 null。
        /// </summary>
        public static string? Format(KeyEventArgs e)
        {
            var mods = new List<string>();
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods.Add("Ctrl");
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods.Add("Alt");
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods.Add("Shift");
            if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods.Add("Win");

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
                Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Escape)
                return null;

            string keyName = key switch
            {
                Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3",
                Key.D4 => "4", Key.D5 => "5", Key.D6 => "6", Key.D7 => "7",
                Key.D8 => "8", Key.D9 => "9",
                Key.Space => "Space",
                _ => key.ToString()
            };
            // 允许单键（无修饰键），如 F2 / A / 5；也允许带修饰键组合
            return mods.Count == 0 ? keyName : string.Join("+", mods) + "+" + keyName;
        }

        /// <summary>把鼠标滚轮事件格式化为 "WheelUp" / "Ctrl+WheelDown" 形式（可带修饰键）。</summary>
        public static string? FormatWheel(MouseWheelEventArgs e)
        {
            string keyName = e.Delta > 0 ? "WheelUp" : "WheelDown";
            var mods = new List<string>();
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods.Add("Ctrl");
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods.Add("Alt");
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods.Add("Shift");
            if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods.Add("Win");
            return mods.Count == 0 ? keyName : string.Join("+", mods) + "+" + keyName;
        }

        /// <summary>
        /// 把鼠标按键事件格式化为 "XButton1" / "Ctrl+XButton2" / "MButton" 形式。
        /// 鼠标左键/右键忽略（避免与 UI 交互冲突），仅中键与侧键可绑定。
        /// </summary>
        public static string? FormatMouse(MouseButtonEventArgs e)
        {
            string? keyName = e.ChangedButton switch
            {
                MouseButton.Middle => "MButton",
                MouseButton.XButton1 => "XButton1",
                MouseButton.XButton2 => "XButton2",
                _ => null
            };
            if (keyName == null) return null;

            var mods = new List<string>();
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods.Add("Ctrl");
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods.Add("Alt");
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods.Add("Shift");
            if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods.Add("Win");
            return mods.Count == 0 ? keyName : string.Join("+", mods) + "+" + keyName;
        }
    }
}

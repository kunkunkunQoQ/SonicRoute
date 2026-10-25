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
        // 原有动作 ID 沿用为“下一个”，保留用户绑定及显式禁用，无需迁移配置或额外写盘。
        public const string ActSwitchOutputNext = "切换当前应用快捷设备";
        public const string ActSwitchOutputPrevious = "切换当前应用上一个输出设备";
        public const string ActMute = "静音当前应用";
        public const string ActMuteInput = "麦克风静音当前应用";
        public const string ActVolUp = "增大当前应用音量";
        public const string ActVolDown = "减小当前应用音量";
        public const string ActSystemVolUp = "增大系统默认输出音量";
        public const string ActSystemVolDown = "减小系统默认输出音量";
        public const string ActPanel = "打开快速面板";
        /// <summary>所有有音频会话的应用切到下一个/上一个保留输出设备。</summary>
        public const string ActSwitchAllOutputNext = "切换全局应用输出设备";
        public const string ActSwitchAllOutputPrevious = "切换全局应用上一个输出设备";
        /// <summary>清除全部应用（含已退出应用）的持久化输出/输入路由；保留音量与系统默认设备。</summary>
        public const string ActResetAllApps = "还原全部应用默认设备";
        /// <summary>切换全局应用的输入设备（跟随麦克风选项，同 ActSwitchInputNext）。</summary>
        public const string ActSwitchAllInputNext = "切换全局应用输入设备";
        public const string ActSwitchAllInputPrevious = "切换全局应用上一个输入设备";
        /// <summary>切换系统默认输出设备（改的是系统默认，非按应用路由）。</summary>
        public const string ActSetDefaultOutputNext = "切换系统默认输出设备";
        public const string ActSetDefaultOutputPrevious = "切换系统默认上一个输出设备";
        /// <summary>切换系统默认输入设备（无需开启麦克风选项，始终可用）。</summary>
        public const string ActSetDefaultInputNext = "切换系统默认输入设备";
        public const string ActSetDefaultInputPrevious = "切换系统默认上一个输入设备";
        /// <summary>麦克风选项开启后才显示/注册的动作：切换当前应用的录音（输入）设备。</summary>
        public const string ActSwitchInputNext = "切换当前应用麦克风设备";
        public const string ActSwitchInputPrevious = "切换当前应用上一个输入设备";

        public static readonly string[] All =
        {
            ActVolUp,
            ActVolDown,
            ActSystemVolUp,
            ActSystemVolDown,
            ActSwitchOutputNext,
            ActMute,
            ActMuteInput,
            ActSwitchAllOutputNext,
            ActSwitchAllInputNext,
            ActResetAllApps,
            ActSetDefaultOutputNext,
            ActSetDefaultInputNext,
            ActSwitchInputNext,
            ActPanel,
            // 原动作的注册/冲突优先级不变；新增方向排在其后，UI顺序由Groups单独定义。
            ActSwitchOutputPrevious,
            ActSwitchInputPrevious,
            ActSwitchAllOutputPrevious,
            ActSwitchAllInputPrevious,
            ActSetDefaultOutputPrevious,
            ActSetDefaultInputPrevious
        };

        /// <summary>快捷键分组：L10n 分类键 → 该组动作（用于快捷键设置页分类展示）。</summary>
        public static readonly (string L10nKey, string[] Actions)[] Groups =
        {
            ("Hk.CatVolume", new[] { ActVolUp, ActVolDown, ActSystemVolUp, ActSystemVolDown, ActMute, ActMuteInput }),
            ("Hk.CatApp", new[] { ActSwitchOutputPrevious, ActSwitchOutputNext, ActSwitchInputPrevious, ActSwitchInputNext }),
            ("Hk.CatGlobal", new[] { ActSwitchAllOutputPrevious, ActSwitchAllOutputNext, ActSwitchAllInputPrevious, ActSwitchAllInputNext, ActResetAllApps, ActSetDefaultOutputPrevious, ActSetDefaultOutputNext, ActSetDefaultInputPrevious, ActSetDefaultInputNext }),
            ("Hk.CatUi", new[] { ActPanel }),
        };

        // 未列出的动作默认未绑定，也没有冲突回退组合；避免空默认项参与冲突关联。
        public static readonly Dictionary<string, string> Defaults = new()
        {
            [ActVolUp] = "Ctrl+Alt+WheelUp",
            [ActVolDown] = "Ctrl+Alt+WheelDown",
            [ActSystemVolUp] = "Ctrl+Alt+Shift+WheelUp",
            [ActSystemVolDown] = "Ctrl+Alt+Shift+WheelDown",
            [ActSwitchOutputNext] = "Ctrl+Alt+D",
            [ActMute] = "Ctrl+Shift+M",   // Ctrl+Alt+M 常被其他程序注册（本机实测被占用），改用 Ctrl+Shift+M
            [ActMuteInput] = "Ctrl+Shift+N",      // 麦克风静音，与扬声器静音 Ctrl+Shift+M 区分
            [ActSwitchAllOutputNext] = "Ctrl+Alt+Shift+O", // 切换全局应用输出设备
            [ActSwitchAllInputNext] = "Ctrl+Alt+Shift+I",  // 切换全局应用输入设备
            [ActResetAllApps] = "Ctrl+Alt+Shift+R",     // 还原全部应用默认设备
            [ActSetDefaultOutputNext] = "Ctrl+Alt+O", // 切换系统默认输出设备
            [ActSetDefaultInputNext] = "Ctrl+Alt+I",  // 切换系统默认输入设备（无需麦克风选项）
            [ActSwitchInputNext] = "Ctrl+Alt+Shift+D", // 麦克风选项开启后：切换当前应用麦克风设备
            [ActPanel] = "Ctrl+Alt+Space",
        };

        /// <summary>动作的本地化显示名。</summary>
        public static string DisplayName(string action) => action switch
        {
            ActSwitchOutputPrevious => L10n.T("Act.SwitchOutput.Previous"),
            ActSwitchOutputNext => L10n.T("Act.SwitchOutput.Next"),
            ActMute => L10n.T("Act.Mute"),
            ActMuteInput => L10n.T("Act.MuteInput"),
            ActVolUp => L10n.T("Act.VolUp"),
            ActVolDown => L10n.T("Act.VolDown"),
            ActSystemVolUp => L10n.T("Act.SystemVolUp"),
            ActSystemVolDown => L10n.T("Act.SystemVolDown"),
            ActPanel => L10n.T("Act.Panel"),
            ActResetAllApps => L10n.T("Act.ResetAllApps"),
            ActSwitchAllOutputPrevious => L10n.T("Act.SwitchAllOutput.Previous"),
            ActSwitchAllOutputNext => L10n.T("Act.SwitchAllOutput.Next"),
            ActSwitchAllInputPrevious => L10n.T("Act.SwitchAllInput.Previous"),
            ActSwitchAllInputNext => L10n.T("Act.SwitchAllInput.Next"),
            ActSetDefaultOutputPrevious => L10n.T("Act.SetDefaultOutput.Previous"),
            ActSetDefaultOutputNext => L10n.T("Act.SetDefaultOutput.Next"),
            ActSetDefaultInputPrevious => L10n.T("Act.SetDefaultInput.Previous"),
            ActSetDefaultInputNext => L10n.T("Act.SetDefaultInput.Next"),
            ActSwitchInputPrevious => L10n.T("Act.SwitchInput.Previous"),
            ActSwitchInputNext => L10n.T("Act.SwitchInput.Next"),
            _ => action
        };

        /// <summary>应用录音路由两个方向共用麦克风选项；系统默认输入始终可用。</summary>
        public static bool RequiresMicrophoneOption(string action) => action is
            ActSwitchInputPrevious or ActSwitchInputNext or ActSwitchAllInputPrevious or ActSwitchAllInputNext;

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

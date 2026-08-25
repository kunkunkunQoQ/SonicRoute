using System.Collections.Generic;
using System.ComponentModel;

namespace SonicRoute
{
    /// <summary>
    /// 中英文本地化。XAML 用 {Binding [key], Source={x:Static local:L10n.Instance}}，
    /// 代码用 L10n.T("key")。切换语言时刷新全部绑定。
    /// </summary>
    public sealed class L10n : INotifyPropertyChanged
    {
        public static L10n Instance { get; } = new();
        public static string CurrentLanguage { get; private set; } = "zh-CN";

        public string this[string key] =>
            Tables.TryGetValue(CurrentLanguage, out var t) && t.TryGetValue(key, out var v)
                ? v
                : (Tables["zh-CN"].TryGetValue(key, out var zh) ? zh : key);

        public void SetLanguage(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) lang = "zh-CN";
            if (CurrentLanguage == lang) return;
            CurrentLanguage = lang;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static string T(string key) => Instance[key];

        private static readonly Dictionary<string, Dictionary<string, string>> Tables = new()
        {
            ["zh-CN"] = new Dictionary<string, string>
            {
                // 应用名
                ["App.Name"] = "音跃",
                ["App.NameFull"] = "音跃 SonicRoute",
                ["App.About"] = "音跃 SonicRoute v1.02 · 困困困",

                // 导航
                ["Nav.Overview"] = "概览",
                ["Nav.Apps"] = "应用",
                ["Nav.Hotkeys"] = "快捷键",
                ["Nav.Theme"] = "主题",
                ["Nav.Settings"] = "设置",

                // 概览
                ["Ov.CurrentApp"] = "当前应用",
                ["Ov.Refresh"] = "↻ 刷新",
                ["Ov.Output"] = "🔊 输出设备",
                ["Ov.Input"] = "🎤 输入设备",
                ["Ov.Volume"] = "🔉 应用音量",
                ["Ov.Mute"] = "🔇 静音",
                ["Ov.Unmute"] = "🔈 取消静音",
                ["Ov.Apply"] = "✓ 应用设置",
                ["Ov.NoSession"] = "该应用无音量会话",
                ["Ov.NoAudio"] = "当前应用暂无音频",
                ["Ov.ApplyOk"] = "✓ 已应用：{0}",
                ["Ov.SwitchOk"] = "✓ 已切换 {0} → {1}",
                ["Ov.VolOk"] = "✓ 音量 {0}%",
                ["Ov.VolFail"] = "✗ 音量设置失败",
                ["Ov.Muted"] = "✓ 已静音",
                ["Ov.Unmuted"] = "✓ 已取消静音",
                ["Ov.MuteMic"] = "🎤 麦克风静音",
                ["Ov.MicUnmute"] = "🎤 取消麦克风静音",
                ["Ov.MicMuted"] = "✓ 麦克风已静音",
                ["Ov.MicUnmuted"] = "✓ 麦克风已取消静音",
                ["Ov.MicNoSession"] = "⚠ 该应用无输入会话",
                ["Ov.Default"] = "（默认）",
                ["Ov.Unset"] = "未设置",
                ["Ov.Current"] = "当前：",
                ["Ov.CurrentUnavailable"] = "当前设备不可用",
                ["Ov.Detecting"] = "正在检测前台应用…",
                ["Ov.NoOutput"] = "未选择输出设备",
                ["Ov.NoInput"] = "未选择输入设备",

                // 应用页
                ["Apps.Title"] = "有音频的应用",
                ["Apps.Refresh"] = "↻ 刷新",
                ["Apps.SelectHint"] = "请选择左侧应用",
                ["Apps.Output"] = "输出设备",
                ["Apps.Input"] = "输入设备",
                ["Apps.Volume"] = "音量",
                ["Apps.Mute"] = "🔇 静音",
                ["Apps.Unmute"] = "🔈 取消静音",
                ["Apps.Apply"] = "✓ 应用设置",

                // 设置页
                ["St.Settings"] = "设置",
                ["St.KeepDevices"] = "保留的设备（快速切换界面只显示勾选的设备）",
                ["St.SelectAll"] = "☑ 全选",
                ["St.ClearAll"] = "☐ 取消全选",
                ["St.Output"] = "🔊 播放设备（输出）",
                ["St.Input"] = "🎤 录音设备（输入）",
                ["St.DeviceNames"] = "设备名称",
                ["St.DeviceNamesHint"] = "自定义设备显示名称（留空则用默认名称）",
                ["St.DefaultApp"] = "默认打开的应用",
                ["St.DefaultAppRecent"] = "最近打开的程序",
                ["St.DefaultAppLast"] = "上次操作的应用",
                ["St.DefaultAppFixed"] = "指定应用",
                ["St.Language"] = "语言",
                ["St.LangZh"] = "中文",
                ["St.LangEn"] = "English",
                ["St.Theme"] = "主题",
                ["St.ThemeMode"] = "主题模式",
                ["St.ThemeSystem"] = "跟随系统",
                ["St.ThemeLight"] = "浅色",
                ["St.ThemeDark"] = "深色",
                ["St.Accent"] = "强调色",
                ["St.AccentBlue"] = "蓝色",
                ["St.AccentGreen"] = "绿色",
                ["St.AccentPurple"] = "紫色",
                ["Th.Custom"] = "自定义",
                ["St.StartMinimized"] = "启动时最小化到托盘（不显示主窗口）",
                ["St.StartPanel"] = "启动时显示快速面板",
                ["St.AutoStart"] = "开机自启",
                ["St.FixedAppHint"] = "选择指定应用后，概览默认显示它",
                ["St.DefaultAppHint"] = "概览默认应用：最近打开 / 上次操作 / 指定",
                ["St.Opacity"] = "背景透明度",
                ["St.OpacityHint"] = "快速面板与窗口背景的透明度（越低越透）",

                // 快捷键页
                ["Hk.Title"] = "全局快捷键",
                ["Hk.Hint"] = "点击右侧「修改」后按下新组合键即可重新绑定。",
                ["Hk.Change"] = "修改",
                ["Act.SwitchOutput"] = "切换当前应用快捷设备",
                ["Act.SwitchInput"] = "切换当前应用输入设备",
                ["Act.Mute"] = "静音当前应用",
                ["Act.MuteInput"] = "麦克风静音当前应用",
                ["Act.VolUp"] = "增大当前应用音量",
                ["Act.VolDown"] = "减小当前应用音量",
                ["Act.Panel"] = "打开快速面板",

                // 快速面板
                ["Qp.CurrentApp"] = "当前应用：{0}",
                ["Qp.NoAudio"] = "当前应用：暂无音频",
                ["Qp.Output"] = "🔊 输出",
                ["Qp.Input"] = "🎤 输入",
                ["Qp.Volume"] = "🔉 音量",
                ["Qp.Mute"] = "🔇 静音",
                ["Qp.Unmute"] = "🔈 取消静音",
                ["Qp.Settings"] = "⚙ 设置",
                ["Qp.Unset"] = "未设置",
                ["Qp.NoAudioMsg"] = "当前应用暂无音频",
                ["Qp.SwitchOk"] = "✓ 已切换 {0} → {1}",
                ["Qp.VolOk"] = "✓ 音量 {0}%",
                ["Qp.VolFail"] = "✗ 音量设置失败",
                ["Qp.Muted"] = "✓ 已静音",
                ["Qp.Unmuted"] = "✓ 已取消静音",
                ["Qp.MicMute"] = "🎤 麦克风静音",
                ["Qp.MicUnmute"] = "🎤 取消麦克风静音",
                ["Qp.MicMuted"] = "✓ 麦克风已静音",
                ["Qp.MicUnmuted"] = "✓ 麦克风已取消静音",
                ["Qp.MicNoSession"] = "⚠ 该应用无输入会话",
                ["Qp.LoadFail"] = "加载失败",
                ["Qp.Detecting"] = "当前应用：正在检测…",

                // 托盘
                ["Tray.OpenMain"] = "打开完整界面",
                ["Tray.OpenPanel"] = "快速切换面板",
                ["Tray.Exit"] = "退出",

                // 快捷键捕键
                ["Hk.PressNew"] = "请按下新组合键…（Esc 取消）",
                ["Hk.Conflict"] = "该组合键已被占用或无效",
                ["Hk.Updated"] = "✓ 快捷键已更新",
                ["Hk.ConflictTip"] = "组合键 {0} 被其他程序占用，已自动使用 {1}（可在下方重新设置）",
                ["Hk.Unregistered"] = "该快捷键未能注册（可能与其他程序冲突）",
            },
            ["en-US"] = new Dictionary<string, string>
            {
                ["App.Name"] = "SonicRoute",
                ["App.NameFull"] = "SonicRoute",
                ["App.About"] = "SonicRoute v1.02 · by 困困困",

                ["Nav.Overview"] = "Overview",
                ["Nav.Apps"] = "Apps",
                ["Nav.Hotkeys"] = "Hotkeys",
                ["Nav.Theme"] = "Theme",
                ["Nav.Settings"] = "Settings",

                ["Ov.CurrentApp"] = "Current App",
                ["Ov.Refresh"] = "↻ Refresh",
                ["Ov.Output"] = "🔊 Output Device",
                ["Ov.Input"] = "🎤 Input Device",
                ["Ov.Volume"] = "🔉 App Volume",
                ["Ov.Mute"] = "🔇 Mute",
                ["Ov.Unmute"] = "🔈 Unmute",
                ["Ov.Apply"] = "✓ Apply",
                ["Ov.NoSession"] = "No volume session",
                ["Ov.NoAudio"] = "Current app has no audio",
                ["Ov.ApplyOk"] = "✓ Applied: {0}",
                ["Ov.SwitchOk"] = "✓ Switched {0} → {1}",
                ["Ov.VolOk"] = "✓ Volume {0}%",
                ["Ov.VolFail"] = "✗ Failed to set volume",
                ["Ov.Muted"] = "✓ Muted",
                ["Ov.Unmuted"] = "✓ Unmuted",
                ["Ov.MuteMic"] = "🎤 Mute Mic",
                ["Ov.MicUnmute"] = "🎤 Unmute Mic",
                ["Ov.MicMuted"] = "✓ Mic muted",
                ["Ov.MicUnmuted"] = "✓ Mic unmuted",
                ["Ov.MicNoSession"] = "⚠ No input session",
                ["Ov.Default"] = "(Default)",
                ["Ov.Unset"] = "Not set",
                ["Ov.Current"] = "Current: ",
                ["Ov.CurrentUnavailable"] = "Current device unavailable",
                ["Ov.Detecting"] = "Detecting foreground app…",
                ["Ov.NoOutput"] = "No output selected",
                ["Ov.NoInput"] = "No input selected",

                ["Apps.Title"] = "Apps with Audio",
                ["Apps.Refresh"] = "↻ Refresh",
                ["Apps.SelectHint"] = "Select an app",
                ["Apps.Output"] = "Output Device",
                ["Apps.Input"] = "Input Device",
                ["Apps.Volume"] = "Volume",
                ["Apps.Mute"] = "🔇 Mute",
                ["Apps.Unmute"] = "🔈 Unmute",
                ["Apps.Apply"] = "✓ Apply",

                ["St.Settings"] = "Settings",
                ["St.KeepDevices"] = "Kept devices (quick switch shows only checked)",
                ["St.SelectAll"] = "☑ Select all",
                ["St.ClearAll"] = "☐ Select none",
                ["St.Output"] = "🔊 Playback (Output)",
                ["St.Input"] = "🎤 Recording (Input)",
                ["St.DeviceNames"] = "Device Names",
                ["St.DeviceNamesHint"] = "Custom device display names (empty = default)",
                ["St.DefaultApp"] = "Default App",
                ["St.DefaultAppRecent"] = "Recently opened app",
                ["St.DefaultAppLast"] = "Last used app",
                ["St.DefaultAppFixed"] = "Specific app",
                ["St.Language"] = "Language",
                ["St.LangZh"] = "中文",
                ["St.LangEn"] = "English",
                ["St.Theme"] = "Theme",
                ["St.ThemeMode"] = "Theme Mode",
                ["St.ThemeSystem"] = "Follow system",
                ["St.ThemeLight"] = "Light",
                ["St.ThemeDark"] = "Dark",
                ["St.Accent"] = "Accent",
                ["St.AccentBlue"] = "Blue",
                ["St.AccentGreen"] = "Green",
                ["St.AccentPurple"] = "Purple",
                ["Th.Custom"] = "Custom",
                ["St.StartMinimized"] = "Start minimized to tray (no window)",
                ["St.StartPanel"] = "Show quick panel on start",
                ["St.AutoStart"] = "Run at startup",
                ["St.FixedAppHint"] = "Overview defaults to the selected app",
                ["St.DefaultAppHint"] = "Default app: Recent / Last used / Specific",
                ["St.Opacity"] = "Background Opacity",
                ["St.OpacityHint"] = "Opacity of the quick panel and window background (lower = more transparent)",

                ["Hk.Title"] = "Global Hotkeys",
                ["Hk.Hint"] = "Click 'Change' then press the new key combination.",
                ["Hk.Change"] = "Change",
                ["Act.SwitchOutput"] = "Switch current app device",
                ["Act.SwitchInput"] = "Switch current app input device",
                ["Act.Mute"] = "Mute current app",
                ["Act.MuteInput"] = "Mute current app mic",
                ["Act.VolUp"] = "Increase current app volume",
                ["Act.VolDown"] = "Decrease current app volume",
                ["Act.Panel"] = "Open quick panel",

                ["Qp.CurrentApp"] = "Current app: {0}",
                ["Qp.NoAudio"] = "Current app: no audio",
                ["Qp.Output"] = "🔊 Output",
                ["Qp.Input"] = "🎤 Input",
                ["Qp.Volume"] = "🔉 Volume",
                ["Qp.Mute"] = "🔇 Mute",
                ["Qp.Unmute"] = "🔈 Unmute",
                ["Qp.Settings"] = "⚙ Settings",
                ["Qp.Unset"] = "Not set",
                ["Qp.NoAudioMsg"] = "Current app has no audio",
                ["Qp.SwitchOk"] = "✓ Switched {0} → {1}",
                ["Qp.VolOk"] = "✓ Volume {0}%",
                ["Qp.VolFail"] = "✗ Failed to set volume",
                ["Qp.Muted"] = "✓ Muted",
                ["Qp.Unmuted"] = "✓ Unmuted",
                ["Qp.MicMute"] = "🎤 Mute Mic",
                ["Qp.MicUnmute"] = "🎤 Unmute Mic",
                ["Qp.MicMuted"] = "✓ Mic muted",
                ["Qp.MicUnmuted"] = "✓ Mic unmuted",
                ["Qp.MicNoSession"] = "⚠ No input session",
                ["Qp.LoadFail"] = "Load failed",
                ["Qp.Detecting"] = "Current app: detecting…",

                ["Tray.OpenMain"] = "Open Full UI",
                ["Tray.OpenPanel"] = "Quick Switch Panel",
                ["Tray.Exit"] = "Exit",

                ["Hk.PressNew"] = "Press new key combination… (Esc to cancel)",
                ["Hk.Conflict"] = "Key combination occupied or invalid",
                ["Hk.Updated"] = "✓ Hotkey updated",
                ["Hk.ConflictTip"] = "{0} is occupied by another program; using {1} instead (re-assign below)",
                ["Hk.Unregistered"] = "This hotkey could not be registered (likely conflicts with another program)",
            }
        };
    }
}

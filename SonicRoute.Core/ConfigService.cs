using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SonicRoute.Core
{
    /// <summary>应用配置（持久化到 %LocalAppData%\SonicRoute\config.json）。</summary>
    public sealed class AppConfig
    {
        /// <summary>快速界面隐藏的播放设备（短 ID 列表）。</summary>
        public List<string> HiddenOutputDevices { get; set; } = new();

        /// <summary>快速界面隐藏的录音设备（短 ID 列表）。</summary>
        public List<string> HiddenInputDevices { get; set; } = new();

        public bool StartMinimized { get; set; } = true;

        /// <summary>全局快捷键：动作名 → 组合键描述（如 "Ctrl+Alt+1"）。</summary>
        public Dictionary<string, string> Hotkeys { get; set; } = new();

        /// <summary>设备自定义名称：短 ID → 显示名（空则用默认名称）。</summary>
        public Dictionary<string, string> DeviceNames { get; set; } = new();

        /// <summary>应用自定义名称：进程名 → 显示名（空则用默认名称）。通知/列表/面板/概览统一显示。</summary>
        public Dictionary<string, string> AppNames { get; set; } = new();

        /// <summary>禁用自动切换的应用（进程名列表）：这些应用不会被自动选为"当前应用"（前台跟随/最近使用等），但仍可手动选择。</summary>
        public List<string> DisabledAutoSwitchApps { get; set; } = new();

        /// <summary>界面语言：空 = 首次启动跟随系统（zh-CN / en-US / ja-JP / ko-KR / fr-FR / de-DE / es-ES / ru-RU）。</summary>
        public string Language { get; set; } = "";

        /// <summary>主题模式：system / light / dark。</summary>
        public string ThemeMode { get; set; } = "system";

        /// <summary>强调色：blue / green / purple。</summary>
        public string Accent { get; set; } = "blue";

        /// <summary>默认打开的应用：recent(最近使用) / last(上次操作) / fixed(指定)。</summary>
        public string DefaultAppMode { get; set; } = "recent";

        /// <summary>指定默认应用（进程名）。</summary>
        public string FixedAppName { get; set; } = "";

        /// <summary>上次操作的应用（进程名），用于 last 模式。</summary>
        public string LastUsedAppName { get; set; } = "";

        /// <summary>启动时显示快速面板。</summary>
        public bool StartPanelOnStart { get; set; } = false;

        /// <summary>开机自启（写入 HKCU\...\Run）。</summary>
        public bool AutoStart { get; set; } = false;

        /// <summary>窗口/面板背景透明度（60–100，默认 85：适当通透、保持可读）。</summary>
        public int BackgroundOpacity { get; set; } = 85;

        /// <summary>实验模式已解锁（设置页底部点击作者名 5 次触发，持久化后显示实验模式开关）。</summary>
        public bool ExperimentalUnlocked { get; set; }

        /// <summary>实验模式开关（需先解锁，重启生效）。</summary>
        public bool ExperimentalMode { get; set; }

        /// <summary>实验模式 - 麦克风选项（需开启实验模式，重启生效；开启后显示麦克风相关设置与快捷键）。</summary>
        public bool ExperimentalMic { get; set; }

        /// <summary>实验设置 - 麦克风相关选项是否在快捷面板显示（需实验模式+麦克风选项，重启生效）。</summary>
        public bool MicInPanel { get; set; } = true;

        /// <summary>折叠设置页"保留的设备/设备名称"区块（含麦克风），默认开启；开关位于设置页（不依赖实验模式）。</summary>
        public bool CollapseDeviceSections { get; set; } = true;

        /// <summary>实验设置 - OSD 显示位置（9 宫格：TL/T/TR/L/C/R/BL/B/BR；"Custom" 用自定义坐标）。</summary>
        public string OsdPosition { get; set; } = "TR";

        /// <summary>实验设置 - 关闭完整界面时释放 UI 占用的内存（真正关闭窗口，下次打开重建，实时生效）。默认开启。</summary>
        public bool FreeUIMemoryOnClose { get; set; } = true;

        /// <summary>实验设置 - 关闭快速面板时释放面板 UI 占用的内存（子选项，实时生效，独立于主开关）。</summary>
        public bool FreePanelUIMemory { get; set; }

        /// <summary>实验设置 - OSD 水平偏移（像素，右/下为正；非自定义模式生效）。</summary>
        public int OsdOffsetX { get; set; }

        /// <summary>实验设置 - OSD 垂直偏移（像素，右/下为正；非自定义模式生效）。</summary>
        public int OsdOffsetY { get; set; }

        /// <summary>实验设置 - 自定义 OSD 位置 X（屏幕像素坐标，-1 表示未设置回退默认）。</summary>
        public int OsdCustomX { get; set; } = -1;

        /// <summary>实验设置 - 自定义 OSD 位置 Y（屏幕像素坐标，-1 表示未设置回退默认）。</summary>
        public int OsdCustomY { get; set; } = -1;
    }

    public static class ConfigService
    {
        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonicRoute",
            "config.json");

        // 内存缓存：单实例进程内唯一写者是本进程，Save 时同步更新缓存；
        // 高频调用（托盘滚轮每格、快捷键每次、设备名称每键）直接读缓存，
        // 避免反复读盘 + 反序列化，降低 GC 压力与内存波动。
        private static AppConfig? _cache;
        private static readonly object _lock = new();

        public static AppConfig Load()
        {
            lock (_lock)
            {
                if (_cache != null) return _cache;
            }
            AppConfig cfg;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                else
                {
                    cfg = new AppConfig();
                }
            }
            catch
            {
                // 读取失败回退默认
                cfg = new AppConfig();
            }
            lock (_lock)
            {
                return _cache ??= cfg;
            }
        }

        public static void Save(AppConfig config)
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(ConfigPath,
                    JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 保存失败不阻断运行
            }
            lock (_lock)
            {
                _cache = config;
            }
        }
    }
}

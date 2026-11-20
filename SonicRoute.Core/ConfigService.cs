using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SonicRoute.Core.Models;

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

        /// <summary>Portable editions check once at startup when the last attempt is at least 24 hours old.</summary>
        public bool AutoCheckUpdates { get; set; } = false;
        public string IgnoredUpdateVersion { get; set; } = "";
        public DateTime LastUpdateCheckUtc { get; set; } = DateTime.MinValue;

        /// <summary>全局快捷键：动作名 → 组合键描述（如 "Ctrl+Alt+1"）。</summary>
        public Dictionary<string, string> Hotkeys { get; set; } = new();

        /// <summary>设备自定义名称：短 ID → 显示名（空则用默认名称）。</summary>
        public Dictionary<string, string> DeviceNames { get; set; } = new();

        /// <summary>应用自定义名称：进程名 → 显示名（空则用默认名称）。通知/列表/面板/概览统一显示。</summary>
        public Dictionary<string, string> AppNames { get; set; } = new();

        /// <summary>应用自定义图标：进程名 → Icons 目录中的受管 PNG 文件名。</summary>
        public Dictionary<string, string> AppIcons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>禁用自动切换的应用（进程名列表）：这些应用不会被自动选为"当前应用"（前台跟随/最近使用等），但仍可手动选择。</summary>
        public List<string> DisabledAutoSwitchApps { get; set; } = new();

        /// <summary>不在快速面板显示的应用（进程名列表，不区分大小写）。</summary>
        public List<string> HiddenPanelApps { get; set; } = new();

        /// <summary>托盘常用规则，按规则 Id 保存，顺序与收藏顺序一致。</summary>
        public List<string> FavoriteRuleIds { get; set; } = new();

        /// <summary>自动化列表的自定义展示顺序（规则 Id）；空列表按名称排序，不影响触发执行顺序。</summary>
        public List<string> AutomationRuleOrder { get; set; } = new();

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

        /// <summary>快速面板固定高度（px，350–850，默认 450）：展开/收起不改变；内容超出在应用区域滚动。</summary>
        public int QuickPanelHeight { get; set; } = 450;

        /// <summary>在附加面板中展开全部输出设备音量；仅用户点击切换时保存。</summary>
        public bool QuickPanelShowAllOutputVolumes { get; set; } = false;

        /// <summary>手机音频伴随面板开关；关闭快速面板时保留，下次打开恢复。</summary>
        public bool QuickPanelShowPhoneAudio { get; set; } = true;

        /// <summary>Null lets existing paired installations restore Windows Link on startup.</summary>
        public bool? WindowsLinkEnabled { get; set; }

        public string AirPlayReceiverName { get; set; } = "";
        public int AirPlayVideoDecoder { get; set; }
        public bool AirPlayRequireCode { get; set; }

        /// <summary>额外音量面板仅显示保留的输出设备；独立于顶部切换系统默认设备开关。</summary>
        public bool PanelOutputVolumesRetainedOnly { get; set; } = false;

        /// <summary>主题页：快速面板内显示每应用实时声音活动；关闭时不启动 Meter，显示与旧版一致。</summary>
        public bool ShowAppPeakMeter { get; set; } = true;

        /// <summary>快速面板顶部下拉框切换设备时更改系统默认输出设备（默认开；保留已保存选择）。</summary>
        public bool PanelChangeSystemDefault { get; set; } = true;

        /// <summary>快速面板位置模式：default=任务栏右下角 / custom=自定义坐标（主题页拖拽调整，逻辑同 OSD）。</summary>
        public string QuickPanelPosMode { get; set; } = "default";
        public int QuickPanelCustomX { get; set; } = -1;
        public int QuickPanelCustomY { get; set; } = -1;

        /// <summary>开机自启（写入 HKCU\...\Run，正常/绿色版）。</summary>
        public bool AutoStart { get; set; } = false;

        /// <summary>开机自启（商店版 MSIX StartupTask，与正常版 AutoStart 分开存储，互不影响）。</summary>
        public bool AutoStartStore { get; set; } = false;

        /// <summary>音量调节步进（1–20%，默认 4）：托盘滚轮、面板行滑块滚轮、音量±快捷键共用，设置页可自定义。</summary>
        public int VolumeStep { get; set; } = 4;

        /// <summary>托盘滚轮音量/Alt设备切换区域：默认false=仅音跃图标；true=整个托盘通知区。保留已保存选择。</summary>
        public bool TrayWheelEverywhere { get; set; } = false;
        /// <summary>暂停托盘区域滚轮音量和Alt设备切换，保存后跨重启生效；不影响全局快捷键。</summary>
        public bool TrayWheelPaused { get; set; } = false;
        /// <summary>窗口/面板背景透明度（60–100，默认 85：适当通透、保持可读）。</summary>
        public int BackgroundOpacity { get; set; } = 85;

        /// <summary>实验模式已解锁（设置页底部点击作者名 5 次触发，持久化后显示实验模式开关）。</summary>
        public bool ExperimentalUnlocked { get; set; }

        /// <summary>实验模式开关（需先解锁，重启生效）。</summary>
        public bool ExperimentalMode { get; set; }

        /// <summary>实验设置 - 持续定时校验前台与麦克风；默认关闭，通知异常时仍临时恢复。</summary>
        public bool BackgroundPollingFallbackEnabled { get; set; } = false;

        /// <summary>主题设置：使用快速面板简洁单行布局；缺省开启，保留旧配置键。</summary>
        public bool QuickPanelUseLegacyAppRows { get; set; } = true;

        /// <summary>简洁布局子选项：在右侧下拉入口显示播放设备名称；默认关闭。</summary>
        public bool QuickPanelCompactShowOutputName { get; set; } = false;

        /// <summary>主题设置：完整布局显示播放设备名；默认关闭并显示应用名，名称关闭时不查询播放路由。</summary>
        public bool QuickPanelShowOutputName { get; set; } = false;

        /// <summary>实验设置：记录自动化执行详情；缺省关闭，不读取或创建历史存储。</summary>
        public bool ExecutionHistoryEnabled { get; set; } = false;
        /// <summary>执行详情的磁盘容量上限（MiB），1–64，默认4。</summary>
        public int ExecutionHistoryLimitMb { get; set; } = 4;

        /// <summary>麦克风选项默认开启，显示输入设备设置与切换快捷键；保留已保存的关闭选择，不依赖实验模式。</summary>
        public bool ExperimentalMic { get; set; } = true;

        /// <summary>实验设置 - 麦克风相关选项是否在快速面板显示（需实验模式+麦克风选项，重启生效）。</summary>
        public bool MicInPanel { get; set; } = false;

        /// <summary>折叠设置页"保留的设备/设备名称"区块（含麦克风），默认开启；开关位于设置页（不依赖实验模式）。</summary>
        public bool CollapseDeviceSections { get; set; } = true;

        /// <summary>主题 - OSD 显示位置（"TR"=主显示器右上角；"Custom"=用户拖拽保存的自定义坐标）。</summary>
        public string OsdPosition { get; set; } = "TR";



        /// <summary>实验设置 - OSD 水平偏移（像素，右/下为正；非自定义模式生效）。</summary>
        public int OsdOffsetX { get; set; }

        /// <summary>实验设置 - OSD 垂直偏移（像素，右/下为正；非自定义模式生效）。</summary>
        public int OsdOffsetY { get; set; }

        /// <summary>旧版 OSD 自定义位置 X（WPF DIP）；Custom 模式允许负坐标，新拖拽另存物理坐标。</summary>
        public int OsdCustomX { get; set; } = -1;

        /// <summary>旧版 OSD 自定义位置 Y（WPF DIP）；位置是否启用由 OsdPosition 决定。</summary>
        public int OsdCustomY { get; set; } = -1;

        /// <summary>新拖拽位置以物理像素保存；null 时沿用旧版 DIP 坐标，Custom 模式允许负坐标。</summary>
        public int? OsdCustomPhysicalX { get; set; }
        public int? OsdCustomPhysicalY { get; set; }

        /// <summary>主题 - OSD 显示宽度（逻辑像素，180~600，默认 240；调整模式下拖手柄横向自由拉长缩短）。</summary>
        public int OsdWidth { get; set; } = 240;

        /// <summary>主题 - 音量 OSD 专用布局与进度条；关闭时不创建、更新或动画其控件。</summary>
        public bool OsdVolumeVisualEnabled { get; set; } = true;

        /// <summary>主题 - OSD 字号倍率（0.7~2.0，默认 1.0；调整模式下拖手柄纵向调整）。</summary>
        /// <summary>主题 - OSD 淡入时长（ms，0~500，默认 100；0 = 禁用淡入直接显示）。</summary>
        public int OsdFadeInMs { get; set; } = 100;

        /// <summary>主题 - OSD 淡出时长（ms，0~1000，默认 200；0 = 禁用淡出直接隐藏）。</summary>
        public int OsdFadeOutMs { get; set; } = 200;
        /// <summary>主题 - 所有录音设备静音时 OSD 常驻（默认关闭）。</summary>
        public bool MicMuteOsdPersistent { get; set; }

        /// <summary>旧版监听偏好，用于迁移常驻设置和保留原有短时提示。</summary>
        public bool MicMuteOsdTrackInputMuted { get; set; } = true;

        private bool? _micMuteOsdDefaultInputPersistent;
        /// <summary>主题 - 默认录音设备静音时 OSD 常驻。</summary>
        public bool MicMuteOsdDefaultInputPersistent
        {
            get => _micMuteOsdDefaultInputPersistent ?? (MicMuteOsdPersistent && MicMuteOsdTrackInputMuted);
            set => _micMuteOsdDefaultInputPersistent = value;
        }

        public double OsdFontScale { get; set; } = 1.0;

        /// <summary>JSON can explicitly assign null despite the declared non-null contracts.</summary>
        public void NormalizeCollections()
        {
            HiddenOutputDevices = NormalizeList(HiddenOutputDevices);
            HiddenInputDevices = NormalizeList(HiddenInputDevices);
            DisabledAutoSwitchApps = NormalizeList(DisabledAutoSwitchApps);
            HiddenPanelApps = NormalizeList(HiddenPanelApps);
            FavoriteRuleIds = NormalizeList(FavoriteRuleIds);
            AutomationRuleOrder = NormalizeList(AutomationRuleOrder);
            Hotkeys = NormalizeMap(Hotkeys);
            DeviceNames = NormalizeMap(DeviceNames);
            AppNames = NormalizeMap(AppNames);
            AppIcons = AppIconFiles.NormalizeMappings(AppIcons);
        }

        private static List<string> NormalizeList(List<string>? source) =>
            source == null ? new() : source.FindAll(value => !string.IsNullOrWhiteSpace(value));

        private static Dictionary<string, string> NormalizeMap(Dictionary<string, string>? source)
        {
            var result = new Dictionary<string, string>(source?.Comparer ?? StringComparer.Ordinal);
            if (source != null)
                foreach (var pair in source)
                    if (!string.IsNullOrWhiteSpace(pair.Key)) result[pair.Key] = pair.Value ?? "";
            return result;
        }

        /// <summary>Called by the settings owner before handing work to a worker; mutations must use that same owner.</summary>
        public AppConfig Snapshot()
        {
            var copy = (AppConfig)MemberwiseClone();
            copy.HiddenOutputDevices = new(HiddenOutputDevices ?? new());
            copy.HiddenInputDevices = new(HiddenInputDevices ?? new());
            copy.DisabledAutoSwitchApps = new(DisabledAutoSwitchApps ?? new());
            copy.HiddenPanelApps = new(HiddenPanelApps ?? new());
            copy.FavoriteRuleIds = new(FavoriteRuleIds ?? new());
            copy.AutomationRuleOrder = new(AutomationRuleOrder ?? new());
            copy.Hotkeys = new(Hotkeys ?? new(), Hotkeys?.Comparer ?? StringComparer.Ordinal);
            copy.DeviceNames = new(DeviceNames ?? new(), DeviceNames?.Comparer ?? StringComparer.Ordinal);
            copy.AppNames = new(AppNames ?? new(), AppNames?.Comparer ?? StringComparer.Ordinal);
            copy.AppIcons = new(AppIcons ?? new(), AppIcons?.Comparer ?? StringComparer.OrdinalIgnoreCase);
            return copy;
        }
    }

    public static class ConfigService
    {
        public static string ConfigPath => Path.Combine(
            AppDataRoot.DirectoryPath,
            "config.json");
        public static string BackupPath => ConfigPath + ".bak";

        public sealed class LoadWarningInfo
        {
            public string? PreservedPath { get; }
            public bool UsedBackup { get; }
            public bool SavesBlocked { get; }
            internal LoadWarningInfo(string? preservedPath, bool usedBackup, bool savesBlocked)
            { PreservedPath = preservedPath; UsedBackup = usedBackup; SavesBlocked = savesBlocked; }
        }
        public static LoadWarningInfo? LoadWarning { get; private set; }
        private static bool _savesBlocked;

        // 内存缓存：单实例进程内唯一写者是本进程，Save 时同步更新缓存；
        // 高频调用（托盘滚轮每格、快捷键每次、设备名称每键）直接读缓存，
        // 避免反复读盘 + 反序列化，降低 GC 压力与内存波动。
        private static AppConfig? _cache;
        private static AppConfig? _publishedSnapshot;
        private static readonly object _lock = new();
        private static readonly object _writeLock = new();
        private static long _saveVersion;
        private static (AppConfig Config, long Version)? _pendingSave;
        private static Task _saveTask = Task.CompletedTask;
        public static event Action<bool>? SaveCompleted;

        public static HashSet<string> GetHiddenPanelAppsSnapshot()
        {
            Load();
            lock (_lock)
                return new HashSet<string>(_publishedSnapshot?.HiddenPanelApps ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsPanelAppVisible(string? processName)
        {
            Load();
            lock (_lock)
            {
                var hidden = _publishedSnapshot?.HiddenPanelApps;
                return hidden == null || hidden.Count == 0
                    || processName != null && !hidden.Contains(processName, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>Called by the settings owner: capture now, coalesce only not-yet-committed saves.</summary>
        public static void QueueSave(AppConfig config)
        {
            var snapshot = config.Snapshot();
            lock (_lock)
            {
                _cache = config;
                _publishedSnapshot = snapshot;
                _pendingSave = (snapshot, ++_saveVersion);
                if (_saveTask.IsCompleted) _saveTask = Task.Run(DrainSaves);
            }
        }

        private static void DrainSaves()
        {
            while (true)
            {
                (AppConfig Config, long Version) pending;
                lock (_lock)
                {
                    if (!_pendingSave.HasValue) { _saveTask = Task.CompletedTask; return; }
                    pending = _pendingSave.Value; _pendingSave = null;
                }
                bool saved = false, current = false;
                try
                {
                    string json = JsonSerializer.Serialize(pending.Config, SaveOptions);
                    lock (_writeLock)
                    {
                        lock (_lock) current = pending.Version == _saveVersion;
                        if (current)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                            WriteConfig(json);
                            saved = true;
                        }
                    }
                }
                catch { }
                lock (_lock) current = pending.Version == _saveVersion;
                if (current) try { SaveCompleted?.Invoke(saved); } catch { }
            }
        }

        public static Task PendingSaves { get { lock (_lock) return _saveTask; } }
        public static void FlushPendingSaves() => PendingSaves.GetAwaiter().GetResult();

        public static AppConfig Load()
        {
            lock (_lock)
            {
                if (_cache != null) return _cache;
            }
            lock (_writeLock)
            {
                lock (_lock) if (_cache != null) return _cache;
                // Serialize the one-time read/quarantine with saves and other first-load callers.
                AutoRuleStore.MigrateLegacyIfNeeded();
                AppConfig cfg;
                try { cfg = File.Exists(ConfigPath) ? ReadConfig(ConfigPath) : new AppConfig(); }
                catch (Exception ex)
                {
                    ExceptionDiagnostics.Record("Config.Load", ex);
                    bool usedBackup = false;
                    cfg = new AppConfig();
                    try { if (File.Exists(BackupPath)) { cfg = ReadConfig(BackupPath); usedBackup = true; } }
                    catch (Exception backupError) { ExceptionDiagnostics.Record("Config.BackupRead", backupError); }
                    string? preserved = null;
                    _savesBlocked = true;
                    if (ex is JsonException)
                    {
                        try
                        {
                            preserved = ConfigPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
                            File.Move(ConfigPath, preserved);
                            _savesBlocked = false;
                        }
                        catch (Exception preserveError)
                        { preserved = null; ExceptionDiagnostics.Record("Config.Preserve", preserveError); }
                    }
                    LoadWarning = new LoadWarningInfo(preserved, usedBackup, _savesBlocked);
                }
                cfg.NormalizeCollections();
                lock (_lock)
                {
                    // A settings owner can publish a queued save while this cold read is in progress.
                    if (_cache != null) return _cache;
                    _publishedSnapshot = cfg.Snapshot();
                    return _cache = cfg;
                }
            }
        }

        private static AppConfig ReadConfig(string path)
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), ReadOptions)
                ?? throw new JsonException("The configuration root must be an object.");
            return cfg;
        }

        // Called only while _writeLock is owned. Failure to back up aborts the replacement.
        private static void BackupBeforeWrite()
        {
            if (_savesBlocked) throw new IOException("Configuration saves are paused because the original file could not be preserved.");
            if (!File.Exists(ConfigPath)) return;
            using var original = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            AtomicFile.Write(BackupPath, stream => original.CopyTo(stream));
        }

        private static void WriteConfig(string json)
        {
            BackupBeforeWrite();
            AtomicFile.WriteAllText(ConfigPath, json);
        }

        public static void Save(AppConfig config)
        {
            bool saved = TrySave(config);
            try { SaveCompleted?.Invoke(saved); } catch { }
        }

        /// <summary>保存并报告是否成功落盘；保留原有内存缓存更新行为。</summary>
        public static bool TrySave(AppConfig config)
        {
            // Capture on the settings owner before entering the I/O serialization lock.
            AppConfig snapshot;
            try { snapshot = config.Snapshot(); }
            catch { return false; }
            lock (_writeLock)
            {
                lock (_lock) { ++_saveVersion; _pendingSave = null; _cache = config; _publishedSnapshot = snapshot; }
                bool saved = false;
                try
                {
                    var dir = Path.GetDirectoryName(ConfigPath);
                    if (dir != null) Directory.CreateDirectory(dir);
                    WriteConfig(JsonSerializer.Serialize(snapshot, SaveOptions));
                    saved = true;
                }
                catch { /* 保存失败保留原文件，不阻断运行。 */ }
                return saved;
            }
        }

#if NET10_0_OR_GREATER
        private static readonly JsonSerializerOptions SaveOptions = PersistenceJsonContext.Default.Options;
#else
        private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };
#endif
#if NET10_0_OR_GREATER || NET48
        // Indentation affects writes only. Legacy can reuse its existing reflection
        // metadata for reads and saves instead of building a second options cache.
        private static JsonSerializerOptions? ReadOptions => SaveOptions;
#else
        private static JsonSerializerOptions? ReadOptions => null;
#endif

        /// <summary>替换整份小映射，后台图标读取可继续使用旧快照；保存失败则恢复旧映射。</summary>
        public static bool TrySetAppIcon(string processName, string? fileName)
        {
            if (string.IsNullOrWhiteSpace(processName) || (fileName != null && !AppIconFiles.IsFileName(fileName))) return false;
            AppConfig config = Load();
            Dictionary<string, string> previous, next;
            lock (_lock)
            {
                config = _cache ?? config;
                previous = config.AppIcons;
                if (AppIconFiles.GetFileName(config, processName) == fileName) return true;
                next = AppIconFiles.NormalizeMappings(previous);
                if (fileName == null) next.Remove(processName);
                else
                {
                    if (next.Count >= AppIconFiles.MaxIcons && !next.ContainsKey(processName)) return false;
                    next[processName] = fileName;
                }
                config.AppIcons = next;
            }
            if (TrySave(config)) return true;
            lock (_lock) if (ReferenceEquals(config.AppIcons, next)) config.AppIcons = previous;
            return false;
        }

        /// <summary>一键清理配置文件：删除 config.json 并重置内存缓存为全新默认配置（实验设置功能）。</summary>
        public static AppConfig ResetToDefault()
        {
            lock (_writeLock)
            {
                lock (_lock) { ++_saveVersion; _pendingSave = null; }
                // This explicit reset is allowed to replace defaults, but retain the previous settings.
                _savesBlocked = false;
                try { BackupBeforeWrite(); if (File.Exists(ConfigPath)) File.Delete(ConfigPath); }
                catch (Exception ex) { ExceptionDiagnostics.Record("Config.Reset", ex); return Load(); }
                LoadWarning = null;
                var cfg = new AppConfig();
                lock (_lock) { _cache = cfg; _publishedSnapshot = cfg.Snapshot(); }
                return cfg;
            }
        }

        /// <summary>在界面线程捕获普通配置快照；不读取图片或写盘。</summary>
        public static string CaptureExportSnapshot()
        {
            // This explicit capture runs on the same owner as settings edits.
            return JsonSerializer.Serialize(Load().Snapshot(), SaveOptions);
        }

        /// <summary>后台导出稳定配置及图片；不把快照替换成运行中的配置对象。</summary>
        public static bool ExportTo(string destPath, string? snapshot = null)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(destPath), Path.GetFullPath(ConfigPath), StringComparison.OrdinalIgnoreCase)) return false;
                FlushPendingSaves();
                if (snapshot == null)
                {
                    AppConfig published;
                    Load();
                    lock (_lock) published = _publishedSnapshot!;
                    // Background callers use the last owner-published snapshot. Never
                    // enumerate or re-save the live dictionaries while the UI edits them.
                    snapshot = JsonSerializer.Serialize(published, SaveOptions);
                }
                using var document = JsonDocument.Parse(snapshot);
                var config = document.RootElement.Deserialize<AppConfig>(ReadOptions);
                if (config == null) return false;
                var assets = AppIconFiles.ExportAssets(config);
                AtomicFile.WriteAllBytes(destPath, TransferJson(document.RootElement, config, assets));
                return true;
            }
            catch { return false; }
        }

        /// <summary>从指定文件导入配置：复制到配置路径并清空内存缓存，下次 Load 重新读取（调用方负责重启应用）。</summary>
        public static bool ImportFrom(string srcPath)
        {
            var prepared = PrepareImport(srcPath);
            return prepared != null && ApplyImport(prepared);
        }

        /// <summary>后台验证备份与图片；只生成配置提交数据，失败不替换当前配置。</summary>
        public static byte[]? PrepareImport(string srcPath)
        {
            try
            {
                using var stream = File.OpenRead(srcPath);
                if (stream.Length > AppIconFiles.MaxTransferBytes) return null;
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                var config = document.RootElement.Deserialize<AppConfig>(ReadOptions);
                if (config == null || (config.AppIcons?.Count ?? 0) > AppIconFiles.MaxIcons) return null;
                config.NormalizeCollections();
                var assets = AppIconFiles.ReadImportAssets(document.RootElement, config);
                var encoded = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var asset in assets) encoded[asset.Key] = Convert.ToBase64String(asset.Value);
                return TransferJson(document.RootElement, config, encoded);
            }
            catch { return null; }
        }

        /// <summary>调用方串行提交已验证的普通配置，然后按原流程重启。</summary>
        public static bool ApplyImport(byte[] prepared)
        {
            if (prepared.Length > AppIconFiles.MaxTransferBytes) return false;
            try
            {
                using var document = JsonDocument.Parse(prepared);
                var config = document.RootElement.Deserialize<AppConfig>(ReadOptions);
                if (config == null) return false;
                config.NormalizeCollections();
                AppIconFiles.ImportAssets(document.RootElement, config);
                byte[] ordinary = TransferJson(document.RootElement, config, null);
                lock (_writeLock)
                {
                    lock (_lock) { ++_saveVersion; _pendingSave = null; }
                    var dir = Path.GetDirectoryName(ConfigPath);
                    if (dir != null) Directory.CreateDirectory(dir);
                    BackupBeforeWrite();
                    AtomicFile.WriteAllBytes(ConfigPath, ordinary);
                    lock (_lock) { _cache = null; _publishedSnapshot = null; }
                }
                return true;
            }
            catch { return false; }
        }

        public static AppConfig? ReadPreparedConfig(byte[] prepared)
        {
            if (prepared.Length > AppIconFiles.MaxTransferBytes) return null;
            try
            {
                var config = JsonSerializer.Deserialize<AppConfig>(prepared, ReadOptions);
                config?.NormalizeCollections();
                return config;
            }
            catch { return null; }
        }

        // Asset data exists only in explicit backups, never in the frequently loaded/saved config.json.
        private static byte[] TransferJson(JsonElement root, AppConfig config, Dictionary<string, string>? assets)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                    if (property.Name != "AppIconAssets" && property.Name != nameof(AppConfig.AppIcons)) property.WriteTo(writer);
                writer.WritePropertyName(nameof(AppConfig.AppIcons));
                JsonSerializer.Serialize(writer, config.AppIcons);
                if (assets != null && assets.Count > 0)
                {
                    writer.WritePropertyName("AppIconAssets");
                    JsonSerializer.Serialize(writer, assets);
                }
                writer.WriteEndObject();
            }
            if (buffer.Length > AppIconFiles.MaxTransferBytes) throw new InvalidDataException("Configuration backup too large.");
            return buffer.ToArray();
        }
    }
}

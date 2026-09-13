using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using Application = System.Windows.Application;

namespace SonicRoute
{
    /// <summary>
    /// 应用编排器（阶段 2 双进程）：同一 exe 按启动参数分流两个角色——
    /// - Backend（--service / 默认无参首次）：创建 BackendHost（托盘/快捷键/OSD/麦克风/监听/单实例锁），
    ///   是配置权威写入者；默认启动时拉起 UI 进程。
    /// - UI（--ui / 默认有 Backend 已存在）：连接 Named Pipe，作为 IHostServices 的 IPC 代理，
    ///   创建/显示主窗口与快速面板；UI 关闭仅退出 UI 进程，Backend 继续运行。
    /// Backend 不持有任何 Window/QuickPanel 引用；UI 关闭/重开经事件管道 + Hello 会话管理。
    /// </summary>
    public partial class App : Application, IHostServices
    {
        // ---- Backend 角色 ----
        private BackendHost? _backendHost;

        // ---- UI 角色 ----
        private IpcClient? _ipc;
        private MainWindow? _mainWindow;
        private IQuickPanel? _quickPanel;
        private DispatcherTimer? _reconnectTimer;
        private bool _shuttingDown;

        // ---- IHostServices 本地事件（转发：面板/主窗订阅；IPC 事件到达时 Invoke） ----
        private Action? _osdAdjustFinished;
        private Action? _quickPanelAdjustFinished;

        /// <summary>从程序集版本读取显示版本号，随 csproj &lt;Version&gt; 自动更新。</summary>
        public static string DisplayVersion
        {
            get
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
                    asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                var v = attr?.InformationalVersion ?? asm.GetName().Version?.ToString(3) ?? "0.0";
                int plus = v.IndexOf('+');
                if (plus > 0) v = v.Substring(0, plus);
                return "v" + v;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = e.Args;

            bool serviceMode = args.Contains("--service", StringComparer.OrdinalIgnoreCase);
            bool uiMode = args.Contains("--ui", StringComparer.OrdinalIgnoreCase);

            if (serviceMode)
            {
                // 显式后台模式：必须是 Backend 锁拥有者，否则已有 Backend，退出
                if (!BackendHost.TryEnterSingleInstance()) { Shutdown(); return; }
                StartBackend(spawnUi: false, extraArgs: null);
                return;
            }

            if (!uiMode && BackendHost.TryEnterSingleInstance())
            {
                // 默认无参/普通参数启动且尚无 Backend → 本进程成为 Backend，拉起 UI 进程（透传原始参数如 --panel/--main）
                string extra = string.Join(" ", args.Where(a => !string.Equals(a, "--ui", StringComparison.OrdinalIgnoreCase)));
                StartBackend(spawnUi: true, extraArgs: string.IsNullOrWhiteSpace(extra) ? null : $"--ui {extra}");
                return;
            }

            // UI 角色：连接 Backend（不存在则拉起），Hello 会话判定，取回状态并显示 UI
            StartUi(args);
        }

        // ================= Backend 进程 =================

        /// <summary>成为 Backend：主题/语言初始化（OSD 渲染依赖）、自启自检、创建后台宿主、拉起 UI。</summary>
        private void StartBackend(bool spawnUi, string? extraArgs)
        {
            var config = ConfigService.Load();
            // 首次启动（未设置过语言）跟随系统语言，之后使用配置的语言
            if (string.IsNullOrWhiteSpace(config.Language))
            {
                config.Language = DetectSystemLanguage();
                ConfigService.Save(config);
            }
            L10n.Instance.SetLanguage(config.Language);
            ThemeService.Apply(config.ThemeMode, config.Accent);
            ThemeService.ApplyBackgroundOpacity(config.BackgroundOpacity);

            // 启动自检：自启开启时 Run 键路径与当前 exe 不一致则自动修复（应对绿色版搬家/改名后自启失效）
            if (!IsPackaged())
                AutoStartSelfRepair(config.AutoStart);

            _backendHost = new BackendHost();
            _backendHost.Start();

            if (spawnUi)
                BackendHost.SpawnUi(extraArgs);
        }

        /// <summary>拉起 Backend 进程（UI 连接失败/断线时；Backend 不存在则 --service 启动）。</summary>
        private static void SpawnBackend()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) return;
                Process.Start(new ProcessStartInfo { FileName = exe, Arguments = "--service", UseShellExecute = true });
            }
            catch { }
        }


        /// <summary>判断是否已有 SonicRoute 进程在运行（排除自身）：重连时仅当 Backend 确实已死才拉起，
        /// 避免每轮重试重复 spawn。查询失败保守认为存活（不误 spawn）。</summary>
        private static bool IsBackendProcessAlive()
        {
            try
            {
                int own = Environment.ProcessId;
                return Process.GetProcessesByName("SonicRoute").Any(p => p.Id != own);
            }
            catch
            {
                return true;
            }
        }
        // ================= UI 进程 =================

        private void StartUi(string[] args)
        {
            _ipc = new IpcClient();

            // 连接 Backend：失败则拉起（首次失败才拉起，避免反复 spawn），最多 5 次共约 10s
            bool connected = false;
            for (int i = 0; i < 5; i++)
            {
                if (_ipc.TryConnect(2000)) { connected = true; break; }
                if (i == 0) SpawnBackend();
                Thread.Sleep(1500);
            }
            if (!connected)
            {
                System.Windows.MessageBox.Show("启动失败：无法连接到音跃后台服务。\n请重新启动音跃。", "音跃 SonicRoute");
                Shutdown();
                return;
            }

            // Hello 会话判定：已有活动 UI → 已通知其激活（重复启动）或退出（--restart 重启），本进程决定是否退出
            var hello = _ipc.Request<HelloRespDto>(IpcReq.Hello,
                new HelloDto { UiId = _ipc.UiId, Replace = args.Contains("--restart", StringComparer.OrdinalIgnoreCase) });
            if (hello?.ExistsUi == true)
            {
                Shutdown();
                return;
            }

            // 取回完整状态：配置快照 + 当前应用 + 麦克风静音
            var state = _ipc.Request<StateRespDto>(IpcReq.QueryState, null);
            var config = state?.Config ?? ConfigService.Load();

            // 配置：UI 进程只读快照 + 写经 IPC（Backend 权威落盘并广播）
            ConfigService.SetSnapshot(config);
            ConfigService.SetSaveOverride(cfg => _ipc?.RequestVoid(IpcReq.SaveConfig, new ConfigDto { Config = cfg }));
            ConfigService.SetResetOverride(() =>
            {
                var c = _ipc?.Request<AppConfig>(IpcReq.ResetConfig, null);
                if (c != null) ConfigService.SetSnapshot(c);
                return c ?? new AppConfig();
            });
            ConfigService.SetExportOverride(path => _ipc?.Request<OkDto>(IpcReq.ExportConfig, new PathDto { Path = path })?.Ok ?? false);
            ConfigService.SetImportOverride(path =>
            {
                bool ok = _ipc?.Request<OkDto>(IpcReq.ImportConfig, new PathDto { Path = path })?.Ok ?? false;
                if (ok)
                {
                    var c = _ipc?.Request<StateRespDto>(IpcReq.QueryState, null)?.Config;
                    if (c != null) ConfigService.SetSnapshot(c);
                }
                return ok;
            });

            // 语言/主题：以 Backend 下发的配置为准
            if (string.IsNullOrWhiteSpace(config.Language)) config.Language = DetectSystemLanguage();
            L10n.Instance.SetLanguage(config.Language);
            ThemeService.Apply(config.ThemeMode, config.Accent);
            ThemeService.ApplyBackgroundOpacity(config.BackgroundOpacity);

            // 当前应用初始化（UI 面板/概览的当前应用行）
            if (state?.CurrentApp is AppInfoDto dto && dto.Pid > 0)
            {
                CurrentAppService.LastForegroundAudio = new AudioAppInfo
                {
                    ProcessId = (uint)dto.Pid,
                    ProcessName = dto.ProcessName,
                    DisplayName = dto.DisplayName
                };
                CurrentAppService.Current = CurrentAppService.LastForegroundAudio;
            }

            // 事件订阅（Backend → UI 广播；回调在后台线程，必须切 Dispatcher）
            _ipc.EventReceived += OnIpcEvent;
            _ipc.Disconnected += OnIpcDisconnected;

            // 启动行为：--panel → 快速面板；--main → 完整界面；否则按配置
            if (args.Contains("--panel", StringComparer.OrdinalIgnoreCase))
            {
                Dispatcher.BeginInvoke(ToggleQuickPanel);
            }
            else if (args.Contains("--main", StringComparer.OrdinalIgnoreCase))
            {
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
            else if (config.StartPanelOnStart)
                Dispatcher.BeginInvoke(ToggleQuickPanel);
            else if (!config.StartMinimized)
                Dispatcher.BeginInvoke(ShowMainWindow);
            else
                Dispatcher.BeginInvoke(CheckUiExit, DispatcherPriority.ContextIdle); // 无窗口打开：托盘/快捷键由 Backend 承担，UI 进程退出
        }

        /// <summary>Backend → UI 事件（后台线程回调，切 UI 线程处理）。</summary>
        private void OnIpcEvent(string type, JsonElement? payload)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_shuttingDown) return;
                switch (type)
                {
                    case IpcEvt.OpenMainWindow:
                        ShowMainWindow();
                        break;
                    case IpcEvt.ToggleQuickPanel:
                        ToggleQuickPanel();
                        break;
                    case IpcEvt.ActivateWindow:
                        // 重复启动 UI：激活已有主窗口（无则打开）
                        ShowMainWindow();
                        break;
                    case IpcEvt.CurrentChanged:
                        HandleCurrentChanged(payload);
                        break;
                    case IpcEvt.MicMuteStateChanged:
                        break; // 预留：麦克风横幅由 OSD 常驻处理（Backend 显示），UI 无需额外动作
                    case IpcEvt.OsdAdjustFinished:
                        _osdAdjustFinished?.Invoke();
                        break;
                    case IpcEvt.ConfigChanged:
                        {
                            var cfg = IpcProtocol.Get<ConfigDto>(payload)?.Config;
                            if (cfg != null)
                            {
                                ConfigService.SetSnapshot(cfg);
                                // 导入/外部修改场景：同步语言与主题（正常设置流程 UI 自己已应用，幂等）
                                if (!string.Equals(L10n.CurrentLanguage, cfg.Language, StringComparison.OrdinalIgnoreCase))
                                    L10n.Instance.SetLanguage(cfg.Language);
                            }
                        }
                        break;
                    case IpcEvt.Quit:
                        _shuttingDown = true;
                        Shutdown();
                        break;
                }
            }), DispatcherPriority.Normal);
        }

        /// <summary>当前应用广播：同步 LastForegroundAudio + Current（面板/概览订阅 CurrentChanged 自行刷新）。</summary>
        private void HandleCurrentChanged(JsonElement? payload)
        {
            var dto = IpcProtocol.Get<CurrentChangedDto>(payload)?.App;
            if (dto == null || dto.Pid <= 0) return;
            var app = new AudioAppInfo
            {
                ProcessId = (uint)dto.Pid,
                ProcessName = dto.ProcessName,
                DisplayName = dto.DisplayName
            };
            CurrentAppService.LastForegroundAudio = app;
            var cur = CurrentAppService.Current;
            if (cur == null || cur.ProcessId != (uint)dto.Pid)
            {
                CurrentAppService.Current = app;
            }
            else
            {
                // 同一应用：状态可能已变（快捷键改音量/静音）→ 强制触发刷新
                CurrentAppService.NotifyRefresh();
            }
        }

        /// <summary>事件管道断开（Backend 退出/崩溃）：若已收到 Quit 则不再重连；否则定时拉起并重连 Backend。</summary>
        private void OnIpcDisconnected()
        {
            if (_shuttingDown) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_shuttingDown || _reconnectTimer != null) return;
                _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _reconnectTimer.Tick += async (_, _) =>
                {
                    if (await TryReconnectAsync()) { _reconnectTimer.Stop(); _reconnectTimer = null; }
                };
                _reconnectTimer.Start();
            }));
        }

        private async Task<bool> TryReconnectAsync()
        {
            // 确保 Backend 在：仅当系统里已没有其他 SonicRoute 进程（Backend 已死/被杀）时才拉起，
            // 避免每轮重试重复 spawn 堆积多余进程（Hello 会话机制保证 UI 不会多开，但反复启动/退出仍浪费）
            if (!IsBackendProcessAlive()) SpawnBackend();
            for (int i = 0; i < 3; i++)
            {
                var c = new IpcClient();
                if (c.TryConnect(1000))
                {
                    var hello = c.Request<HelloRespDto>(IpcReq.Hello, new HelloDto { UiId = c.UiId });
                    if (hello?.ExistsUi == true)
                    {
                        // 重连期间另有 UI 接管：本进程退出
                        _shuttingDown = true;
                        c.Dispose();
                        Shutdown();
                        return true;
                    }
                    var state = c.Request<StateRespDto>(IpcReq.QueryState, null);
                    if (state == null)
                    {
                        c.Dispose();
                        await Task.Delay(800);
                        continue;
                    }
                    var old = _ipc;
                    old?.Dispose();
                    _ipc = c;
                    ConfigService.SetSnapshot(state.Config);
                    ConfigService.SetSaveOverride(cfg => _ipc?.RequestVoid(IpcReq.SaveConfig, new ConfigDto { Config = cfg }));
                    c.EventReceived += OnIpcEvent;
                    c.Disconnected += OnIpcDisconnected;
                    // 刷新当前应用（UI 侧订阅方自行重读）
                    CurrentAppService.NotifyRefresh();
                    return true;
                }
                c.Dispose();
                await Task.Delay(800);
            }
            return false;
        }

        // ================= IHostServices（UI 进程 IPC 代理；面板/主窗经 _host 调用） =================

        /// <summary>右上角 OSD 提示（托盘滚轮/快捷键/设置提示共用）：经 IPC 由 Backend 显示（OSD 在后台进程）。</summary>
        public void ShowOsd(string app, string text) => _ipc?.RequestVoid(IpcReq.ShowOsd, new ShowOsdDto { App = app, Text = text });

        /// <summary>重建托盘右键菜单（语言即时生效时调用：菜单文本取当前语言）。</summary>
        public void RebuildTrayMenu() => _ipc?.RequestVoid(IpcReq.RebuildTrayMenu, null);

        /// <summary>麦克风静音状态 OSD 统一入口（面板静音按钮共用）：静音且常驻开关开启 → 常驻显示。</summary>
        public void ShowMicMuteOsd(string app, bool muted) => _ipc?.RequestVoid(IpcReq.ShowMicMuteOsd, new MicMuteDto { App = app, Muted = muted });

        /// <summary>设置页「麦克风静音时 OSD 常驻」开关变化：立即生效（开启且已静音 → 常驻；关闭 → 退出常驻）。</summary>
        public void NotifyMicMuteOsdSettingChanged(bool on) => _ipc?.RequestVoid(IpcReq.NotifyMicMuteOsdSettingChanged, new BoolDto { Value = on });

        /// <summary>进入 OSD 调整模式（主题页「调整位置」）。</summary>
        public void BeginOsdAdjust() => _ipc?.RequestVoid(IpcReq.BeginOsdAdjust, null);

        /// <summary>取消 OSD 调整（不保存）。</summary>
        public void CancelOsdAdjust() => _ipc?.RequestVoid(IpcReq.CancelOsdAdjust, null);

        /// <summary>实时位置预览（偏移滑块/坐标输入联动）。</summary>
        public void PreviewOsd() => _ipc?.RequestVoid(IpcReq.PreviewOsd, null);

        /// <summary>应用 OSD 尺寸（实际生效）。</summary>
        public void ApplyOsdSize(double w, double fs) => _ipc?.RequestVoid(IpcReq.ApplyOsdSize, new SizeDto { W = w, Fs = fs });

        /// <summary>设置 OSD 尺寸并保存（主题页滑条实时调整）。</summary>
        public void SetOsdSize(int w, double fs) => _ipc?.RequestVoid(IpcReq.SetOsdSize, new SizeDto { W = w, Fs = fs });

        /// <summary>OSD 拖拽保存后通知（设置页复位按钮/同步输入框）：Backend 完成拖拽 → 广播 → 本地事件。</summary>
        public event Action? OsdAdjustFinished
        {
            add { _osdAdjustFinished += value; }
            remove { _osdAdjustFinished -= value; }
        }

        /// <summary>进入快速面板位置调整（主题页，逻辑同 OSD）：打开面板并进入拖拽调整模式（松手即保存自定义坐标）。</summary>
        public void BeginQuickPanelAdjust()
        {
            if (_quickPanel == null) ToggleQuickPanel();
            // 面板刚创建时尚未 Loaded：延迟到 Loaded 后再进入调整模式（避免拖拽状态未就绪）
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_quickPanel is QuickPanelWindow c) c.SetAdjustMode(true);
                else if (_quickPanel is QuickPanelModernWindow m) m.SetAdjustMode(true);
            }), DispatcherPriority.Loaded);
        }

        /// <summary>取消快速面板位置调整（不保存，退出调整模式）。</summary>
        public void CancelQuickPanelAdjust()
        {
            if (_quickPanel is QuickPanelWindow c) c.SetAdjustMode(false);
            else if (_quickPanel is QuickPanelModernWindow m) m.SetAdjustMode(false);
        }

        /// <summary>一键还原快速面板位置：恢复任务栏右下角默认位置（Custom 坐标清除），已打开则立即重定位。</summary>
        public void ResetQuickPanelPosition()
        {
            var cfg = ConfigService.Load();
            cfg.QuickPanelPosMode = "default";
            cfg.QuickPanelCustomX = -1;
            cfg.QuickPanelCustomY = -1;
            ConfigService.Save(cfg); // 经 IPC 由 Backend 落盘
            if (_quickPanel is QuickPanelWindow c) c.ResetPosition();
            else if (_quickPanel is QuickPanelModernWindow m) m.ResetPosition();
        }

        /// <summary>快速面板拖拽保存后通知（主题页复位按钮）。</summary>
        public event Action? QuickPanelAdjustFinished
        {
            add { _quickPanelAdjustFinished += value; }
            remove { _quickPanelAdjustFinished -= value; }
        }

        /// <summary>供面板窗口在拖拽保存/关闭时触发（外部类不能直接 Invoke 事件）。</summary>
        public void NotifyQuickPanelAdjustFinished() => _quickPanelAdjustFinished?.Invoke();

        /// <summary>重新加载全局快捷键（设置页修改后调用）：经 IPC 由 Backend 注册（快捷键在后台进程）。</summary>
        public void ReloadHotkeys() => _ipc?.RequestVoid(IpcReq.ReloadHotkeys, null);

        /// <summary>快捷键实际注册状态：动作 → 生效组合（用于设置页显示占用冲突）。</summary>
        public IReadOnlyDictionary<string, string> HotkeyRegistration =>
            _ipc?.Request<HotkeyMapDto>(IpcReq.QueryHotkeyRegistration, null)?.Map ?? new Dictionary<string, string>();

        // ================= UI 生命周期（App 保留：创建与显示） =================

        internal void ToggleQuickPanel()
        {
            if (_quickPanel == null)
            {
                // 按设置选择面板样式：modern=简洁面板（默认）/ classic=经典面板
                var cfg = ConfigService.Load();
                _quickPanel = cfg.QuickPanelStyle == "classic" ? new QuickPanelWindow() : new QuickPanelModernWindow();
                _quickPanel.Closed += (_, _) =>
                {
                    _quickPanel = null;
                    AppIconService.Clear(); // 清空图标缓存，让面板加载的 BitmapSource 可被 GC 回收
                    CheckUiExit();
                };
            }

            if (_quickPanel.IsVisible)
            {
                _quickPanel.Close();
                return;
            }

            _quickPanel.ShowQuickPanel();
        }

        public void ShowMainWindow()
        {
            if (_mainWindow == null)
            {
                try
                {
                    _mainWindow = new MainWindow();
                }
                catch (Exception ex)
                {
                    _mainWindow = null;
                    throw;
                }
                _mainWindow.Closed += (_, _) =>
                {
                    _mainWindow = null;
                    AppIconService.Clear(); // 清空图标缓存（窗口关闭后残留的主要静态持有物），让 BitmapSource 可被 GC 回收
                    CheckUiExit();
                };
            }

            _mainWindow.Show();
            _mainWindow.Activate();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
        }

        /// <summary>UI 进程退出条件：主窗口与快速面板都不存在时退出（Backend 进程继续后台运行）。</summary>
        private void CheckUiExit()
        {
            if (_shuttingDown) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_shuttingDown) return;
                if (_mainWindow == null && _quickPanel == null) Shutdown();
            }), DispatcherPriority.ContextIdle);
        }

        // ================= 启动辅助 =================


        /// <summary>检测当前是否运行在 MSIX 包中（非包环境调用 Package.Current 会抛异常）。</summary>
        private static bool IsPackaged()
        {
            try
            {
                _ = global::Windows.ApplicationModel.Package.Current;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 启动自检修复：自启开启时，注册表 Run 键指向的 exe 与当前路径不一致则自动重写。
        /// 应对绿色版搬家/改名后自启失效；仅修复，不新建（用户已关闭自启则不动）。
        /// </summary>
        private static void AutoStartSelfRepair(bool autoStart)
        {
            if (!autoStart) return;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (key == null) return;
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) return;
                var cur = key.GetValue("SonicRoute") as string;
                if (string.IsNullOrWhiteSpace(cur)) return; // 自启项已被删，不重新加回
                var target = cur.Trim().Trim('"');
                if (!string.Equals(target, exe, StringComparison.OrdinalIgnoreCase))
                    key.SetValue("SonicRoute", $"\"{exe}\"");
            }
            catch
            {
                // 静默：修复失败不影响启动
            }
        }

        /// <summary>首次启动：按 Windows 系统 UI 语言匹配到支持的语言；未匹配则默认英文。</summary>
        private static string DetectSystemLanguage()
        {
            try
            {
                var ci = System.Globalization.CultureInfo.InstalledUICulture;
                string name = ci?.Name?.ToLowerInvariant() ?? "";
                if (name.StartsWith("zh-tw") || name.StartsWith("zh-hk") || name.StartsWith("zh-mo") || name.StartsWith("zh-hant"))
                    return "zh-TW";
                string two = ci?.TwoLetterISOLanguageName?.ToLowerInvariant() ?? "";
                return two switch
                {
                    "zh" => "zh-CN",
                    "ja" => "ja-JP",
                    "ko" => "ko-KR",
                    "fr" => "fr-FR",
                    "de" => "de-DE",
                    "es" => "es-ES",
                    "ru" => "ru-RU",
                    _ => "en-US"
                };
            }
            catch
            {
                return "en-US";
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _shuttingDown = true;
            _reconnectTimer?.Stop();
            _reconnectTimer = null;
            if (_backendHost != null)
            {
                // Backend 进程：释放后台宿主（IPC/托盘/热键/OSD/监听/计时器）
                _backendHost.Dispose();
                _backendHost = null;
            }
            else
            {
                // UI 进程：断开管道（Backend 检测到事件连接断开后清空 UI 会话）
                _ipc?.Dispose();
                _ipc = null;
            }
            base.OnExit(e);
        }
    }
}

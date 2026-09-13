using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Win32;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using Application = System.Windows.Application;

namespace SonicRoute
{
    /// <summary>
    /// 后台宿主（阶段 2 双进程）：运行在 Backend 进程中，承载全部常驻后台功能——
    /// Named Pipe 服务端、托盘图标与菜单、全局快捷键（含鼠标键/滚轮绑定）、托盘滚轮调音量 + OSD、
    /// 麦克风静音监听、idle 回收、前台应用监听、后台预热、单实例锁与后台生命周期。
    /// 不持有任何 Window / QuickPanel 引用：UI 打开/关闭经事件管道请求 UI 进程，UI 不在线时自行拉起 UI 进程。
    /// 配置为权威写入者：UI 的 Save/Reset/Import/Export 全部经 IPC 到达本类后落盘。
    /// </summary>
    public sealed class BackendHost : IDisposable
    {
        // ================= 单实例（Backend 锁） =================
        private static Mutex? _instanceMutex;

        /// <summary>Backend 单实例：首个实例返回 true；非首个（已有 Backend）返回 false，由调用方决定转入 UI 角色。</summary>
        public static bool TryEnterSingleInstance()
        {
            _instanceMutex = new Mutex(true, @"Local\SonicRoute_9NQZGRTPM1NT", out bool isFirstInstance);
            return isFirstInstance;
        }

        // ================= 后台组件 =================
        private IpcServer? _ipc;
        private NotifyIcon? _trayIcon;
        private HotkeyService? _hotkeys;
        private TrayWheelService? _trayWheel;
        private DispatcherTimer? _micMuteWatchTimer;
        private bool _micMuteBaselineReady;
        private bool _lastMicMutedBaseline;
        private DispatcherTimer? _idleTimer;
        private CancellationTokenSource? _singleClickCts;
        private Action? _trayWheelOsdHandler; // 持引用以便 Dispose 正确退订（匿名 lambda 无法 -=）
        private bool _disposed;

        /// <summary>启动后台宿主（必须 UI 线程调用）：Pipe 服务、托盘、热键、滚轮/OSD、麦克风监听、
        /// idle 回收、前台监听、后台预热。</summary>
        public void Start()
        {
            if (_disposed) return;
            var config = ConfigService.Load();

            // 主题与语言（OSD 窗口引用主题资源，托盘菜单取 L10n）
            ThemeService.Apply(config.ThemeMode, config.Accent);
            ThemeService.ApplyBackgroundOpacity(config.BackgroundOpacity);
            L10n.Instance.SetLanguage(config.Language);

            // Named Pipe 服务：处理 UI 请求 + 向 UI 推送事件
            _ipc = new IpcServer(HandleRequest);
            _ipc.Start();

            _trayIcon = new NotifyIcon
            {
                Icon = IconFactory.CreateAppIcon(IconFactory.IsTaskbarDark()),
                Text = $"音跃 SonicRoute {App.DisplayVersion}",
                Visible = true
            };

            // 托盘图标深浅色跟随任务栏主题：主题变化时重建图标（深色任务栏→白色图标，浅色→原图标）
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            RebuildTrayMenu();

            // 单击左键 → 快速面板（延时判别，避免与双击冲突）；UI 在线发事件，不在线拉起 UI 进程
            _trayIcon.MouseClick += (_, args) =>
            {
                if (args.Button != MouseButtons.Left) return;
                _singleClickCts?.Cancel();
                var cts = _singleClickCts = new CancellationTokenSource();
                _ = Task.Delay(280, cts.Token).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    Application.Current.Dispatcher.BeginInvoke(() => RequestUi(IpcEvt.ToggleQuickPanel));
                }, TaskScheduler.Default);
            };

            // 双击 → 完整界面
            _trayIcon.DoubleClick += (_, _) =>
            {
                _singleClickCts?.Cancel();
                Application.Current.Dispatcher.BeginInvoke(() => RequestUi(IpcEvt.OpenMainWindow));
            };

            // 全局快捷键（含鼠标键/滚轮绑定：HotkeyService 内部使用 MouseHotkeyHook）
            _hotkeys = new HotkeyService();
            _hotkeys.HotkeyPressed += action => Application.Current.Dispatcher.BeginInvoke(() => _ = ExecuteHotkeyAsync(action));
            ReloadHotkeys();

            // 托盘滚轮调音量 + OSD（传入托盘图标：关闭"整片托盘区域"开关时仅音跃图标上滚轮响应）
            _trayWheel = new TrayWheelService(_trayIcon);
            _trayWheel.Start();
            _trayWheel.OsdAdjustFinished += _trayWheelOsdHandler = () => _ipc?.SendEvent(IpcEvt.OsdAdjustFinished, null);

            // 麦克风静音状态后台检测（2 秒低频轮询兜底）：首次 tick 只建立基线不弹 OSD，之后状态变化立即更新 OSD 并广播
            _micMuteWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _micMuteWatchTimer.Tick += async (_, _) =>
            {
                try
                {
                    bool muted = await Task.Run(() =>
                    {
                        var cfg = ConfigService.Load();
                        return GlobalMicMuteService.IsAnyMuted(cfg.MicMuteOsdTrackInputMuted);
                    });
                    if (!_micMuteBaselineReady)
                    {
                        _micMuteBaselineReady = true;
                        _lastMicMutedBaseline = muted;
                        return; // 首次只建立基线，不弹 OSD（保持启动行为与旧版一致）
                    }
                    if (muted != _lastMicMutedBaseline)
                    {
                        _lastMicMutedBaseline = muted;
                        ShowMicMuteOsd(L10n.T("Ov.MuteMic"), muted);
                        _ipc?.SendEvent(IpcEvt.MicMuteStateChanged, new MicMuteChangedDto { Muted = muted });
                    }
                }
                catch { }
            };
            _micMuteWatchTimer.Start();

            // idle 回收：首次 15s 后，之后每 120s；UI 进程未连接时强制 GC + 换出工作集（Backend 自身）
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            bool firstTrim = true;
            _idleTimer.Tick += (_, _) =>
            {
                if (_ipc?.HasUi != true)
                {
                    GcNow();
                    TrimWorkingSet();
                }
                if (firstTrim)
                {
                    firstTrim = false;
                    _idleTimer.Interval = TimeSpan.FromSeconds(120);
                }
            };
            _idleTimer.Start();

            // 前台监听：recent 模式下自动跟随前台音频应用（抖音/游戏等），并维护"最近有音频的前台应用"。
            // Backend 是解析权威：变化时经事件管道广播 CurrentChanged，UI 进程同步刷新。
            CurrentAppService.StartForegroundWatcher();
            CurrentAppService.CurrentChanged += OnCurrentChanged;

            // 后台预热音频会话/应用缓存，避免首次滚轮/切语言时在 UI 线程做重量级 COM 枚举
            _ = Task.Run(() =>
            {
                try { AudioService.GetApps(); } catch { }
                try { SessionVolumeService.Refresh(); } catch { }
            });
        }

        // ================= IPC 请求处理（UI → Backend） =================

        /// <summary>UI 请求入口（后台线程调用）：涉及 WPF UI 对象（OSD 窗口/托盘/主题资源/热键服务）的类型
        /// 切回 Backend UI 线程执行；纯数据/文件操作（状态查询/重置/导入导出）直接在后台线程完成。</summary>
        private object? HandleRequest(string type, JsonElement? p)
        {
            if (RequiresUiThread(type))
            {
                object? r = null;
                try
                {
                    Application.Current.Dispatcher.Invoke(() => r = HandleCore(type, p));
                }
                catch { }
                return r;
            }
            return HandleCore(type, p);
        }

        private static bool RequiresUiThread(string type) => type switch
        {
            IpcReq.ShowOsd or IpcReq.ShowMicMuteOsd or IpcReq.NotifyMicMuteOsdSettingChanged or
            IpcReq.BeginOsdAdjust or IpcReq.CancelOsdAdjust or IpcReq.PreviewOsd or
            IpcReq.SetOsdSize or IpcReq.ApplyOsdSize or IpcReq.RebuildTrayMenu or
            IpcReq.ReloadHotkeys or IpcReq.SaveConfig or IpcReq.QueryHotkeyRegistration => true,
            _ => false
        };

        private object? HandleCore(string type, JsonElement? p)
        {
            try
            {
                switch (type)
                {
                    case IpcReq.QueryState:
                        return QueryState();

                    case IpcReq.SaveConfig:
                        {
                            var cfg = IpcProtocol.Get<ConfigDto>(p)?.Config;
                            if (cfg != null) SaveConfigCore(cfg);
                            return new OkDto { Ok = true };
                        }
                    case IpcReq.ResetConfig:
                        {
                            var cfg = ConfigService.ResetToDefault();
                            _ipc?.SendEvent(IpcEvt.ConfigChanged, new ConfigDto { Config = cfg });
                            return cfg;
                        }
                    case IpcReq.ExportConfig:
                        return new OkDto { Ok = ConfigService.ExportTo(IpcProtocol.Get<PathDto>(p)?.Path ?? "") };
                    case IpcReq.ImportConfig:
                        {
                            bool ok = ConfigService.ImportFrom(IpcProtocol.Get<PathDto>(p)?.Path ?? "");
                            if (ok) _ipc?.SendEvent(IpcEvt.ConfigChanged, new ConfigDto { Config = ConfigService.Load() });
                            return new OkDto { Ok = ok };
                        }

                    case IpcReq.ShowOsd:
                        {
                            var d = IpcProtocol.Get<ShowOsdDto>(p);
                            ShowOsd(d?.App ?? "", d?.Text ?? "");
                            return null;
                        }
                    case IpcReq.ShowMicMuteOsd:
                        {
                            var d = IpcProtocol.Get<MicMuteDto>(p);
                            ShowMicMuteOsd(d?.App ?? "", d?.Muted ?? false);
                            return null;
                        }
                    case IpcReq.NotifyMicMuteOsdSettingChanged:
                        NotifyMicMuteOsdSettingChanged(IpcProtocol.Get<BoolDto>(p)?.Value ?? false);
                        return null;
                    case IpcReq.BeginOsdAdjust:
                        BeginOsdAdjust();
                        return null;
                    case IpcReq.CancelOsdAdjust:
                        CancelOsdAdjust();
                        return null;
                    case IpcReq.PreviewOsd:
                        PreviewOsd();
                        return null;
                    case IpcReq.SetOsdSize:
                        {
                            var d = IpcProtocol.Get<SizeDto>(p);
                            SetOsdSize(d == null ? 240 : (int)d.W, d?.Fs ?? 1.0);
                            return null;
                        }
                    case IpcReq.ApplyOsdSize:
                        {
                            var d = IpcProtocol.Get<SizeDto>(p);
                            ApplyOsdSize(d?.W ?? 240, d?.Fs ?? 1.0);
                            return null;
                        }

                    case IpcReq.RebuildTrayMenu:
                        RebuildTrayMenu();
                        return null;
                    case IpcReq.ReloadHotkeys:
                        ReloadHotkeys();
                        return null;
                    case IpcReq.QueryHotkeyRegistration:
                        return new HotkeyMapDto { Map = HotkeyRegistration.ToDictionary(kv => kv.Key, kv => kv.Value) };
                }
            }
            catch { }
            return null;
        }

        /// <summary>QueryState：UI 启动/重连时一次取回完整状态（配置快照 + 当前应用 + 麦克风静音）。</summary>
        private StateRespDto QueryState()
        {
            var cur = CurrentAppService.Current;
            return new StateRespDto
            {
                Config = ConfigService.Load(),
                CurrentApp = cur == null ? null : ToDto(cur),
                MicMuted = GlobalMicMuteService.IsMuted()
            };
        }

        /// <summary>配置权威写入：落盘；语言变化时同步 L10n + 重建托盘菜单；广播 ConfigChanged 让 UI 刷新。</summary>
        private void SaveConfigCore(AppConfig cfg)
        {
            var prev = ConfigService.Load();
            bool langChanged = !string.Equals(prev.Language, cfg.Language, StringComparison.OrdinalIgnoreCase);
            bool themeChanged = prev.ThemeMode != cfg.ThemeMode || prev.Accent != cfg.Accent || prev.BackgroundOpacity != cfg.BackgroundOpacity;
            ConfigService.Save(cfg);
            if (langChanged)
            {
                L10n.Instance.SetLanguage(cfg.Language);
                RebuildTrayMenu();
            }
            if (themeChanged)
            {
                ThemeService.Apply(cfg.ThemeMode, cfg.Accent);
                ThemeService.ApplyBackgroundOpacity(cfg.BackgroundOpacity);
            }
            _ipc?.SendEvent(IpcEvt.ConfigChanged, new ConfigDto { Config = cfg });
        }

        /// <summary>请求 UI 执行动作：UI 在线发事件；不在线拉起 UI 进程（带对应启动参数，保证单击→面板、双击/设置→完整界面）。</summary>
        private void RequestUi(string evt)
        {
            if (_ipc != null && _ipc.SendEvent(evt, null)) return;
            SpawnUi(evt == IpcEvt.ToggleQuickPanel ? "--ui --panel" : "--ui --main");
        }

        /// <summary>拉起 UI 进程（same exe --ui；extraArgs 透传原始启动参数如 --panel/--main）。
        /// Backend 分支启动时由 App 调用（默认无参启动），托盘操作缺 UI 时也调用（extraArgs=null）。</summary>
        public static void SpawnUi(string? extraArgs)
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) return;
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = string.IsNullOrWhiteSpace(extraArgs) ? "--ui" : extraArgs,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        /// <summary>当前应用变化（前台监听/最近使用切换）：广播给 UI 进程刷新面板/概览。</summary>
        private void OnCurrentChanged()
        {
            _ipc?.SendEvent(IpcEvt.CurrentChanged, new CurrentChangedDto { App = ToDto(CurrentAppService.Current) });
        }

        /// <summary>快捷键操作当前应用后主动广播（当前应用未变但音量/静音状态已变，UI 需重读状态）。</summary>
        private void BroadcastCurrentChanged()
        {
            _ipc?.SendEvent(IpcEvt.CurrentChanged, new CurrentChangedDto { App = ToDto(CurrentAppService.Current) });
        }

        private static AppInfoDto? ToDto(AudioAppInfo? a) =>
            a == null ? null : new AppInfoDto { Pid = (int)a.ProcessId, ProcessName = a.ProcessName, DisplayName = a.DisplayName };

        // ================= 宿主能力（IPC 请求目标） =================

        /// <summary>右上角 OSD 提示（托盘滚轮/快捷键/设置提示共用）。</summary>
        public void ShowOsd(string app, string text) => _trayWheel?.ShowOsd(app, text);

        /// <summary>重建托盘右键菜单（语言即时生效时调用：菜单文本取当前语言）。</summary>
        public void RebuildTrayMenu()
        {
            try
            {
                if (_trayIcon == null) return;
                var menu = new ContextMenuStrip();
                menu.Items.Add(L10n.T("St.Settings"), null, (_, _) => RequestUi(IpcEvt.OpenMainWindow));
                menu.Items.Add(L10n.T("Tray.OpenPanel"), null, (_, _) => RequestUi(IpcEvt.ToggleQuickPanel));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(L10n.T("Tray.Exit"), null, (_, _) =>
                {
                    try { Application.Current.Shutdown(); } catch { } // 退出：通知 UI 后清理（OnExit → Dispose）
                });
                _trayIcon.ContextMenuStrip = menu;
            }
            catch { }
        }

        /// <summary>麦克风静音状态 OSD 统一入口（快捷键 / 后台检测器 / UI 请求共用）：静音且常驻开关开启 → 常驻显示。</summary>
        public void ShowMicMuteOsd(string app, bool muted) => _trayWheel?.ShowMicMuteOsd(app, muted);

        /// <summary>设置页「麦克风静音时 OSD 常驻」开关变化：立即生效（开启且已静音 → 常驻；关闭 → 退出常驻）。</summary>
        public void NotifyMicMuteOsdSettingChanged(bool on) => _trayWheel?.NotifyMicMuteOsdSettingChanged(on);

        /// <summary>进入 OSD 调整模式（主题页「调整位置」）。</summary>
        public void BeginOsdAdjust() => _trayWheel?.BeginOsdAdjust();

        /// <summary>取消 OSD 调整（不保存）。</summary>
        public void CancelOsdAdjust() => _trayWheel?.CancelOsdAdjust();

        /// <summary>实时位置预览（偏移滑块/坐标输入联动）。</summary>
        public void PreviewOsd() => _trayWheel?.PreviewOsd();

        /// <summary>应用 OSD 尺寸（实际生效）。</summary>
        public void ApplyOsdSize(double w, double fs) => _trayWheel?.ApplyOsdSize(w, fs);

        /// <summary>设置 OSD 尺寸并保存（主题页滑条实时调整）。</summary>
        public void SetOsdSize(int w, double fs) => _trayWheel?.SetOsdSize(w, fs);

        /// <summary>重新加载全局快捷键（设置页修改后调用）。实验模式的隐藏动作仅在"实验模式+麦克风选项"开启时注册。</summary>
        public void ReloadHotkeys()
        {
            if (_hotkeys == null) return;
            var config = ConfigService.Load();
            bool expMicOn = config.ExperimentalMic;
            var map = new Dictionary<string, string>();
            foreach (var a in HotkeyActions.All)
            {
                // 实验模式隐藏动作：未开启麦克风选项不注册，避免后台占用组合键
                if ((a == HotkeyActions.ActSwitchInput || a == HotkeyActions.ActSwitchAllInput) && !expMicOn) continue;
                map[a] = config.Hotkeys.TryGetValue(a, out var c)
                    ? c
                    : (HotkeyActions.Defaults.TryGetValue(a, out var d) ? d : "");
            }
            _hotkeys.Reload(map);
        }

        /// <summary>快捷键实际注册状态：动作 → 生效组合（用于设置页显示占用冲突）。</summary>
        public IReadOnlyDictionary<string, string> HotkeyRegistration =>
            _hotkeys?.RegistrationStatus ?? new Dictionary<string, string>();

        // ================= 快捷键执行（后台执行；无 UI 依赖，结果经事件广播） =================

        private async Task ExecuteHotkeyAsync(string action)
        {
            if (action == HotkeyActions.ActPanel)
            {
                RequestUi(IpcEvt.ToggleQuickPanel);
                return;
            }

            // 静音/切设备/音量的目标应用：与托盘滚轮调音量完全一致——直接走
            // CurrentAppService.Resolve 同一套规则（last/fixed → 前台 → 最近使用 → 兜底），
            // 保证快捷键和"鼠标放任务栏滚轮调音量"永远解析出同一个应用。
            var cfg = ConfigService.Load();
            var apps = await Task.Run(() => AudioService.GetApps());
            var target = CurrentAppService.Resolve(apps, cfg);
            // 仅"当前应用"类动作需要解析当前应用；全局切换 / 系统默认切换不依赖，解析失败照常执行
            bool needsTarget = action is HotkeyActions.ActMute or HotkeyActions.ActVolUp or HotkeyActions.ActVolDown or HotkeyActions.ActSwitchOutput or HotkeyActions.ActSwitchInput;
            if (needsTarget && target == null) return;
            int pid = target == null ? -1 : (int)target.ProcessId;
            string name = target == null ? "" : AppDisplayName.Get(target);

            switch (action)
            {
                case HotkeyActions.ActMute:
                    // 静音面板/概览的当前应用（与面板静音按钮同一 Core 路径）；广播让 UI 刷新当前应用行
                    {
                        var mr = await Task.Run(() => SessionVolumeService.ToggleMuteChecked(pid));
                        _trayWheel?.ShowOsd(name, mr.Applied
                            ? (mr.Muted ? L10n.T("Ov.AppMuted") : L10n.T("Ov.AppUnmuted"))
                            : L10n.T("Ov.NoOutputSession"));
                        BroadcastCurrentChanged();
                    }
                    break;

                case HotkeyActions.ActMuteInput:
                    // 全局麦克风静音：静音/取消静音系统所有录音设备（与当前应用无关）。
                    // 切换后立即用真实状态更新 OSD（静音且常驻开关开启 → 常驻显示，不等待后台检测）并广播。
                    {
                        bool gm = await Task.Run(() => GlobalMicMuteService.Toggle());
                        ShowMicMuteOsd(L10n.T("Ov.MuteMic"), gm); // 全局麦克风静音：标题固定「麦克风静音」，不显示应用名
                        _ipc?.SendEvent(IpcEvt.MicMuteStateChanged, new MicMuteChangedDto { Muted = gm });
                    }
                    break;

                case HotkeyActions.ActVolUp:
                case HotkeyActions.ActVolDown:
                    // 调整面板/概览显示的当前应用音量（每次 ±步进）；广播让 UI 刷新滑块/百分比
                    {
                        int step = Math.Clamp(ConfigService.Load().VolumeStep, 1, 20);
                        int delta = action == HotkeyActions.ActVolUp ? step : -step;
                        int curVol = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
                        if (curVol < 0) { _trayWheel?.ShowOsd(name, "⚠ " + L10n.T("Ov.NoOutputSession")); break; }
                        int nextVol = Math.Clamp(curVol + delta, 0, 100);
                        bool ok = await Task.Run(() => SessionVolumeService.SetVolumePercent(pid, nextVol));
                        int act = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
                        _trayWheel?.ShowOsd(name, ok && act >= 0 ? $"🔉 {act}%" : L10n.T("Ov.VolAdjustFail"));
                        BroadcastCurrentChanged();
                    }
                    break;

                case HotkeyActions.ActSwitchOutput:
                    string? dev = await CycleDeviceAsync(pid, EDataFlow.eRender);
                    _trayWheel?.ShowOsd(name, string.IsNullOrEmpty(dev) ? L10n.T("Ov.NoDevice") : $"🔊 {dev}");
                    break;

                case HotkeyActions.ActSwitchInput:
                    // 实验模式 - 麦克风选项开启后才注册的隐藏动作：切换当前应用的录音（输入）设备
                    string? mdev = await CycleDeviceAsync(pid, EDataFlow.eCapture);
                    _trayWheel?.ShowOsd(name, string.IsNullOrEmpty(mdev) ? L10n.T("Ov.NoMicDevice") : $"🎤 {mdev}");
                    break;

                case HotkeyActions.ActResetAllApps:
                    // 一键还原全部应用（含未打开但曾设置过/正在运行的进程）输出+输入为系统默认
                    {
                        var rr = await Task.Run(() => AudioService.ResetAllPersistedEndpoints());
                        _trayWheel?.ShowOsd(L10n.T("Act.ResetAllApps"),
                            rr.Total == 0
                                ? L10n.T("Ov.NoneToReset")
                                : string.Format(L10n.T("Act.ResetAllAppsDone"), rr.OutOk, rr.InOk));
                    }
                    break;

                case HotkeyActions.ActSwitchAllOutput:
                    // 切换全局应用输出设备：所有有音频会话的应用切到下一个保留设备
                    string? ao = await CycleAllAppsDeviceAsync(EDataFlow.eRender);
                    _trayWheel?.ShowOsd(L10n.T("Act.SwitchAllOutput"), string.IsNullOrEmpty(ao) ? L10n.T("Ov.NoDevice") : $"🔊 {ao}");
                    break;

                case HotkeyActions.ActSwitchAllInput:
                    // 切换全局应用输入设备（跟随麦克风选项显示/注册）
                    string? ai = await CycleAllAppsDeviceAsync(EDataFlow.eCapture);
                    _trayWheel?.ShowOsd(L10n.T("Act.SwitchAllInput"), string.IsNullOrEmpty(ai) ? L10n.T("Ov.NoMicDevice") : $"🎤 {ai}");
                    break;

                case HotkeyActions.ActSetDefaultOutput:
                    // 切换系统默认输出设备（改系统默认，非按应用）
                    string? sd = await CycleSystemDefaultDeviceAsync(EDataFlow.eRender);
                    _trayWheel?.ShowOsd(L10n.T("Act.SetDefaultOutput"), string.IsNullOrEmpty(sd) ? L10n.T("Ov.NoDevice") : $"🔊 {sd}");
                    break;

                case HotkeyActions.ActSetDefaultInput:
                    // 切换系统默认输入设备（无需启用麦克风选项，始终可用）
                    string? si = await CycleSystemDefaultDeviceAsync(EDataFlow.eCapture);
                    _trayWheel?.ShowOsd(L10n.T("Act.SetDefaultInput"), string.IsNullOrEmpty(si) ? L10n.T("Ov.NoMicDevice") : $"🎤 {si}");
                    break;
            }
        }

        /// <summary>构建可见设备列表（真实保留设备，按隐藏集合过滤）。</summary>
        private static List<AudioDeviceInfo> BuildVisibleDevices(EDataFlow flow, AppConfig config)
        {
            var devs = AudioService.GetDevices(flow);
            var hidden = flow == EDataFlow.eRender ? config.HiddenOutputDevices : config.HiddenInputDevices;
            var list = devs.Where(d => !hidden.Contains(d.Id)).ToList();
            // 列表头部加入"系统默认输出/输入"虚拟项（与快捷面板/概览一致），选中即切回跟随系统默认
            return PanelDevices.WithSystemDefault(list, flow, config);
        }

        /// <summary>设备名（自定义名优先）。</summary>
        private static string DeviceDisplayName(AppConfig config, AudioDeviceInfo dev)
        {
            return config.DeviceNames.TryGetValue(dev.Id, out var n) && !string.IsNullOrWhiteSpace(n)
                ? n!
                : (dev.DisplayName ?? dev.Id);
        }

        /// <summary>在当前应用的可见设备间循环切换，返回切换到的设备名；失败/无设备返回 null。
        /// 当前"跟随系统默认"（无持久化）且默认项在列表 → 从默认项的下一个开始。</summary>
        private static Task<string?> CycleDeviceAsync(int pid, EDataFlow flow) => Task.Run(() =>
        {
            try
            {
                var config = ConfigService.Load();
                var visible = BuildVisibleDevices(flow, config);
                if (visible.Count == 0) return null;

                var persisted = AudioService.GetPersistedEndpoint(pid, flow);
                string? curShort = persisted == null ? null : AudioPolicyConfig.UnpackDeviceId(persisted);
                // 跟随系统默认（无持久化）→ 位于"系统默认"虚拟项（首位），按下切到第一个真实设备
                int idx = visible.FindIndex(d => string.Equals(d.Id, curShort, StringComparison.OrdinalIgnoreCase));
                if (idx < 0 && persisted == null && AudioService.IsSystemDefault(visible[0].Id)) idx = 0;  // 仅当"系统默认"虚拟项在列表首位时（未被隐藏），从它开始循环
                int next = idx < 0 ? 0 : (idx + 1) % visible.Count;
                var target = visible[next];
                var r = AudioService.ApplyEndpoint(pid, flow, target.Id);
                if (!r.Success) return null;
                return DeviceDisplayName(config, target);
            }
            catch
            {
                return null;
            }
        });

        /// <summary>在系统默认输出/输入设备间循环切换：从当前默认的下一个可见设备开始，改系统默认设备。
        /// 返回切换到的设备名（用自定义名）；失败/无设备返回 null。</summary>
        private static Task<string?> CycleSystemDefaultDeviceAsync(EDataFlow flow) => Task.Run(() =>
        {
            try
            {
                var config = ConfigService.Load();
                var devs = AudioService.GetDevices(flow);
                var hidden = flow == EDataFlow.eRender ? config.HiddenOutputDevices : config.HiddenInputDevices;
                var visible = devs.Where(d => !hidden.Contains(d.Id)).ToList();
                if (visible.Count == 0) return null;

                // GetDefaultDeviceId 返回完整 ID（"{0.0.0.00000000}.{...}"），需解包为短 ID 才能与设备列表比较，
                // 否则永远找不到当前默认 → 总从第一个设备开始循环（用户实测的"逻辑不一致"根因）
                var curDefault = AudioService.GetDefaultDeviceId(flow);
                string? curShort = curDefault == null ? null : AudioPolicyConfig.UnpackDeviceId(curDefault);
                int idx = curShort == null ? -1
                    : visible.FindIndex(d => string.Equals(d.Id, curShort, StringComparison.OrdinalIgnoreCase));
                int next = idx < 0 ? 0 : (idx + 1) % visible.Count;
                var r = SystemDefaultDeviceService.SetDefault(flow, visible[next].Id);
                if (!r.Success) return null;
                // 通知/OSD 显示用户自定义名称（与设置页"设备名称"一致）
                string? custom = config.DeviceNames.TryGetValue(visible[next].Id, out var n) ? n : null;
                return string.IsNullOrWhiteSpace(custom) ? visible[next].DisplayName : custom;
            }
            catch
            {
                return null;
            }
        });

        /// <summary>切换全局应用设备：所有有音频会话的应用统一切到"当前系统默认设备的下一个可见设备"。
        /// 目标可为"系统默认"虚拟项（清除所有应用的持久化路由，跟随系统默认）。
        /// 返回目标设备名；无可见设备/无应用返回 null。</summary>
        private static Task<string?> CycleAllAppsDeviceAsync(EDataFlow flow) => Task.Run(() =>
        {
            try
            {
                var config = ConfigService.Load();
                var visible = BuildVisibleDevices(flow, config);
                if (visible.Count == 0) return null;

                var apps = AudioService.GetApps();
                var target = visible[0]; // 无参考时默认第一个可见设备
                // 参考设备：优先用第一个有音频会话应用的实际设备（会随上次切换更新，保证连续按能循环）；
                // 应用跟随系统默认时退回用系统默认设备。
                string? refShort = null;
                var firstApp = apps.FirstOrDefault();
                if (firstApp != null)
                {
                    var persisted = AudioService.GetPersistedEndpoint((int)firstApp.ProcessId, flow);
                    if (persisted != null) refShort = AudioPolicyConfig.UnpackDeviceId(persisted);
                }
                if (refShort == null)
                {
                    var curDefault = AudioService.GetDefaultDeviceId(flow);
                    refShort = curDefault == null ? null : AudioPolicyConfig.UnpackDeviceId(curDefault);
                }
                int idx = refShort == null ? -1
                    : visible.FindIndex(d => string.Equals(d.Id, refShort, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) target = visible[(idx + 1) % visible.Count];

                int done = 0;
                foreach (var app in apps)
                {
                    if (app.ProcessId <= 0) continue;
                    var r = AudioService.ApplyEndpoint((int)app.ProcessId, flow, target.Id);
                    if (r.Success) done++;
                }
                if (done == 0) return null;
                return DeviceDisplayName(config, target);
            }
            catch
            {
                return null;
            }
        });

        // ================= 内存回收 =================

        /// <summary>强制回收：GC 两轮（含终结器队列），用于 idle 回收时尽快回收进程内残留（Backend 自身内存）。</summary>
        public static void GcNow()
        {
            try
            {
                // 压缩 LOH（大对象堆）：WPF 视觉树/位图可能产生 >85KB 的大对象，
                // 默认 LOH 不压缩，回收后内存碎片不归还给 OS，导致残留 1-4MB
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true); // 强制阻塞压缩式完整GC
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
            }
            catch { }
        }

        /// <summary>将进程工作集换出到磁盘。WPF Milcore 渲染缓存（native、进程级共享）无法被托管 GC 回收，
        /// 残余内存只能靠换出；换出的不活跃页面不再换回（无访问），任务管理器"内存"列立即下降。</summary>
        public static void TrimWorkingSet()
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                SetProcessWorkingSetSize(p.Handle, new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }

        // ================= 托盘生命周期 =================

        /// <summary>任务栏深浅色切换时重建托盘图标（深色任务栏用白色图标，浅色用原图标）。</summary>
        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            try
            {
                if (e.Category != UserPreferenceCategory.General) return;
                if (_trayIcon == null) return;
                var old = _trayIcon.Icon;
                _trayIcon.Icon = IconFactory.CreateAppIcon(IconFactory.IsTaskbarDark());
                try { old?.Dispose(); } catch { }
            }
            catch { }
        }

        /// <summary>释放托盘图标及其 HICON（避免退出后残留 GDI 资源）。</summary>
        private void DisposeTray()
        {
            if (_trayIcon == null) return;
            try { _trayIcon.Visible = false; } catch { }
            try { _trayIcon.Icon?.Dispose(); } catch { }
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        /// <summary>释放后台宿主：停止监听/计时器、释放 IPC/托盘/热键/OSD（程序退出时由 Backend 进程调用）。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentAppService.CurrentChanged -= OnCurrentChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _micMuteWatchTimer?.Stop();
            _micMuteWatchTimer = null;
            if (_trayWheel != null && _trayWheelOsdHandler != null)
                _trayWheel.OsdAdjustFinished -= _trayWheelOsdHandler;
            _trayWheel?.Dispose();
            _trayWheel = null;
            _hotkeys?.Dispose();
            _hotkeys = null;
            _idleTimer?.Stop();
            _idleTimer = null;
            // 通知 UI 退出（尽力；管道断开时 UI 会自行检测并退出/重连）
            _ipc?.SendEvent(IpcEvt.Quit, null);
            _ipc?.Dispose();
            _ipc = null;
            DisposeTray();
        }

        // ================= P/Invoke =================

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);
    }
}

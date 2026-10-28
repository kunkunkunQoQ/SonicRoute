using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using SonicRoute.Core;
using SonicRoute.Core.Compat;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using Application = System.Windows.Application;

namespace SonicRoute
{
    /// <summary>
    /// 应用编排器：托盘图标 + 窗口管理 + 全局快捷键。
    /// 单击托盘 → 快速切换面板；双击托盘 → 完整界面；右键 → 上下文菜单。
    /// </summary>
    public partial class App : Application
    {
        private NotifyIcon? _trayIcon;
        private MainWindow? _mainWindow;
        private QuickPanelModernWindow? _quickPanel;

        /// <summary>当前快速面板实例（供设置页立即应用面板高度等）。</summary>
        public QuickPanelModernWindow? QuickPanelInstance => _quickPanel;

        internal void ApplyQuickPanelMeterSetting(bool enabled)
        {
            _quickPanel?.SetMeterEnabled(enabled);
        }
        private HotkeyService? _hotkeys;
        private TrayWheelService? _trayWheel;
        private CancellationTokenSource? _singleClickCts;
        private System.Windows.Threading.DispatcherTimer? _uiReclaimTimer;
        private bool _uiWorkingSetDirty = true;
        private bool _startupWarmupComplete, _exiting;
        private static Mutex? _instanceMutex;
        private bool _ownsInstanceMutex;
        private RuleCommandServer? _ruleCommandServer;
        private static int _activateMsg;

        /// <summary>从程序集版本读取显示版本号（v1.13），随 csproj &lt;Version&gt; 自动更新。</summary>
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
        private System.Windows.Interop.HwndSource? _activateSink;
        // 麦克风静音状态后台检测（低频轮询兜底）：外部程序/Windows 修改静音状态时立即更新 OSD
        private System.Windows.Threading.DispatcherTimer? _micMuteWatchTimer;
        private bool _micMuteBaselineReady;
        private bool _lastMicMutedBaseline;
        private bool _micMuteWatchInProgress;

        // Store, portable Lite and Legacy activate only their own running instance.
        private static string InstanceMutexName => AppRuntimeIdentity.Current.MutexName;
        private static string ActivateSinkTitle => AppRuntimeIdentity.Current.ActivateSinkTitle;
        private static string ActivateMessageName => AppRuntimeIdentity.Current.ActivateMessageName;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);


            var commandMode = RuleCommandLine.Parse(SonicRoute.Updates.UpdateBootstrap.Pending != null ? Array.Empty<string>() : e.Args, out var ruleSelector);
            if (commandMode == RuleCommandLine.Mode.Help || commandMode == RuleCommandLine.Mode.Invalid)
            {
                if (commandMode == RuleCommandLine.Mode.Help)
                {
                    var helpLanguage = ConfigService.Load().Language;
                    L10n.Instance.SetLanguage(string.IsNullOrWhiteSpace(helpLanguage) ? DetectSystemLanguage() : helpLanguage);
                    RuleCommandLine.WriteHelp();
                }
                else RuleCommandLine.WriteResult(RuleCommandLine.Error(2, "invalid_arguments"));
                Shutdown(commandMode == RuleCommandLine.Mode.Help ? 0 : 2);
                return;
            }

            // single instance: notify existing instance to open main window, then exit
            _activateMsg = NativeRegisterWindowMessage(ActivateMessageName);
            _instanceMutex = new Mutex(true, InstanceMutexName, out bool isFirstInstance);
            _ownsInstanceMutex = isFirstInstance;
            if (!isFirstInstance)
            {
                if (commandMode == RuleCommandLine.Mode.Run)
                {
                    Dispatcher.BeginInvoke(new Action(() => _ = ForwardRuleCommandAsync(ruleSelector)));
                    return;
                }
                var h = NativeFindWindow(null, ActivateSinkTitle);
                if (h != IntPtr.Zero) NativePostMessage(h, _activateMsg, IntPtr.Zero, IntPtr.Zero);
                Shutdown();
                return;
            }
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

            if (commandMode == RuleCommandLine.Mode.Run)
            {
                // 临时执行只初始化规则所需音频和 OSD，不开启托盘、快捷键、触发器或常驻监听。
                _trayWheel = new TrayWheelService(null);
                var commandWarmup = WarmupAudioAsync();
                StartRuleCommandServer(commandWarmup);
                Dispatcher.BeginInvoke(new Action(() => _ = RunTemporaryRuleCommandAsync(ruleSelector, commandWarmup)));
                return;
            }

            // 正常启动在后台登记用户级短命令，不阻塞窗口与音频预热。
            var shortCommandHelp = L10n.T("Auto.ShortCliHelp");
            _ = Task.Run(() => RuleCommandLine.EnsureShortCommand(shortCommandHelp));
            // 启动自检：自启开启时 Run 键路径与当前 exe 不一致则自动修复（应对绿色版搬家/改名后自启失效）
            if (!IsPackaged())
                AutoStartSelfRepair(config.AutoStart);

            _trayIcon = new NotifyIcon
            {
                Icon = IconFactory.CreateAppIcon(IconFactory.IsTaskbarDark()),
                Text = $"音跃 SonicRoute {DisplayVersion}",
                Visible = true
            };

            // 托盘图标深浅色跟随任务栏主题：主题变化时重建图标（深色任务栏→白色图标，浅色→原图标）
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            RebuildTrayMenu();

            // 单击左键 → 快速面板（延时判别，避免与双击冲突）
            _trayIcon.MouseClick += (_, args) =>
            {
                if (args.Button == MouseButtons.Right)
                {
                    _singleClickCts?.Cancel();
                    Dispatcher.BeginInvoke(ShowTrayMenu);
                    return;
                }
                if (args.Button != MouseButtons.Left) return;
                _singleClickCts?.Cancel();
                var cts = _singleClickCts = new CancellationTokenSource();
                _ = Task.Delay(280, cts.Token).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    Dispatcher.BeginInvoke(ToggleQuickPanel);
                }, TaskScheduler.Default);
            };

            // 双击 → 完整界面
            _trayIcon.DoubleClick += (_, _) =>
            {
                _singleClickCts?.Cancel();
                Dispatcher.BeginInvoke(ShowMainWindow);
            };

            // 全局快捷键
            _hotkeys = new HotkeyService();
            _hotkeys.HotkeyPressed += action => Dispatcher.BeginInvoke(() => _ = ExecuteHotkeyAsync(action));
            AutoRuleStore.Changed += RulesChanged;
            ReloadHotkeys();

            // 自动化定时调度器：挂接系统时间变化 / 睡眠恢复监听并开始调度
            AutoRuleScheduler.Start();

            // 托盘滚轮调音量（传入托盘图标：关闭"整片托盘区域"开关时仅音跃图标上滚轮响应）
            _trayWheel = new TrayWheelService(_trayIcon);
            _trayWheel.Start();

            StartBackgroundMonitoring();

            // single-instance activate sink (invisible): opens full UI on message
            _activateSink = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters(ActivateSinkTitle)
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            });
            _activateSink.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == _activateMsg) { Dispatcher.BeginInvoke(ShowMainWindow); handled = true; }
                return IntPtr.Zero;
            });

            // 前台监听：recent 模式下自动跟随前台音频应用（抖音/游戏等），并维护"最近有音频的前台应用"
            var foregroundWarmup = CurrentAppService.StartForegroundWatcher();

            // 后台预热音频会话/应用缓存，避免首次滚轮/切语言时在 UI 线程做重量级 COM 枚举
            var audioWarmup = WarmupAudioAsync();
            var startupWarmup = Task.WhenAll(foregroundWarmup, audioWarmup);
            StartUpdates(startupWarmup);
            _ = ReclaimAfterWarmupAsync(startupWarmup);
            StartRuleCommandServer(audioWarmup);

            // 启动行为
                        if (e.Args.Contains("--benchmark-ui", StringComparer.OrdinalIgnoreCase))
            {
                Dispatcher.BeginInvoke(new Action(async () => { await foregroundWarmup; await RunUiBenchmarkAsync(); }));
                return;
            }
            bool showMain = !config.StartMinimized;
            if (e.Args.Contains("--panel", StringComparer.OrdinalIgnoreCase))
                Dispatcher.BeginInvoke(new Action(async () => { await foregroundWarmup; ToggleQuickPanel(); }));
            else if (e.Args.Contains("--main", StringComparer.OrdinalIgnoreCase))
                Dispatcher.BeginInvoke(new Action(async () => { await foregroundWarmup; ShowMainWindow(); }));
            else if (config.StartPanelOnStart)
                Dispatcher.BeginInvoke(new Action(async () => { await foregroundWarmup; ToggleQuickPanel(); }));
            else if (showMain)
                Dispatcher.BeginInvoke(new Action(async () => { await foregroundWarmup; ShowMainWindow(); }));

            // 一次性启动规则等待原有后台预热完成，服务/OSD 已初始化，不阻塞启动界面。
            // 在 UI 进入交互前捕获身份，新建的 Startup 规则留到下次启动。
            var startupRuleIds = AutoRuleStore.LoadAll()
                .Where(rule => rule.Enabled && rule.Trigger == AutoRuleTrigger.Startup)
                .Select(rule => rule.Id).ToList();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => _ = RunStartupRulesAsync(audioWarmup, startupRuleIds)));
        }

        private static Task WarmupAudioAsync() => Task.Run(() =>
        {
            try { AudioService.GetApps(); } catch { }
            try { SessionVolumeService.Refresh(); } catch { }
        });

        private void StartRuleCommandServer(Task audioWarmup)
        {
            try { _ruleCommandServer = new RuleCommandServer(Dispatcher, audioWarmup); }
            catch (Exception ex) { AutoRuleScheduler.Log("CLI 通道初始化失败: " + ex.Message); }
        }

        private async Task ForwardRuleCommandAsync(string selector)
        {
            var result = await RuleCommandLine.SendAsync(selector);
            RuleCommandLine.WriteResult(result);
            Shutdown(result.ExitCode);
        }

        private async Task RunTemporaryRuleCommandAsync(string selector, Task audioWarmup)
        {
            RuleCommandLine.Result result;
            try
            {
                await audioWarmup;
                result = await RuleCommandLine.ExecuteAsync(selector);
                if (_ruleCommandServer != null) await _ruleCommandServer.StopAcceptingAsync();
                if (_trayWheel != null) await _trayWheel.FinishTemporaryOsdAsync();
            }
            catch (Exception ex)
            {
                AutoRuleScheduler.Log("CLI 临时执行失败: " + ex.Message);
                result = RuleCommandLine.Error(8, "internal_error");
            }
            RuleCommandLine.WriteResult(result);
            Shutdown(result.ExitCode);
        }

        private async Task RunStartupRulesAsync(Task audioWarmup, IReadOnlyList<string> ruleIds)
        {
            try
            {
                await audioWarmup;
                if (Dispatcher.HasShutdownStarted) return;
                await AutoRuleService.ExecuteStartupAsync(ruleIds);
            }
            catch (Exception ex) { AutoRuleScheduler.Log("Startup 执行失败: " + ex.Message); }
        }

        /// <summary>右上角 OSD 提示（托盘滚轮/快捷键/设置提示共用）。</summary>
        internal void ShowOsd(string app, string text) => _trayWheel?.ShowOsd(app, text);
        internal void ShowVolumeOsd(string app, int volume, string text, bool muted = false, string? target = null)
            => _trayWheel?.ShowVolumeOsd(app, volume, text, muted, target);
        internal void NotifyOsdVolumeVisualChanged() => _trayWheel?.NotifyOsdVolumeVisualChanged();
        /// <summary>重建托盘右键菜单（语言即时生效时调用：菜单文本取当前语言）。</summary>
        internal void RebuildTrayMenu()
        {
            // 下次右键时按当前语言/主题创建，关闭后不常驻菜单视觉树。
            CloseTrayMenu();
        }
        /// <summary>麦克风静音状态 OSD 统一入口（快捷键 / 后台检测器 / 面板共用）：静音且常驻开关开启 → 常驻显示。</summary>
        internal void ShowMicMuteOsd(string app, bool muted)
        {
            _lastMicMutedBaseline = muted;
            _micMuteBaselineReady = true;
            _trayWheel?.ShowMicMuteOsd(app, muted);
        }
        /// <summary>设置页「麦克风静音时 OSD 常驻」开关变化：立即生效（开启且已静音 → 常驻；关闭 → 退出常驻）。</summary>
        internal void NotifyMicMuteOsdSettingChanged(bool on)
        {
            _trayWheel?.NotifyMicMuteOsdSettingChanged(on);
            RefreshMicMonitoringState();
        }
        /// <summary>进入 OSD 调整模式（实验设置「调整位置」）。</summary>
        internal void BeginOsdAdjust() => _trayWheel?.BeginOsdAdjust();
        /// <summary>取消 OSD 调整（不保存）。</summary>
        internal void CancelOsdAdjust() => _trayWheel?.CancelOsdAdjust();
        /// <summary>实时位置预览（偏移滑块/坐标输入联动）。</summary>
        internal void PreviewOsd() => _trayWheel?.PreviewOsd();
        internal void ApplyOsdSize(double w, double fs) => _trayWheel?.ApplyOsdSize(w, fs);
        internal void SetOsdSize(int w, double fs) => _trayWheel?.SetOsdSize(w, fs);
        /// <summary>OSD 拖拽保存后通知（设置页复位按钮/同步输入框）。</summary>
        internal event Action? OsdAdjustFinished
        {
            add { if (_trayWheel != null) _trayWheel.OsdAdjustFinished += value; }
            remove { if (_trayWheel != null) _trayWheel.OsdAdjustFinished -= value; }
        }

        /// <summary>快速面板位置调整（主题页，逻辑同 OSD）：打开面板并进入拖拽调整模式（松手即保存自定义坐标）。</summary>
        internal void BeginQuickPanelAdjust()
        {
            if (_quickPanel == null) ToggleQuickPanel();
            _quickPanel?.SetAdjustMode(true);
        }

        /// <summary>取消快速面板位置调整（不保存，关闭面板）。</summary>
        internal void CancelQuickPanelAdjust()
        {
            _quickPanel?.SetAdjustMode(false);
            _quickPanel?.Close();
        }

        /// <summary>一键还原快速面板位置：恢复任务栏右下角默认位置，已打开则立即重定位。</summary>
        internal void ResetQuickPanelPosition()
        {
            var cfg = ConfigService.Load();
            cfg.QuickPanelPosMode = "default";
            cfg.QuickPanelCustomX = -1;
            cfg.QuickPanelCustomY = -1;
            ConfigService.Save(cfg);
            _quickPanel?.ResetPosition();
        }

        /// <summary>快速面板拖拽保存后通知（主题页复位按钮）。</summary>
        internal event Action? QuickPanelAdjustFinished;

        /// <summary>供面板窗口在拖拽保存/关闭时触发（外部类不能直接 Invoke 事件）。</summary>
        internal void NotifyQuickPanelAdjustFinished() => QuickPanelAdjustFinished?.Invoke();

        internal void NotifyAppIconChanged(string processName)
        {
            _mainWindow?.RefreshAppIcons(processName);
            _quickPanel?.RefreshAppIcons(processName);
        }

        internal void ApplyAppInputMuteReadback(int pid, bool muted) => _mainWindow?.ApplyAppInputMuteReadback(pid, muted);
        internal void ApplyAppMuteReadback(int pid, bool muted)
        {
            _quickPanel?.ApplyAppMuteReadback(pid, muted);
            _mainWindow?.ApplyAppMuteReadback(pid, muted);
        }

        /// <summary>检测当前是否运行在 MSIX 包中（非包环境调用 Package.Current 会抛异常）。
        /// net48 Legacy 为传统桌面程序，不依赖 WinRT / Windows App SDK，恒为 false。</summary>
        private static bool IsPackaged() => AppRuntimeIdentity.Current.IsPackaged;

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
                var exe = AppInfo.ExecutablePath;
                if (string.IsNullOrWhiteSpace(exe)) return;
                PortableStartupRegistration.Repair(exe, name => key.GetValue(name) as string,
                    (name, command) => key.SetValue(name, command!));
            }
            catch
            {
                // 静默：修复失败不影响启动
            }
        }

        internal bool SetQuickPanelVisibility(bool visible)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return false;
            if (visible)
            {
                if (_quickPanel?.IsVisible != true) ToggleQuickPanel();
                return _quickPanel?.IsVisible == true;
            }
            _quickPanel?.Close(); // No hidden panel is created just to close it.
            return _quickPanel == null || !_quickPanel.IsVisible;
        }

        internal void ToggleQuickPanel()
        {
            if (_quickPanel == null)
            {
                _uiWorkingSetDirty = true;
                CancelUiReclaim();
                AppIconService.Resume();
                _quickPanel = new QuickPanelModernWindow();
                _quickPanel.Closed += (_, _) =>
                {
                    _quickPanel = null;
                    if (_mainWindow == null)
                    {
                        AppIconService.TrimForIdle();
                        ScheduleUiReclaim();
                    }
                    else UpdateAppIconMetadataDemand();
                };
            }

            if (_quickPanel.IsVisible)
            {
                _quickPanel.Close();
                return;
            }

            _quickPanel.ShowQuickPanel();
        }

        // Reuse the existing window references and visibility events; no separate UI registry.
        internal void UpdateAppIconMetadataDemand() => AudioService.SetAppIconMetadataEnabled(
            (_mainWindow?.IsVisible == true && _mainWindow.WindowState != WindowState.Minimized)
            || _quickPanel?.IsVisible == true);


        private async Task RunUiBenchmarkAsync()
        {
            var logPath = Environment.GetEnvironmentVariable("SONICROUTE_BENCH_TRACE");
            if (string.IsNullOrWhiteSpace(logPath))
                logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui_benchmark.json");

            var results = new Dictionary<string, object>();
            var proc = System.Diagnostics.Process.GetCurrentProcess();

            (double WallMs, double CpuMs, long AllocBytes) Measure(Action action)
            {
                long startAlloc = GC.GetAllocatedBytesForCurrentThread();
                TimeSpan startCpu = proc.TotalProcessorTime;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                action();
                sw.Stop();
                TimeSpan endCpu = proc.TotalProcessorTime;
                long endAlloc = GC.GetAllocatedBytesForCurrentThread();
                return (sw.Elapsed.TotalMilliseconds, (endCpu - startCpu).TotalMilliseconds, endAlloc - startAlloc);
            }

            async Task<(double WallMs, double CpuMs, long AllocBytes)> MeasureAsync(Func<Task> action)
            {
                long startAlloc = GC.GetAllocatedBytesForCurrentThread();
                TimeSpan startCpu = proc.TotalProcessorTime;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await action();
                sw.Stop();
                TimeSpan endCpu = proc.TotalProcessorTime;
                long endAlloc = GC.GetAllocatedBytesForCurrentThread();
                return (sw.Elapsed.TotalMilliseconds, (endCpu - startCpu).TotalMilliseconds, endAlloc - startAlloc);
            }

            // 1. 首次创建 MainWindow
            SonicRoute.MainWindow? win = null;
            var m1 = Measure(() => { win = new SonicRoute.MainWindow(); });
            results["1_FirstMainWindowConstructor"] = new { WallMs = m1.WallMs, CpuMs = m1.CpuMs, AllocBytes = m1.AllocBytes, InitializeComponentMs = SonicRoute.MainWindow.LastInitializeComponentMs };

            // 2. 首次显示 MainWindow (包含渲染完成)
            var m2 = await MeasureAsync(async () =>
            {
                win!.Show();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            });
            results["2_FirstMainWindowShow"] = new { WallMs = m2.WallMs, CpuMs = m2.CpuMs, AllocBytes = m2.AllocBytes };

            // 3. 隐藏 MainWindow
            var m3 = await MeasureAsync(async () =>
            {
                win!.Hide();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            });
            results["3_MainWindowHide"] = new { WallMs = m3.WallMs, CpuMs = m3.CpuMs, AllocBytes = m3.AllocBytes };

            // 4. 再次打开已有 MainWindow
            var m4 = await MeasureAsync(async () =>
            {
                win!.Show();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            });
            results["4_MainWindowReShow"] = new { WallMs = m4.WallMs, CpuMs = m4.CpuMs, AllocBytes = m4.AllocBytes };

            // 5. 反复开关已有窗口 5 次
            var toggleWall = new List<double>();
            var toggleCpu = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var mt = await MeasureAsync(async () =>
                {
                    win!.Hide();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    win!.Show();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                });
                toggleWall.Add(mt.WallMs);
                toggleCpu.Add(mt.CpuMs);
            }
            results["5_MainWindowToggle5x_AvgWallMs"] = toggleWall.Average();
            results["5_MainWindowToggle5x_AvgCpuMs"] = toggleCpu.Average();
            win!.Close();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // 6. 快速面板首次打开与重复打开
            QuickPanelModernWindow? panel = null;
            var mp1 = await MeasureAsync(async () =>
            {
                panel = new QuickPanelModernWindow();
                panel.ShowQuickPanel();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            });
            results["6_1_FirstQuickPanelShow"] = new { WallMs = mp1.WallMs, CpuMs = mp1.CpuMs, AllocBytes = mp1.AllocBytes };

            var panelToggleWall = new List<double>();
            var panelToggleCpu = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var mt = await MeasureAsync(async () =>
                {
                    panel!.Hide();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    panel!.ShowQuickPanel();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                });
                panelToggleWall.Add(mt.WallMs);
                panelToggleCpu.Add(mt.CpuMs);
            }
            results["6_2_QuickPanelToggle5x_AvgWallMs"] = panelToggleWall.Average();
            results["6_2_QuickPanelToggle5x_AvgCpuMs"] = panelToggleCpu.Average();
            panel!.Close();

            string json = System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            try { System.IO.File.WriteAllText(logPath, json); } catch { }
            Console.WriteLine(json);
            Shutdown(0);
        }
        internal void ShowMainWindow()
        {
            if (_mainWindow == null)
            {
                _uiWorkingSetDirty = true;
                CancelUiReclaim();
                AppIconService.Resume();
                _mainWindow = new MainWindow();
                _mainWindow.Closed += (_, _) =>
                {
                    _mainWindow = null;
                    if (_quickPanel == null)
                    {
                        AppIconService.TrimForIdle();
                        ScheduleUiReclaim();
                    }
                    else UpdateAppIconMetadataDemand();
                };
            }

            _mainWindow.Show();
            _mainWindow.Activate();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
        }

        private void CancelUiReclaim()
        {
            _uiReclaimTimer?.Stop();
        }

        private void ScheduleUiReclaim()
        {
            if (!ShouldReclaimIdle() || _mainWindow != null || _quickPanel != null || _trayMenu != null || Dispatcher.HasShutdownStarted) return;
            if (_uiReclaimTimer == null)
            {
                _uiReclaimTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle, Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(1500) };
                _uiReclaimTimer.Tick += ReclaimUiWhenIdle;
            }
            _uiReclaimTimer.Stop();
            _uiReclaimTimer.Start();
        }

        private async Task ReclaimAfterWarmupAsync(Task warmup)
        {
            try { await warmup.ConfigureAwait(false); }
            catch { /* A failed warmup must not leave the startup idle request waiting forever. */ }
            try { await Dispatcher.InvokeAsync(() => { _startupWarmupComplete = true; ScheduleUiReclaim(); }).Task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
        }

        private bool ShouldReclaimIdle() => _uiWorkingSetDirty && _startupWarmupComplete && !_exiting;

        private void ReclaimUiWhenIdle(object? sender, EventArgs e)
        {
            // 复用单次延迟：先停止，再在同一个空闲 Dispatcher 回合核验并回收，不周期唤醒。
            _uiReclaimTimer?.Stop();
            if (_mainWindow != null || _quickPanel != null || _trayMenu != null || !ShouldReclaimIdle()
                || Dispatcher.HasShutdownStarted) return;
            TrimWorkingSet();
            _uiWorkingSetDirty = false;
        }

        // Single-instance activation uses a hidden message window.
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr NativeFindWindow(string? cls, string? win);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern bool NativePostMessage(IntPtr h, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int NativeRegisterWindowMessage(string name);

        /// <summary>请求系统移出不活跃的工作集页面；不释放私有提交量，也不保证页面不会重新进入工作集。</summary>
        private static void TrimWorkingSet()
        {
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                SetProcessWorkingSetSize(p.Handle, new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        /// <summary>重新加载全局快捷键（设置页修改后调用）。实验模式的隐藏动作仅在"实验模式+麦克风选项"开启时注册。</summary>
        internal void ReloadHotkeys(bool force = false)
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
            // 自动化规则快捷键：Rule:{Id} 动作并入同一注册通道；冲突/无效时不注册（RegistrationStatus 标记空）
            foreach (var r in AutoRuleStore.LoadAll())
            {
                if (r.Enabled && r.Trigger == AutoRuleTrigger.Hotkey && !string.IsNullOrWhiteSpace(r.Hotkey))
                    map[AutoRuleService.HotkeyPrefix + r.Id] = r.Hotkey;
            }
            _hotkeys.Reload(map, force);
            // 应用启动 / 前台切换触发监听：仅当存在启用规则时启动（无规则自动停止）
            AutoRuleService.RefreshWatcher();
            // 定时触发调度：规则增删改/启停后立即重新计算最近一次执行时刻
            AutoRuleScheduler.RefreshScheduler();
        }

        /// <summary>快捷键实际注册状态：动作 → 生效组合（用于设置页显示占用冲突）。</summary>
        internal IReadOnlyDictionary<string, string> HotkeyRegistration =>
            _hotkeys?.RegistrationStatus ?? new Dictionary<string, string>();

        internal bool IsHotkeyCaptureActive => _mainWindow is { IsVisible: true } && _mainWindow.IsHotkeyRecording;
        internal bool ShouldBypassTrayWheel(int delta) => IsHotkeyCaptureActive || _hotkeys?.HandlesMouseWheel(delta) == true;

        private readonly SemaphoreSlim _volumeHotkeyGate = new(1, 1);

        private async Task ExecuteHotkeyAsync(string action)
        {
            if (action is not (HotkeyActions.ActVolUp or HotkeyActions.ActVolDown
                or HotkeyActions.ActSystemVolUp or HotkeyActions.ActSystemVolDown))
            {
                await ExecuteHotkeyCoreAsync(action);
                return;
            }
            int? foregroundPid = action is HotkeyActions.ActVolUp or HotkeyActions.ActVolDown
                ? ForegroundAppService.GetForegroundProcessId() : null;
            // 连续增减按输入顺序执行；等待不阻塞 UI，防止多轮读旧值/旧 OSD 覆盖。
            await _volumeHotkeyGate.WaitAsync();
            try { await ExecuteHotkeyCoreAsync(action, foregroundPid); }
            finally { _volumeHotkeyGate.Release(); }
        }

        private async Task ExecuteHotkeyCoreAsync(string action, int? foregroundPid = null)

        {
            // 自动化规则快捷键分发：Rule:{Id} → 规则引擎执行（无匹配规则时忽略）
            if (action.StartsWith(AutoRuleService.HotkeyPrefix, StringComparison.Ordinal))
            {
                // net48 无 string[Range] 索引器（.NET Core 3.0+）：用 Substring 等价实现
                await AutoRuleService.ExecuteByHotkeyAsync(action.Substring(AutoRuleService.HotkeyPrefix.Length));
                return;
            }

            if (action == HotkeyActions.ActPanel)
            {
                ToggleQuickPanel();
                return;
            }

            if (action == HotkeyActions.ActResetAllApps)
            {
                var result = await Task.Run(() => AudioService.ResetAllPersistedEndpointsChecked());
                _trayWheel?.ShowOsd(L10n.T("Act.ResetAllApps"), L10n.T(result.Applied
                    ? "Act.ResetAllAppsDone" : "Act.ResetAllAppsFailed"));
                return;
            }

            // 系统设备音量不依赖当前应用，也不受面板所选设备影响。
            if (action is HotkeyActions.ActSystemVolUp or HotkeyActions.ActSystemVolDown)
            {
                await AdjustDefaultOutputVolumeAsync(action == HotkeyActions.ActSystemVolUp);
                return;
            }

            // 静音/切设备/音量的目标应用：与托盘滚轮调音量完全一致——直接走
            // CurrentAppService.Resolve 同一套规则（last/fixed → 前台 → 最近使用 → 兜底），
            // 保证快捷键和"鼠标放任务栏滚轮调音量"永远解析出同一个应用。
            // 仅"当前应用"类动作需要解析当前应用；全局切换 / 系统默认切换不依赖，解析失败照常执行
            bool needsTarget = action is HotkeyActions.ActMute or HotkeyActions.ActVolUp or HotkeyActions.ActVolDown or HotkeyActions.ActSwitchOutput or HotkeyActions.ActSwitchInput;
            AudioAppInfo? target = null;
            if (needsTarget)
            {
                var cfg = ConfigService.Load();
                int fg = foregroundPid ?? ForegroundAppService.GetForegroundProcessId();
                try { target = await Task.Run(() => CurrentAppService.Resolve(AudioService.GetApps(), cfg, fg)); }
                catch { /* 会话发现失败只影响当前应用类动作，仍给出已有的无会话反馈。 */ }
                if (target == null) { _trayWheel?.ShowOsd(L10n.T("Ov.VolumeTitle"), L10n.T("Ov.NoOutputSession")); return; }
            }
            int pid = target == null ? -1 : (int)target.ProcessId;
            string name = target == null ? "" : AppDisplayName.Get(target);

            switch (action)
            {
                case HotkeyActions.ActMute:
                    // 当前应用快捷键始终控制同一个会话；面板顶部按钮只控制所选系统设备。
                    var mr = await Task.Run(() => SessionVolumeService.ToggleMuteChecked(pid));
                    if (mr.Applied) ApplyAppMuteReadback(pid, mr.Muted);
                    _trayWheel?.ShowOsd(name, mr.Applied
                        ? (mr.Muted ? L10n.T("Ov.AppMuted") : L10n.T("Ov.AppUnmuted"))
                        : L10n.T("Ov.NoOutputSession"));
                    break;

                case HotkeyActions.ActMuteInput:
                    // 全局麦克风静音：静音/取消静音系统所有录音设备（与当前应用无关）。
                    // 统一执行一次，面板仅同步读回；OSD 也只经共享入口显示一次。
                    bool gm = await Task.Run(() => GlobalMicMuteService.Toggle());
                    _quickPanel?.ApplyMicMuteReadback(gm);
                    ShowMicMuteOsd(L10n.T("Ov.MuteMic"), gm); // 全局麦克风静音：标题固定「麦克风静音」，不显示应用名
                    break;

                case HotkeyActions.ActVolUp:
                case HotkeyActions.ActVolDown:
                    // 当前应用直接走会话调整，再用读回值同步快速面板的对应应用行。
                    {
                        int step = MathEx.Clamp(ConfigService.Load().VolumeStep, 1, 20);
                        int delta = action == HotkeyActions.ActVolUp ? step : -step;
                        var adjusted = await Task.Run(() => SessionVolumeService.AdjustVolumePercent(pid, delta));
                        if (adjusted.Current < 0) { _trayWheel?.ShowOsd(name, "⚠ " + L10n.T("Ov.NoOutputSession")); break; }
                        if (adjusted.Applied && adjusted.Actual >= 0)
                        {
                            if (_quickPanel is { IsVisible: true } modern)
                                modern.ApplyAppVolumeReadback(pid, adjusted.Actual);
                            _trayWheel?.ShowVolumeOsd(name, adjusted.Actual, $"🔉 {adjusted.Actual}%", target: "app:" + pid);
                        }
                        else _trayWheel?.ShowOsd(name, L10n.T("Ov.VolAdjustFail"));
                        break;
                    }

                case HotkeyActions.ActSwitchOutput:
                    string? dev = await CycleDeviceAsync(pid, EDataFlow.eRender);
                    _trayWheel?.ShowOsd(name, string.IsNullOrEmpty(dev) ? L10n.T("Ov.NoDevice") : $"🔊 {dev}");
                    break;

                case HotkeyActions.ActSwitchInput:
                    // 实验模式 - 麦克风选项开启后才注册的隐藏动作：切换当前应用的录音（输入）设备
                    string? mdev = await CycleDeviceAsync(pid, EDataFlow.eCapture);
                    _trayWheel?.ShowOsd(name, string.IsNullOrEmpty(mdev) ? L10n.T("Ov.NoMicDevice") : $"🎤 {mdev}");

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

        internal async Task AdjustDefaultOutputVolumeAsync(bool increase)
        {
            string title = L10n.T("Dev.SystemDefaultOut");
            int step = MathEx.Clamp(ConfigService.Load().VolumeStep, 1, 20);
            try
            {
                var result = await Task.Run(() =>
                {
                    // 每次解析默认输出；本次调整、静音读回和 OSD 指向同一个端点。
                    string? deviceId = AudioService.GetDefaultDeviceId(EDataFlow.eRender);
                    if (deviceId == null)
                        return (DeviceId: (string?)null, Current: -1, Actual: -1, Applied: false, Muted: false);
                    var adjusted = SystemVolumeService.AdjustVolumeWithState(deviceId, increase ? step : -step);
                    return (DeviceId: deviceId, adjusted.Current, adjusted.Actual, adjusted.Applied, adjusted.Muted);
                });
                if (result.Current < 0)
                {
                    _trayWheel?.ShowOsd(title, L10n.T("Ov.NoDevice"));
                    return;
                }
                ShowSystemVolumeFeedback(result.DeviceId, result.Actual, result.Applied, result.Muted, title);
            }
            catch { _trayWheel?.ShowOsd(title, L10n.T("Ov.VolAdjustFail")); }
        }

        internal void ShowSystemVolumeFeedback(string? deviceId, int actual, bool applied, bool muted, string title)
        {
            if (actual >= 0 && _quickPanel is { IsVisible: true } modern)
                modern.ApplySystemVolumeReadback(deviceId, actual, muted);
            if (applied && actual >= 0)
                ShowVolumeOsd(title, actual, string.Format(L10n.T("Qp.VolOk"), actual), muted, "device:" + deviceId);
            else ShowOsd(title, L10n.T("Ov.VolAdjustFail"));
        }

        /// <summary>构建可见设备列表（真实保留设备，按隐藏集合过滤）。</summary>
        private static List<AudioDeviceInfo> BuildVisibleDevices(EDataFlow flow, AppConfig config)
        {
            var devs = AudioService.GetDevices(flow);
            var hidden = flow == EDataFlow.eRender ? config.HiddenOutputDevices : config.HiddenInputDevices;
            var list = devs.Where(d => !hidden.Contains(d.Id)).ToList();
            // 列表头部加入"系统默认输出/输入"虚拟项（与快速面板/概览一致），选中即切回跟随系统默认
            return PanelDevices.WithSystemDefault(list, flow, config);
        }
        /// <summary>设备名（自定义名优先）。</summary>
        private static string DeviceDisplayName(AppConfig config, AudioDeviceInfo dev)
        {
            return config.DeviceNames.TryGetValue(dev.Id, out var n) && !string.IsNullOrWhiteSpace(n)
                ? n
                : dev.DisplayName ?? string.Empty;
        }

        /// <summary>在当前应用的可见设备间循环切换，返回切换到的设备名；失败/无设备返回 null。
        /// 当前"跟随系统默认"（无持久化）且默认项在列表 → 从默认项按方向切换。</summary>
        private static Task<string?> CycleDeviceAsync(int pid, EDataFlow flow) =>
            Task.Run(() => CycleDevice(pid, flow, previous: false));

        // 托盘Alt滚轮复用同一切换实现；合并多个刻度只读取/写入一次设备路由。
        internal static string? CycleDevice(int pid, EDataFlow flow, bool previous, int steps = 1)
        {
            try
            {
                var config = ConfigService.Load();
                var visible = BuildVisibleDevices(flow, config);
                if (visible.Count == 0) return null;

                var persisted = AudioService.GetPersistedEndpoint(pid, flow);
                string? curShort = persisted == null ? null : AudioPolicyConfig.UnpackDeviceId(persisted);
                // 跟随系统默认（无持久化）→ 位于"系统默认"虚拟项（首位）。
                int idx = visible.FindIndex(d => string.Equals(d.Id, curShort, StringComparison.OrdinalIgnoreCase));
                if (idx < 0 && persisted == null && AudioService.IsSystemDefault(visible[0].Id)) idx = 0;  // 仅当"系统默认"虚拟项在列表首位时（未被隐藏），从它开始循环
                int next = PanelDevices.CycleIndex(idx, visible.Count, previous, steps);
                var target = visible[next];
                var r = AudioService.ApplyEndpoint(pid, flow, target.Id);
                if (!r.Success) return null;
                return DeviceDisplayName(config, target);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>在系统默认输出/输入设备间循环切换：从当前默认的下一个可见设备开始，改系统默认设备。
        /// 返回切换到的设备名（用自定义名）；失败/无设备返回 null。</summary>
        private static Task<string?> CycleSystemDefaultDeviceAsync(EDataFlow flow) =>
            Task.Run(() => CycleSystemDefaultDevice(flow, previous: false));

        // 原快捷键与托盘Alt滚轮复用系统默认设备切换，避免重复枚举及多刻度重复写入。
        internal static string? CycleSystemDefaultDevice(EDataFlow flow, bool previous, int steps = 1)
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
                int next = PanelDevices.CycleIndex(idx, visible.Count, previous, steps);
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
        }

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

        private void Quit()
        {
            _exiting = true;
            Updates.Dispose();
            _mainWindow?.FlushPendingSettings();
            CancelUiReclaim();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            AutoRuleScheduler.Shutdown();
            AutoRuleService.Shutdown();
            _ruleCommandServer?.Dispose(); _ruleCommandServer = null;
            StopBackgroundMonitoring();
            _trayWheel?.Dispose();
            _trayWheel = null;
            _hotkeys?.Dispose();
            _hotkeys = null;
            DisposeTray();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Updates.Dispose();
            _exiting = true;
            _mainWindow?.FlushPendingSettings();
            CancelUiReclaim();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            AutoRuleScheduler.Shutdown();
            AutoRuleService.Shutdown();
            _ruleCommandServer?.Dispose(); _ruleCommandServer = null;
            StopBackgroundMonitoring();
            _trayWheel?.Dispose();
            _trayWheel = null;
            _hotkeys?.Dispose();
            _hotkeys = null;
            DisposeTray();
            _activateSink?.Dispose(); _activateSink = null;
            if (_ownsInstanceMutex)
            {
                try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
                _ownsInstanceMutex = false;
            }
            _instanceMutex?.Dispose(); _instanceMutex = null;
            base.OnExit(e);
        }

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
            var tray = _trayIcon;
            _trayIcon = null;
            CloseTrayMenu();
            if (tray == null) return;
            try { tray.Visible = false; } catch { }
            try { tray.Icon?.Dispose(); } catch { }
            tray.Dispose();
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
    }
}


using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using SonicRoute.Core;
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
        private QuickPanelWindow? _quickPanel;
        private HotkeyService? _hotkeys;
        private TrayWheelService? _trayWheel;
        private CancellationTokenSource? _singleClickCts;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
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

            _trayIcon = new NotifyIcon
            {
                Icon = IconFactory.CreateAppIcon(),
                Text = "音跃 SonicRoute v1.0.7r2",
                Visible = true
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add(L10n.T("St.Settings"), null, (_, _) => ShowMainWindow());
            menu.Items.Add(L10n.T("Tray.OpenPanel"), null, (_, _) => ToggleQuickPanel());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L10n.T("Tray.Exit"), null, (_, _) => Quit());
            _trayIcon.ContextMenuStrip = menu;

            // 单击左键 → 快速面板（延时判别，避免与双击冲突）
            _trayIcon.MouseClick += (_, args) =>
            {
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
            ReloadHotkeys();

            // 托盘滚轮调音量
            _trayWheel = new TrayWheelService();
            _trayWheel.Start();

            // 前台监听：recent 模式下自动跟随前台音频应用（抖音/游戏等），并维护"最近有音频的前台应用"
            CurrentAppService.StartForegroundWatcher();

            // 后台预热音频会话/应用缓存，避免首次滚轮/切语言时在 UI 线程做重量级 COM 枚举
            _ = Task.Run(() =>
            {
                try { AudioService.GetApps(); } catch { }
                try { SessionVolumeService.Refresh(); } catch { }
            });

            // 启动行为
            bool showMain = !config.StartMinimized;
            if (e.Args.Contains("--panel", StringComparer.OrdinalIgnoreCase))
                Dispatcher.BeginInvoke(ToggleQuickPanel);
            else if (e.Args.Contains("--main", StringComparer.OrdinalIgnoreCase))
                Dispatcher.BeginInvoke(ShowMainWindow);
            else if (config.StartPanelOnStart)
                Dispatcher.BeginInvoke(ToggleQuickPanel);
            else if (showMain)
                Dispatcher.BeginInvoke(ShowMainWindow);
        }

        /// <summary>右上角 OSD 提示（托盘滚轮/快捷键/设置提示共用）。</summary>
        internal void ShowOsd(string app, string text) => _trayWheel?.ShowOsd(app, text);

        internal void ToggleQuickPanel()
        {
            if (_quickPanel == null)
            {
                _quickPanel = new QuickPanelWindow();
                _quickPanel.Closed += (_, _) => _quickPanel = null;
            }

            if (_quickPanel.IsVisible)
            {
                _quickPanel.Close();
                // 实验设置「释放快速面板 UI 内存」：面板关闭后立即 + 延迟多次（1s/3s/5s）强制回收面板 UI 内存
                if (ConfigService.Load().FreePanelUIMemory)
                    _ = Task.Run(async () =>
                    {
                        GcNow();
                        foreach (var ms in new[] { 1000, 3000, 5000 })
                        {
                            await Task.Delay(ms);
                            GcNow();
                        }
                    });
                return;
            }

            _quickPanel.ShowQuickPanel();
        }

        internal void ShowMainWindow()
        {
            if (_mainWindow == null)
            {
                _mainWindow = new MainWindow();
                _mainWindow.Closed += (_, _) =>
                {
                    _mainWindow = null;
                    // 实验设置「关闭 UI 释放内存」：窗口真正关闭后强制回收 UI 内存。
                    // 立即回收一次，再延迟多次重试（1s/3s/5s）：窗口关闭瞬间可能有挂起的异步续体
                    // （切设备/调音量/刷新应用等，闭包会捕获窗口对象），等它们跑完后窗口才真正可回收，
                    // 此时再次 GC 确保窗口与视觉树被回收。
                    if (ConfigService.Load().FreeUIMemoryOnClose)
                        _ = Task.Run(async () =>
                        {
                            GcNow();
                            foreach (var ms in new[] { 1000, 3000, 5000 })
                            {
                                await Task.Delay(ms);
                                GcNow();
                            }
                        });
                };
            }

            _mainWindow.Show();
            _mainWindow.Activate();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
        }

        /// <summary>强制回收：GC 两轮（含终结器队列），用于「关闭 UI 释放内存」时尽快回收窗口与 UI 资源。</summary>
        private static void GcNow()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch { }
        }

        /// <summary>重新加载全局快捷键（设置页修改后调用）。实验模式的隐藏动作仅在"实验模式+麦克风选项"开启时注册。</summary>
        internal void ReloadHotkeys()
        {
            if (_hotkeys == null) return;
            var config = ConfigService.Load();
            bool expMicOn = config.ExperimentalMode && config.ExperimentalMic;
            var map = new Dictionary<string, string>();
            foreach (var a in HotkeyActions.All)
            {
                // 实验模式隐藏动作：未开启麦克风选项不注册，避免后台占用组合键
                if (a == HotkeyActions.ActSwitchInput && !expMicOn) continue;
                map[a] = config.Hotkeys.TryGetValue(a, out var c)
                    ? c
                    : (HotkeyActions.Defaults.TryGetValue(a, out var d) ? d : "");
            }
            _hotkeys.Reload(map);
        }

        /// <summary>快捷键实际注册状态：动作 → 生效组合（用于设置页显示占用冲突）。</summary>
        internal IReadOnlyDictionary<string, string> HotkeyRegistration =>
            _hotkeys?.RegistrationStatus ?? new Dictionary<string, string>();

        private async Task ExecuteHotkeyAsync(string action)
        {
            if (action == HotkeyActions.ActPanel)
            {
                ToggleQuickPanel();
                return;
            }

            // 静音/切设备/音量的目标应用：与托盘滚轮调音量完全一致——直接走
            // CurrentAppService.Resolve 同一套规则（last/fixed → 前台 → 最近使用 → 兜底），
            // 保证快捷键和"鼠标放任务栏滚轮调音量"永远解析出同一个应用。
            var cfg = ConfigService.Load();
            var apps = await Task.Run(() => AudioService.GetApps());
            var target = CurrentAppService.Resolve(apps, cfg);
            if (target == null) return;
            int pid = (int)target.ProcessId;
            string name = AppDisplayName.Get(target);
            if (pid <= 0) return;

            switch (action)
            {
                case HotkeyActions.ActMute:
                    // 优先走快捷面板的静音路径：与面板静音按钮完全一致，静音的是面板/概览
                    // 显示的同一个当前应用，并同步面板按钮文字/状态行。面板未打开或无当前
                    // 应用时回退到共享当前应用路径。
                    if (_quickPanel is { IsVisible: true } && await _quickPanel.MuteCurrentAppAsync())
                        break;
                    var mr = await Task.Run(() => SessionVolumeService.ToggleMuteChecked(pid));
                    _trayWheel?.ShowOsd(name, mr.Applied
                        ? (mr.Muted ? "🔇 已静音" : "🔊 取消静音")
                        : "⚠ 该应用无输出会话");
                    break;

                case HotkeyActions.ActMuteInput:
                    // 全局麦克风静音：静音/取消静音系统所有录音设备（与当前应用无关）。
                    // 面板打开时走面板路径（同步按钮/状态行），否则直接全局静音并 OSD。
                    if (_quickPanel is { IsVisible: true })
                    {
                        await _quickPanel.ToggleGlobalMicMuteAsync();
                    }
                    else
                    {
                        bool gm = await Task.Run(() => GlobalMicMuteService.Toggle());
                        _trayWheel?.ShowOsd(name, gm
                            ? L10n.T("Ov.MicMuted")
                            : L10n.T("Ov.MicUnmuted"));
                    }
                    break;

                case HotkeyActions.ActVolUp:
                case HotkeyActions.ActVolDown:
                    // 调整面板/概览显示的当前应用音量（每次 ±5%）。面板打开时走面板路径
                    // （与 ± 按钮一致并同步滑块/状态行）；否则直接对共享当前应用调整并 OSD。
                    {
                        int delta = action == HotkeyActions.ActVolUp ? 5 : -5;
                        if (_quickPanel is { IsVisible: true })
                        {
                            int v = await _quickPanel.AdjustVolumeAsync(delta);
                            if (v >= 0) { _trayWheel?.ShowOsd(name, $"🔉 {v}%"); break; }
                        }
                        int curVol = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
                        if (curVol < 0) { _trayWheel?.ShowOsd(name, "⚠ 该应用无输出会话"); break; }
                        int nextVol = Math.Clamp(curVol + delta, 0, 100);
                        bool ok = await Task.Run(() => SessionVolumeService.SetVolumePercent(pid, nextVol));
                        int act = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
                        _trayWheel?.ShowOsd(name, ok && act >= 0 ? $"🔉 {act}%" : "⚠ 调整失败");
                        break;
                    }

                case HotkeyActions.ActSwitchOutput:
                    string? dev = await CycleDeviceAsync(pid, EDataFlow.eRender);
                    _trayWheel?.ShowOsd(name, string.IsNullOrEmpty(dev) ? "无可用设备" : $"🔊 {dev}");
                    break;

                case HotkeyActions.ActSwitchInput:
                    // 实验模式 - 麦克风选项开启后才注册的隐藏动作：切换当前应用的录音（输入）设备
                    string? mdev = await CycleDeviceAsync(pid, EDataFlow.eCapture);
                    _trayWheel?.ShowOsd(name, string.IsNullOrEmpty(mdev) ? "无可用麦克风设备" : $"🎤 {mdev}");
                    break;
            }
        }

        /// <summary>在当前应用的可见播放设备间循环切换，返回切换到的设备名；失败/无设备返回 null。</summary>
        private static Task<string?> CycleDeviceAsync(int pid, EDataFlow flow) => Task.Run(() =>
        {
            try
            {
                var config = ConfigService.Load();
                var devs = AudioService.GetDevices(flow);
                var hidden = flow == EDataFlow.eRender ? config.HiddenOutputDevices : config.HiddenInputDevices;
                var visible = devs.Where(d => !hidden.Contains(d.Id)).ToList();
                if (visible.Count == 0) return null;

                var persisted = AudioService.GetPersistedEndpoint(pid, flow);
                string? curShort = persisted == null ? null : AudioPolicyConfig.UnpackDeviceId(persisted);
                int idx = visible.FindIndex(d => string.Equals(d.Id, curShort, StringComparison.OrdinalIgnoreCase));
                int next = idx < 0 ? 0 : (idx + 1) % visible.Count;
                var r = AudioService.ApplyEndpoint(pid, flow, visible[next].Id);
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

        private void Quit()
        {
            _trayWheel?.Dispose();
            _trayWheel = null;
            _hotkeys?.Dispose();
            _hotkeys = null;
            DisposeTray();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _trayWheel?.Dispose();
            _trayWheel = null;
            _hotkeys?.Dispose();
            _hotkeys = null;
            DisposeTray();
            base.OnExit(e);
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

        /// <summary>首次启动：按 Windows 系统 UI 语言匹配到支持的语言；未匹配则默认英文。</summary>
        private static string DetectSystemLanguage()
        {
            try
            {
                var ci = System.Globalization.CultureInfo.InstalledUICulture;
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

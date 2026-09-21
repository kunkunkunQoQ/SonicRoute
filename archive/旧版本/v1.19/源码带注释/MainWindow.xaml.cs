using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Dock = System.Windows.Controls.Dock;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SonicRoute
{
    public partial class MainWindow : Window
    {
        private bool _isClosed;
        private HwndSource? _hwndSource;
        private List<AudioDeviceInfo> _outputs = new();
        private List<AudioDeviceInfo> _outputDisplay = new();
        private List<AudioDeviceInfo> _inputs = new();
        private List<AudioDeviceInfo> _inputDisplay = new();
        private AudioAppInfo? _overviewApp;
        private AudioAppInfo? _appsSelected;
        private bool _suppressVolume;
        private bool _overviewVolumeReady;
        private bool _appsVolumeReady;
        private bool _suppressAppCombo;
        private bool _suppressDevCombo;
        private bool _suppressSettings;


        private bool _suppressFilter;
        private bool _suppressRename;
        private List<AppItem> _appItems = new();
        private readonly AppConfig _config;
        // ===== 鑷姩鍖栬鍒欙紙鏋佺畝鑷姩鍖栭〉锛?=====
        private List<AudioAppInfo> _autoApps = new();
        /// <summary>自动化页低频自动刷新应用列表（慢更新：15s，仅页面可见时运行）。</summary>
        private System.Windows.Threading.DispatcherTimer? _autoRefreshTimer;
        /// <summary>当前已创建的步骤应用下拉（供慢刷新重填，RenderAutoSteps 重建时清理）。</summary>
        private readonly List<System.Windows.Controls.ComboBox> _autoStepAppCombos = new();
        private string? _autoEditingId;
        private bool _autoCapturingHotkey;
        private string _autoHotkeyCombo = "";
        private bool _suppressAutoUi;

        public MainWindow()
        {
            InitializeComponent();
            Title = $"音跃 SonicRoute {App.DisplayVersion}";
            if (HeaderTitleText != null)
                HeaderTitleText.Text = $"🎧 音跃 SonicRoute {App.DisplayVersion}";
            _config = ConfigService.Load();
            Loaded += async (_, _) =>
            {
                await LoadDevicesAsync();
                await RefreshOverviewAsync();
                BuildDeviceNameLists();
                SyncOsdSliders();
                // 实验 UI（概览/应用/设置输入区/名称区）可见性：麦克风选项开启时显示
                ApplyExpMicUi(_config.ExperimentalMic);
                NavExperimental.Visibility = _config.ExperimentalUnlocked && _config.ExperimentalMode
                    ? Visibility.Visible : Visibility.Collapsed;
            };
            // 共享"当前应用"变化（前台自动跟随/面板切换）时同步概览
            CurrentAppService.CurrentChanged += OnSharedCurrentChanged;
            Closed += (_, _) =>
            {
                _isClosed = true;
                // 关闭面板：停止自动化页应用列表低频刷新（否则 Timer 随 Dispatcher 常驻并持有窗口引用）
                StopAutoRefresh();
                CurrentAppService.CurrentChanged -= OnSharedCurrentChanged;
                PreviewKeyDown -= MainWindow_PreviewKeyDown;
                _hwndSource?.RemoveHook(TaskbarMinimizeWndProc);
                _hwndSource = null;
                // 置空音频对象/UI 列表引用，帮助窗口与视觉树更快被 GC 回收（关闭 UI 释放内存优化）
                _outputs = new(); _outputDisplay = new();
                _inputs = new(); _inputDisplay = new();
                _overviewApp = null; _appsSelected = null;
                _appItems = new();
            };
            // 快捷键内联录音：在窗口内直接捕获按键，免弹窗
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            // 快捷键内联录音（鼠标键）：录制态下支持绑定中键/侧键（XButton1/2）
            PreviewMouseDown += MainWindow_PreviewMouseDown;
            // 快捷键内联录音（滚轮）：录制态下支持 Ctrl+滚轮上/下绑定（WheelUp/WheelDown）
            PreviewMouseWheel += MainWindow_PreviewMouseWheel;
            // 自绘边框窗口：点击任务栏图标也能最小化（补 WS_MINIMIZEBOX + 拦截系统最小化命令）
            SourceInitialized += MainWindow_SourceInitialized;
        }

        /// <summary>窗口句柄就绪后：确保带"最小化框"样式，并拦截任务栏/系统发出的最小化命令。</summary>
        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            const int GWL_STYLE = -16;
            const int WS_MINIMIZEBOX = 0x00020000;
            var style = GetWindowLong(hwnd, GWL_STYLE);
            if ((style & WS_MINIMIZEBOX) == 0)
                SetWindowLong(hwnd, GWL_STYLE, style | WS_MINIMIZEBOX);
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(TaskbarMinimizeWndProc);
        }

        /// <summary>WM_SYSCOMMAND / SC_MINIMIZE：点击任务栏图标时让窗口真正最小化。</summary>
        private IntPtr TaskbarMinimizeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_MINIMIZE = 0xF020;
            if (msg == WM_SYSCOMMAND && (((int)wParam) & 0xFFF0) == SC_MINIMIZE)
            {
                WindowState = WindowState.Minimized;
                handled = true;
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_autoCapturingHotkey)
            {
                e.Handled = true;
                if (e.Key == Key.Escape)
                {
                    _autoCapturingHotkey = false;
                    _autoHotkeyCombo = "";
                    UpdateAutoHotkeyHint();
                    return;
                }
                var autoCombo = HotkeyActions.Format(e);
                if (autoCombo == null) return;
                _autoCapturingHotkey = false;
                _autoHotkeyCombo = autoCombo;
                UpdateAutoHotkeyHint();
                return;
            }
            if (_recordingAction == null) return;
            e.Handled = true;
            if (e.Key == Key.Escape)
            {
                // Esc = 不绑定任何东西：清除该动作的快捷键（不是保留原绑定）
                if (_recordingAction != null)
                {
                    var escAction = _recordingAction;
                    _recordingAction = null;
                    _config.Hotkeys[escAction] = "";
                    ConfigService.Save(_config);
                    ((App)Application.Current).ReloadHotkeys();
                    BuildHotkeyList();
                }
                return;
            }
            var combo = HotkeyActions.Format(e);
            if (combo == null) return; // 缺少修饰键，等待有效组合
            var action = _recordingAction;
            _recordingAction = null;
            _config.Hotkeys[action] = combo;
            ConfigService.Save(_config);
            // 先重载注册（让 registered 反映新组合），再按最新注册刷新显示
            ((App)Application.Current).ReloadHotkeys();
            BuildHotkeyList();
        }

        /// <summary>录制态下捕获鼠标键（中键/侧键）绑定为快捷键；左键/右键忽略（不吞事件，避免影响 UI 交互）。</summary>
        private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_autoCapturingHotkey)
            {
                var autoCombo = HotkeyActions.FormatMouse(e);
                if (autoCombo == null) return;
                e.Handled = true;
                _autoCapturingHotkey = false;
                _autoHotkeyCombo = autoCombo;
                UpdateAutoHotkeyHint();
                return;
            }
            if (_recordingAction == null) return;
            var combo = HotkeyActions.FormatMouse(e);
            if (combo == null) return; // 左键/右键等不可绑定的鼠标键：忽略
            e.Handled = true;
            var action = _recordingAction;
            _recordingAction = null;
            _config.Hotkeys[action] = combo;
            ConfigService.Save(_config);
            ((App)Application.Current).ReloadHotkeys();
            BuildHotkeyList();
        }

        /// <summary>录制态下捕获鼠标滚轮：Ctrl+滚轮上 = Ctrl+WheelUp，滚轮下 = WheelDown（可带修饰键）。</summary>
        private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_autoCapturingHotkey)
            {
                var autoCombo = HotkeyActions.FormatWheel(e);
                if (autoCombo == null) return;
                e.Handled = true;
                _autoCapturingHotkey = false;
                _autoHotkeyCombo = autoCombo;
                UpdateAutoHotkeyHint();
                return;
            }
            if (_recordingAction == null) return;
            var combo = HotkeyActions.FormatWheel(e);
            if (combo == null) return;
            e.Handled = true;
            var action = _recordingAction;
            _recordingAction = null;
            _config.Hotkeys[action] = combo;
            ConfigService.Save(_config);
            ((App)Application.Current).ReloadHotkeys();
            BuildHotkeyList();
        }

        private async void OnSharedCurrentChanged()
        {
            try
            {
                if (!IsLoaded || OverviewPage.Visibility != Visibility.Visible) return;
                var cur = CurrentAppService.Current;
                if (cur == null) return;
                if (_overviewApp != null && _overviewApp.ProcessId == cur.ProcessId) return;
                await SetOverviewAppAsync(cur);
                // 同步下拉选中（_suppressAppCombo 防误触发应用切换）
                if (OverviewAppCombo.ItemsSource is System.Collections.IEnumerable src)
                {
                    _suppressAppCombo = true;
                    OverviewAppCombo.SelectedItem = src.OfType<AppItem>()
                        .FirstOrDefault(i => i.ProcessId == (int)cur.ProcessId);
                    _suppressAppCombo = false;
                }
            }
            catch { }
        }

        // ==================================================================
        // 标题栏（无边框圆角窗口：拖动 / 最小化 / 最大化 / 关闭）
        // ==================================================================

        private bool _titleMaximized;
        private readonly double _restoreWidth = 920, _restoreHeight = 620;

        // 快捷键内联录音：正在等待重新绑定的动作名（非 null 表示处于录音态）
        private string? _recordingAction;

        private void Titlebar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (!_titleMaximized) DragMove();
        }

        private void TitlebarMin_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void TitlebarMax_Click(object sender, RoutedEventArgs e)
        {
            if (!_titleMaximized)
            {
                _titleMaximized = true;
                var wa = SystemParameters.WorkArea;
                WindowState = WindowState.Normal;
                Left = wa.Left;
                Top = wa.Top;
                Width = wa.Width;
                Height = wa.Height;
                RootBorder.CornerRadius = new CornerRadius(0);
                RootBorder.Margin = new Thickness(0);
                RootBorder.Effect = null;
            }
            else
            {
                _titleMaximized = false;
                WindowState = WindowState.Normal;
                Width = _restoreWidth;
                Height = _restoreHeight;
                Left = (SystemParameters.PrimaryScreenWidth - _restoreWidth) / 2;
                Top = (SystemParameters.PrimaryScreenHeight - _restoreHeight) / 2;
                RootBorder.CornerRadius = new CornerRadius(10);
                RootBorder.Margin = new Thickness(0);
                RootBorder.Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 2,
                    Opacity = 0.25,
                    Color = Colors.Black
                };
            }
        }

        private void TitlebarClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ==================================================================
        // 导航切换
        // ==================================================================

        private async void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_isClosed) return;
            var tag = ((RadioButton)sender).Tag as string;
            OverviewPage.Visibility = tag == "Overview" ? Visibility.Visible : Visibility.Collapsed;
            AppsPage.Visibility = tag == "Apps" ? Visibility.Visible : Visibility.Collapsed;
            HotkeysPage.Visibility = tag == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
            ThemePage.Visibility = tag == "Theme" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            ExperimentalPage.Visibility = tag == "Experimental" ? Visibility.Visible : Visibility.Collapsed;
            AutomationPage.Visibility = tag == "Automation" ? Visibility.Visible : Visibility.Collapsed;

            if (tag == "Overview") await RefreshOverviewAsync();
            else if (tag == "Apps") await LoadAppsAsync();
            else if (tag == "Hotkeys") BuildHotkeyList();
            else if (tag == "Theme") LoadTheme();
            else if (tag == "Settings") LoadSettings();
            else if (tag == "Experimental") LoadExperimentalSettings();
            else if (tag == "Automation") ShowAutomationPage();
            else StopAutoRefresh();
        }

        // ==================================================================
        // 设备加载（自定义名称 + 筛选 + 名称编辑）
        // ==================================================================

        private async Task LoadDevicesAsync()
        {
            // 输出/输入两组并行枚举（各自含设备列表 + 默认设备），互不依赖
            var outTask = Task.Run(() =>
            {
                var outputs = AudioService.GetDevices(EDataFlow.eRender);
                string? defOut = AudioService.GetDefaultDeviceId(EDataFlow.eRender);
                foreach (var d in outputs) d.IsDefault = string.Equals(d.Id, defOut, StringComparison.OrdinalIgnoreCase);
                return outputs;
            });
            var inTask = Task.Run(() =>
            {
                var inputs = AudioService.GetDevices(EDataFlow.eCapture);
                string? defIn = AudioService.GetDefaultDeviceId(EDataFlow.eCapture);
                foreach (var d in inputs) d.IsDefault = string.Equals(d.Id, defIn, StringComparison.OrdinalIgnoreCase);
                return inputs;
            });

            var outputs = await outTask;
            _outputs = outputs;

            // 输入设备（麦克风）：仅实验模式 + 麦克风选项使用（快速切换当前应用麦克风设备/保留设置）
            try
            {
                _inputs = await inTask;
            }
            catch { _inputs = new List<AudioDeviceInfo>(); }

            ReloadDeviceDisplay();
        }

        /// <summary>应用自定义设备名称到副本（不改动原始设备）。</summary>
        private List<AudioDeviceInfo> DisplayDevices(IEnumerable<AudioDeviceInfo> devs)
        {
            return devs.Select(d =>
            {
                string? custom = _config.DeviceNames.TryGetValue(d.Id, out var n) ? n : null;
                return string.IsNullOrWhiteSpace(custom)
                    ? d
                    : new AudioDeviceInfo { Id = d.Id, DisplayName = custom, Flow = d.Flow, IsDefault = d.IsDefault };
            }).ToList();
        }



        /// <summary>应用自定义应用名称到副本（不改动原始应用项）。</summary>
        private IEnumerable<AudioAppInfo> DisplayApps(IEnumerable<AudioAppInfo> apps)
        {
            return apps.Select(a =>
            {
                if (a.ProcessName != null
                    && _config.AppNames.TryGetValue(a.ProcessName, out var n)
                    && !string.IsNullOrWhiteSpace(n))
                    return new AudioAppInfo { ProcessId = a.ProcessId, DisplayName = n, ProcessName = a.ProcessName, HasActiveSession = a.HasActiveSession };
                return a;
            }).ToList();
        }        private IEnumerable<AudioDeviceInfo> VisibleOutputs =>
            _outputs.Where(d => !_config.HiddenOutputDevices.Contains(d.Id));

        private IEnumerable<AudioDeviceInfo> VisibleInputs =>
            _inputs.Where(d => !_config.HiddenInputDevices.Contains(d.Id));

        /// <summary>刷新所有设备下拉 / 快速按钮 / 名称编辑列表的显示。</summary>
        private void ReloadDeviceDisplay()
        {
            RefreshDeviceDisplays();
            BuildDeviceNameLists();
        }

        /// <summary>仅刷新设备显示（下拉/快捷按钮），不重建名称编辑输入框。
        /// 名称输入时调用它而不是 ReloadDeviceDisplay：每敲一个字就重建 TextBox 会
        /// 导致输入框失焦、中文输入法组合中断（用户需要每字重新点一下的 bug 根因）。</summary>
        private void RefreshDeviceDisplays()
        {
            _outputDisplay = PanelDevices.WithSystemDefault(DisplayDevices(_outputs), EDataFlow.eRender, _config);
            OverviewOutputCombo.ItemsSource = null;
            OverviewOutputCombo.ItemsSource = _outputDisplay;
            AppsOutputCombo.ItemsSource = null;
            AppsOutputCombo.ItemsSource = _outputDisplay;
        }

        // ==================================================================
        // 概览页：默认应用解析 + 应用切换器
        // ==================================================================

        private async Task RefreshOverviewAsync(bool force = false)
        {
            var apps = await Task.Run(() => AudioService.GetApps(force));
            var items = apps.Select(AppItem.From).ToList();
            AppItem.LoadIconsAsync(items);
            _suppressAppCombo = true;
            OverviewAppCombo.ItemsSource = null;
            OverviewAppCombo.ItemsSource = items;
            _suppressAppCombo = false;

            var cur = CurrentAppService.Current;
            var target = cur != null
                ? apps.FirstOrDefault(a => a.ProcessId == cur.ProcessId)
                : null;
            target ??= await ResolveDefaultAppAsync(apps);
            if (target != null)
            {
                _suppressAppCombo = true;
                OverviewAppCombo.SelectedItem = items.FirstOrDefault(i => i.ProcessId == (int)target.ProcessId);
                _suppressAppCombo = false;
            }
            await SetOverviewAppAsync(target);
        }

        /// <summary>当前默认应用：统一走 CurrentAppService（last/fixed/前台音频/上次操作）。
        /// 每次重新读配置，避免面板或设置页改动后本地缓存过期导致选不中。</summary>
        private Task<AudioAppInfo?> ResolveDefaultAppAsync(List<AudioAppInfo> apps)
        {
            var cfg = ConfigService.Load();
            return Task.FromResult(CurrentAppService.Resolve(apps, cfg));
        }

        private async void OverviewAppCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAppCombo) return;
            if (OverviewAppCombo.SelectedItem is AppItem item)
            {
                MarkLastUsed(item.Info);
                await SetOverviewAppAsync(item.Info);
            }
        }

        /// <summary>记录"上次操作的应用"（进程名），供 last 模式使用。</summary>
        private void MarkLastUsed(AudioAppInfo app)
        {
            var name = app.ProcessName;
            if (string.IsNullOrWhiteSpace(name)) return;
            // 与上次记录相同则跳过写盘（切设备/切应用高频点击时减少无谓的配置全量保存）
            if (string.Equals(_config.LastUsedAppName, name, StringComparison.OrdinalIgnoreCase)) return;
            _config.LastUsedAppName = name;
            ConfigService.Save(_config);
        }

        private async Task SetOverviewAppAsync(AudioAppInfo? app)
        {
            _overviewApp = app;
            CurrentAppService.Current = app; // 共享给快捷键/面板/托盘
            if (app == null)
            {
                OverviewAppPidText.Text = "";
            }
            else
            {
                OverviewAppPidText.Text = $"PID {app.ProcessId} · {app.ProcessName ?? "?"}";
            }
            await RefreshOverviewDevicesVolumeAsync();
        }

        // ==================================================================
        // 概览：设备 + 音量 + 应用
        // ==================================================================

        private async Task RefreshOverviewDevicesVolumeAsync()
        {
            var outs = PanelDevices.WithSystemDefault(DisplayDevices(VisibleOutputs), EDataFlow.eRender, _config);
            // 输入下拉框显示全部设备（与输出下拉一致，不受「保留的设备」筛选影响）
            _inputDisplay = PanelDevices.WithSystemDefault(DisplayDevices(_inputs), EDataFlow.eCapture, _config);
            OverviewInputCombo.ItemsSource = null;
            OverviewInputCombo.ItemsSource = _inputDisplay;

            if (_overviewApp == null)
            {
                bool globalMicMuted = await Task.Run(() => GlobalMicMuteService.IsMuted());
                // 全局麦克风状态与应用无关，始终刷新按钮文案
                OverviewMicMuteButton.Content = L10n.T(globalMicMuted ? "Ov.MicUnmute" : "Ov.MuteMic");
                OverviewOutputCurrentText.Text = "";
                RenderQuickButtons(OverviewOutputQuickPanel, outs);
                OverviewInputCurrentText.Text = "";
                OverviewInputCurrentText.Tag = null;
                RenderInputQuickButtons();
                SetVolumeUi(null);
                return;
            }

            var pid = (int)_overviewApp.ProcessId;
            // 5 路音频查询并行（持久化端点×2、会话音量/静音、全局麦克风），互不依赖
            var tMic = Task.Run(() => GlobalMicMuteService.IsMuted());
            var tOut = Task.Run(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender));
            var tIn = Task.Run(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eCapture));
            var tVol = Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
            var tMuted = Task.Run(() => SessionVolumeService.IsMuted(pid));
            await Task.WhenAll(tMic, tOut, tIn, tVol, tMuted);
            bool micMuted = tMic.Result;
            var outId = tOut.Result;
            var inId = tIn.Result;
            int vol = tVol.Result;
            bool muted = tMuted.Result;

            // 全局麦克风状态与应用无关，始终刷新按钮文案
            OverviewMicMuteButton.Content = L10n.T(micMuted ? "Ov.MicUnmute" : "Ov.MuteMic");

            string? outShort = outId == null ? null : AudioPolicyConfig.UnpackDeviceId(outId);

            OverviewOutputCurrentText.Text = DescribeCurrent(_outputDisplay, outShort, true);
            RenderQuickButtons(OverviewOutputQuickPanel, outs);

            // 选中项必须从下拉实际绑定的显示列表（含自定义名称）中查找，
            // 否则改过名称的设备会多出一个"默认名"的幽灵项
            var selectedOut = outShort == null
                ? (_outputDisplay.FirstOrDefault(d => AudioService.IsSystemDefault(d.Id)) ?? _outputDisplay.FirstOrDefault(d => d.IsDefault) ?? _outputDisplay.FirstOrDefault())
                : _outputDisplay.FirstOrDefault(d => string.Equals(d.Id, outShort, StringComparison.OrdinalIgnoreCase));
            _suppressDevCombo = true;
            OverviewOutputCombo.SelectedItem = selectedOut;
            _suppressDevCombo = false;

            // 输入设备（麦克风）：与输出一致读取持久化端点并刷新下拉/快捷按钮
            string? inShort = inId == null ? null : AudioPolicyConfig.UnpackDeviceId(inId);
            OverviewInputCurrentText.Text = DescribeCurrent(_inputDisplay, inShort, false);
            OverviewInputCurrentText.Tag = inShort;
            RenderInputQuickButtons();
            var selectedIn = inShort == null
                ? (_inputDisplay.FirstOrDefault(d => AudioService.IsSystemDefault(d.Id)) ?? _inputDisplay.FirstOrDefault(d => d.IsDefault) ?? _inputDisplay.FirstOrDefault())
                : _inputDisplay.FirstOrDefault(d => string.Equals(d.Id, inShort, StringComparison.OrdinalIgnoreCase));
            _suppressDevCombo = true;
            OverviewInputCombo.SelectedItem = selectedIn;
            _suppressDevCombo = false;

            SetVolumeUi(vol >= 0 ? vol : null);
            OverviewMuteButton.Content = L10n.T(muted ? "Ov.Unmute" : "Ov.Mute");
        }

        private void SetVolumeUi(int? percent)
        {
            _suppressVolume = true;
            bool ready = percent != null;
            _overviewVolumeReady = ready;
            OverviewVolumeSlider.IsEnabled = ready;
            OverviewMinus.IsEnabled = ready;
            OverviewPlus.IsEnabled = ready;
            OverviewMuteButton.IsEnabled = ready;
            if (!ready || percent == null)
            {
                OverviewVolumeSlider.Value = 0;
                OverviewVolumeText.Text = "—";
            }
            else
            {
                OverviewVolumeSlider.Value = percent.Value;
                OverviewVolumeText.Text = $"{percent.Value}%";
            }
            _suppressVolume = false;
        }

        private void RenderQuickButtons(ItemsControl panel, List<AudioDeviceInfo> devices)
        {
            panel.Items.Clear();
            foreach (var dev in devices)
            {
                var btn = new Button
                {
                    Content = ShortName(dev.DisplayName),
                    Tag = dev,
                    ToolTip = dev.DisplayName
                };
                btn.SetResourceReference(StyleProperty, "QuickDevButton");
                btn.Click += (_, _) => OnQuickSwitch(dev);
                panel.Items.Add(btn);
            }
            HighlightQuickActive(panel);
        }

        private void HighlightQuickActive(ItemsControl panel)
        {
            string? activeId = OverviewOutputCurrentText.Tag as string;
            foreach (var item in panel.Items)
            {
                if (item is not Button btn || btn.Tag is not AudioDeviceInfo dev) continue;
                bool active = activeId != null &&
                              string.Equals(dev.Id, activeId, StringComparison.OrdinalIgnoreCase);
                btn.SetResourceReference(StyleProperty, active ? "QuickDevButtonActive" : "QuickDevButton");
            }
        }

        private async void OnQuickSwitch(AudioDeviceInfo dev)
        {
            var app = _overviewApp;
            if (app == null)
            {
                OverviewStatusText.Text = L10n.T("Ov.NoAudio");
                return;
            }
            MarkLastUsed(app);

            var pid = (int)app.ProcessId;
            var (ok, msg) = await Apply(pid, EDataFlow.eRender, dev.Id);
            OverviewStatusText.Text = ok
                ? string.Format(L10n.T("Ov.SwitchOk"), "🔊 " + dev.DisplayName, AppDisplayName.Get(app))
                : $"✗ {msg}";
            await RefreshOverviewDevicesVolumeAsync();
        }

        // ==================================================================
        // 概览：输入设备（麦克风）—— 与输出完全对称（实验模式 + 麦克风选项开启时使用）
        // ==================================================================

        private void RenderInputQuickButtons()
        {
            OverviewInputQuickPanel.Items.Clear();
            // 快捷按钮按「保留的设备」显示（与输出侧一致）；下拉框才显示全部设备
            var visible = PanelDevices.WithSystemDefault(DisplayDevices(VisibleInputs), EDataFlow.eCapture, _config);
            foreach (var dev in visible)
            {
                var btn = new Button
                {
                    Content = ShortName(dev.DisplayName),
                    Tag = dev,
                    ToolTip = dev.DisplayName
                };
                btn.SetResourceReference(StyleProperty, "QuickDevButton");
                btn.Click += (_, _) => OnInputQuickSwitch(dev);
                OverviewInputQuickPanel.Items.Add(btn);
            }
            HighlightInputQuickActive();
        }

        private void HighlightInputQuickActive()
        {
            string? activeId = OverviewInputCurrentText.Tag as string;
            foreach (var item in OverviewInputQuickPanel.Items)
            {
                if (item is not Button btn || btn.Tag is not AudioDeviceInfo dev) continue;
                bool active = activeId != null &&
                              string.Equals(dev.Id, activeId, StringComparison.OrdinalIgnoreCase);
                btn.SetResourceReference(StyleProperty, active ? "QuickDevButtonActive" : "QuickDevButton");
            }
        }

        private async void OnInputQuickSwitch(AudioDeviceInfo dev)
        {
            var app = _overviewApp;
            if (app == null)
            {
                OverviewStatusText.Text = L10n.T("Ov.NoAudio");
                return;
            }
            MarkLastUsed(app);

            var pid = (int)app.ProcessId;
            var (ok, msg) = await Apply(pid, EDataFlow.eCapture, dev.Id);
            OverviewStatusText.Text = ok
                ? string.Format(L10n.T("Ov.SwitchOk"), "🎤 " + dev.DisplayName, AppDisplayName.Get(app))
                : $"✗ {msg}";
            await RefreshOverviewDevicesVolumeAsync();
        }

        private async void OverviewInputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDevCombo) return;
            await ApplyOverviewInputComboSelectionAsync();
        }

        /// <summary>概览输入下拉选择即生效。</summary>
        private async Task ApplyOverviewInputComboSelectionAsync()
        {
            var app = _overviewApp;
            if (app == null) return;
            if (OverviewInputCombo.SelectedItem is not AudioDeviceInfo dev) return;

            var pid = (int)app.ProcessId;
            var (ok, msg) = await Apply(pid, EDataFlow.eCapture, dev.Id);
            OverviewStatusText.Text = ok
                ? string.Format(L10n.T("Ov.SwitchOk"), "🎤 " + dev.DisplayName, AppDisplayName.Get(app))
                : $"✗ {msg}";
            await RefreshOverviewDevicesVolumeAsync();
        }

        private async void OverviewVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressVolume || _overviewApp == null || !_overviewVolumeReady) return;
            int pct = (int)Math.Round(e.NewValue);
            OverviewVolumeText.Text = $"{pct}%";
            var pid = (int)_overviewApp.ProcessId;

            bool ok = await Task.Run(() => SessionVolumeService.SetVolumePercent(pid, pct));
            int actual = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
            if (actual >= 0)
            {
                _suppressVolume = true;
                OverviewVolumeSlider.Value = actual;
                OverviewVolumeText.Text = $"{actual}%";
                _suppressVolume = false;
            }
            OverviewStatusText.Text = ok && actual >= 0
                ? string.Format(L10n.T("Ov.VolOk"), actual)
                : L10n.T("Ov.VolFail");
        }

        private void OverviewMinus_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressVolume) return;
            OverviewVolumeSlider.Value = Math.Max(0, OverviewVolumeSlider.Value - 5);
        }

        private void OverviewPlus_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressVolume) return;
            OverviewVolumeSlider.Value = Math.Min(100, OverviewVolumeSlider.Value + 5);
        }

        private async void OverviewMuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_overviewApp == null || !_overviewVolumeReady) return;
            MarkLastUsed(_overviewApp);
            bool muted = await Task.Run(() => SessionVolumeService.ToggleMute((int)_overviewApp.ProcessId));
            OverviewMuteButton.Content = L10n.T(muted ? "Ov.Unmute" : "Ov.Mute");
            OverviewStatusText.Text = L10n.T(muted ? "Ov.Muted" : "Ov.Unmuted");
        }

        private async void OverviewMicMuteButton_Click(object sender, RoutedEventArgs e)
        {
            // 全局麦克风静音：静音/取消静音系统所有录音设备（与当前应用无关）
            bool muted = await Task.Run(() => GlobalMicMuteService.Toggle());
            OverviewMicMuteButton.Content = L10n.T(muted ? "Ov.MicUnmute" : "Ov.MuteMic");
            OverviewStatusText.Text = L10n.T(muted ? "Ov.MicMuted" : "Ov.MicUnmuted");
        }

        private async void OverviewOutputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDevCombo) return;
            await ApplyOverviewComboSelectionAsync();
        }

        /// <summary>概览下拉选择即生效：直接持久化当前应用的输出设备（无需再点"应用设置"）。</summary>
        private async Task ApplyOverviewComboSelectionAsync()
        {
            var app = _overviewApp;
            if (app == null) return;
            var dev = OverviewOutputCombo.SelectedItem as AudioDeviceInfo;
            if (dev == null) return;
            MarkLastUsed(app);

            var pid = (int)app.ProcessId;
            var (ok, msg) = await Apply(pid, EDataFlow.eRender, dev.Id);
            OverviewStatusText.Text = ok
                ? string.Format(L10n.T("Ov.SwitchOk"), "🔊 " + dev.DisplayName, AppDisplayName.Get(app))
                : $"✗ {msg}";
            await RefreshOverviewDevicesVolumeAsync();
        }

        private static Task<(bool, string)> Apply(int pid, EDataFlow flow, string deviceId)
        {
            return Task.Run(() =>
            {
                var r = AudioService.ApplyEndpoint(pid, flow, deviceId);
                return (r.Success, r.Message);
            });
        }

        // ==================================================================
        // 应用页
        // ==================================================================

        private async void AppsRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadAppsAsync();
        }

        /// <summary>应用页：输入即自动保存应用自定义名称（按进程名），留空=恢复默认。
        /// 不重建列表/输入框，避免中文输入法组合中断；仅更新标题与列表项显示。</summary>
        private void AppsRenameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressRename || _appsSelected == null) return;
            var pn = _appsSelected.ProcessName;
            if (string.IsNullOrWhiteSpace(pn)) return;
            var name = AppsRenameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) _config.AppNames.Remove(pn);
            else _config.AppNames[pn] = name;
            ConfigService.Save(_config);

            AppsDetailTitle.Text = AppDisplayName.Get(_appsSelected);
            foreach (var it in _appItems)
                if (string.Equals(it.ProcessName, pn, StringComparison.OrdinalIgnoreCase))
                    it.RefreshName();
        }

        private async Task LoadAppsAsync()
        {
            var apps = await Task.Run(() => AudioService.GetApps());
            _appItems = apps.Select(AppItem.From).ToList();
            AppItem.LoadIconsAsync(_appItems);
            foreach (var item in _appItems) item.RefreshAutoSwitchState();
            AppsListBox.ItemsSource = null;
            AppsListBox.ItemsSource = _appItems;
        }

        private async void AppsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _appsSelected = (AppsListBox.SelectedItem as AppItem)?.Info;
            if (_appsSelected == null)
            {
                AppsDetailTitle.Text = L10n.T("Apps.SelectHint");
                AppsDetailPid.Text = "";
                _suppressRename = true;
                AppsRenameBox.Text = "";
                _suppressRename = false;
                AppsRenameBox.IsEnabled = false;
                SetAppsVolumeUi(null);
                return;
            }

            var pid = (int)_appsSelected.ProcessId;
            AppsDetailTitle.Text = AppDisplayName.Get(_appsSelected);
            AppsDetailPid.Text = $"PID {pid} · {_appsSelected.ProcessName ?? "?"}";
            AppsRenameBox.IsEnabled = true;
            _suppressRename = true;
            AppsRenameBox.Text = _config.AppNames.TryGetValue(_appsSelected.ProcessName ?? "", out var rn) ? rn ?? "" : "";
            _suppressRename = false;

            var outId = await Task.Run(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender));

            string? outShort = outId == null ? null : AudioPolicyConfig.UnpackDeviceId(outId);

            AppsOutputCombo.SelectedItem = outShort == null
                                           ? (_outputDisplay.FirstOrDefault(d => AudioService.IsSystemDefault(d.Id)) ?? _outputDisplay.FirstOrDefault(d => d.IsDefault) ?? _outputDisplay.FirstOrDefault())
                                           : _outputDisplay.FirstOrDefault(d => string.Equals(d.Id, outShort, StringComparison.OrdinalIgnoreCase))
                                             ?? _outputDisplay.FirstOrDefault(d => d.IsDefault) ?? _outputDisplay.FirstOrDefault();
            int vol = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
            bool muted = await Task.Run(() => SessionVolumeService.IsMuted(pid));
            SetAppsVolumeUi(vol >= 0 ? vol : null);
            ApplyAppsMuteVisual(muted);
            UpdateAppsDisableAutoButton();
            UpdateAppsShowInPanelButton();
        }

        /// <summary>切换选中应用的"禁用自动切换"状态（不影响手动选择，只影响自动检测/跟随）。</summary>
        private async void AppsDisableAutoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_appsSelected == null) return;
            var name = _appsSelected.ProcessName;
            if (string.IsNullOrWhiteSpace(name)) return;

            var cfg = ConfigService.Load();
            if (cfg.DisabledAutoSwitchApps.Contains(name)) cfg.DisabledAutoSwitchApps.Remove(name);
            else cfg.DisabledAutoSwitchApps.Add(name);
            ConfigService.Save(cfg);

            foreach (var item in _appItems)
                if (string.Equals(item.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                    item.RefreshAutoSwitchState();
            UpdateAppsDisableAutoButton();
            UpdateAppsShowInPanelButton();
            await Task.CompletedTask;
        }

        /// <summary>应用页三按钮：启用/禁用状态文字变强调色，不切换文案。</summary>
        private void ApplyAppsMuteVisual(bool muted)
        {
            AppsMuteButton.Content = L10n.T("Apps.Mute");
            if (muted) AppsMuteButton.Foreground = (Brush)FindResource("Theme.Accent");
            else AppsMuteButton.ClearValue(Button.ForegroundProperty);
        }

        private void ApplyAppsDisableAutoVisual(bool disabled)
        {
            AppsDisableAutoButton.Content = L10n.T("Apps.DisableAuto");
            if (disabled) AppsDisableAutoButton.Foreground = (Brush)FindResource("Theme.Accent");
            else AppsDisableAutoButton.ClearValue(Button.ForegroundProperty);
        }

        private void ApplyAppsShowInPanelVisual(bool hidden)
        {
            AppsShowInPanelButton.Content = L10n.T("Apps.ShowInPanel");
            if (hidden) AppsShowInPanelButton.Foreground = (Brush)FindResource("Theme.Accent");
            else AppsShowInPanelButton.ClearValue(Button.ForegroundProperty);
        }

        /// <summary>按当前选中应用是否禁用自动切换刷新按钮文字。</summary>
        private void UpdateAppsDisableAutoButton()
        {
            bool disabled = _appsSelected != null && !string.IsNullOrWhiteSpace(_appsSelected.ProcessName)
                && ConfigService.Load().DisabledAutoSwitchApps.Contains(_appsSelected.ProcessName);
            ApplyAppsDisableAutoVisual(disabled);
        }


        /// <summary>切换选中应用的"在快速面板显示"状态（隐藏的应用不出现在简洁/经典面板列表/下拉）。</summary>
        private async void AppsShowInPanelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_appsSelected == null) return;
            var name = _appsSelected.ProcessName;
            if (string.IsNullOrWhiteSpace(name)) return;
            var cfg = ConfigService.Load();
            var hidden = cfg.HiddenPanelApps;
            bool isHidden = hidden.Any(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
            if (isHidden) hidden.RemoveAll(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
            else hidden.Add(name);
            ConfigService.Save(cfg);
            UpdateAppsShowInPanelButton();
            await Task.CompletedTask;
        }

        /// <summary>按当前选中应用是否已在快速面板隐藏刷新按钮文字。</summary>
        private void UpdateAppsShowInPanelButton()
        {
            bool hidden = _appsSelected != null && !string.IsNullOrWhiteSpace(_appsSelected.ProcessName)
                && ConfigService.Load().HiddenPanelApps.Any(h => string.Equals(h, _appsSelected.ProcessName, StringComparison.OrdinalIgnoreCase));
            ApplyAppsShowInPanelVisual(hidden);
        }
        private void SetAppsVolumeUi(int? percent)
        {
            _suppressVolume = true;
            bool ready = percent != null;
            _appsVolumeReady = ready;
            AppsVolumeSlider.IsEnabled = ready;
            AppsMinus.IsEnabled = ready;
            AppsPlus.IsEnabled = ready;
            AppsMuteButton.IsEnabled = ready;
            if (!ready || percent == null)
            {
                AppsVolumeSlider.Value = 0;
                AppsVolumeText.Text = "—";
            }
            else
            {
                AppsVolumeSlider.Value = percent.Value;
                AppsVolumeText.Text = $"{percent.Value}%";
            }
            _suppressVolume = false;
        }

        private async void AppsVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressVolume || _appsSelected == null || !_appsVolumeReady) return;
            int pct = (int)Math.Round(e.NewValue);
            AppsVolumeText.Text = $"{pct}%";
            var pid = (int)_appsSelected.ProcessId;

            bool ok = await Task.Run(() => SessionVolumeService.SetVolumePercent(pid, pct));
            int actual = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
            if (actual >= 0)
            {
                _suppressVolume = true;
                AppsVolumeSlider.Value = actual;
                AppsVolumeText.Text = $"{actual}%";
                _suppressVolume = false;
            }
            AppsStatusText.Text = ok && actual >= 0
                ? string.Format(L10n.T("Ov.VolOk"), actual)
                : L10n.T("Ov.VolFail");
        }

        private void AppsMinus_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressVolume) return;
            AppsVolumeSlider.Value = Math.Max(0, AppsVolumeSlider.Value - 5);
        }

        private void AppsPlus_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressVolume) return;
            AppsVolumeSlider.Value = Math.Min(100, AppsVolumeSlider.Value + 5);
        }

        private async void AppsMuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_appsSelected == null || !_appsVolumeReady) return;
            bool muted = await Task.Run(() => SessionVolumeService.ToggleMute((int)_appsSelected.ProcessId));
            ApplyAppsMuteVisual(muted);
        }

        private async void AppsOutputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDevCombo) return;
            await ApplyAppsComboSelectionAsync();
        }

        /// <summary>应用页输出下拉选择即生效（无需再点"应用设置"）。</summary>
        private async Task ApplyAppsComboSelectionAsync()
        {
            var app = _appsSelected;
            if (app == null) return;
            var dev = AppsOutputCombo.SelectedItem as AudioDeviceInfo;
            if (dev == null) return;

            var pid = (int)app.ProcessId;
            var (ok, msg) = await Apply(pid, EDataFlow.eRender, dev.Id);
            if (ok)
            {
                AppsStatusText.Text = string.Format(L10n.T("Ov.SwitchOk"), "🔊 " + dev.DisplayName, AppDisplayName.Get(app));
                await RefreshAppsSelectionAsync();
            }
            else
            {
                AppsStatusText.Text = $"✗ {msg}";
            }
        }

        /// <summary>应用页输入下拉选择即生效。</summary>
        private async void AppsInputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDevCombo) return;
            await ApplyAppsInputComboSelectionAsync();
        }

        /// <summary>应用页输入（麦克风）设备切换，与输出一致即选即生效。</summary>
        private async Task ApplyAppsInputComboSelectionAsync()
        {
            var app = _appsSelected;
            if (app == null) return;
            if (AppsInputCombo.SelectedItem is not AudioDeviceInfo dev) return;

            var pid = (int)app.ProcessId;
            var (ok, msg) = await Apply(pid, EDataFlow.eCapture, dev.Id);
            if (ok)
            {
                AppsStatusText.Text = string.Format(L10n.T("Ov.SwitchOk"), "🎤 " + dev.DisplayName, AppDisplayName.Get(app));
                await RefreshAppsSelectionAsync();
            }
            else
            {
                AppsStatusText.Text = $"✗ {msg}";
            }
        }

        /// <summary>重新读取当前选中应用的状态（下拉选中项 / 音量 / 静音）。</summary>
        private async Task RefreshAppsSelectionAsync()
        {
            var app = _appsSelected;
            if (app == null) return;
            var pid = (int)app.ProcessId;
            var outId = await Task.Run(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender));
            string? outShort = outId == null ? null : AudioPolicyConfig.UnpackDeviceId(outId);
            _suppressDevCombo = true;
            AppsOutputCombo.SelectedItem = outShort == null
                                           ? (_outputDisplay.FirstOrDefault(d => AudioService.IsSystemDefault(d.Id)) ?? _outputDisplay.FirstOrDefault(d => d.IsDefault) ?? _outputDisplay.FirstOrDefault())
                                           : _outputDisplay.FirstOrDefault(d => string.Equals(d.Id, outShort, StringComparison.OrdinalIgnoreCase))
                                             ?? _outputDisplay.FirstOrDefault(d => d.IsDefault) ?? _outputDisplay.FirstOrDefault();
            _suppressDevCombo = false;
            // 输入设备（麦克风）：与输出一致
            var inId = await Task.Run(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eCapture));
            string? inShort = inId == null ? null : AudioPolicyConfig.UnpackDeviceId(inId);
            _suppressDevCombo = true;
            AppsInputCombo.ItemsSource = null;
            AppsInputCombo.ItemsSource = _inputDisplay;
            AppsInputCombo.SelectedItem = inShort == null
                                          ? (_inputDisplay.FirstOrDefault(d => AudioService.IsSystemDefault(d.Id)) ?? _inputDisplay.FirstOrDefault(d => d.IsDefault) ?? _inputDisplay.FirstOrDefault())
                                          : _inputDisplay.FirstOrDefault(d => string.Equals(d.Id, inShort, StringComparison.OrdinalIgnoreCase))
                                            ?? _inputDisplay.FirstOrDefault(d => d.IsDefault) ?? _inputDisplay.FirstOrDefault();
            _suppressDevCombo = false;
            int vol = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
            bool muted = await Task.Run(() => SessionVolumeService.IsMuted(pid));
            SetAppsVolumeUi(vol >= 0 ? vol : null);
            ApplyAppsMuteVisual(muted);
        }

        // ==================================================================
        // 设置页：保留设备筛选
        // ==================================================================

        private void BuildDeviceFilter()
        {
            OutputFilterList.Items.Clear();
            // 系统默认输出虚拟项：与正常设备一样参与"保留设备"勾选（默认显示，可隐藏）
            AddFilterCheckBox(OutputFilterList, new AudioDeviceInfo { Id = AudioService.SystemDefaultDeviceId, DisplayName = L10n.T("Dev.SystemDefaultOut"), Flow = EDataFlow.eRender }, _config.HiddenOutputDevices, DeviceFilterChanged);
            foreach (var dev in _outputs)
            {
                var cb = new CheckBox
                {
                    Content = dev.DisplayLabel,
                    IsChecked = !_config.HiddenOutputDevices.Contains(dev.Id),
                    Tag = dev,
                    FontSize = 13,
                    Margin = new Thickness(0, 4, 6, 4)
                };
                cb.Checked += DeviceFilterChanged;
                cb.Unchecked += DeviceFilterChanged;
                OutputFilterList.Items.Add(cb);
            }

            // 输入设备（麦克风）保留：仅实验模式 + 麦克风选项开启时显示并生效
            InputFilterList.Items.Clear();
            // 系统默认输入虚拟项（麦克风）：与正常设备一样可勾选隐藏
            AddFilterCheckBox(InputFilterList, new AudioDeviceInfo { Id = AudioService.SystemDefaultInputDeviceId, DisplayName = L10n.T("Dev.SystemDefaultIn"), Flow = EDataFlow.eCapture }, _config.HiddenInputDevices, InputFilterChanged);
            foreach (var dev in _inputs)
            {
                var cb = new CheckBox
                {
                    Content = dev.DisplayLabel,
                    IsChecked = !_config.HiddenInputDevices.Contains(dev.Id),
                    Tag = dev,
                    FontSize = 13,
                    Margin = new Thickness(0, 4, 6, 4)
                };
                cb.Checked += InputFilterChanged;
                cb.Unchecked += InputFilterChanged;
                InputFilterList.Items.Add(cb);
            }

        }



        /// <summary>构造一个"保留设备"勾选框（供输出/输入与系统默认虚拟项共用）。</summary>
        private static void AddFilterCheckBox(ItemsControl list, AudioDeviceInfo dev, System.Collections.Generic.List<string> hidden, RoutedEventHandler handler)
        {
            var cb = new CheckBox
            {
                Content = dev.DisplayName,
                IsChecked = !hidden.Contains(dev.Id),
                Tag = dev,
                FontSize = 13,
                Margin = new Thickness(0, 4, 6, 4)
            };
            cb.Checked += handler;
            cb.Unchecked += handler;
            list.Items.Add(cb);
        }

        private void DeviceFilterChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb || cb.Tag is not AudioDeviceInfo dev) return;
            if (cb.IsChecked == false) { if (!_config.HiddenOutputDevices.Contains(dev.Id)) _config.HiddenOutputDevices.Add(dev.Id); }
            else _config.HiddenOutputDevices.Remove(dev.Id);
            ConfigService.Save(_config);
            UpdateSelectAllLabels();
            // 快速切换界面立即生效（刷新设备显示）
            if (!_suppressFilter) _ = RefreshOverviewDevicesVolumeAsync();
        }

        /// <summary>输入设备（麦克风）保留勾选：实验模式 + 麦克风选项开启时可用。</summary>
        private void InputFilterChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb || cb.Tag is not AudioDeviceInfo dev) return;
            if (cb.IsChecked == false) { if (!_config.HiddenInputDevices.Contains(dev.Id)) _config.HiddenInputDevices.Add(dev.Id); }
            else _config.HiddenInputDevices.Remove(dev.Id);
            ConfigService.Save(_config);
            UpdateSelectAllLabels();
        }

        /// <summary>输出设备全选/全不选。</summary>
        private void SelectAllOutput_Click(object sender, RoutedEventArgs e)
        {
            ToggleSelectAll(OutputFilterList.Items.OfType<CheckBox>().ToList());
        }

        /// <summary>输入设备（麦克风）全选/全不选。</summary>
        private void SelectAllInput_Click(object sender, RoutedEventArgs e)
        {
            ToggleSelectAll(InputFilterList.Items.OfType<CheckBox>().ToList());
        }

        /// <summary>一键全选/全不选：该组当前若全部勾选则全部取消，否则全部勾选。</summary>
        private void ToggleSelectAll(List<CheckBox> boxes)
        {
            if (boxes.Count == 0) return;
            bool allChecked = boxes.All(cb => cb.IsChecked == true);
            bool? target = allChecked ? false : true;
            _suppressFilter = true;
            try { foreach (var cb in boxes) cb.IsChecked = target; }
            finally { _suppressFilter = false; }
            ConfigService.Save(_config);
            UpdateSelectAllLabels();
            _ = RefreshOverviewDevicesVolumeAsync();
        }

        private void UpdateSelectAllLabels()
        {
            UpdateSelectAllLabel(SelectAllOutputButton, OutputFilterList.Items.OfType<CheckBox>().ToList());
            UpdateSelectAllLabel(SelectAllInputButton, InputFilterList.Items.OfType<CheckBox>().ToList());
        }

        private static void UpdateSelectAllLabel(System.Windows.Controls.Button? btn, List<CheckBox> boxes)
        {
            if (btn == null) return;
            bool allChecked = boxes.Count > 0 && boxes.All(cb => cb.IsChecked == true);
            btn.Content = L10n.T(allChecked ? "St.ClearAll" : "St.SelectAll");
        }

        // ==================================================================
        // 设置页：设备名称编辑
        // ==================================================================

        private void BuildDeviceNameLists()
        {
            OutputNameList.Items.Clear();
            // 系统默认输出虚拟项：可在"设备名称"中改名（存 DeviceNames[@@SYSTEM_DEFAULT@@]）
            OutputNameList.Items.Add(MakeNameRow(new AudioDeviceInfo { Id = AudioService.SystemDefaultDeviceId, DisplayName = L10n.T("Dev.SystemDefaultOut"), Flow = EDataFlow.eRender }));
            foreach (var dev in _outputs) OutputNameList.Items.Add(MakeNameRow(dev));
            InputNameList.Items.Clear();
            // 系统默认输入虚拟项（麦克风）
            InputNameList.Items.Add(MakeNameRow(new AudioDeviceInfo { Id = AudioService.SystemDefaultInputDeviceId, DisplayName = L10n.T("Dev.SystemDefaultIn"), Flow = EDataFlow.eCapture }));
            foreach (var dev in _inputs) InputNameList.Items.Add(MakeNameRow(dev));
        }
        private UIElement MakeNameRow(AudioDeviceInfo dev)
        {
            var tb = new TextBox
            {
                Text = _config.DeviceNames.TryGetValue(dev.Id, out var n) ? n ?? "" : "",
                Tag = dev.Id,
                Width = 230,
                FontSize = 12.5,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 4, 8, 4)
            };
            tb.TextChanged += DeviceName_TextChanged;
            var label = new TextBlock
            {
                Text = dev.DisplayName ?? "(未知设备)",
                FontSize = 12,
                Foreground = (Brush)FindResource("Theme.TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var panel = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            panel.Children.Add(tb);
            panel.Children.Add(label);
            return panel;
        }

        private void DeviceName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox tb || tb.Tag is not string id) return;
            var name = tb.Text.Trim();
            if (string.IsNullOrEmpty(name)) _config.DeviceNames.Remove(id);
            else _config.DeviceNames[id] = name;
            ConfigService.Save(_config);
            // 只刷新显示，不重建名称输入框（重建会导致失焦、中文输入法组合中断）
            RefreshDeviceDisplays();
            _ = RefreshOverviewDevicesVolumeAsync();
        }

        // ==================================================================
        // 设置页：默认应用 / 语言 / 主题 / 启动选项
        // ==================================================================

        private void LoadSettings()
        {
            _suppressSettings = true;
            try
            {
                // 保留的设备
                BuildDeviceFilter();
                UpdateSelectAllLabels();

                // 默认应用
                SetRadioByTag(DefaultAppRecent, DefaultAppLast, DefaultAppFixed, _config.DefaultAppMode);
                var apps = new List<AppItem>();
                try
                {
                    apps = AudioService.GetApps().Select(AppItem.From).ToList();
            AppItem.LoadIconsAsync(apps);
                }
                catch { }
                _suppressAppCombo = true;
                FixedAppCombo.ItemsSource = null;
                FixedAppCombo.ItemsSource = apps;
                FixedAppCombo.SelectedItem = apps.FirstOrDefault(a =>
                    string.Equals(a.ProcessName, _config.FixedAppName, StringComparison.OrdinalIgnoreCase));
                _suppressAppCombo = false;
                FixedAppCombo.IsEnabled = _config.DefaultAppMode == "fixed";

                // 语言（下拉，重启生效）
                LangCombo.ItemsSource = L10n.SupportedLanguages.Select(x => x.NativeName).ToList();
                int li = Array.FindIndex(L10n.SupportedLanguages,
                    x => string.Equals(x.Code, _config.Language, StringComparison.OrdinalIgnoreCase));
                LangCombo.SelectedIndex = li < 0 ? 0 : li;
                // 自定义语言管理区（方案3）
                RefreshCustomLangList();

                // 启动选项
                // 启动选项（商店版与正常版自启配置分开存储）
                SettingsAutoStart.IsChecked = IsPackaged() ? _config.AutoStartStore : _config.AutoStart;

                // 商店版不显示「清理自启项」（StartupTask 由系统托管，无残留概念），折叠区一并隐藏
                if (CleanAutoStartBtn != null)
                {
                    bool packaged = IsPackaged();
                    CleanAutoStartBtn.Visibility = packaged ? Visibility.Collapsed : Visibility.Visible;
                    SettingsMoreToggle.Visibility = packaged ? Visibility.Collapsed : Visibility.Visible;
                    if (packaged) SettingsMorePanel.Visibility = Visibility.Collapsed;
                }
                SettingsStartMinimized.IsChecked = _config.StartMinimized;
                SettingsShowPanelOnStart.IsChecked = _config.StartPanelOnStart;
                SettingsPanelChangeSysDef.IsChecked = _config.PanelChangeSystemDefault;
                // 快速面板样式：经典面板 / 简洁面板（默认简洁）
                QuickPanelStyleCombo.ItemsSource = new[] { L10n.T("St.PanelClassic"), L10n.T("St.PanelModern") };
                QuickPanelStyleCombo.SelectedIndex = _config.QuickPanelStyle == "classic" ? 0 : 1;
                // 简洁面板更改系统默认设备：仅"简洁面板"样式时显示
                PanelChangeSysDefSection.Visibility = _config.QuickPanelStyle == "classic"
                    ? Visibility.Collapsed : Visibility.Visible;
                // 简洁面板高度设置：经典面板不显示
                PanelHeightSection.Visibility = _config.QuickPanelStyle == "classic"
                    ? Visibility.Collapsed : Visibility.Visible;
                // 简洁面板固定高度（px，400–800，默认 500）
                PanelHeightSlider.Value = Math.Clamp(_config.QuickPanelHeight, 350, 800);
                PanelHeightValue.Text = Math.Clamp(_config.QuickPanelHeight, 350, 800) + " px";
            VolumeStepBox.Text = Math.Clamp(_config.VolumeStep, 1, 20).ToString();
            SettingsTrayWheelEverywhere.IsChecked = _config.TrayWheelEverywhere;

                ExpCollapseCheck.IsChecked = _config.CollapseDeviceSections;

                // 实验模式（隐藏）：解锁后显示开关；开启实验模式后导航显示"实验设置"
                bool expUnlocked = _config.ExperimentalUnlocked;
                bool expOn = expUnlocked && _config.ExperimentalMode;
                SettingsExperimentalMode.Visibility = expUnlocked ? Visibility.Visible : Visibility.Collapsed;
                ExperimentalModeHint.Visibility = expUnlocked ? Visibility.Visible : Visibility.Collapsed;
                SettingsExperimentalMode.IsChecked = expOn;
                NavExperimental.Visibility = expOn ? Visibility.Visible : Visibility.Collapsed;

                // 麦克风选项（设置页常驻，不依赖实验模式）：开启后概览/应用/设置显示麦克风 UI
                ExpMicOptionCheck.IsChecked = _config.ExperimentalMic;
                ExpMicPanelCheck.IsChecked = _config.MicInPanel;
                ExpMicPanelCheck.Visibility = _config.ExperimentalMic ? Visibility.Visible : Visibility.Collapsed;
                ApplyExpMicUi(_config.ExperimentalMic);

                // 折叠设置页设备区块（设置页开关，默认开启，不依赖实验模式）：开启后"保留的设备/设备名称"默认收起，可点击标题按钮展开
                ApplyCollapseUi();
            }
            finally
            {
                _suppressSettings = false;
            }
        }

        private static void SetRadioByTag(RadioButton? a, RadioButton? b, RadioButton? c, string tag)
        {
            bool Match(RadioButton? r) => r != null && string.Equals(r.Tag as string, tag, StringComparison.OrdinalIgnoreCase);
            if (a != null) a.IsChecked = Match(a);
            if (b != null) b.IsChecked = Match(b);
            if (c != null) c.IsChecked = Match(c);
        }

        private void DefaultAppMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings || sender is not RadioButton rb) return;
            _config.DefaultAppMode = rb.Tag as string ?? "recent";
            FixedAppCombo.IsEnabled = _config.DefaultAppMode == "fixed";
            ConfigService.Save(_config);
        }

        private void FixedAppCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings || _suppressAppCombo) return;
            _config.FixedAppName = (FixedAppCombo.SelectedItem as AppItem)?.ProcessName ?? "";
            ConfigService.Save(_config);
        }

        private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings || LangCombo.SelectedIndex < 0) return;
            if (LangCombo.SelectedIndex >= L10n.SupportedLanguages.Length) return;
            var code = L10n.SupportedLanguages[LangCombo.SelectedIndex].Code;
            if (string.Equals(code, _config.Language, StringComparison.OrdinalIgnoreCase)) return;
            _config.Language = code;
            ConfigService.Save(_config);
            // 即时生效（无需重启）：失效缓存 + 切语言 + 全量绑定刷新 + 重建托盘菜单 + 重刷代码动态文本
            L10n.Instance.ApplyImportedLanguage(code);
            ((App)Application.Current).RebuildTrayMenu();
            RefreshLangCombo(code);
            RefreshDynamicTexts();
            ((App)Application.Current).ShowOsd(L10n.T("St.Language"), L10n.T("St.LangApplied"));
        }

        /// <summary>语言切换后刷新代码动态文本（XAML 绑定已由 PropertyChanged 自动刷新；
        /// 概览/应用页按钮与设置页动态项由代码设置，需按当前状态重刷）。</summary>
        private async void RefreshDynamicTexts()
        {
            try
            {
                // 概览：设备/音量/按钮全量重刷（异步读取实际状态）
                await RefreshOverviewDevicesVolumeAsync();
                // 应用页：重读选中应用静音状态刷新按钮
                if (_appsSelected != null)
                {
                    var pid = (int)_appsSelected.ProcessId;
                    bool muted = await Task.Run(() => SessionVolumeService.IsMuted(pid));
                    ApplyAppsMuteVisual(muted);
                }
                UpdateAppsDisableAutoButton();
                UpdateAppsShowInPanelButton();
                // 设置页动态文本
                QuickPanelStyleCombo.ItemsSource = new[] { L10n.T("St.PanelClassic"), L10n.T("St.PanelModern") };
                UpdateSelectAllLabels();
                RefreshCustomLangList();
            }
            catch { }
        }

        private void LoadTheme()
        {
            _suppressSettings = true;
            try
            {
                SetRadioByTag(ThemeSystem, ThemeLight, ThemeDark, _config.ThemeMode);
                OpacitySlider.Value = Math.Clamp(_config.BackgroundOpacity, 0, 100);
                OpacityText.Text = $"{_config.BackgroundOpacity}%";

                string accent = _config.Accent ?? "blue";
                bool isCustom = accent.StartsWith("#", StringComparison.OrdinalIgnoreCase);
                AccentCustom.IsChecked = isCustom;
                if (!isCustom) SetRadioByTag(AccentBlue, AccentGreen, AccentPurple, accent);
                SyncRgbUi(accent);

                // 快速面板高度（简洁面板固定高度，350–800，默认 350）：
                // 该设置位于主题页，必须在 LoadTheme 初始化，否则会显示 XAML 硬编码初值；经典面板不显示
                PanelHeightSection.Visibility = _config.QuickPanelStyle == "classic"
                    ? Visibility.Collapsed : Visibility.Visible;
                PanelHeightSlider.Value = Math.Clamp(_config.QuickPanelHeight, 350, 800);
                PanelHeightValue.Text = Math.Clamp(_config.QuickPanelHeight, 350, 800) + " px";
            }
            finally
            {
                _suppressSettings = false;
            }
        }

        /// <summary>把 accent（预设名或 #RRGGBB）同步到 R/G/B 滑块 + 预览色块 + 十六进制文本。</summary>
        private void SyncRgbUi(string accent)
        {
            var color = accent.StartsWith("#", StringComparison.OrdinalIgnoreCase) && accent.Length == 7
                ? (Color)System.Windows.Media.ColorConverter.ConvertFromString(accent)
                : accent switch
                {
                    "green" => Color.FromRgb(0x22, 0xC5, 0x5E),
                    "purple" => Color.FromRgb(0xEC, 0x48, 0x99), // 粉色 #EC4899（与 ThemeService 一致）
                    _ => Color.FromRgb(0x2F, 0x80, 0xED)
                };
            RText.Text = color.R.ToString();
            GText.Text = color.G.ToString();
            BText.Text = color.B.ToString();
            RSlider.Value = color.R;
            GSlider.Value = color.G;
            BSlider.Value = color.B;
            RgbHex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            RgbPreview.Background = new SolidColorBrush(color);
        }

        private static string RgbToHex(int r, int g, int b) => $"#{r & 0xFF:X2}{g & 0xFF:X2}{b & 0xFF:X2}";

        private void Theme_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            var mode = (new[] { ThemeSystem, ThemeLight, ThemeDark })
                .FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "system";
            string accent;
            if (AccentCustom.IsChecked == true)
            {
                accent = RgbToHex((int)Math.Round(RSlider.Value), (int)Math.Round(GSlider.Value), (int)Math.Round(BSlider.Value));
            }
            else
            {
                accent = (new[] { AccentBlue, AccentGreen, AccentPurple })
                    .FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "blue";
                SyncRgbUi(accent);
            }
            _config.ThemeMode = mode;
            _config.Accent = accent;
            ConfigService.Save(_config);
            ThemeService.Apply(mode, accent);
            // 主题背景色变化后，透明度要基于新背景色重新生成
            ThemeService.ApplyBackgroundOpacity(_config.BackgroundOpacity);
            // 强调色变化后刷新应用列表"禁用自动切换"状态点的反色
            if (_appItems != null)
                foreach (var item in _appItems) item.RefreshAutoSwitchState();
        }

        private void Rgb_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _suppressSettings) return;
            int r = (int)Math.Round(RSlider.Value);
            int g = (int)Math.Round(GSlider.Value);
            int b = (int)Math.Round(BSlider.Value);
            RText.Text = r.ToString();
            GText.Text = g.ToString();
            BText.Text = b.ToString();
            var hex = RgbToHex(r, g, b);
            RgbHex.Text = hex;
            RgbPreview.Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            if (AccentCustom.IsChecked == true)
            {
                _config.Accent = hex;
                ConfigService.Save(_config);
                ThemeService.Apply(_config.ThemeMode, hex);
                ThemeService.ApplyBackgroundOpacity(_config.BackgroundOpacity);
            }
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _suppressSettings) return;
            int v = (int)Math.Round(e.NewValue);
            OpacityText.Text = $"{v}%";
            _config.BackgroundOpacity = v;
            ConfigService.Save(_config);
            ThemeService.ApplyBackgroundOpacity(v);
        }

        // ==================================================================
        // 滚轮调音量：悬停在音量区（滑块/±键）时滚动滚轮
        // ==================================================================

        private void OverviewVolume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_suppressVolume || _overviewApp == null || !_overviewVolumeReady) return;
            int step = Math.Clamp(ConfigService.Load().VolumeStep, 1, 20);
            int pct = (int)Math.Round(OverviewVolumeSlider.Value) + (e.Delta > 0 ? step : -step);
            OverviewVolumeSlider.Value = Math.Clamp(pct, 0, 100);
            e.Handled = true;
        }

        private void AppsVolume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_suppressVolume || !_appsVolumeReady || _appsSelected == null) return;
            int step = Math.Clamp(ConfigService.Load().VolumeStep, 1, 20);
            int pct = (int)Math.Round(AppsVolumeSlider.Value) + (e.Delta > 0 ? step : -step);
            AppsVolumeSlider.Value = Math.Clamp(pct, 0, 100);
            e.Handled = true;
        }

        private void SettingsStartMinimized_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            _config.StartMinimized = SettingsStartMinimized.IsChecked == true;
            ConfigService.Save(_config);
        }

        private void SettingsShowPanelOnStart_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            _config.StartPanelOnStart = SettingsShowPanelOnStart.IsChecked == true;
            ConfigService.Save(_config);
        }

        private void VolumeStepBox_LostFocus(object sender, RoutedEventArgs e) => SaveVolumeStep();

        private void VolumeStepBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            SaveVolumeStep();
            Keyboard.ClearFocus();
        }

        private void SaveVolumeStep()
        {
            if (_suppressSettings) return;
            if (!int.TryParse(VolumeStepBox.Text.Trim(), out int v)) { VolumeStepBox.Text = "4"; v = 4; }
            int step = Math.Clamp(v, 1, 20);
            if (step != v) VolumeStepBox.Text = step.ToString();
            if (_config.VolumeStep == step) return;
            _config.VolumeStep = step;
            ConfigService.Save(_config);
        }

        /// <summary>托盘滚轮调音量区域开关：true=整个托盘通知区响应（默认）；false=仅音跃托盘图标上响应。</summary>
        private void SettingsTrayWheelEverywhere_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressSettings) return;
            _config.TrayWheelEverywhere = SettingsTrayWheelEverywhere.IsChecked == true;
            ConfigService.Save(_config);
        }
        private void QuickPanelStyleCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings || QuickPanelStyleCombo.SelectedIndex < 0) return;
            _config.QuickPanelStyle = QuickPanelStyleCombo.SelectedIndex == 0 ? "classic" : "modern";
            ConfigService.Save(_config);
            // 简洁面板更改系统默认设备选项仅简洁面板样式显示
            PanelChangeSysDefSection.Visibility = _config.QuickPanelStyle == "classic"
                ? Visibility.Collapsed : Visibility.Visible;
            // 简洁面板高度设置仅简洁面板样式显示
            PanelHeightSection.Visibility = _config.QuickPanelStyle == "classic"
                ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>简洁面板更改系统默认设备开关（默认关）：开启后简洁面板下拉框切换设备 = 更改系统默认输出设备。</summary>
        private void SettingsPanelChangeSysDef_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            _config.PanelChangeSystemDefault = SettingsPanelChangeSysDef.IsChecked == true;
            ConfigService.Save(_config);
        }

        /// <summary>检测当前是否运行在 MSIX 包中（非包环境调用 Package.Current 会抛异常）。</summary>
        private static bool IsPackaged()
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>开机自启：MSIX 环境用 StartupTask API，非 MSIX（绿色版）写注册表 Run 键。</summary>
        private async void SettingsAutoStart_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            bool on = SettingsAutoStart.IsChecked == true;
            if (IsPackaged()) _config.AutoStartStore = on; else _config.AutoStart = on;
            ConfigService.Save(_config);

            if (IsPackaged())
            {
                // MSIX：注册表写入会被沙箱重定向，必须用 StartupTask API
                try
                {
                    var task = await Windows.ApplicationModel.StartupTask.GetAsync("SonicRouteStartup");
                    if (on)
                        await task.RequestEnableAsync();
                    else
                        task.Disable();
                }
                catch
                {
                    // StartupTask 调用失败不打断界面
                }
            }
            else
            {
                // 绿色版：写 HKCU\...\Run
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    if (key == null) return;
                    if (on)
                    {
                        var exe = Environment.ProcessPath;
                        if (!string.IsNullOrWhiteSpace(exe))
                            key.SetValue("SonicRoute", $"\"{exe}\"");
                    }
                    else
                    {
                        key.DeleteValue("SonicRoute", throwOnMissingValue: false);
                    }
                }
                catch
                {
                    // 注册表写入失败不打断界面
                }
            }
        }

        /// <summary>「更多选项」展开/收起统一动画：淡入淡出 + 轻微上滑/下滑（展开 130ms，收起 100ms）。</summary>
        private static void AnimatePanelExpand(UIElement panel, bool expand)
        {
            if (panel == null) return;
            if (expand)
            {
                // 取消可能挂起的收起动画
                panel.BeginAnimation(UIElement.OpacityProperty, null);
                panel.RenderTransform = null;
                panel.Visibility = Visibility.Visible;
                panel.Opacity = 0;
                var t = new TranslateTransform(0, -8);
                panel.RenderTransform = t;
                panel.BeginAnimation(UIElement.OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(130)))
                    {
                        EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                        {
                            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                        }
                    });
                t.BeginAnimation(TranslateTransform.YProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(130)))
                    {
                        EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                        {
                            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                        }
                    });
            }
            else
            {
                var t = new TranslateTransform(0, 0);
                panel.RenderTransform = t;
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(100)))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
                    }
                };
                fade.Completed += (s, e2) =>
                {
                    panel.BeginAnimation(UIElement.OpacityProperty, null);
                    panel.RenderTransform = null;
                    panel.Opacity = 1;
                    panel.Visibility = Visibility.Collapsed;
                };
                panel.BeginAnimation(UIElement.OpacityProperty, fade);
                t.BeginAnimation(TranslateTransform.YProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(-8, new Duration(TimeSpan.FromMilliseconds(100)))
                    {
                        EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                        {
                            EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
                        }
                    });
            }
        }

        /// <summary>语言卡片「显示更多选项」折叠：展开/收起语言文件管理（导出/导入/打开/还原）。</summary>
        private void LangMoreToggle_Click(object sender, RoutedEventArgs e)
        {
            AnimatePanelExpand(LangMorePanel, LangMoreToggle.IsChecked == true);
        }

        /// <summary>设置页「显示更多选项」折叠：展开/收起清理自启项与麦克风子选项。</summary>
        private void SettingsMoreToggle_Click(object sender, RoutedEventArgs e)
        {
            AnimatePanelExpand(SettingsMorePanel, SettingsMoreToggle.IsChecked == true);
        }

        /// <summary>麦克风子选项「显示更多选项」折叠：展开/收起「在快捷面板显示麦克风」。</summary>
        private void MicMoreToggle_Click(object sender, RoutedEventArgs e)
        {
            AnimatePanelExpand(MicMorePanel, MicMoreToggle.IsChecked == true);
        }
        /// <summary>清理开机自启项（方案四B）：绿色版删 Run 键，商店版禁用 StartupTask；同步配置与 UI。</summary>
        private void CleanAutoStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (IsPackaged())
                {
                    var task = Windows.ApplicationModel.StartupTask.GetAsync("SonicRouteStartup").GetAwaiter().GetResult();
                    task.Disable();
                }
                else
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    key?.DeleteValue("SonicRoute", throwOnMissingValue: false);
                }

                // 同步配置与 UI（清理即视为关闭自启）
                if (IsPackaged() ? _config.AutoStartStore : _config.AutoStart)
                {
                    if (IsPackaged()) _config.AutoStartStore = false; else _config.AutoStart = false;

                    ConfigService.Save(_config);
                    _suppressSettings = true;
                    SettingsAutoStart.IsChecked = false;
                    _suppressSettings = false;
                }
                ((App)Application.Current).ShowOsd(L10n.T("St.AutoStartTitle"), L10n.T("St.CleanAutoStartDone"));
            }
            catch
            {
                ((App)Application.Current).ShowOsd(L10n.T("St.AutoStartTitle"), L10n.T("St.CleanAutoStartFail"));
            }
        }
        // ==================================================================
        // 实验模式（隐藏功能）
        // 解锁：设置页底部点击"困困困"（作者名）5 次 → 持久化 ExperimentalUnlocked。
        // 之后"开机自启"栏下方出现"实验模式"开关；开启（重启生效）后出现"麦克风选项"；
        // 开启麦克风选项（重启生效）后：设置页"保留的设备"显示输入设备区、快捷键页显示"切换当前应用麦克风设备"。
        // 更新日志规范：实验模式内容不写入 GitHub 更新日志，仅记录于本地规范 README。
        // ==================================================================

        private int _authorClicks;

        private void AboutAuthorText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_config.ExperimentalUnlocked) return; // 已解锁
            _authorClicks++;
            if (_authorClicks >= 5)
            {
                _authorClicks = 0;
                _config.ExperimentalUnlocked = true;
                ConfigService.Save(_config);
                SettingsExperimentalMode.Visibility = Visibility.Visible;
                ExperimentalModeHint.Visibility = Visibility.Visible;
                ShowToast(L10n.T("St.EggDone"));
            }
            else
            {
                string[] eggs = { L10n.T("St.Egg1"), L10n.T("St.Egg2"), L10n.T("St.Egg3"), L10n.T("St.Egg4") };
                ShowToast(_authorClicks <= eggs.Length ? eggs[_authorClicks - 1] : $"还需点击 {5 - _authorClicks} 次");
            }
        }

        private void SettingsExperimentalMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            _config.ExperimentalMode = SettingsExperimentalMode.IsChecked == true;
            ConfigService.Save(_config);
            // 导航"实验设置"入口立即显示/隐藏（功能本身重启生效）
            NavExperimental.Visibility = _config.ExperimentalMode ? Visibility.Visible : Visibility.Collapsed;
            ShowToast(L10n.T("St.ExpModeNeedRestart"));
        }

        /// <summary>按麦克风选项开关状态统一控制各界面麦克风（输入）UI 的可见性。
        /// 概览 / 应用 / 设置保留区 / 设备名称区 与实验设置页子选项联动。</summary>
        private void ApplyExpMicUi(bool expMic)
        {
            OverviewInputCard.Visibility = expMic ? Visibility.Visible : Visibility.Collapsed;
            AppsInputLabel.Visibility = expMic ? Visibility.Visible : Visibility.Collapsed;
            AppsInputCombo.Visibility = expMic ? Visibility.Visible : Visibility.Collapsed;
            InputFilterHeader.Visibility = expMic ? Visibility.Visible : Visibility.Collapsed;
            InputFilterList.Visibility = expMic ? Visibility.Visible : Visibility.Collapsed;
            InputNameHeader.Visibility = expMic ? Visibility.Visible : Visibility.Collapsed;
            InputNameList.Visibility = expMic ? Visibility.Visible : Visibility.Collapsed;
            // 麦克风子选项折叠按钮：仅麦克风选项开启时显示
            if (ExpMicPanelCheck != null)
                ExpMicPanelCheck.Visibility = expMic ? Visibility.Visible : Visibility.Collapsed;
            if (MicMoreToggle != null)
                MicMoreToggle.Visibility = expMic ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>实验设置页：加载麦克风选项 / 快捷面板显示 / 折叠 的当前配置。</summary>
        private void LoadExperimentalSettings()
        {
            _suppressSettings = true;
            try
            {
                bool expOn = _config.ExperimentalMode;
                ApplyExpMicUi(_config.ExperimentalMic);

            }
            finally
            {
                _suppressSettings = false;
            }
        }


        private void ExpMicOption_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            _config.ExperimentalMic = ExpMicOptionCheck.IsChecked == true;
            ConfigService.Save(_config);
            ApplyExpMicUi(_config.ExperimentalMic);
            ExpMicPanelCheck.Visibility = _config.ExperimentalMic ? Visibility.Visible : Visibility.Collapsed;
            ShowToast(L10n.T("St.ExpMicNeedRestart"));
        }

        private void ExpMicPanel_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            _config.MicInPanel = ExpMicPanelCheck.IsChecked == true;
            ConfigService.Save(_config);
            ShowToast(L10n.T("St.ExpMicPanelNeedRestart"));
        }

        private void ExpCollapse_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            _config.CollapseDeviceSections = ExpCollapseCheck.IsChecked == true;
            ConfigService.Save(_config);
            ApplyCollapseUi();
        }




        /// <summary>实验设置 - 一键还原全部应用输出/输入默认设备：
        /// 覆盖系统里所有应用（所有运行进程 + 曾设置过/有音频会话的应用），清除持久化路由跟随系统默认。</summary>
        private async void ExpResetAll_Click(object sender, RoutedEventArgs e)
        {
            ExpResetAllButton.IsEnabled = false;
            try
            {
                var (okOut, okIn, total) = await Task.Run(() => AudioService.ResetAllPersistedEndpoints());
                if (total == 0) ShowToast(L10n.T("Exp.ResetAllNone"));
                else if (okOut == total && okIn == total) ShowToast(string.Format(L10n.T("Exp.ResetAllDone"), total));
                else ShowToast(string.Format(L10n.T("Exp.ResetAllFail"), okOut, total, okIn, total));
            }
            finally { ExpResetAllButton.IsEnabled = true; }
        }

        /// <summary>实验设置 - 一键清理配置文件：删除 config.json 恢复全部默认并自动重启应用。</summary>
        private void ExpClearConfig_Click(object sender, RoutedEventArgs e)
        {
            SonicRoute.Core.ConfigService.ResetToDefault();
            RestartApp();
        }

        /// <summary>实验设置 - 导出配置文件：把当前全部设置保存为 json 副本。</summary>
        private void ExpExportConfig_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = L10n.T("Exp.ExportConfig"),
                Filter = "JSON (*.json)|*.json",
                FileName = "SonicRoute-config.json",
                DefaultExt = ".json",
            };
            if (dlg.ShowDialog() != true) return;
            if (SonicRoute.Core.ConfigService.ExportTo(dlg.FileName))
                ShowToast(L10n.T("Exp.ExportDone"));
            else
                ShowToast(L10n.T("Exp.TransferFail"));
        }

        /// <summary>实验设置 - 导入配置文件：从备份文件恢复全部设置并自动重启应用。</summary>
        private void ExpImportConfig_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L10n.T("Exp.ImportConfig"),
                Filter = "JSON (*.json)|*.json",
                DefaultExt = ".json",
            };
            if (dlg.ShowDialog() != true) return;
            ImportConfigFile(dlg.FileName);
        }

        /// <summary>导入配置文件（点击对话框 / 拖入文件共用）：恢复全部设置并自动重启应用。</summary>
        private void ImportConfigFile(string file)
        {
            if (SonicRoute.Core.ConfigService.ImportFrom(file))
            {
                ShowToast(L10n.T("Exp.ImportDone"));
                RestartApp();
            }
            else
            {
                ShowToast(L10n.T("Exp.TransferFail"));
            }
        }

        /// <summary>导入配置文件按钮：拖入 json 文件直接导入。</summary>
        private void ExpImportConfigButton_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void ExpImportConfigButton_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
            {
                var f = files.FirstOrDefault(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                if (f != null) ImportConfigFile(f);
            }
        }

        /// <summary>实验设置 - 导出语言：把内置 9 语言文件写入用户选择的文件夹（可编辑后导入）。</summary>
        private void ExpExportLang_Click(object sender, RoutedEventArgs e)
        {
            using var fbd = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = L10n.T("Exp.ExportLang"),
                UseDescriptionForTitle = true,
            };
            if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            if (L10n.ExportBuiltinLanguages(fbd.SelectedPath))
            {
                ShowToast(L10n.T("Exp.LangExportDone"));
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = fbd.SelectedPath,
                        UseShellExecute = true,
                    });
                }
                catch { }
            }
            else
            {
                ShowToast(L10n.T("Exp.LangImportFail"));
            }
        }

        /// <summary>实验设置 - 导入语言：支持一次选择多个 json 批量导入（迁移/恢复场景）。
        /// 方案1+4：导入成功即时生效（重建缓存 + 自动切到最后成功导入的语言 + 刷新界面 + 重建托盘菜单），无需重启。
        /// 方案2：单文件导入显示键覆盖率，低于 80% 提示缺失键将显示中文。</summary>
        private void ExpImportLang_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L10n.T("Exp.ImportLang"),
                Filter = "JSON (*.json)|*.json",
                DefaultExt = ".json",
                Multiselect = true,
            };
            if (dlg.ShowDialog() != true) return;
            ImportLangFiles(dlg.FileNames);
        }

        /// <summary>导入语言文件（点击对话框 / 拖入文件共用）：批量导入 + 即时生效 + 覆盖率提示。</summary>
        private void ImportLangFiles(string[] files)
        {
            if (files == null || files.Length == 0) return;
            int ok = 0, fail = 0;
            string? lastCode = null; int lastCov = 100;
            foreach (var f in files)
            {
                var r = L10n.ImportLanguageFile(f);
                if (r.Ok) { ok++; lastCode = r.Code; lastCov = r.CoveragePct; }
                else fail++;
            }
            if (ok == 0) { ShowToast(L10n.T("Exp.LangImportFail")); return; }

            // 即时生效：失效缓存 + 切到最后成功导入的语言 + 全量绑定刷新 + 重建托盘菜单
            L10n.Instance.ApplyImportedLanguage(lastCode!);
            ((App)Application.Current).RebuildTrayMenu();
            RefreshLangCombo(lastCode!);
            RefreshCustomLangList();

            if (files.Length == 1)
            {
                // 单文件：显示键覆盖率；低于 80% 额外警告缺失键回退中文
                if (lastCov >= 80) ShowToast(L10n.T("Exp.LangImportDone"));
                else ShowToast(string.Format(L10n.T("Exp.LangCoverageLow"), lastCov));
            }
            else if (fail > 0)
            {
                ShowToast(string.Format(L10n.T("Exp.LangImportPartial"), ok, fail));
            }
            else
            {
                ShowToast(L10n.T("Exp.LangImportDone"));
            }
        }

        /// <summary>导入语言按钮：拖入 json 文件直接导入。</summary>
        private void ExpImportLangButton_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void ExpImportLangButton_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
            {
                var jsons = files.Where(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (jsons.Length > 0) ImportLangFiles(jsons);
            }
        }

        /// <summary>重建语言下拉列表并选中指定语言（导入/删除/重命名后刷新，抑制保存）。</summary>
        private void RefreshLangCombo(string selectCode)
        {
            LangCombo.ItemsSource = L10n.SupportedLanguages.Select(x => x.NativeName).ToList();
            int idx = Array.FindIndex(L10n.SupportedLanguages,
                x => string.Equals(x.Code, selectCode, StringComparison.OrdinalIgnoreCase));
            _suppressSettings = true;
            LangCombo.SelectedIndex = idx < 0 ? 0 : idx;
            _suppressSettings = false;
        }

        /// <summary>刷新自定义语言管理区：当前使用的语言置顶（强调色圆点标记），其余按显示名排序；
        /// 每行「显示名输入框 + code + 改名 + 移除」。</summary>
        private void RefreshCustomLangList()
        {
            var langs = L10n.ListCustomLanguages()
                .OrderBy(x => x.NativeName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => string.Equals(x.Code, L10n.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                .ToList();
            CustomLangSection.Visibility = langs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var panel = new StackPanel();
            foreach (var (code, native) in langs)
            {
                bool isCurrent = string.Equals(code, L10n.CurrentLanguage, StringComparison.OrdinalIgnoreCase);
                var row = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                var box = new TextBox
                {
                    Text = native, Width = 140, FontSize = 12,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                box.ToolTip = L10n.T("Exp.LangNameHint");
                var codePart = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 10, 0),
                };
                if (isCurrent)
                {
                    codePart.Children.Add(new TextBlock
                    {
                        Text = "●",
                        Foreground = (Brush)FindResource("Theme.Accent"),
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0),
                    });
                }
                var codeText = new TextBlock
                {
                    Text = code, FontSize = 11,
                    Foreground = (Brush)FindResource("Theme.TextSecondary"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                codeText.ToolTip = isCurrent ? L10n.T("Exp.LangCurrent") : code;
                codePart.Children.Add(codeText);
                var rename = new Button
                {
                    Content = L10n.T("Exp.LangSave"), Style = (Style)FindResource("GhostButton"),
                    Height = 26, MinWidth = 64, Padding = new Thickness(8, 0, 8, 0), Tag = code,
                };
                rename.Click += (_, _) =>
                {
                    if (L10n.RenameCustomLanguage(code, box.Text))
                    {
                        RefreshCustomLangList();
                        RefreshLangCombo(code);
                    }
                };
                var remove = new Button
                {
                    Content = L10n.T("Exp.LangDelete"), Style = (Style)FindResource("GhostButton"),
                    Height = 26, MinWidth = 64, Padding = new Thickness(8, 0, 8, 0),
                    Margin = new Thickness(6, 0, 0, 0), Tag = code,
                };
                remove.Click += (_, _) =>
                {
                    if (L10n.DeleteCustomLanguage(code))
                    {
                        RefreshCustomLangList();
                        RefreshLangCombo(L10n.CurrentLanguage);
                    }
                };
                row.Children.Add(box); row.Children.Add(codePart); row.Children.Add(rename); row.Children.Add(remove);
                panel.Children.Add(row);
            }
            CustomLangList.Content = panel;
        }

        /// <summary>实验设置 - 打开语言文件夹（外置语言目录 %LocalAppData%\SonicRoute\Lang，不存在则创建）。</summary>
        private void ExpOpenLangDir_Click(object sender, RoutedEventArgs e)
        {
            if (!L10n.OpenExternalLangDir())
                ShowToast(L10n.T("Exp.LangOpenFail"));
        }

        /// <summary>实验设置 - 还原默认语言：删除外置目录中覆盖内置的同名语言文件（自定义语言保留），重启生效。</summary>
        private void ExpRestoreLang_Click(object sender, RoutedEventArgs e)
        {
            var r = L10n.RestoreBuiltinLanguages();
            if (!r.Ok) { ShowToast(L10n.T("Exp.LangRestoreFail")); return; }
            if (r.Restored == 0) { ShowToast(L10n.T("Exp.LangNoOverride")); return; }
            ShowToast(string.Format(L10n.T("Exp.LangRestored"), r.Restored));
            RestartApp();
        }

        /// <summary>重启应用（供清理配置等需要全量重新初始化的场景使用）。</summary>
        private void RestartApp()
        {
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                          ?? Environment.ProcessPath ?? "SonicRoute.exe";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                });
            }
            catch { }
            System.Windows.Application.Current.Shutdown();
        }
        /// <summary>应用折叠状态：开启后显示折叠按钮并默认收起"保留的设备/设备名称"，关闭则全部展开。</summary>
        private void ApplyCollapseUi()
        {
            bool collapse = _config.CollapseDeviceSections;
            KeepDevicesMoreToggle.Visibility = collapse ? Visibility.Visible : Visibility.Collapsed;
            DeviceNamesMoreToggle.Visibility = collapse ? Visibility.Visible : Visibility.Collapsed;
            if (collapse)
            {
                KeepDevicesBody.Visibility = Visibility.Collapsed;
                DeviceNamesBody.Visibility = Visibility.Collapsed;
                KeepDevicesMoreToggle.IsChecked = false;
                DeviceNamesMoreToggle.IsChecked = false;
            }
            else
            {
                KeepDevicesBody.Visibility = Visibility.Visible;
                DeviceNamesBody.Visibility = Visibility.Visible;
            }
        }

        /// <summary>主题页 - 一键还原 OSD 位置到默认（右上角 + 零偏移 + 清空自定义坐标）。</summary>
        private void OsdReset_Click(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;
            // 若处于调整模式先退出，否则 PreviewOsd 被 _osdAdjustMode 挡住不显示（还原位置不生效的根因）
            if (_osdAdjusting) { _osdAdjusting = false; SetOsdAdjustLabel(L10n.T("Exp.OsdAdjust")); app.CancelOsdAdjust(); }
            _config.OsdPosition = "TR";
            _config.OsdOffsetX = 0;
            _config.OsdOffsetY = 0;
            _config.OsdCustomX = -1;
            _config.OsdCustomY = -1;
            _config.OsdWidth = 240;
            _config.OsdFontScale = 1.0;
            _config.OsdFadeInMs = 100;
            _config.OsdFadeOutMs = 200;
            ConfigService.Save(_config);
            SyncOsdSliders(); // 同步主题页滑条到还原值
            ShowToast(L10n.T("Exp.OsdResetDone"));
            app.PreviewOsd(); // 立即预览还原后的默认位置与尺寸（内部按需重定位到主屏默认位置）
        }

        private bool _osdAdjusting;
        private bool _osdAdjustSubscribed;

        /// <summary>主题页 - 「调整位置」：进入/取消 OSD 拖拽定位模式（拖动松手即保存为自定义坐标）。</summary>
        private void OsdAdjust_Click(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;
            if (!_osdAdjusting)
            {
                SubscribeOsdAdjust();
                _osdAdjusting = true;
                SetOsdAdjustLabel(L10n.T("Exp.OsdAdjustCancel"));
                app.BeginOsdAdjust();
            }
            else
            {
                _osdAdjusting = false;
                SetOsdAdjustLabel(L10n.T("Exp.OsdAdjust"));
                app.CancelOsdAdjust();
            }
        }

        /// <summary>同步主题页的「调整位置」按钮文字。</summary>
        private void SetOsdAdjustLabel(string text)
        {
            if (OsdAdjustBtnTheme != null) OsdAdjustBtnTheme.Content = text;
        }

        private void SubscribeOsdAdjust()
        {
            if (_osdAdjustSubscribed) return;
            _osdAdjustSubscribed = true;
            ((App)Application.Current).OsdAdjustFinished += () =>
            {
                // 拖拽松手已保存：复位主题页按钮状态
                _osdAdjusting = false;
                SetOsdAdjustLabel(L10n.T("Exp.OsdAdjust"));
            };
        }

        private bool _panelPosAdjusting;
        private bool _panelPosAdjustSubscribed;

        /// <summary>主题页 - 「调整位置」：进入/取消快速面板拖拽定位模式（拖动松手即保存为自定义坐标）。</summary>
        private void PanelPosAdjust_Click(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;
            if (!_panelPosAdjusting)
            {
                SubscribePanelPosAdjust();
                _panelPosAdjusting = true;
                SetPanelPosAdjustLabel(L10n.T("Exp.PanelPosAdjustCancel"));
                app.BeginQuickPanelAdjust();
            }
            else
            {
                _panelPosAdjusting = false;
                SetPanelPosAdjustLabel(L10n.T("Exp.PanelPosAdjust"));
                app.CancelQuickPanelAdjust();
            }
        }

        /// <summary>主题页 - 「一键还原」：快速面板恢复任务栏右下角默认位置。</summary>
        /// <summary>主题页「快速面板高度」：简洁面板窗口固定高度（400–800px，默认 500），保存并立即应用（面板打开时）。</summary>
        private void PanelHeightSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _suppressSettings) return;
            int v = Math.Clamp((int)Math.Round(e.NewValue), 350, 800);
            if (PanelHeightValue != null) PanelHeightValue.Text = v + " px";
            _config.QuickPanelHeight = v;
            ConfigService.Save(_config);
            // 简洁面板打开时立即应用固定高度
            if (Application.Current is App app && app.QuickPanelInstance is QuickPanelModernWindow m && m.IsVisible)
                m.ApplyPanelHeightFromConfig();
        }

        private void PanelPosReset_Click(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;
            if (_panelPosAdjusting)
            {
                _panelPosAdjusting = false;
                SetPanelPosAdjustLabel(L10n.T("Exp.PanelPosAdjust"));
                app.CancelQuickPanelAdjust();
            }
            app.ResetQuickPanelPosition();
            ShowToast(L10n.T("Exp.PanelPosResetDone"));
        }

        /// <summary>同步主题页的「调整位置」按钮文字。</summary>
        private void SetPanelPosAdjustLabel(string text)
        {
            if (PanelPosAdjustBtn != null) PanelPosAdjustBtn.Content = text;
        }

        private void SubscribePanelPosAdjust()
        {
            if (_panelPosAdjustSubscribed) return;
            _panelPosAdjustSubscribed = true;
            ((App)Application.Current).QuickPanelAdjustFinished += () =>
            {
                // 拖拽松手已保存（或面板被关闭）：复位主题页按钮状态
                _panelPosAdjusting = false;
                SetPanelPosAdjustLabel(L10n.T("Exp.PanelPosAdjust"));
            };
        }


        /// <summary>主题页 - OSD 宽度滑条：实时调整 OSD 宽度并保存。</summary>
        private void OsdWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncingOsdSliders) return; // 初始化/一键还原同步滑条值时不弹「已保存」OSD
            if (OsdWidthValue == null || !IsLoaded) return;
            int w = (int)Math.Round(OsdWidthSlider.Value);
            OsdWidthValue.Text = w + "px";
            var app = (App)Application.Current;
            if (_config.OsdWidth != w) { _config.OsdWidth = w; ConfigService.Save(_config); }
            app.SetOsdSize(w, _config.OsdFontScale);
        }

        /// <summary>主题页 - OSD 字号倍率滑条：实时调整 OSD 字号并保存。</summary>
        private void OsdFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncingOsdSliders) return; // 初始化/一键还原同步滑条值时不弹「已保存」OSD
            if (OsdFontValue == null || !IsLoaded) return;
            double fs = Math.Round(OsdFontSlider.Value, 2);
            OsdFontValue.Text = (int)Math.Round(fs * 100) + "%";
            var app = (App)Application.Current;
            if (Math.Abs(_config.OsdFontScale - fs) > 0.001) { _config.OsdFontScale = fs; ConfigService.Save(_config); }
            app.SetOsdSize(_config.OsdWidth, fs);
        }

        /// <summary>主题页 - OSD 淡入时长滑条：实时调整淡入动画时长并保存（0 = 禁用淡入直接显示）。</summary>
        private void OsdFadeInSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncingOsdSliders) return; // 初始化/一键还原同步滑条值时不触发保存
            if (OsdFadeInValue == null || !IsLoaded) return;
            int ms = (int)Math.Round(OsdFadeInSlider.Value);
            OsdFadeInValue.Text = ms + "ms";
            if (_config.OsdFadeInMs != ms) { _config.OsdFadeInMs = ms; ConfigService.Save(_config); }
        }

        /// <summary>主题页 - OSD 淡出时长滑条：实时调整淡出动画时长并保存（0 = 禁用淡出直接隐藏）。</summary>
        private void OsdFadeOutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncingOsdSliders) return;
            if (OsdFadeOutValue == null || !IsLoaded) return;
            int ms = (int)Math.Round(OsdFadeOutSlider.Value);
            OsdFadeOutValue.Text = ms + "ms";
            if (_config.OsdFadeOutMs != ms) { _config.OsdFadeOutMs = ms; ConfigService.Save(_config); }
        }

        private bool _syncingOsdSliders;

        /// <summary>同步主题页 OSD 滑条与数值文本（页面加载与一键还原时调用，不触发保存/OSD 提示）。</summary>
        private void SyncOsdSliders()
        {
            if (OsdWidthSlider == null) return;
            _syncingOsdSliders = true;
            OsdWidthSlider.Value = _config.OsdWidth;
            OsdFontSlider.Value = _config.OsdFontScale;
            if (OsdFadeInSlider != null) OsdFadeInSlider.Value = _config.OsdFadeInMs;
            if (OsdFadeOutSlider != null) OsdFadeOutSlider.Value = _config.OsdFadeOutMs;
            _syncingOsdSliders = false;
            OsdWidthValue.Text = _config.OsdWidth + "px";
            OsdFontValue.Text = (int)Math.Round(_config.OsdFontScale * 100) + "%";
            if (OsdFadeInValue != null) OsdFadeInValue.Text = _config.OsdFadeInMs + "ms";
            if (OsdFadeOutValue != null) OsdFadeOutValue.Text = _config.OsdFadeOutMs + "ms";
            if (MicMutePersistCheck != null) MicMutePersistCheck.IsChecked = _config.MicMuteOsdPersistent;
            if (MicMuteTrackInputCheck != null) MicMuteTrackInputCheck.IsChecked = _config.MicMuteOsdTrackInputMuted;
            if (MicMuteMoreToggle != null) ApplyMicMutePersistUi(_config.MicMuteOsdPersistent);
        }

        /// <summary>主题页 - 常驻「更多选项」联动（防呆）：主开关关闭时隐藏更多选项并折叠子选项，交互与麦克风选项一致。</summary>
        private void ApplyMicMutePersistUi(bool on)
        {
            if (MicMuteMoreToggle != null)
                MicMuteMoreToggle.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on)
            {
                if (MicMuteMoreToggle != null) MicMuteMoreToggle.IsChecked = false;
                if (MicMuteMorePanel != null) MicMuteMorePanel.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>折叠/展开设置页"保留的设备"卡片（实验设置-折叠开启时可见）。</summary>
        /// <summary>折叠/展开设置页"保留的设备"卡片（更多选项样式，实验设置-折叠开启时可见）。</summary>
        private void KeepDevicesMoreToggle_Click(object sender, RoutedEventArgs e)
        {
            AnimatePanelExpand(KeepDevicesBody, KeepDevicesMoreToggle.IsChecked == true);
        }

        /// <summary>折叠/展开设置页"设备名称"卡片（更多选项样式，实验设置-折叠开启时可见）。</summary>
        private void DeviceNamesMoreToggle_Click(object sender, RoutedEventArgs e)
        {
            AnimatePanelExpand(DeviceNamesBody, DeviceNamesMoreToggle.IsChecked == true);
        }

        /// <summary>主题页 - 常驻子选项「同时监听默认输入静音」：保存配置并让 OSD 重新评估常驻状态
        /// （开启且当前默认输入静音 → 立即常驻显示；关闭且常驻横幅仅由输入静音维持 → 退出常驻）。</summary>
        private void MicMuteTrackInput_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || MicMuteTrackInputCheck == null) return;
            bool on = MicMuteTrackInputCheck.IsChecked == true;
            if (_config.MicMuteOsdTrackInputMuted != on)
            {
                _config.MicMuteOsdTrackInputMuted = on;
                ConfigService.Save(_config);
            }
            ((App)Application.Current).NotifyMicMuteOsdSettingChanged(_config.MicMuteOsdPersistent);
        }

        /// <summary>主题页 - 常驻子选项「更多选项」折叠展开。</summary>
        private void MicMuteMoreToggle_Click(object sender, RoutedEventArgs e)
        {
            AnimatePanelExpand(MicMuteMorePanel, MicMuteMoreToggle.IsChecked == true);
        }

        /// <summary>主题页 - OSD 调整项（宽度/字号/淡入/淡出）「更多选项」折叠展开。</summary>
        private void OsdMoreToggle_Click(object sender, RoutedEventArgs e)
        {
            if (OsdSlidersPanel == null) return;
            AnimatePanelExpand(OsdSlidersPanel, OsdMoreToggle.IsChecked == true);
        }

        /// <summary>主题页 - 「麦克风静音时 OSD 常驻」开关：保存配置并立即生效
        /// （开启且当前已静音 → 立即常驻显示；关闭且正在常驻 → 退出常驻按普通生命周期隐藏）。</summary>
        private void MicMutePersist_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || MicMutePersistCheck == null) return;
            bool on = MicMutePersistCheck.IsChecked == true;
            if (_config.MicMuteOsdPersistent != on)
            {
                _config.MicMuteOsdPersistent = on;
                ConfigService.Save(_config);
            }
            ApplyMicMutePersistUi(on);
            ((App)Application.Current).NotifyMicMuteOsdSettingChanged(on);
        }

        /// <summary>右上角 OSD 通知（兼容主题/强调色/透明度）。</summary>
        private void ShowToast(string text)
        {
            try { ((App)Application.Current).ShowOsd(L10n.T("St.Settings"), text); }
            catch { /* 通知失败静默 */ }
        }

        /// <summary>打开 B 站链接。</summary>
        private void BiliLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://b23.tv/TDqSAKM",
                    UseShellExecute = true
                });
            }
            catch
            {
                // 打开失败静默
            }
        }

        /// <summary>打开爱发电链接。</summary>
        private void IfdianLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://www.ifdian.net/a/koukou021",
                    UseShellExecute = true
                });
            }
            catch
            {
                // 打开失败静默
            }
        }

        /// <summary>打开 GitHub 链接。</summary>
        private void GithubLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/kunkunkunQoQ/SonicRoute",
                    UseShellExecute = true
                });
            }
            catch
            {
                // 打开失败静默
            }
        }

        private bool _checkingUpdate;
        private static readonly HttpClient _updateHttp = CreateUpdateHttp();

        private static HttpClient CreateUpdateHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("SonicRoute");
            return h;
        }

        /// <summary>检查更新：仅点击触发，不自动检测；商店版与非商店版行为不同。</summary>
        private void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_checkingUpdate) return;
            _checkingUpdate = true;
            try { CheckUpdateRun.Text = L10n.T("St.CheckingUpdate"); }
            catch { /* 忽略 */ }
            _ = CheckUpdateAsync();
        }

        private async Task CheckUpdateAsync()
        {
            string? remoteTag = null;
            try
            {
                using var resp = await _updateHttp.GetAsync("https://api.github.com/repos/kunkunkunQoQ/SonicRoute/releases/latest");
                if (!resp.IsSuccessStatusCode) { FinishCheck(null); return; }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("tag_name", out var tag)) remoteTag = tag.GetString();
            }
            catch
            {
                FinishCheck(null);
                return;
            }
            if (string.IsNullOrWhiteSpace(remoteTag)) { FinishCheck(null); return; }
            var newer = IsNewerVersion(remoteTag);
            if (newer == null) { FinishCheck(null); return; }
            FinishCheck(newer.Value, remoteTag);
        }

        /// <summary>检查结果回 UI 线程弹窗：hasUpdate=null 失败；true 有新版（商店版引导去微软商店，非商店版去 GitHub）；false 已是最新。</summary>
        private void FinishCheck(bool? hasUpdate, string? remoteTag = null)
        {
            _checkingUpdate = false;
            try { CheckUpdateRun.Text = L10n.T("St.CheckUpdate"); }
            catch { /* 忽略 */ }
            var title = L10n.T("St.CheckUpdate");
            if (hasUpdate == null)
            {
                System.Windows.MessageBox.Show(L10n.T("St.UpdateCheckFailed"), title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (hasUpdate.Value)
            {
                var msg = string.Format(L10n.T("St.UpdateAvailable"), remoteTag ?? string.Empty) + "\n" +
                          (IsPackaged() ? L10n.T("St.UpdateOpenStore") : L10n.T("St.UpdateGoDownload"));
                if (System.Windows.MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    var url = IsPackaged()
                        ? "ms-windows-store://pdp/?ProductId=9NQZGRTPM1NT"
                        : "https://github.com/kunkunkunQoQ/SonicRoute/releases/latest";
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch
                    {
                        // 打开失败静默
                    }
                }
                return;
            }
            System.Windows.MessageBox.Show(L10n.T("St.UpdateLatest"), title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static bool? IsNewerVersion(string remoteTag)
        {
            var r = ParseVersion(remoteTag);
            var l = ParseVersion(App.DisplayVersion);
            if (r == null || l == null) return null;
            return r.Value.CompareTo(l.Value) > 0;
        }

        /// <summary>解析 vX.Y[.Z][rN]（r 无数字=1）；返回 (主, 次, 修订, rN)。</summary>
        private static (int Major, int Minor, int Rev, int R)? ParseVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            var m = Regex.Match(s, @"^(\d+)\.(\d+)(?:\.(\d+))?(?:r(\d*))?$");
            if (!m.Success) return null;
            int rev = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            int rn = m.Groups[4].Success ? (m.Groups[4].Length == 0 ? 1 : int.Parse(m.Groups[4].Value)) : 0;
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), rev, rn);
        }

        // ==================================================================
        // 快捷键页
        // ==================================================================

        private void BuildHotkeyList()
        {
            _recordingAction = null;
            HotkeyList.Items.Clear();
            // 实验模式隐藏动作：仅在"实验模式 + 麦克风选项"开启时显示（切换当前应用麦克风设备）
            bool expMicOn = _config.ExperimentalMic;
            var registered = ((App)Application.Current).HotkeyRegistration;
            // 按分组渲染：每组先加分类标题，再渲染动作行
            foreach (var (l10nKey, groupActions) in HotkeyActions.Groups)
            {
                var visible = groupActions
                    .Where(a => (a != HotkeyActions.ActSwitchInput && a != HotkeyActions.ActSwitchAllInput) || expMicOn)
                    .ToArray();
                if (visible.Length == 0) continue;
                var header = new TextBlock
                {
                    Text = L10n.T(l10nKey),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("Theme.Accent"),
                    Margin = new Thickness(0, 12, 0, 4)
                };
                HotkeyList.Items.Add(header);
                foreach (var a in visible)
                {
                string combo = _config.Hotkeys.TryGetValue(a, out var c) ? c
                    : HotkeyActions.Defaults.TryGetValue(a, out var d) ? d : L10n.T("Ov.Unset");
                var actionLabel = HotkeyActions.DisplayName(a);
                var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
                var label = new TextBlock
                {
                    Text = actionLabel,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("Theme.TextPrimary")
                };
                // 实际注册状态：配置组合被其他程序占用回退默认时，显示生效组合并标注 ⚠
                string display = combo;
                string tip = "";
                if (registered.TryGetValue(a, out var actual)
                    && !string.Equals(actual, combo, StringComparison.OrdinalIgnoreCase))
                {
                    display = actual + " ⚠";
                    tip = string.Format(L10n.T("Hk.ConflictTip"), combo, actual);
                }
                else if (!registered.ContainsKey(a) && !string.IsNullOrEmpty(combo))
                {
                    display = combo + " ⚠";
                    tip = L10n.T("Hk.Unregistered");
                }
                var btn = new Button
                {
                    Content = display,
                    Tag = a,
                    Width = 180,
                    Height = 32,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Padding = new Thickness(8, 0, 8, 0),
                    ToolTip = string.IsNullOrEmpty(tip) ? null : tip
                };
                // 主题化：GhostButton 样式（圆角/主题背景/hover），组合键文字用强调色
                btn.SetResourceReference(StyleProperty, "GhostButton");
                btn.Content = new TextBlock
                {
                    Text = display,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("Theme.Accent")
                };
                    btn.Click += HotkeyRebind_Click;
                    row.Children.Add(btn);
                    row.Children.Add(label);
                    DockPanel.SetDock(btn, Dock.Right);
                    HotkeyList.Items.Add(row);
                }
            }
        }

        private void HotkeyRebind_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string action) return;
            // 已有录制进行中：先恢复所有按钮，避免多个按钮同时处于录音态
            if (_recordingAction != null) BuildHotkeyList();
            _recordingAction = action;
            btn.Content = new TextBlock
            {
                Text = L10n.T("Hk.PressNew"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("Theme.Accent")
            };
            btn.Focus();
        }

        // ==================================================================
        // 工具
        // ==================================================================

        private string DescribeCurrent(List<AudioDeviceInfo> devices, string? currentShortId, bool isOutput)
        {
            if (currentShortId == null)
            {
                string sysId = isOutput ? AudioService.SystemDefaultDeviceId : AudioService.SystemDefaultInputDeviceId;
                // 状态显示系统默认（自定义名优先；虚拟项被"保留设备"隐藏时仍显示状态）
                if (_config.DeviceNames.TryGetValue(sysId, out var cn) && !string.IsNullOrWhiteSpace(cn)) return L10n.T("Ov.Current") + cn;  // 与正常设备"当前：设备名"格式一致
                return L10n.T("Ov.Current") + L10n.T(isOutput ? "Dev.SystemDefaultOut" : "Dev.SystemDefaultIn");
            }
            var dev = devices.FirstOrDefault(d => string.Equals(d.Id, currentShortId, StringComparison.OrdinalIgnoreCase));
            return dev != null ? L10n.T("Ov.Current") + dev.DisplayName : L10n.T("Ov.CurrentUnavailable");
            }

        private static string ShortName(string? full)
        {
            if (string.IsNullOrWhiteSpace(full)) return "(未知设备)";
            int paren = full.IndexOf('(');
            string head = paren > 0 ? full.Substring(0, paren).Trim() : full;
            if (head.Length <= 12) return head;
            return head.Substring(0, 11) + "…";
        }


        // ==================================================================
        // 自动化规则（极简自动化页）
        // ==================================================================

        private List<AutoRuleStep> _autoSteps = new();

        // 操作积木拖拽排序状态
        private int _autoDragFrom = -1;
        private AutoRuleStep? _autoDragStep;
        private bool _autoDragging;
        private double _autoDragStartY;
        private int _autoDragTarget = -1;
        private bool _autoReorderPending;
        private FrameworkElement? _autoGrip;

        private void ShowAutomationPage()
        {
            FillAutoCombos();
            _ = RefreshAutoRulesAsync();
            StartAutoRefresh();
        }

        /// <summary>启动自动化页应用列表低频刷新（8s 一次，离开页面自动停止）。</summary>
        private void StartAutoRefresh()
        {
            if (_autoRefreshTimer != null) return;
            _autoRefreshTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(8) };
            _autoRefreshTimer.Tick += async (_, _) => await RefreshAutoAppsSlowAsync();
            _autoRefreshTimer.Start();
        }

        private void StopAutoRefresh()
        {
            if (_autoRefreshTimer == null) return;
            _autoRefreshTimer.Stop();
            _autoRefreshTimer = null;
        }

        /// <summary>慢速刷新应用列表：新启动 / 退出的应用自动出现在自动化下拉中（保留选中项）。</summary>
        private async Task RefreshAutoAppsSlowAsync()
        {
            try
            {
                var apps = await Task.Run(() => AudioService.GetApps());
                _autoApps = apps;

                // 触发应用下拉：重填并保留选中
                var prevTrigger = (AutoTriggerAppCombo.SelectedItem as AppItem)?.Info.ProcessName;
                LoadAutoAppCombo(AutoTriggerAppCombo, withAny: true);
                SelectAutoApp(AutoTriggerAppCombo, prevTrigger);

                // 已打开的步骤应用下拉：重填并保留选中
                foreach (var combo in _autoStepAppCombos)
                {
                    var prev = (combo.SelectedItem as AppItem)?.Info.ProcessName;
                    combo.Items.Clear();
                    foreach (var a in DisplayApps(_autoApps)) combo.Items.Add(AppItem.From(a));
                    AppItem.LoadIconsAsync(combo.Items.OfType<AppItem>());
                    SelectAutoApp(combo, prev);
                }
            }
            catch { /* 静默：单次刷新失败不影响页面 */ }
        }

        private async Task RefreshAutoRulesAsync()
        {
            var rules = await Task.Run(() => AutoRuleStore.LoadAll());
            AutoRuleList.Items.Clear();
            AutoEmptyText.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var r in rules)
                AutoRuleList.Items.Add(BuildAutoRuleRow(r));
        }

        private UIElement BuildAutoRuleRow(AutoRule r)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            var nameBlock = new TextBlock
            {
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("Theme.TextPrimary")
            };
            nameBlock.Inlines.Add(BuildAutoRuleSummary(r));
            if (r.Trigger == AutoRuleTrigger.Hotkey && !string.IsNullOrWhiteSpace(r.Hotkey))
            {
                nameBlock.Inlines.Add(new System.Windows.Documents.Run("  "));
                nameBlock.Inlines.Add(new System.Windows.Documents.Run(r.Hotkey)
                {
                    Foreground = (Brush)FindResource("Theme.Accent"),
                    FontWeight = FontWeights.SemiBold
                });
            }
            var editBtn = new Button { Content = L10n.T("Auto.Edit"), Tag = r.Id, Width = 64, Height = 28 };
            editBtn.SetResourceReference(StyleProperty, "GhostButton");
            editBtn.Click += AutoEdit_Click;
            var toggleBtn = new Button
            {
                Content = r.Enabled ? L10n.T("Auto.Disable") : L10n.T("Auto.Enable"),
                Tag = r.Id, Width = 64, Height = 28,
                Margin = new Thickness(8, 0, 0, 0)
            };
            toggleBtn.SetResourceReference(StyleProperty, "GhostButton");
            toggleBtn.Click += AutoToggle_Click;
            var delBtn = new Button
            {
                Content = L10n.T("Auto.Delete"), Tag = r.Id, Width = 64, Height = 28,
                Margin = new Thickness(8, 0, 0, 0)
            };
            delBtn.SetResourceReference(StyleProperty, "GhostButton");
            delBtn.Click += AutoDelete_Click;
            var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(editBtn);
            btns.Children.Add(toggleBtn);
            btns.Children.Add(delBtn);
            // 仅一次已执行 / 已禁用的规则：名称右上角显示主题色反色状态点
            var nameHost = new Grid();
            nameHost.Children.Add(nameBlock);
            bool showStateDot = !r.Enabled
                || (r.Trigger == AutoRuleTrigger.Schedule && r.ScheduleMode == 0 && !string.IsNullOrWhiteSpace(r.LastRunKey));
            if (showStateDot)
            {
                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -3, 0, 0),
                    Fill = InvertAccentBrush()
                };
                ToolTipService.SetToolTip(dot, !r.Enabled ? L10n.T("Auto.Disabled") : L10n.T("Auto.OnceExecuted"));
                nameHost.Children.Add(dot);
                nameBlock.Margin = new Thickness(12, 0, 0, 0);
            }
            var dock = new DockPanel();
            DockPanel.SetDock(btns, Dock.Right);
            dock.Children.Add(btns);
            dock.Children.Add(nameHost);
            panel.Children.Add(dock);
            if (IsAutoHotkeyConflict(r))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = L10n.T("Auto.HotkeyConflict"),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("Theme.TextSecondary"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            return panel;
        }

        /// <summary>主题强调色 RGB 反色（255 - 各通道），用于规则状态点。</summary>
        private SolidColorBrush InvertAccentBrush()
        {
            try
            {
                if (FindResource("Theme.Accent") is SolidColorBrush b && b.Color.A > 0)
                {
                    var c = b.Color;
                    return new SolidColorBrush(Color.FromRgb((byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B)));
                }
            }
            catch { }
            return new SolidColorBrush(Colors.White);
        }

        private string BuildAutoRuleSummary(AutoRule r)
        {
            string trigger = r.Trigger switch
            {
                AutoRuleTrigger.AppStart => L10n.T("Auto.TriggerAppStart"),
                AutoRuleTrigger.AppSwitch => L10n.T("Auto.TriggerAppSwitch"),
                AutoRuleTrigger.AppExit => L10n.T("Auto.TriggerAppExit"),
                AutoRuleTrigger.Schedule => BuildScheduleTriggerText(r),
                _ => L10n.T("Auto.TriggerHotkey")
            };
            var actions = r.Actions.Count > 0
                ? r.Actions.Select(s => s.Action).ToList()
                : new List<AutoRuleAction> { r.Action };
            var actionTexts = actions.Select(a => a switch
            {
                AutoRuleAction.SetSystemOutput => L10n.T("Auto.ActionSystemOutput"),
                AutoRuleAction.SetSystemInput => L10n.T("Auto.ActionSystemInput"),
                AutoRuleAction.SetSystemVolume => L10n.T("Auto.ActionSystemVolume"),
                AutoRuleAction.ToggleSystemMute => L10n.T("Auto.ActionSystemMute"),
                AutoRuleAction.SetAppVolume => L10n.T("Auto.ActionAppVolume"),
                AutoRuleAction.ToggleAppMute => L10n.T("Auto.ActionAppMute"),
                AutoRuleAction.SetAppOutput => L10n.T("Auto.ActionAppOutput"),
                AutoRuleAction.SetAppInput => L10n.T("Auto.ActionAppInput"),
                AutoRuleAction.LaunchProgram => L10n.T("Auto.ActionLaunch"),
                AutoRuleAction.ShowOsd => L10n.T("Auto.ActionShowOsd"),
                _ => L10n.T("Auto.ActionPowerShell")
            }).ToList();
            var parts = new List<string> { r.Name, "·", trigger };
            if (r.Trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppSwitch or AutoRuleTrigger.AppExit
                && !string.IsNullOrEmpty(r.TriggerApp))
                parts.Add(r.TriggerApp);
            parts.Add("→");
            parts.Add(string.Join("、", actionTexts));
            return string.Join(" ", parts);
        }

        private static string BuildScheduleTriggerText(AutoRule r)
        {
            string mode = r.ScheduleMode switch
            {
                1 => L10n.T("Auto.ScheduleDaily"),
                2 => L10n.T("Auto.ScheduleWeekly"),
                _ => L10n.T("Auto.ScheduleOnce")
            };
            var s = L10n.T("Auto.TriggerSchedule") + "·" + mode;
            if (!string.IsNullOrWhiteSpace(r.ScheduleTime)) s += " " + r.ScheduleTime.Trim();
            if (r.ScheduleMode == 2 && r.ScheduleWeekdays is { Count: > 0 })
                s += " " + string.Join("/", r.ScheduleWeekdays.OrderBy(w => w).Select(w => L10n.T("Auto.Wd" + w)));
            return s;
        }

        private bool IsAutoHotkeyConflict(AutoRule r)
        {
            if (string.IsNullOrWhiteSpace(r.Hotkey)) return false;
            var reg = ((App)Application.Current).HotkeyRegistration;
            return reg.TryGetValue(AutoRuleService.HotkeyPrefix + r.Id, out var actual)
                && string.IsNullOrEmpty(actual);
        }

        private void AutoNew_Click(object sender, RoutedEventArgs e)
        {
            FillAutoCombos();
            _autoEditingId = null;
            _autoHotkeyCombo = "";
            ResetAutoEditForm();
            AutoEditTitle.Text = L10n.T("Auto.New");
            AutoEditCard.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 自动化 - 打开脚本文件夹（%LocalAppData%\SonicRoute\Automation，不存在则创建后再打开）。
        /// 与「设置 → 语言 → 打开语言文件夹」共用 Core 的 ShellOpen.Folder，行为与失败提示一致。
        /// </summary>
        private void AutoOpenRuleDir_Click(object sender, RoutedEventArgs e)
        {
            if (!ShellOpen.Folder(AutoRuleStore.RulesDir))
                ShowToast(L10n.T("Auto.OpenFolderFail"));
        }

        private void AutoEdit_Click(object sender, RoutedEventArgs e)
        {
            var id = (string)((FrameworkElement)sender).Tag;
            var rule = AutoRuleStore.Find(id);
            if (rule == null) return;
            FillAutoCombos();
            _autoEditingId = rule.Id;
            _suppressAutoUi = true;
            AutoNameBox.Text = rule.Name;
            AutoTriggerCombo.SelectedIndex = (int)rule.Trigger;
            _autoHotkeyCombo = rule.Hotkey ?? "";
            _autoSteps = rule.Actions.Count > 0
                ? rule.Actions.Select(s => s.Clone()).ToList()
                : new List<AutoRuleStep>
                {
                    new AutoRuleStep
                    {
                        Action = rule.Action,
                        TargetApp = rule.TargetApp ?? "",
                        TargetDeviceId = rule.TargetDeviceId ?? "",
                        Volume = rule.Volume,
                        DelayMs = rule.DelayMs,
                        OsdTitle = rule.OsdTitle ?? "",
                        OsdText = rule.OsdText ?? "",
                        ProgramPaths = string.IsNullOrWhiteSpace(rule.ProgramPath)
                            ? new List<string>()
                            : new List<string> { rule.ProgramPath }
                    }
                };
            // 启动程序步骤统一规范化为启动项列表（旧配置由 ProgramPaths 转换，打开方式为空 = 默认方式）
            NormalizeLaunchSteps(_autoSteps);
            _suppressAutoUi = false;
            AutoScheduleModeCombo.SelectedIndex = Math.Clamp(rule.ScheduleMode, 0, 2);
            SetAutoScheduleTime(rule.ScheduleTime);
            SetAutoWeekday(rule.ScheduleWeekdays);
            UpdateAutoTriggerPanels();
            SelectAutoApp(AutoTriggerAppCombo, rule.TriggerApp);
            RenderAutoSteps();
            AutoEditTitle.Text = L10n.T("Auto.Edit");
            AutoEditCard.Visibility = Visibility.Visible;
            UpdateAutoHotkeyHint();
        }

        private void AutoToggle_Click(object sender, RoutedEventArgs e)
        {
            var id = (string)((FrameworkElement)sender).Tag;
            var r = AutoRuleStore.Find(id);
            if (r == null) return;
            r.Enabled = !r.Enabled;
            AutoRuleStore.Save(r);
            ((App)Application.Current).ReloadHotkeys();
            if (_autoEditingId == id)
            {
                _autoCapturingHotkey = false;
                AutoEditCard.Visibility = Visibility.Collapsed;
            }
            _ = RefreshAutoRulesAsync();
        }

        private void AutoDelete_Click(object sender, RoutedEventArgs e)
        {
            var id = (string)((FrameworkElement)sender).Tag;
            AutoRuleStore.Delete(id);
            ((App)Application.Current).ReloadHotkeys();
            if (_autoEditingId == id)
            {
                _autoCapturingHotkey = false;
                AutoEditCard.Visibility = Visibility.Collapsed;
            }
            _ = RefreshAutoRulesAsync();
        }

        private void AutoCancel_Click(object sender, RoutedEventArgs e)
        {
            _autoCapturingHotkey = false;
            AutoEditCard.Visibility = Visibility.Collapsed;
        }

        private void FillAutoCombos()
        {
            if (_autoApps.Count == 0)
                _autoApps = AudioService.GetApps();
            LoadAutoAppCombo(AutoTriggerAppCombo, withAny: true);
            if (AutoTriggerCombo.Items.Count == 0)
            {
                AutoTriggerCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.TriggerHotkey"), Tag = AutoRuleTrigger.Hotkey });
                AutoTriggerCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.TriggerAppStart"), Tag = AutoRuleTrigger.AppStart });
                AutoTriggerCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.TriggerAppSwitch"), Tag = AutoRuleTrigger.AppSwitch });
                AutoTriggerCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.TriggerAppExit"), Tag = AutoRuleTrigger.AppExit });
                AutoTriggerCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.TriggerSchedule"), Tag = AutoRuleTrigger.Schedule });
            }
            if (AutoScheduleModeCombo.Items.Count == 0)
            {
                AutoScheduleModeCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.ScheduleOnce"), Tag = 0 });
                AutoScheduleModeCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.ScheduleDaily"), Tag = 1 });
                AutoScheduleModeCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.ScheduleWeekly"), Tag = 2 });
            }
            if (AutoScheduleHourCombo.Items.Count == 0)
            {
                for (int i = 0; i < 24; i++) AutoScheduleHourCombo.Items.Add(i.ToString("00"));
                for (int i = 0; i < 60; i++) AutoScheduleMinuteCombo.Items.Add(i.ToString("00"));
            }
        }

        private void LoadAutoAppCombo(System.Windows.Controls.ComboBox combo, bool withAny)
        {
            combo.Items.Clear();
            if (withAny)
                combo.Items.Add(new AppItem { Info = new AudioAppInfo { ProcessId = 0, DisplayName = L10n.T("Auto.AnyApp"), ProcessName = null } });
            foreach (var a in _autoApps)
                combo.Items.Add(AppItem.From(a));
            // 图标后台懒加载（列表先显示名称，图标就绪后自动出现，不阻塞 UI）
            AppItem.LoadIconsAsync(combo.Items.OfType<AppItem>());
        }

        private void SelectAutoApp(System.Windows.Controls.ComboBox combo, string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) { combo.SelectedIndex = -1; return; }
            foreach (var item in combo.Items)
                if (item is AppItem ai && string.Equals(ai.Info.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                { combo.SelectedItem = item; return; }
            combo.SelectedIndex = -1;
        }

        private static void SelectAutoDevice(System.Windows.Controls.ComboBox combo, string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) { combo.SelectedIndex = -1; return; }
            foreach (var item in combo.Items)
                if (item is AudioDeviceInfo d && string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                { combo.SelectedItem = item; return; }
            combo.SelectedIndex = -1;
        }

        private void AutoTriggerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAutoUi) return;
            UpdateAutoTriggerPanels();
        }


        private void UpdateAutoTriggerPanels()
        {
            var trigger = AutoTriggerCombo.SelectedIndex < 0 ? AutoRuleTrigger.Hotkey : (AutoRuleTrigger)AutoTriggerCombo.SelectedIndex;
            AutoHotkeyPanel.Visibility = trigger == AutoRuleTrigger.Hotkey ? Visibility.Visible : Visibility.Collapsed;
            bool appBased = trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppSwitch or AutoRuleTrigger.AppExit;
            AutoTriggerAppPanel.Visibility = appBased ? Visibility.Visible : Visibility.Collapsed;
            AutoSchedulePanel.Visibility = trigger == AutoRuleTrigger.Schedule ? Visibility.Visible : Visibility.Collapsed;
            UpdateAutoScheduleModePanels();
        }

        private void AutoScheduleModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAutoUi) return;
            UpdateAutoScheduleModePanels();
        }

        private void UpdateAutoScheduleModePanels()
        {
            int mode = AutoScheduleModeCombo.SelectedIndex < 0 ? 0 : AutoScheduleModeCombo.SelectedIndex;
            AutoScheduleWeekdayPanel.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetAutoScheduleTime(string? time)
        {
            int h = 8, m = 0;
            if (!string.IsNullOrWhiteSpace(time) && TimeSpan.TryParse(time.Trim(), out var ts))
            {
                h = ts.Hours; m = ts.Minutes;
            }
            if (h < 0 || h > 23) h = 8;
            if (m < 0 || m > 59) m = 0;
            AutoScheduleHourCombo.SelectedIndex = h;
            AutoScheduleMinuteCombo.SelectedIndex = m;
        }

        private string CollectAutoScheduleTime()
        {
            int h = AutoScheduleHourCombo.SelectedIndex < 0 ? 8 : AutoScheduleHourCombo.SelectedIndex;
            int m = AutoScheduleMinuteCombo.SelectedIndex < 0 ? 0 : AutoScheduleMinuteCombo.SelectedIndex;
            return h.ToString("00") + ":" + m.ToString("00");
        }

        private void SetAutoWeekday(List<int> days)
        {
            AutoWeekdayPanel.Children.Clear();
            for (int i = 0; i < 7; i++)
            {
                var cb = new CheckBox
                {
                    Content = L10n.T("Auto.Wd" + i),
                    Tag = i,
                    IsChecked = days?.Contains(i) == true,
                    Margin = new Thickness(0, 0, 14, 6),
                    FontSize = 12.5,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                AutoWeekdayPanel.Children.Add(cb);
            }
        }

        private List<int> CollectAutoWeekday()
        {
            var list = new List<int>();
            foreach (var child in AutoWeekdayPanel.Children)
            {
                if (child is CheckBox cb && cb.Tag is int i && cb.IsChecked == true)
                    list.Add(i);
            }
            return list;
        }


        private void ResetAutoEditForm()
        {
            AutoNameBox.Text = "";
            _autoHotkeyCombo = "";
            _autoSteps = new List<AutoRuleStep> { new AutoRuleStep() };
            _suppressAutoUi = true;
            AutoTriggerCombo.SelectedIndex = 0;
            AutoScheduleModeCombo.SelectedIndex = 0;
            _suppressAutoUi = false;
            SetAutoScheduleTime(null);
            SetAutoWeekday(new List<int>());
            AutoTriggerAppCombo.SelectedIndex = -1;
            UpdateAutoTriggerPanels();
            RenderAutoSteps();
            UpdateAutoHotkeyHint();
        }

        private void RenderAutoSteps()
        {
            _autoStepAppCombos.Clear();
            AutoStepsHost.Items.Clear();
            foreach (var step in _autoSteps)
                AutoStepsHost.Items.Add(BuildAutoStepRow(step));
        }

        /// <summary>
        /// 把启动程序步骤规范化为启动项列表（仅在「从未载入过启动项」时由旧的 ProgramPaths 转换一次，
        /// 打开方式留空 = 默认方式），保证旧自动化规则读入编辑器后与执行行为一致。
        /// 已有启动项时保持原列表对象不变——避免每次重渲染都替换列表，导致 UI 事件闭包持有失效对象。
        /// </summary>
        private static void NormalizeLaunchSteps(IEnumerable<AutoRuleStep> steps)
        {
            foreach (var s in steps)
            {
                if (s.Action != AutoRuleAction.LaunchProgram) continue;
                if (s.LaunchItems.Count == 0 && s.ProgramPaths.Any(p => !string.IsNullOrWhiteSpace(p)))
                    s.LaunchItems = s.EffectiveLaunchItems();
            }
        }

        private static List<ComboBoxItem> AutoActionItems()
        {
            var list = new List<ComboBoxItem>();
            list.Add(new ComboBoxItem { Content = L10n.T("Auto.ActionSystemOutput"), Tag = AutoRuleAction.SetSystemOutput });
            list.Add(new ComboBoxItem { Content = L10n.T("Auto.ActionSystemInput"), Tag = AutoRuleAction.SetSystemInput });
            list.Add(new ComboBoxItem { Content = L10n.T("Auto.ActionSystemVolume"), Tag = AutoRuleAction.SetSystemVolume });
            list.Add(new ComboBoxItem { Content = L10n.T("Auto.ActionSystemMute"), Tag = AutoRuleAction.ToggleSystemMute });
            list.Add(new ComboBoxItem { Content = L10n.T("Auto.ActionAppVolume"), Tag = AutoRuleAction.SetAppVolume });
            list.Add(new ComboBoxItem { Content = L10n.T("Auto.ActionAppMute"), Tag = AutoRuleAction.ToggleAppMute });
            list.Add(new ComboBoxItem { Content = L10n.T("Auto.ActionAppOutput"), Tag = AutoRuleAction.SetAppOutput });
            list.Add(new ComboBoxItem { Content = L10n.T("Auto.ActionAppInput"), Tag = AutoRuleAction.SetAppInput });
            list.Add(new ComboBoxItem { Content = L10n.T("Auto.ActionLaunch"), Tag = AutoRuleAction.LaunchProgram });
            list.Add(new ComboBoxItem { Content = L10n.T("Auto.ActionPowerShell"), Tag = AutoRuleAction.RunPowerShell });
            list.Add(new ComboBoxItem { Content = L10n.T("Auto.ActionShowOsd"), Tag = AutoRuleAction.ShowOsd });
            return list;
        }

        private (SolidColorBrush bg, SolidColorBrush border, SolidColorBrush fg) AutoBlockPalette(AutoRuleAction a)
        {
            bool dark = _config.ThemeMode switch { "light" => false, "dark" => true, _ => ThemeService.IsDarkMode() };
            (byte R, byte G, byte B, byte BR, byte BG, byte BB, byte FR, byte FG, byte FB) =
                a switch
                {
                    AutoRuleAction.SetSystemOutput or AutoRuleAction.SetSystemInput
                        or AutoRuleAction.SetSystemVolume or AutoRuleAction.ToggleSystemMute
                        => dark ? ((byte)0x2A, (byte)0x3A, (byte)0x5C, (byte)0x4A, (byte)0x6F, (byte)0xA8, (byte)0xBF, (byte)0xD4, (byte)0xFF)
                                : ((byte)0xE9, (byte)0xF1, (byte)0xFE, (byte)0xC7, (byte)0xD9, (byte)0xF8, (byte)0x1E, (byte)0x41, (byte)0x91),
                    AutoRuleAction.SetAppVolume or AutoRuleAction.ToggleAppMute
                        or AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput
                        => dark ? ((byte)0x4A, (byte)0x37, (byte)0x22, (byte)0x7A, (byte)0x5A, (byte)0x2E, (byte)0xFF, (byte)0xD9, (byte)0xA8)
                                : ((byte)0xFD, (byte)0xF1, (byte)0xE4, (byte)0xF6, (byte)0xDC, (byte)0xBB, (byte)0x8C, (byte)0x4E, (byte)0x11),
                    AutoRuleAction.LaunchProgram or AutoRuleAction.RunPowerShell
                        => dark ? ((byte)0x3A, (byte)0x33, (byte)0x54, (byte)0x5E, (byte)0x4F, (byte)0x8F, (byte)0xD3, (byte)0xC4, (byte)0xFF)
                                : ((byte)0xF1, (byte)0xED, (byte)0xFE, (byte)0xDE, (byte)0xD4, (byte)0xF9, (byte)0x5C, (byte)0x3F, (byte)0xAA),
                    _ => dark ? ((byte)0x25, (byte)0x46, (byte)0x4A, (byte)0x3E, (byte)0x6E, (byte)0x70, (byte)0xB8, (byte)0xEC, (byte)0xE9)
                              : ((byte)0xE6, (byte)0xF8, (byte)0xF6, (byte)0xC5, (byte)0xEA, (byte)0xE8, (byte)0x13, (byte)0x7B, (byte)0x77)
                };
            static SolidColorBrush Mk(byte r, byte g, byte b)
            {
                var b2 = new SolidColorBrush(Color.FromRgb(r, g, b));
                b2.Freeze();
                return b2;
            }
            return (Mk(R, G, B), Mk(BR, BG, BB), Mk(FR, FG, FB));
        }

        private UIElement BuildAutoStepRow(AutoRuleStep step)
        {
            var (bg, border, fg) = AutoBlockPalette(step.Action);
            var block = new Border
            {
                Background = bg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 12, 8),
                Margin = new Thickness(0, 8, 0, 0),
                Tag = step
            };
            var body = new StackPanel();

            // 头行：拖拽把手（左）+ 操作类型下拉 + 删除（右）
            var head = new DockPanel();
            var del = new Button { Content = L10n.T("Auto.Delete"), Tag = step, Width = 56, Height = 28 };
            del.SetResourceReference(StyleProperty, "GhostButton");
            del.Click += AutoStepDelete_Click;
            DockPanel.SetDock(del, Dock.Right);
            head.Children.Add(del);
            var grip = new Border
            {
                Tag = block,
                Width = 30,
                Height = 28,
                Cursor = System.Windows.Input.Cursors.SizeAll,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
                ToolTip = L10n.T("Auto.DragOrder")
            };
            var gripText = new TextBlock
            {
                Text = "⋮⋮",
                FontSize = 14,
                Foreground = fg,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            grip.Child = gripText;
            grip.MouseLeftButtonDown += AutoStepGrip_MouseLeftButtonDown;
            grip.MouseMove += AutoStepGrip_MouseMove;
            grip.MouseLeftButtonUp += AutoStepGrip_MouseLeftButtonUp;
            DockPanel.SetDock(grip, Dock.Left);
            head.Children.Add(grip);
            var combo = new System.Windows.Controls.ComboBox
            {
                Style = (Style)FindResource("SelCombo"),
                Width = 300,
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = step
            };
            combo.SelectionChanged += AutoStepAction_SelectionChanged;
            foreach (var item in AutoActionItems())
                combo.Items.Add(item);
            combo.SelectedIndex = (int)step.Action;
            head.Children.Add(combo);
            body.Children.Add(head);

            // 参数区（内联积木）
            body.Children.Add(BuildStepParams(step, fg));

            // 延时行（底部，虚线分隔）
            var delayRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            delayRow.Children.Add(new TextBlock
            {
                Text = L10n.T("Auto.Delay"),
                FontSize = 11.5,
                Foreground = fg,
                Opacity = 0.85,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
            var delayBox = new TextBox
            {
                Text = step.DelayMs.ToString(),
                FontSize = 12,
                Width = 64,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = step
            };
            delayBox.TextChanged += AutoStepDelay_TextChanged;
            delayRow.Children.Add(delayBox);
            delayRow.Children.Add(new TextBlock
            {
                Text = L10n.T("Auto.DelayUnit"),
                FontSize = 11,
                Foreground = fg,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            });
            delayRow.Children.Add(new TextBlock
            {
                Text = L10n.T("Auto.DelayRange"),
                FontSize = 10.5,
                Foreground = fg,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
            body.Children.Add(delayRow);

            block.Child = body;
            return block;
        }


        private void AutoStepDelay_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox box || box.Tag is not AutoRuleStep step) return;
            var text = box.Text.Trim();
            // 空文本不处理：用户还在输入，保留上一次合法值
            if (text.Length == 0) return;
            // 超过 5 位数字（含在最前面继续输入的情况）：直接校验，超范围一律校准为 60000
            if (text.Length > 5 || !int.TryParse(text, out var ms))
            {
                int target = int.TryParse(text, out var t)
                    ? Math.Clamp(t, 0, 60000)
                    : Math.Clamp(step.DelayMs, 0, 60000);
                step.DelayMs = target;
                SetDelayBoxText(box, target.ToString());
                return;
            }
            var clamped = Math.Clamp(ms, 0, 60000);
            step.DelayMs = clamped;
            if (clamped != ms)
            {
                // 超出范围自动纠正显示（防重入 + 保留光标位置，避免前面输入时光标被重置到末尾）
                SetDelayBoxText(box, clamped.ToString());
            }
        }

        /// <summary>改写延时输入框文本，保留光标在用户输入位置附近（防重入）。</summary>
        private void SetDelayBoxText(TextBox box, string display)
        {
            var caret = Math.Min(box.CaretIndex, display.Length);
            box.TextChanged -= AutoStepDelay_TextChanged;
            box.Text = display;
            box.TextChanged += AutoStepDelay_TextChanged;
            box.CaretIndex = caret;
        }

        private void AutoStepGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not Border block) return;
            if (block.Tag is not AutoRuleStep step) return;
            _autoDragFrom = _autoSteps.IndexOf(step);
            _autoDragStep = step;
            _autoDragging = false;
            _autoDragStartY = e.GetPosition(AutoStepsHost).Y;
            _autoDragTarget = _autoDragFrom;
            _autoGrip = fe;
            fe.CaptureMouse();
            e.Handled = true;
        }

        private void AutoStepGrip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_autoDragFrom < 0 || _autoDragStep == null) return;
            var y = e.GetPosition(AutoStepsHost).Y;
            if (!_autoDragging)
            {
                if (Math.Abs(y - _autoDragStartY) < 6) return;
                _autoDragging = true;
                if (_autoGrip?.Tag is Border gb) gb.Opacity = 0.75;
            }
            int cur = _autoSteps.IndexOf(_autoDragStep);
            if (cur < 0) return;
            int slot = AutoDragHitSlot(y);
            int target = slot > cur ? slot - 1 : slot;
            if (target == cur) { _autoDragTarget = cur; e.Handled = true; return; }
            if (target == _autoDragTarget) { e.Handled = true; return; }
            _autoDragTarget = target;
            if (!_autoReorderPending)
            {
                _autoReorderPending = true;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(ApplyAutoReorder));
            }
            e.Handled = true;
        }

        /// <summary>在消息循环（非事件栈）内安全执行积木实时换位，避免修改 Items 集合触发容器生成异常。</summary>
        private void ApplyAutoReorder()
        {
            _autoReorderPending = false;
            if (!_autoDragging || _autoDragStep == null || _autoGrip == null) return;
            int cur = _autoSteps.IndexOf(_autoDragStep);
            int target = _autoDragTarget;
            if (cur < 0 || target < 0 || cur == target || target > _autoSteps.Count) return;
            // Items 里存的是积木 Border（Tag=step），必须移动 Border 而不是 step 对象
            Border? block = null;
            foreach (var it in AutoStepsHost.Items)
            {
                if (it is Border b && ReferenceEquals(b.Tag, _autoDragStep)) { block = b; break; }
            }
            if (block == null) return;

            // 记录移动前各积木的视觉 Y 位置，用于滑动动画
            var before = new Dictionary<Border, double>();
            foreach (var it in AutoStepsHost.Items)
                if (it is Border b) before[b] = AutoItemY(b);

            AutoStepsHost.Items.RemoveAt(cur);
            AutoStepsHost.Items.Insert(target, block);
            _autoSteps.RemoveAt(cur);
            _autoSteps.Insert(target, _autoDragStep);

            // 简单切换动画：积木从旧位置平滑滑到新位置
            AutoStepsHost.UpdateLayout();
            foreach (var kv in before)
            {
                double offset = kv.Value - AutoItemY(kv.Key);
                if (Math.Abs(offset) < 0.5) continue;
                var t = new TranslateTransform(0, offset);
                kv.Key.RenderTransform = t;
                var anim = new System.Windows.Media.Animation.DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(150)))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                };
                t.BeginAnimation(TranslateTransform.YProperty, anim);
            }

            // 容器重建会丢鼠标捕获，恢复，保证后续 MouseMove 继续到达
            if (_autoGrip.IsLoaded) _autoGrip.CaptureMouse();
        }

        /// <summary>积木在其容器（AutoStepsHost）中的视觉 Y 位置。</summary>
        private double AutoItemY(Border b)
        {
            if (AutoStepsHost.ItemContainerGenerator.ContainerFromItem(b) is FrameworkElement c)
                return c.TransformToAncestor(AutoStepsHost).Transform(new System.Windows.Point(0, 0)).Y;
            return 0;
        }

        private void AutoStepGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                fe.ReleaseMouseCapture();
                if (fe.Tag is Border b) b.Opacity = 1;
            }
            if (_autoDragging && _autoDragStep != null && _autoDragFrom >= 0)
            {
                int cur = _autoSteps.IndexOf(_autoDragStep);
                if (cur >= 0)
                {
                    int slot = AutoDragHitSlot(e.GetPosition(AutoStepsHost).Y);
                    int target = slot > cur ? slot - 1 : slot;
                    if (target != cur)
                    {
                        _autoSteps.RemoveAt(cur);
                        _autoSteps.Insert(Math.Clamp(target, 0, _autoSteps.Count), _autoDragStep);
                        RenderAutoSteps();
                    }
                }
            }
            _autoDragFrom = -1;
            _autoDragStep = null;
            _autoDragging = false;
            _autoDragTarget = -1;
            _autoGrip = null;
            e.Handled = true;
        }

        /// <summary>计算鼠标纵坐标 y 对应的插入槽位（0..N，插到该索引项之前）。</summary>
        /// <summary>计算鼠标位置对应的插入槽位。排除被拖积木（避免拖动中自身移动干扰判定），
        /// 阈值放宽到积木上 35% 高度即触发换位（比中线更灵敏、更容易落位）。</summary>
        private int AutoDragHitSlot(double y)
        {
            int count = AutoStepsHost.Items.Count;
            int insert = count; // 默认末尾（含被拖项序列 0..Count）
            int cur = _autoDragStep != null ? _autoSteps.IndexOf(_autoDragStep) : -1;
            for (int i = 0; i < count; i++)
            {
                if (AutoStepsHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
                if (c is Border b && _autoDragStep != null && ReferenceEquals(b.Tag, _autoDragStep)) continue;
                var p = c.TransformToAncestor(AutoStepsHost).Transform(new System.Windows.Point(0, 0));
                if (y < p.Y + c.ActualHeight * 0.35)
                {
                    // 插入到悬停积木之前：若被拖积木本来就在其前面，插入位后移一位
                    insert = cur >= 0 && cur < i ? i - 1 : i;
                    break;
                }
                insert = i + 1;
            }
            return insert;
        }

        private void AutoStepDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AutoRuleStep step)
            {
                _autoSteps.Remove(step);
                RenderAutoSteps();
            }
        }

        private void AutoStepAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.ComboBox combo || combo.Tag is not AutoRuleStep step) return;
            step.Action = combo.SelectedIndex < 0 ? AutoRuleAction.SetSystemOutput : (AutoRuleAction)combo.SelectedIndex;
            NormalizeLaunchSteps(new[] { step });
            var block = FindParent<Border>(combo);
            if (block == null) return;
            var (bg, border, fg) = AutoBlockPalette(step.Action);
            block.Background = bg;
            block.BorderBrush = border;
            if (block.Child is StackPanel sp && sp.Children.Count >= 2)
            {
                sp.Children.RemoveAt(1);
                sp.Children.Insert(1, BuildStepParams(step, fg));
            }
        }


        private UIElement BuildStepParams(AutoRuleStep step, SolidColorBrush fg)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var labelBrush = new SolidColorBrush(Color.FromArgb(210, fg.Color.R, fg.Color.G, fg.Color.B));
            labelBrush.Freeze();
            UIElement MkLabel(string text) => new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = labelBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            UIElement Group(string label, UIElement ctrl)
            {
                var sp = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 14, 8),
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(MkLabel(label));
                sp.Children.Add(ctrl);
                return sp;
            }

            bool app = step.Action is AutoRuleAction.SetAppVolume or AutoRuleAction.ToggleAppMute
                or AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput;
            bool dev = step.Action is AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput
                or AutoRuleAction.SetSystemOutput or AutoRuleAction.SetSystemInput;
            bool vol = step.Action is AutoRuleAction.SetSystemVolume or AutoRuleAction.SetAppVolume;
            bool prog = step.Action is AutoRuleAction.LaunchProgram or AutoRuleAction.RunPowerShell;
            bool osd = step.Action == AutoRuleAction.ShowOsd;

            if (app)
            {
                var cb = new System.Windows.Controls.ComboBox
                {
                    Style = (Style)FindResource("SelCombo"),
                    Width = 240,
                    ItemTemplate = (DataTemplate)FindResource("AutoAppItemTemplate"),
                    Tag = step
                };
                foreach (var a in DisplayApps(_autoApps)) cb.Items.Add(AppItem.From(a));
                AppItem.LoadIconsAsync(cb.Items.OfType<AppItem>());
                _autoStepAppCombos.Add(cb);
                SelectAutoApp(cb, step.TargetApp);
                cb.SelectionChanged += (_, _) =>
                {
                    if (cb.SelectedItem is AppItem ai) step.TargetApp = ai.Info.ProcessName ?? "";
                };
                wrap.Children.Add(Group(L10n.T("Auto.TargetApp"), cb));
            }
            if (dev)
            {
                var cb = new System.Windows.Controls.ComboBox
                {
                    Style = (Style)FindResource("SelCombo"),
                    Width = 260,
                    DisplayMemberPath = "DisplayLabel",
                    Tag = step
                };
                var flow = step.Action is AutoRuleAction.SetAppInput or AutoRuleAction.SetSystemInput
                    ? EDataFlow.eCapture : EDataFlow.eRender;
                foreach (var d in DisplayDevices(AudioService.GetDevices(flow))) cb.Items.Add(d);
                SelectAutoDevice(cb, step.TargetDeviceId);
                cb.SelectionChanged += (_, _) =>
                {
                    if (cb.SelectedItem is AudioDeviceInfo d) step.TargetDeviceId = d.Id;
                };
                wrap.Children.Add(Group(L10n.T("Auto.TargetDevice"), cb));
            }
            if (vol)
            {
                var txt = new TextBlock
                {
                    Text = step.Volume + "%",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    MinWidth = 44,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var sl = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = step.Volume,
                    Width = 200,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsMoveToPointEnabled = true
                };
                sl.ValueChanged += (_, _) =>
                {
                    step.Volume = (int)Math.Round(sl.Value);
                    txt.Text = step.Volume + "%";
                };
                // 鼠标滚轮调节音量（±5，阻止冒泡避免页面滚动）
                sl.MouseWheel += (_, e) =>
                {
                    int delta = e.Delta > 0 ? 5 : -5;
                    step.Volume = Math.Clamp(step.Volume + delta, 0, 100);
                    sl.Value = step.Volume;
                    e.Handled = true;
                };
                var g = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 14, 8),
                    VerticalAlignment = VerticalAlignment.Center
                };
                g.Children.Add(sl);
                g.Children.Add(txt);
                wrap.Children.Add(g);
            }
            if (prog)
            {
                if (step.Action == AutoRuleAction.LaunchProgram)
                {
                    // 启动程序（v1.18）：启动模式 + 启动列表，每个启动项独立打开方式
                    wrap.Children.Add(BuildLaunchPanel(step, fg, labelBrush));
                }
                else
                {
                    var pathPanel = new StackPanel
                    {
                        Margin = new Thickness(0, 0, 14, 8),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    pathPanel.Children.Add(MkLabel(L10n.T("Auto.Script")));
                    for (int i = 0; i < step.ProgramPaths.Count; i++)
                        pathPanel.Children.Add(BuildPathRow(step, i));
                    var addBtn = new Button
                    {
                        Content = L10n.T("Auto.AddPath"),
                        Tag = step,
                        Width = 150,
                        Height = 28,
                        Margin = new Thickness(0, 6, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        AllowDrop = true,
                        ToolTip = L10n.T("Auto.DragHint")
                    };
                    addBtn.SetResourceReference(StyleProperty, "GhostButton");
                    addBtn.Click += AutoAddPath_Click;
                    addBtn.PreviewDragOver += AutoAddPath_DragOver;
                    addBtn.PreviewDrop += AutoAddPath_Drop;
                    pathPanel.Children.Add(addBtn);
                    pathPanel.Children.Add(new TextBlock
                    {
                        Text = L10n.T("Auto.DragHint"),
                        FontSize = 11,
                        Foreground = labelBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 0)
                    });
                    wrap.Children.Add(pathPanel);
                }
            }
            if (osd)
            {
                var titleBox = new TextBox
                {
                    Text = step.OsdTitle,
                    FontSize = 12.5,
                    Width = 200,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(8, 3, 8, 3),
                    Tag = step
                };
                titleBox.TextChanged += (_, _) =>
                {
                    if (titleBox.Tag is AutoRuleStep s) s.OsdTitle = titleBox.Text;
                };
                wrap.Children.Add(Group(L10n.T("Auto.OsdTitle"), titleBox));
                var textBox = new TextBox
                {
                    Text = step.OsdText,
                    FontSize = 12.5,
                    Width = 200,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(8, 3, 8, 3),
                    Tag = step
                };
                textBox.TextChanged += (_, _) =>
                {
                    if (textBox.Tag is AutoRuleStep s) s.OsdText = textBox.Text;
                };
                wrap.Children.Add(Group(L10n.T("Auto.OsdText"), textBox));
            }
            return wrap;
        }

        /// <summary>打开方式下拉中「选择其他程序…」项的哨兵值（不作为实际路径保存）。</summary>
        private const string AutoLaunchPickTag = "__pick__";

        /// <summary>启动程序动作参数区（v1.18）：启动模式 + 启动列表（每个启动项独立保存打开方式）。</summary>
        private UIElement BuildLaunchPanel(AutoRuleStep step, SolidColorBrush fg, SolidColorBrush labelBrush)
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(0, 0, 14, 8),
                VerticalAlignment = VerticalAlignment.Center
            };
            UIElement MkLabel(string text) => new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = labelBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            // 启动模式：全部启动 / 随机启动一个
            var modeRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            modeRow.Children.Add(MkLabel(L10n.T("Auto.LaunchMode")));
            var modeCombo = new System.Windows.Controls.ComboBox
            {
                Style = (Style)FindResource("SelCombo"),
                Width = 160,
                Height = 28,
                Tag = step,
                VerticalAlignment = VerticalAlignment.Center
            };
            modeCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.LaunchModeAll") });
            modeCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.LaunchModeRandom") });
            modeCombo.SelectedIndex = step.LaunchMode == 1 ? 1 : 0;
            modeCombo.SelectionChanged += (_, _) => step.LaunchMode = modeCombo.SelectedIndex == 1 ? 1 : 0;
            modeRow.Children.Add(modeCombo);
            panel.Children.Add(modeRow);

            // 启动列表
            panel.Children.Add(new TextBlock
            {
                Text = L10n.T("Auto.LaunchList"),
                FontSize = 12,
                Foreground = labelBrush,
                Margin = new Thickness(0, 8, 0, 0)
            });
            var listBorder = new Border
            {
                Width = 520,
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, fg.Color.R, fg.Color.G, fg.Color.B))
            };
            var listPanel = new StackPanel();
            for (int i = 0; i < step.LaunchItems.Count; i++)
                listPanel.Children.Add(BuildLaunchItemRow(step, step.LaunchItems[i], i));
            listBorder.Child = listPanel;
            panel.Children.Add(listBorder);

            // 添加文件 / 程序（按钮本身支持拖放，与 PowerShell 路径区一致）
            var addBtn = new Button
            {
                Content = L10n.T("Auto.AddFile"),
                Tag = step,
                Width = 150,
                Height = 28,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                AllowDrop = true,
                ToolTip = L10n.T("Auto.DragHint")
            };
            addBtn.SetResourceReference(StyleProperty, "GhostButton");
            addBtn.Click += AutoLaunchAdd_Click;
            addBtn.PreviewDragOver += AutoAddPath_DragOver;
            addBtn.PreviewDrop += AutoLaunchAdd_Drop;
            panel.Children.Add(addBtn);
            panel.Children.Add(new TextBlock
            {
                Text = L10n.T("Auto.DragHint"),
                FontSize = 11,
                Foreground = labelBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
            return panel;
        }

        /// <summary>
        /// 「打开方式」下拉项：图标 + 显示名 + 实际 EXE 路径。
        /// 属性名与 AutoAppItemTemplate（Icon / Label）一致，直接复用该模板，不新增 XAML。
        /// </summary>
        private sealed class OpenWithItem
        {
            public ImageSource? Icon { get; set; }
            public string Label { get; set; } = "";
            public string ExePath { get; set; } = "";
        }

        /// <summary>启动项一行：路径输入框（左）+ 打开方式下拉 + 删除（右）。</summary>
        private UIElement BuildLaunchItemRow(AutoRuleStep step, AutoLaunchItem item, int index)
        {
            var tag = new Tuple<AutoRuleStep, AutoLaunchItem>(step, item);
            var row = new DockPanel { Margin = new Thickness(0, index == 0 ? 0 : 6, 0, 0) };

            var del = new Button
            {
                Content = "×",
                Tag = tag,
                Width = 28,
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            del.SetResourceReference(StyleProperty, "GhostButton");
            del.Click += AutoLaunchDelete_Click;
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            // 打开方式：默认程序 / 系统关联应用（按该启动项的扩展名枚举）/ 选择其他应用…
            var openCombo = new System.Windows.Controls.ComboBox
            {
                Style = (Style)FindResource("SelCombo"),
                Width = 176,
                Height = 28,
                Tag = tag,
                ItemTemplate = (DataTemplate)FindResource("AutoAppItemTemplate"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = string.IsNullOrWhiteSpace(item.OpenWith) ? L10n.T("Auto.OpenWith") : item.OpenWith
            };

            bool filling = false;        // 填充/恢复选中期间不写回配置
            string lastExt = "\0";       // 已按哪个扩展名枚举过（路径改了要重枚举）

            void Fill()
            {
                filling = true;
                try
                {
                    openCombo.Items.Clear();
                    openCombo.Items.Add(new OpenWithItem { Label = L10n.T("Auto.OpenWithDefault"), ExePath = "" });

                    foreach (var app in OpenWithService.GetHandlers(item.Path))
                    {
                        openCombo.Items.Add(new OpenWithItem
                        {
                            Label = app.DisplayName,
                            ExePath = app.ExePath,
                            Icon = AppIconService.GetIconForPath(app.ExePath)
                        });
                    }

                    // 已保存但不在系统列表中的应用也保留：程序被移动 / 关联变化时不丢配置
                    if (!string.IsNullOrWhiteSpace(item.OpenWith)
                        && !openCombo.Items.OfType<OpenWithItem>().Any(
                            x => string.Equals(x.ExePath, item.OpenWith, StringComparison.OrdinalIgnoreCase)))
                    {
                        openCombo.Items.Add(new OpenWithItem
                        {
                            Label = string.IsNullOrWhiteSpace(item.OpenWithName)
                                ? ProgramDisplayName(item.OpenWith) : item.OpenWithName,
                            ExePath = item.OpenWith,
                            Icon = AppIconService.GetIconForPath(item.OpenWith)
                        });
                    }

                    openCombo.Items.Add(new OpenWithItem { Label = L10n.T("Auto.OpenWithPick"), ExePath = AutoLaunchPickTag });

                    int sel = 0;
                    if (!string.IsNullOrWhiteSpace(item.OpenWith))
                    {
                        int hit = openCombo.Items.OfType<OpenWithItem>().ToList()
                            .FindIndex(x => string.Equals(x.ExePath, item.OpenWith, StringComparison.OrdinalIgnoreCase));
                        if (hit >= 0) sel = hit;
                    }
                    openCombo.SelectedIndex = sel;
                }
                finally { filling = false; }
            }

            // 路径被手动改过（扩展名变化）时，展开下拉前重新向系统查询关联应用
            openCombo.DropDownOpened += (_, _) =>
            {
                var ext = OpenWithService.NormalizeExtension(item.Path);
                if (ext == lastExt) return;
                lastExt = ext;
                Fill();
            };
            openCombo.SelectionChanged += (_, _) =>
            {
                if (filling || openCombo.SelectedItem is not OpenWithItem picked) return;
                if (picked.ExePath == AutoLaunchPickTag)
                {
                    var exe = PickProgramExe();
                    if (exe != null)
                    {
                        item.OpenWith = exe;
                        item.OpenWithName = ProgramDisplayName(exe);
                    }
                    // 下拉正在关闭，延后重建列表避免在事件内改自身集合
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        lastExt = OpenWithService.NormalizeExtension(item.Path);
                        Fill();
                    }));
                    return;
                }
                item.OpenWith = picked.ExePath;
                item.OpenWithName = picked.ExePath.Length == 0 ? "" : picked.Label;
                openCombo.ToolTip = picked.ExePath.Length == 0 ? L10n.T("Auto.OpenWith") : picked.ExePath;
            };

            // 构建时即按当前路径枚举系统关联应用：选完文件后打开方式立即可选，
            // 同时避免在展开下拉的过程中改动集合
            lastExt = OpenWithService.NormalizeExtension(item.Path);
            Fill();
            DockPanel.SetDock(openCombo, Dock.Right);
            row.Children.Add(openCombo);

            var box = new TextBox
            {
                Text = item.Path,
                FontSize = 12.5,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 4, 8, 4),
                AllowDrop = true,
                Tag = tag
            };
            box.PreviewDragOver += AutoPathBox_DragOver;
            box.PreviewDrop += AutoLaunchBox_Drop;
            box.TextChanged += (_, _) => item.Path = box.Text;
            row.Children.Add(box);
            return row;
        }

        /// <summary>打开方式显示名（EXE 文件名，去掉扩展名）。</summary>
        private static string ProgramDisplayName(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return "";
            try
            {
                var n = System.IO.Path.GetFileNameWithoutExtension(exePath.Trim());
                return string.IsNullOrWhiteSpace(n) ? exePath.Trim() : n;
            }
            catch { return exePath.Trim(); }
        }

        /// <summary>「添加文件/程序」文件选择过滤器：默认展示全部文件（程序只是其中一类）。</summary>
        private static string AddFileFilter =>
            L10n.T("Auto.FileFilterAll") + " (*.*)|*.*|" + L10n.T("Auto.FileFilterExe") + " (*.exe)|*.exe";

        /// <summary>浏览本机 EXE（「选择其他应用…」）；取消返回 null。</summary>
        private static string? PickProgramExe()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L10n.T("Auto.PickProgram"),
                Filter = L10n.T("Auto.FileFilterExe") + " (*.exe)|*.exe|" + L10n.T("Auto.FileFilterAll") + " (*.*)|*.*",
                CheckFileExists = true
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        /// <summary>
        /// 「＋ 添加文件/程序」：打开文件选择器（支持多选），选中的文件/程序逐个加入启动项，
        /// 新增项打开方式一律为「默认程序」。与「选择打开方式」是两个独立操作，互不覆盖。
        /// </summary>
        private void AutoLaunchAdd_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not AutoRuleStep step) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L10n.T("Auto.AddFileTitle"),
                Filter = AddFileFilter,
                Multiselect = true,
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;
            AddLaunchPaths(step, dlg.FileNames);
            RenderAutoSteps();
        }

        /// <summary>把路径批量加入启动项（按路径去重，打开方式默认）。</summary>
        private static void AddLaunchPaths(AutoRuleStep step, IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (step.LaunchItems.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
                step.LaunchItems.Add(new AutoLaunchItem { Path = p });
            }
        }

        private void AutoLaunchDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Tuple<AutoRuleStep, AutoLaunchItem> tg)
            {
                tg.Item1.LaunchItems.Remove(tg.Item2);
                RenderAutoSteps();
            }
        }

        private void AutoLaunchAdd_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AutoRuleStep step)
                AddLaunchItemsFromDrop(step, e);
        }

        private void AutoLaunchBox_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (sender is TextBox box && box.Tag is Tuple<AutoRuleStep, AutoLaunchItem> tg)
                AddLaunchItemsFromDrop(tg.Item1, e);
        }

        /// <summary>拖入文件追加为新的启动项（按路径去重，打开方式保持默认）。</summary>
        private void AddLaunchItemsFromDrop(AutoRuleStep step, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files) return;
            AddLaunchPaths(step, files);
            e.Handled = true;
            RenderAutoSteps();
        }

        private UIElement BuildPathRow(AutoRuleStep step, int index)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var del = new Button
            {
                Content = "×",
                Tag = step,
                Width = 28,
                Height = 26,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            del.SetResourceReference(StyleProperty, "GhostButton");
            del.Click += AutoPathDelete_Click;
            DockPanel.SetDock(del, Dock.Right);
            var box = new TextBox
            {
                Text = index < step.ProgramPaths.Count ? step.ProgramPaths[index] : "",
                FontSize = 12.5,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 4, 8, 4),
                AllowDrop = true,
                Tag = new Tuple<AutoRuleStep, int>(step, index)
            };
            box.PreviewDragOver += AutoPathBox_DragOver;
            box.PreviewDrop += AutoPathBox_Drop;
            box.TextChanged += (_, _) =>
            {
                if (box.Tag is Tuple<AutoRuleStep, int> tg
                    && tg.Item2 < tg.Item1.ProgramPaths.Count)
                    tg.Item1.ProgramPaths[tg.Item2] = box.Text;
            };
            row.Children.Add(del);
            row.Children.Add(box);
            return row;
        }

        private void AutoAddPath_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AutoRuleStep step)
            {
                step.ProgramPaths.Add("");
                RenderAutoSteps();
            }
        }

        private void AutoPathDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AutoRuleStep step)
            {
                var row = FindParent<DockPanel>(btn);
                if (row != null && row.Children.Count > 1 && row.Children[1] is TextBox box)
                {
                    step.ProgramPaths.Remove(box.Text);
                    RenderAutoSteps();
                }
            }
        }

        private void AutoPathBox_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void AutoAddPath_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void AutoAddPath_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AutoRuleStep step
                && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
            {
                foreach (var f in files)
                {
                    if (!string.IsNullOrWhiteSpace(f) && !step.ProgramPaths.Contains(f))
                        step.ProgramPaths.Add(f);
                }
                e.Handled = true;
                RenderAutoSteps();
            }
        }

        private void AutoPathBox_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (sender is TextBox box && box.Tag is Tuple<AutoRuleStep, int> tg
                && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
            {
                foreach (var f in files)
                {
                    if (!string.IsNullOrWhiteSpace(f) && !tg.Item1.ProgramPaths.Contains(f))
                        tg.Item1.ProgramPaths.Add(f);
                }
                e.Handled = true;
                RenderAutoSteps();
            }
        }

        private void AutoAddStep_Click(object sender, RoutedEventArgs e)
        {
            _autoSteps.Add(new AutoRuleStep());
            RenderAutoSteps();
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var p = System.Windows.Media.VisualTreeHelper.GetParent(child);
            while (p != null)
            {
                if (p is T t) return t;
                p = System.Windows.Media.VisualTreeHelper.GetParent(p);
            }
            return null;
        }
        private void AutoHotkeyCapture_Click(object sender, RoutedEventArgs e)
        {
            _autoCapturingHotkey = !_autoCapturingHotkey;
            UpdateAutoHotkeyHint();
        }

        private void UpdateAutoHotkeyHint()
        {
            if (_autoCapturingHotkey)
            {
                AutoHotkeyCapture.Content = L10n.T("Auto.HotkeyCapturing");
                AutoHotkeyHintText.Text = L10n.T("Auto.HotkeyCapturing");
                return;
            }
            AutoHotkeyHintText.Text = L10n.T("Auto.HotkeyHint");
            if (string.IsNullOrEmpty(_autoHotkeyCombo))
            {
                AutoHotkeyCapture.Content = new TextBlock
                {
                    Text = L10n.T("Auto.HotkeyUnbound"),
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("Theme.Accent")
                };
                return;
            }
            AutoHotkeyCapture.Content = new TextBlock
            {
                Text = _autoHotkeyCombo,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("Theme.Accent")
            };
        }


        private void AutoSave_Click(object sender, RoutedEventArgs e)
        {
            var name = AutoNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) { _ = System.Windows.MessageBox.Show(L10n.T("Auto.NameRequired")); return; }
            var trigger = AutoTriggerCombo.SelectedIndex < 0 ? AutoRuleTrigger.Hotkey : (AutoRuleTrigger)AutoTriggerCombo.SelectedIndex;
            var hotkey = _autoHotkeyCombo.Trim();
            if (trigger == AutoRuleTrigger.Hotkey && string.IsNullOrEmpty(hotkey))
            { _ = System.Windows.MessageBox.Show(L10n.T("Auto.HotkeyRequired")); return; }
            string? triggerApp = (AutoTriggerAppCombo.SelectedItem as AppItem)?.Info.ProcessName;
            if (trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppSwitch or AutoRuleTrigger.AppExit
                && string.IsNullOrEmpty(triggerApp))
            { _ = System.Windows.MessageBox.Show(L10n.T("Auto.TriggerAppRequired")); return; }
            int scheduleMode = AutoScheduleModeCombo.SelectedIndex < 0 ? 0 : AutoScheduleModeCombo.SelectedIndex;
            var scheduleTime = CollectAutoScheduleTime();
            var scheduleWeekdays = CollectAutoWeekday();
            if (trigger == AutoRuleTrigger.Schedule && scheduleMode == 2 && scheduleWeekdays.Count == 0)
            {
                _ = System.Windows.MessageBox.Show(L10n.T("Auto.ScheduleWeekdayRequired"));
                return;
            }
            foreach (var s in _autoSteps)
            {
                if (s.Action is AutoRuleAction.SetAppVolume or AutoRuleAction.ToggleAppMute
                    or AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput
                    && string.IsNullOrWhiteSpace(s.TargetApp))
                { _ = System.Windows.MessageBox.Show(L10n.T("Auto.ActionAppRequired")); return; }
                if (s.Action is AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput
                    or AutoRuleAction.SetSystemOutput or AutoRuleAction.SetSystemInput
                    && string.IsNullOrWhiteSpace(s.TargetDeviceId))
                { _ = System.Windows.MessageBox.Show(L10n.T("Auto.TargetDevice")); return; }
                if ((s.Action == AutoRuleAction.LaunchProgram && s.CurrentLaunchItems().Count == 0)
                    || (s.Action == AutoRuleAction.RunPowerShell && s.ProgramPaths.All(string.IsNullOrWhiteSpace)))
                { _ = System.Windows.MessageBox.Show(L10n.T("Auto.ProgramRequired")); return; }
            }
            var rule = _autoEditingId == null ? null : AutoRuleStore.Find(_autoEditingId);
            if (rule == null)
            {
                rule = new AutoRule { Id = Guid.NewGuid().ToString("N"), Name = name };
            }
            else
            {
                rule.Name = name;
            }
            rule.Trigger = trigger;
            rule.Hotkey = hotkey;
            rule.TriggerApp = trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppSwitch or AutoRuleTrigger.AppExit
                ? triggerApp ?? "" : "";
            rule.ScheduleMode = trigger == AutoRuleTrigger.Schedule ? scheduleMode : 0;
            rule.ScheduleTime = trigger == AutoRuleTrigger.Schedule ? scheduleTime : "";
            rule.ScheduleWeekdays = trigger == AutoRuleTrigger.Schedule ? scheduleWeekdays : new List<int>();
            // 落盘映射统一走 AutoRuleStep.ToPersisted()（含 LaunchItems / LaunchMode 与 ProgramPaths 兼容镜像），
            // 避免编辑器模型 → 存储模型的转换漏字段
            rule.Actions = _autoSteps.Select(s => s.ToPersisted()).ToList();
            if (rule.Actions.Count > 0)
            {
                var f = rule.Actions[0];
                rule.Action = f.Action;
                rule.TargetApp = f.TargetApp;
                rule.TargetDeviceId = f.TargetDeviceId;
                rule.Volume = f.Volume;
                rule.DelayMs = f.DelayMs;
                rule.OsdTitle = f.OsdTitle ?? "";
                rule.OsdText = f.OsdText ?? "";
                rule.ProgramPath = f.ProgramPaths.FirstOrDefault() ?? "";
            }
            rule.Enabled = true;
            AutoRuleStore.Save(rule);
            ((App)Application.Current).ReloadHotkeys();
            AutoEditCard.Visibility = Visibility.Collapsed;
            _autoCapturingHotkey = false;
            _ = RefreshAutoRulesAsync();
        }
    }
}

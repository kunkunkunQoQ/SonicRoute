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
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using SonicRoute.Core;
using SonicRoute.Core.Compat;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using Panel = System.Windows.Controls.Panel;
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
        private int _overviewRefreshRequest, _overviewStateRequest, _appsRefreshRequest, _appsStateRequest;
        private System.Windows.Threading.DispatcherTimer? _nameSaveTimer;
        private bool _nameSavePending, _deviceNamesPending;
        private bool _suppressSettings;
        private System.Windows.Threading.DispatcherTimer? _themeSaveTimer;
        private bool _themeSavePending;
        private System.Windows.Threading.DispatcherTimer? _osdPreviewTimer;
        private bool _osdPreviewPending;


        private bool _suppressFilter;
        private bool _suppressRename;
        private List<AppItem> _appItems = new();
        private List<AudioAppInfo> _settingsApps = new();
        private bool _settingsFilterBuilt;
        private string? _settingsFilterLanguage;
        private int _navigationVersion;
        private readonly AppConfig _config;
        // ===== 鑷姩鍖栬鍒欙紙鏋佺畝鑷姩鍖栭〉锛?=====
        private List<AudioAppInfo> _autoApps = new();
        private bool _autoRefreshInProgress;
        private bool _autoRefreshPending;
        private int _autoRuleRefreshRequest;
        private string? _autoRuleListSignature;
        private long _autoRulesRevision = -1;
        private List<AutoRule> _autoRules = new();
        private Dictionary<string, string> _autoRuleFingerprints = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AutoRuleRow> _autoRows = new(StringComparer.Ordinal);
        private sealed class AutoRuleRow
        {
            internal string Id = "";
            internal UIElement Element = null!;
            internal Button Run = null!;
            internal string Fingerprint = "";
            internal TextBlock Conflict = null!;
        }
        /// <summary>自动化页低频自动刷新应用列表（8s，仅页面可见时运行）。</summary>
        private System.Windows.Threading.DispatcherTimer? _autoRefreshTimer;
        /// <summary>当前已创建的步骤应用下拉（供慢刷新重填，RenderAutoSteps 重建时清理）。</summary>
        private readonly List<System.Windows.Controls.ComboBox> _autoStepAppCombos = new();
        private readonly Dictionary<System.Windows.Controls.ComboBox, EDataFlow> _autoStepDeviceCombos = new();
        private List<AudioDeviceInfo>? _autoOutputs, _autoInputs;
        private int _autoDeviceRefreshRequest;
        private string? _autoEditingId;
        private bool _autoCapturingHotkey;
        private string _autoHotkeyCombo = "";
        private bool _suppressAutoUi;

        public MainWindow()
        {
            InitializeComponent();
            OverviewAppCombo.IsSynchronizedWithCurrentItem = false;
            AppsListBox.IsSynchronizedWithCurrentItem = false;
            VolumePresets.Attach(OverviewVolumeSlider);
            VolumePresets.Attach(AppsVolumeSlider);
            AppsRenameBox.LostKeyboardFocus += NameEdit_LostKeyboardFocus;
            FixedAppCombo.DropDownOpened += AppCombo_DropDownOpened;
            AutoTriggerAppCombo.DropDownOpened += AppCombo_DropDownOpened;
            Title = $"SonicRoute {App.DisplayVersion}";
            if (HeaderTitleText != null)
                HeaderTitleText.Text = $"SonicRoute {App.DisplayVersion}";
            _config = ConfigService.Load();
            InitializeAutoExtras();
            Loaded += async (_, _) =>
            {
                await LoadDevicesAsync();
                if (_isClosed) return;
                if (!_isClosed && OverviewPage.Visibility == Visibility.Visible)
                    await RefreshOverviewAsync();
                SyncOsdSliders();
                // 实验 UI（概览/应用/设置输入区/名称区）可见性：麦克风选项开启时显示
                ApplyExpMicUi(_config.ExperimentalMic);
                NavExperimental.Visibility = _config.ExperimentalUnlocked && _config.ExperimentalMode
                    ? Visibility.Visible : Visibility.Collapsed;
            };
            // 共享"当前应用"变化（前台自动跟随/面板切换）时同步概览
            CurrentAppService.CurrentChanged += OnSharedCurrentChanged;
            Closed += (_, _) => CleanupClosedWindow();
            StateChanged += AutoRefreshWindowStateChanged;
            IsVisibleChanged += AutoRefreshVisibilityChanged;
            // 快捷键内联录音：在窗口内直接捕获按键，免弹窗
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            Deactivated += (_, _) => { EndAutoDrag(commit: false); EndAutomationRuleDrag(false); };
            AutoStepsHost.SizeChanged += (_, _) => { if (_autoDragging) EndAutoDrag(commit: false); };
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
            if (_automationRuleGrip != null && e.Key == Key.Escape)
            {
                EndAutomationRuleDrag(false);
                e.Handled = true;
                return;
            }
            if (_autoGrip != null && e.Key == Key.Escape)
            {
                EndAutoDrag(commit: false);
                e.Handled = true;
                return;
            }
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
                if (_isClosed || !IsLoaded || OverviewPage.Visibility != Visibility.Visible) return;
                var cur = CurrentAppService.Current;
                if (cur == null) return;
                if (_overviewApp != null && _overviewApp.ProcessId == cur.ProcessId) return;
                ++_overviewRefreshRequest;
                await SetOverviewAppAsync(cur);
                if (_isClosed || _overviewApp?.ProcessId != cur.ProcessId) return;
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
                WindowShadow.CornerRadius = new CornerRadius(0);
                RootBorder.Margin = new Thickness(0);
                WindowShadow.Effect = null;
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
                WindowShadow.CornerRadius = new CornerRadius(10);
                RootBorder.Margin = new Thickness(0);
                WindowShadow.Effect = new DropShadowEffect
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
            int navigationVersion = ++_navigationVersion;
            FinishPanelAnimations();
            if (tag != "Automation") StopAutoRefresh();
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
            else if (tag == "Settings")
            {
                LoadSettings();
                _ = RefreshSettingsAppsAsync(navigationVersion);
            }
            else if (tag == "Experimental") LoadExperimentalSettings();
            else if (tag == "Automation") ShowAutomationPage(navigationVersion);
        }

        // ==================================================================
        // 设备加载（自定义名称 + 筛选 + 名称编辑）
        // ==================================================================

        private async Task LoadDevicesAsync()
        {
            if (_isClosed) return;
            // 输出/输入两组并行枚举（各自含设备列表 + 默认设备），互不依赖
            var outTask = _uiLifetime.ReadAsync(() =>
            {
                var outputs = AudioService.GetDevices(EDataFlow.eRender);
                string? defOut = AudioService.GetDefaultDeviceId(EDataFlow.eRender);
                foreach (var d in outputs) d.IsDefault = string.Equals(d.Id, defOut, StringComparison.OrdinalIgnoreCase);
                return outputs;
            });
            var inTask = _uiLifetime.ReadAsync(() =>
            {
                var inputs = AudioService.GetDevices(EDataFlow.eCapture);
                string? defIn = AudioService.GetDefaultDeviceId(EDataFlow.eCapture);
                foreach (var d in inputs) d.IsDefault = string.Equals(d.Id, defIn, StringComparison.OrdinalIgnoreCase);
                return inputs;
            });

            List<AudioDeviceInfo> outputs;
            try { outputs = await outTask; }
            catch (OperationCanceledException) when (_isClosed) { return; }
            if (_isClosed) return;
            _outputs = outputs;

            // 输入设备（麦克风）：仅实验模式 + 麦克风选项使用（快速切换当前应用麦克风设备/保留设置）
            try
            {
                _inputs = await inTask;
            }
            catch { _inputs = new List<AudioDeviceInfo>(); }
            if (_isClosed) return;
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


        private IEnumerable<AudioDeviceInfo> VisibleOutputs =>
            _outputs.Where(d => !_config.HiddenOutputDevices.Contains(d.Id));

        private IEnumerable<AudioDeviceInfo> VisibleInputs =>
            _inputs.Where(d => !_config.HiddenInputDevices.Contains(d.Id));

        /// <summary>刷新所有设备下拉 / 快速按钮 / 名称编辑列表的显示。</summary>
        private void ReloadDeviceDisplay()
        {
            RefreshDeviceDisplays();
            InvalidateSettingsDeviceSections();
            EnsureSettingsDeviceSections();
        }

        /// <summary>仅刷新设备显示（下拉/快捷按钮），不重建名称编辑输入框。
        /// 名称输入时调用它而不是 ReloadDeviceDisplay：每敲一个字就重建 TextBox 会
        /// 导致输入框失焦、中文输入法组合中断（用户需要每字重新点一下的 bug 根因）。</summary>
        private void RefreshDeviceDisplays()
        {
            string? overviewId = (OverviewOutputCombo.SelectedItem as AudioDeviceInfo)?.Id;
            string? appsId = (AppsOutputCombo.SelectedItem as AudioDeviceInfo)?.Id;
            _outputDisplay = PanelDevices.WithSystemDefault(DisplayDevices(_outputs), EDataFlow.eRender, _config);
            bool suppressed = _suppressDevCombo;
            _suppressDevCombo = true;
            try
            {
                OverviewOutputCombo.ItemsSource = _outputDisplay;
                AppsOutputCombo.ItemsSource = _outputDisplay;
                if (overviewId != null) OverviewOutputCombo.SelectedItem = _outputDisplay.FirstOrDefault(d => d.Id == overviewId);
                if (appsId != null) AppsOutputCombo.SelectedItem = _outputDisplay.FirstOrDefault(d => d.Id == appsId);
            }
            finally { _suppressDevCombo = suppressed; }
        }

        // ==================================================================
        // 概览页：默认应用解析 + 应用切换器
        // ==================================================================

        private async Task RefreshOverviewAsync(bool force = false)
        {
            if (_isClosed) return;
            try
            {
                int request = ++_overviewRefreshRequest;
                var apps = await _uiLifetime.ReadAsync(() => AudioService.GetApps(force));
                if (_isClosed || request != _overviewRefreshRequest) return;
                var items = GetAppCandidates(apps);
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
                if (_isClosed || request != _overviewRefreshRequest) return;
                if (target != null)
                {
                    _suppressAppCombo = true;
                    OverviewAppCombo.SelectedItem = items.FirstOrDefault(i => i.ProcessId == (int)target.ProcessId);
                    _suppressAppCombo = false;
                }
                await SetOverviewAppAsync(target);

            }
            catch (OperationCanceledException) when (_isClosed) { }
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
                ++_overviewRefreshRequest; // 用户选择优先于尚未返回的列表刷新。
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
            if (_isClosed) return;
            try
            {
                int request = ++_overviewStateRequest;
                uint? targetPid = _overviewApp?.ProcessId;
                var outs = PanelDevices.WithSystemDefault(DisplayDevices(VisibleOutputs), EDataFlow.eRender, _config);
                // 输入下拉框显示全部设备（与输出下拉一致，不受「保留的设备」筛选影响）
                _inputDisplay = PanelDevices.WithSystemDefault(DisplayDevices(_inputs), EDataFlow.eCapture, _config);
                OverviewInputCombo.ItemsSource = null;
                OverviewInputCombo.ItemsSource = _inputDisplay;

                if (_overviewApp == null)
                {
                    bool globalMicMuted = await _uiLifetime.ReadAsync(() => GlobalMicMuteService.IsMuted());
                    if (_isClosed || request != _overviewStateRequest || targetPid != _overviewApp?.ProcessId) return;
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
                var tMic = _uiLifetime.ReadAsync(() => GlobalMicMuteService.IsMuted());
                var tOut = _uiLifetime.ReadAsync(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender));
                var tIn = _uiLifetime.ReadAsync(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eCapture));
                var tVol = _uiLifetime.ReadAsync(() => SessionVolumeService.GetVolumePercent(pid));
                var tMuted = _uiLifetime.ReadAsync(() => SessionVolumeService.IsMuted(pid));
                await Task.WhenAll(tMic, tOut, tIn, tVol, tMuted);
                if (_isClosed || request != _overviewStateRequest || targetPid != _overviewApp?.ProcessId) return;
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
            catch (OperationCanceledException) when (_isClosed) { }
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
            int pid = (int)_overviewApp.ProcessId;
            bool muted = await Task.Run(() => SessionVolumeService.ToggleMute(pid));
            if (_isClosed || _overviewApp?.ProcessId != (uint)pid) return;
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
            ScheduleNameSave(deviceNames: false);

            AppsDetailTitle.Text = AppDisplayName.Get(_appsSelected);
            foreach (var it in _appItems)
                if (string.Equals(it.ProcessName, pn, StringComparison.OrdinalIgnoreCase))
                    it.RefreshName();
        }

        private async Task LoadAppsAsync()
        {
            if (_isClosed) return;
            try
            {
                int request = ++_appsRefreshRequest;
                var apps = await _uiLifetime.ReadAsync(() => AudioService.GetApps());
                if (_isClosed || request != _appsRefreshRequest) return;
                if (SameAutoApps(_appItems.Select(item => item.Info).ToList(), apps))
                {
                    foreach (var item in _appItems) { item.RefreshName(); item.RefreshAutoSwitchState(); }
                    if (_appsSelected != null) await RefreshAppsSelectionAsync();
                    return;
                }
                int? selectedPid = (AppsListBox.SelectedItem as AppItem)?.ProcessId;
                _appItems = GetAppCandidates(apps);
                AppItem.LoadIconsAsync(_appItems);
                foreach (var item in _appItems) item.RefreshAutoSwitchState();
                AppsListBox.ItemsSource = null;
                AppsListBox.ItemsSource = _appItems;
                if (selectedPid.HasValue) AppsListBox.SelectedItem = _appItems.FirstOrDefault(item => item.ProcessId == selectedPid.Value);

            }
            catch (OperationCanceledException) when (_isClosed) { }
        }

        private async void AppsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ++_appsStateRequest;
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

            UpdateAppsDisableAutoButton();
            UpdateAppsShowInPanelButton();
            await RefreshAppsSelectionAsync();
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


        /// <summary>切换选中应用的"在快速面板显示"状态（隐藏的应用不出现在简洁/应用面板列表/下拉）。</summary>
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
            int pid = (int)_appsSelected.ProcessId;
            bool muted = await Task.Run(() => SessionVolumeService.ToggleMute(pid));
            if (_isClosed || _appsSelected?.ProcessId != (uint)pid) return;
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
            if (_isClosed) return;
            try
            {
                int request = ++_appsStateRequest;
                var app = _appsSelected;
                if (app == null) return;
                var pid = (int)app.ProcessId;
                var output = _uiLifetime.ReadAsync(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender));
                var input = _uiLifetime.ReadAsync(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eCapture));
                var volume = _uiLifetime.ReadAsync(() => SessionVolumeService.GetVolumePercent(pid));
                var mute = _uiLifetime.ReadAsync(() => SessionVolumeService.IsMuted(pid));
                await Task.WhenAll(output, input, volume, mute);
                if (_isClosed || request != _appsStateRequest || _appsSelected?.ProcessId != app.ProcessId) return;
                var outId = output.Result;
                string? outShort = outId == null ? null : AudioPolicyConfig.UnpackDeviceId(outId);
                _suppressDevCombo = true;
                AppsOutputCombo.SelectedItem = outShort == null
                                               ? (_outputDisplay.FirstOrDefault(d => AudioService.IsSystemDefault(d.Id)) ?? _outputDisplay.FirstOrDefault(d => d.IsDefault) ?? _outputDisplay.FirstOrDefault())
                                               : _outputDisplay.FirstOrDefault(d => string.Equals(d.Id, outShort, StringComparison.OrdinalIgnoreCase))
                                                 ?? _outputDisplay.FirstOrDefault(d => d.IsDefault) ?? _outputDisplay.FirstOrDefault();
                _suppressDevCombo = false;
                // 输入设备（麦克风）：与输出一致
                var inId = input.Result;
                string? inShort = inId == null ? null : AudioPolicyConfig.UnpackDeviceId(inId);
                _suppressDevCombo = true;
                AppsInputCombo.ItemsSource = null;
                AppsInputCombo.ItemsSource = _inputDisplay;
                AppsInputCombo.SelectedItem = inShort == null
                                              ? (_inputDisplay.FirstOrDefault(d => AudioService.IsSystemDefault(d.Id)) ?? _inputDisplay.FirstOrDefault(d => d.IsDefault) ?? _inputDisplay.FirstOrDefault())
                                              : _inputDisplay.FirstOrDefault(d => string.Equals(d.Id, inShort, StringComparison.OrdinalIgnoreCase))
                                                ?? _inputDisplay.FirstOrDefault(d => d.IsDefault) ?? _inputDisplay.FirstOrDefault();
                _suppressDevCombo = false;
                int vol = volume.Result;
                bool muted = mute.Result;
                SetAppsVolumeUi(vol >= 0 ? vol : null);
                ApplyAppsMuteVisual(muted);

            }
            catch (OperationCanceledException) when (_isClosed) { }
        }

        // ==================================================================
        // 设置页：保留设备筛选
        // ==================================================================

        private void BuildDeviceFilter()
        {
            OutputFilterList.Items.Clear();
            AddFilterCheckBox(OutputFilterList, new AudioDeviceInfo { Id = AudioService.SystemDefaultDeviceId, DisplayName = L10n.T("Dev.SystemDefaultOut"), Flow = EDataFlow.eRender }, _config.HiddenOutputDevices, DeviceFilterChanged);
            foreach (var device in _outputs) AddFilterCheckBox(OutputFilterList, device, _config.HiddenOutputDevices, DeviceFilterChanged);
            InputFilterList.Items.Clear();
            AddFilterCheckBox(InputFilterList, new AudioDeviceInfo { Id = AudioService.SystemDefaultInputDeviceId, DisplayName = L10n.T("Dev.SystemDefaultIn"), Flow = EDataFlow.eCapture }, _config.HiddenInputDevices, InputFilterChanged);
            foreach (var device in _inputs) AddFilterCheckBox(InputFilterList, device, _config.HiddenInputDevices, InputFilterChanged);
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
        private void DeviceName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox tb || tb.Tag is not string id) return;
            var name = tb.Text.Trim();
            if (string.IsNullOrEmpty(name)) _config.DeviceNames.Remove(id);
            else _config.DeviceNames[id] = name;
            ScheduleNameSave(deviceNames: true);
        }

        private void ScheduleNameSave(bool deviceNames)
        {
            _nameSavePending = true;
            _deviceNamesPending |= deviceNames;
            if (_nameSaveTimer == null)
            {
                _nameSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _nameSaveTimer.Tick += NameSaveTimer_Tick;
            }
            _nameSaveTimer.Stop();
            _nameSaveTimer.Start();
        }

        private void NameSaveTimer_Tick(object? sender, EventArgs e) => FlushNameChanges(refreshDisplay: true);
        private void NameEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => FlushNameChanges(refreshDisplay: true);

        private void FlushNameChanges(bool refreshDisplay)
        {
            _nameSaveTimer?.Stop();
            if (_nameSaveTimer != null) _nameSaveTimer.Tick -= NameSaveTimer_Tick;
            _nameSaveTimer = null;
            if (_nameSavePending) { _nameSavePending = false; ConfigService.Save(_config); }
            bool devices = _deviceNamesPending;
            _deviceNamesPending = false;
            if (devices && refreshDisplay && !_isClosed)
            {
                RefreshDeviceDisplays();
                _ = RefreshOverviewDevicesVolumeAsync();
            }
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

                // 默认应用
                SetRadioByTag(DefaultAppRecent, DefaultAppLast, DefaultAppFixed, _config.DefaultAppMode);
                if (FixedAppCombo.ItemsSource == null && _settingsApps.Count > 0)
                    BindSettingsApps(_settingsApps);
                else
                    SelectSettingsApp();
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
                    if (packaged) SetPanelExpanded(SettingsMorePanel, false, false);
                }
                SettingsStartMinimized.IsChecked = _config.StartMinimized;
                SettingsShowPanelOnStart.IsChecked = _config.StartPanelOnStart;
                SettingsPanelChangeSysDef.IsChecked = _config.PanelChangeSystemDefault;
                // 快速面板样式：应用面板 / 快捷面板（默认简洁）
                QuickPanelStyleCombo.ItemsSource = new[] { L10n.T("St.PanelClassic"), L10n.T("St.PanelModern") };
                QuickPanelStyleCombo.SelectedIndex = _config.QuickPanelStyle == "classic" ? 0 : 1;
                // 快捷面板更改系统默认设备：仅"快捷面板"样式时显示
                PanelChangeSysDefSection.Visibility = _config.QuickPanelStyle == "classic"
                    ? Visibility.Collapsed : Visibility.Visible;
                // 快捷面板高度设置：应用面板不显示
                PanelHeightSection.Visibility = _config.QuickPanelStyle == "classic"
                    ? Visibility.Collapsed : Visibility.Visible;
                // 快捷面板固定高度（px，350–800，默认 450）
                PanelHeightSlider.Value = MathEx.Clamp(_config.QuickPanelHeight, 350, 800);
                PanelHeightValue.Text = MathEx.Clamp(_config.QuickPanelHeight, 350, 800) + " px";
                VolumeStepBox.Text = MathEx.Clamp(_config.VolumeStep, 1, 20).ToString();
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

        private async Task RefreshSettingsAppsAsync(int navigationVersion)
        {
            if (_isClosed) return;
            try
            {
                var apps = await _uiLifetime.ReadAsync(() => AudioService.GetApps());
                if (_isClosed || navigationVersion != _navigationVersion
                    || SettingsPage.Visibility != Visibility.Visible) return;
                if (SameAutoApps(_settingsApps, apps) && FixedAppCombo.ItemsSource != null) return;
                await _uiLifetime.Post(Dispatcher, () => { }).Task;
                if (_isClosed || navigationVersion != _navigationVersion
                    || SettingsPage.Visibility != Visibility.Visible) return;
                _settingsApps = apps;
                BindSettingsApps(apps);
            }
            catch { /* 单次音频枚举失败不影响设置页 */ }

        }

        private void BindSettingsApps(List<AudioAppInfo> apps)
        {
            var items = GetAppCandidates(apps);
            _suppressAppCombo = true;
            try
            {
                FixedAppCombo.IsSynchronizedWithCurrentItem = false;
                FixedAppCombo.ItemsSource = items;
                FixedAppCombo.SelectedItem = items.FirstOrDefault(a =>
                    string.Equals(a.ProcessName, _config.FixedAppName, StringComparison.OrdinalIgnoreCase));
            }
            finally { _suppressAppCombo = false; }
            LoadSelectedAppIcon(FixedAppCombo);
        }

        private void SelectSettingsApp()
        {
            if (FixedAppCombo.ItemsSource is not IEnumerable<AppItem> items) return;
            _suppressAppCombo = true;
            try
            {
                FixedAppCombo.SelectedItem = items.FirstOrDefault(a =>
                    string.Equals(a.ProcessName, _config.FixedAppName, StringComparison.OrdinalIgnoreCase));
            }
            finally { _suppressAppCombo = false; }
            LoadSelectedAppIcon(FixedAppCombo);
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
                InvalidateSettingsDeviceSections();
                EnsureSettingsDeviceSections();
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
                OpacitySlider.Value = MathEx.Clamp(_config.BackgroundOpacity, 0, 100);
                OpacityText.Text = $"{_config.BackgroundOpacity}%";

                string accent = _config.Accent ?? "blue";
                SyncThemeColorUi(accent);
                AppPeakMeterCheck.IsChecked = _config.ShowAppPeakMeter;

                // 快速面板高度（快捷面板固定高度，350–800，默认 450）：
                // 该设置位于主题页，必须在 LoadTheme 初始化，否则会显示 XAML 硬编码初值；应用面板不显示
                PanelHeightSection.Visibility = _config.QuickPanelStyle == "classic"
                    ? Visibility.Collapsed : Visibility.Visible;
                PanelHeightSlider.Value = MathEx.Clamp(_config.QuickPanelHeight, 350, 800);
                PanelHeightValue.Text = MathEx.Clamp(_config.QuickPanelHeight, 350, 800) + " px";
            }
            finally
            {
                _suppressSettings = false;
            }
        }


        private void ApplyThemePreview()
        {
            ThemeService.Apply(_config.ThemeMode, _config.Accent);
            foreach (var item in _appItems) item.RefreshAutoSwitchState();
        }


        private void ScheduleThemeSave()
        {
            _themeSavePending = true;
            if (_themeSaveTimer == null)
            {
                _themeSaveTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(200) };
                _themeSaveTimer.Tick += ThemeSaveTimer_Tick;
            }
            _themeSaveTimer.Stop();
            _themeSaveTimer.Start();
        }

        private void ThemeSaveTimer_Tick(object? sender, EventArgs e)
        {
            _themeSaveTimer?.Stop();
            if (!_themeSavePending) return;
            _themeSavePending = false;
            ConfigService.Save(_config);
        }

        internal void FlushPendingSettings()
        {
            FlushNameChanges(refreshDisplay: false);
            FlushThemeChanges();
        }

        private void FlushThemeChanges()
        {
            _themeSaveTimer?.Stop();
            _osdPreviewTimer?.Stop();
            if (_osdPreviewPending)
            {
                _osdPreviewPending = false;
                ((App)Application.Current).ApplyOsdSize(_config.OsdWidth, _config.OsdFontScale);
            }
            if (_themeSavePending)
            {
                _themeSavePending = false;
                ConfigService.Save(_config);
            }
            if (_themeSaveTimer != null) _themeSaveTimer.Tick -= ThemeSaveTimer_Tick;
            _themeSaveTimer = null;
            if (_osdPreviewTimer != null) _osdPreviewTimer.Tick -= OsdPreviewTimer_Tick;
            _osdPreviewTimer = null;
        }

        private void ScheduleOsdPreview()
        {
            _osdPreviewPending = true;
            if (_osdPreviewTimer == null)
            {
                _osdPreviewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                _osdPreviewTimer.Tick += OsdPreviewTimer_Tick;
            }
            if (!_osdPreviewTimer.IsEnabled) _osdPreviewTimer.Start();
        }

        private void OsdPreviewTimer_Tick(object? sender, EventArgs e)
        {
            _osdPreviewTimer?.Stop();
            if (!_osdPreviewPending || _isClosed) return;
            _osdPreviewPending = false;
            ((App)Application.Current).SetOsdSize(_config.OsdWidth, _config.OsdFontScale);
        }

        private void Theme_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            var mode = (new[] { ThemeSystem, ThemeLight, ThemeDark })
                .FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "system";
            _config.ThemeMode = mode;
            _themeSaveTimer?.Stop();
            _themeSavePending = false;
            ConfigService.Save(_config);
            ApplyThemePreview();
        }


        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _suppressSettings) return;
            int v = (int)Math.Round(e.NewValue);
            OpacityText.Text = $"{v}%";
            if (_config.BackgroundOpacity == v) return;
            _config.BackgroundOpacity = v;
            ThemeService.ApplyBackgroundOpacity(v);
            ScheduleThemeSave();
        }

        // ==================================================================
        // 滚轮调音量：悬停在音量区（滑块/±键）时滚动滚轮
        // ==================================================================

        private void OverviewVolume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_suppressVolume || _overviewApp == null || !_overviewVolumeReady) return;
            int step = MathEx.Clamp(ConfigService.Load().VolumeStep, 1, 20);
            int pct = (int)Math.Round(OverviewVolumeSlider.Value) + (e.Delta > 0 ? step : -step);
            OverviewVolumeSlider.Value = MathEx.Clamp(pct, 0, 100);
            e.Handled = true;
        }

        private void AppsVolume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_suppressVolume || !_appsVolumeReady || _appsSelected == null) return;
            int step = MathEx.Clamp(ConfigService.Load().VolumeStep, 1, 20);
            int pct = (int)Math.Round(AppsVolumeSlider.Value) + (e.Delta > 0 ? step : -step);
            AppsVolumeSlider.Value = MathEx.Clamp(pct, 0, 100);
            e.Handled = true;
        }

        private void SettingsStartMinimized_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsSettingsChange(sender, e)) return;
            _config.StartMinimized = SettingsStartMinimized.IsChecked == true;
            ConfigService.Save(_config);
        }

        private void SettingsShowPanelOnStart_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsSettingsChange(sender, e)) return;
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
            int step = MathEx.Clamp(v, 1, 20);
            if (step != v) VolumeStepBox.Text = step.ToString();
            if (_config.VolumeStep == step) return;
            _config.VolumeStep = step;
            ConfigService.Save(_config);
        }

        /// <summary>托盘滚轮调音量区域开关：true=整个托盘通知区响应（默认）；false=仅音跃托盘图标上响应。</summary>
        private void SettingsTrayWheelEverywhere_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsSettingsChange(sender, e)) return;
            _config.TrayWheelEverywhere = SettingsTrayWheelEverywhere.IsChecked == true;
            ConfigService.Save(_config);
        }
        private void QuickPanelStyleCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings || QuickPanelStyleCombo.SelectedIndex < 0) return;
            _config.QuickPanelStyle = QuickPanelStyleCombo.SelectedIndex == 0 ? "classic" : "modern";
            ConfigService.Save(_config);
            // 快捷面板更改系统默认设备选项仅快捷面板样式显示
            PanelChangeSysDefSection.Visibility = _config.QuickPanelStyle == "classic"
                ? Visibility.Collapsed : Visibility.Visible;
            // 快捷面板高度设置仅快捷面板样式显示
            PanelHeightSection.Visibility = _config.QuickPanelStyle == "classic"
                ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>快捷面板更改系统默认设备开关（默认关）：开启后快捷面板下拉框切换设备 = 更改系统默认输出设备。</summary>
        private void SettingsPanelChangeSysDef_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsSettingsChange(sender, e)) return;
            _config.PanelChangeSystemDefault = SettingsPanelChangeSysDef.IsChecked == true;
            ConfigService.Save(_config);
        }

        /// <summary>检测当前是否运行在 MSIX 包中（非包环境调用 Package.Current 会抛异常）。
        /// net48 Legacy 为传统桌面程序，不依赖 WinRT / Windows App SDK，恒为 false。</summary>
        private static bool IsPackaged()
        {
#if NET48
            return false;
#else
            try
            {
                _ = Windows.ApplicationModel.Package.Current;
                return true;
            }
            catch
            {
                return false;
            }
#endif
        }

        /// <summary>开机自启：MSIX 环境用 StartupTask API，非 MSIX（绿色版）写注册表 Run 键。</summary>
#if NET48
        // net48 Legacy 无 MSIX 分支（方法内无 await 调用）：去掉 async 修饰符，避免 CS1998
        private void SettingsAutoStart_Changed(object sender, RoutedEventArgs e)
#else
        private async void SettingsAutoStart_Changed(object sender, RoutedEventArgs e)
#endif
        {
            if (!IsSettingsChange(sender, e)) return;
            bool on = SettingsAutoStart.IsChecked == true;
            if (IsPackaged()) _config.AutoStartStore = on; else _config.AutoStart = on;
            ConfigService.Save(_config);

#if NET48
            // net48 Legacy 无 MSIX / Store 形态：不做 Store 自启，恒走绿色版 Run 键分支
#else
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
#endif
            {
                // 绿色版：写 HKCU\...\Run
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    if (key == null) return;
                    if (on)
                    {
                        var exe = AppInfo.ExecutablePath;
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

        /// <summary>统一裁剪展开，内部内容保持自然高度；中途点击从当前状态反向播放。</summary>
        private static void AnimatePanelExpand(UIElement panel, bool expand)
        {
            SetPanelExpanded(panel, expand, true);
        }

        private static void SetPanelExpanded(UIElement panel, bool expand, bool animate)
        {
            if (panel == null) return;
            if (panel is FrameworkElement element && element.Parent is AnimatedExpandHost host)
                host.SetExpanded(expand, animate);
            else
                panel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FinishPanelAnimations()
        {
            foreach (var panel in new FrameworkElement[] { LangMorePanel, SettingsMorePanel, MicMorePanel,
                KeepDevicesBody, DeviceNamesBody, OsdSlidersPanel })
                if (panel?.Parent is AnimatedExpandHost host) host.FinishImmediately();
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
#if NET48
                // net48 Legacy 无 MSIX / Store 形态：只清理绿色版 Run 键
#else
                if (IsPackaged())
                {
                    var task = Windows.ApplicationModel.StartupTask.GetAsync("SonicRouteStartup").GetAwaiter().GetResult();
                    task.Disable();
                }
                else
#endif
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
            if (!IsSettingsChange(sender, e)) return;
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
                ExpBackgroundPollingCheck.IsChecked = _config.BackgroundPollingFallbackEnabled;

            }
            finally
            {
                _suppressSettings = false;
            }
        }

        private void ExpBackgroundPolling_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsSettingsChange(sender, e)) return;
            _config.BackgroundPollingFallbackEnabled = ExpBackgroundPollingCheck.IsChecked == true;
            ConfigService.Save(_config);
            ((App)Application.Current).RefreshBackgroundMonitoringPolicy();
        }

        private void ExpMicOption_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsSettingsChange(sender, e)) return;
            _config.ExperimentalMic = ExpMicOptionCheck.IsChecked == true;
            ConfigService.Save(_config);
            ApplyExpMicUi(_config.ExperimentalMic);
            ExpMicPanelCheck.Visibility = _config.ExperimentalMic ? Visibility.Visible : Visibility.Collapsed;
            ShowToast(L10n.T("St.ExpMicNeedRestart"));
        }

        private void ExpMicPanel_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsSettingsChange(sender, e)) return;
            _config.MicInPanel = ExpMicPanelCheck.IsChecked == true;
            ConfigService.Save(_config);
            ShowToast(L10n.T("St.ExpMicPanelNeedRestart"));
        }

        private void ExpCollapse_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsSettingsChange(sender, e)) return;
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
#if !NET48
                // UseDescriptionForTitle 是 .NET Core 3.0+ 新增属性；net48 无此属性，
                // net48 下 Description 作为对话框内文本显示（功能等价，仅标题栏文案位置不同）
                UseDescriptionForTitle = true,
#endif
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
            bool previous = _suppressSettings;
            _suppressSettings = true;
            try
            {
                LangCombo.ItemsSource = L10n.SupportedLanguages.Select(x => x.NativeName).ToList();
                int idx = Array.FindIndex(L10n.SupportedLanguages,
                    x => string.Equals(x.Code, selectCode, StringComparison.OrdinalIgnoreCase));
                LangCombo.SelectedIndex = idx < 0 ? 0 : idx;
            }
            finally { _suppressSettings = previous; }
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
                    Text = native,
                    Width = 140,
                    FontSize = 12,
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
                    Text = code,
                    FontSize = 11,
                    Foreground = (Brush)FindResource("Theme.TextSecondary"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                codeText.ToolTip = isCurrent ? L10n.T("Exp.LangCurrent") : code;
                codePart.Children.Add(codeText);
                var rename = new Button
                {
                    Content = L10n.T("Exp.LangSave"),
                    Style = (Style)FindResource("GhostButton"),
                    Height = 26,
                    MinWidth = 64,
                    Padding = new Thickness(8, 0, 8, 0),
                    Tag = code,
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
                    Content = L10n.T("Exp.LangDelete"),
                    Style = (Style)FindResource("GhostButton"),
                    Height = 26,
                    MinWidth = 64,
                    Padding = new Thickness(8, 0, 8, 0),
                    Margin = new Thickness(6, 0, 0, 0),
                    Tag = code,
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
                var exe = AppInfo.ExecutablePath ?? "SonicRoute.exe";
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
                SetPanelExpanded(KeepDevicesBody, false, false);
                SetPanelExpanded(DeviceNamesBody, false, false);
                KeepDevicesMoreToggle.IsChecked = false;
                DeviceNamesMoreToggle.IsChecked = false;
            }
            else
            {
                SetPanelExpanded(KeepDevicesBody, true, false);
                SetPanelExpanded(DeviceNamesBody, true, false);
            }
            EnsureSettingsDeviceSections();
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
            _config.OsdCustomPhysicalX = null;
            _config.OsdCustomPhysicalY = null;
            _config.OsdWidth = 240;
            _config.OsdFontScale = 1.0;
            _config.OsdFadeInMs = 100;
            _config.OsdFadeOutMs = 200;
            _osdPreviewTimer?.Stop();
            _osdPreviewPending = false;
            _themeSaveTimer?.Stop();
            _themeSavePending = false;
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
            if (_isClosed || _osdAdjustSubscribed) return;
            ((App)Application.Current).OsdAdjustFinished += OnOsdAdjustFinished;
            _osdAdjustSubscribed = true;
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
        /// <summary>主题页「快速面板高度」：快捷面板窗口固定高度（350–800px，默认 450），保存并立即应用（面板打开时）。</summary>
        private void PanelHeightSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _suppressSettings) return;
            int v = MathEx.Clamp((int)Math.Round(e.NewValue), 350, 800);
            if (PanelHeightValue != null) PanelHeightValue.Text = v + " px";
            _config.QuickPanelHeight = v;
            ConfigService.Save(_config);
            // 快捷面板打开时立即应用固定高度
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
            if (_isClosed || _panelPosAdjustSubscribed) return;
            ((App)Application.Current).QuickPanelAdjustFinished += OnPanelPositionAdjustFinished;
            _panelPosAdjustSubscribed = true;
        }


        /// <summary>主题页 - OSD 宽度滑条：实时调整 OSD 宽度并保存。</summary>
        private void OsdWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncingOsdSliders) return; // 初始化/一键还原同步滑条值时不弹「已保存」OSD
            if (OsdWidthValue == null || !IsLoaded) return;
            int w = (int)Math.Round(OsdWidthSlider.Value);
            OsdWidthValue.Text = w + "px";
            if (_config.OsdWidth == w) return;
            _config.OsdWidth = w;
            ScheduleThemeSave();
            ScheduleOsdPreview();
        }

        /// <summary>主题页 - OSD 字号倍率滑条：实时调整 OSD 字号并保存。</summary>
        private void OsdFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncingOsdSliders) return; // 初始化/一键还原同步滑条值时不弹「已保存」OSD
            if (OsdFontValue == null || !IsLoaded) return;
            double fs = Math.Round(OsdFontSlider.Value, 2);
            OsdFontValue.Text = (int)Math.Round(fs * 100) + "%";
            if (Math.Abs(_config.OsdFontScale - fs) <= 0.001) return;
            _config.OsdFontScale = fs;
            ScheduleThemeSave();
            ScheduleOsdPreview();
        }

        /// <summary>主题页 - OSD 淡入时长滑条：实时调整淡入动画时长并保存（0 = 禁用淡入直接显示）。</summary>
        private void OsdFadeInSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncingOsdSliders) return; // 初始化/一键还原同步滑条值时不触发保存
            if (OsdFadeInValue == null || !IsLoaded) return;
            int ms = (int)Math.Round(OsdFadeInSlider.Value);
            OsdFadeInValue.Text = ms + "ms";
            if (_config.OsdFadeInMs != ms) { _config.OsdFadeInMs = ms; ScheduleThemeSave(); }
        }

        /// <summary>主题页 - OSD 淡出时长滑条：实时调整淡出动画时长并保存（0 = 禁用淡出直接隐藏）。</summary>
        private void OsdFadeOutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncingOsdSliders) return;
            if (OsdFadeOutValue == null || !IsLoaded) return;
            int ms = (int)Math.Round(OsdFadeOutSlider.Value);
            OsdFadeOutValue.Text = ms + "ms";
            if (_config.OsdFadeOutMs != ms) { _config.OsdFadeOutMs = ms; ScheduleThemeSave(); }
        }

        private void OsdVolumeVisual_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingOsdSliders || !IsLoaded || OsdVolumeVisualCheck == null) return;
            bool enabled = OsdVolumeVisualCheck.IsChecked == true;
            if (_config.OsdVolumeVisualEnabled == enabled) return;
            _config.OsdVolumeVisualEnabled = enabled;
            ConfigService.Save(_config);
            ((App)Application.Current).NotifyOsdVolumeVisualChanged();
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
            if (OsdVolumeVisualCheck != null) OsdVolumeVisualCheck.IsChecked = _config.OsdVolumeVisualEnabled;
            _syncingOsdSliders = false;
            OsdWidthValue.Text = _config.OsdWidth + "px";
            OsdFontValue.Text = (int)Math.Round(_config.OsdFontScale * 100) + "%";
            if (OsdFadeInValue != null) OsdFadeInValue.Text = _config.OsdFadeInMs + "ms";
            if (OsdFadeOutValue != null) OsdFadeOutValue.Text = _config.OsdFadeOutMs + "ms";
            if (MicMutePersistCheck != null) MicMutePersistCheck.IsChecked = _config.MicMuteOsdPersistent;
            if (MicMuteTrackInputCheck != null) MicMuteTrackInputCheck.IsChecked = _config.MicMuteOsdTrackInputMuted;
            if (MicMuteTrackInputCheck != null) ApplyMicMutePersistUi(_config.MicMuteOsdPersistent);
        }

        /// <summary>常驻开关控制监听子项的可见性；OSD 参数共用更多选项。</summary>
        private void ApplyMicMutePersistUi(bool on)
        {
            // 主开关关闭时隐藏监听子项，OSD 参数共用一个更多选项入口。
            if (MicMuteTrackInputCheck != null)
                MicMuteTrackInputCheck.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>折叠/展开设置页"保留的设备"卡片（实验设置-折叠开启时可见）。</summary>
        /// <summary>折叠/展开设置页"保留的设备"卡片（更多选项样式，实验设置-折叠开启时可见）。</summary>
        private void KeepDevicesMoreToggle_Click(object sender, RoutedEventArgs e)
        {
            EnsureSettingsDeviceSections();
            AnimatePanelExpand(KeepDevicesBody, KeepDevicesMoreToggle.IsChecked == true);
        }

        /// <summary>折叠/展开设置页"设备名称"卡片（更多选项样式，实验设置-折叠开启时可见）。</summary>
        private void DeviceNamesMoreToggle_Click(object sender, RoutedEventArgs e)
        {
            EnsureSettingsDeviceSections();
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

        private void AppPeakMeter_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings || AppPeakMeterCheck == null) return;
            bool enabled = AppPeakMeterCheck.IsChecked == true;
            if (_config.ShowAppPeakMeter == enabled) return;
            _config.ShowAppPeakMeter = enabled;
            ConfigService.Save(_config);
            ((App)Application.Current).ApplyQuickPanelMeterSetting(enabled);
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
                using var resp = await _updateHttp.GetAsync("https://api.github.com/repos/kunkunkunQoQ/SonicRoute/releases/latest", _uiLifetime.Token);
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
            if (_isClosed) return;
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
        private readonly Dictionary<AutoRuleStep, TextBlock> _autoStepNumbers = new();
        private readonly HashSet<string> _autoRunIds = new(StringComparer.Ordinal);

        // 操作积木拖拽排序状态
        private int _autoDragFrom = -1;
        private AutoRuleStep? _autoDragStep;
        private bool _autoDragging;
        private double _autoDragStartY;
        private int _autoDragTarget = -1;
        private FrameworkElement? _autoGrip;
        private readonly List<AutoDragRow> _autoDragRows = new();
        private readonly List<AutoDragRow> _autoDropSlides = new();
        private bool _autoDragFrameQueued;
        private TimeSpan _autoDragLastFrame;
        private double _autoDragHeight;
        private double _autoDragLastPointerY, _autoDragDistance;
        private System.Windows.Threading.DispatcherOperation? _autoDropOperation;
        private int _autoDragGeneration;
        private static readonly QuadraticEase AutoDragEase = CreateAutoDragEase();

        private sealed class AutoDragRow
        {
            internal Border Block = null!;
            internal FrameworkElement Container = null!;
            internal readonly TranslateTransform Slide = new();
            internal readonly TransformGroup Motion = new();
            internal Transform OriginalTransform = Transform.Identity;
            internal double Top, Height, Target;
            internal int AnimationVersion;
        }

        private static QuadraticEase CreateAutoDragEase()
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            return ease;
        }

        private void ShowAutomationPage(int navigationVersion)
        {
            FillAutoCombos();
            RefreshAutomationEditorPalette();
            if (SortAutomationRules()) _autoRuleListSignature = null;
            _ = RefreshAutoRulesAsync();
            StartAutoRefresh();
            _ = RefreshAutoAppsSlowAsync(navigationVersion, ensureFresh: true);
            _ = RefreshAutoDevicesAsync(navigationVersion);
        }

        private async Task RefreshAutoDevicesAsync(int navigationVersion)
        {
            if (_isClosed) return;
            int request = ++_autoDeviceRefreshRequest;
            try
            {
                var output = _uiLifetime.ReadAsync(() => AudioService.GetDevices(EDataFlow.eRender));
                var input = _uiLifetime.ReadAsync(() => AudioService.GetDevices(EDataFlow.eCapture));
                await Task.WhenAll(output, input);
                if (_isClosed || request != _autoDeviceRefreshRequest || navigationVersion != _navigationVersion
                    || AutomationPage.Visibility != Visibility.Visible) return;
                _autoOutputs = output.Result;
                _autoInputs = input.Result;
                foreach (var pair in _autoStepDeviceCombos)
                {
                    var combo = pair.Key;
                    var step = (AutoRuleStep)combo.Tag;
                    // 重填期间解绑选中写回，保留尚未连接设备的配置 ID。
                    _suppressAutoUi = true;
                    try
                    {
                        combo.Items.Clear();
                        foreach (var device in DisplayDevices(pair.Value == EDataFlow.eRender ? _autoOutputs : _autoInputs)) combo.Items.Add(device);
                        SelectAutoDevice(combo, step.TargetDeviceId);
                    }
                    finally { _suppressAutoUi = false; }
                }
            }
            catch { /* 保留上一份可用设备快照。 */ }

        }

        /// <summary>启动自动化页应用列表低频刷新（8s 一次，离开页面自动停止）。</summary>
        private void StartAutoRefresh()
        {
            if (!AutoRefreshWindowVisible) return;
            if (_autoRefreshTimer != null) return;
            _autoRefreshTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(8) };
            _autoRefreshTimer.Tick += async (_, _) => await RefreshAutoAppsSlowAsync(_navigationVersion);
            _autoRefreshTimer.Start();
        }

        private bool AutoRefreshWindowVisible => !_isClosed && IsVisible && WindowState != WindowState.Minimized
            && AutomationPage.Visibility == Visibility.Visible;

        private void AutoRefreshWindowStateChanged(object? sender, EventArgs e) => UpdateAutoRefreshVisibility();
        private void AutoRefreshVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateAutoRefreshVisibility();
        private void UpdateAutoRefreshVisibility()
        {
            if (!AutoRefreshWindowVisible) { StopAutoRefresh(); return; }
            StartAutoRefresh();
            _ = RefreshAutoAppsSlowAsync(_navigationVersion, ensureFresh: true);
        }

        private void StopAutoRefresh()
        {
            EndAutoDrag(commit: false);
            EndAutomationRuleDrag(false);
            _autoRefreshPending = false;
            if (_autoRefreshTimer != null)
            {
                _autoRefreshTimer.Stop();
                _autoRefreshTimer = null;
            }
        }

        /// <summary>慢速刷新应用列表：新启动 / 退出的应用自动出现在自动化下拉中（保留选中项）。</summary>
        private async Task RefreshAutoAppsSlowAsync(int navigationVersion, bool ensureFresh = false)
        {
            if (_isClosed) return;
            if (_isClosed || navigationVersion != _navigationVersion
                || !AutoRefreshWindowVisible) return;
            if (_autoRefreshInProgress)
            {
                if (ensureFresh) _autoRefreshPending = true;
                return;
            }
            _autoRefreshInProgress = true;
            try
            {
                var apps = await _uiLifetime.ReadAsync(() => AudioService.GetApps());
                if (_isClosed || navigationVersion != _navigationVersion
                    || !AutoRefreshWindowVisible) return;
                if (SameAutoApps(_autoApps, apps)) return;
                await _uiLifetime.Post(Dispatcher, () => { }).Task;
                if (_isClosed || navigationVersion != _navigationVersion
                    || !AutoRefreshWindowVisible) return;
                _autoApps = apps;

                // 触发应用下拉：重填并保留选中
                var prevTrigger = (AutoTriggerAppCombo.SelectedItem as AppItem)?.Info.ProcessName;
                LoadAutoAppCombo(AutoTriggerAppCombo);
                SelectAutoApp(AutoTriggerAppCombo, prevTrigger);

                // 已打开的步骤应用下拉：重填并保留选中
                foreach (var combo in _autoStepAppCombos)
                {
                    var prev = combo.Tag is AutoRuleStep step ? step.TargetApp : (combo.SelectedItem as AppItem)?.Info.ProcessName;
                    BindAutoAppCandidates(combo, prev);
                }
            }
            catch { /* 静默：单次刷新失败不影响页面 */ }
            finally
            {
                _autoRefreshInProgress = false;
                if (_autoRefreshPending && AutoRefreshWindowVisible)
                {
                    _autoRefreshPending = false;
                    _ = RefreshAutoAppsSlowAsync(_navigationVersion);
                }
            }

        }

        private static bool SameAutoApps(List<AudioAppInfo> current, List<AudioAppInfo> next)
        {
            if (current.Count != next.Count) return false;
            for (int i = 0; i < current.Count; i++)
            {
                var a = current[i];
                var b = next[i];
                if (a.ProcessId != b.ProcessId || a.HasActiveSession != b.HasActiveSession
                    || !string.Equals(a.ProcessName, b.ProcessName, StringComparison.Ordinal)
                    || !string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private async Task RefreshAutoRulesAsync()
        {
            if (_isClosed) return;
            int request = ++_autoRuleRefreshRequest;
            long revision = _autoRulesRevision;
            try
            {
                var result = await _uiLifetime.ReadAsync(() =>
                {
                    var snapshot = AutoRuleStore.ReadSnapshot(revision);
                    var fingerprints = snapshot.Rules?.ToDictionary(r => r.Id, r => JsonSerializer.Serialize(r), StringComparer.Ordinal);
                    return (snapshot.Revision, snapshot.Rules, Fingerprints: fingerprints);
                });
                if (_isClosed || request != _autoRuleRefreshRequest
                    || AutomationPage.Visibility != Visibility.Visible) return;
                if (result.Rules != null)
                {
                    EndAutomationRuleDrag(false);
                    _autoRules = result.Rules;
                    SortAutomationRules();
                    _autoRuleFingerprints = result.Fingerprints!;
                    _autoRulesRevision = result.Revision;
                }
                _autoRunIds.Clear();
                _autoRunIds.UnionWith(AutoRuleService.RunningRuleIds());
                string conflicts = string.Join(",", _autoRules.Where(IsAutoHotkeyConflict).Select(r => r.Id));
                string running = string.Join(",", _autoRunIds.OrderBy(id => id));
                string signature = L10n.CurrentLanguage + "\n" + conflicts + "\n" + running + "\n" + _autoRulesRevision;
                if (string.Equals(_autoRuleListSignature, signature, StringComparison.Ordinal)) return;
                await _uiLifetime.Post(Dispatcher, () => { }).Task;
                if (_isClosed || request != _autoRuleRefreshRequest
                    || AutomationPage.Visibility != Visibility.Visible) return;
                var ids = new HashSet<string>(_autoRules.Select(r => r.Id), StringComparer.Ordinal);
                foreach (string id in _autoRows.Keys.Where(id => !ids.Contains(id)).ToList())
                {
                    AutoRuleList.Items.Remove(_autoRows[id].Element);
                    _autoRows.Remove(id);
                }
                AutoEmptyText.Visibility = _autoRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                int built = 0;
                foreach (var rule in _autoRules)
                {
                    string fingerprint = L10n.CurrentLanguage + "\n" + _autoRuleFingerprints[rule.Id];
                    if (!_autoRows.TryGetValue(rule.Id, out var row) || row.Fingerprint != fingerprint)
                    {
                        if (row != null) AutoRuleList.Items.Remove(row.Element);
                        BuildAutoRuleRow(rule);
                        row = _autoRows[rule.Id];
                        row.Fingerprint = fingerprint;
                    }
                    bool executing = _autoRunIds.Contains(rule.Id);
                    UpdateAutomationRowState(row, executing);
                    row.Conflict.Visibility = IsAutoHotkeyConflict(rule) ? Visibility.Visible : Visibility.Collapsed;
                    int index = AutoRuleList.Items.IndexOf(row.Element);
                    if (index != built)
                    {
                        if (index >= 0) AutoRuleList.Items.RemoveAt(index);
                        AutoRuleList.Items.Insert(built, row.Element);
                    }
                    if (++built % 8 == 0 && built < _autoRules.Count)
                    {
                        await _uiLifetime.Post(Dispatcher, () => { }).Task;
                        if (_isClosed || request != _autoRuleRefreshRequest
                            || AutomationPage.Visibility != Visibility.Visible) return;
                    }
                }
                ApplyAutoRuleFilters();
                _autoRuleListSignature = signature;
            }
            catch { /* 单条规则文件异常不影响页面切换 */ }

        }

        /// <summary>主题强调色 RGB 反色（255 - 各通道），用于规则状态点。</summary>
        private Brush InvertAccentBrush() => ThemeService.GetInvertedAccentBrush();

        private string BuildAutoActionSummary(AutoRule r)
        {
            var steps = r.Actions.Count > 0 ? r.Actions : r.CloneSteps();
            var actionTexts = steps.Select(step =>
            {
                string text = L10n.T(AutoActionKey(step.Action));
                if (step.Action is AutoRuleAction.SetSystemMute or AutoRuleAction.SetAppMute or AutoRuleAction.SetGlobalMicMute)
                    text += "·" + L10n.T(step.Muted ? "Auto.MuteOn" : "Auto.MuteOff");
                if (step.Action is AutoRuleAction.AdjustSystemVolume or AutoRuleAction.AdjustAppVolume)
                    text += " " + (step.VolumeDelta > 0 ? "+" : "") + step.VolumeDelta + "%";
                if (step.Action is AutoRuleAction.SetSystemVolume or AutoRuleAction.SetAppVolume)
                    text += " " + step.Volume + "%";
                if (step.Action == AutoRuleAction.LaunchProgram && step.LaunchMode == 1)
                    text += " · " + L10n.T("Auto.LaunchModeRandom");
                return text;
            }).ToList();
            return string.Join("、", actionTexts);
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
            AutoEditTitle.Text = L10n.T("Auto.EditorNew");
            SetAutomationEditorVisible(true);
            RevealAutomationEditor();
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
            AutoTriggerCombo.SelectedItem = AutoTriggerCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is AutoRuleTrigger trigger && trigger == rule.Trigger);
            _autoHotkeyCombo = rule.Hotkey ?? "";
            _autoSteps = rule.CloneSteps();
            // 启动程序步骤统一规范化为启动项列表（旧配置由 ProgramPaths 转换，打开方式为空 = 默认方式）
            NormalizeLaunchSteps(_autoSteps);
            _suppressAutoUi = false;
            AutoScheduleModeCombo.SelectedIndex = MathEx.Clamp(rule.ScheduleMode, 0, 2);
            SetAutoScheduleTime(rule.ScheduleTime);
            SetAutoWeekday(rule.ScheduleWeekdays);
            UpdateAutoTriggerPanels();
            SelectAutoApp(AutoTriggerAppCombo, rule.TriggerApp);
            RenderAutoSteps();
            AutoEditTitle.Text = L10n.T("Auto.EditorEdit");
            SetAutomationEditorVisible(true);
            UpdateAutoHotkeyHint();
            RevealAutomationEditor();
        }

        private void AutoToggle_Click(object sender, RoutedEventArgs e)
        {
            var id = (string)((FrameworkElement)sender).Tag;
            var r = AutoRuleStore.Find(id);
            if (r == null) return;
            bool previous = r.Enabled;
            r.Enabled = sender is CheckBox toggle ? toggle.IsChecked == true : !r.Enabled;
            try { AutoRuleStore.Save(r); }
            catch
            {
                if (sender is CheckBox failedToggle) failedToggle.IsChecked = previous;
                ShowToast(L10n.T("Auto.SaveFailed"));
                return;
            }
            ((App)Application.Current).ReloadHotkeys();
            if (_autoEditingId == id)
            {
                _autoCapturingHotkey = false;
                SetAutomationEditorVisible(false);
            }
            _ = RefreshAutoRulesAsync();
        }

        private void AutoRun_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string id || _isClosed) return;
            var rule = AutoRuleStore.Find(id);
            if (rule == null) { ShowToast(L10n.T("Auto.RunFailed")); return; }
            if (AutoRuleService.IsRunning(id)) { ShowToast(L10n.T("Auto.Running")); return; }
            if (!_autoRunIds.Add(id)) return;
            SetAutomationRunState(button, true);
            UpdateAutomationSelection();
            // 长延时规则只持有窗口弱引用，关闭管理界面不拖住整个 UI 树。
            _ = RunAutoRuleAndReportAsync(new WeakReference<MainWindow>(this), rule);
        }

        private static async Task RunAutoRuleAndReportAsync(WeakReference<MainWindow> owner, AutoRule rule)
        {
            AutoRuleService.ExecutionResult result;
            try { result = await AutoRuleService.ExecuteWithResultAsync(rule); }
            catch { result = new AutoRuleService.ExecutionResult(0, 1); }
            if (!owner.TryGetTarget(out var window) || window._isClosed || window.Dispatcher.HasShutdownStarted) return;
            window._autoRunIds.Remove(rule.Id);
            string text = AutoRuleService.DescribeResult(result);
            window.ShowToast(text);
            window._autoRuleListSignature = null;
            _ = window.RefreshAutoRulesAsync();
        }

        private void AutoCopy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string id) return;
            var rule = AutoRuleStore.Find(id);
            if (rule == null) return;
            try
            {
                var occupied = new HashSet<string>(AutoRuleStore.LoadAll().Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
                string baseName = string.Format(L10n.T("Auto.CopyName"), rule.Name), name = baseName;
                for (int suffix = 2; occupied.Contains(name); suffix++) name = baseName + " (" + suffix + ")";
                AutoRuleStore.Save(rule.CopyAsNew(name));
                ((App)Application.Current).ReloadHotkeys();
                ShowToast(L10n.T("Auto.Copied"));
                _ = RefreshAutoRulesAsync();
            }
            catch { ShowToast(L10n.T("Auto.CopyFailed")); }
        }

        private void AutoDelete_Click(object sender, RoutedEventArgs e)
        {
            var id = (string)((FrameworkElement)sender).Tag;
            try { AutoRuleStore.Delete(id); }
            catch { ShowToast(L10n.T("Auto.DeleteFailed")); return; }
            ((App)Application.Current).ReloadHotkeys();
            if (_autoEditingId == id)
            {
                _autoCapturingHotkey = false;
                SetAutomationEditorVisible(false);
            }
            _ = RefreshAutoRulesAsync();
        }

        private void AutoCancel_Click(object sender, RoutedEventArgs e)
        {
            _autoCapturingHotkey = false;
            SetAutomationEditorVisible(false);
            UpdateAutomationSelection();
        }

        private void FillAutoCombos()
        {
            LoadAutoAppCombo(AutoTriggerAppCombo);
            if (AutoTriggerCombo.Items.Count == 0)
            {
                AutoTriggerCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.TriggerHotkey"), Tag = AutoRuleTrigger.Hotkey });
                AutoTriggerCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.TriggerAppStart"), Tag = AutoRuleTrigger.AppStart });
                AutoTriggerCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.TriggerAppSwitch"), Tag = AutoRuleTrigger.AppSwitch });
                AutoTriggerCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.TriggerAppExit"), Tag = AutoRuleTrigger.AppExit });
                AutoTriggerCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.TriggerSchedule"), Tag = AutoRuleTrigger.Schedule });
                AutoTriggerCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.TriggerStartup"), Tag = AutoRuleTrigger.Startup });
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

        private void LoadAutoAppCombo(System.Windows.Controls.ComboBox combo)
        {
            BindAutoAppCandidates(combo, (combo.SelectedItem as AppItem)?.ProcessName);
        }

        private static void AppCombo_DropDownOpened(object sender, EventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox combo) LoadAppComboIcons(combo);
        }

        private static void LoadAppComboIcons(System.Windows.Controls.ComboBox combo)
        {
            AppItem.LoadIconsAsync(combo.Items.OfType<AppItem>());
        }

        private static void LoadSelectedAppIcon(System.Windows.Controls.ComboBox combo)
        {
            if (combo.SelectedItem is AppItem item) AppItem.LoadIconsAsync(new[] { item });
        }

        private void SelectAutoApp(System.Windows.Controls.ComboBox combo, string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) { combo.SelectedIndex = -1; return; }
            foreach (var item in combo.Items)
                if (item is AppItem ai && string.Equals(ai.Info.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                { combo.SelectedItem = item; LoadSelectedAppIcon(combo); return; }
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
            var trigger = SelectedAutoTrigger();
            AutoHotkeyPanel.Visibility = trigger == AutoRuleTrigger.Hotkey ? Visibility.Visible : Visibility.Collapsed;
            bool appBased = trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppSwitch or AutoRuleTrigger.AppExit;
            AutoTriggerAppPanel.Visibility = appBased ? Visibility.Visible : Visibility.Collapsed;
            AutoSchedulePanel.Visibility = trigger == AutoRuleTrigger.Schedule ? Visibility.Visible : Visibility.Collapsed;
            UpdateAutoScheduleModePanels();
        }

        private AutoRuleTrigger SelectedAutoTrigger() => AutoTriggerCombo.SelectedItem is ComboBoxItem
        { Tag: AutoRuleTrigger trigger } ? trigger : AutoRuleTrigger.Hotkey;

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
            EndAutoDrag(commit: false);
            _autoStepAppCombos.Clear();
            _autoStepDeviceCombos.Clear();
            _autoStepNumbers.Clear();
            _autoStepViews.Clear();
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

        private static string AutoActionKey(AutoRuleAction action) => action switch
        {
            AutoRuleAction.SetSystemOutput => "Auto.ActionSystemOutput",
            AutoRuleAction.SetSystemInput => "Auto.ActionSystemInput",
            AutoRuleAction.SetSystemVolume => "Auto.ActionSystemVolume",
            AutoRuleAction.ToggleSystemMute => "Auto.ActionSystemMute",
            AutoRuleAction.SetAppVolume => "Auto.ActionAppVolume",
            AutoRuleAction.ToggleAppMute => "Auto.ActionAppMute",
            AutoRuleAction.SetAppOutput => "Auto.ActionAppOutput",
            AutoRuleAction.SetAppInput => "Auto.ActionAppInput",
            AutoRuleAction.LaunchProgram => "Auto.ActionLaunch",
            AutoRuleAction.RunPowerShell => "Auto.ActionPowerShell",
            AutoRuleAction.ExecuteRule => "Auto.ActionExecuteRule",
            AutoRuleAction.ShowOsd => "Auto.ActionShowOsd",
            AutoRuleAction.SetSystemMute => "Auto.ActionSetSystemMute",
            AutoRuleAction.SetAppMute => "Auto.ActionSetAppMute",
            AutoRuleAction.AdjustSystemVolume => "Auto.ActionAdjustSystemVolume",
            AutoRuleAction.AdjustAppVolume => "Auto.ActionAdjustAppVolume",
            AutoRuleAction.SetGlobalMicMute => "Auto.ActionSetMicMute",
            AutoRuleAction.ToggleGlobalMicMute => "Auto.ActionToggleMicMute",
            _ => "Auto.Action"
        };

        private (SolidColorBrush bg, SolidColorBrush border, SolidColorBrush fg) AutoBlockPalette(AutoRuleAction a)
        {
            bool dark = _config.ThemeMode switch { "light" => false, "dark" => true, _ => ThemeService.IsDarkMode() };
            int group = a switch
            {
                AutoRuleAction.SetSystemOutput or AutoRuleAction.SetSystemInput or AutoRuleAction.SetSystemVolume
                    or AutoRuleAction.ToggleSystemMute or AutoRuleAction.SetSystemMute or AutoRuleAction.AdjustSystemVolume
                    or AutoRuleAction.SetGlobalMicMute or AutoRuleAction.ToggleGlobalMicMute => 0,
                AutoRuleAction.SetAppVolume or AutoRuleAction.ToggleAppMute or AutoRuleAction.SetAppOutput
                    or AutoRuleAction.SetAppInput or AutoRuleAction.SetAppMute or AutoRuleAction.AdjustAppVolume => 1,
                AutoRuleAction.LaunchProgram or AutoRuleAction.RunPowerShell => 2,
                _ => 3
            };
            // 四类积木颜色在同一深浅模式下相同，冻结画刷共用，不按卡片重复创建。
            if (_autoBlockPaletteCache.TryGetValue((dark, group), out var cached)) return cached;
            (byte R, byte G, byte B, byte BR, byte BG, byte BB, byte FR, byte FG, byte FB) =
                group switch
                {
                    0 => dark ? ((byte)0x2A, (byte)0x3A, (byte)0x5C, (byte)0x4A, (byte)0x6F, (byte)0xA8, (byte)0xBF, (byte)0xD4, (byte)0xFF)
                                : ((byte)0xE9, (byte)0xF1, (byte)0xFE, (byte)0xC7, (byte)0xD9, (byte)0xF8, (byte)0x1E, (byte)0x41, (byte)0x91),
                    1 => dark ? ((byte)0x4A, (byte)0x37, (byte)0x22, (byte)0x7A, (byte)0x5A, (byte)0x2E, (byte)0xFF, (byte)0xD9, (byte)0xA8)
                                : ((byte)0xFD, (byte)0xF1, (byte)0xE4, (byte)0xF6, (byte)0xDC, (byte)0xBB, (byte)0x8C, (byte)0x4E, (byte)0x11),
                    2 => dark ? ((byte)0x3A, (byte)0x33, (byte)0x54, (byte)0x5E, (byte)0x4F, (byte)0x8F, (byte)0xD3, (byte)0xC4, (byte)0xFF)
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
            var palette = (Mk(R, G, B), Mk(BR, BG, BB), Mk(FR, FG, FB));
            _autoBlockPaletteCache[(dark, group)] = palette;
            return palette;
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
                    ? MathEx.Clamp(t, 0, 60000)
                    : MathEx.Clamp(step.DelayMs, 0, 60000);
                step.DelayMs = target;
                SetDelayBoxText(box, target.ToString());
                return;
            }
            var clamped = MathEx.Clamp(ms, 0, 60000);
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
            if (sender is not FrameworkElement grip || grip.Tag is not Border block || block.Tag is not AutoRuleStep step) return;
            EndAutoDrag(commit: false);
            _autoDragFrom = _autoSteps.IndexOf(step);
            if (_autoDragFrom < 0) return;
            _autoDragStep = step;
            _autoDragStartY = e.GetPosition(AutoStepsHost).Y;
            _autoDragLastPointerY = _autoDragStartY;
            _autoDragDistance = 0;
            _autoDragTarget = _autoDragFrom;
            _autoGrip = grip;
            if (!grip.CaptureMouse()) EndAutoDrag(commit: false);
            e.Handled = true;
        }

        private void AutoStepGrip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_autoGrip == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndAutoDrag(commit: false); return; }
            double y = e.GetPosition(AutoStepsHost).Y;
            if (!_autoDragging)
            {
                if (Math.Abs(y - _autoDragStartY) < SystemParameters.MinimumVerticalDragDistance) return;
                if (!BeginAutoDragPreview()) { EndAutoDrag(commit: false); return; }
            }
            QueueAutoDragFrame();
            e.Handled = true;
        }

        private bool BeginAutoDragPreview()
        {
            for (int i = 0; i < AutoStepsHost.Items.Count; i++)
            {
                if (AutoStepsHost.Items[i] is not Border block
                    || AutoStepsHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                    return false;
                _autoDragRows.Add(new AutoDragRow
                {
                    Block = block,
                    Container = container,
                    OriginalTransform = block.RenderTransform,
                    Top = container.TranslatePoint(new System.Windows.Point(0, 0), AutoStepsHost).Y,
                    Height = container.ActualHeight
                });
            }
            if (_autoDragRows.Count != _autoSteps.Count || _autoDragFrom >= _autoDragRows.Count) return false;
            _autoDragHeight = _autoDragRows[_autoDragFrom].Height;
            if (_autoDragHeight <= 0) return false;
            foreach (var row in _autoDragRows)
            {
                row.Motion.Children.Add(row.OriginalTransform);
                row.Motion.Children.Add(row.Slide);
                row.Block.RenderTransform = row.Motion;
            }
            var dragged = _autoDragRows[_autoDragFrom];
            dragged.Block.Opacity = 0.90;
            Panel.SetZIndex(dragged.Container, 1);
            _autoDragging = true;
            return true;
        }

        private void QueueAutoDragFrame()
        {
            if (!_autoDragging || _autoDragFrameQueued) return;
            _autoDragFrameQueued = true;
            CompositionTarget.Rendering += AutoDrag_Rendering;
        }

        private void AutoDrag_Rendering(object? sender, EventArgs e)
        {
            if (!_autoDragging || _autoGrip == null || Mouse.LeftButton != MouseButtonState.Pressed)
            { EndAutoDrag(commit: false); return; }
            UpdateAutoDragPreview(Mouse.GetPosition(AutoStepsHost).Y);

            // 只有靠近可见区域边缘时连续请求帧；停在中间即解除 Rendering。
            var time = e is RenderingEventArgs rendering ? rendering.RenderingTime : TimeSpan.Zero;
            double seconds = _autoDragLastFrame == TimeSpan.Zero ? 1.0 / 60
                : MathEx.Clamp((time - _autoDragLastFrame).TotalSeconds, 0, 0.05);
            _autoDragLastFrame = time;
            var point = Mouse.GetPosition(AutomationScroll);
            double speed = 0;
            if (point.X >= 0 && point.X <= AutomationScroll.ActualWidth)
            {
                const double edge = 40;
                double height = AutomationScroll.ViewportHeight;
                if (point.Y < edge) speed = -420 * MathEx.Clamp((edge - point.Y) / edge, 0, 1);
                else if (point.Y > height - edge) speed = 420 * MathEx.Clamp((point.Y - height + edge) / edge, 0, 1);
            }
            double next = MathEx.Clamp(AutomationScroll.VerticalOffset + speed * seconds, 0, AutomationScroll.ScrollableHeight);
            if (Math.Abs(next - AutomationScroll.VerticalOffset) > 0.01)
                AutomationScroll.ScrollToVerticalOffset(next);
            else StopAutoDragFrames();
        }

        private void StopAutoDragFrames()
        {
            if (_autoDragFrameQueued) CompositionTarget.Rendering -= AutoDrag_Rendering;
            _autoDragFrameQueued = false;
            _autoDragLastFrame = TimeSpan.Zero;
        }

        private void UpdateAutoDragPreview(double pointerY)
        {
            if (!_autoDragging || _autoDragFrom < 0 || _autoDragFrom >= _autoDragRows.Count) return;
            var dragged = _autoDragRows[_autoDragFrom];
            double top = MathEx.Clamp(dragged.Top + pointerY - _autoDragStartY,
                _autoDragRows[0].Top - 8,
                _autoDragRows[_autoDragRows.Count - 1].Top + _autoDragRows[_autoDragRows.Count - 1].Height - _autoDragHeight + 8);
            dragged.Slide.Y = top - dragged.Top;
            int target = _autoDragTarget;
            _autoDragDistance += pointerY - _autoDragLastPointerY;
            _autoDragLastPointerY = pointerY;
            double SwapDistance(int index)
            {
                // 每移动一格只需约 18–32 DIP；长参数卡片不再要求拖过半张卡片。
                return MathEx.Clamp(_autoDragRows[index].Height * 0.18, 18, 32);
            }
            // 相对上一次换位累积位移；反向必须跨过下一段距离，小幅手抖不回跳。
            while (target < _autoDragRows.Count - 1)
            {
                int nextIndex = target < _autoDragFrom ? target : target + 1;
                double distance = SwapDistance(nextIndex);
                if (_autoDragDistance < distance) break;
                _autoDragDistance -= distance;
                target++;
            }
            while (target > 0)
            {
                int previousIndex = target - 1 < _autoDragFrom ? target - 1 : target;
                double distance = SwapDistance(previousIndex);
                if (_autoDragDistance > -distance) break;
                _autoDragDistance += distance;
                target--;
            }
            if (target == 0 && _autoDragDistance < 0 || target == _autoDragRows.Count - 1 && _autoDragDistance > 0)
                _autoDragDistance = 0;
            if (target == _autoDragTarget) return;
            _autoDragTarget = target;
            for (int i = 0; i < _autoDragRows.Count; i++)
            {
                if (i == _autoDragFrom) continue;
                double offset = target > _autoDragFrom && i > _autoDragFrom && i <= target ? -_autoDragHeight
                    : target < _autoDragFrom && i >= target && i < _autoDragFrom ? _autoDragHeight : 0;
                SetAutoPreviewOffset(_autoDragRows[i], offset);
            }
            for (int i = 0; i < _autoSteps.Count; i++)
            {
                int previewIndex = i == _autoDragFrom ? target
                    : target > _autoDragFrom && i > _autoDragFrom && i <= target ? i - 1
                    : target < _autoDragFrom && i >= target && i < _autoDragFrom ? i + 1 : i;
                if (_autoStepNumbers.TryGetValue(_autoSteps[i], out var number))
                    number.Text = string.Format(L10n.T("Auto.StepNumber"), previewIndex + 1);
            }
        }

        private static void SetAutoPreviewOffset(AutoDragRow row, double target, Action? completed = null)
        {
            if (row.Target == target) return;
            row.Target = target;
            double current = row.Slide.Y;
            int version = ++row.AnimationVersion;
            row.Slide.BeginAnimation(TranslateTransform.YProperty, null);
            row.Slide.Y = target;
            if (!UiMotion.AnimationsEnabled || !UiMotion.HardwareRendering || Math.Abs(current - target) < 0.5)
            { completed?.Invoke(); return; }
            var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(110)) { EasingFunction = AutoDragEase };
            Timeline.SetDesiredFrameRate(animation, 30);
            animation.Completed += (_, _) =>
            {
                if (version != row.AnimationVersion) return;
                row.Slide.BeginAnimation(TranslateTransform.YProperty, null);
                completed?.Invoke();
            };
            row.Slide.BeginAnimation(TranslateTransform.YProperty, animation);
        }

        private void AutoStepGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_autoGrip == null) return;
            if (_autoDragging) UpdateAutoDragPreview(e.GetPosition(AutoStepsHost).Y);
            EndAutoDrag(commit: true);
            e.Handled = true;
        }

        private void AutoStepGrip_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (ReferenceEquals(sender, _autoGrip)) EndAutoDrag(commit: false);
        }

        private void EndAutoDrag(bool commit)
        {
            StopAutoDragFrames();
            foreach (var row in _autoDropSlides)
            {
                ++row.AnimationVersion;
                row.Slide.BeginAnimation(TranslateTransform.YProperty, null);
                row.Block.RenderTransform = row.OriginalTransform;
            }
            _autoDropSlides.Clear();
            _autoDropOperation?.Abort(); _autoDropOperation = null;
            int generation = ++_autoDragGeneration;
            var grip = _autoGrip;
            var step = _autoDragStep;
            int from = _autoDragFrom, target = _autoDragTarget;
            Border? moved = _autoDragging && from >= 0 && from < _autoDragRows.Count ? _autoDragRows[from].Block : null;
            var settling = new List<(AutoDragRow Row, double Offset)>();
            for (int i = 0; i < _autoDragRows.Count; i++)
            {
                var row = _autoDragRows[i];
                if (commit && moved != null && target >= 0 && target < _autoDragRows.Count)
                {
                    double newTop = i == from ? target < from ? _autoDragRows[target].Top
                            : target > from ? _autoDragRows[target].Top + _autoDragRows[target].Height - _autoDragHeight : row.Top
                        : target > from && i > from && i <= target ? row.Top - _autoDragHeight
                        : target < from && i >= target && i < from ? row.Top + _autoDragHeight : row.Top;
                    double offset = row.Top + row.Slide.Y - newTop;
                    if (Math.Abs(offset) >= 0.5) settling.Add((row, offset));
                }
                ++row.AnimationVersion;
                row.Slide.BeginAnimation(TranslateTransform.YProperty, null);
                row.Block.RenderTransform = row.OriginalTransform;
                row.Block.Opacity = 1;
                Panel.SetZIndex(row.Container, 0);
            }
            _autoDragRows.Clear();
            for (int i = 0; i < _autoSteps.Count; i++)
                if (_autoStepNumbers.TryGetValue(_autoSteps[i], out var number))
                    number.Text = string.Format(L10n.T("Auto.StepNumber"), i + 1);
            _autoGrip = null; _autoDragStep = null; _autoDragFrom = -1; _autoDragTarget = -1; _autoDragging = false;
            if (grip?.IsMouseCaptured == true) grip.ReleaseMouseCapture();
            if (commit && moved != null && step != null && from >= 0 && target >= 0
                && from < _autoSteps.Count && target < _autoSteps.Count && ReferenceEquals(_autoSteps[from], step))
            {
                // 离开输入事件栈再落位；在下一次绘制/输入前提交，不重建控件或强制布局。
                _autoDropOperation = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(() =>
                {
                    _autoDropOperation = null;
                    if (_isClosed || generation != _autoDragGeneration || from >= _autoSteps.Count
                        || target >= _autoSteps.Count || !ReferenceEquals(_autoSteps[from], step)
                        || from >= AutoStepsHost.Items.Count || !ReferenceEquals(AutoStepsHost.Items[from], moved)) return;
                    if (from != target)
                    {
                        _autoSteps.RemoveAt(from); _autoSteps.Insert(target, step);
                        AutoStepsHost.Items.RemoveAt(from); AutoStepsHost.Items.Insert(target, moved);
                    }
                    for (int i = 0; i < _autoSteps.Count; i++)
                        if (_autoStepNumbers.TryGetValue(_autoSteps[i], out var number))
                            number.Text = string.Format(L10n.T("Auto.StepNumber"), i + 1);
                    foreach (var entry in settling)
                    {
                        var row = entry.Row;
                        row.Block.RenderTransform = row.Motion;
                        row.Slide.Y = entry.Offset;
                        row.Target = double.NaN;
                        _autoDropSlides.Add(row);
                        SetAutoPreviewOffset(row, 0, () =>
                        {
                            row.Block.RenderTransform = row.OriginalTransform;
                            _autoDropSlides.Remove(row);
                        });
                    }
                }));
            }
        }


        private void AutoStepDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AutoRuleStep step)
            {
                _autoSteps.Remove(step);
                RenderAutoSteps();
            }
        }

        private void AutoStepCopy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not AutoRuleStep step) return;
            int index = _autoSteps.IndexOf(step);
            if (index < 0) return;
            _autoSteps.Insert(index + 1, step.Clone());
            RenderAutoSteps();
        }

        private void AutoStepAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAutoUi || sender is not System.Windows.Controls.ComboBox combo || combo.Tag is not AutoRuleStep step
                || combo.SelectedItem is not AutomationActionChoice { Action: AutoRuleAction action }) return;
            step.Action = action;
            combo.ToolTip = L10n.T(AutoActionKey(action));
            NormalizeLaunchSteps(new[] { step });
            UpdateAutomationStepView(step);
        }


        private UIElement BuildStepParams(AutoRuleStep step)
        {
            var wrap = new StackPanel();
            var labelBrush = (SolidColorBrush)FindResource("Theme.TextSecondary");
            UIElement MkLabel(string text)
            {
                var label = new TextBlock { Text = text };
                label.SetResourceReference(StyleProperty, "AutomationFormLabel");
                return label;
            }
            UIElement Group(string label, UIElement ctrl)
            {
                var sp = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                sp.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
                sp.ColumnDefinitions.Add(new ColumnDefinition());
                if (ctrl is FrameworkElement field)
                {
                    field.Width = double.NaN;
                    field.HorizontalAlignment = HorizontalAlignment.Stretch;
                }
                Grid.SetColumn(ctrl, 1);
                sp.Children.Add(MkLabel(label));
                sp.Children.Add(ctrl);
                return sp;
            }

            bool app = step.Action is AutoRuleAction.SetAppVolume or AutoRuleAction.ToggleAppMute
                or AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput
                or AutoRuleAction.SetAppMute or AutoRuleAction.AdjustAppVolume;
            bool dev = step.Action is AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput
                or AutoRuleAction.SetSystemOutput or AutoRuleAction.SetSystemInput;
            bool relativeVolume = step.Action is AutoRuleAction.AdjustSystemVolume or AutoRuleAction.AdjustAppVolume;
            bool vol = relativeVolume || step.Action is AutoRuleAction.SetSystemVolume or AutoRuleAction.SetAppVolume;
            bool muteState = step.Action is AutoRuleAction.SetSystemMute or AutoRuleAction.SetAppMute or AutoRuleAction.SetGlobalMicMute;
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
                cb.DropDownOpened += AppCombo_DropDownOpened;
                BindAutoAppCandidates(cb, step.TargetApp);
                _autoStepAppCombos.Add(cb);
                SelectAutoApp(cb, step.TargetApp);
                cb.SelectionChanged += (_, _) =>
                {
                    if (!_suppressAutoUi && cb.SelectedItem is AppItem ai) step.TargetApp = ai.Info.ProcessName ?? "";
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
                var devices = flow == EDataFlow.eRender ? (_autoOutputs ?? _outputs) : (_autoInputs ?? _inputs);
                foreach (var d in DisplayDevices(devices)) cb.Items.Add(d);
                _autoStepDeviceCombos[cb] = flow;
                SelectAutoDevice(cb, step.TargetDeviceId);
                cb.SelectionChanged += (_, _) =>
                {
                    if (!_suppressAutoUi && cb.SelectedItem is AudioDeviceInfo d) step.TargetDeviceId = d.Id;
                };
                wrap.Children.Add(Group(L10n.T("Auto.TargetDevice"), cb));
            }
            if (vol)
            {
                int min = relativeVolume ? -100 : 0;
                int value = MathEx.Clamp(relativeVolume ? step.VolumeDelta : step.Volume, min, 100);
                string VolumeText(int percent) => (relativeVolume && percent > 0 ? "+" : "") + percent + "%";
                var txt = new TextBlock
                {
                    Text = VolumeText(value),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    MinWidth = 44,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var sl = new Slider
                {
                    Minimum = min,
                    Maximum = 100,
                    Value = value,
                    Width = 200,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsMoveToPointEnabled = true
                };
                sl.ValueChanged += (_, _) =>
                {
                    int changed = (int)Math.Round(sl.Value);
                    if (relativeVolume) step.VolumeDelta = changed; else step.Volume = changed;
                    txt.Text = VolumeText(changed);
                };
                // 鼠标滚轮调节音量（±5，阻止冒泡避免页面滚动）
                sl.MouseWheel += (_, e) =>
                {
                    int delta = e.Delta > 0 ? 5 : -5;
                    sl.Value = MathEx.Clamp((int)Math.Round(sl.Value) + delta, min, 100);
                    e.Handled = true;
                };
                var g = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 8),
                    VerticalAlignment = VerticalAlignment.Center
                };
                g.Children.Add(sl);
                g.Children.Add(txt);
                wrap.Children.Add(g);
            }
            if (muteState)
            {
                var cb = new System.Windows.Controls.ComboBox
                {
                    Style = (Style)FindResource("SelCombo"),
                    Width = 160
                };
                cb.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.MuteOn"), Tag = true });
                cb.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.MuteOff"), Tag = false });
                cb.SelectedIndex = step.Muted ? 0 : 1;
                cb.SelectionChanged += (_, _) =>
                {
                    if (cb.SelectedItem is ComboBoxItem { Tag: bool muted }) step.Muted = muted;
                };
                wrap.Children.Add(Group(L10n.T("Auto.MuteState"), cb));
            }
            if (prog)
            {
                if (step.Action == AutoRuleAction.LaunchProgram)
                {
                    // 启动程序（v1.18）：启动模式 + 启动列表，每个启动项独立打开方式
                    wrap.Children.Add(BuildLaunchPanel(step, labelBrush));
                }
                else
                {
                    var pathPanel = new StackPanel
                    {
                        Margin = new Thickness(0, 0, 0, 8),
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
            if (step.Action == AutoRuleAction.ExecuteRule)
            {
                wrap.Children.Add(Group(L10n.T("Auto.TargetRule"), BuildTargetRuleCombo(step)));
                wrap.Children.Add(new TextBlock
                {
                    Text = L10n.T("Auto.RuleCallHint"),
                    FontSize = 11,
                    Foreground = labelBrush,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360,
                    Margin = new Thickness(0, 16, 0, 8)
                });
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
                // 两个 OSD 文本字段共用一行，宽度随内容区分配，避免固定宽度挤出窗口。
                titleBox.Width = textBox.Width = double.NaN;
                titleBox.Height = textBox.Height = 32;
                var osdFields = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                osdFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                osdFields.ColumnDefinitions.Add(new ColumnDefinition());
                osdFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                osdFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                osdFields.ColumnDefinitions.Add(new ColumnDefinition());
                var titleLabel = MkLabel(L10n.T("Auto.OsdTitle"));
                var textLabel = MkLabel(L10n.T("Auto.OsdText"));
                Grid.SetColumn(titleBox, 1); Grid.SetColumn(textLabel, 3); Grid.SetColumn(textBox, 4);
                osdFields.Children.Add(titleLabel); osdFields.Children.Add(titleBox);
                osdFields.Children.Add(textLabel); osdFields.Children.Add(textBox);
                wrap.Children.Add(osdFields);
            }
            var stopOnFailure = new CheckBox
            {
                Content = L10n.T("Auto.StopOnFailure"),
                ToolTip = L10n.T("Auto.StopOnFailureHint"),
                IsChecked = step.StopOnFailure,
                FontSize = 11.5,
                Margin = new Thickness(0, 4, 0, 0)
            };
            stopOnFailure.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Theme.TextSecondary");
            stopOnFailure.Checked += (_, _) => step.StopOnFailure = true;
            stopOnFailure.Unchecked += (_, _) => step.StopOnFailure = false;
            var parameters = new StackPanel();
            parameters.Children.Add(wrap);
            parameters.Children.Add(stopOnFailure);
            return parameters;
        }

        /// <summary>打开方式下拉中「选择其他程序…」项的哨兵值（不作为实际路径保存）。</summary>
        private const string AutoLaunchPickTag = "__pick__";

        /// <summary>启动程序动作参数区（v1.18）：启动模式 + 启动列表（每个启动项独立保存打开方式）。</summary>
        private UIElement BuildLaunchPanel(AutoRuleStep step, SolidColorBrush labelBrush)
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 8),
                VerticalAlignment = VerticalAlignment.Center
            };
            UIElement MkLabel(string text)
            {
                var label = new TextBlock { Text = text };
                label.SetResourceReference(StyleProperty, "AutomationFormLabel");
                return label;
            }

            // 启动模式：全部启动 / 随机启动一个
            var modeRow = new Grid { VerticalAlignment = VerticalAlignment.Center };
            modeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            modeRow.ColumnDefinitions.Add(new ColumnDefinition());
            modeRow.Children.Add(MkLabel(L10n.T("Auto.LaunchMode")));
            var modeCombo = new System.Windows.Controls.ComboBox
            {
                Style = (Style)FindResource("SelCombo"),
                Width = 260,
                Height = 34,
                Tag = step,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            modeCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.LaunchModeAll") });
            modeCombo.Items.Add(new ComboBoxItem { Content = L10n.T("Auto.LaunchModeRandom") });
            modeCombo.SelectedIndex = step.LaunchMode == 1 ? 1 : 0;
            modeCombo.SelectionChanged += (_, _) => step.LaunchMode = modeCombo.SelectedIndex == 1 ? 1 : 0;
            Grid.SetColumn(modeCombo, 1);
            modeRow.Children.Add(modeCombo);
            panel.Children.Add(modeRow);

            // 启动列表
            var listRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            listRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            listRow.ColumnDefinitions.Add(new ColumnDefinition());
            var listLabel = (TextBlock)MkLabel(L10n.T("Auto.LaunchList"));
            listLabel.VerticalAlignment = VerticalAlignment.Top;
            listLabel.Margin = new Thickness(0, 8, 10, 0);
            listRow.Children.Add(listLabel);
            var listBorder = new Border
            {
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1)
            };
            listBorder.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
            var listPanel = new StackPanel();
            for (int i = 0; i < step.LaunchItems.Count; i++)
                listPanel.Children.Add(BuildLaunchItemRow(step, step.LaunchItems[i], i));
            listBorder.Child = listPanel;
            Grid.SetColumn(listBorder, 1);
            listRow.Children.Add(listBorder);
            panel.Children.Add(listRow);

            // 添加文件 / 程序（按钮本身支持拖放，与 PowerShell 路径区一致）
            var addBtn = new Button
            {
                Content = L10n.T("Auto.AddFile"),
                Tag = step,
                Width = 150,
                Height = 28,
                Margin = new Thickness(84, 8, 0, 0),
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
                Margin = new Thickness(84, 6, 0, 0)
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
                Width = 132,
                Height = 32,
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
                    _uiLifetime.Post(Dispatcher, new Action(() =>
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
            var fileIcon = AutomationIcons.Create(AutomationIcons.File, 15);
            fileIcon.Margin = new Thickness(0, 0, 5, 0);
            fileIcon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(fileIcon, Dock.Left);
            row.Children.Add(fileIcon);
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
            var trigger = SelectedAutoTrigger();
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
                if (s.Action == AutoRuleAction.ExecuteRule && (string.IsNullOrWhiteSpace(s.TargetRuleId)
                    || s.TargetRuleId == _autoEditingId || AutoRuleStore.Find(s.TargetRuleId) == null))
                { _ = System.Windows.MessageBox.Show(L10n.T("Auto.RuleRequired")); return; }
                if (s.Action is AutoRuleAction.SetAppVolume or AutoRuleAction.ToggleAppMute
                    or AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput
                    or AutoRuleAction.SetAppMute or AutoRuleAction.AdjustAppVolume
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
            rule.SetSteps(_autoSteps);
            rule.Enabled = true;
            try { AutoRuleStore.Save(rule, L10n.T("Auto.DefaultNamePrefix")); }
            catch { ShowToast(L10n.T("Auto.SaveFailed")); return; }
            ((App)Application.Current).ReloadHotkeys();
            SetAutomationEditorVisible(false);
            _autoCapturingHotkey = false;
            _ = RefreshAutoRulesAsync();
        }
    }
}

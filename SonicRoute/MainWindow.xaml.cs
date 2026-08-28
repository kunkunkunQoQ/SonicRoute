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

        public MainWindow()
        {
            InitializeComponent();
            _config = ConfigService.Load();
            Loaded += async (_, _) =>
            {
                await LoadDevicesAsync();
                await RefreshOverviewAsync();
                BuildDeviceNameLists();
                // 实验 UI（概览/应用/设置输入区/名称区）可见性：麦克风选项开启时显示
                ApplyExpMicUi(_config.ExperimentalUnlocked && _config.ExperimentalMode && _config.ExperimentalMic);
                NavExperimental.Visibility = _config.ExperimentalUnlocked && _config.ExperimentalMode
                    ? Visibility.Visible : Visibility.Collapsed;
            };
            // 共享"当前应用"变化（前台自动跟随/面板切换）时同步概览
            CurrentAppService.CurrentChanged += OnSharedCurrentChanged;
            Closed += (_, _) => CurrentAppService.CurrentChanged -= OnSharedCurrentChanged;
            // 快捷键内联录音：在窗口内直接捕获按键，免弹窗
            PreviewKeyDown += MainWindow_PreviewKeyDown;
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
            HwndSource.FromHwnd(hwnd)?.AddHook(TaskbarMinimizeWndProc);
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
            if (_recordingAction == null) return;
            e.Handled = true;
            if (e.Key == Key.Escape)
            {
                _recordingAction = null;
                BuildHotkeyList();
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
            var tag = ((RadioButton)sender).Tag as string;
            OverviewPage.Visibility = tag == "Overview" ? Visibility.Visible : Visibility.Collapsed;
            AppsPage.Visibility = tag == "Apps" ? Visibility.Visible : Visibility.Collapsed;
            HotkeysPage.Visibility = tag == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
            ThemePage.Visibility = tag == "Theme" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            ExperimentalPage.Visibility = tag == "Experimental" ? Visibility.Visible : Visibility.Collapsed;

            if (tag == "Overview") await RefreshOverviewAsync();
            else if (tag == "Apps") await LoadAppsAsync();
            else if (tag == "Hotkeys") BuildHotkeyList();
            else if (tag == "Theme") LoadTheme();
            else if (tag == "Settings") LoadSettings();
            else if (tag == "Experimental") LoadExperimentalSettings();
        }

        // ==================================================================
        // 设备加载（自定义名称 + 筛选 + 名称编辑）
        // ==================================================================

        private async Task LoadDevicesAsync()
        {
            var outputs = await Task.Run(() => AudioService.GetDevices(EDataFlow.eRender));

            string? defOut = await Task.Run(() => AudioService.GetDefaultDeviceId(EDataFlow.eRender));
            foreach (var d in outputs) d.IsDefault = string.Equals(d.Id, defOut, StringComparison.OrdinalIgnoreCase);

            _outputs = outputs;

            // 输入设备（麦克风）：仅实验模式 + 麦克风选项使用（快速切换当前应用麦克风设备/保留设置）
            try
            {
                var inputs = await Task.Run(() => AudioService.GetDevices(EDataFlow.eCapture));
                string? defIn = await Task.Run(() => AudioService.GetDefaultDeviceId(EDataFlow.eCapture));
                foreach (var d in inputs) d.IsDefault = string.Equals(d.Id, defIn, StringComparison.OrdinalIgnoreCase);
                _inputs = inputs;
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

        private IEnumerable<AudioDeviceInfo> VisibleOutputs =>
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
            _outputDisplay = DisplayDevices(_outputs);
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
            // 全局麦克风状态与应用无关，始终刷新按钮文案
            bool globalMicMuted = await Task.Run(() => GlobalMicMuteService.IsMuted());
            OverviewMicMuteButton.Content = L10n.T(globalMicMuted ? "Ov.MicUnmute" : "Ov.MuteMic");

            var outs = DisplayDevices(VisibleOutputs).ToList();
            _inputDisplay = DisplayDevices(VisibleInputs).ToList();
            OverviewInputCombo.ItemsSource = null;
            OverviewInputCombo.ItemsSource = _inputDisplay;

            if (_overviewApp == null)
            {
                OverviewOutputCurrentText.Text = "";
                RenderQuickButtons(OverviewOutputQuickPanel, outs);
                OverviewInputCurrentText.Text = "";
                OverviewInputCurrentText.Tag = null;
                RenderInputQuickButtons();
                SetVolumeUi(null);
                return;
            }

            var pid = (int)_overviewApp.ProcessId;
            var outId = await Task.Run(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender));

            string? outShort = outId == null ? null : AudioPolicyConfig.UnpackDeviceId(outId);

            OverviewOutputCurrentText.Text = DescribeCurrent(_outputDisplay, outShort);
            RenderQuickButtons(OverviewOutputQuickPanel, outs);

            // 选中项必须从下拉实际绑定的显示列表（含自定义名称）中查找，
            // 否则改过名称的设备会多出一个"默认名"的幽灵项
            var selectedOut = _outputDisplay.FirstOrDefault(d => string.Equals(d.Id, outShort, StringComparison.OrdinalIgnoreCase));
            _suppressDevCombo = true;
            OverviewOutputCombo.SelectedItem = selectedOut ?? _outputDisplay.FirstOrDefault(d => d.IsDefault) ?? _outputDisplay.FirstOrDefault();
            _suppressDevCombo = false;

            // 输入设备（麦克风）：与输出一致读取持久化端点并刷新下拉/快捷按钮
            var inId = await Task.Run(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eCapture));
            string? inShort = inId == null ? null : AudioPolicyConfig.UnpackDeviceId(inId);
            OverviewInputCurrentText.Text = DescribeCurrent(_inputDisplay, inShort);
            OverviewInputCurrentText.Tag = inShort;
            RenderInputQuickButtons();
            var selectedIn = _inputDisplay.FirstOrDefault(d => string.Equals(d.Id, inShort, StringComparison.OrdinalIgnoreCase));
            _suppressDevCombo = true;
            OverviewInputCombo.SelectedItem = selectedIn ?? _inputDisplay.FirstOrDefault(d => d.IsDefault) ?? _inputDisplay.FirstOrDefault();
            _suppressDevCombo = false;

            int vol = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
            bool muted = await Task.Run(() => SessionVolumeService.IsMuted(pid));
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
            var (ok, _, msg) = await Task.Run(() => AudioService.ApplyEndpoint(pid, EDataFlow.eRender, dev.Id));
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
            foreach (var dev in _inputDisplay)
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
            var (ok, _, msg) = await Task.Run(() => AudioService.ApplyEndpoint(pid, EDataFlow.eCapture, dev.Id));
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
            var (ok, _, msg) = await Task.Run(() => AudioService.ApplyEndpoint(pid, EDataFlow.eCapture, dev.Id));
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

            AppsOutputCombo.SelectedItem = _outputDisplay.FirstOrDefault(d => string.Equals(d.Id, outShort, StringComparison.OrdinalIgnoreCase))
                                           ?? _outputDisplay.FirstOrDefault(d => d.IsDefault) ?? _outputDisplay.FirstOrDefault();

            int vol = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
            bool muted = await Task.Run(() => SessionVolumeService.IsMuted(pid));
            SetAppsVolumeUi(vol >= 0 ? vol : null);
            AppsMuteButton.Content = L10n.T(muted ? "Apps.Unmute" : "Apps.Mute");
            UpdateAppsDisableAutoButton();
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
            AppsStatusText.Text = L10n.T(cfg.DisabledAutoSwitchApps.Contains(name)
                ? "Apps.DisableAutoOn" : "Apps.DisableAuto");
            await Task.CompletedTask;
        }

        /// <summary>按当前选中应用是否禁用自动切换刷新按钮文字。</summary>
        private void UpdateAppsDisableAutoButton()
        {
            bool disabled = _appsSelected != null && !string.IsNullOrWhiteSpace(_appsSelected.ProcessName)
                && ConfigService.Load().DisabledAutoSwitchApps.Contains(_appsSelected.ProcessName);
            AppsDisableAutoButton.Content = L10n.T(disabled ? "Apps.DisableAutoOn" : "Apps.DisableAuto");
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
            AppsMuteButton.Content = L10n.T(muted ? "Apps.Unmute" : "Apps.Mute");
            AppsStatusText.Text = L10n.T(muted ? "Ov.Muted" : "Ov.Unmuted");
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
            AppsOutputCombo.SelectedItem = _outputDisplay.FirstOrDefault(d => string.Equals(d.Id, outShort, StringComparison.OrdinalIgnoreCase))
                                           ?? _outputDisplay.FirstOrDefault(d => d.IsDefault) ?? _outputDisplay.FirstOrDefault();
            _suppressDevCombo = false;

            // 输入设备（麦克风）：与输出一致
            var inId = await Task.Run(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eCapture));
            string? inShort = inId == null ? null : AudioPolicyConfig.UnpackDeviceId(inId);
            _suppressDevCombo = true;
            AppsInputCombo.ItemsSource = null;
            AppsInputCombo.ItemsSource = _inputDisplay;
            AppsInputCombo.SelectedItem = _inputDisplay.FirstOrDefault(d => string.Equals(d.Id, inShort, StringComparison.OrdinalIgnoreCase))
                                          ?? _inputDisplay.FirstOrDefault(d => d.IsDefault) ?? _inputDisplay.FirstOrDefault();
            _suppressDevCombo = false;

            int vol = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
            bool muted = await Task.Run(() => SessionVolumeService.IsMuted(pid));
            SetAppsVolumeUi(vol >= 0 ? vol : null);
            AppsMuteButton.Content = L10n.T(muted ? "Apps.Unmute" : "Apps.Mute");
        }

        // ==================================================================
        // 设置页：保留设备筛选
        // ==================================================================

        private void BuildDeviceFilter()
        {
            OutputFilterList.Items.Clear();
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
            foreach (var dev in _outputs) OutputNameList.Items.Add(MakeNameRow(dev));
            InputNameList.Items.Clear();
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

                // 启动选项
                SettingsAutoStart.IsChecked = _config.AutoStart;
                SettingsStartMinimized.IsChecked = _config.StartMinimized;
                SettingsShowPanelOnStart.IsChecked = _config.StartPanelOnStart;
                ExpCollapseCheck.IsChecked = _config.CollapseDeviceSections;

                // 实验模式（隐藏）：解锁后显示开关；开启实验模式后导航显示"实验设置"；麦克风选项开启后各处显示麦克风 UI
                bool expUnlocked = _config.ExperimentalUnlocked;
                bool expOn = expUnlocked && _config.ExperimentalMode;
                bool expMic = expOn && _config.ExperimentalMic;
                SettingsExperimentalMode.Visibility = expUnlocked ? Visibility.Visible : Visibility.Collapsed;
                ExperimentalModeHint.Visibility = expUnlocked ? Visibility.Visible : Visibility.Collapsed;
                SettingsExperimentalMode.IsChecked = expOn;
                NavExperimental.Visibility = expOn ? Visibility.Visible : Visibility.Collapsed;
                ApplyExpMicUi(expMic);

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
            // 语言更改在下次启动生效：仅提示，不刷新当前界面
            ((App)Application.Current).ShowOsd(L10n.T("St.Language"), L10n.T("St.LangRestart"));
        }

        private void LoadTheme()
        {
            _suppressSettings = true;
            try
            {
                SetRadioByTag(ThemeSystem, ThemeLight, ThemeDark, _config.ThemeMode);
                OpacitySlider.Value = Math.Clamp(_config.BackgroundOpacity, 60, 100);
                OpacityText.Text = $"{_config.BackgroundOpacity}%";

                string accent = _config.Accent ?? "blue";
                bool isCustom = accent.StartsWith("#", StringComparison.OrdinalIgnoreCase);
                AccentCustom.IsChecked = isCustom;
                if (!isCustom) SetRadioByTag(AccentBlue, AccentGreen, AccentPurple, accent);
                SyncRgbUi(accent);
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
            int pct = (int)Math.Round(OverviewVolumeSlider.Value) + (e.Delta > 0 ? 4 : -4);
            OverviewVolumeSlider.Value = Math.Clamp(pct, 0, 100);
            e.Handled = true;
        }

        private void AppsVolume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_suppressVolume || !_appsVolumeReady || _appsSelected == null) return;
            int pct = (int)Math.Round(AppsVolumeSlider.Value) + (e.Delta > 0 ? 4 : -4);
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

        /// <summary>开机自启：写入/删除 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。</summary>
        private void SettingsAutoStart_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            bool on = SettingsAutoStart.IsChecked == true;
            _config.AutoStart = on;
            ConfigService.Save(_config);
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
                ShowToast(L10n.T("St.ExpUnlocked"));
            }
            else
            {
                ShowToast(string.Format(L10n.T("St.ExpClicked"), _authorClicks, 5));
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
            // 实验设置页"在快捷面板显示麦克风"子选项：仅麦克风选项开启时显示
            if (ExpMicPanelCheck != null)
                ExpMicPanelCheck.Visibility = expMic ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>实验设置页：加载麦克风选项 / 快捷面板显示 / OSD 位置 / 折叠 的当前配置。</summary>
        private void LoadExperimentalSettings()
        {
            _suppressSettings = true;
            try
            {
                bool expOn = _config.ExperimentalMode;
                bool expMic = expOn && _config.ExperimentalMic;

                ExpMicOptionCheck.IsChecked = _config.ExperimentalMic;
                ExpMicOptionCheck.IsEnabled = expOn; // 未开实验模式时置灰
                ApplyExpMicUi(expMic);
                ExpMicPanelCheck.IsChecked = _config.MicInPanel;
                ExpMicPanelCheck.IsEnabled = expMic;
                ExpFreeUIMemCheck.IsChecked = _config.FreeUIMemoryOnClose;
                ExpFreePanelMemCheck.IsChecked = _config.FreePanelUIMemory;
                ExpFreePanelMemCheck.Visibility = _config.FreeUIMemoryOnClose ? Visibility.Visible : Visibility.Collapsed;

                // OSD 位置：下拉（9 宫格 + 自定义）
                var posLabels = OsdPosKeys.Select(k => L10n.T("Exp.Osd." + k)).ToList();
                posLabels.Add(L10n.T("Exp.Osd.Custom"));
                OsdPositionCombo.ItemsSource = null;
                OsdPositionCombo.ItemsSource = posLabels;
                _suppressOsdPos = true;
                if (string.Equals(_config.OsdPosition, "Custom", StringComparison.OrdinalIgnoreCase))
                    OsdPositionCombo.SelectedIndex = posLabels.Count - 1;
                else
                {
                    int pi = Array.IndexOf(OsdPosKeys, _config.OsdPosition);
                    OsdPositionCombo.SelectedIndex = pi < 0 ? 2 : pi;
                }
                _suppressOsdPos = false;

                OsdOffsetXSlider.Value = Math.Clamp(_config.OsdOffsetX, -300, 300);
                OsdOffsetYSlider.Value = Math.Clamp(_config.OsdOffsetY, -300, 300);
                OsdOffsetXText.Text = _config.OsdOffsetX.ToString();
                OsdOffsetYText.Text = _config.OsdOffsetY.ToString();
                OsdCustomXBox.Text = _config.OsdCustomX >= 0 ? _config.OsdCustomX.ToString() : "";
                OsdCustomYBox.Text = _config.OsdCustomY >= 0 ? _config.OsdCustomY.ToString() : "";
                UpdateOsdPanels();
            }
            finally
            {
                _suppressSettings = false;
            }
        }

        private bool _suppressOsdPos;

        /// <summary>OSD 9 宫格位置键（与实验设置页下拉索引一一对应，最后一个索引为"自定义"）。</summary>
        private static readonly string[] OsdPosKeys = { "TL", "T", "TR", "L", "C", "R", "BL", "B", "BR" };

        private void ExpMicOption_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            _config.ExperimentalMic = ExpMicOptionCheck.IsChecked == true;
            ConfigService.Save(_config);
            ApplyExpMicUi(_config.ExperimentalMode && _config.ExperimentalMic);
            ExpMicPanelCheck.IsEnabled = _config.ExperimentalMode && _config.ExperimentalMic;
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

        /// <summary>实验设置 - 关闭 UI 释放内存开关（实时生效：下次关闭完整界面时真正关闭并回收 UI 内存）。</summary>
        private void ExpFreeUIMem_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            _config.FreeUIMemoryOnClose = ExpFreeUIMemCheck.IsChecked == true;
            ConfigService.Save(_config);
            // 子选项「释放快速面板 UI 内存」跟随主开关显示
            ExpFreePanelMemCheck.Visibility = ExpFreeUIMemCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>实验设置 - 子选项：关闭快速面板时释放面板 UI 内存（实时生效，独立于主开关）。</summary>
        private void ExpFreePanelMem_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            _config.FreePanelUIMemory = ExpFreePanelMemCheck.IsChecked == true;
            ConfigService.Save(_config);
        }

        /// <summary>应用折叠状态：开启后显示折叠按钮并默认收起"保留的设备/设备名称"，关闭则全部展开。</summary>
        private void ApplyCollapseUi()
        {
            bool collapse = _config.CollapseDeviceSections;
            KeepDevicesToggleButton.Visibility = collapse ? Visibility.Visible : Visibility.Collapsed;
            DeviceNamesToggleButton.Visibility = collapse ? Visibility.Visible : Visibility.Collapsed;
            if (collapse)
            {
                KeepDevicesBody.Visibility = Visibility.Collapsed;
                DeviceNamesBody.Visibility = Visibility.Collapsed;
                KeepDevicesToggleButton.Content = "▸";
                DeviceNamesToggleButton.Content = "▸";
            }
            else
            {
                KeepDevicesBody.Visibility = Visibility.Visible;
                DeviceNamesBody.Visibility = Visibility.Visible;
            }
        }

        /// <summary>OSD 位置下拉选择：0-8 对应 9 宫格，末项为"自定义"（用 X/Y 坐标参数定位）。</summary>
        private void OsdPosition_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings || _suppressOsdPos) return;
            int idx = OsdPositionCombo.SelectedIndex;
            if (idx >= 0 && idx < OsdPosKeys.Length)
                _config.OsdPosition = OsdPosKeys[idx];
            else if (idx == OsdPosKeys.Length)
                _config.OsdPosition = "Custom";
            else return;
            ConfigService.Save(_config);
            UpdateOsdPanels();
        }

        /// <summary>切换自定义坐标 / 偏移微调面板的可见性。</summary>
        private void UpdateOsdPanels()
        {
            bool isCustom = string.Equals(_config.OsdPosition, "Custom", StringComparison.OrdinalIgnoreCase);
            if (OsdCustomPanel == null || OsdOffsetPanel == null) return;
            OsdCustomPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            OsdOffsetPanel.Visibility = isCustom ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>自定义 X 坐标输入（输入即保存）。</summary>
        private void OsdCustomX_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            if (int.TryParse(OsdCustomXBox.Text, out int x))
            {
                _config.OsdCustomX = x;
                ConfigService.Save(_config);
            }
        }

        /// <summary>自定义 Y 坐标输入（输入即保存）。</summary>
        private void OsdCustomY_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings) return;
            if (int.TryParse(OsdCustomYBox.Text, out int y))
            {
                _config.OsdCustomY = y;
                ConfigService.Save(_config);
            }
        }

        /// <summary>一键还原 OSD 位置到默认（右上角 + 零偏移 + 清空自定义坐标）。</summary>
        private void OsdReset_Click(object sender, RoutedEventArgs e)
        {
            _config.OsdPosition = "TR";
            _config.OsdOffsetX = 0;
            _config.OsdOffsetY = 0;
            _config.OsdCustomX = -1;
            _config.OsdCustomY = -1;
            ConfigService.Save(_config);
            _suppressSettings = true;
            _suppressOsdPos = true;
            OsdPositionCombo.SelectedIndex = 2; // TR
            OsdOffsetXSlider.Value = 0;
            OsdOffsetYSlider.Value = 0;
            OsdCustomXBox.Text = "";
            OsdCustomYBox.Text = "";
            _suppressOsdPos = false;
            _suppressSettings = false;
            OsdOffsetXText.Text = "0";
            OsdOffsetYText.Text = "0";
            UpdateOsdPanels();
            ShowToast(L10n.T("Exp.OsdResetDone"));
        }

        private void OsdOffsetX_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _suppressSettings) return;
            _config.OsdOffsetX = (int)OsdOffsetXSlider.Value;
            OsdOffsetXText.Text = _config.OsdOffsetX.ToString();
            ConfigService.Save(_config);
        }

        private void OsdOffsetY_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _suppressSettings) return;
            _config.OsdOffsetY = (int)OsdOffsetYSlider.Value;
            OsdOffsetYText.Text = _config.OsdOffsetY.ToString();
            ConfigService.Save(_config);
        }

        /// <summary>折叠/展开设置页"保留的设备"卡片（实验设置-折叠开启时可见）。</summary>
        private void KeepDevicesToggle_Click(object sender, RoutedEventArgs e)
        {
            bool collapsed = KeepDevicesBody.Visibility != Visibility.Visible;
            KeepDevicesBody.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            KeepDevicesToggleButton.Content = collapsed ? "▾" : "▸";
        }

        /// <summary>折叠/展开设置页"设备名称"卡片（实验设置-折叠开启时可见）。</summary>
        private void DeviceNamesToggle_Click(object sender, RoutedEventArgs e)
        {
            bool collapsed = DeviceNamesBody.Visibility != Visibility.Visible;
            DeviceNamesBody.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            DeviceNamesToggleButton.Content = collapsed ? "▾" : "▸";
        }

        /// <summary>右上角 OSD 通知（兼容主题/强调色/透明度）。</summary>
        private void ShowToast(string text)
        {
            try { ((App)Application.Current).ShowOsd(L10n.T("App.NameFull"), text); }
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

        // ==================================================================
        // 快捷键页
        // ==================================================================

        private void BuildHotkeyList()
        {
            _recordingAction = null;
            HotkeyList.Items.Clear();
            // 实验模式隐藏动作：仅在"实验模式 + 麦克风选项"开启时显示（切换当前应用麦克风设备）
            bool expMicOn = _config.ExperimentalMode && _config.ExperimentalMic;
            var actions = HotkeyActions.All
                .Where(a => a != HotkeyActions.ActSwitchInput || expMicOn)
                .ToArray();
            var registered = ((App)Application.Current).HotkeyRegistration;
            foreach (var a in actions)
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

        private static string DescribeCurrent(List<AudioDeviceInfo> devices, string? currentShortId)
        {
            if (currentShortId == null)
            {
                var def = devices.FirstOrDefault(d => d.IsDefault);
                return def != null ? L10n.T("Ov.Default") + def.DisplayName : L10n.T("Ov.Unset");
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

        /// <summary>关闭窗口 → 默认最小化到托盘（真正退出走托盘菜单）。
        /// 实验设置「关闭 UI 释放内存」开启时：真正关闭窗口，触发 Closed → App 置空引用 → 窗口与 UI 资源可被 GC 回收。</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!ConfigService.Load().FreeUIMemoryOnClose)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }
    }
}

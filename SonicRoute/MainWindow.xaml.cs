using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        private AudioAppInfo? _overviewApp;
        private AudioAppInfo? _appsSelected;
        private bool _suppressVolume;
        private bool _overviewVolumeReady;
        private bool _appsVolumeReady;
        private bool _suppressAppCombo;
        private bool _suppressDevCombo;
        private bool _suppressSettings;
        private bool _suppressFilter;
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
            };
            // 共享"当前应用"变化（前台自动跟随/面板切换）时同步概览
            CurrentAppService.CurrentChanged += OnSharedCurrentChanged;
            Closed += (_, _) => CurrentAppService.CurrentChanged -= OnSharedCurrentChanged;
            // 快捷键内联录音：在窗口内直接捕获按键，免弹窗
            PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

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

            if (tag == "Overview") await RefreshOverviewAsync();
            else if (tag == "Apps") await LoadAppsAsync();
            else if (tag == "Hotkeys") BuildHotkeyList();
            else if (tag == "Theme") LoadTheme();
            else if (tag == "Settings") LoadSettings();
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

        private async void OverviewRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshOverviewAsync(force: true);
        }

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

            if (_overviewApp == null)
            {
                OverviewOutputCurrentText.Text = "";
                RenderQuickButtons(OverviewOutputQuickPanel, outs);
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
                ? string.Format(L10n.T("Ov.SwitchOk"), "🔊 " + dev.DisplayName, app.Label)
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
                ? string.Format(L10n.T("Ov.SwitchOk"), "🔊 " + dev.DisplayName, app.Label)
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

        private async Task LoadAppsAsync()
        {
            var apps = await Task.Run(() => AudioService.GetApps());
            AppsListBox.ItemsSource = null;
            AppsListBox.ItemsSource = apps.Select(AppItem.From).ToList();
        }

        private async void AppsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _appsSelected = (AppsListBox.SelectedItem as AppItem)?.Info;
            if (_appsSelected == null)
            {
                AppsDetailTitle.Text = L10n.T("Apps.SelectHint");
                AppsDetailPid.Text = "";
                SetAppsVolumeUi(null);
                return;
            }

            var pid = (int)_appsSelected.ProcessId;
            AppsDetailTitle.Text = _appsSelected.Label;
            AppsDetailPid.Text = $"PID {pid} · {_appsSelected.ProcessName ?? "?"}";

            var outId = await Task.Run(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender));

            string? outShort = outId == null ? null : AudioPolicyConfig.UnpackDeviceId(outId);

            AppsOutputCombo.SelectedItem = _outputDisplay.FirstOrDefault(d => string.Equals(d.Id, outShort, StringComparison.OrdinalIgnoreCase))
                                           ?? _outputDisplay.FirstOrDefault(d => d.IsDefault) ?? _outputDisplay.FirstOrDefault();

            int vol = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
            bool muted = await Task.Run(() => SessionVolumeService.IsMuted(pid));
            SetAppsVolumeUi(vol >= 0 ? vol : null);
            AppsMuteButton.Content = L10n.T(muted ? "Apps.Unmute" : "Apps.Mute");
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
                AppsStatusText.Text = string.Format(L10n.T("Ov.SwitchOk"), "🔊 " + dev.DisplayName, app.Label);
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

        /// <summary>输出设备全选/全不选。</summary>
        private void SelectAllOutput_Click(object sender, RoutedEventArgs e)
        {
            ToggleSelectAll(OutputFilterList.Items.OfType<CheckBox>().ToList());
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

                // 语言
                SetRadioByTag(LangZh, LangEn, null, _config.Language);

                // 启动选项
                SettingsAutoStart.IsChecked = _config.AutoStart;
                SettingsStartMinimized.IsChecked = _config.StartMinimized;
                SettingsShowPanelOnStart.IsChecked = _config.StartPanelOnStart;
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

        private void Language_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSettings || sender is not RadioButton rb) return;
            _config.Language = rb.Tag as string ?? "zh-CN";
            ConfigService.Save(_config);
            L10n.Instance.SetLanguage(_config.Language);
            // 代码生成的部分文本需要手动刷新；GetApps 带缓存，重复调用不会重复枚举
            BuildHotkeyList();
            _ = RefreshOverviewAsync();
            if (AppsPage.Visibility == Visibility.Visible) _ = LoadAppsAsync();
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
                    "purple" => Color.FromRgb(0x8B, 0x5C, 0xF6),
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
            var actions = HotkeyActions.All;
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

        /// <summary>关闭窗口 → 最小化到托盘（真正退出走托盘菜单）。</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
            base.OnClosing(e);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Size = System.Windows.Size;

namespace SonicRoute
{
    /// <summary>
    /// 托盘快速切换面板：当前应用（可切换）+ 快速切换输出设备 + 应用音量 + 麦克风静音。
    /// 只显示设置里勾选（保留）的设备。
    /// </summary>
    public partial class QuickPanelWindow : Window, IQuickPanel
    {
        /// <summary>宿主服务（阶段 0：App 直接实现；阶段 1 起由后台进程实现，经 IPC 代理调用）。</summary>
        private readonly IHostServices _host;
        private List<AudioDeviceInfo> _outputs = new();
        private List<AudioDeviceInfo> _outputDisplay = new();
        private List<AudioDeviceInfo> _inputs = new();
        private List<AudioDeviceInfo> _inputDisplay = new();
        private AudioAppInfo? _currentApp;
        private string? _currentOutId;
        private string? _currentInId;
        private bool _volumeReady;
        private bool _suppressAppCombo;
        private bool _everFocused;
        private bool _adjustMode;          // 主题页「调整快速面板位置」：可拖拽，松手保存
        private bool _adjustDragging;
        private System.Windows.Point _adjustDragStart;
        private bool _positionPending;     // 合并定位请求：同帧多次触发（SizeChanged/列表刷新/展开收起）只定位一次

        public QuickPanelWindow()
        {
            InitializeComponent();
            _host = (IHostServices)Application.Current;
            VersionText.Text = App.DisplayVersion;
            // 只有面板真正获得过焦点（托盘点击等正常交互）才在失焦时关闭；
            // 启动/脚本等未获焦场景下保持打开，避免一闪而过
            Activated += (_, _) => _everFocused = true;
            Deactivated += (_, _) =>
            {
                if (IsVisible && _everFocused && !_adjustMode) Close();
            };
            // 调整模式拖动：按住左键移动窗口，松手保存位置（逻辑同 OSD 调整）
            MouseLeftButtonDown += (_, e) =>
            {
                if (!_adjustMode) return;
                _adjustDragging = true;
                _adjustDragStart = e.GetPosition(null);
                CaptureMouse();
                e.Handled = true;
            };
            MouseMove += (_, e) =>
            {
                if (!_adjustDragging) return;
                var p = e.GetPosition(null);
                var (minX, maxX, minY, maxY) = QuickPanelPosition.DragBounds(this);
                Left = Math.Clamp(Left + (p.X - _adjustDragStart.X), minX, Math.Max(minX, maxX));
                Top = Math.Clamp(Top + (p.Y - _adjustDragStart.Y), minY, Math.Max(minY, maxY));
                e.Handled = true;
            };
            MouseLeftButtonUp += (_, e) =>
            {
                if (!_adjustDragging) return;
                _adjustDragging = false;
                ReleaseMouseCapture();
                e.Handled = true;
                SaveAdjustPosition();
            };
            // SizeToContent 下尺寸随内容变化（应用列表填充/输入区显示），合并请求只定位一次；
            // 拖拽期间不自动定位（让用户拖拽）
            SizeChanged += (_, _) => { if (IsVisible && !_adjustMode) RequestPosition(); };
            // 工作区变化（分辨率/任务栏位置/DPI 缩放）时重定位，避免默认位置失效
            SystemParameters.StaticPropertyChanged += OnSystemParamChanged;
            // 共享"当前应用"变化（前台自动跟随/概览切换）时同步面板显示
            CurrentAppService.CurrentChanged += OnSharedCurrentChanged;
            Closed += (_, _) =>
            {
                CurrentAppService.CurrentChanged -= OnSharedCurrentChanged;
                SystemParameters.StaticPropertyChanged -= OnSystemParamChanged;
                if (_adjustMode) { _adjustMode = false; _host.NotifyQuickPanelAdjustFinished(); }
            };
        }

        /// <summary>工作区变化（分辨率/任务栏位置/DPI 缩放）时重定位默认位置；自定义位置保持用户设定。</summary>
        private void OnSystemParamChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SystemParameters.WorkArea)) return;
            if (IsVisible && !_adjustMode) RequestPosition();
        }

        /// <summary>合并定位请求：同帧多次触发只定位一次，避免窗口反复移动/跳动。</summary>
        private void RequestPosition()
        {
            if (_positionPending) return;
            _positionPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _positionPending = false;
                if (!IsVisible || _adjustDragging) return;
                // 调整模式：面板已可见时不自动定位（让用户拖拽）；刚打开仍在屏幕外时仍先定位，确保可见可拖
                if (_adjustMode && Left >= -5000 && Top >= -5000) return;
                // Apply 内部：Custom 直接设回用户保存坐标（相同值 WPF 不移动窗口，仅完成首次定位/拉回偏离位置），
                // 默认锚定主显示器工作区右下角
                QuickPanelPosition.Apply(this, ConfigService.Load());
            });
        }

        /// <summary>进入/退出位置调整模式（主题页调用）。</summary>
        internal void SetAdjustMode(bool on)
        {
            _adjustMode = on;
            if (on) _host.ShowOsd(L10n.T("Exp.PanelPosDragTitle"), L10n.T("Exp.PanelPosHint"));
        }

        /// <summary>一键还原默认位置（任务栏右下角），已打开则立即重定位。</summary>
        internal void ResetPosition()
        {
            if (_adjustMode) _adjustMode = false;
            if (IsVisible) QuickPanelPosition.Apply(this, ConfigService.Load());
        }

        /// <summary>拖动松手：把当前窗口位置写入配置（Custom 模式），保存并通知主题页。</summary>
        private void SaveAdjustPosition()
        {
            try
            {
                QuickPanelPosition.Save(this, ConfigService.Load());
                _adjustMode = false;
                _host.ShowOsd("📍", L10n.T("Exp.PanelPosSaved"));
                _host.NotifyQuickPanelAdjustFinished();
            }
            catch { }
        }

        private async void OnSharedCurrentChanged()
        {
            try
            {
                if (!IsVisible) return;
                var cur = CurrentAppService.Current;
                if (cur == null) return;
                if (_currentApp != null && _currentApp.ProcessId == cur.ProcessId) return;
                await ResolveDefaultAppAsync();
            }
            catch { }
        }

        /// <summary>刷新数据并显示面板（先放到屏幕外，内容加载完再定位，避免闪烁/溢出）。</summary>
        public void ShowQuickPanel()
        {
            _everFocused = false;
            Left = -10000;
            Top = -10000;
            Show();
            Activate();
            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            try
            {
                var outputs = await Task.Run(() => AudioService.GetDevices(EDataFlow.eRender));
                var cfg = ConfigService.Load();

                _outputs = outputs;
                _outputDisplay = PanelDevices.WithSystemDefault(DisplayDevices(outputs), EDataFlow.eRender, cfg);

                RenderDeviceButtons(OutputButtonsPanel, _outputDisplay);

                // 输入设备（麦克风）：仅实验模式 + 麦克风选项 + 快捷面板显示时展示
                bool micOn = cfg.ExperimentalMic;
                bool showInput = micOn && cfg.MicInPanel;
                InputSection.Visibility = showInput ? Visibility.Visible : Visibility.Collapsed;
                if (micOn)
                {
                    var inputs = await Task.Run(() => AudioService.GetDevices(EDataFlow.eCapture));
                    _inputs = inputs;
                    _inputDisplay = PanelDevices.WithSystemDefault(DisplayDevices(inputs), EDataFlow.eCapture, cfg);
                    RenderInputDeviceButtons();
                }

                await ResolveDefaultAppAsync();

                await RefreshCurrentAppDataAsync();

                // 内容已就绪，重新定位到任务栏右下角（避免溢出屏幕）
                RequestPosition();
            }
            catch (Exception ex)
            {
                AppNameText.Text = L10n.T("Qp.LoadFail");
                PanelStatusText.Text = ex.Message;
            }
        }

        private List<AudioDeviceInfo> DisplayDevices(IEnumerable<AudioDeviceInfo> devs)
        {
            var cfg = ConfigService.Load();
            return devs.Select(d =>
            {
                string? custom = cfg.DeviceNames.TryGetValue(d.Id, out var n) ? n : null;
                return string.IsNullOrWhiteSpace(custom)
                    ? d
                    : new AudioDeviceInfo { Id = d.Id, DisplayName = custom, Flow = d.Flow, IsDefault = d.IsDefault };
            }).ToList();
        }

        /// <summary>按配置决定当前应用（与托盘滚轮/概览统一规则，优先共享的当前应用）。</summary>
        private async Task ResolveDefaultAppAsync()
        {
            var cfg = ConfigService.Load();
            var apps = await Task.Run(() => AudioService.GetApps());
            // 过滤：完整界面「应用」里关闭"在快速面板显示"的应用
            var hiddenPanel = cfg.HiddenPanelApps;
            apps = apps.Where(a => !string.IsNullOrWhiteSpace(a.ProcessName)
                && !hiddenPanel.Any(h => string.Equals(h, a.ProcessName, StringComparison.OrdinalIgnoreCase))).ToList();
            var items = apps.Select(AppItem.From).ToList();
            AppItem.LoadIconsAsync(items);

            var cur = CurrentAppService.Current;
            var target = cur != null
                ? apps.FirstOrDefault(a => a.ProcessId == cur.ProcessId)
                : null;
            target ??= CurrentAppService.Resolve(apps, cfg);

            _suppressAppCombo = true;
            AppCombo.ItemsSource = null;
            AppCombo.ItemsSource = items;
            AppCombo.SelectedItem = target == null ? null : items.FirstOrDefault(i => i.ProcessId == (int)target.ProcessId);
            _suppressAppCombo = false;

            SetCurrentApp(target);
        }

        private async void AppCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAppCombo) return;
            if (AppCombo.SelectedItem is AppItem item)
            {
                SetCurrentApp(item.Info);
                MarkLastUsed(item.Info);
                await RefreshCurrentAppDataAsync();
            }
        }

        private void MarkLastUsed(AudioAppInfo app)
        {
            var name = app.ProcessName;
            if (string.IsNullOrWhiteSpace(name)) return;
            var cfg = ConfigService.Load();
            cfg.LastUsedAppName = name;
            ConfigService.Save(cfg);
        }

        private void SetCurrentApp(AudioAppInfo? app)
        {
            _currentApp = app;
            CurrentAppService.Current = app; // 共享给快捷键/概览/托盘
            if (app == null)
                AppNameText.Text = L10n.T("Qp.NoAudio");
            else
                AppNameText.Text = string.Format(L10n.T("Qp.CurrentApp"), AppDisplayName.Get(app));
        }

        private async Task RefreshCurrentAppDataAsync()
        {
            if (_currentApp == null)
            {
                OutputCurrentText.Text = "—";
                InputCurrentText.Text = "—";
                VolumeSlider.Value = 0;
                VolumePercentText.Text = "0%";
                return;
            }

            var pid = (int)_currentApp.ProcessId;
            var outId = await Task.Run(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender));

            _currentOutId = outId == null ? null : AudioPolicyConfig.UnpackDeviceId(outId);

            OutputCurrentText.Text = DescribeCurrent(_outputDisplay, _currentOutId);

            HighlightActive(OutputButtonsPanel, _currentOutId);

            // 输入设备（麦克风）：与输出对称
            var inId = await Task.Run(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eCapture));
            _currentInId = inId == null ? null : AudioPolicyConfig.UnpackDeviceId(inId);
            InputCurrentText.Text = DescribeCurrent(_inputDisplay, _currentInId);
            HighlightInputActive();

            var vol = await Task.Run(() =>
            {
                SessionVolumeService.Refresh();
                return (pct: SessionVolumeService.GetVolumePercent(pid), muted: SessionVolumeService.IsMuted(pid));
            });

            // 无论有无输出会话，都同步麦克风静音按钮文案（全局状态与应用无关，切换应用后不残留旧状态）
            bool globalMicMuted = await Task.Run(() => GlobalMicMuteService.IsMuted());
            ApplyMicMuteVisual(globalMicMuted);

            if (vol.pct >= 0)
            {
                VolumeSlider.Value = vol.pct;   // 此时 _volumeReady 仍为 false，ValueChanged 不会写回
                VolumePercentText.Text = $"{vol.pct}%";
                ApplyMuteVisual(vol.muted);
                _volumeReady = true;
                VolumeSlider.IsEnabled = true;
                MinusButton.IsEnabled = true;
                PlusButton.IsEnabled = true;
                MuteButton.IsEnabled = true;
            }
            else
            {
                _volumeReady = false;
                VolumeSlider.Value = 0;
                VolumePercentText.Text = "—";
                ApplyMuteVisual(false);
                VolumeSlider.IsEnabled = false;
                MinusButton.IsEnabled = false;
                PlusButton.IsEnabled = false;
                MuteButton.IsEnabled = false;
            }
        }

        private static string DescribeCurrent(List<AudioDeviceInfo> devices, string? currentShortId)
        {
            if (currentShortId == null)
            {
                var sysDef = devices.FirstOrDefault(d => AudioService.IsSystemDefault(d.Id));
                return sysDef != null ? L10n.T("Ov.Default") + sysDef.DisplayName! : L10n.T("Ov.Unset");
            }
            var dev = devices.FirstOrDefault(d => string.Equals(d.Id, currentShortId, StringComparison.OrdinalIgnoreCase));
            return dev != null ? dev.DisplayName! : L10n.T("Ov.CurrentUnavailable");
        }

        // ------------------------------------------------------------------
        // 设备按钮渲染 + 点击切换
        // ------------------------------------------------------------------

        private void RenderDeviceButtons(ItemsControl panel, List<AudioDeviceInfo> devices)
        {
            panel.Items.Clear();
            var cfg = ConfigService.Load();
            foreach (var dev in devices)
            {
                if (cfg.HiddenOutputDevices.Contains(dev.Id)) continue;
                var btn = new Button
                {
                    Content = ShortName(dev.DisplayName),
                    Tag = dev,
                    ToolTip = dev.DisplayName
                };
                btn.SetResourceReference(StyleProperty, "DevButton");
                btn.Click += (_, _) => OnDeviceButtonClick(dev);
                panel.Items.Add(btn);
            }
        }

        private void HighlightActive(ItemsControl panel, string? activeShortId)
        {
            foreach (var item in panel.Items)
            {
                if (item is not Button btn || btn.Tag is not AudioDeviceInfo dev) continue;
                bool active = (activeShortId == null && AudioService.IsSystemDefault(dev.Id))
                              || (activeShortId != null && string.Equals(dev.Id, activeShortId, StringComparison.OrdinalIgnoreCase));
                btn.SetResourceReference(StyleProperty, active ? "DevButtonActive" : "DevButton");
            }
        }

        private async void OnDeviceButtonClick(AudioDeviceInfo dev)
        {
            if (_currentApp == null)
            {
                PanelStatusText.Text = L10n.T("Qp.NoAudioMsg");
                return;
            }

            MarkLastUsed(_currentApp);
            var pid = (int)_currentApp.ProcessId;
            var (ok, _, msg) = await Task.Run(() => AudioService.ApplyEndpoint(pid, EDataFlow.eRender, dev.Id));
            if (ok)
                PanelStatusText.Text = string.Format(L10n.T("Qp.SwitchOk"), "🔊 " + dev.DisplayName, AppDisplayName.Get(_currentApp));
            else
                PanelStatusText.Text = $"✗ {msg}";

            await RefreshCurrentAppDataAsync();
        }

        // ------------------------------------------------------------------
        // 输入设备（麦克风）：与输出完全对称（实验模式 + 麦克风选项 + 快捷面板显示）
        // ------------------------------------------------------------------

        private void RenderInputDeviceButtons()
        {
            InputButtonsPanel.Items.Clear();
            var cfg = ConfigService.Load();
            foreach (var dev in _inputDisplay)
            {
                if (cfg.HiddenInputDevices.Contains(dev.Id)) continue;
                var btn = new Button
                {
                    Content = ShortName(dev.DisplayName),
                    Tag = dev,
                    ToolTip = dev.DisplayName
                };
                btn.SetResourceReference(StyleProperty, "DevButton");
                btn.Click += (_, _) => OnInputDeviceButtonClick(dev);
                InputButtonsPanel.Items.Add(btn);
            }
            HighlightInputActive();
        }

        private void HighlightInputActive()
        {
            foreach (var item in InputButtonsPanel.Items)
            {
                if (item is not Button btn || btn.Tag is not AudioDeviceInfo dev) continue;
                bool active = (_currentInId == null && AudioService.IsSystemDefault(dev.Id))
                              || (_currentInId != null && string.Equals(dev.Id, _currentInId, StringComparison.OrdinalIgnoreCase));
                btn.SetResourceReference(StyleProperty, active ? "DevButtonActive" : "DevButton");
            }
        }

        private async void OnInputDeviceButtonClick(AudioDeviceInfo dev)
        {
            if (_currentApp == null)
            {
                PanelStatusText.Text = L10n.T("Qp.NoAudioMsg");
                return;
            }

            MarkLastUsed(_currentApp);
            var pid = (int)_currentApp.ProcessId;
            var (ok, _, msg) = await Task.Run(() => AudioService.ApplyEndpoint(pid, EDataFlow.eCapture, dev.Id));
            if (ok)
                PanelStatusText.Text = string.Format(L10n.T("Qp.SwitchOk"), "🎤 " + dev.DisplayName, AppDisplayName.Get(_currentApp));
            else
                PanelStatusText.Text = $"✗ {msg}";

            await RefreshCurrentAppDataAsync();
        }

        private static string ShortName(string? full)
        {
            if (string.IsNullOrWhiteSpace(full)) return "(未知设备)";
            int paren = full.IndexOf('(');
            string head = paren > 0 ? full.Substring(0, paren).Trim() : full;
            if (head.Length <= 12) return head;
            return head.Substring(0, 11) + "…";
        }

        // ------------------------------------------------------------------
        // 音量
        // ------------------------------------------------------------------

        private async void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            VolumePercentText.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (!_volumeReady || _currentApp == null) return;
            int pct = (int)Math.Round(e.NewValue);
            var pid = (int)_currentApp.ProcessId;

            bool ok = await Task.Run(() => SessionVolumeService.SetVolumePercent(pid, pct));
            int actual = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
            if (actual >= 0)
            {
                VolumeSlider.Value = actual;
                VolumePercentText.Text = $"{actual}%";
            }
            PanelStatusText.Text = ok && actual >= 0
                ? string.Format(L10n.T("Qp.VolOk"), actual)
                : L10n.T("Qp.VolFail");
        }

        /// <summary>调整当前应用音量（delta 为 ±n 百分比），同步面板滑块/百分比/状态行。
        /// 供音量快捷键与面板 ± 按钮共用，保证快捷键调的就是面板/概览显示的当前应用。
        /// 返回调整后的实际音量；无当前应用/无输出会话返回 -1。</summary>
        public async Task<int> AdjustVolumeAsync(int delta)
        {
            if (_currentApp == null || !_volumeReady) return -1;
            MarkLastUsed(_currentApp);
            var pid = (int)_currentApp.ProcessId;
            int cur = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
            if (cur < 0) return -1;
            int next = Math.Clamp(cur + delta, 0, 100);
            bool ok = await Task.Run(() => SessionVolumeService.SetVolumePercent(pid, next));
            int actual = await Task.Run(() => SessionVolumeService.GetVolumePercent(pid));
            if (actual >= 0)
            {
                VolumeSlider.Value = actual;
                VolumePercentText.Text = $"{actual}%";
                PanelStatusText.Text = string.Format(L10n.T("Qp.VolOk"), actual);
            }
            return actual >= 0 ? actual : -1;
        }

        private void MinusButton_Click(object sender, RoutedEventArgs e)
        {
            VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
        }

        private void PlusButton_Click(object sender, RoutedEventArgs e)
        {
            VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
        }

        private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_currentApp == null || !_volumeReady) return;
            int step = Math.Clamp(ConfigService.Load().VolumeStep, 1, 20);
            int pct = (int)Math.Round(VolumeSlider.Value) + (e.Delta > 0 ? step : -step);
            VolumeSlider.Value = Math.Clamp(pct, 0, 100);
            e.Handled = true;
        }

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            await MuteCurrentAppAsync();
        }

        /// <summary>全局麦克风静音：文本固定「麦克风静音」，静音状态文字变强调色。</summary>
        private void ApplyMicMuteVisual(bool muted)
        {
            MicMuteButton.Content = L10n.T("Qp.MicMute");
            if (muted) MicMuteButton.Foreground = (System.Windows.Media.Brush)FindResource("Theme.Accent");
            else MicMuteButton.ClearValue(Button.ForegroundProperty);
        }

        /// <summary>静音按钮：文本固定「静音」，静音状态文字变强调色（不切换文案）。</summary>
        private void ApplyMuteVisual(bool muted)
        {
            MuteButton.Content = L10n.T("Qp.Mute");
            if (muted) MuteButton.Foreground = (System.Windows.Media.Brush)FindResource("Theme.Accent");
            else MuteButton.ClearValue(Button.ForegroundProperty);
        }

        /// <summary>静音/取消静音当前应用（面板显示的应用）。面板按钮与静音快捷键共用同一条
        /// 路径，保证快捷键静音的就是面板/概览显示的同一个当前应用；同时回写面板按钮文字与
        /// 状态行，让快捷键操作在面板上有可见反馈。返回是否真正执行。</summary>
        public async Task<bool> MuteCurrentAppAsync()
        {
            if (_currentApp == null || !_volumeReady) return false;
            MarkLastUsed(_currentApp);
            bool muted = await Task.Run(() => SessionVolumeService.ToggleMute((int)_currentApp.ProcessId));
            ApplyMuteVisual(muted);
            PanelStatusText.Text = L10n.T(muted ? "Qp.Muted" : "Qp.Unmuted");
            return true;
        }

        private async void MicMuteButton_Click(object sender, RoutedEventArgs e)
        {
            await ToggleGlobalMicMuteAsync();
        }

        /// <summary>全局麦克风静音：直接静音/取消静音系统所有录音设备（设备级），
        /// 与当前应用无关，所有应用录音都生效。面板按钮与麦克风静音快捷键共用。
        /// 返回是否已静音。</summary>
        public async Task<bool> ToggleGlobalMicMuteAsync()
        {
            bool muted = await Task.Run(() => GlobalMicMuteService.Toggle());
            ApplyMicMuteVisual(muted);
            PanelStatusText.Text = L10n.T(muted ? "Qp.MicMuted" : "Qp.MicUnmuted");
            return muted; // 返回真实静音状态（快捷键共用：切换后立即更新 OSD）
        }

        // ------------------------------------------------------------------
        // 底部按钮
        // ------------------------------------------------------------------

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            _host.ShowMainWindow();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
            base.OnKeyDown(e);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Image = System.Windows.Controls.Image;
using Brush = System.Windows.Media.Brush;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using SonicRoute.Core;
using SonicRoute.Core.Compat;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SonicRoute
{
    /// <summary>
    /// 简洁面板（新默认）：Windows 11 音量飞出式布局。
    /// 顶部 = 系统音频设备（选择输出设备为控制对象，大滑块/静音控制系统设备音量，不改默认设备）；
    /// 下方列出全部有音频的应用，每行 = 图标（点击静音/取消静音）+ 音量条 + 百分比 + ▾（展开设置
    /// 该应用输出/输入设备）。静音后音量条变强调色 RGB 反色。底部：全局输出静音 + 全局麦克风静音 + 完整界面。
    /// 保留全部优化：滚轮调系统音量、失焦自动关闭、Esc 关闭、保留设备筛选、自定义设备名、OSD 通知。
    /// </summary>
    public partial class QuickPanelModernWindow : Window, IQuickPanel
    {
        private sealed class AppRow
        {
            public AudioAppInfo App = null!;
            public Border HeaderBorder = null!;
            public Slider Slider = null!;
            public Border MuteDot = null!;
            public Border? TrackBg;
            public Border? TrackFill;
            public Border? PeakVisual;
            public ScaleTransform? PeakTransform;
            public float PeakLevel;
            public bool IsMuted;
            public Thumb? Thumb;
            public ToggleButton ExpandButton = null!;
            public StackPanel ExpandPanel = null!;
            public AnimatedExpandHost ExpandWrap = null!;
            public RotateTransform ExpandArrow = new();
            public readonly List<(Button Button, string Id, EDataFlow Flow)> DeviceButtons = new();
            public string? DeviceSignature;
            public int DeviceBuildVersion;
            public FrameworkElement? RootElement; // 行根容器（AppListPanel 的 item）：展开后滚动定位到该行
            public bool Expanded;
        }

        private readonly Dictionary<int, AppRow> _rows = new();
        private Task? _loadTask;
        private readonly PanelEntranceMotion _entranceMotion;
        private bool _contentReady;
        private int _loadVersion;
        private int _globalMuteVersion;
        private int _micMuteVersion;
        private AppRow? _recentDeviceRow;
        private AppRow? _scrollingRow;
        private string? _appRowsSignature;
        private bool _updatingRowVolumes;
        private readonly Dictionary<int, int> _pendingVolume = new();
        private DispatcherTimer? _volDebounce;
        private AudioMeterService? _meter;
        private bool _meterEnabled;
        private bool _isClosed;
        private readonly object _peakGate = new();
        private IReadOnlyDictionary<int, float>? _latestPeaks;
        private int _peakDispatchPending;

        private List<AudioDeviceInfo> _outputDisplay = new();
        private List<AudioDeviceInfo> _inputDisplay = new();
        private List<AudioDeviceInfo> _systemDevices = new();
        private string? _systemDeviceId;
        private bool _systemReady;
        private bool _suppressDevCombo;
        private bool _everFocused;
        private bool _micUiOn;
        private bool _adjustMode;          // 主题页「调整快速面板位置」：可拖拽，松手保存
        private bool _adjustDragging;
        private System.Windows.Point _adjustDragStart;
        private bool _positionPending;     // 合并定位请求：同帧多次触发（SizeChanged/列表刷新/展开收起）只定位一次
        private const double RowHeight = 38; // 应用行高（行头 36 + 行底距 2）：列表区高度 = 最多显示行数 × RowHeight

        public QuickPanelModernWindow()
        {
            InitializeComponent();
            _entranceMotion = new PanelEntranceMotion(PanelSurface, PanelSlide);
            AppListScroll.PreviewMouseWheel += (_, _) => StopRowScroll();
            AppListScroll.PreviewMouseDown += (_, _) => StopRowScroll();
            AppListScroll.PreviewKeyDown += (_, _) => StopRowScroll();
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
                Left = MathEx.Clamp(Left + (p.X - _adjustDragStart.X), minX, Math.Max(minX, maxX));
                Top = MathEx.Clamp(Top + (p.Y - _adjustDragStart.Y), minY, Math.Max(minY, maxY));
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
            // 面板高度恒定（固定值，不再随内容/展开变化）：尺寸变化仅发生在应用固定高度与列表行数时，
            // 合并请求重定位（默认模式锚定右下角；自定义位置保持用户设定不重设）
            SizeChanged += (_, _) =>
            {
                if (!IsVisible || _adjustMode) return;
                RequestPosition();
            };
            // 工作区变化（分辨率/任务栏位置/DPI 缩放）时重定位默认位置
            SystemParameters.StaticPropertyChanged += OnSystemParamChanged;
            // 共享"当前应用"变化（前台自动跟随/概览切换）时同步面板高亮
            CurrentAppService.CurrentChanged += OnSharedCurrentChanged;
            Closed += (_, _) =>
            {
                _isClosed = true;
                ++_loadVersion;
                _entranceMotion.Stop();
                ClearRowVisuals();
                StopMeter();
                _volDebounce?.Stop();
                if (_volDebounce != null) _volDebounce.Tick -= VolDebounce_Tick;
                CurrentAppService.CurrentChanged -= OnSharedCurrentChanged;
                SystemParameters.StaticPropertyChanged -= OnSystemParamChanged;
                if (_adjustMode) { _adjustMode = false; ((App)Application.Current).NotifyQuickPanelAdjustFinished(); }
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
            if (!_contentReady || _isClosed) return;
            if (_positionPending) return;
            _positionPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _positionPending = false;
                if (_isClosed || !IsVisible || _adjustDragging) return;
                // 调整模式：面板已可见时不自动定位（让用户拖拽）；刚打开仍在屏幕外时仍先定位，确保可见可拖
                if (_adjustMode && Left >= -5000 && Top >= -5000) return;
                // Apply 内部：Custom 直接设回用户保存坐标（相同值 WPF 不移动窗口，仅完成首次定位/拉回偏离位置），
                // 默认锚定主显示器工作区右下角
                QuickPanelPosition.Apply(this, ConfigService.Load());
            });
        }

        /// <summary>应用固定面板高度：窗口高度 = 用户设置值（QuickPanelHeight，400–800，clamp 工作区）。
        /// 面板高度自此恒定——展开/收起设备区只在固定高度的应用区域内变化，窗口高度永不变；
        /// 底部操作栏固定贴底，内容超出在应用区域内滚动。</summary>
        private void ApplyFixedPanelHeight(SonicRoute.Core.AppConfig cfg)
        {
            try
            {
                // 先量固定部分：列表区压到 0 后布局，取内容期望高度（Grid 下 ActualHeight 会被窗口高度撑满，
                // 用 DesiredSize 得到标题/设备/音量/分隔/底部操作栏的固定高总和）
                AppListScroll.Height = 0;
                AppListScroll.UpdateLayout();
                double fixedH = RootBorder.DesiredSize.Height;
                if (fixedH <= 0) fixedH = RootBorder.ActualHeight; // 兜底
                double total = MathEx.Clamp(cfg.QuickPanelHeight, 350, 800);
                var wa = SystemParameters.WorkArea;
                double maxTotal = Math.Max(240, wa.Height - 16); // 不超屏幕限度
                if (total > maxTotal) total = maxTotal;
                double listH = Math.Max(RowHeight, total - fixedH); // 应用区域 = 窗口高 - 固定部分（不足时至少一行）
                AppListScroll.Height = listH;
                Height = Math.Round(total);
            }
            catch { }
        }

        /// <summary>重新读取配置并立即应用面板固定高度（设置页修改后调用）。</summary>
        public void ApplyPanelHeightFromConfig()
        {
            try
            {
                var cfg = SonicRoute.Core.ConfigService.Load();
                ApplyFixedPanelHeight(cfg);
            }
            catch { }
        }

        /// <summary>展开后滚动应用列表，使目标行（含设备区）可见；列表区高度固定，超出的行滚动查看。</summary>
        private void ScrollRowIntoView(AppRow row)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                try
                {
                    if (_isClosed || !row.Expanded) return;
                    if (row.RootElement == null) return;
                    double y = 0;
                    foreach (var item in AppListPanel.Items)
                    {
                        if (item is not FrameworkElement fe) continue;
                        if (fe == row.RootElement)
                        {
                            AppListScroll.ScrollToVerticalOffset(Math.Max(0, y));
                            return;
                        }
                        y += fe.ActualHeight;
                    }
                    AppListScroll.ScrollToEnd();
                }
                catch { }
            });
        }

        /// <summary>进入/退出位置调整模式（主题页调用）。</summary>
        internal void SetAdjustMode(bool on)
        {
            _adjustMode = on;
            if (on) ((App)Application.Current).ShowOsd(L10n.T("Exp.PanelPosDragTitle"), L10n.T("Exp.PanelPosHint"));
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
                ((App)Application.Current).ShowOsd("📍", L10n.T("Exp.PanelPosSaved"));
                ((App)Application.Current).NotifyQuickPanelAdjustFinished();
            }
            catch { }
        }

        private void OnSharedCurrentChanged()
        {
            try
            {
                if (!IsVisible) return;
                UpdateRowHighlights();
            }
            catch { }
        }

        /// <summary>刷新数据并显示面板（定位到任务栏右下角）。</summary>
        private bool _entrancePlayed;

        /// <summary>定位和布局就绪后，内部背景与内容一起做 160ms 入场，窗口坐标保持固定。</summary>
        private void PlayEntranceAnimation()
        {
            _entranceMotion.Play();
        }

        public void ShowQuickPanel()
        {
            _everFocused = false;
            if (IsVisible && _entranceMotion.IsRunning)
            {
                // 动画进行中再次呼出：不位移、不重播动画，仅激活并刷新内容（避免闪烁/抖动）
                _entrancePlayed = true;
                Show();
                Activate();
                _ = LoadAsync();
                return;
            }
            if (!IsVisible)
            {
                // 先放到屏幕外，内容加载完再由 LoadAsync 定位，避免闪烁/溢出
                Left = -10000;
                Top = -10000;
                _contentReady = false;
                _entrancePlayed = false;
                _entranceMotion.Prepare();
            }
            else
            {
                // 已可见但动画已完成：内容刷新，不重播入场动画，保持原位
                _entrancePlayed = true;
            }
            Show();
            Activate();
            _ = LoadAsync();
        }

        private Task LoadAsync()
        {
            // 重复呼出共用正在进行的加载，避免多轮枚举、清空和重建互相覆盖。
            if (_loadTask != null && !_loadTask.IsCompleted) return _loadTask;
            return _loadTask = LoadCoreAsync();
        }

        private async Task LoadCoreAsync()
        {
            try
            {
                var cfg = ConfigService.Load();
                int loadVersion = ++_loadVersion;
                _micUiOn = cfg.ExperimentalMic && cfg.MicInPanel;
                GlobalMuteButton.IsEnabled = false;
                MicMuteButton.IsEnabled = false;
                var devices = await Task.Run(() =>
                {
                    var outputs = AudioService.GetDevices(EDataFlow.eRender);
                    var inputs = cfg.ExperimentalMic ? AudioService.GetDevices(EDataFlow.eCapture) : new List<AudioDeviceInfo>();
                    return (Outputs: outputs, Inputs: inputs, DefaultId: AudioService.GetDefaultDeviceId(EDataFlow.eRender));
                });
                if (_isClosed) return;
                _outputDisplay = PanelDevices.WithSystemDefault(DisplayDevices(devices.Outputs), EDataFlow.eRender, cfg);
                _inputDisplay = cfg.ExperimentalMic
                    ? PanelDevices.WithSystemDefault(DisplayDevices(devices.Inputs), EDataFlow.eCapture, cfg)
                    : new List<AudioDeviceInfo>();

                // 顶部系统音频设备
                await LoadSystemDevicesAsync(devices.Outputs, devices.DefaultId);
                if (_isClosed) return;

                var apps = await Task.Run(() => AudioService.GetApps());
                if (_isClosed) return;
                await BuildAppRowsAsync(apps);
                if (_isClosed) return;
                SetMeterEnabled(ConfigService.Load().ShowAppPeakMeter);
                ApplyFixedPanelHeight(cfg);
                // 先让 WPF 完成布局；最终定位与动画在同一个回调中执行，避免动画在屏幕外提前运行。
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_isClosed || !IsVisible || loadVersion != _loadVersion) return;
                    _contentReady = true;
                    QuickPanelPosition.Apply(this, cfg);
                    if (!_entrancePlayed) { _entrancePlayed = true; PlayEntranceAnimation(); }
                }, DispatcherPriority.Background);
                if (_isClosed) return;
                await RefreshGlobalMuteStateAsync(loadVersion);
            }
            catch (Exception)
            {
                if (!_isClosed) ShowOsd(L10n.T("Qp.Panel"), L10n.T("Qp.LoadFail"));
            }
        }

        private async Task RefreshGlobalMuteStateAsync(int loadVersion)
        {
            int outputVersion = _globalMuteVersion, micVersion = _micMuteVersion;
            try
            {
                var state = await Task.Run(() => (Output: SessionVolumeService.AllMuted(), Mic: GlobalMicMuteService.IsMuted()));
                if (_isClosed || loadVersion != _loadVersion) return;
                if (outputVersion == _globalMuteVersion) { ApplyGlobalMuteVisual(state.Output); GlobalMuteButton.IsEnabled = true; }
                if (micVersion == _micMuteVersion) { ApplyMicMuteVisual(state.Mic); MicMuteButton.IsEnabled = true; }
            }
            catch { /* 底部状态读取失败不打断已准备好的面板，下次刷新重试。 */ }
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

        // ------------------------------------------------------------------
        // 顶部：系统音频设备（选择控制对象 + 系统设备音量/静音；不修改系统默认设备）
        // ------------------------------------------------------------------

        private async Task LoadSystemDevicesAsync(List<AudioDeviceInfo> devs, string? defaultId)
        {
            _systemDevices = DisplayDevices(devs);
            foreach (var d in _systemDevices) d.IsDefault = d.Id == defaultId;

            _suppressDevCombo = true;
            SystemDevCombo.ItemsSource = null;
            SystemDevCombo.ItemsSource = _systemDevices;
            SystemDevCombo.SelectedItem = _systemDevices.FirstOrDefault(d => d.IsDefault);
            _suppressDevCombo = false;

            _systemDeviceId = (SystemDevCombo.SelectedItem as AudioDeviceInfo)?.Id ?? defaultId;
            await RefreshSystemVolumeAsync();
        }

        private async void SystemDevCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDevCombo) return;
            if (SystemDevCombo.SelectedItem is AudioDeviceInfo dev)
            {
                _systemDeviceId = dev.Id;
                // 设置「简洁面板更改系统默认设备」开启时：下拉切换 = 更改系统默认输出设备（IPolicyConfig.SetDefaultEndpoint）
                if (SonicRoute.Core.ConfigService.Load().PanelChangeSystemDefault)
                {
                    var ok = await Task.Run(() => SystemDefaultDeviceService.SetDefault(EDataFlow.eRender, dev.Id));
                    if (!ok.Success)
                    {
                        ((App)System.Windows.Application.Current).ShowOsd(L10n.T("Act.SetDefaultOutput"), L10n.T("St.SysDefaultSetFail"));
                    }
                }
            }
            await RefreshSystemVolumeAsync();
        }

        /// <summary>全局输出静音/全局麦克风静音：文本固定，静音状态文字变强调色。</summary>
        private void ApplyGlobalMuteVisual(bool muted)
        {
            GlobalMuteButton.Content = L10n.T("Qp.GlobalMute");
            if (muted) GlobalMuteButton.Foreground = (Brush)FindResource("Theme.Accent");
            else GlobalMuteButton.ClearValue(Button.ForegroundProperty);
        }

        private void ApplyMicMuteVisual(bool muted)
        {
            MicMuteButton.Content = L10n.T("Qp.MicMute");
            if (muted) MicMuteButton.Foreground = (Brush)FindResource("Theme.Accent");
            else MicMuteButton.ClearValue(Button.ForegroundProperty);
        }

        /// <summary>顶部静音按钮：文本固定「静音」，静音状态文字变强调色（不切换文案）。</summary>
        private void ApplySystemMuteVisual(bool muted)
        {
            MuteButton.Content = L10n.T("Qp.Mute");
            if (muted) MuteButton.Foreground = (Brush)FindResource("Theme.Accent");
            else MuteButton.ClearValue(Button.ForegroundProperty);
        }

        /// <summary>读取所选系统设备的音量/静音，同步顶部滑块/百分比/按钮。</summary>
        private async Task RefreshSystemVolumeAsync()
        {
            var (vol, muted) = await Task.Run(() =>
            {
                var v = SystemVolumeService.GetVolumePercent(_systemDeviceId);
                var m = SystemVolumeService.IsMuted(_systemDeviceId);
                return (v, m);
            });

            _systemReady = false;
            if (vol < 0)
            {
                VolumeSlider.Value = 0;
                VolumePercentText.Text = "—";
                VolumeSlider.IsEnabled = false;
                MuteButton.IsEnabled = false;
                MuteButton.Content = L10n.T("Qp.Mute");
                MuteButton.ClearValue(Button.ForegroundProperty);
                return;
            }
            VolumeSlider.Value = vol;
            VolumePercentText.Text = $"{vol}%";
            ApplySystemMuteVisual(muted);
            VolumeSlider.IsEnabled = true;
            MuteButton.IsEnabled = true;
            _systemReady = true;
        }

        private async void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            VolumePercentText.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (!_systemReady) return;
            int pct = (int)Math.Round(e.NewValue);
            string? deviceId = _systemDeviceId;
            var result = await Task.Run(() =>
            {
                bool applied = SystemVolumeService.SetVolumePercent(deviceId, pct);
                return (Applied: applied, Actual: SystemVolumeService.GetVolumePercent(deviceId));
            });
            if (_isClosed || deviceId != _systemDeviceId) return;
            int actual = result.Actual;
            if (actual >= 0)
                ApplySystemVolumeReadback(actual);
            ShowDeviceOsd(result.Applied && actual >= 0
                ? string.Format(L10n.T("Qp.VolOk"), actual)
                : L10n.T("Qp.VolFail"));
        }

        private void ApplySystemVolumeReadback(int actual)
        {
            bool ready = _systemReady;
            _systemReady = false;
            try
            {
                VolumeSlider.Value = actual;
                VolumePercentText.Text = $"{actual}%";
            }
            finally { _systemReady = ready; }
        }

        private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_systemReady) return;
            int step = MathEx.Clamp(ConfigService.Load().VolumeStep, 1, 20);
            int pct = (int)Math.Round(VolumeSlider.Value) + (e.Delta > 0 ? step : -step);
            VolumeSlider.Value = MathEx.Clamp(pct, 0, 100);
            e.Handled = true;
        }

        /// <summary>操作反馈改为右上角 OSD 通知（避免面板状态文本顶掉底部按钮）。</summary>
        private void ShowOsd(string text) => ((App)Application.Current).ShowOsd(L10n.T("Ov.VolumeTitle"), text);

        /// <summary>OSD 通知（自定义主标题 + 副标题）。</summary>
        private void ShowOsd(string title, string text) => ((App)Application.Current).ShowOsd(title, text);

        /// <summary>设备音量/静音 OSD：主标题显示当前所选系统设备名（跟随设置的自定义设备名，无则默认名）。</summary>
        private void ShowDeviceOsd(string text)
        {
            string title = (SystemDevCombo.SelectedItem as AudioDeviceInfo)?.DisplayName ?? L10n.T("App.NameFull");
            ((App)Application.Current).ShowOsd(title, text);
        }

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_systemReady) return;
            bool muted = await Task.Run(() => SystemVolumeService.ToggleMute(_systemDeviceId));
            ApplySystemMuteVisual(muted);
            ShowDeviceOsd(L10n.T(muted ? "Qp.Muted" : "Qp.Unmuted"));
        }

        /// <summary>调整系统设备音量（delta 为 ±n 百分比），同步顶部滑块/百分比/状态行。
        /// 供音量快捷键与面板共用，保证快捷键调的就是面板顶部显示的系统设备音量。
        /// 返回调整后的实际音量；无可用设备返回 -1。</summary>
        public async Task<int> AdjustVolumeAsync(int delta)
        {
            if (!_systemReady) return -1;
            string? deviceId = _systemDeviceId;
            var result = await Task.Run(() => SystemVolumeService.AdjustVolumePercent(deviceId, delta));
            int actual = result.Actual;
            if (_isClosed || deviceId != _systemDeviceId) return actual;
            if (actual >= 0)
            {
                ApplySystemVolumeReadback(actual);
                ShowDeviceOsd(string.Format(L10n.T("Qp.VolOk"), actual));
            }
            return actual >= 0 ? actual : -1;
        }

        /// <summary>静音/取消静音顶部选中的系统输出设备（快捷键与面板顶部按钮一致；不修改系统默认设备）。</summary>
        public async Task<bool> MuteCurrentAppAsync()
        {
            if (!_systemReady) return false;
            bool muted = await Task.Run(() => SystemVolumeService.ToggleMute(_systemDeviceId));
            ApplySystemMuteVisual(muted);
            ShowDeviceOsd(L10n.T(muted ? "Qp.Muted" : "Qp.Unmuted"));
            return true;
        }
        // ------------------------------------------------------------------

        private string GetAppRowsSignature(List<AudioAppInfo> apps, AppConfig cfg)
        {
            var text = new StringBuilder();
            void Add(string? value) => text.Append(value?.Length ?? -1).Append(':').Append(value).Append(';');
            Add(cfg.Language); Add(cfg.ThemeMode); Add(cfg.Accent); Add(_micUiOn.ToString());
            foreach (var app in apps)
            {
                Add(app.ProcessId.ToString()); Add(app.ProcessName); Add(app.DisplayName); Add(AppDisplayName.Get(app));
            }
            foreach (var device in _outputDisplay) { Add(device.Id); Add(device.DisplayName); }
            foreach (var device in _inputDisplay) { Add(device.Id); Add(device.DisplayName); }
            foreach (var id in cfg.HiddenOutputDevices) Add(id);
            Add(null);
            foreach (var id in cfg.HiddenInputDevices) Add(id);
            return text.ToString();
        }

        private async Task BuildAppRowsAsync(List<AudioAppInfo> apps)
        {
            try
            {
                // 过滤：完整界面「应用」里关闭"在快速面板显示"的应用不列出
                var cfg = ConfigService.Load();
                var hiddenPanel = cfg.HiddenPanelApps;
                apps = apps.Where(a => !string.IsNullOrWhiteSpace(a.ProcessName)
                    && !hiddenPanel.Any(h => string.Equals(h, a.ProcessName, StringComparison.OrdinalIgnoreCase))).ToList();
                string signature = GetAppRowsSignature(apps, cfg);

                // 批量读各应用音量/静音（UI 线程外，避免逐行 COM 开销）
                var vols = await Task.Run(() =>
                {
                    SessionVolumeService.Refresh();
                    var d = new Dictionary<int, (int vol, bool muted)>();
                    foreach (var a in apps)
                    {
                        var pid = (int)a.ProcessId;
                        d[pid] = (SessionVolumeService.GetVolumePercent(pid), SessionVolumeService.IsMuted(pid));
                    }
                    return d;
                });
                if (_isClosed) return;
                _updatingRowVolumes = true;
                if (_appRowsSignature == signature)
                {
                    foreach (var app in apps)
                    {
                        var row = _rows[(int)app.ProcessId];
                        row.App = app;
                        var value = vols[(int)app.ProcessId];
                        row.Slider.IsEnabled = value.vol >= 0;
                        if (value.vol >= 0) row.Slider.Value = value.vol;
                        ApplyRowMutedVisual(row, value.vol >= 0 && value.muted);
                    }
                    UpdateMeterTargets();
                    UpdateRowHighlights();
                    return;
                }
                StopMeter();
                ClearRowVisuals();
                AppListPanel.Items.Clear();
                _rows.Clear();
                _appRowsSignature = null;

                var pendingIcons = new List<AppItem>();
                foreach (var app in apps)
                {
                    var pid = (int)app.ProcessId;
                    var item = AppItem.From(app);
                    pendingIcons.Add(item);
                    var row = new AppRow { App = app };

                    // 行头：图标（可点击静音）+ 百分比 + ▾ + 音量条
                    var header = new Border
                    {
                        Height = 36,
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(4, 0, 4, 0)
                    };
                    var dock = new DockPanel();

                    // 图标按钮（点击静音/取消静音），右上角静音点
                    var iconGrid = new Grid { Width = 30, Height = 30, Margin = new Thickness(0, 0, 2, 0) };
                    var img = new Image
                    {
                        Width = 18,
                        Height = 18,
                        SnapsToDevicePixels = true
                    };
                    // 懒加载图标：初始 Source 为空，后台加载完成后 AppItem.Icon 触发 PropertyChanged，
                    // 通过 OneWay 绑定自动刷新（不能一次性赋值，否则图标永远空白）。
                    img.DataContext = item;
                    img.SetBinding(Image.SourceProperty,
                        new System.Windows.Data.Binding(nameof(AppItem.Icon))
                        { Mode = System.Windows.Data.BindingMode.OneWay });
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    var iconBtn = new Button
                    {
                        Content = img,
                        Style = (Style)FindResource("RowIconButton"),
                        Tag = row,
                        ToolTip = item.Label,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center,
                        Padding = new Thickness(2)
                    };
                    iconBtn.Click += async (_, _) => await RowIconToggleMuteAsync(row);
                    iconGrid.Children.Add(iconBtn);
                    row.MuteDot = new Border
                    {
                        Width = 8,
                        Height = 8,
                        CornerRadius = new CornerRadius(4),
                        Background = ThemeService.GetInvertedAccentBrush(),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        VerticalAlignment = System.Windows.VerticalAlignment.Top,
                        Margin = new Thickness(0, 2, 1, 0),
                        Visibility = Visibility.Collapsed
                    };
                    iconGrid.Children.Add(row.MuteDot);
                    DockPanel.SetDock(iconGrid, Dock.Left);

                    var expand = new ToggleButton
                    {
                        Width = 32,
                        Height = 16,
                        MinWidth = 32,
                        MinHeight = 16,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center,
                        Style = (Style)FindResource("RowExpandButton"),
                        Tag = row,
                        ToolTip = L10n.T("Qp.ExpandDevices")
                    };
                    var glyph = new TextBlock { Text = "▾", RenderTransform = row.ExpandArrow, RenderTransformOrigin = new System.Windows.Point(0.5, 0.5) };
                    glyph.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = expand });
                    expand.Content = glyph;
                    expand.Checked += RowExpand_Changed;
                    expand.Unchecked += RowExpand_Changed;
                    row.ExpandButton = expand;
                    DockPanel.SetDock(expand, Dock.Right);

                    row.Slider = new Slider
                    {
                        Minimum = 0,
                        Maximum = 100,
                        Style = (Style)FindResource("RowSliderStyle"),
                        VerticalAlignment = System.Windows.VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0),
                        Tag = row,
                        Foreground = (Brush)FindResource("Theme.Accent")
                    };
                    row.Slider.ApplyTemplate();
                    row.TrackBg = row.Slider.Template.FindName("PART_TrackBg", row.Slider) as Border;
                    row.TrackFill = row.Slider.Template.FindName("PART_TrackFill", row.Slider) as Border;
                    row.PeakVisual = row.Slider.Template.FindName("PART_PeakVisual", row.Slider) as Border;
                    if (row.PeakVisual != null)
                    {
                        // 模板内声明的 Freezable 可能被 WPF 共享并冻结；每行创建独立可写实例。
                        row.PeakTransform = new ScaleTransform(0, 1);
                        row.PeakVisual.RenderTransform = row.PeakTransform;
                    }
                    row.Thumb = row.Slider.Template.FindName("PART_Thumb", row.Slider) as Thumb;
                    // 轨道宽度变化和 ValueChanged 才需要重排，避免每次电平渲染触发所有行的布局计算。
                    if (row.TrackBg != null)
                        row.TrackBg.SizeChanged += (_, _) => UpdateRowSliderLayout(row);
                    if (row.Thumb != null)
                    {
                        row.Thumb.DragDelta += (_, e2) =>
                        {
                            if (row.TrackBg == null || row.TrackBg.ActualWidth <= 0) return;
                            double range = row.Slider.Maximum - row.Slider.Minimum;
                            double left = (row.Thumb?.Margin.Left ?? 0) + e2.HorizontalChange;
                            row.Slider.Value = MathEx.Clamp(row.Slider.Minimum + left / row.TrackBg.ActualWidth * range,
                                row.Slider.Minimum, row.Slider.Maximum);
                        };
                    }
                    if (row.TrackBg != null)
                    {
                        row.TrackBg.MouseLeftButtonDown += (_, e2) =>
                        {
                            if (row.TrackBg.ActualWidth <= 0) return;
                            double range = row.Slider.Maximum - row.Slider.Minimum;
                            double x = e2.GetPosition(row.TrackBg).X;
                            row.Slider.Value = MathEx.Clamp(row.Slider.Minimum + x / row.TrackBg.ActualWidth * range,
                                row.Slider.Minimum, row.Slider.Maximum);
                            e2.Handled = true;
                        };
                    }
                    row.Slider.ValueChanged += RowSlider_ValueChanged;
                    row.Slider.MouseWheel += RowSlider_MouseWheel;

                    dock.Children.Add(iconGrid);
                    dock.Children.Add(expand);
                    dock.Children.Add(row.Slider);
                    header.Child = dock;

                    // 子内容始终按自然高度布局，外壳从顶部裁剪，文字和按钮不被压缩。
                    row.ExpandPanel = new StackPanel
                    {
                        Margin = new Thickness(26, 0, 0, 6),
                        Visibility = Visibility.Collapsed
                    };
                    row.ExpandWrap = new AnimatedExpandHost
                    {
                        Child = row.ExpandPanel
                    };

                    var root = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
                    root.Children.Add(header);
                    root.Children.Add(row.ExpandWrap);
                    AppListPanel.Items.Add(root);

                    row.RootElement = root;
                    row.HeaderBorder = header;
                    _rows[pid] = row;

                    // 初始音量/静音（无会话 -1 → 显示 —）
                    if (vols.TryGetValue(pid, out var v) && v.vol >= 0)
                    {
                        row.Slider.Value = v.vol;
                        ApplyRowMutedVisual(row, v.muted);
                    }
                    else
                    {
                        row.Slider.IsEnabled = false;
                        ApplyRowMutedVisual(row, false);
                    }
                }

                AppItem.LoadIconsAsync(pendingIcons);

                _appRowsSignature = signature;
                UpdateRowHighlights();
            }
            catch (Exception ex)
            {
                ShowOsd(L10n.T("Qp.LoadFail") + " " + ex.Message);
            }
            finally { _updatingRowVolumes = false; }
        }

        /// <summary>更新行滑块进度条/圆点的布局（Value → 填充宽度 + 圆点位置）。</summary>
        private void UpdateRowSliderLayout(AppRow row)
        {
            if (row.TrackBg == null || row.TrackFill == null || row.Thumb == null) return;
            double w = row.TrackBg.ActualWidth;
            if (w <= 0) return;
            double range = row.Slider.Maximum - row.Slider.Minimum;
            double frac = range <= 0 ? 0 : (row.Slider.Value - row.Slider.Minimum) / range;
            double fillWidth = Math.Max(0, w * frac);
            if (row.TrackFill.Width != fillWidth) row.TrackFill.Width = fillWidth;
            double thumbMax = Math.Max(0, w - 14);
            double thumbLeft = MathEx.Clamp(fillWidth - 7, 0, thumbMax);
            if (row.Thumb.Margin.Left != thumbLeft)
                row.Thumb.Margin = new Thickness(thumbLeft, 0, 0, 0);
            UpdatePeakVisual(row);
        }

        private void RowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is not Slider sl || sl.Tag is not AppRow row) return;
            int pct = (int)Math.Round(e.NewValue);
            UpdateRowSliderLayout(row);
            UpdatePeakVisual(row);
            if (_updatingRowVolumes) return; // 初始读回和刷新不写音量，防止逐行触发无用 COM 写入。
            _pendingVolume[(int)row.App.ProcessId] = pct;

            _volDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            if (_volDebounce.Tag == null)
            {
                _volDebounce.Tag = true;
                _volDebounce.Tick += VolDebounce_Tick;
            }
            _volDebounce.Stop();
            _volDebounce.Start();
        }

        /// <summary>行滑块鼠标滚轮：悬停应用音量条时滚轮调节该应用音量 ±4%（写回走 RowSlider_ValueChanged 防抖）。</summary>
        private void RowSlider_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not Slider sl || sl.Tag is not AppRow row) return;
            int step = MathEx.Clamp(SonicRoute.Core.ConfigService.Load().VolumeStep, 1, 20);
            int pct = (int)Math.Round(sl.Value) + (e.Delta > 0 ? step : -step);
            sl.Value = MathEx.Clamp(pct, 0, 100);
            e.Handled = true;
        }
        private async void VolDebounce_Tick(object? sender, EventArgs e)
        {
            _volDebounce!.Stop();
            if (_pendingVolume.Count == 0) return;
            var items = _pendingVolume.ToArray();
            _pendingVolume.Clear();
            await Task.Run(() =>
            {
                foreach (var kv in items) SessionVolumeService.SetVolumePercent(kv.Key, kv.Value);
            });
        }

        /// <summary>行静音视觉：静音后音量条/百分比变强调色 RGB 反色，图标右上显示反色圆点。</summary>
        private void ApplyRowMutedVisual(AppRow row, bool muted)
        {
            var inv = ThemeService.GetInvertedAccentBrush();
            row.Slider.Foreground = muted ? inv : (Brush)FindResource("Theme.Accent");
            row.MuteDot.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
            row.IsMuted = muted;
            if (muted) row.PeakLevel = 0f;
            UpdatePeakVisual(row);
            if (!_updatingRowVolumes) UpdateMeterTargets();
        }

        /// <summary>主题页开关即时生效；关闭后滑块恢复原样且工作线程和 COM 缓存全部释放。</summary>
        public void SetMeterEnabled(bool enabled)
        {
            if (_isClosed) return;
            _meterEnabled = enabled;
            if (!enabled || !IsVisible || _rows.Count == 0)
            {
                StopMeter();
                foreach (var row in _rows.Values) { row.PeakLevel = 0f; UpdatePeakVisual(row); }
                return;
            }

            if (_meter == null)
            {
                _meter = new AudioMeterService();
                _meter.PeaksUpdated += OnPeaksUpdated;
                _meter.Start(_rows.Values.Where(r => r.Slider.IsEnabled && !r.IsMuted).Select(r => (int)r.App.ProcessId));
            }
            else UpdateMeterTargets();
        }

        private void StopMeter()
        {
            var meter = _meter;
            if (meter == null) return;
            _meter = null;
            meter.PeaksUpdated -= OnPeaksUpdated;
            meter.Dispose();
            lock (_peakGate) _latestPeaks = null;
        }

        private void UpdateMeterTargets()
        {
            _meter?.UpdateTargets(_rows.Values.Where(r => r.Slider.IsEnabled && !r.IsMuted)
                .Select(r => (int)r.App.ProcessId));
        }

        private void OnPeaksUpdated(IReadOnlyDictionary<int, float> peaks)
        {
            lock (_peakGate) _latestPeaks = peaks;
            // UI 忙时只保留最新快照，避免 30Hz 回调在 Dispatcher 上积压。
            if (Interlocked.Exchange(ref _peakDispatchPending, 1) != 0) return;
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    IReadOnlyDictionary<int, float>? latest;
                    lock (_peakGate) { latest = _latestPeaks; _latestPeaks = null; }
                    Interlocked.Exchange(ref _peakDispatchPending, 0);
                    if (_isClosed || !_meterEnabled || !IsVisible || latest == null) return;
                    foreach (var pair in _rows)
                    {
                        var row = pair.Value;
                        if (row.IsMuted || !row.Slider.IsEnabled) continue;
                        float next = latest.TryGetValue(pair.Key, out float level) ? level : 0f;
                        if (row.PeakLevel == next) continue;
                        row.PeakLevel = next;
                        UpdatePeakVisual(row);
                    }
                }));
            }
            catch { Interlocked.Exchange(ref _peakDispatchPending, 0); }
        }

        private void UpdatePeakVisual(AppRow row)
        {
            if (row.PeakVisual == null || row.PeakTransform == null) return;
            bool show = _meterEnabled && !row.IsMuted && row.Slider.IsEnabled && row.PeakLevel > 0.001f;
            var visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (row.PeakVisual.Visibility != visibility) row.PeakVisual.Visibility = visibility;
            // 动态帧只改变 ScaleX；Slider.Value 和 Thumb 均不由 Meter 修改。
            double scale = show ? MathEx.Clamp(row.PeakLevel * (float)(row.Slider.Value / 100d), 0f, 1f) : 0d;
            double width = row.TrackBg?.ActualWidth ?? 0;
            if (width > 0)
            {
                double pixels = width * VisualTreeHelper.GetDpi(row.Slider).DpiScaleX;
                scale = Math.Round(scale * pixels) / pixels;
            }
            // 同一物理像素位置不重复更新 Transform；缩放/轨道尺寸改变时重算。
            if (row.PeakTransform.ScaleX != scale) row.PeakTransform.ScaleX = scale;
        }

        /// <summary>点击行图标：静音/取消静音该应用。</summary>
        private async Task RowIconToggleMuteAsync(AppRow row)
        {
            var pid = (int)row.App.ProcessId;
            bool muted = await Task.Run(() => SessionVolumeService.ToggleMute(pid));
            ApplyRowMutedVisual(row, muted);
            ShowOsd(AppDisplayName.Get(row.App), L10n.T(muted ? "Qp.Muted" : "Qp.Unmuted"));
        }

        private async void RowExpand_Changed(object sender, RoutedEventArgs e)
        {
            if (_isClosed || sender is not ToggleButton tb || tb.Tag is not AppRow row) return;
            bool expanded = tb.IsChecked == true;
            if (row.Expanded == expanded) return;
            StopRowScroll();
            row.Expanded = expanded;
            if (!expanded)
            {
                ++row.DeviceBuildVersion;
                UiMotion.RotateArrow(row.ExpandArrow, false, true);
                row.ExpandWrap.SetExpanded(false, true, () =>
                {
                    if (!_isClosed && !row.Expanded) CacheCollapsedRow(row);
                });
                return;
            }

            // 用户指定设计：旧栏瞬间收起，新栏数据就绪后直接显示，不增加应用切换动画。
            bool switching = false;
            foreach (var other in _rows.Values)
            {
                if (other == row || (!other.Expanded && !other.ExpandWrap.IsAnimating)) continue;
                switching = true;
                other.Expanded = false;
                ++other.DeviceBuildVersion;
                other.ExpandWrap.SetExpanded(false, false);
                UiMotion.RotateArrow(other.ExpandArrow, false, false);
                if (other.ExpandButton.IsChecked == true) other.ExpandButton.IsChecked = false;
                CacheCollapsedRow(other);
            }
            if (_recentDeviceRow == row) _recentDeviceRow = null;
            row.ExpandWrap.HoldCurrentState();
            UiMotion.RotateArrow(row.ExpandArrow, true, !switching);
            if (!await BuildRowDevicesAsync(row) || _isClosed || !row.Expanded) return;
            if (switching)
            {
                row.ExpandWrap.SetExpanded(true, false);
                ScrollRowIntoView(row); // 切换仍按原设计直接定位。
                return;
            }

            var scroll = PrepareRowScroll(row);
            row.ExpandWrap.SetExpanded(true, true, () =>
            {
                if (_scrollingRow == row) _scrollingRow = null;
            }, scroll);
        }

        private void StopRowScroll()
        {
            _scrollingRow?.ExpandWrap.StopProgress();
            _scrollingRow = null;
        }

        /// <summary>复用展开高度动画的进度，只在必要时作最小距离滚动，无额外 Timer/Rendering 订阅。</summary>
        private Action<double>? PrepareRowScroll(AppRow row)
        {
            if (row.RootElement == null || AppListScroll.ViewportHeight <= 0) return null;
            double top = 0;
            foreach (var item in AppListPanel.Items)
            {
                if (item == row.RootElement) break;
                if (item is FrameworkElement element) top += element.ActualHeight;
            }
            double startOffset = AppListScroll.VerticalOffset;
            double oldHeight = row.ExpandWrap.ActualHeight;
            double viewport = AppListScroll.ViewportHeight;
            double extent = AppListScroll.ExtentHeight;
            double targetOffset = startOffset;
            bool prepared = false;
            double lastOffset = double.NaN;
            double dpi = VisualTreeHelper.GetDpi(AppListScroll).DpiScaleY;
            _scrollingRow = row;
            return progress =>
            {
                if (_isClosed || !row.Expanded || _scrollingRow != row) return;
                if (!prepared)
                {
                    double rowHeight = RowHeight + row.ExpandWrap.NaturalHeight;
                    if (top < startOffset || rowHeight >= viewport) targetOffset = top;
                    else if (top + rowHeight > startOffset + viewport) targetOffset = top + rowHeight - viewport;
                    double maxOffset = Math.Max(0, extent + row.ExpandWrap.NaturalHeight - oldHeight - viewport);
                    targetOffset = MathEx.Clamp(targetOffset, 0, maxOffset);
                    prepared = true;
                }
                double offset = Math.Round((startOffset + (targetOffset - startOffset) * progress) * dpi) / dpi;
                if (offset == lastOffset) return;
                lastOffset = offset;
                AppListScroll.ScrollToVerticalOffset(offset);
            };
        }

        private void CacheCollapsedRow(AppRow row)
        {
            if (_recentDeviceRow != null && _recentDeviceRow != row && !_recentDeviceRow.Expanded)
                ClearDeviceControls(_recentDeviceRow);
            _recentDeviceRow = row.DeviceButtons.Count > 0 ? row : null;
        }

        private static void ClearDeviceControls(AppRow row)
        {
            ++row.DeviceBuildVersion;
            row.ExpandPanel.Children.Clear();
            row.DeviceButtons.Clear();
            row.DeviceSignature = null;
        }

        private void ClearRowVisuals()
        {
            StopRowScroll();
            _recentDeviceRow = null;
            foreach (var row in _rows.Values)
            {
                row.ExpandWrap.FinishImmediately();
                row.ExpandArrow.BeginAnimation(RotateTransform.AngleProperty, null);
                ClearDeviceControls(row);
            }
        }

        /// <summary>先在后台一次读取输出/输入选择，再原子更新 UI；仅结构变化时重新创建设备按钮。</summary>
        private async Task<bool> BuildRowDevicesAsync(AppRow row)
        {
            int request = ++row.DeviceBuildVersion;
            int pid = (int)row.App.ProcessId;
            var cfg = ConfigService.Load();
            bool micOn = _micUiOn;
            var outputs = _outputDisplay.Where(d => !cfg.HiddenOutputDevices.Contains(d.Id)).ToList();
            var inputs = micOn ? _inputDisplay.Where(d => !cfg.HiddenInputDevices.Contains(d.Id)).ToList() : new List<AudioDeviceInfo>();
            var signature = new StringBuilder();
            void Add(string? value) => signature.Append(value?.Length ?? -1).Append(':').Append(value).Append(';');
            Add(L10n.CurrentLanguage); Add(micOn.ToString());
            foreach (var device in outputs) { Add(device.Id); Add(device.DisplayName); }
            Add(null);
            foreach (var device in inputs) { Add(device.Id); Add(device.DisplayName); }
            string structure = signature.ToString();
            foreach (var cached in row.DeviceButtons) cached.Button.IsEnabled = false;
            try
            {
                var selected = await Task.Run(() =>
                {
                    var output = AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender);
                    var input = micOn ? AudioService.GetPersistedEndpoint(pid, EDataFlow.eCapture) : null;
                    return (Output: output == null ? AudioService.SystemDefaultDeviceId : AudioPolicyConfig.UnpackDeviceId(output),
                        Input: input == null ? AudioService.SystemDefaultInputDeviceId : AudioPolicyConfig.UnpackDeviceId(input));
                });
                if (_isClosed || !row.Expanded || request != row.DeviceBuildVersion
                    || !_rows.TryGetValue(pid, out var current) || current != row) return false;
                if (row.DeviceSignature != structure)
                {
                    row.ExpandPanel.Children.Clear();
                    row.DeviceButtons.Clear();
                    void AddDevices(List<AudioDeviceInfo> devices, EDataFlow flow, string title, Thickness margin)
                    {
                        var label = new TextBlock
                        {
                            Text = L10n.T(title), FontSize = 11, FontWeight = FontWeights.SemiBold,
                            Margin = new Thickness(0, 2, 0, 3)
                        };
                        label.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
                        var wrap = new WrapPanel { Margin = margin };
                        foreach (var device in devices)
                        {
                            var button = NewDevButton(device, false);
                            button.Click += (_, _) => OnRowDeviceClickAsync(row, device, flow);
                            wrap.Children.Add(button);
                            row.DeviceButtons.Add((button, device.Id, flow));
                        }
                        row.ExpandPanel.Children.Add(label);
                        row.ExpandPanel.Children.Add(wrap);
                    }
                    AddDevices(outputs, EDataFlow.eRender, "Qp.Output", new Thickness(0, 0, 0, 8));
                    if (micOn) AddDevices(inputs, EDataFlow.eCapture, "Qp.Input", new Thickness(0, 0, 0, 4));
                    row.DeviceSignature = structure;
                }
                foreach (var item in row.DeviceButtons)
                {
                    string activeId = (item.Flow == EDataFlow.eRender ? selected.Output : selected.Input)
                        ?? (item.Flow == EDataFlow.eRender ? AudioService.SystemDefaultDeviceId : AudioService.SystemDefaultInputDeviceId);
                    string key = item.Id == activeId ? "DevButtonActive" : "DevButton";
                    if (!ReferenceEquals(item.Button.Style, FindResource(key))) item.Button.SetResourceReference(StyleProperty, key);
                    item.Button.IsEnabled = true;
                }
                row.ExpandWrap.InvalidateMeasure();
                return true;
            }
            catch
            {
                if (!_isClosed && request == row.DeviceBuildVersion)
                    foreach (var item in row.DeviceButtons) item.Button.IsEnabled = true;
                return false;
            }
        }

        private Button NewDevButton(AudioDeviceInfo dev, bool active)
        {
            var btn = new Button
            {
                Content = ShortName(dev.DisplayName),
                Tag = dev,
                ToolTip = dev.DisplayName
            };
            btn.SetResourceReference(StyleProperty, active ? "DevButtonActive" : "DevButton");
            return btn;
        }

        private async void OnRowDeviceClickAsync(AppRow row, AudioDeviceInfo dev, EDataFlow flow)
        {
            MarkLastUsed(row.App);
            var pid = (int)row.App.ProcessId;
            var (ok, _, msg) = await Task.Run(() => AudioService.ApplyEndpoint(pid, flow, dev.Id));
            if (ok)
                ShowOsd(AppDisplayName.Get(row.App), (flow == EDataFlow.eRender ? "🔊 " : "🎤 ") + dev.DisplayName);
            else
                ShowOsd(AppDisplayName.Get(row.App), $"✗ {msg}");

            // 仍展开时重建，刷新高亮
            if (row.Expanded) await BuildRowDevicesAsync(row);
        }

        private void MarkLastUsed(AudioAppInfo app)
        {
            var name = app.ProcessName;
            if (string.IsNullOrWhiteSpace(name)) return;
            var cfg = ConfigService.Load();
            // 与上次记录相同则跳过写盘（减少无谓配置全量保存）
            if (string.Equals(cfg.LastUsedAppName, name, StringComparison.OrdinalIgnoreCase)) return;
            cfg.LastUsedAppName = name;
            ConfigService.Save(cfg);
        }

        /// <summary>高亮当前应用行（前台自动跟随/概览切换的操作目标）。</summary>
        private void UpdateRowHighlights()
        {
            var cur = CurrentAppService.Current;
            // net48 无 KeyValuePair<K,V>.Deconstruct（.NET Core 2.0+）：显式取 Key/Value
            foreach (var kv in _rows)
            {
                int pid = kv.Key;
                var row = kv.Value;
                bool isCur = cur != null && pid == (int)cur.ProcessId;
                if (isCur) row.HeaderBorder.SetResourceReference(BackgroundProperty, "Theme.Overlay");
                else row.HeaderBorder.ClearValue(BackgroundProperty);
            }
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
        // 底部：全局输出静音 / 全局麦克风静音 / 完整界面
        // ------------------------------------------------------------------

        private async void GlobalMuteButton_Click(object sender, RoutedEventArgs e)
        {
            await ToggleGlobalOutputMuteAsync();
        }

        /// <summary>全局输出静音：静音/取消静音所有有输出会话的应用。面板按钮专用。</summary>
        public async Task<bool> ToggleGlobalOutputMuteAsync()
        {
            int version = ++_globalMuteVersion;
            bool muted = await Task.Run(() => SessionVolumeService.ToggleAllMute());
            if (!_isClosed && version == _globalMuteVersion)
            {
                ++_globalMuteVersion; // 丢弃操作进行期间读到、却延迟返回的初始化状态。
                ApplyGlobalMuteVisual(muted);
                GlobalMuteButton.IsEnabled = true;
                // net48 无 KeyValuePair<K,V>.Deconstruct：显式取 Value
                foreach (var kv in _rows) ApplyRowMutedVisual(kv.Value, muted);
            }
            // 全局输出静音 OSD 主标题显示「全部应用」（静音的是所有应用，不是音跃本身）
            ((App)Application.Current).ShowOsd(L10n.T("Qp.AllApps"), L10n.T(muted ? "Qp.GlobalMuted" : "Qp.GlobalUnmuted"));
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
            int version = ++_micMuteVersion;
            bool muted = await Task.Run(() => GlobalMicMuteService.Toggle());
            if (!_isClosed && version == _micMuteVersion)
            {
                ++_micMuteVersion;
                ApplyMicMuteVisual(muted);
                MicMuteButton.IsEnabled = true;
            }
            ((App)Application.Current).ShowMicMuteOsd(L10n.T("Ov.MuteMic"), muted); // 统一入口：标题固定「麦克风静音」，静音且常驻开关开 → 常驻
            return muted; // 返回真实静音状态（快捷键共用：切换后立即更新 OSD）
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            ((App)Application.Current).ShowMainWindow();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
            base.OnKeyDown(e);
        }
    }
}

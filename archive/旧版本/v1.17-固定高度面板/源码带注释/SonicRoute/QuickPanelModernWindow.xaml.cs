using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Image = System.Windows.Controls.Image;
using Brush = System.Windows.Media.Brush;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using SonicRoute.Core;
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
            public Thumb? Thumb;
            public ToggleButton ExpandButton = null!;
            public StackPanel ExpandPanel = null!;
            public Border ExpandWrap = null!;
            public FrameworkElement? RootElement; // 行根容器（AppListPanel 的 item）：展开后滚动定位到该行
            public bool Expanded;
        }

        private readonly Dictionary<int, AppRow> _rows = new();
        private readonly Dictionary<int, int> _pendingVolume = new();
        private DispatcherTimer? _volDebounce;

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
                double total = Math.Clamp(cfg.QuickPanelHeight, 350, 800);
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
        private bool _entranceAnimating;

        /// <summary>入场动画：仅内部 RootBorder 轻微上移 + 淡入，Window 位置/高度全程不变（140ms，QuadraticEase EaseOut）。
        /// 动画进行中不重复启动，避免快速连续呼出时叠加/闪烁。</summary>
        private void PlayEntranceAnimation()
        {
            if (PanelSlide == null || RootBorder == null) return;
            _entranceAnimating = true;
            var ease = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(140);

            PanelSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            PanelSlide.Y = 6;
            var yAnim = new System.Windows.Media.Animation.DoubleAnimation(0, dur) { EasingFunction = ease };
            yAnim.Completed += (_, _) => _entranceAnimating = false;
            PanelSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, yAnim);

            RootBorder.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
            RootBorder.Opacity = 0;
            var oAnim = new System.Windows.Media.Animation.DoubleAnimation(1, dur) { EasingFunction = ease };
            RootBorder.BeginAnimation(System.Windows.UIElement.OpacityProperty, oAnim);
        }

        public void ShowQuickPanel()
        {
            _everFocused = false;
            if (IsVisible && _entranceAnimating)
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
            }
            else
            {
                // 已可见但动画已完成：内容刷新，不重播入场动画，保持原位
                _entrancePlayed = true;
            }
            PanelSlide?.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            if (PanelSlide != null) PanelSlide.Y = 0;
            if (RootBorder != null) RootBorder.Opacity = 1;
            Show();
            Activate();
            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            try
            {
                var cfg = ConfigService.Load();
                _micUiOn = cfg.ExperimentalMic && cfg.MicInPanel;

                var outputs = await Task.Run(() => AudioService.GetDevices(EDataFlow.eRender));
                _outputDisplay = PanelDevices.WithSystemDefault(DisplayDevices(outputs), EDataFlow.eRender, cfg);

                if (cfg.ExperimentalMic)
                {
                    var inputs = await Task.Run(() => AudioService.GetDevices(EDataFlow.eCapture));
                    _inputDisplay = PanelDevices.WithSystemDefault(DisplayDevices(inputs), EDataFlow.eCapture, cfg);
                }

                // 顶部系统音频设备
                await LoadSystemDevicesAsync();

                var apps = await Task.Run(() => AudioService.GetApps());
                BuildAppRows(apps);
                ApplyFixedPanelHeight(cfg);

                // 底部按钮初始文案（全局输出静音 / 全局麦克风静音）
                bool allMuted = await Task.Run(() => SessionVolumeService.AllMuted());
                ApplyGlobalMuteVisual(allMuted);
                bool micMuted = await Task.Run(() => GlobalMicMuteService.IsMuted());
                ApplyMicMuteVisual(micMuted);

                // 内容已就绪，重新定位到任务栏右下角（避免溢出屏幕）
                RequestPosition();
                if (!_entrancePlayed) { _entrancePlayed = true; PlayEntranceAnimation(); }
            }
            catch (Exception)
            {
                ShowOsd(L10n.T("Qp.Panel"), L10n.T("Qp.LoadFail"));
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

        // ------------------------------------------------------------------
        // 顶部：系统音频设备（选择控制对象 + 系统设备音量/静音；不修改系统默认设备）
        // ------------------------------------------------------------------

        private async Task LoadSystemDevicesAsync()
        {
            var devs = await Task.Run(() => AudioService.GetDevices(EDataFlow.eRender));
            var defaultId = await Task.Run(() => AudioService.GetDefaultDeviceId(EDataFlow.eRender));
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
            bool ok = await Task.Run(() => SystemVolumeService.SetVolumePercent(_systemDeviceId, pct));
            int actual = await Task.Run(() => SystemVolumeService.GetVolumePercent(_systemDeviceId));
            if (actual >= 0)
            {
                VolumeSlider.Value = actual;
                VolumePercentText.Text = $"{actual}%";
            }
            ShowDeviceOsd(ok && actual >= 0
                ? string.Format(L10n.T("Qp.VolOk"), actual)
                : L10n.T("Qp.VolFail"));
        }

        private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_systemReady) return;
            int step = Math.Clamp(ConfigService.Load().VolumeStep, 1, 20);
            int pct = (int)Math.Round(VolumeSlider.Value) + (e.Delta > 0 ? step : -step);
            VolumeSlider.Value = Math.Clamp(pct, 0, 100);
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
            int cur = await Task.Run(() => SystemVolumeService.GetVolumePercent(_systemDeviceId));
            if (cur < 0) return -1;
            int next = Math.Clamp(cur + delta, 0, 100);
            bool ok = await Task.Run(() => SystemVolumeService.SetVolumePercent(_systemDeviceId, next));
            int actual = await Task.Run(() => SystemVolumeService.GetVolumePercent(_systemDeviceId));
            if (actual >= 0)
            {
                VolumeSlider.Value = actual;
                VolumePercentText.Text = $"{actual}%";
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

        private async void BuildAppRows(List<AudioAppInfo> apps)
        {
            try
            {
                // 过滤：完整界面「应用」里关闭"在快速面板显示"的应用不列出
                var cfg = ConfigService.Load();
                var hiddenPanel = cfg.HiddenPanelApps;
                apps = apps.Where(a => !string.IsNullOrWhiteSpace(a.ProcessName)
                    && !hiddenPanel.Any(h => string.Equals(h, a.ProcessName, StringComparison.OrdinalIgnoreCase))).ToList();
                AppListPanel.Items.Clear();
                _rows.Clear();

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
                        Content = "▾",
                        Width = 32,
                        Height = 16,
                        MinWidth = 32,
                        MinHeight = 16,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center,
                        Style = (Style)FindResource("RowExpandButton"),
                        Tag = row,
                        ToolTip = L10n.T("Qp.ExpandDevices")
                    };
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
                    row.Thumb = row.Slider.Template.FindName("PART_Thumb", row.Slider) as Thumb;
                    row.Slider.LayoutUpdated += (_, _) => UpdateRowSliderLayout(row);
                    if (row.Thumb != null)
                    {
                        row.Thumb.DragDelta += (_, e2) =>
                        {
                            if (row.TrackBg == null || row.TrackBg.ActualWidth <= 0) return;
                            double range = row.Slider.Maximum - row.Slider.Minimum;
                            double left = (row.Thumb?.Margin.Left ?? 0) + e2.HorizontalChange;
                            row.Slider.Value = Math.Clamp(row.Slider.Minimum + left / row.TrackBg.ActualWidth * range,
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
                            row.Slider.Value = Math.Clamp(row.Slider.Minimum + x / row.TrackBg.ActualWidth * range,
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

                    // 内容面板：贴底对齐（收起动画裁剪顶部，实现从上面向下收），由外层裁剪容器控制显隐/高度
                    row.ExpandPanel = new StackPanel
                    {
                        Margin = new Thickness(26, 0, 0, 6),
                        VerticalAlignment = System.Windows.VerticalAlignment.Bottom
                    };
                    row.ExpandWrap = new Border
                    {
                        ClipToBounds = true,
                        Visibility = Visibility.Collapsed,
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

                UpdateRowHighlights();
            }
            catch (Exception ex)
            {
                ShowOsd(L10n.T("Qp.LoadFail") + " " + ex.Message);
            }
        }

        /// <summary>更新行滑块进度条/圆点的布局（Value → 填充宽度 + 圆点位置）。</summary>
        private void UpdateRowSliderLayout(AppRow row)
        {
            if (row.TrackBg == null || row.TrackFill == null || row.Thumb == null) return;
            double w = row.TrackBg.ActualWidth;
            if (w <= 0) return;
            double range = row.Slider.Maximum - row.Slider.Minimum;
            double frac = range <= 0 ? 0 : (row.Slider.Value - row.Slider.Minimum) / range;
            row.TrackFill.Width = Math.Max(0, w * frac);
            double thumbMax = Math.Max(0, w - 14);
            row.Thumb.Margin = new Thickness(Math.Clamp(row.TrackFill.Width - 7, 0, thumbMax), 0, 0, 0);
        }

        private void RowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is not Slider sl || sl.Tag is not AppRow row) return;
            int pct = (int)Math.Round(e.NewValue);
            UpdateRowSliderLayout(row);
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
            int step = Math.Clamp(SonicRoute.Core.ConfigService.Load().VolumeStep, 1, 20);
            int pct = (int)Math.Round(sl.Value) + (e.Delta > 0 ? step : -step);
            sl.Value = Math.Clamp(pct, 0, 100);
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
            if (sender is not ToggleButton tb || tb.Tag is not AppRow row) return;
            bool expanded = tb.IsChecked == true;
            tb.Content = expanded ? "▴" : "▾";
            if (row.Expanded == expanded) return; // 重入保护：切换展开时显式收起旧行后，IsChecked=false 触发的 Unchecked 不再重复执行收起（重复 BeginAnimation 覆盖旧动画会中断）
            row.Expanded = expanded;
            if (expanded)
            {
                // 展开新行时收起其他已展开的行（同一时间只保留一个折叠展开）
                // 切换场景（有其他行正在展开）：旧行瞬间收起 + 新行直接显示（同帧完成、均不播动画）
                bool switching = false;
                foreach (var kv in _rows)
                {
                    var r = kv.Value;
                    if (r == row || !r.Expanded) continue;
                    switching = true;
                    r.Expanded = false;
                    // 瞬间收起：直接隐藏并清空（窗口高度恒定，无任何高度来回变化）
                    r.ExpandWrap.BeginAnimation(FrameworkElement.HeightProperty, null);
                    r.ExpandWrap.Height = double.NaN;
                    r.ExpandWrap.BeginAnimation(UIElement.OpacityProperty, null);
                    r.ExpandWrap.Opacity = 1;
                    r.ExpandWrap.RenderTransform = null;
                    r.ExpandWrap.Visibility = Visibility.Collapsed;
                    r.ExpandPanel.Children.Clear();
                    if (r.ExpandButton != null) r.ExpandButton.IsChecked = false;
                }
                // 展开：ExpandWrap 高度 0→内容高，在固定高度的列表区内平滑生长（窗口高度恒定不变），配合淡入
                row.ExpandWrap.BeginAnimation(FrameworkElement.HeightProperty, null);
                row.ExpandWrap.Height = 0;
                row.ExpandWrap.Visibility = Visibility.Visible;
                row.ExpandWrap.Opacity = 0;
                await BuildRowDevicesAsync(row);
                if (!row.Expanded) return; // 构建期间被收起：收起分支已接管
                double targetH;
                try
                {
                    double probeW = Math.Max(0, row.HeaderBorder.ActualWidth - 26);
                    row.ExpandPanel.Measure(new System.Windows.Size(probeW, double.PositiveInfinity));
                    targetH = Math.Max(1, row.ExpandPanel.DesiredSize.Height + 6);
                }
                catch
                {
                    row.ExpandWrap.BeginAnimation(FrameworkElement.HeightProperty, null);
                    row.ExpandWrap.Height = double.NaN;
                    row.ExpandWrap.Visibility = Visibility.Collapsed;
                    return;
                }
                if (switching)
                {
                    // 切换展开：新行直接显示（不播展开动画），设备区一步到位，随后滚动到该行
                    row.ExpandWrap.BeginAnimation(FrameworkElement.HeightProperty, null);
                    row.ExpandWrap.Height = targetH;
                    row.ExpandWrap.BeginAnimation(UIElement.OpacityProperty, null);
                    row.ExpandWrap.Opacity = 1;
                    ScrollRowIntoView(row);
                    return;
                }
                var hAnim = new System.Windows.Media.Animation.DoubleAnimation(0, targetH, new Duration(TimeSpan.FromMilliseconds(130)))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                };
                hAnim.Completed += (_, _) =>
                {
                    row.ExpandWrap.BeginAnimation(FrameworkElement.HeightProperty, null);
                    row.ExpandWrap.Height = double.NaN; // 回 Auto
                    ScrollRowIntoView(row); // 列表区高度固定：展开后滚动到该行，确保设备区可见
                };
                row.ExpandWrap.BeginAnimation(FrameworkElement.HeightProperty, hAnim);
                row.ExpandWrap.BeginAnimation(UIElement.OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(130)))
                    {
                        EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                        {
                            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                        }
                    });
            }
            else
            {
                // 收起动画：内容保留到动画结束后再清空，避免瞬间消失
                AnimateRowPanelCollapse(row, () => row.ExpandPanel.Children.Clear());
            }
            // 窗口高度恒定：展开/收起不改变窗口尺寸，无需重定位（SizeChanged 仅在应用固定高度时触发）
        }

        /// <summary>应用行设备区收起动画：淡出 + 轻微下滑 + 高度收拢（列表区内收拢，窗口高度恒定），动画结束后再隐藏（并执行回调）。</summary>
        private void AnimateRowPanelCollapse(AppRow row, Action? afterHide = null)
        {
            var panel = row.ExpandWrap;
            if (panel == null) { afterHide?.Invoke(); return; }
            if (panel is not FrameworkElement fe || fe.ActualHeight <= 0)
            {
                panel.Visibility = Visibility.Collapsed;
                afterHide?.Invoke();
                return;
            }
            double startH = fe.ActualHeight;
            fe.Height = startH;
            var t = new TranslateTransform(0, 0);
            panel.RenderTransform = t;
            panel.BeginAnimation(UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(100)))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
                    }
                });
            t.BeginAnimation(TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(-8, new Duration(TimeSpan.FromMilliseconds(100)))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
                    }
                });
            // 高度从当前值收拢到 0，下方应用行随布局平滑上移（列表区内，窗口高度恒定）
            var hAnim = new System.Windows.Media.Animation.DoubleAnimation(startH, 0, new Duration(TimeSpan.FromMilliseconds(120)))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
                }
            };
            hAnim.Completed += (s, e2) =>
            {
                fe.BeginAnimation(FrameworkElement.HeightProperty, null);
                fe.Height = double.NaN; // 恢复 Auto
                panel.BeginAnimation(UIElement.OpacityProperty, null);
                panel.RenderTransform = null;
                panel.Opacity = 1;
                panel.Visibility = Visibility.Collapsed;
                afterHide?.Invoke();
            };
            fe.BeginAnimation(FrameworkElement.HeightProperty, hAnim);
        }

        /// <summary>为某应用行构建输出/输入设备按钮（保留设备筛选 + 自定义名 + 当前设备高亮）。</summary>
        private async Task BuildRowDevicesAsync(AppRow row)
        {
            row.ExpandPanel.Children.Clear();
            try
            {
                var pid = (int)row.App.ProcessId;
                var cfg = ConfigService.Load();

                var outIdRaw = await Task.Run(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender));
                var outId = outIdRaw == null ? null : AudioPolicyConfig.UnpackDeviceId(outIdRaw);

                var outTitle = new TextBlock
                {
                    Text = L10n.T("Qp.Output"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("Theme.TextSecondary"),
                    Margin = new Thickness(0, 2, 0, 3)
                };
                row.ExpandPanel.Children.Add(outTitle);
                var outWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
                foreach (var dev in _outputDisplay)
                {
                    if (cfg.HiddenOutputDevices.Contains(dev.Id)) continue;
                    var btn = NewDevButton(dev, dev.Id == (outId ?? AudioService.SystemDefaultDeviceId));
                    btn.Click += (_, _) => OnRowDeviceClickAsync(row, dev, EDataFlow.eRender);
                    outWrap.Children.Add(btn);
                }
                row.ExpandPanel.Children.Add(outWrap);

                if (_micUiOn)
                {
                    var inIdRaw = await Task.Run(() => AudioService.GetPersistedEndpoint(pid, EDataFlow.eCapture));
                    var inId = inIdRaw == null ? null : AudioPolicyConfig.UnpackDeviceId(inIdRaw);

                    var inTitle = new TextBlock
                    {
                        Text = L10n.T("Qp.Input"),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)FindResource("Theme.TextSecondary"),
                        Margin = new Thickness(0, 2, 0, 3)
                    };
                    row.ExpandPanel.Children.Add(inTitle);
                    var inWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                    foreach (var dev in _inputDisplay)
                    {
                        if (cfg.HiddenInputDevices.Contains(dev.Id)) continue;
                        var btn = NewDevButton(dev, dev.Id == (inId ?? AudioService.SystemDefaultInputDeviceId));
                        btn.Click += (_, _) => OnRowDeviceClickAsync(row, dev, EDataFlow.eCapture);
                        inWrap.Children.Add(btn);
                    }
                    row.ExpandPanel.Children.Add(inWrap);
                }
            }
            catch { } // 设备枚举/解包异常不冒泡：展开动画照常执行，计数不泄漏
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
            foreach (var (pid, row) in _rows)
            {
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
            bool muted = await Task.Run(() => SessionVolumeService.ToggleAllMute());
            ApplyGlobalMuteVisual(muted);
            foreach (var (_, row) in _rows) ApplyRowMutedVisual(row, muted);
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
            bool muted = await Task.Run(() => GlobalMicMuteService.Toggle());
            ApplyMicMuteVisual(muted);
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
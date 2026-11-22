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
    /// 快速面板：顶部控制所选系统输出设备的音量和静音，可选同步更改系统默认设备。
    /// 应用行提供图标静音、名称、音量条和设备下拉；播放设备名按主题开关加载。
    /// 保留失焦/Esc 关闭、设备筛选、自定义名称和现有动画；顶部音量调节仅就地反馈。
    /// </summary>
    public partial class QuickPanelModernWindow : Window
    {
        private sealed class AppRow : IDeferredPanelRow
        {
            public QuickPanelModernWindow Owner = null!;
            public bool Virtualized;
            public int Volume = -1;
            public CancellationTokenSource? VisualLifetime;
            public object ListItem => Virtualized ? this : RootElement!;
            FrameworkElement IDeferredPanelRow.Realize() => Owner.CreateAppRowVisual(this);
            void IDeferredPanelRow.ReleaseVisual() => Owner.ReleaseAppRowVisual(this);
            public AudioAppInfo App = null!;
            public AppItem IconItem = null!;
            public Border HeaderBorder = null!;
            public Slider Slider = null!;
            public Border MuteDot = null!;
            public Border? TrackBg;
            public Border? TrackFill;
            public Border? PeakVisual;
            public ScaleTransform? PeakTransform;
            public float PeakLevel;
            public bool IsMuted;
            public bool MuteBusy;
            public bool MuteKnown;
            public Button? IconButton;
            public ToggleButton ExpandButton = null!;
            public TextBlock? Name;
            public string? NamePrimary, NameSuffix;
            public bool LegacyLayout;
            public double CollapsedHeight => LegacyLayout ? 38 : 54;
            public bool ShowOutputName;
            public string? OutputId;
            public string? PlaybackAnchor;
            public Task PlaybackChange = Task.CompletedTask;
            public HashSet<string> ConfirmedPlayback = new(StringComparer.OrdinalIgnoreCase);
            public PlaybackIntent? PendingPlayback;
            public int PlaybackReadVersion, ConfirmedPlaybackRead;
            public Task InputChange = Task.CompletedTask;
            public string? ConfirmedInput;
            public PendingInputIntent? PendingInput;
            public int InputReadVersion, ConfirmedInputRead;
            public StackPanel ExpandPanel = null!;
            public AnimatedExpandHost ExpandWrap = null!;
            public RotateTransform ExpandArrow = new();
            public readonly List<(Button Button, string Id, EDataFlow Flow)> DeviceButtons = new();
            public string? DeviceSignature;
            public int DeviceBuildVersion;
            public FrameworkElement? RootElement; // 行根容器（AppListPanel 的 item）：展开后滚动定位到该行
            public bool Expanded;
        }

        private readonly ScrollViewer AppListScroll;
        private readonly Dictionary<int, AppRow> _rows = new();
        private Task? _loadTask;
        private readonly PanelEntranceMotion _entranceMotion;
        private bool _contentReady;
        private int _loadVersion;
        private int _globalMuteVersion;
        private (int Load, int OutputVersion, bool Muted, bool EligibleNativeState)? _globalMuteLoadState;
        private Func<IEnumerable<int>, (Dictionary<int, (int Volume, bool Muted, bool MuteKnown)> Apps, bool? AllMuted)> _readPanelState = SessionVolumeService.ReadPanelStateChecked;
        private Func<int, bool, (string Output, string Input)> _readRowEndpoints = static (pid, mic) =>
        {
            var output = ApplicationAudio.GetPersistedEndpoint(pid, EDataFlow.eRender);
            var input = mic ? ApplicationAudio.GetPersistedEndpoint(pid, EDataFlow.eCapture) : null;
            return (output == null ? AudioService.SystemDefaultDeviceId : AudioPolicyConfig.UnpackDeviceId(output),
                input == null ? AudioService.SystemDefaultInputDeviceId : AudioPolicyConfig.UnpackDeviceId(input));
        };
        private int _micMuteVersion;
        private int _systemVolumeRequest;
        private AppRow? _recentDeviceRow;
        private AppRow? _scrollingRow;
        private string? _appRowsSignature;
        private string? _appRowsLayoutKey;
        private bool _rowIconsQueued;
        private bool _updatingRowVolumes;
        private CoalescedVolumeWriter? _appVolumeWriter;
        private DispatcherTimer? _volDebounce;
        private AudioMeterService? _meter;
        private Action<IReadOnlyDictionary<int, float>>? _meterListener;
        private readonly List<AppRow> _visibleMeterRows = new();
        private IReadOnlyDictionary<int, float>? _displayPeaks;
        private bool _meterViewportQueued;
        private bool _meterEnabled;
        private bool _isClosed;
        private readonly object _peakGate = new();
        private IReadOnlyDictionary<int, float>? _latestPeaks;
        private int _peakDispatchPending;

        private List<AudioDeviceInfo> _outputDisplay = new();
        private List<AudioDeviceInfo> _inputDisplay = new();
        private string? _systemDeviceId;
        private bool _systemReady;
        private bool _suppressDevCombo;
        private bool _everFocused;
        private bool _micUiOn;
        private bool _adjustMode;          // 主题页「调整快速面板位置」：可拖拽，松手保存
        private bool _adjustDragging;
        private System.Windows.Point _adjustDragStart;
        private bool _positionPending;     // 合并定位请求：同帧多次触发（SizeChanged/列表刷新/展开收起）只定位一次

        public QuickPanelModernWindow()
        {
            App.EnsureWindowResources();
            InitializeComponent();
            AppListPanel.ApplyTemplate();
            AppListScroll = (ScrollViewer)AppListPanel.Template.FindName("AppListScroll", AppListPanel);
            RegisterName("AppListScroll", AppListScroll);
            AppListPanel.ItemTemplateSelector = new DeferredPanelRowTemplateSelector((DataTemplate)AppListPanel.FindResource("DeferredAppRowTemplate"));
            VolumePresets.Attach(VolumeSlider);
            VolumeSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SystemVolumeDragCompleted), true);
            InitializeOutputVolumes();
            InitializeAndroidPanel();
            _entranceMotion = new PanelEntranceMotion(PanelSurface, PanelSlide);
            AppListScroll.PreviewMouseWheel += (_, _) => StopRowScroll();
            AppListScroll.PreviewMouseDown += (_, _) => StopRowScroll();
            AppListScroll.PreviewKeyDown += (_, _) => StopRowScroll();
            AppListScroll.ScrollChanged += (_, _) => { QueueMeterViewport(); QueueRowIconLoads(); };
            VersionText.Text = App.DisplayVersion;
            // 只有面板真正获得过焦点（托盘点击等正常交互）才在失焦时关闭；
            // 启动/脚本等未获焦场景下保持打开，避免一闪而过
            Activated += (_, _) => _everFocused = true;
            Deactivated += (_, _) =>
            {
                if (AppIconDragController.IsDragging || ((App)Application.Current).KeepQuickPanelForAndroid) return;
                if (OutputVolumesPopup.IsOpen || ((App)Application.Current).IsPhoneAudioVisible) CloseAfterFocusLeaves();
                else if (IsVisible && _everFocused && !_adjustMode) Close();
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
                QueueMeterViewport();
                if (!IsVisible || _adjustMode) return;
                RequestPosition();
                if (OutputVolumesPopup.IsOpen) PositionOutputPopup();
            };
            // 工作区变化（分辨率/任务栏位置/DPI 缩放）时重定位默认位置
            SystemParameters.StaticPropertyChanged += OnSystemParamChanged;
            // 共享"当前应用"变化（前台自动跟随切换）时同步面板高亮
            CurrentAppService.CurrentChanged += OnSharedCurrentChanged;
#if !SONICROUTE_PHONE
            ApplicationAudio.Changed += OnLocalPlaybackChanged;
#endif
            Closed += (_, _) => CleanupClosedWindow();
        }

        /// <summary>工作区变化（分辨率/任务栏位置/DPI 缩放）时重定位默认位置；自定义位置保持用户设定。</summary>
        private void OnSystemParamChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SystemParameters.WorkArea)) return;
            if (IsVisible && !_adjustMode) RequestPosition();
            if (OutputVolumesPopup.IsOpen) InvalidateOutputPopupScreen();
        }

        /// <summary>合并定位请求：同帧多次触发只定位一次，避免窗口反复移动/跳动。</summary>
        private void RequestPosition()
        {
            if (!_contentReady || _isClosed) return;
            if (_positionPending) return;
            _positionPending = true;
            _uiLifetime.Post(Dispatcher, () =>
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

        /// <summary>应用固定面板高度：窗口高度 = 用户设置值（QuickPanelHeight，350–850，默认 450，clamp 工作区）。
        /// 面板高度自此恒定——展开/收起设备区只在固定高度的应用区域内变化，窗口高度永不变；
        /// 底部操作栏固定贴底，内容超出在应用区域内滚动。</summary>
        private void ApplyFixedPanelHeight(SonicRoute.Core.AppConfig cfg)
        {
            try
            {
                // 列表复用原 Grid 的 * 行分配剩余空间；不临时压高度或强制同步布局。
                double total = MathEx.Clamp(cfg.QuickPanelHeight, 350, 850);
                var wa = SystemParameters.WorkArea;
                double maxTotal = Math.Max(240, wa.Height - 16); // 不超屏幕限度
                if (total > maxTotal) total = maxTotal;
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
            _uiLifetime.Post(Dispatcher, () =>
            {
                try
                {
                    if (_isClosed || !row.Expanded) return;
                    AppListPanel.ScrollIntoView(row.ListItem);
                    row.RootElement?.BringIntoView();
                }
                catch { }
            });
        }

        /// <summary>进入/退出位置调整模式（主题页调用）。</summary>
        internal void SetAdjustMode(bool on)
        {
            _adjustMode = on;
            OutputVolumesToggle.IsEnabled = !on;
            if (on) StopOutputVolumes();
            if (on) ((App)Application.Current).ShowOsd(L10n.T("Exp.PanelPosDragTitle"), L10n.T("Exp.PanelPosHint"));
        }

        /// <summary>一键还原默认位置（任务栏右下角），已打开则立即重定位。</summary>
        internal void ResetPosition()
        {
            if (_adjustMode) SetAdjustMode(false);
            if (IsVisible) QuickPanelPosition.Apply(this, ConfigService.Load());
        }

        /// <summary>拖动松手：把当前窗口位置写入配置（Custom 模式），保存并通知主题页。</summary>
        private void SaveAdjustPosition()
        {
            try
            {
                QuickPanelPosition.Save(this, ConfigService.Load());
                SetAdjustMode(false);
                ((App)Application.Current).ShowOsd("📍", L10n.T("Exp.PanelPosSaved"));
                ((App)Application.Current).NotifyQuickPanelAdjustFinished();
                OpenOutputVolumes();
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
                // 已可见（含动画进行中）只激活和刷新，不位移或重播入场动画。
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
            if (_isClosed) return;
            try
            {
                var cfg = ConfigService.Load();
                int loadVersion = ++_loadVersion;
                _micUiOn = cfg.ExperimentalMic && cfg.MicInPanel;
                GlobalMuteButton.IsEnabled = false;
                MicMuteButton.IsEnabled = false;
                // 两项独立发现共用原服务/缓存；一起等待以观察失败和关窗取消。
                var deviceRead = _uiLifetime.ReadAsync(cfg.ExperimentalMic, static includeInputs =>
                {
                    var outputs = AudioService.GetDevices(EDataFlow.eRender);
                    var inputs = includeInputs ? AudioService.GetDevices(EDataFlow.eCapture) : new List<AudioDeviceInfo>();
                    return (Outputs: outputs, Inputs: inputs, DefaultId: AudioService.GetDefaultDeviceId(EDataFlow.eRender));
                });
                var appRead = _uiLifetime.ReadAsync(AudioService.GetAppsAsync());
                await Task.WhenAll(deviceRead, appRead);
                if (_isClosed) return;
                var devices = deviceRead.Result;
                var outputs = DisplayDevices(devices.Outputs, cfg);
                _outputDisplay = PanelDevices.WithSystemDefault(DisplayDevices(ApplicationAudio.MergeExternalOutputs(devices.Outputs), cfg), EDataFlow.eRender, cfg);
                _inputDisplay = cfg.ExperimentalMic
                    ? PanelDevices.WithSystemDefault(DisplayDevices(devices.Inputs, cfg), EDataFlow.eCapture, cfg)
                    : new List<AudioDeviceInfo>();

                // 顶部系统音频设备
                SetSystemDevices(outputs, devices.DefaultId);
                // 顶部音量和应用行互不依赖，初次展示仍等待两者都准备好。
                await Task.WhenAll(RefreshSystemVolumeAsync(), BuildAppRowsAsync(appRead.Result, cfg));
                if (_isClosed) return;
                SetMeterEnabled(cfg.ShowAppPeakMeter);
                ApplyFixedPanelHeight(cfg);
                // 先让 WPF 完成布局；最终定位与动画在同一个回调中执行，避免动画在屏幕外提前运行。
                await _uiLifetime.Post(Dispatcher, () =>
                {
                    if (_isClosed || !IsVisible || loadVersion != _loadVersion) return;
                    _contentReady = true;
                    QuickPanelPosition.Apply(this, cfg);
                    if (!_entrancePlayed) { _entrancePlayed = true; PlayEntranceAnimation(); }
                    OpenOutputVolumes();
#if SONICROUTE_PHONE
                    ((App)Application.Current).ShowPhoneAudioWindow(this);
#endif
                }).Task;
                if (_isClosed) return;
                await RefreshGlobalMuteStateAsync(loadVersion);
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Quick panel load: {0}", error);
                if (!_isClosed) ShowOsd(L10n.T("Qp.Panel"), L10n.T("Qp.LoadFail"));
            }

        }

        private async Task RefreshGlobalMuteStateAsync(int loadVersion)
        {
            if (_isClosed) return;
            int outputVersion = _globalMuteVersion, micVersion = _micMuteVersion;
            try
            {
                var cached = _globalMuteLoadState;
                bool? output = cached.HasValue && cached.Value.Load == loadVersion && cached.Value.OutputVersion == outputVersion
                    ? cached.Value.Muted : null;
                bool eligible = output.HasValue && cached!.Value.EligibleNativeState;
                var state = await _uiLifetime.ReadAsync((Output: output, Eligible: eligible), static saved =>
                    (Output: ApplicationAudio.HasExternal
                        ? saved.Eligible ? ApplicationAudio.CombineExternalMute(saved.Output!.Value) : ApplicationAudio.AllMuted()
                        : saved.Output ?? SessionVolumeService.AllMuted(), Mic: GlobalMicMuteService.IsMuted()));
                if (_isClosed || loadVersion != _loadVersion) return;
                if (outputVersion == _globalMuteVersion) { ApplyGlobalMuteVisual(state.Output); GlobalMuteButton.IsEnabled = true; }
                if (micVersion == _micMuteVersion) { ApplyMicMuteVisual(state.Mic); MicMuteButton.IsEnabled = true; }
            }
            catch { /* 底部状态读取失败不打断已准备好的面板，下次刷新重试。 */ }
        }

        private static List<AudioDeviceInfo> DisplayDevices(IEnumerable<AudioDeviceInfo> devs, AppConfig cfg) =>
            devs.Select(device => PanelDevices.WithDeviceName(device, cfg)).ToList();

        // ------------------------------------------------------------------
        // 顶部：系统音频设备（选择控制对象 + 系统设备音量/静音；不修改系统默认设备）
        // ------------------------------------------------------------------

        private int _systemDefaultChangeVersion;
        private Task _systemDefaultChangeTask = Task.CompletedTask;
        private Func<EDataFlow, string, (bool Success, int HResult, string Message)> _setSystemDefault = SystemDefaultDeviceService.SetDefault;
        private Func<EDataFlow, string?> _readSystemDefaultId = AudioService.GetDefaultDeviceId;

        private void SetSystemDevices(List<AudioDeviceInfo> devices, string? defaultId)
        {
            ++_systemDefaultChangeVersion;
            foreach (var d in devices) d.IsDefault = d.Id == defaultId;

            _suppressDevCombo = true;
            try
            {
                SystemDevCombo.ItemsSource = devices;
                SystemDevCombo.SelectedItem = devices.FirstOrDefault(d => d.IsDefault);
            }
            finally { _suppressDevCombo = false; }

            _systemDeviceId = (SystemDevCombo.SelectedItem as AudioDeviceInfo)?.Id ?? defaultId;
        }

        private async void SystemDevCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDevCombo) return;
            int version = ++_systemDefaultChangeVersion;
            if (SystemDevCombo.SelectedItem is AudioDeviceInfo dev)
            {
                FlushPendingVolumes();
                ++_systemVolumeWriteVersion;
                _systemDeviceId = dev.Id;
                // 设置「快速面板更改系统默认设备」开启时：下拉切换 = 更改系统默认输出设备（IPolicyConfig.SetDefaultEndpoint）
                if (SonicRoute.Core.ConfigService.Load().PanelChangeSystemDefault)
                {
                    var previous = _systemDefaultChangeTask;
                    var apply = ApplySystemDefaultChangeAsync(previous, dev, version);
                    _systemDefaultChangeTask = apply;
                    await apply;
                    return;
                }
            }
            if (!_isClosed && version == _systemDefaultChangeVersion)
                await RefreshSystemVolumeAsync();
        }

        private async Task ApplySystemDefaultChangeAsync(Task previous, AudioDeviceInfo dev, int version)
        {
            try
            {
                try { await previous; } catch { }
                if (_isClosed || version != _systemDefaultChangeVersion) return;

                var ok = await Task.Run(() => _setSystemDefault(EDataFlow.eRender, dev.Id));
                if (_isClosed || version != _systemDefaultChangeVersion) return;

                if (ok.Success)
                {
                    ApplySystemDefaultDisplaySelection(dev.Id);
                    RefreshRowDeviceNames();
                }
                else
                {
                    ((App)System.Windows.Application.Current).ShowOsd(L10n.T("Act.SetDefaultOutput"), L10n.T("St.SysDefaultSetFail"));
                    string? actual = null;
                    try { actual = await _uiLifetime.ReadAsync(() => _readSystemDefaultId(EDataFlow.eRender)); }
                    catch (OperationCanceledException) when (_isClosed) { return; }
                    if (_isClosed || version != _systemDefaultChangeVersion) return;
                    RestoreSystemDefaultComboSelection(actual);
                    RefreshRowDeviceNames();
                }
            }
            catch (OperationCanceledException) when (_isClosed) { }
            if (!_isClosed && version == _systemDefaultChangeVersion)
                await RefreshSystemVolumeAsync();
        }

        private void ApplySystemDefaultDisplaySelection(string? defaultId)
        {
            static bool SameDevice(string? first, string? second) =>
                first != null && second != null
                && (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(AudioPolicyConfig.UnpackDeviceId(first), AudioPolicyConfig.UnpackDeviceId(second), StringComparison.OrdinalIgnoreCase));

            foreach (var output in _outputDisplay)
                output.IsDefault = SameDevice(output.Id, defaultId);
            if (SystemDevCombo.ItemsSource is IEnumerable<AudioDeviceInfo> items)
            {
                foreach (var item in items)
                    item.IsDefault = SameDevice(item.Id, defaultId);
            }
        }

        private void RestoreSystemDefaultComboSelection(string? actualDefaultId)
        {
            ApplySystemDefaultDisplaySelection(actualDefaultId);
            AudioDeviceInfo? match = null;
            if (SystemDevCombo.ItemsSource is IEnumerable<AudioDeviceInfo> items)
                match = items.FirstOrDefault(d => d.IsDefault);
            _suppressDevCombo = true;
            try
            {
                SystemDevCombo.SelectedItem = match;
                _systemDeviceId = match?.Id ?? (actualDefaultId == null ? null : AudioPolicyConfig.UnpackDeviceId(actualDefaultId));
            }
            finally { _suppressDevCombo = false; }
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
            _systemMutedForOsd = muted;
            MuteButton.Content = L10n.T("Qp.Mute");
            if (muted) MuteButton.Foreground = (Brush)FindResource("Theme.Accent");
            else MuteButton.ClearValue(Button.ForegroundProperty);
        }

        /// <summary>读取所选系统设备的音量/静音，同步顶部滑块/百分比/按钮。</summary>
        private async Task RefreshSystemVolumeAsync()
        {
            if (_isClosed) return;
            try
            {
                int request = ++_systemVolumeRequest;
                string? deviceId = _systemDeviceId;
                var (vol, muted, known) = await _uiLifetime.ReadAsync(() => SystemVolumeService.ReadStateChecked(deviceId));
                if (_isClosed || request != _systemVolumeRequest || !string.Equals(deviceId, _systemDeviceId, StringComparison.OrdinalIgnoreCase)) return;

                _systemReady = false;
                if (vol < 0)
                {
                    ApplySystemVolumeUnavailable();
                    return;
                }
                VolumeSlider.Value = vol;
                VolumePercentText.Text = $"{vol}%";
                if (known) ApplySystemMuteVisual(muted);
                VolumeSlider.IsEnabled = true;
                MuteButton.IsEnabled = known;
                _systemReady = true;

            }
            catch (OperationCanceledException) when (_isClosed) { }
        }

        private void ApplySystemVolumeUnavailable()
        {
            ++_systemVolumeRequest;
            _systemReady = false;
            VolumeSlider.Value = 0;
            VolumePercentText.Text = "—";
            VolumeSlider.IsEnabled = MuteButton.IsEnabled = false;
            MuteButton.Content = L10n.T("Qp.Mute");
            MuteButton.ClearValue(Button.ForegroundProperty);
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            VolumePercentText.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (!_systemReady) return;
            int pct = (int)Math.Round(e.NewValue);
            if (_isClosed) return;
            QueueSystemVolume(pct);
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

        internal void ApplySystemVolumeReadback(string? deviceId, int actual, bool muted)
        {
            SyncExpandedOutputReadback(deviceId, actual, muted);
            ApplySystemVolumeControls(deviceId, actual, muted);
        }

        private void ApplySystemVolumeControls(string? deviceId, int actual, bool muted)
        {
            if (_isClosed || !string.Equals(deviceId, _systemDeviceId, StringComparison.OrdinalIgnoreCase)) return;
            ++_systemVolumeRequest; // 旧初始化/选择读取不能盖过这次实际调整。
            ApplySystemVolumeReadback(actual);
            ApplySystemMuteVisual(muted);
            VolumeSlider.IsEnabled = MuteButton.IsEnabled = true;
            _systemReady = true;
        }

        internal void ApplyAppVolumeReadback(int pid, int actual, bool? muted = null)
        {
            if (_isClosed || !_rows.TryGetValue(pid, out var row)) return;
            if (_appVolumeWriter?.HasPending(pid) != true && row.Slider is not { IsMouseCaptureWithin: true })
            {
                bool updating = _updatingRowVolumes;
                _updatingRowVolumes = true;
                try
                {
                    row.Volume = actual;
                    if (row.Slider != null) { row.Slider.Value = actual; row.Slider.IsEnabled = true; }
                }
                finally { _updatingRowVolumes = updating; }
            }
            if (muted.HasValue) ApplyRowMutedVisual(row, muted.Value);
        }

        private void ApplyAppVolumeWriteReadback(int pid, bool applied, int actual)
        {
            if (_isClosed || !applied || actual < 0) return;
            if (_rows.TryGetValue(pid, out var row)
                && _appVolumeWriter?.HasPending(pid) != true
                && row.Slider is not { IsMouseCaptureWithin: true }
                && row.Volume != actual)
            {
                bool updating = _updatingRowVolumes;
                _updatingRowVolumes = true;
                try
                {
                    row.Volume = actual;
                    if (row.Slider != null) { row.Slider.Value = actual; row.Slider.IsEnabled = true; }
                }
                finally { _updatingRowVolumes = updating; }
            }
            (Application.Current as App)?.NotifyAppVolumeChanged(pid, actual, caller: this);
        }

        internal void ApplyAppMuteReadback(int pid, bool muted)
        {
            if (!_isClosed && _rows.TryGetValue(pid, out var row)) ApplyRowMutedVisual(row, muted);
        }

        internal void ApplyMicMuteReadback(bool muted)
        {
            if (_isClosed) return;
            ++_micMuteVersion;
            ApplyMicMuteVisual(muted);
            MicMuteButton.IsEnabled = true;
        }

        internal void RefreshAppRouteReadback(int pid)
        {
            if (_isClosed || !IsVisible || !_rows.TryGetValue(pid, out var row)) return;
            if (row.Expanded) { _ = BuildRowDevicesAsync(row); return; }
            if (row.ShowOutputName) _ = RefreshCollapsedRowOutputAsync(row, pid);
        }

        private async Task RefreshCollapsedRowOutputAsync(AppRow row, int pid)
        {
            var read = _readRowEndpoints;
            try
            {
                string output = await _uiLifetime.ReadAsync((Pid: pid, Read: read), static args => args.Read(args.Pid, false).Output);
                if (_isClosed || !IsVisible || !_rows.TryGetValue(pid, out var current)
                    || !ReferenceEquals(current, row) || row.Expanded || row.PendingPlayback != null) return;
                UpdateRowName(row, output);
            }
            catch (OperationCanceledException) when (_isClosed) { }
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

        private bool _systemMutedForOsd;

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            await ToggleSelectedOutputMuteAsync();
        }

        /// <summary>静音/取消静音顶部选中的系统输出设备，不修改系统默认设备。</summary>
        private async Task ToggleSelectedOutputMuteAsync()
        {
            if (!_systemReady) return;
            string? deviceId = _systemDeviceId;
            FlushPendingVolumes();
            try { await _volumeWriteTask; } catch { }
            if (_isClosed || deviceId != _systemDeviceId) return;
            var result = await Task.Run(() => SystemVolumeService.ToggleMuteChecked(deviceId));
            if (_isClosed || deviceId != _systemDeviceId) return;
            if (result.Applied) ApplySystemMuteVisual(result.Muted);
            ShowDeviceOsd(L10n.T(result.Applied ? (result.Muted ? "Qp.Muted" : "Qp.Unmuted") : "Tray.AudioUnavailable"));
        }
        // ------------------------------------------------------------------

        private string GetAppRowsSignature(List<AudioAppInfo> apps, AppConfig cfg, bool showOutputName)
        {
            var text = new StringBuilder();
            void Add(string? value) => text.Append(value?.Length ?? -1).Append(':').Append(value).Append(';');
            Add(cfg.Language); Add(cfg.ThemeMode); Add(cfg.Accent); Add(_micUiOn.ToString());
            Add(showOutputName.ToString());
            Add(cfg.QuickPanelUseLegacyAppRows.ToString());
            foreach (var app in apps)
            {
                Add(app.ProcessId.ToString()); Add(app.ProcessName); Add(app.DisplayName); Add(app.IconPath); Add(AppDisplayName.Get(app));
            }
            foreach (var device in _outputDisplay) { Add(device.Id); Add(device.DisplayName); }
            foreach (var device in _inputDisplay) { Add(device.Id); Add(device.DisplayName); }
            foreach (var id in cfg.HiddenOutputDevices) Add(id);
            Add(null);
            foreach (var id in cfg.HiddenInputDevices) Add(id);
            return text.ToString();
        }

        private async Task BuildAppRowsAsync(List<AudioAppInfo> apps, AppConfig cfg)
        {
            if (_isClosed) return;
            try
            {
                // 过滤：完整界面「应用」里关闭"在快速面板显示"的应用不列出
                var hiddenPanel = cfg.HiddenPanelApps?.Count > 0
                    ? new HashSet<string>(cfg.HiddenPanelApps, StringComparer.OrdinalIgnoreCase) : null;
                apps = apps.Where(a => !string.IsNullOrWhiteSpace(a.ProcessName)
                    && !AudioService.IsInternalApplication(a.ProcessId, a.ProcessName)
                    && hiddenPanel?.Contains(a.ProcessName) != true).ToList();
                bool virtualize = apps.Count > 20;
                bool legacyLayout = cfg.QuickPanelUseLegacyAppRows;
                bool showOutputName = legacyLayout ? cfg.QuickPanelCompactShowOutputName : cfg.QuickPanelShowOutputName;
                string signature = GetAppRowsSignature(apps, cfg, showOutputName);

                // 名称开关关闭时显示应用名，只走原音量读取，不查询播放路由。
                _globalMuteLoadState = null;
                int outputVersion = _globalMuteVersion, loadVersion = _loadVersion;
                var snapshot = await _uiLifetime.ReadAsync((Apps: apps, ShowOutputName: showOutputName, Read: _readPanelState), static state =>
                {
                    var d = new Dictionary<int, (int vol, bool muted, bool known, string? output)>();
                    if (state.Apps.Count == 0) return (States: d, AllMuted: (bool?)null);
                    var panel = state.Read(state.Apps.Select(a => (int)a.ProcessId));
                    using var routes = state.ShowOutputName ? new AudioPolicyConfig(EDataFlow.eRender) : null;
                    foreach (var a in state.Apps)
                    {
                        var pid = (int)a.ProcessId;
                        string? output = null;
                        if (state.ShowOutputName)
                        {
                            string? persisted;
                            try { persisted = ApplicationAudio.GetPhoneRoutedDeviceId(pid) ?? routes!.GetDefaultEndPoint(pid); }
                            catch { persisted = null; }
                            output = persisted == null ? AudioService.SystemDefaultDeviceId : AudioPolicyConfig.UnpackDeviceId(persisted);
                        }
                        var audio = panel.Apps[pid];
                        d[pid] = (audio.Volume, audio.Muted, audio.MuteKnown, output);
                    }
                    return (States: d, AllMuted: (bool?)panel.AllMuted);
                });
                if (_isClosed) return;
                var vols = snapshot.States;
                if (snapshot.AllMuted is { } muted)
                {
                    // A render snapshot also contains internal host streams. A known eligible PC row
                    // witnesses the result. Ambiguous or empty snapshots use the normal app lookup.
                    bool witnessed = vols.Values.Any(v => v.known && v.muted == muted);
                    _globalMuteLoadState = (loadVersion, outputVersion, muted, witnessed);
                }
                _updatingRowVolumes = true;
                if (_appRowsSignature == signature)
                {
                    foreach (var app in apps)
                    {
                        var row = _rows[(int)app.ProcessId];
                        row.App = app;
                        var value = vols[(int)app.ProcessId];
                        UpdateRowName(row, value.output);
                        ApplyRowAudioState(row, value.vol, value.muted, value.known);
                    }
                    UpdateRowHighlights();
                    return;
                }
                string layoutKey = L10n.CurrentLanguage + "|" + legacyLayout + "|" + showOutputName + "|" + virtualize;
                double scrollOffset = AppListScroll.VerticalOffset;
                if (_appRowsLayoutKey != layoutKey)
                {
                    StopMeter();
                    ClearRowVisuals();
                    AppListPanel.Items.Clear();
                    var nextPids = new HashSet<int>();
                    foreach (var app in apps) nextPids.Add((int)app.ProcessId);
                    foreach (int prevPid in _rows.Keys)
                        if (!nextPids.Contains(prevPid)) _appVolumeWriter?.Cancel(prevPid);
                    _rows.Clear();
                }
                else
                {
                    var desired = apps.ToDictionary(app => (int)app.ProcessId);
                    foreach (int stale in _rows.Keys.Where(pid => !desired.TryGetValue(pid, out var app)
                        || !SameRowApp(_rows[pid].App, app)).ToList())
                    {
                        var removed = _rows[stale];
                        AppListPanel.Items.Remove(removed.ListItem);
                        ReleaseAppRowVisual(removed);
                        _rows.Remove(stale);
                        _appVolumeWriter?.Cancel(stale);
                    }
                }
                _appRowsLayoutKey = layoutKey;
                _appRowsSignature = null;

                foreach (var app in apps)
                {
                    var pid = (int)app.ProcessId;
                    if (_rows.TryGetValue(pid, out var retained))
                    {
                        retained.App = app;
                        var value = vols[pid];
                        UpdateRowName(retained, value.output);
                        ApplyRowAudioState(retained, value.vol, value.muted, value.known);
                        retained.DeviceSignature = null;
                        if (retained.Expanded && retained.RootElement != null) _ = BuildRowDevicesAsync(retained);
                        continue;
                    }
                    var item = AppItem.From(app);
                    var row = new AppRow { App = app, IconItem = item, ShowOutputName = showOutputName, LegacyLayout = legacyLayout };

                    // 复用原图标静音、滑块与展开交互：中间上方显示名称，下方显示音量条。
                    row.Owner = this;
                    row.Virtualized = virtualize;
                    _rows[pid] = row;

                    // 初始音量/静音（无会话 -1 → 显示 —）
                    var v = vols[pid];
                    UpdateRowName(row, v.output);
                    ApplyRowAudioState(row, v.vol, v.muted, v.known);
                    if (!virtualize) CreateAppRowVisual(row);
                    AppListPanel.Items.Add(row.ListItem);
                }

                for (int index = 0; index < apps.Count; index++)
                {
                    var root = _rows[(int)apps[index].ProcessId].ListItem;
                    int targetIndex = index + AndroidRowOffset;
                    if (targetIndex < AppListPanel.Items.Count && ReferenceEquals(AppListPanel.Items[targetIndex], root)) continue;
                    AppListPanel.Items.Remove(root);
                    AppListPanel.Items.Insert(Math.Min(targetIndex, AppListPanel.Items.Count), root);
                }
                AppListScroll.ScrollToVerticalOffset(scrollOffset);

                _appRowsSignature = signature;
                UpdateRowHighlights();
            }
            catch (Exception ex)
            {
                ShowOsd(L10n.T("Qp.LoadFail") + " " + ex.Message);
            }
            finally
            {
                _updatingRowVolumes = false;
                if (!_isClosed) { RefreshAndroidRow(fromBuildAppRows: true); UpdateMeterTargets(); QueueMeterViewport(); QueueRowIconLoads(); }
            }

        }

        private static bool SameRowApp(AudioAppInfo a, AudioAppInfo b) => a.ProcessId == b.ProcessId
            && string.Equals(a.ProcessName, b.ProcessName, StringComparison.Ordinal)
            && string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal)
            && string.Equals(a.IconPath, b.IconPath, StringComparison.Ordinal);

        private void QueueRowIconLoads()
        {
            if (_isClosed || _rowIconsQueued) return;
            _rowIconsQueued = true;
            _uiLifetime.Post(Dispatcher, () =>
            {
                _rowIconsQueued = false;
                if (_isClosed || !IsVisible) return;
                var visible = new List<(AppItem Item, CancellationToken Token)>();
                foreach (var row in _rows.Values)
                {
                    if (row.IconItem.Icon != null || row.RootElement?.ActualHeight <= 0 || row.RootElement == null) continue;
                    var bounds = row.RootElement.TransformToAncestor(AppListScroll).TransformBounds(new Rect(row.RootElement.RenderSize));
                    if (bounds.Bottom > 0 && bounds.Top < AppListScroll.ActualHeight)
                        visible.Add((row.IconItem, row.VisualLifetime?.Token ?? _uiLifetime.Token));
                }
                AppItem.LoadVisibleIconsAsync(visible, _uiLifetime.Token);
            }, DispatcherPriority.Render);
        }

        private static TextBlock NewRowName()
        {
            var name = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
            return name;
        }

        private void UpdateRowName(AppRow row, string? outputId)
        {
            row.OutputId = outputId;
            if (row.Name == null) return;
            string primary;
            string? suffix = null;
            if (row.ShowOutputName)
            {
                string? displayId = row.PendingPlayback is { } pending
                    ? SelectOptimisticPrimaryOutput(pending.Outputs, outputId, preferPhysical: true)
                    : outputId;
                bool systemDefault = AudioService.IsSystemDefault(displayId);
                bool needDefault = systemDefault && !row.LegacyLayout;
                AudioDeviceInfo? output = null;
                // 直接查现有快照，不创建每行 LINQ 闭包；找到即停止。
                foreach (var device in _outputDisplay)
                {
                    if (!string.Equals(device.Id, displayId, StringComparison.OrdinalIgnoreCase)) continue;
                    output = device;
                    break;
                }
                primary = output != null ? PanelDevices.Name(output) : L10n.T(systemDefault ? "Dev.SystemDefaultOut" : "Ov.CurrentUnavailable");
                // 简洁布局不需要实际默认设备；索引遍历快照不创建闭包或枚举器。
                if (needDefault)
                {
                    var devices = _outputDisplay;
                    for (int i = 0; i < devices.Count; i++)
                        if (devices[i].IsDefault) { suffix = PanelDevices.Name(devices[i]); break; }
                }
            }
            else primary = AppDisplayName.Get(row.App);
            // 比较当前快照中的两部分，名称未变时不再分配组合字符串。
            if (row.NamePrimary == primary && row.NameSuffix == suffix) return;
            row.NamePrimary = primary;
            row.NameSuffix = suffix;
            string name = suffix == null ? primary : primary + " · " + suffix;
            row.Name.Text = name;
            row.Name.ToolTip = name;
            if (!row.LegacyLayout) row.Slider.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(row.ExpandButton, name + " " + L10n.T("Qp.ExpandDevices"));
        }

        private void RefreshRowDeviceNames()
        {
            foreach (var row in _rows.Values)
                if (row.ShowOutputName) UpdateRowName(row, row.OutputId);
#if SONICROUTE_PHONE
            foreach (var row in _androidRows.Values)
                if (row.ShowOutputName) UpdateAndroidRoute(row);
#endif
        }

        /// <summary>更新行滑块填充宽度；圆点定位与输入由标准 Track 处理。</summary>
        private void UpdateRowSliderLayout(AppRow row)
        {
            if (row.Slider == null || !VolumeTrackLayout.Update(row.Slider, row.TrackBg, row.TrackFill)) return;
            UpdatePeakVisual(row);
        }

        private void RowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is not Slider sl || sl.Tag is not AppRow row) return;
            int pct = (int)Math.Round(e.NewValue);
            row.Volume = pct;
            UpdateRowSliderLayout(row);
            if (_isClosed || _updatingRowVolumes) return; // 初始读回和刷新不写音量，防止逐行触发无用 COM 写入。
            int pid = (int)row.App.ProcessId;
            var owner = new WeakReference<QuickPanelModernWindow>(this);
            EnsureAppVolumeWriter().Enqueue(pid, pct, (target, _, applied, actual) =>
            {
                if (owner.TryGetTarget(out var panel)) panel.ApplyAppVolumeWriteReadback(target, applied, actual);
            });
        }

        private void RowSliderDragCompleted(object sender, DragCompletedEventArgs e) => _appVolumeWriter?.Flush();

        /// <summary>行滑块鼠标滚轮：悬停应用音量条时滚轮调节该应用音量 ±4%（写回走 RowSlider_ValueChanged 防抖）。</summary>
        private void RowSlider_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not Slider sl || sl.Tag is not AppRow row) return;
            int step = MathEx.Clamp(SonicRoute.Core.ConfigService.Load().VolumeStep, 1, 20);
            int pct = (int)Math.Round(sl.Value) + (e.Delta > 0 ? step : -step);
            sl.Value = MathEx.Clamp(pct, 0, 100);
            e.Handled = true;
        }
        private void VolDebounce_Tick(object? sender, EventArgs e)
        {
            bool keepThrottle = _systemVolumeThrottled && _pendingSystemVolume.HasValue;
            FlushPendingVolumes();
            _systemVolumeThrottled = keepThrottle;
            if (keepThrottle) SchedulePendingVolumeFlush(false);
        }

        private void ApplyRowAudioState(AppRow row, int volume, bool muted, bool known)
        {
            row.Volume = volume;
            if (row.Slider == null)
            {
                if (known) row.IsMuted = muted;
                row.MuteKnown = known;
                return;
            }
            row.Slider.IsEnabled = volume >= 0;
            if (volume >= 0) row.Slider.Value = volume;
            if (known) ApplyRowMutedVisual(row, muted);
            else ApplyRowMuteAppearance(row.Slider, row.MuteDot, row.IsMuted);
            row.MuteKnown = known;
            if (row.IconButton != null)
            {
                row.IconButton.IsEnabled = known && volume >= 0 && !row.MuteBusy;
                row.IconButton.ToolTip = known && volume >= 0 ? row.IconItem.Label : L10n.T("Tray.AudioUnavailable");
            }
        }

        private void ApplyRowMutedVisual(AppRow row, bool muted)
        {
            row.MuteKnown = true;
            row.IsMuted = muted;
            if (row.Slider == null)
            {
                if (!_updatingRowVolumes) UpdateMeterTargets();
                return;
            }
            if (row.IconButton != null) row.IconButton.IsEnabled = row.Slider.IsEnabled && !row.MuteBusy;
            ApplyRowMuteAppearance(row.Slider, row.MuteDot, muted);
            if (muted) row.PeakLevel = 0f;
            UpdatePeakVisual(row);
            if (!_updatingRowVolumes) UpdateMeterTargets();
        }

        private void ApplyAllRowsMutedVisual(bool muted)
        {
            bool updating = _updatingRowVolumes;
            _updatingRowVolumes = true;
            try
            {
                foreach (var row in _rows.Values) ApplyRowMutedVisual(row, muted);
                ApplyAllAndroidRowsMutedVisual(muted);
            }
            finally { _updatingRowVolumes = updating; }
            if (!updating) UpdateMeterTargets();
        }

        /// <summary>主题页开关即时生效；关闭后滑块恢复原样且工作线程和 COM 缓存全部释放。</summary>
        public void SetMeterEnabled(bool enabled)
        {
            if (_isClosed) return;
            _meterEnabled = enabled;
            if (!enabled || !IsVisible || (_rows.Count == 0 && !HasAndroidMeterRows))
            {
                StopMeter();
                foreach (var row in _rows.Values) { row.PeakLevel = 0f; UpdatePeakVisual(row); }
                ResetAndroidMeterPeaks();
                return;
            }

            if (_meter == null)
            {
                var meter = _meter = CreateAudioMeterService();
                SubscribeAndroidMeter(true);
                _meterListener = peaks => OnPeaksUpdated(meter, peaks);
                meter.PeaksUpdated += _meterListener;
                meter.UpdateVisibleTargets(Array.Empty<int>());
                meter.Start(GetMeterTargets(visibleOnly: false));
            }
            else UpdateMeterTargets();
            QueueMeterViewport();
        }

        private void StopMeter()
        {
            SubscribeAndroidMeter(false);
            var meter = _meter;
            _meter = null;
            if (meter != null)
            {
                meter.PeaksUpdated -= _meterListener;
                _ = meter.DisposeAsync();
            }
            _meterListener = null;
            _visibleMeterRows.Clear();
            ResetAndroidMeterPeaks(clearVisible: true);
            _displayPeaks = null;
            lock (_peakGate) _latestPeaks = null;
        }

        private void QueueMeterViewport()
        {
            if (_isClosed || !_meterEnabled || !IsVisible || _meterViewportQueued) return;
            _meterViewportQueued = true;
            _uiLifetime.Post(Dispatcher, () =>
            {
                _meterViewportQueued = false;
                if (!_isClosed && _meterEnabled && IsVisible) RefreshMeterViewport();
            }, DispatcherPriority.Render);
        }

        private void RefreshMeterViewport()
        {
            _visibleMeterRows.Clear();
            foreach (var row in _rows.Values)
            {
                if (row.Slider == null || !AppListScroll.IsAncestorOf(row.Slider)
                    || (IsVisible && !row.Slider.IsVisible) || row.Slider.ActualHeight <= 0) continue;
                var bounds = row.Slider.TransformToAncestor(AppListScroll).TransformBounds(new Rect(row.Slider.RenderSize));
                if (bounds.Bottom <= 0 || bounds.Top >= AppListScroll.ActualHeight) continue;
                _visibleMeterRows.Add(row);
                ApplyVisiblePeak(row);
            }
            RefreshAndroidMeterViewport();
            _meter?.UpdateVisibleTargets(GetMeterTargets(visibleOnly: true));
        }

        private void ApplyVisiblePeak(AppRow row)
        {
            if (row.Slider == null || row.IsMuted || !row.Slider.IsEnabled) return;
            float next = _displayPeaks != null && _displayPeaks.TryGetValue((int)row.App.ProcessId, out float level) ? level : 0f;
            if (row.PeakLevel == next) return;
            row.PeakLevel = next;
            UpdatePeakVisual(row);
        }

        private void UpdateMeterTargets()
        {
            UpdateMeterTargetsCore(); }
        private IEnumerable<int> GetMeterTargets(bool visibleOnly) =>
            (visibleOnly ? (IEnumerable<AppRow>)_visibleMeterRows : _rows.Values)
                .Where(r => r.Volume >= 0 && !r.IsMuted).Select(r => (int)r.App.ProcessId)
                .Concat(GetAndroidMeterTargets(visibleOnly));

        private void UpdateMeterTargetsCore()
        {
            if (_isClosed || !_meterEnabled || !IsVisible) return;
            if (_rows.Count == 0 && !HasAndroidMeterRows) { StopMeter(); return; }
            if (_meter == null) { SetMeterEnabled(true); return; }
            _meter.UpdateTargets(GetMeterTargets(visibleOnly: false));
            if (_visibleMeterRows.Count > 0 || HasVisibleAndroidMeterRows)
                _meter.UpdateVisibleTargets(GetMeterTargets(visibleOnly: true));
        }

        private void OnPeaksUpdated(AudioMeterService source, IReadOnlyDictionary<int, float> peaks)
        {
            lock (_peakGate)
            {
                if (!ReferenceEquals(source, _meter)) return;
                _latestPeaks = peaks;
            }
            // UI 忙时只保留最新快照，避免 30Hz 回调在 Dispatcher 上积压。
            if (Interlocked.Exchange(ref _peakDispatchPending, 1) != 0) return;
            try
            {
                _uiLifetime.Post(Dispatcher, new Action(() =>
                {
                    IReadOnlyDictionary<int, float>? latest;
                    lock (_peakGate) { latest = _latestPeaks; _latestPeaks = null; }
                    Interlocked.Exchange(ref _peakDispatchPending, 0);
                    if (_isClosed || !_meterEnabled || !IsVisible || latest == null) return;
                    _displayPeaks = latest;
                    foreach (var row in _visibleMeterRows) ApplyVisiblePeak(row);
                    ApplyVisibleAndroidPeaks();
                }), DispatcherPriority.Render);
            }
            catch { Interlocked.Exchange(ref _peakDispatchPending, 0); }
        }

        private void UpdatePeakVisual(Slider slider, Border? trackBg, Border? peakVisual, ScaleTransform? peakTransform, float peakLevel, bool isMuted)
        {
            if (peakVisual == null || peakTransform == null) return;
            bool show = _meterEnabled && !isMuted && slider.IsEnabled && peakLevel > 0.001f;
            var visibility = show ? Visibility.Visible : Visibility.Hidden;
            if (peakVisual.Visibility != visibility) peakVisual.Visibility = visibility;
            double scale = show ? MathEx.Clamp(peakLevel * (float)(slider.Value / 100d), 0f, 1f) : 0d;
            double width = trackBg?.ActualWidth ?? 0;
            if (width > 0)
            {
                double pixels = width * VisualTreeHelper.GetDpi(slider).DpiScaleX;
                scale = Math.Round(scale * pixels) / pixels;
            }
            if (peakTransform.ScaleX != scale) peakTransform.ScaleX = scale;
        }

        private void UpdatePeakVisual(AppRow row)
        {
            if (row.Slider != null) UpdatePeakVisual(row.Slider, row.TrackBg, row.PeakVisual, row.PeakTransform, row.PeakLevel, row.IsMuted);
        }

        /// <summary>点击行图标：静音/取消静音该应用。</summary>
        internal void RefreshAppIcons(string processName)
        {
            if (_isClosed) return;
            foreach (var row in _rows.Values)
                if (string.Equals(row.IconItem.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                {
                    if (row.RootElement != null) row.IconItem.ReloadIcon();
                    else row.IconItem = AppItem.From(row.App);
                }
            RefreshAndroidAppIcons(processName);
        }

        private async Task RowIconToggleMuteAsync(AppRow row)
        {
            if (_isClosed || row.MuteBusy || row.Slider == null || !row.Slider.IsEnabled || !row.MuteKnown) return;
            var pid = (int)row.App.ProcessId;
            row.MuteBusy = true;
            if (row.IconButton != null) row.IconButton.IsEnabled = false;
            try
            {
                var result = await Task.Run(() => SessionVolumeService.ToggleMuteChecked(pid));
                if (_isClosed || !_rows.TryGetValue(pid, out var current) || !ReferenceEquals(current, row)) return;
                if (result.Applied)
                {
                    ApplyRowMutedVisual(row, result.Muted);
                    (Application.Current as App)?.NotifyAppMuteChanged(pid, result.Muted, caller: this);
                }
                ShowOsd(AppDisplayName.Get(row.App), L10n.T(result.Applied
                    ? (result.Muted ? "Qp.Muted" : "Qp.Unmuted") : "Tray.AudioUnavailable"));
            }
            finally
            {
                row.MuteBusy = false;
                if (!_isClosed && row.IconButton != null) row.IconButton.IsEnabled = row.Slider.IsEnabled && row.MuteKnown;
            }
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
            bool switching = CollapseNativeDeviceRows(row) | CollapseAndroidRows(null);
            if (_recentDeviceRow == row) _recentDeviceRow = null;
            row.ExpandWrap.HoldCurrentState();
            UiMotion.RotateArrow(row.ExpandArrow, true, !switching);
            if (!await BuildRowDevicesAsync(row) || _isClosed || !row.Expanded) return;
#if SONICROUTE_PHONE
            CheckPhonePlaybackDevices();
#endif
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
            _scrollingRow?.ExpandWrap?.StopProgress();
            _scrollingRow = null;
        }

        /// <summary>复用展开高度动画的进度，只在必要时作最小距离滚动，无额外 Timer/Rendering 订阅。</summary>
        private Action<double>? PrepareRowScroll(AppRow row)
        {
            var root = row.RootElement;
            if (root == null || AppListScroll.ViewportHeight <= 0 || !AppListScroll.IsAncestorOf(root)) return null;
            double top = root.TransformToAncestor(AppListScroll).Transform(new System.Windows.Point(0, 0)).Y + AppListScroll.VerticalOffset;
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
                    double rowHeight = row.CollapsedHeight + row.ExpandWrap.NaturalHeight;
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
            row.ExpandPanel?.Children.Clear();
            row.DeviceButtons.Clear();
            row.DeviceSignature = null;
        }

        private void ClearRowVisuals()
        {
            StopRowScroll();
            _recentDeviceRow = null;
            foreach (var row in _rows.Values)
            {
                ReleaseAppRowVisual(row);
            }
        }

        /// <summary>先在后台一次读取输出/输入选择，再原子更新 UI；仅结构变化时重新创建设备按钮。</summary>
        private async Task<bool> BuildRowDevicesAsync(AppRow row)
        {
            int request = ++row.DeviceBuildVersion;
            int playbackRead = ++row.PlaybackReadVersion;
            int inputRead = ++row.InputReadVersion;
            int pid = (int)row.App.ProcessId;
            var cfg = ConfigService.Load();
            bool micOn = _micUiOn;
            var outputs = RowPlaybackDevices(row.App, cfg).ToList();
            var inputs = micOn ? _inputDisplay.Where(d => !cfg.HiddenInputDevices.Contains(d.Id)).ToList() : new List<AudioDeviceInfo>();
            var signature = new StringBuilder();
            void Add(string? value) => signature.Append(value?.Length ?? -1).Append(':').Append(value).Append(';');
            Add(L10n.CurrentLanguage); Add(micOn.ToString());
            foreach (var device in outputs) { Add(device.Id); Add(device.DisplayName); }
            Add(null);
            foreach (var device in inputs) { Add(device.Id); Add(device.DisplayName); }
            string structure = signature.ToString();
            try
            {
                var selected = await ReadLocalPlaybackAsync(pid, micOn);
                if (_isClosed || !row.Expanded || request != row.DeviceBuildVersion
                    || !_rows.TryGetValue(pid, out var current) || current != row) return false;
                ConfirmLocalPlayback(row, playbackRead, selected.Output, selected.Playback);
                if (micOn) ConfirmLocalInput(row, inputRead, selected.Input);
                if (row.DeviceSignature != structure)
                {
                    row.ExpandPanel.Children.Clear();
                    row.DeviceButtons.Clear();
                    void AddDevices(List<AudioDeviceInfo> devices, EDataFlow flow)
                    {
                        AddRowDeviceGroup(row.ExpandPanel, devices, flow, (button, device) =>
                        {
                            if (flow == EDataFlow.eRender)
                            {
                                button.ToolTip = L10n.T("Qp.PlaybackMultiSelect");
                                System.Windows.Automation.AutomationProperties.SetHelpText(button, (string)button.ToolTip);
                                button.Click += async (_, _) => await QueuePlaybackClickAsync(row, device, Keyboard.Modifiers);
                            }
                            else
                                button.Click += async (_, _) => await QueueInputClickAsync(row, device);
                            row.DeviceButtons.Add((button, device.Id, flow));
                        });
                    }
                    AddDevices(outputs, EDataFlow.eRender);
                    if (micOn) AddDevices(inputs, EDataFlow.eCapture);
                    row.DeviceSignature = structure;
                }
                foreach (var item in row.DeviceButtons)
                    item.Button.IsEnabled = true;
                UpdateLocalPlaybackSelection(row);
                if (micOn) UpdateLocalInputSelection(row);
                row.ExpandWrap.InvalidateMeasure();
                return true;
            }
            catch
            {
                if (!_isClosed && request == row.DeviceBuildVersion)
                {
                    foreach (var item in row.DeviceButtons) item.Button.IsEnabled = true;
                }
                return false;
            }
        }

        private void MarkLastUsed(AudioAppInfo app)
        {
            var name = app.ProcessName;
            if (string.IsNullOrWhiteSpace(name)) return;
            var cfg = ConfigService.Load();
            // 与上次记录相同则跳过写盘（减少无谓配置全量保存）
            if (string.Equals(cfg.LastUsedAppName, name, StringComparison.OrdinalIgnoreCase)) return;
            cfg.LastUsedAppName = name;
            ConfigService.QueueSave(cfg);
        }

        /// <summary>高亮当前应用行（前台自动跟随切换的操作目标）。</summary>
        private void UpdateRowHighlights()
        {
            var cur = CurrentAppService.Current;
            // net48 无 KeyValuePair<K,V>.Deconstruct（.NET Core 2.0+）：显式取 Key/Value
            foreach (var kv in _rows)
            {
                int pid = kv.Key;
                var row = kv.Value;
                if (row.HeaderBorder == null) continue;
                bool isCur = cur != null && pid == (int)cur.ProcessId;
                if (isCur) row.HeaderBorder.SetResourceReference(BackgroundProperty, "Theme.Overlay");
                else row.HeaderBorder.ClearValue(BackgroundProperty);
            }
        }

        // ------------------------------------------------------------------
        // 底部：全局输出静音 / 全局麦克风静音 / 完整界面
        // ------------------------------------------------------------------

        private async void GlobalMuteButton_Click(object sender, RoutedEventArgs e)
        {
            await ToggleGlobalOutputMuteAsync();
        }

        /// <summary>全局输出静音：静音/取消静音所有有输出会话的应用。面板按钮专用。</summary>
        private async Task ToggleGlobalOutputMuteAsync()
        {
            int version = ++_globalMuteVersion;
            var result = await Task.Run(() => ApplicationAudio.ToggleAllMuteChecked());
            bool muted = result.Muted;
            if (!_isClosed && version == _globalMuteVersion)
            {
                ++_globalMuteVersion; // 丢弃操作进行期间读到、却延迟返回的初始化状态。
                ApplyGlobalMuteVisual(muted);
                GlobalMuteButton.IsEnabled = true;
                if (result.Applied)
                {
                    ApplyAllRowsMutedVisual(muted);
                    (Application.Current as App)?.NotifyGlobalAppMuteChanged(muted, caller: this);
                }
            }
            // 全局输出静音 OSD 主标题显示「全部应用」（静音的是所有应用，不是音跃本身）
            ((App)Application.Current).ShowOsd(L10n.T("Qp.AllApps"), L10n.T(result.Applied
                ? (muted ? "Qp.GlobalMuted" : "Qp.GlobalUnmuted") : "Tray.AudioUnavailable"));
        }

        private async void MicMuteButton_Click(object sender, RoutedEventArgs e)
        {
            await ToggleGlobalMicMuteAsync();
        }

        /// <summary>全局麦克风静音：直接静音/取消静音系统所有录音设备（设备级），
        /// 与当前应用无关，所有应用录音都生效；复用全局服务和 OSD 入口。</summary>
        private async Task ToggleGlobalMicMuteAsync()
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

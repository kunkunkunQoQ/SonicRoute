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

        public QuickPanelModernWindow()
        {
            InitializeComponent();
            // 只有面板真正获得过焦点（托盘点击等正常交互）才在失焦时关闭；
            // 启动/脚本等未获焦场景下保持打开，避免一闪而过
            Activated += (_, _) => _everFocused = true;
            Deactivated += (_, _) =>
            {
                if (IsVisible && _everFocused) Close();
            };
            // 共享"当前应用"变化（前台自动跟随/概览切换）时同步面板高亮
            CurrentAppService.CurrentChanged += OnSharedCurrentChanged;
            Closed += (_, _) => CurrentAppService.CurrentChanged -= OnSharedCurrentChanged;
            // SizeToContent 下 ActualHeight 异步更新（应用列表填充/▾ 展开都会变高），
            // 尺寸变化时重定位到任务栏右下角，避免定位过早导致窗口下沉/超出屏幕
            SizeChanged += (_, _) => { if (IsVisible) PositionPanel(); };
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
        public void ShowQuickPanel()
        {
            _everFocused = false;
            // 先放到屏幕外，内容加载完再由 LoadAsync 定位，避免闪烁/溢出
            Left = -10000;
            Top = -10000;
            Show();
            Activate();
            _ = LoadAsync();
        }

        private void PositionPanel()
        {
            // 窗口已显示、内容已加载，ActualWidth/ActualHeight 即为最终尺寸，直接定位到任务栏右下角；
            // 展开 ▾ 后窗口变高时重定位，并做屏幕边界保护，避免下沉/溢出
            var work = SystemParameters.WorkArea;
            Left = work.Right - ActualWidth - 12;
            double top = work.Bottom - ActualHeight - 10;
            if (top < work.Top) top = work.Top + 6;
            Top = top;
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

                // 底部按钮初始文案（全局输出静音 / 全局麦克风静音）
                bool allMuted = await Task.Run(() => SessionVolumeService.AllMuted());
                ApplyGlobalMuteVisual(allMuted);
                bool micMuted = await Task.Run(() => GlobalMicMuteService.IsMuted());
                ApplyMicMuteVisual(micMuted);

                // 内容已就绪，重新定位到任务栏右下角（避免溢出屏幕）
                PositionPanel();
            }
            catch (Exception ex)
            {
                ShowOsd(L10n.T("Qp.LoadFail") + " " + ex.Message);
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
            if (SystemDevCombo.SelectedItem is AudioDeviceInfo dev) _systemDeviceId = dev.Id;
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
            ShowOsd(ok && actual >= 0
                ? string.Format(L10n.T("Qp.VolOk"), actual)
                : L10n.T("Qp.VolFail"));
        }

        private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_systemReady) return;
            int pct = (int)Math.Round(VolumeSlider.Value) + (e.Delta > 0 ? 4 : -4);
            VolumeSlider.Value = Math.Clamp(pct, 0, 100);
            e.Handled = true;
        }

        /// <summary>操作反馈改为右上角 OSD 通知（避免面板状态文本顶掉底部按钮）。</summary>
        private void ShowOsd(string text) => ((App)Application.Current).ShowOsd(L10n.T("App.NameFull"), text);

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_systemReady) return;
            bool muted = await Task.Run(() => SystemVolumeService.ToggleMute(_systemDeviceId));
            ApplySystemMuteVisual(muted);
            ShowOsd(L10n.T(muted ? "Qp.Muted" : "Qp.Unmuted"));
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
                ShowOsd(string.Format(L10n.T("Qp.VolOk"), actual));
            }
            return actual >= 0 ? actual : -1;
        }

        /// <summary>静音/取消静音顶部选中的系统输出设备（快捷键与面板顶部按钮一致；不修改系统默认设备）。</summary>
        public async Task<bool> MuteCurrentAppAsync()
        {
            if (!_systemReady) return false;
            bool muted = await Task.Run(() => SystemVolumeService.ToggleMute(_systemDeviceId));
            ApplySystemMuteVisual(muted);
            ShowOsd(L10n.T(muted ? "Qp.Muted" : "Qp.Unmuted"));
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

                foreach (var app in apps)
                {
                    var pid = (int)app.ProcessId;
                    var item = AppItem.From(app);
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
                        Source = item.Icon,
                        Width = 18,
                        Height = 18,
                        SnapsToDevicePixels = true
                    };
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
                        Width = 13,
                        Height = 26,
                        MinWidth = 13,
                        MinHeight = 26,
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

                    row.ExpandPanel = new StackPanel
                    {
                        Margin = new Thickness(26, 0, 0, 6),
                        Visibility = Visibility.Collapsed
                    };

                    var root = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
                    root.Children.Add(header);
                    root.Children.Add(row.ExpandPanel);
                    AppListPanel.Items.Add(root);

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
            row.Thumb.Margin = new Thickness(Math.Max(0, row.TrackFill.Width - 7), 0, 0, 0);
        }

        private async void RowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
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
            int pct = (int)Math.Round(sl.Value) + (e.Delta > 0 ? 4 : -4);
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
            ShowOsd(L10n.T(muted ? "Qp.Muted" : "Qp.Unmuted") + " · " + AppDisplayName.Get(row.App));
        }

        private async void RowExpand_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton tb || tb.Tag is not AppRow row) return;
            bool expanded = tb.IsChecked == true;
            tb.Content = expanded ? "▴" : "▾";
            row.Expanded = expanded;
            row.ExpandPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            // 展开新行时收起其他已展开的行（同一时间只保留一个折叠展开）
            if (expanded)
            {
                foreach (var kv in _rows)
                {
                    var r = kv.Value;
                    if (r == row || !r.Expanded) continue;
                    r.Expanded = false;
                    r.ExpandPanel.Visibility = Visibility.Collapsed;
                    r.ExpandPanel.Children.Clear();
                    if (r.ExpandButton != null) r.ExpandButton.IsChecked = false;
                }
            }
            if (expanded)
                await BuildRowDevicesAsync(row);
            else
                row.ExpandPanel.Children.Clear();
            // 展开/收起后面板高度变化，重新定位避免下沉/溢出
            PositionPanel();
        }

        /// <summary>为某应用行构建输出/输入设备按钮（保留设备筛选 + 自定义名 + 当前设备高亮）。</summary>
        private async Task BuildRowDevicesAsync(AppRow row)
        {
            row.ExpandPanel.Children.Clear();
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
                ShowOsd(string.Format(L10n.T("Qp.SwitchOk"),
                    (flow == EDataFlow.eRender ? "🔊 " : "🎤 ") + dev.DisplayName,
                    AppDisplayName.Get(row.App)));
            else
                ShowOsd($"✗ {msg}");

            // 仍展开时重建，刷新高亮
            if (row.Expanded) await BuildRowDevicesAsync(row);
        }

        private void MarkLastUsed(AudioAppInfo app)
        {
            var name = app.ProcessName;
            if (string.IsNullOrWhiteSpace(name)) return;
            var cfg = ConfigService.Load();
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
            ShowOsd(L10n.T(muted ? "Qp.GlobalMuted" : "Qp.GlobalUnmuted"));
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
            ShowOsd(L10n.T(muted ? "Qp.MicMuted" : "Qp.MicUnmuted"));
            return true;
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

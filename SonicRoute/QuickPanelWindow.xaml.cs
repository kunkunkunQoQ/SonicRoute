using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    /// 托盘快速切换面板：当前应用（可切换）+ 快速切换输出/输入设备 + 应用音量。
    /// 只显示设置里勾选（保留）的设备。
    /// </summary>
    public partial class QuickPanelWindow : Window
    {
        private List<AudioDeviceInfo> _outputs = new();
        private List<AudioDeviceInfo> _inputs = new();
        private List<AudioDeviceInfo> _outputDisplay = new();
        private List<AudioDeviceInfo> _inputDisplay = new();
        private AudioAppInfo? _currentApp;
        private string? _currentOutId;
        private string? _currentInId;
        private bool _volumeReady;
        private bool _suppressAppCombo;
        private bool _everFocused;

        public QuickPanelWindow()
        {
            InitializeComponent();
            // 只有面板真正获得过焦点（托盘点击等正常交互）才在失焦时关闭；
            // 启动/脚本等未获焦场景下保持打开，避免一闪而过
            Activated += (_, _) => _everFocused = true;
            Deactivated += (_, _) =>
            {
                if (IsVisible && _everFocused) Close();
            };
            // 共享"当前应用"变化（前台自动跟随/概览切换）时同步面板显示
            CurrentAppService.CurrentChanged += OnSharedCurrentChanged;
            Closed += (_, _) => CurrentAppService.CurrentChanged -= OnSharedCurrentChanged;
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
            // 窗口已显示、内容已加载，ActualWidth/ActualHeight 即为最终尺寸，直接定位到任务栏右下角
            var work = SystemParameters.WorkArea;
            Left = work.Right - ActualWidth - 12;
            Top = work.Bottom - ActualHeight - 10;
        }

        private async Task LoadAsync()
        {
            try
            {
                var (outputs, inputs) = await Task.Run(() =>
                (
                    AudioService.GetDevices(EDataFlow.eRender),
                    AudioService.GetDevices(EDataFlow.eCapture)
                ));

                _outputs = outputs;
                _inputs = inputs;
                _outputDisplay = DisplayDevices(outputs);
                _inputDisplay = DisplayDevices(inputs);

                RenderDeviceButtons(OutputButtonsPanel, _outputDisplay, true);
                RenderDeviceButtons(InputButtonsPanel, _inputDisplay, false);

                await ResolveDefaultAppAsync();

                await RefreshCurrentAppDataAsync();

                // 内容已就绪，重新定位到任务栏右下角（避免溢出屏幕）
                PositionPanel();
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
            var items = apps.Select(AppItem.From).ToList();

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
                AppNameText.Text = string.Format(L10n.T("Qp.CurrentApp"), app.Label);
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
            var (outId, inId) = await Task.Run(() =>
            (
                AudioService.GetPersistedEndpoint(pid, EDataFlow.eRender),
                AudioService.GetPersistedEndpoint(pid, EDataFlow.eCapture)
            ));

            _currentOutId = outId == null ? null : AudioPolicyConfig.UnpackDeviceId(outId);
            _currentInId = inId == null ? null : AudioPolicyConfig.UnpackDeviceId(inId);

            OutputCurrentText.Text = DescribeCurrent(_outputDisplay, _currentOutId);
            InputCurrentText.Text = DescribeCurrent(_inputDisplay, _currentInId);

            HighlightActive(OutputButtonsPanel, _currentOutId);
            HighlightActive(InputButtonsPanel, _currentInId);

            var vol = await Task.Run(() =>
            {
                SessionVolumeService.Refresh();
                return (pct: SessionVolumeService.GetVolumePercent(pid), muted: SessionVolumeService.IsMuted(pid));
            });

            if (vol.pct >= 0)
            {
                VolumeSlider.Value = vol.pct;   // 此时 _volumeReady 仍为 false，ValueChanged 不会写回
                VolumePercentText.Text = $"{vol.pct}%";
                MuteButton.Content = L10n.T(vol.muted ? "Qp.Unmute" : "Qp.Mute");
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
                MuteButton.Content = L10n.T("Qp.Mute");
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
                var def = devices.FirstOrDefault(d => d.IsDefault);
                return def != null ? L10n.T("Ov.Default") + def.DisplayName : L10n.T("Ov.Unset");
            }
            var dev = devices.FirstOrDefault(d => string.Equals(d.Id, currentShortId, StringComparison.OrdinalIgnoreCase));
            return dev != null ? dev.DisplayName! : L10n.T("Ov.CurrentUnavailable");
        }

        // ------------------------------------------------------------------
        // 设备按钮渲染 + 点击切换
        // ------------------------------------------------------------------

        private void RenderDeviceButtons(ItemsControl panel, List<AudioDeviceInfo> devices, bool isOutput)
        {
            panel.Items.Clear();
            var cfg = ConfigService.Load();
            var hidden = isOutput ? cfg.HiddenOutputDevices : cfg.HiddenInputDevices;
            foreach (var dev in devices)
            {
                if (hidden.Contains(dev.Id)) continue;
                var btn = new Button
                {
                    Content = ShortName(dev.DisplayName),
                    Tag = dev,
                    ToolTip = dev.DisplayName
                };
                btn.SetResourceReference(StyleProperty, "DevButton");
                btn.Click += (_, _) => OnDeviceButtonClick(dev, isOutput);
                panel.Items.Add(btn);
            }
        }

        private void HighlightActive(ItemsControl panel, string? activeShortId)
        {
            foreach (var item in panel.Items)
            {
                if (item is not Button btn || btn.Tag is not AudioDeviceInfo dev) continue;
                bool active = activeShortId != null &&
                              string.Equals(dev.Id, activeShortId, StringComparison.OrdinalIgnoreCase);
                btn.SetResourceReference(StyleProperty, active ? "DevButtonActive" : "DevButton");
            }
        }

        private async void OnDeviceButtonClick(AudioDeviceInfo dev, bool isOutput)
        {
            if (_currentApp == null)
            {
                PanelStatusText.Text = L10n.T("Qp.NoAudioMsg");
                return;
            }

            MarkLastUsed(_currentApp);
            var pid = (int)_currentApp.ProcessId;
            var flow = isOutput ? EDataFlow.eRender : EDataFlow.eCapture;
            var (ok, _, msg) = await Task.Run(() => AudioService.ApplyEndpoint(pid, flow, dev.Id));
            if (ok)
                PanelStatusText.Text = string.Format(L10n.T("Qp.SwitchOk"), (isOutput ? "🔊 " : "🎤 ") + dev.DisplayName, _currentApp.Label);
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
        internal async Task<int> AdjustVolumeAsync(int delta)
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
            int pct = (int)Math.Round(VolumeSlider.Value) + (e.Delta > 0 ? 4 : -4);
            VolumeSlider.Value = Math.Clamp(pct, 0, 100);
            e.Handled = true;
        }

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            await MuteCurrentAppAsync();
        }

        /// <summary>静音/取消静音当前应用（面板显示的应用）。面板按钮与静音快捷键共用同一条
        /// 路径，保证快捷键静音的就是面板/概览显示的同一个当前应用；同时回写面板按钮文字与
        /// 状态行，让快捷键操作在面板上有可见反馈。返回是否真正执行。</summary>
        internal async Task<bool> MuteCurrentAppAsync()
        {
            if (_currentApp == null || !_volumeReady) return false;
            MarkLastUsed(_currentApp);
            bool muted = await Task.Run(() => SessionVolumeService.ToggleMute((int)_currentApp.ProcessId));
            MuteButton.Content = L10n.T(muted ? "Qp.Unmute" : "Qp.Mute");
            PanelStatusText.Text = L10n.T(muted ? "Qp.Muted" : "Qp.Unmuted");
            return true;
        }

        private async void MicMuteButton_Click(object sender, RoutedEventArgs e)
        {
            await MicMuteCurrentAppAsync();
        }

        /// <summary>静音/取消静音当前应用的麦克风（输入会话）。与扬声器静音同一套语义
        /// （读组内第一个、写遍历全部），面板按钮与麦克风静音快捷键共用；无输入会话时
        /// 状态行提示并返回 false。返回是否真正执行。</summary>
        internal async Task<bool> MicMuteCurrentAppAsync()
        {
            if (_currentApp == null) return false;
            MarkLastUsed(_currentApp);
            var r = await Task.Run(() => SessionVolumeService.ToggleInputMuteChecked((int)_currentApp.ProcessId));
            if (!r.Applied)
            {
                PanelStatusText.Text = L10n.T("Qp.MicNoSession");
                return false;
            }
            MicMuteButton.Content = L10n.T(r.Muted ? "Qp.MicUnmute" : "Qp.MicMute");
            PanelStatusText.Text = L10n.T(r.Muted ? "Qp.MicMuted" : "Qp.MicUnmuted");
            return true;
        }

        // ------------------------------------------------------------------
        // 底部按钮
        // ------------------------------------------------------------------

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

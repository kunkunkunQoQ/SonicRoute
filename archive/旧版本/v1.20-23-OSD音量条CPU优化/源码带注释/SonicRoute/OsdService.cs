using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using SonicRoute.Core;
using SonicRoute.Core.Compat;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SonicRoute
{
    /// <summary>共享 OSD：输入触发更新，复用窗口；音量专用控件可完全移除，没有周期刷新。</summary>
    internal sealed class OsdService : IDisposable
    {
        private readonly struct Notice
        {
            internal Notice(string title, string text, int volume = -1, bool muted = false, string? target = null)
            { Title = title; Text = text; Volume = volume; Muted = muted; Target = target ?? title; }
            internal readonly string Title, Text, Target;
            internal readonly int Volume;
            internal readonly bool Muted;
        }

        private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
        private readonly DispatcherTimer _hideTimer;
        private const int BarFramesPerSecond = 30;
        private static readonly TimeSpan BarFrameInterval = TimeSpan.FromMilliseconds(1000 / BarFramesPerSecond);
        private static readonly QuadraticEase BarEase = CreateBarEase();
        private readonly EventHandler _barRenderingHandler;
        private Window? _window;
        private Border? _surface;
        private StackPanel? _stack;
        private TextBlock? _title, _text;
        private StackPanel? _volumePanel;
        private Grid? _volumeRow, _track;
        private TextBlock? _volumeIcon, _volumeCaption, _percent;
        private Rectangle? _fill;
        private ScaleTransform? _fillScale;
        private HwndSource? _source;
        private DispatcherOperation? _placementOperation;
        private Notice _notice;
        private bool _hasNotice, _volumeVisible, _metricsDirty = true, _positionDirty = true;
        private bool _barMuted, _barAnimating, _barUpdatePending;
        private double _barTarget = double.NaN, _pendingBarTarget;
        private string? _barOwner;
        private TimeSpan _barAnimationStartedAt;
        private double _width = 240, _fontScale = 1;
        private string _position = "";
        private int _offsetX, _offsetY, _customX, _customY;
        private int? _physicalX, _physicalY;
        private int _nativeWidth, _nativeHeight;
        private bool _fadingOut, _adjusting, _dragging, _micPersistent, _restorePending, _showingMic;
        private volatile bool _disposed;
        private long _messageVersion, _micVersion, _fadeVersion, _barVersion;
        private Task<bool>? _micQuery;
        private bool _micQueryTrackInput;
        private long _micQueryVersion;
        private OsdPlacement.NativeRect _dragStart;
        private OsdPlacement.NativePoint _cursorStart;

        internal event Action? AdjustFinished;
        internal long MicStateVersion => _micVersion;

        internal OsdService()
        {
            _barRenderingHandler = BarRendering;
            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
            _hideTimer.Tick += HideTimer_Tick;
        }

        internal void Show(string title, string text) => ShowCore(new Notice(title, text));

        internal void ShowVolume(string title, int volume, string text, bool muted = false, string? target = null)
            => ShowCore(new Notice(title, text, MathEx.Clamp(volume, 0, 100), muted, target));

        private void ShowCore(Notice notice, bool persistent = false, bool adjustment = false)
        {
            if (_disposed || _dispatcher.HasShutdownStarted || (_adjusting && !adjustment)) return;
            try
            {
                var config = ConfigService.Load();
                EnsureWindow();
                bool wasVisible = _window!.IsVisible;
                bool animateBar = wasVisible && _volumeVisible && _hasNotice && _notice.Target == notice.Target;
                bool wasFading = _fadingOut;
                ++_messageVersion;
                _hideTimer.Stop();
                if (!persistent && _micPersistent && !adjustment) _restorePending = true;
                _showingMic = persistent;
                _notice = notice;
                _hasNotice = true;
                ApplyConfig(config);
                ApplyNotice(config, animateBar);
                ApplyMetrics();

                if (!wasVisible)
                {
                    // 初始透明度、尺寸与原生位置先准备好，Show 之后不强制再次布局。
                    StopFade();
                    int fadeIn = FadeIn(config);
                    _window.Opacity = fadeIn > 0 ? 0 : 1;
                    _positionDirty = true;
                    PlaceNow(config);
                    _window.Show();
                    if (fadeIn > 0) AnimateOpacity(1, fadeIn);
                }
                else if (wasFading)
                {
                    // 读当前有效透明度后再移除旧时钟，避免直接跳回全亮。
                    AnimateOpacity(1, FadeIn(config));
                }
                if (_positionDirty) QueuePlacement();
                if (!persistent && !_adjusting) _hideTimer.Start();
            }
            catch { } // 音频目标退出或 Dispatcher 关闭不打断输入处理。
        }

        private void EnsureWindow()
        {
            if (_window != null) return;
            // 主标题使用随深浅模式调整的强调色，其余文字直接跟随用户强调色。
            _title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            _title.SetResourceReference(TextBlock.ForegroundProperty, "Theme.OsdTitleText");
            _text = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            _text.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Accent");
            _stack = new StackPanel();
            _stack.Children.Add(_title);
            _stack.Children.Add(_text);
            _surface = new Border { BorderThickness = new Thickness(1), Child = _stack, ClipToBounds = true };
            _surface.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceBgAlpha");
            _surface.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
            _window = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.Manual, Focusable = false, Content = _surface,
                UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            _window.SourceInitialized += Window_SourceInitialized;
            _window.MouseLeftButtonDown += Window_MouseDown;
            _window.MouseMove += Window_MouseMove;
            _window.MouseLeftButtonUp += Window_MouseUp;
            _window.LostMouseCapture += Window_LostCapture;
            SystemParameters.StaticPropertyChanged += SystemParameters_Changed;
            SystemEvents.DisplaySettingsChanged += DisplaySettings_Changed;
        }

        private void ApplyConfig(AppConfig config)
        {
            double width = MathEx.Clamp(config.OsdWidth, 180, 600), scale = MathEx.Clamp(config.OsdFontScale, 0.7, 2);
            if (_width != width || _fontScale != scale)
            { _width = width; _fontScale = scale; _metricsDirty = true; _positionDirty = true; }
            string position = config.OsdPosition ?? "TR";
            if (_position != position || _offsetX != config.OsdOffsetX || _offsetY != config.OsdOffsetY ||
                _customX != config.OsdCustomX || _customY != config.OsdCustomY ||
                _physicalX != config.OsdCustomPhysicalX || _physicalY != config.OsdCustomPhysicalY)
            {
                _position = position; _offsetX = config.OsdOffsetX; _offsetY = config.OsdOffsetY;
                _customX = config.OsdCustomX; _customY = config.OsdCustomY;
                _physicalX = config.OsdCustomPhysicalX; _physicalY = config.OsdCustomPhysicalY;
                _positionDirty = true;
            }
            if (!config.OsdVolumeVisualEnabled) RemoveVolumeVisual();
        }

        private void ApplyNotice(AppConfig config, bool animate)
        {
            SetText(_title!, _notice.Title);
            bool volume = config.OsdVolumeVisualEnabled && _notice.Volume >= 0;
            if (_volumeVisible != volume)
            { _volumeVisible = volume; _metricsDirty = true; _positionDirty = true; }
            _text!.Visibility = volume ? Visibility.Collapsed : Visibility.Visible;
            if (!volume)
            {
                SetText(_text, _notice.Text);
                StopBar();
                if (_volumePanel != null) _volumePanel.Visibility = Visibility.Collapsed;
                return;
            }
            EnsureVolumeVisual();
            _volumePanel!.Visibility = Visibility.Visible;
            SetText(_volumeIcon!, _notice.Muted ? "🔇" : "🔊");
            SetText(_volumeCaption!, _notice.Muted ? L10n.T("Ov.Muted") : "");
            SetText(_percent!, _notice.Volume + "%");
            if (_barMuted != _notice.Muted)
            {
                _barMuted = _notice.Muted;
                _fill!.SetResourceReference(Rectangle.FillProperty, _barMuted ? "Theme.OsdTitle" : "Theme.OsdAccent");
            }
            UpdateBar(_notice.Volume / 100.0, animate);
        }

        private void EnsureVolumeVisual()
        {
            if (_volumePanel != null) return;
            StopBar();
            _volumeIcon = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            _volumeIcon.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Accent");
            _volumeCaption = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            _volumeCaption.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Accent");
            _percent = new TextBlock { FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center };
            _percent.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Accent");
            _volumeRow = new Grid();
            _volumeRow.ColumnDefinitions.Add(new ColumnDefinition());
            _volumeRow.ColumnDefinitions.Add(new ColumnDefinition());
            _volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // 图标、数字靠左；静音说明使用剩余空间，数字列保持固定宽度。
            Grid.SetColumn(_percent, 1); Grid.SetColumn(_volumeCaption, 2);
            _volumeRow.Children.Add(_volumeIcon); _volumeRow.Children.Add(_volumeCaption); _volumeRow.Children.Add(_percent);
            _fillScale = new ScaleTransform(1, 1);
            _fill = new Rectangle { RenderTransform = _fillScale, RenderTransformOrigin = new System.Windows.Point(0, 0.5) };
            _fill.SetResourceReference(Rectangle.FillProperty, "Theme.OsdAccent");
            _barMuted = false;
            _track = new Grid { ClipToBounds = true };
            _track.SetResourceReference(Grid.BackgroundProperty, "Theme.SliderBg");
            _track.Children.Add(_fill);
            _volumePanel = new StackPanel();
            _volumePanel.Children.Add(_volumeRow); _volumePanel.Children.Add(_track);
            _stack!.Children.Add(_volumePanel);
            _metricsDirty = true;
        }

        private void RemoveVolumeVisual()
        {
            if (_volumePanel == null) return;
            StopBar();
            _stack!.Children.Remove(_volumePanel);
            _volumePanel = null; _volumeRow = null; _track = null;
            _volumeIcon = null; _volumeCaption = null; _percent = null; _fill = null; _fillScale = null;
            _metricsDirty = true;
        }

        private void ApplyMetrics()
        {
            if (!_metricsDirty || _window == null) return;
            _metricsDirty = false;
            double s = _fontScale, available = _width - 32 * s - 2;
            _surface!.Padding = new Thickness(16 * s, 10 * s, 16 * s, 10 * s);
            _surface.CornerRadius = new CornerRadius(10 * s);
            _window.Width = _width;
            _title!.FontSize = 12 * s; _title.Height = 18 * s; _title.MaxWidth = available;
            _text!.FontSize = 18 * s; _text.Height = 27 * s; _text.MaxWidth = available;
            _text.Margin = new Thickness(0, 3 * s, 0, 0);
            if (_volumePanel != null)
            {
                _volumePanel.Margin = new Thickness(0, 3 * s, 0, 0);
                _volumeRow!.Height = 27 * s;
                double iconWidth = Math.Min(28 * s, available * 0.26);
                double numberSize = Math.Min(18 * s, (available - iconWidth) / 3.5);
                _volumeRow.ColumnDefinitions[0].Width = new GridLength(iconWidth);
                _volumeRow.ColumnDefinitions[1].Width = new GridLength(3.1 * numberSize);
                _volumeIcon!.FontSize = Math.Min(18 * s, iconWidth / 1.3);
                _volumeCaption!.FontSize = 12 * s;
                _percent!.FontSize = numberSize;
                _track!.Height = 4 * s; _track.Margin = new Thickness(0, 8 * s, 0, 0);
            }
            // 两行固定行高：数字/emoji 变化不会改变窗口高度，无需 UpdateLayout。
            _window.Height = 68 * s + 2 + (_volumeVisible ? 12 * s : 0);
            _positionDirty = true;
        }

        internal void ApplySize(double width, double scale)
        {
            if (_disposed || _window == null) return;
            width = MathEx.Clamp(width, 180, 600); scale = MathEx.Clamp(scale, 0.7, 2);
            if (_width == width && _fontScale == scale) return;
            _width = width; _fontScale = scale; _metricsDirty = true;
            ApplyMetrics();
            if (_window.IsVisible) QueuePlacement();
        }

        internal void PreviewSize(int width, double scale)
        {
            ApplySize(width, scale);
            if (!_adjusting) Show("📍", L10n.T("Exp.OsdPreview"));
        }

        internal void NotifyVolumeVisualChanged()
        {
            if (_disposed || _window == null) return;
            var config = ConfigService.Load();
            ApplyConfig(config);
            if (_hasNotice) ApplyNotice(config, false);
            ApplyMetrics();
            if (_window.IsVisible) QueuePlacement();
        }

        private void StopBar()
        {
            CancelBarUpdate();
            StopBarAnimation();
            _barTarget = double.NaN;
            _barOwner = null;
        }

        private void UpdateBar(double target, bool animate)
        {
            if (!animate || _barOwner != _notice.Target || !UiMotion.AnimationsEnabled || !UiMotion.HardwareRendering)
            {
                // 首次显示、目标切换和禁用动画时直接显示最新值，不等待绘制回调。
                CancelBarUpdate();
                _barOwner = _notice.Target;
                ApplyBar(target, false);
                return;
            }
            if (_barTarget == target)
            {
                // 新输入回到活动动画的目标时，取消中间请求并保留原动画。
                CancelBarUpdate();
                return;
            }
            _pendingBarTarget = target;
            if (_barUpdatePending) return;
            _barUpdatePending = true;
            // 只为尚未绘制的最新目标注册一次；无常驻 Rendering 订阅或新计时器。
            CompositionTarget.Rendering += _barRenderingHandler;
        }

        private void BarRendering(object? sender, EventArgs e)
        {
            if (!_barUpdatePending) return;
            if (_disposed || _dispatcher.HasShutdownStarted || _window?.IsVisible != true ||
                !_volumeVisible || _fillScale == null || !_hasNotice || _notice.Target != _barOwner)
            {
                StopBar();
                return;
            }
            // 给 30 FPS 时钟留下推进时间，避免高刷新率下每帧替换刚启动的动画。
            if (e is RenderingEventArgs frame && _barAnimating &&
                frame.RenderingTime >= _barAnimationStartedAt &&
                frame.RenderingTime - _barAnimationStartedAt < BarFrameInterval) return;
            double target = _pendingBarTarget;
            CancelBarUpdate();
            if (e is RenderingEventArgs rendered) _barAnimationStartedAt = rendered.RenderingTime;
            ApplyBar(target, UiMotion.AnimationsEnabled && UiMotion.HardwareRendering);
        }

        private void CancelBarUpdate()
        {
            if (!_barUpdatePending) return;
            _barUpdatePending = false;
            CompositionTarget.Rendering -= _barRenderingHandler;
        }

        private void ApplyBar(double target, bool animate)
        {
            if (_fillScale == null || (_barTarget == target && (animate || !_barAnimating))) return;
            double current = _fillScale.ScaleX;
            StopBarAnimation();
            _barTarget = target;
            _fillScale.ScaleX = target;
            double distance = Math.Abs(current - target);
            if (!animate || distance < 0.0001) return;

            // Rendering 在布局后执行，使用真实轨道宽度和 DPI 判断物理像素。
            double pixelWidth = _track == null ? 0 : _track.ActualWidth * VisualTreeHelper.GetDpi(_track).DpiScaleX;
            if (pixelWidth > 0 && distance * pixelWidth < 1) return;

            long version = ++_barVersion;
            var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(90))
            { EasingFunction = BarEase };
            // 只降低音量条过渡的期望帧率；数字、淡入淡出和其他动画不受影响。
            Timeline.SetDesiredFrameRate(animation, BarFramesPerSecond);
            animation.Completed += (_, _) =>
            {
                if (_disposed || version != _barVersion) return;
                StopBarAnimation();
                if (_fillScale != null) _fillScale.ScaleX = target;
            };
            _barAnimating = true;
            _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        }

        private void StopBarAnimation()
        {
            if (!_barAnimating || _fillScale == null) return;
            _barAnimating = false;
            ++_barVersion;
            double current = _fillScale.ScaleX;
            _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _fillScale.ScaleX = current;
        }

        private static QuadraticEase CreateBarEase()
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            return ease;
        }

        private static void SetText(TextBlock block, string value) { if (block.Text != value) block.Text = value; }
        private static int FadeIn(AppConfig config) => UiMotion.AnimationsEnabled ? MathEx.Clamp(config.OsdFadeInMs, 0, 500) : 0;

        private void StopFade()
        {
            ++_fadeVersion;
            _fadingOut = false;
            if (_window == null) return;
            double current = _window.Opacity;
            _window.BeginAnimation(Window.OpacityProperty, null);
            _window.Opacity = current;
        }

        private void AnimateOpacity(double target, int milliseconds)
        {
            double current = _window!.Opacity;
            StopFade();
            _window.Opacity = target;
            if (milliseconds <= 0 || Math.Abs(current - target) < 0.0001)
            {
                if (target == 0) { Hide(); _ = RestoreMicAsync(); }
                return;
            }
            long version = _fadeVersion;
            _fadingOut = target == 0;
            var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(milliseconds))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            animation.Completed += (_, _) =>
            {
                if (_disposed || version != _fadeVersion) return;
                StopFade(); _window.Opacity = target;
                if (target == 0) { Hide(); _ = RestoreMicAsync(); }
            };
            _window.BeginAnimation(Window.OpacityProperty, animation);
        }

        private void HideTimer_Tick(object? sender, EventArgs e)
        {
            _hideTimer.Stop();
            if (_disposed || _adjusting) return;
            if (_window?.IsVisible != true) { _ = RestoreMicAsync(); return; }
            var config = ConfigService.Load();
            int duration = UiMotion.AnimationsEnabled ? MathEx.Clamp(config.OsdFadeOutMs, 0, 1000) : 0;
            AnimateOpacity(0, duration);
        }

        private void Hide()
        {
            StopFade(); StopBar();
            if (_window == null) return;
            _window.Hide();
            _window.Opacity = 1;
        }

        // 仅合并同一监听策略、尚未完成的查询；不跨时刻缓存实际静音状态。
        internal Task<bool> QueryMicMuteAsync(bool trackInput)
        {
            if (_disposed) return Task.FromResult(false);
            if (_micQuery != null && !_micQuery.IsCompleted && _micQueryTrackInput == trackInput && _micQueryVersion == _micVersion)
                return _micQuery;
            _micQueryTrackInput = trackInput;
            _micQueryVersion = _micVersion;
            return _micQuery = Task.Run(() => GlobalMicMuteService.IsAnyMuted(trackInput));
        }

        internal void ShowMic(string title, bool muted)
        {
            if (_disposed) return;
            ++_micVersion;
            _micPersistent = muted && ConfigService.Load().MicMuteOsdPersistent;
            _restorePending = _micPersistent && _adjusting;
            if (_adjusting) return;
            ShowCore(new Notice(title, L10n.T(muted ? "Ov.MicMuted" : "Ov.MicUnmuted")), _micPersistent);
        }

        internal async Task NotifyMicSettingChangedAsync(bool on)
        {
            if (_disposed) return;
            long micVersion = ++_micVersion;
            if (!on)
            {
                _micPersistent = false; _restorePending = false;
                if (_showingMic && !_adjusting) { _showingMic = false; _hideTimer.Stop(); _hideTimer.Start(); }
                return;
            }
            try
            {
                bool track = ConfigService.Load().MicMuteOsdTrackInputMuted;
                bool muted = await QueryMicMuteAsync(track);
                var config = ConfigService.Load();
                if (_disposed || _dispatcher.HasShutdownStarted || micVersion != _micVersion ||
                    !config.MicMuteOsdPersistent || config.MicMuteOsdTrackInputMuted != track) return;
                _micPersistent = muted;
                if (!muted)
                {
                    _restorePending = false;
                    if (_showingMic && !_adjusting) ShowMic(L10n.T("Ov.MuteMic"), false);
                }
                else if (_adjusting || (_window?.IsVisible == true && !_showingMic))
                    _restorePending = true;
                else ShowMic(L10n.T("Ov.MuteMic"), true);
            }
            catch { }
        }

        private async Task RestoreMicAsync()
        {
            if (_disposed || _adjusting || !_restorePending || !_micPersistent) return;
            try
            {
                long message = _messageVersion, mic = _micVersion;
                bool track = ConfigService.Load().MicMuteOsdTrackInputMuted;
                bool muted = await QueryMicMuteAsync(track);
                var config = ConfigService.Load();
                if (_disposed || _dispatcher.HasShutdownStarted || _adjusting || message != _messageVersion ||
                    mic != _micVersion || _window?.IsVisible == true || !config.MicMuteOsdPersistent ||
                    config.MicMuteOsdTrackInputMuted != track) return;
                _restorePending = false;
                if (muted) ShowMic(L10n.T("Ov.MuteMic"), true);
                else { _micPersistent = false; ++_micVersion; }
            }
            catch { }
        }

        internal void BeginAdjust()
        {
            if (_disposed) return;
            _adjusting = true;
            ShowCore(new Notice(L10n.T("Exp.OsdDragTitle"), L10n.T("Exp.OsdDragHint")), adjustment: true);
            _hideTimer.Stop();
        }

        internal void CancelAdjust()
        {
            ++_messageVersion;
            _adjusting = false; _dragging = false;
            _window?.ReleaseMouseCapture();
            _hideTimer.Stop(); Hide();
            _restorePending = _micPersistent;
            _ = RestoreMicAsync();
        }

        internal void Preview() { if (!_adjusting) Show("📍", L10n.T("Exp.OsdPreview")); }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_adjusting || _window == null || !OsdPlacement.TryDragStart(_window, out _dragStart, out _cursorStart)) return;
            _dragging = _window.CaptureMouse();
            e.Handled = true;
        }
        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _window == null) return;
            OsdPlacement.Drag(_window, _dragStart, _cursorStart);
            e.Handled = true;
        }
        private void Window_LostCapture(object sender, MouseEventArgs e) { _dragging = false; }
        private void Window_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging || _window == null) return;
            _dragging = false;
            _window.ReleaseMouseCapture();
            e.Handled = true;
            try
            {
                OsdPlacement.Save(_window, ConfigService.Load());
                _adjusting = false;
                Show("📍", L10n.T("Exp.OsdSaved"));
                AdjustFinished?.Invoke();
            }
            catch { }
        }

        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(_window!).Handle);
            _source?.AddHook(WindowMessages);
        }
        private IntPtr WindowMessages(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x02E0 || message == 0x007E || message == 0x001A) InvalidatePlacement();
            // Lite 的系统 DPI 模式跨屏时可能由 Windows 位图缩放；实际尺寸变化也需要重新限位。
            if (message == 0x0047 && _window != null && OsdPlacement.TryWindowRect(_window, out var rect))
            {
                int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
                if (width != _nativeWidth || height != _nativeHeight)
                { _nativeWidth = width; _nativeHeight = height; InvalidatePlacement(); }
            }
            return IntPtr.Zero;
        }
        private void SystemParameters_Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "WorkArea" || e.PropertyName?.StartsWith("VirtualScreen", StringComparison.Ordinal) == true)
                InvalidatePlacement();
        }
        private void DisplaySettings_Changed(object? sender, EventArgs e) => InvalidatePlacement();
        private void InvalidatePlacement()
        {
            if (_disposed || _dispatcher.HasShutdownStarted) return;
            if (!_dispatcher.CheckAccess())
            {
                try { _dispatcher.BeginInvoke(new Action(InvalidatePlacement)); }
                catch (InvalidOperationException) { }
                return;
            }
            _positionDirty = true;
            if (_window?.IsVisible == true && !_dragging) QueuePlacement();
        }
        private void QueuePlacement()
        {
            if (_disposed || _placementOperation != null || _dragging) return;
            _placementOperation = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _placementOperation = null;
                if (!_disposed && !_dragging && _window?.IsVisible == true && _positionDirty)
                    PlaceNow(ConfigService.Load());
            }));
        }
        private void PlaceNow(AppConfig config)
        {
            if (_window == null || _disposed) return;
            _positionDirty = false;
            try { OsdPlacement.Apply(_window, config); }
            catch { _positionDirty = true; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ++_messageVersion; ++_micVersion;
            _restorePending = false;
            _hideTimer.Stop(); _hideTimer.Tick -= HideTimer_Tick;
            _placementOperation?.Abort(); _placementOperation = null;
            SystemParameters.StaticPropertyChanged -= SystemParameters_Changed;
            SystemEvents.DisplaySettingsChanged -= DisplaySettings_Changed;
            StopFade(); StopBar();
            try { _source?.RemoveHook(WindowMessages); } catch (ObjectDisposedException) { }
            _source = null;
            if (_window != null)
            {
                _window.SourceInitialized -= Window_SourceInitialized;
                _window.MouseLeftButtonDown -= Window_MouseDown;
                _window.MouseMove -= Window_MouseMove;
                _window.MouseLeftButtonUp -= Window_MouseUp;
                _window.LostMouseCapture -= Window_LostCapture;
                try { _window.Close(); } catch (InvalidOperationException) { }
            }
            _window = null; _surface = null; _stack = null; _title = null; _text = null;
            _volumePanel = null; _volumeRow = null; _track = null; _fill = null; _fillScale = null;
            _volumeIcon = null; _volumeCaption = null; _percent = null;
            _micQuery = null; _notice = default; _hasNotice = false;
            AdjustFinished = null;
        }
    }
}

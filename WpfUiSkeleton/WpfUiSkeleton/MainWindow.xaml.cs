using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace WpfUiSkeleton
{
    public partial class MainWindow : Window
    {
        // ===== 无边框窗口：最大化用手动尺寸（WindowState.Maximized 会溢出屏幕）=====
        private bool _titleMaximized;
        private readonly double _restoreWidth = 820, _restoreHeight = 560;

        // 当前主题/强调色状态
        private string _currentMode = "auto";
        private string _currentAccent = "blue";

        public MainWindow()
        {
            InitializeComponent();
            // 启动时应用主题（跟随系统 + 蓝色强调色）+ 默认透明度 85%
            ThemeService.Apply(_currentMode, _currentAccent);
            ThemeService.ApplyBackgroundOpacity(85);
            SourceInitialized += MainWindow_SourceInitialized;
        }

        // ===== 任务栏图标点击最小化（无边框窗口需要手动拦截 WM_SYSCOMMAND）=====
        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WndProc);
        }

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_SYSCOMMAND && (((int)wParam) & 0xFFF0) == SC_MINIMIZE)
            {
                WindowState = WindowState.Minimized;
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ==================================================================
        // 标题栏：拖动 / 最小化 / 最大化 / 关闭
        // ==================================================================

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
        // 导航切换（多页面用 Visibility 切换，不用 Frame/Page）
        // ==================================================================

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            var tag = ((RadioButton)sender).Tag as string;
            OverviewPage.Visibility = tag == "Overview" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void GotoSettings(object sender, RoutedEventArgs e)
        {
            NavSettings.IsChecked = true;
        }

        // ==================================================================
        // 主题 / 强调色 / 透明度 / 语言
        // ==================================================================

        private void ApplyTheme()
        {
            ThemeService.Apply(_currentMode, _currentAccent);
        }

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _currentMode = ((RadioButton)sender).Tag as string ?? "auto";
            ApplyTheme();
        }

        private void Accent_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _currentAccent = ((RadioButton)sender).Tag as string ?? "blue";
            ApplyTheme();
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            int pct = (int)Math.Round(e.NewValue);
            ThemeService.ApplyBackgroundOpacity(pct);
            OpacityText.Text = $"{pct}%";
        }

        private void Lang_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            var lang = ((RadioButton)sender).Tag as string ?? "zh";
            L10n.Instance.SetLanguage(lang);
        }
    }
}

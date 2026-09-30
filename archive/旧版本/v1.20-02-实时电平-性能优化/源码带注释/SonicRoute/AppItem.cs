using System.ComponentModel;
using System.Windows.Media;
using SonicRoute.Core;
using SonicRoute.Core.Models;

namespace SonicRoute
{
    /// <summary>带图标的应用列表项（UI 视图模型，供应用切换器 / 应用列表）。</summary>
    public sealed class AppItem : INotifyPropertyChanged
    {
        public required AudioAppInfo Info { get; init; }
        public int ProcessId => (int)Info.ProcessId;
        public string ProcessName => Info.ProcessName ?? "";
        public string Label => AppDisplayName.Get(Info);

        private ImageSource? _icon;
        /// <summary>应用图标（懒加载：列表构建时不提取，由 LoadIcon 后台填充后通知刷新，降低启动耗时与内存）。</summary>
        public ImageSource? Icon
        {
            get => _icon;
            private set
            {
                if (!ReferenceEquals(_icon, value))
                {
                    _icon = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
                }
            }
        }

        private bool _isAutoSwitchDisabled;
        private System.Windows.Media.Brush _dotBrush = System.Windows.Media.Brushes.Transparent;

        /// <summary>该应用是否禁用了自动切换为当前应用（影响自动检测，不影响手动选择）。</summary>
        public bool IsAutoSwitchDisabled
        {
            get => _isAutoSwitchDisabled;
            set
            {
                if (_isAutoSwitchDisabled != value)
                {
                    _isAutoSwitchDisabled = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAutoSwitchDisabled)));
                }
            }
        }

        /// <summary>图标右上角状态点颜色：禁用了自动切换时为主题色 RGB 反色，否则透明。</summary>
        public System.Windows.Media.Brush DotBrush
        {
            get => _dotBrush;
            set
            {
                if (!ReferenceEquals(_dotBrush, value))
                {
                    _dotBrush = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DotBrush)));
                }
            }
        }

        /// <summary>按当前配置与主题刷新"禁用自动切换"状态（应用列表刷新 / 切换主题强调色时调用）。</summary>
        public void RefreshAutoSwitchState()
        {
            bool disabled = !string.IsNullOrWhiteSpace(ProcessName) &&
                            ConfigService.Load().DisabledAutoSwitchApps.Contains(ProcessName);
            IsAutoSwitchDisabled = disabled;
            DotBrush = disabled ? ThemeService.GetInvertedAccentBrush() : System.Windows.Media.Brushes.Transparent;
        }

        public override string ToString() => Label;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>应用名被（即时）重命名后刷新列表项显示，不重建 ItemsSource（避免输入法中断）。</summary>
        public void RefreshName() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));

        public static AppItem From(AudioAppInfo a) => new() { Info = a };

        /// <summary>后台线程提取图标，完成后切回 UI 线程通知绑定刷新（列表构建后批量调用）。</summary>
        public void LoadIcon()
        {
            if (Icon != null) return;
            var icon = AppIconService.GetIconForPid(ProcessId);
            if (icon == null) return;
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
                disp.Invoke(() => Icon = icon);
            else
                Icon = icon;
        }

        /// <summary>批量后台加载图标（不阻塞调用线程）。</summary>
        public static void LoadIconsAsync(IEnumerable<AppItem> items)
        {
            var list = items.Where(i => i.Icon == null).ToList();
            if (list.Count == 0) return;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var item in list) item.LoadIcon();
            });
        }
    }
}

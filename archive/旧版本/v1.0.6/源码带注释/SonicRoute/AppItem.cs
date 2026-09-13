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
        public ImageSource? Icon { get; init; }

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

        public static AppItem From(AudioAppInfo a) => new()
        {
            Info = a,
            Icon = AppIconService.GetIconForPid((int)a.ProcessId)
        };
    }
}

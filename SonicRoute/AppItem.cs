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

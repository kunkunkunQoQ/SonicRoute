using System.Windows.Media;
using SonicRoute.Core.Models;

namespace SonicRoute
{
    /// <summary>带图标的应用列表项（UI 视图模型，供应用切换器 / 应用列表）。</summary>
    public sealed class AppItem
    {
        public required AudioAppInfo Info { get; init; }
        public int ProcessId => (int)Info.ProcessId;
        public string ProcessName => Info.ProcessName ?? "";
        public string Label => Info.Label;
        public ImageSource? Icon { get; init; }

        public override string ToString() => Label;

        public static AppItem From(AudioAppInfo a) => new()
        {
            Info = a,
            Icon = AppIconService.GetIconForPid((int)a.ProcessId)
        };
    }
}

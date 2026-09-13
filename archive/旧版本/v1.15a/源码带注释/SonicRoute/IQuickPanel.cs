using System.Threading.Tasks;

namespace SonicRoute
{
    /// <summary>
    /// 快速面板统一接口：经典面板（QuickPanelWindow）与简洁面板（QuickPanelModernWindow）
    /// 共用同一交互契约，App 按配置（QuickPanelStyle）选择实例，快捷键/托盘统一调用。
    /// </summary>
    public interface IQuickPanel
    {
        bool IsVisible { get; }
        event EventHandler? Closed;
        void Close();
        void ShowQuickPanel();
        Task<int> AdjustVolumeAsync(int delta);
        Task<bool> MuteCurrentAppAsync();
        Task<bool> ToggleGlobalMicMuteAsync();
    }
}

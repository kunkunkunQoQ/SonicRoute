using System;
using System.Collections.Generic;

namespace SonicRoute
{
    /// <summary>
    /// 宿主服务契约（阶段 0：App 直接实现，无 IPC、无新进程；阶段 1 起由后台进程实现，UI 经 IPC 代理调用）。
    /// 覆盖 UI 层（完整界面 / 经典与简洁快速面板）需要的全部宿主能力：
    /// OSD 显示与调整、快速面板位置调整、托盘右键菜单重建、全局快捷键重载/注册状态查询、完整界面打开。
    /// </summary>
    public interface IHostServices
    {
        /// <summary>右上角 OSD 提示（托盘滚轮 / 快捷键 / 设置提示共用）。</summary>
        void ShowOsd(string app, string text);

        /// <summary>麦克风静音状态 OSD 统一入口：静音且常驻开关开启 → 常驻显示。</summary>
        void ShowMicMuteOsd(string app, bool muted);

        /// <summary>设置页「麦克风静音时 OSD 常驻」开关变化：立即生效（开启且已静音 → 常驻；关闭 → 退出常驻）。</summary>
        void NotifyMicMuteOsdSettingChanged(bool on);

        /// <summary>进入 OSD 调整模式（主题页「调整位置」）。</summary>
        void BeginOsdAdjust();

        /// <summary>取消 OSD 调整（不保存）。</summary>
        void CancelOsdAdjust();

        /// <summary>实时位置预览（偏移滑块 / 坐标输入联动 / 一键还原）。</summary>
        void PreviewOsd();

        /// <summary>应用 OSD 尺寸（实际生效）。</summary>
        void ApplyOsdSize(double w, double fs);

        /// <summary>设置 OSD 尺寸并保存（主题页滑条实时调整）。</summary>
        void SetOsdSize(int w, double fs);

        /// <summary>进入快速面板位置调整模式（打开面板并进入拖拽定位，松手即保存）。</summary>
        void BeginQuickPanelAdjust();

        /// <summary>取消快速面板位置调整（不保存，关闭面板）。</summary>
        void CancelQuickPanelAdjust();

        /// <summary>一键还原快速面板位置：恢复任务栏右下角默认位置，已打开则立即重定位。</summary>
        void ResetQuickPanelPosition();

        /// <summary>重建托盘右键菜单（语言切换 / 语言导入等即时生效时调用）。</summary>
        void RebuildTrayMenu();

        /// <summary>重新加载全局快捷键（设置页修改后调用）。实验模式的隐藏动作仅在「实验模式 + 麦克风选项」开启时注册。</summary>
        void ReloadHotkeys();

        /// <summary>快捷键实际注册状态：动作 → 生效组合（设置页显示占用冲突）。</summary>
        IReadOnlyDictionary<string, string> HotkeyRegistration { get; }

        /// <summary>打开完整管理界面（面板「设置」按钮 / 托盘双击 / 二次启动等）。</summary>
        void ShowMainWindow();

        /// <summary>OSD 拖拽保存后通知（设置页复位按钮 / 同步输入框）。</summary>
        event Action? OsdAdjustFinished;

        /// <summary>快速面板拖拽保存后通知（主题页复位按钮）。</summary>
        event Action? QuickPanelAdjustFinished;

        /// <summary>供面板窗口在拖拽保存 / 关闭时触发（外部类不能直接 Invoke 事件）。</summary>
        void NotifyQuickPanelAdjustFinished();
    }
}

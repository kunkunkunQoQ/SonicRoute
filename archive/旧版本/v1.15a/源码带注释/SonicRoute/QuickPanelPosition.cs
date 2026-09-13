using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using SonicRoute.Core;

namespace SonicRoute
{
    /// <summary>
    /// 快速面板定位共享逻辑：经典面板（QuickPanelWindow）与简洁面板（QuickPanelModernWindow）共用，
    /// 保证两版定位行为一致。
    /// 坐标统一 WPF DIP：SystemParameters.WorkArea 直接用（不再除 DPI）；
    /// GetSystemMetrics(76-79) 虚拟屏幕为物理像素，仅在拖拽边界与保存时 ÷ GetDpi().DpiScaleX 换算为 DIP。
    /// </summary>
    internal static class QuickPanelPosition
    {
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        /// <summary>按配置定位面板：Custom 直接使用保存坐标（保持用户设定，不做任何改写）；
        /// 默认锚定主显示器工作区右下角。调用前应保证 w.ActualWidth/ActualHeight 已是最终尺寸。</summary>
        public static void Apply(Window w, AppConfig cfg)
        {
            if (cfg.QuickPanelPosMode == "custom" && cfg.QuickPanelCustomX >= 0 && cfg.QuickPanelCustomY >= 0)
            {
                w.Left = cfg.QuickPanelCustomX;
                w.Top = cfg.QuickPanelCustomY;
                return;
            }
            var work = SystemParameters.WorkArea;
            w.Left = work.Right - w.ActualWidth - 12;
            double top = work.Bottom - w.ActualHeight - 10;
            if (top < work.Top) top = work.Top + 6; // 屏幕过矮时避免顶部越界
            w.Top = top;
        }

        /// <summary>拖拽边界：虚拟屏幕（所有显示器）物理像素 ÷ DPI → DIP，限制面板不能拖出全部显示器范围。</summary>
        public static (double MinX, double MaxX, double MinY, double MaxY) DragBounds(Window w)
        {
            double ws = 1.0;
            try { ws = VisualTreeHelper.GetDpi(w).DpiScaleX; } catch { }
            double vsX = GetSystemMetrics(76), vsY = GetSystemMetrics(77);
            double vsW = GetSystemMetrics(78), vsH = GetSystemMetrics(79);
            return (vsX / ws, (vsX + vsW) / ws - w.ActualWidth, vsY / ws, (vsY + vsH) / ws - w.ActualHeight);
        }

        /// <summary>拖动松手：把当前窗口位置写入配置（Custom 模式），Clamp 在虚拟屏幕范围内并保存。</summary>
        public static void Save(Window w, AppConfig cfg)
        {
            var (minX, maxX, minY, maxY) = DragBounds(w);
            cfg.QuickPanelPosMode = "custom";
            cfg.QuickPanelCustomX = (int)Math.Clamp(w.Left, minX, Math.Max(minX, maxX));
            cfg.QuickPanelCustomY = (int)Math.Clamp(w.Top, minY, Math.Max(minY, maxY));
            ConfigService.Save(cfg);
        }
    }
}

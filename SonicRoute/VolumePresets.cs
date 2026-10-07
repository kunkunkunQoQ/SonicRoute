using System;
using System.Windows.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace SonicRoute
{
    /// <summary>所有音量滑块共用档位菜单；写入继续走原来的 ValueChanged 流程。</summary>
    internal static class VolumePresets
    {
        internal static void Attach(Slider slider)
        {
            var menu = ConvenienceMenus.Create();
            slider.ContextMenuOpening += (_, _) =>
            {
                // 仅第一次右键时创建菜单项，列表加载不为每行预建四个控件。
                if (menu.Items.Count != 0) return;
                foreach (int percent in new[] { 25, 50, 75, 100 })
                {
                    var item = ConvenienceMenus.Item(percent + "%");
                    item.IsCheckable = true;
                    item.Tag = percent;
                    item.Click += (_, _) => { if (slider.IsEnabled) slider.Value = percent; };
                    menu.Items.Add(item);
                }
            };
            menu.Opened += (_, _) =>
            {
                foreach (MenuItem item in menu.Items)
                {
                    item.IsEnabled = slider.IsEnabled;
                    item.IsChecked = Math.Abs(slider.Value - (int)item.Tag) < 0.5;
                }
            };
            slider.ContextMenu = menu;
        }
    }
}

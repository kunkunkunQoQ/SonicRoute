using System;
using System.Windows.Controls;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace SonicRoute
{
    /// <summary>所有音量滑块共用档位菜单；写入继续走原来的 ValueChanged 流程。</summary>
    internal static class VolumePresets
    {
        private static readonly int[] Percentages = { 25, 50, 75, 100 };
        // WPF 在 Opening 实例事件前寻找非空菜单目标；各滑块可共享未打开的空占位。
        // 弱引用不在关窗后保留菜单/控件，线程局部避免跨 Dispatcher 使用 WPF 对象。
        [ThreadStatic] private static WeakReference<ContextMenu>? _placeholder;

        internal static void Populate(ItemCollection items, Action<int> apply, bool staysOpenOnClick = false)
        {
            foreach (int percent in Percentages)
            {
                var item = ConvenienceMenus.Item(percent + "%");
                item.IsCheckable = true;
                item.StaysOpenOnClick = staysOpenOnClick;
                item.Tag = percent;
                item.Click += (_, _) => apply(percent);
                items.Add(item);
            }
        }

        internal static void Update(ItemCollection items, double current, bool enabled)
        {
            foreach (MenuItem item in items)
            {
                item.IsEnabled = enabled;
                item.IsChecked = Math.Abs(current - (int)item.Tag) < 0.5;
            }
        }

        internal static void Attach(Slider slider)
        {
            if (_placeholder == null || !_placeholder.TryGetTarget(out var placeholder))
            {
                placeholder = new ContextMenu();
                _placeholder = new WeakReference<ContextMenu>(placeholder);
            }
            slider.ContextMenu = placeholder;
            slider.ContextMenuOpening += CreateMenuOnOpening;
        }

        private static void CreateMenuOnOpening(object sender, ContextMenuEventArgs e)
        {
            if (e.Handled || sender is not Slider slider || _placeholder == null ||
                !_placeholder.TryGetTarget(out var placeholder) ||
                !ReferenceEquals(slider.ContextMenu, placeholder)) return;

            // 连菜单本身也按需创建；首次打开后交还 WPF，后续仅刷新档位状态。
            var menu = ConvenienceMenus.Create();
            Populate(menu.Items, percent => { if (slider.IsEnabled) slider.Value = percent; });
            menu.Opened += (_, _) =>
            {
                Update(menu.Items, slider.Value, slider.IsEnabled);
            };
            slider.ContextMenu = menu;
            slider.ContextMenuOpening -= CreateMenuOnOpening;
        }
    }
}

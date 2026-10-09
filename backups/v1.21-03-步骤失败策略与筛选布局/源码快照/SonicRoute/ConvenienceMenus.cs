using System.Windows;
using System.Windows.Controls;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace SonicRoute
{
    internal static class ConvenienceMenus
    {
        internal static ContextMenu Create()
        {
            var menu = new ContextMenu();
            menu.SetResourceReference(FrameworkElement.StyleProperty, "ConvenienceMenu");
            return menu;
        }

        internal static MenuItem Item(string header)
        {
            var item = new MenuItem { Header = header };
            item.SetResourceReference(FrameworkElement.StyleProperty, "ConvenienceMenuItem");
            return item;
        }
    }
}

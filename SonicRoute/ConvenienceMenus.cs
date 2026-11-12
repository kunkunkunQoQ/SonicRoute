using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
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
            item.SubmenuOpened += SubmenuOpened;
            item.SizeChanged += SubmenuAnchorSizeChanged;
            return item;
        }

        private static void SubmenuOpened(object sender, RoutedEventArgs args)
        {
            if (ReferenceEquals(sender, args.OriginalSource)) RefreshSubmenuPlacement((MenuItem)sender);
        }

        private static void SubmenuAnchorSizeChanged(object sender, SizeChangedEventArgs args)
            => RefreshSubmenuPlacement((MenuItem)sender);

        private static void RefreshSubmenuPlacement(MenuItem item)
        {
            if (!item.IsSubmenuOpen) return;
            // A device read may widen the parent after its popup is open. At the right
            // screen edge the parent moves left; wait for that layout before reanchoring.
            item.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (item.IsSubmenuOpen && item.Template?.FindName("PART_Popup", item) is Popup popup
                    && popup.IsOpen && popup.PlacementTarget is FrameworkElement anchor)
                    popup.PlacementRectangle = new Rect(anchor.RenderSize);
            }));
        }
    }
}

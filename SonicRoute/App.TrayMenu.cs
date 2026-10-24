using System;
using System.Runtime.InteropServices;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using SonicRoute.Core;

namespace SonicRoute
{
    public partial class App
    {
        private ContextMenu? _trayMenu;
        private HwndSource? _trayMenuHost;
        private WindowUiLifetime? _trayMenuLifetime;

        private void ShowTrayMenu()
        {
            if (_trayIcon == null || Dispatcher.HasShutdownStarted) return;
            int foregroundPid = ForegroundAppService.GetForegroundProcessId();
            CloseTrayMenu();
            CancelUiReclaim();
            _uiWorkingSetDirty = true;
            try
            {
                var menu = CreateTrayMenu(foregroundPid);

                // 托盘菜单需要前台宿主才能正常失焦关闭。仅在菜单打开时创建原生零尺寸窗口，
                // 不创建隐藏 WPF Window，不保留常驻视觉树或轮询。
                var cursor = System.Windows.Forms.Cursor.Position;
                _trayMenuHost = new HwndSource(new HwndSourceParameters("SonicRoute.TrayMenu")
                {
                    Width = 0, Height = 0, PositionX = cursor.X, PositionY = cursor.Y,
                    WindowStyle = unchecked((int)0x90000000), // WS_POPUP | WS_VISIBLE
                    ExtendedWindowStyle = 0x80 // WS_EX_TOOLWINDOW
                });
                _trayMenuHost.AddHook(TrayMenuHostHook);
                SetForegroundWindow(_trayMenuHost.Handle);
                menu.Placement = PlacementMode.MousePoint;
                menu.Opened += (_, _) => menu.Focus();
                menu.PreviewKeyDown += (_, args) =>
                {
                    // 原生宿主没有 WPF Window；明确保留 Esc 的逐级关闭行为。
                    if (args.Key != System.Windows.Input.Key.Escape) return;
                    var submenu = DeepestOpenSubmenu(menu);
                    if (submenu != null) submenu.IsSubmenuOpen = false;
                    else menu.IsOpen = false;
                    args.Handled = true;
                };
                menu.Closed += (_, _) =>
                {
                    if (_trayMenu == menu) CloseTrayMenu();
                };
                menu.IsOpen = true;
            }
            catch { CloseTrayMenu(); }
        }

        private ContextMenu CreateTrayMenu(int foregroundPid)
        {
            if (_trayMenu != null || _trayMenuLifetime != null) CloseTrayMenu();
            var menu = ConvenienceMenus.Create();
            _trayMenuLifetime = new WindowUiLifetime();
            _trayMenu = menu;
            AddTrayAction(menu, L10n.T("Tray.OpenPanel"), () =>
            {
                if (_quickPanel is { IsVisible: true } panel) panel.Activate();
                else ToggleQuickPanel();
            });
            AddTrayAction(menu, L10n.T("Tray.OpenMain"), ShowMainWindow);
            if (Updates.ShouldNotify)
                AddTrayAction(menu, string.Format(L10n.T("Upd.Notice"), Updates.Release!.Tag), ShowUpdateSettings);
            AddTraySeparator(menu.Items);
            AddTrayAudioItems(menu, foregroundPid);
            menu.Items.Add(BuildFavoriteRulesMenu(menu));
            AddTraySeparator(menu.Items);
            AddTrayWheelPause(menu);
            AddTrayAction(menu, L10n.T("Tray.Exit"), Quit);
            return menu;
        }

        private static MenuItem? DeepestOpenSubmenu(ItemsControl parent)
        {
            var open = parent.Items.OfType<MenuItem>().FirstOrDefault(item => item.IsSubmenuOpen);
            return open == null ? null : DeepestOpenSubmenu(open) ?? open;
        }

        private static void AddTraySeparator(ItemCollection items)
        {
            var separator = new Separator();
            separator.SetResourceReference(Separator.StyleProperty, "ConvenienceMenuSeparator");
            items.Add(separator);
        }

        private static void AddTrayAction(ContextMenu menu, string text, Action action)
        {
            var item = ConvenienceMenus.Item(text);
            item.Click += (_, _) => { menu.IsOpen = false; action(); };
            menu.Items.Add(item);
        }

        private IntPtr TrayMenuHostHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x001C && wParam == IntPtr.Zero && _trayMenuHost?.Handle == hwnd)
            {
                // 旧宿主的延迟失焦消息不能关闭随后打开的新菜单。
                var host = _trayMenuHost;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(_trayMenuHost, host)) CloseTrayMenu();
                }));
            }
            return IntPtr.Zero;
        }

        private void CloseTrayMenu()
        {
            var menu = _trayMenu;
            var host = _trayMenuHost;
            _trayMenu = null;
            _trayMenuHost = null;
            var lifetime = _trayMenuLifetime;
            _trayMenuLifetime = null;
            ++_favoriteMenuRequest; // 丢弃已关闭菜单的异步规则读取结果。
            ++_trayAudioRequest;
            lifetime?.Dispose();
            if (menu != null) menu.IsOpen = false;
            if (host != null)
            {
                PostMessage(host.Handle, 0, IntPtr.Zero, IntPtr.Zero);
                host.Dispose();
                if (_trayIcon != null && !Dispatcher.HasShutdownStarted) ScheduleUiReclaim();
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}

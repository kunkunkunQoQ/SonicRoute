using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using SonicRoute.Core;

namespace SonicRoute
{
    public partial class App
    {
        private int _favoriteMenuRequest;

        private ToolStripMenuItem BuildFavoriteRulesMenu(ContextMenuStrip owner)
        {
            var favorites = new ToolStripMenuItem(L10n.T("Auto.Favorites"));
            favorites.DropDownItems.Add(L10n.T("Auto.FavoritesEmpty")).Enabled = false;
            owner.Opening += async (_, _) =>
            {
                int request = ++_favoriteMenuRequest;
                try
                {
                    var ids = new List<string>(ConfigService.Load().FavoriteRuleIds ?? new List<string>());
                    var rules = await Task.Run(() => ids.Distinct(StringComparer.Ordinal).Select(AutoRuleStore.Find)
                        .Where(rule => rule != null).Select(rule => rule!).ToList());
                    if (request != _favoriteMenuRequest || owner.IsDisposed || _trayIcon?.ContextMenuStrip != owner || Dispatcher.HasShutdownStarted) return;
                    var byId = rules.ToDictionary(rule => rule.Id, StringComparer.Ordinal);
                    foreach (ToolStripItem item in favorites.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
                    favorites.DropDownItems.Clear();
                    foreach (string id in ids.Distinct(StringComparer.Ordinal))
                    {
                        if (!byId.TryGetValue(id, out var rule)) continue;
                        var entry = new ToolStripMenuItem(rule.Name.Replace("&", "&&")) { Enabled = !AutoRuleService.IsRunning(id) };
                        entry.Click += async (_, _) => await RunFavoriteRuleAsync(id);
                        favorites.DropDownItems.Add(entry);
                    }
                    if (favorites.DropDownItems.Count == 0) favorites.DropDownItems.Add(L10n.T("Auto.FavoritesEmpty")).Enabled = false;
                }
                catch { /* 菜单仍可用于打开设置和退出。 */ }
            };
            return favorites;
        }

        private async Task RunFavoriteRuleAsync(string id)
        {
            try
            {
                var rule = await Task.Run(() => AutoRuleStore.Find(id));
                if (rule == null) { ShowOsd(L10n.T("Auto.Favorites"), L10n.T("Auto.MissingRule")); return; }
                var result = await AutoRuleService.ExecuteWithResultAsync(rule);
                if (!Dispatcher.HasShutdownStarted) ShowOsd(rule.Name, AutoRuleService.DescribeResult(result));
            }
            catch { if (!Dispatcher.HasShutdownStarted) ShowOsd(L10n.T("Auto.Favorites"), L10n.T("Auto.RunFailed")); }
        }
    }
}

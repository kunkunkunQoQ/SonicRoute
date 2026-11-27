using System;
using System.Globalization;
using SonicRoute.Core.Models;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed partial class AppsPageView
{
    private const string DockToolsId = "apps.tools";
    private const string DockItemPrefix = "apps.item.";
    private const string DockResultPrefix = "apps.result.";
    private const float DockResultHeight = 42f;
    private const int DockMaxResultRows = 12;
    private readonly UiScrollState _dockResultsScroll = new();

    private UiRect _dockToolsRect;
    private UiRect _dockResultsRect;
    private bool _dockToolsOpen;

    public UiDockRail Dock { get; }
    public bool NeedsAnimationTick => Dock.NeedsAnimationTick;
    public void TickAnimation() => Dock.TickAnimation();
    private bool ResetDockHover() => Dock.ResetHover();
    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _hit = hit;
        _clicks.Clear();
        _appRows.Clear();
        RenderCanvasWorkspace(g, t, hit, contentRect);
    }

    public void RenderDock(D2dTarget g, UiThemePalette t, UiHitMap hit, float navEdge)
    {
        var toolsButton = new UiRect(navEdge + UiDockRail.PivotInset - UiDockRail.CardSize * 0.5f, _canvasRect.Y + 2f,
            UiDockRail.CardSize, UiDockRail.CardSize);
        DrawDockSearchButton(g, t, hit, toolsButton, navEdge);

        float listTop = toolsButton.Bottom + 5.5f;
        Dock.Render(g, t, hit, new UiRect(navEdge, listTop, _canvasRect.Right - navEdge,
            MathF.Max(0f, _canvasRect.Bottom - listTop)), _filteredApps.Count, GetDockItem, allowExpansion: !_dockToolsOpen);
        if (_dockToolsOpen)
        {
            float x = toolsButton.Right + 6f;
            float width = MathF.Min(Math.Clamp(_canvasRect.Width * 0.46f, 255f, 326f), _canvasRect.Right - x);
            RenderDockTools(g, t, hit, new UiRect(x, toolsButton.Y, width, _canvasRect.Bottom - toolsButton.Y - 4f));
        }
    }

    private void DrawDockSearchButton(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect plate, float navEdge)
    {
        var visible = new UiRect(navEdge, plate.Y, plate.Right - navEdge + 1f, plate.Height + 2f);
        g.PushClip(visible);
        UiDockRail.DrawPlate(g, t, plate, _dockToolsOpen, _dockToolsOpen, hit.IsHoverOrActive(DockToolsId));
        bool filtered = _filterMode != AppFilterMode.All || !string.IsNullOrWhiteSpace(_searchQuery);
        const float searchSide = 20f;
        float x = plate.CenterX + UiDockRail.IconInset - searchSide * 0.5f;
        g.DrawIcon(UiIcon.Search, new UiRect(x, plate.CenterY - searchSide * 0.5f, searchSide, searchSide),
            filtered || _dockToolsOpen ? t.Accent : t.TextSecondary, 2.2f);
        g.PopClip();
        hit.Add(DockToolsId, visible.Deflate(0f, 0f, 1f, 2f), tooltip: L10n.T("Phone.SearchAppsHint"));
        _clicks[DockToolsId] = ToggleDockTools;
    }

    private UiDockRail.Item GetDockItem(int index)
    {
        var app = _filteredApps[index];
        string id = DockItemPrefix + app.Identity;
        string label = GetAppDisplayLabel(app);
        bool primary = _selectedIdentity == app.Identity;
        _appRows[id] = app;
        return new UiDockRail.Item(id, label, ResolveAppIcon(app, label), primary,
            primary || _multiSelected.Contains(app.Identity), app.IsExternalOffline);
    }

    private static string AppSubtitle(AudioAppInfo app) =>
        app.IsExternal ? app.SourceLabel ?? "" : app.ProcessName ?? $"PID {app.ProcessId}";

    public void OnFocusLost()
    {
        if (ResetDockHover()) _actions.Invalidate();
    }
    private void RenderDockTools(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect available)
    {
        const float padX = 11f, padY = 9f;
        var inner = available.Deflate(padX, padY);
        _searchBoxRect = new UiRect(inner.X, inner.Y + 33f, inner.Width, 29f);
        _filterBtnRect = new UiRect(inner.Right - 56f, inner.Y, 25f, 25f);
        var statusRect = new UiRect(inner.X + 2f, _searchBoxRect.Bottom + 7f, inner.Width - 4f, 18f);
        float listTop = statusRect.Bottom + 4f;
        int availableRows = Math.Clamp((int)((inner.Bottom - listTop) / DockResultHeight), 0, DockMaxResultRows);
        float listHeight = Math.Min(_filteredApps.Count, availableRows) * DockResultHeight;

        var rect = available with { Height = listTop - available.Y + listHeight + padY };
        _dockToolsRect = rect;
        g.FillRoundedRect(rect.Offset(0f, 3f), 12f, UiColor.Black.WithAlpha(t.IsDark ? (byte)72 : (byte)24));
        g.FillRoundedRect(rect, 12f, t.SurfaceBg);
        g.DrawRoundedRect(rect, 12f, t.Border, 1f);
        hit.Add("apps.tools.panel", rect, UiCursorKind.Arrow);
        g.DrawText(L10n.T("Apps.Title"), new UiRect(inner.X + 2f, inner.Y, inner.Width - 87f, 25f), t.TextPrimary, 13f, 600);
        UiPrimitives.DrawGhostIconButton(g, t, hit, "apps.refresh", _filterBtnRect.Offset(-29f, 0f), UiIcon.Refresh, 14f,
            tooltip: L10n.T("Apps.Refresh"));
        _clicks["apps.refresh"] = () => TriggerRefresh(forceAppsRefresh: true);
        bool filterActive = _filterMode != AppFilterMode.All;
        UiPrimitives.DrawGhostIconButton(g, t, hit, "apps.filter", _filterBtnRect, UiIcon.Filter, 14f,
            iconColor: filterActive ? t.AccentIconInk : null, active: filterActive, tooltip: L10n.T("Apps.Filter.Tooltip"));
        _clicks["apps.filter"] = OpenFilterMenu;
        UiPrimitives.DrawGhostIconButton(g, t, hit, "apps.tools.close", _filterBtnRect.Offset(29f, 0f), UiIcon.Close, 11f,
            tooltip: L10n.T("Window.Close"));
        _clicks["apps.tools.close"] = CloseDockTools;

        bool searchFocused = _actions.ActiveInlineEditTag == "apps.search";
        UiPrimitives.DrawTextBox(g, t, hit, "apps.search", _searchBoxRect, _searchQuery,
            placeholder: L10n.T("Phone.SearchAppsHint"), focused: searchFocused, fontSize: 12f, search: true);
        _clicks["apps.search"] = BeginSearchEdit;
        if (!searchFocused && _searchQuery.Length > 0)
        {
            var clear = new UiRect(_searchBoxRect.Right - 25f, _searchBoxRect.CenterY - 10f, 21f, 20f);
            UiPrimitives.DrawGhostIconButton(g, t, hit, "apps.search.clear", clear, UiIcon.Close, 10f,
                tooltip: L10n.T("Auto.ResetFilters"));
            _clicks["apps.search.clear"] = () => SetDockSearch("");
        }

        string count = _filteredApps.Count.ToString(CultureInfo.CurrentCulture);
        if (_filteredApps.Count != _apps.Count) count += " / " + _apps.Count.ToString(CultureInfo.CurrentCulture);
        string status = _apps.Count == 0 ? L10n.T("Apps.Canvas.NoApps")
            : _filteredApps.Count == 0 ? L10n.T("Apps.Dock.NoMatches") : GetFilterLabel(_filterMode);
        g.DrawText(status, statusRect.Deflate(0f, 0f, 55f, 0f), t.TextSecondary, 11f);
        if (_apps.Count != 0)
            g.DrawText(count, statusRect, t.TextSecondary, 11f, 500, UiTextAlign.Trailing, UiVertAlign.Center);
        DrawDockResults(g, t, hit, new UiRect(inner.X, listTop, inner.Width, listHeight));
    }

    private void DrawDockResults(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect list)
    {
        const float scrollRightInset = 3f;
        var scrollViewport = list with { Width = _dockToolsRect.Right - list.X - scrollRightInset };
        _dockResultsRect = scrollViewport;
        _dockResultsScroll.ViewportHeight = list.Height;
        _dockResultsScroll.ContentHeight = _filteredApps.Count * DockResultHeight;
        _dockResultsScroll.Clamp();
        g.DrawLine(list.X + 2f, list.Y - 2f, list.Right - 2f, list.Y - 2f, t.Border, 1f, roundCaps: false);

        float width = _dockResultsScroll.MaxOffset > 0.5f ? scrollViewport.Width - UiMetrics.ScrollBarWidth - 2f : list.Width;
        int first = (int)MathF.Floor(_dockResultsScroll.Offset / DockResultHeight);
        int last = Math.Min(_filteredApps.Count,
            (int)MathF.Ceiling((_dockResultsScroll.Offset + list.Height) / DockResultHeight));
        g.PushClip(list);
        hit.PushClip(list);
        for (int i = first; i < last; i++)
        {
            var app = _filteredApps[i];
            string id = DockResultPrefix + app.Identity;
            bool selected = _selectedIdentity == app.Identity || _multiSelected.Contains(app.Identity);
            float y = list.Y + i * DockResultHeight - _dockResultsScroll.Offset;
            var row = new UiRect(list.X, y + 1f, width, DockResultHeight - 2f);
            hit.Add(id, row);
            _appRows[id] = app;
            if (selected || hit.IsHover(id))
                g.FillRoundedRect(row, 7f, selected ? t.NavSelected : t.NavHover);
            DrawAppIcon(g, t, app, new UiRect(row.X + 8f, row.CenterY - 10f, 20f, 20f));
            var name = new UiRect(row.X + 38f, row.Y + 3f, row.Width - 62f, 18f);
            g.DrawText(GetAppDisplayLabel(app), name, app.IsExternalOffline ? t.TextSecondary : t.TextPrimary,
                12.5f, selected ? 600 : 400);
            g.DrawText(AppSubtitle(app), name.Offset(0f, 17f), t.TextSecondary, 10.5f);
        }
        hit.PopClip();
        g.PopClip();
        _dockResultsScroll.Draw(g, t, hit, "apps.results.scroll", scrollViewport);
    }

    private void ToggleDockTools()
    {
        if (_dockToolsOpen) CloseDockTools();
        else OpenDockTools();
    }

    private void OpenDockTools()
    {
        ResetDockHover();
        ClosePopups();
        _dockToolsOpen = true;
        _dockResultsScroll.Offset = 0f;
        _actions.Invalidate();
    }

    private void CloseDockTools()
    {
        if (!_dockToolsOpen) return;
        _dockToolsOpen = false;
        _dockResultsScroll.EndDrag();
        _dockResultsRect = default;
        if (_actions.ActiveInlineEditTag == "apps.search") _actions.CloseInlineEdit(commit: true);
        _actions.Invalidate();
    }
}

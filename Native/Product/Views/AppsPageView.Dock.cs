using System;
using System.Diagnostics;
using System.Globalization;
using SonicRoute.Core.Models;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed partial class AppsPageView
{
    private const string DockToolsId = "apps.tools";
    private const string DockItemPrefix = "apps.item.";
    private const string DockResultPrefix = "apps.result.";
    private const float DockRowHeight = 37f;
    private const float DockMinRowHeight = 32f;
    private const float DockCardSize = 29f;
    private const float DockCornerRadius = UiMetrics.NavItemRadius;
    private const float DockTileAngle = -12f * MathF.PI / 180f;
    private const float DockPivotInset = 8.5f;
    private const float DockSlide = 7f;
    private const float DockIconSide = 17f;
    private const float DockIconInset = 3.5f;
    private const float DockTitleInset = 29f;
    private const float DockTitlePadding = 10f;
    private const float DockMaxTitleWidth = 148f;
    private const float DockTitleFont = 12f;
    private const float DockResultHeight = 42f;
    private const int DockMaxResultRows = 12;
    private readonly UiScrollState _dockResultsScroll = new();

    private UiRect _dockListRect;
    private UiRect _dockToolsRect;
    private UiRect _dockResultsRect;
    private float _dockRowHeight = DockRowHeight;
    private bool _dockToolsOpen;
    private string? _dockHoverId;
    private string? _dockLiftedId;
    private string? _dockOverlayId;
    private DockCard _dockLiftedCard;
    private float _dockLiftedWidth;
    private float _dockExpansion;
    private float _dockFrom;
    private long _dockAnimationStarted;

    private readonly record struct DockCard(UiPoint Center, float Size, string Label, AppIconVisual Icon,
        bool Primary, bool Selected, bool Offline);

    public bool NeedsAnimationTick => _dockAnimationStarted != 0;
    internal UiRect DockOverlayBounds { get; private set; }
    internal string? DockOverlayFocusId => _dockOverlayId;

    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _hit = hit;
        _clicks.Clear();
        _appRows.Clear();
        DockOverlayBounds = default;
        _dockOverlayId = null;
        RenderCanvasWorkspace(g, t, hit, contentRect);
    }

    internal void RenderAppDock(D2dTarget g, UiThemePalette t, UiHitMap hit, float navEdge)
    {
        var toolsButton = new UiRect(navEdge + DockPivotInset - DockCardSize * 0.5f, _canvasRect.Y + 2f,
            DockCardSize, DockCardSize);
        DrawDockSearchButton(g, t, hit, toolsButton, navEdge);

        float listTop = toolsButton.Bottom + 4f;
        float availableHeight = MathF.Max(0f, _canvasRect.Bottom - listTop);
        int appCount = _filteredApps.Count;
        float rowHeight = Math.Clamp(MathF.Floor(availableHeight / Math.Max(1, appCount)), DockMinRowHeight, DockRowHeight);
        if (rowHeight != _dockRowHeight)
        {
            _appsScroll.Offset *= rowHeight / _dockRowHeight;
            _dockRowHeight = rowHeight;
        }
        float visibleRows = MathF.Min(appCount, MathF.Floor(availableHeight / rowHeight));
        _dockListRect = new UiRect(navEdge, listTop, 28f, visibleRows * rowHeight);
        RenderDockCards(g, t, hit, _canvasRect.Right);
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
        DrawDockPlate(g, t, plate, _dockToolsOpen, _dockToolsOpen, hit.IsHoverOrActive(DockToolsId));
        bool filtered = _filterMode != AppFilterMode.All || !string.IsNullOrWhiteSpace(_searchQuery);
        const float searchSide = 14f;
        float x = plate.CenterX + DockIconInset - searchSide * 0.5f;
        g.DrawIcon(UiIcon.Search, new UiRect(x, plate.CenterY - searchSide * 0.5f, searchSide, searchSide),
            filtered || _dockToolsOpen ? t.Accent : t.TextSecondary, 1.7f);
        g.PopClip();
        hit.Add(DockToolsId, visible.Deflate(0f, 0f, 1f, 2f), tooltip: L10n.T("Phone.SearchAppsHint"));
        _clicks[DockToolsId] = ToggleDockTools;
    }

    private void RenderDockCards(D2dTarget g, UiThemePalette t, UiHitMap hit, float right)
    {
        string? raisedId = _dockLiftedId ?? (!_dockToolsOpen && hit.ShowFocus ? hit.FocusId : null);
        float rowHeight = _dockRowHeight;
        float cardSize = DockCardSize + rowHeight - DockRowHeight;
        _appsScroll.ViewportHeight = _dockListRect.Height;
        _appsScroll.ContentHeight = _filteredApps.Count * rowHeight;
        _appsScroll.Clamp();

        int first = (int)MathF.Floor(_appsScroll.Offset / rowHeight);
        int next = Math.Min(_filteredApps.Count,
            (int)MathF.Ceiling((_appsScroll.Offset + _dockListRect.Height) / rowHeight));
        // The foreground navigation masks the part behind its edge, even with a translucent window theme.
        g.PushClip(_dockListRect);
        hit.PushClip(_dockListRect);
        for (int i = first; i < next; i++)
        {
            var app = _filteredApps[i];
            string id = DockItemPrefix + app.Identity;
            string label = GetAppDisplayLabel(app);
            float y = _dockListRect.Y + i * rowHeight - _appsScroll.Offset;
            var center = new UiPoint(_dockListRect.X + DockPivotInset, y + rowHeight * 0.5f);
            bool primary = _selectedIdentity == app.Identity;
            var card = new DockCard(center, cardSize, label, ResolveAppIcon(app, label), primary,
                primary || _multiSelected.Contains(app.Identity), app.IsExternalOffline);

            var compact = GetDockShape(card, 0f, cardSize);
            hit.AddRotated(id, compact);
            _appRows[id] = app;
            if (id == raisedId)
            {
                _dockOverlayId = id;
                _dockLiftedCard = card;
                // The overlay reuses this width throughout the transition.
                float titleWidth = g.Shared.MeasureText(label, UiMetrics.FontFamily, DockTitleFont, 600,
                    DockMaxTitleWidth, cardSize, wrap: false).Width;
                float expandedLeft = center.X + DockSlide - cardSize * 0.5f;
                float availableTitleWidth = right - expandedLeft - DockTitleInset - DockTitlePadding - 3f;
                float maxTitleWidth = Math.Clamp(availableTitleWidth, 17f, DockMaxTitleWidth);
                _dockLiftedWidth = DockTitleInset + DockTitlePadding + Math.Clamp(MathF.Ceiling(titleWidth), 17f, maxTitleWidth);
                float edge = MathF.Min(right, expandedLeft + _dockLiftedWidth + 3f);
                DockOverlayBounds = UiHitMap.Intersect(new UiRect(_dockListRect.X, y, edge - _dockListRect.X,
                    rowHeight), new UiRect(_dockListRect.X, _dockListRect.Y, edge - _dockListRect.X,
                    _dockListRect.Height));
            }
            else
            {
                DrawDockCard(g, t, card, compact, 0f, cardSize);
            }
        }
        hit.PopClip();
        g.PopClip();
        if (_appsScroll.MaxOffset > 0f) hit.RegisterScroll(_appsScroll, _dockListRect);
        if (DockOverlayBounds.IsEmpty && _dockLiftedId != null) ResetDockHover();
    }

    private static string AppSubtitle(AudioAppInfo app) =>
        app.IsExternal ? app.SourceLabel ?? "" : app.ProcessName ?? $"PID {app.ProcessId}";

    internal void RenderDockOverlay(D2dTarget g, UiThemePalette t)
    {
        var bounds = DockOverlayBounds;
        if (bounds.IsEmpty || _dockOverlayId == null) return;
        var card = _dockLiftedCard;
        g.PushClip(bounds);
        float expansion = _dockOverlayId == _dockLiftedId ? _dockExpansion : 1f;
        var shape = GetDockShape(card, expansion, _dockLiftedWidth);
        DrawDockCard(g, t, card, shape, expansion, _dockLiftedWidth);
        if (_hit != null)
        {
            _hit.SetRotatedOverlay(_dockOverlayId, shape, bounds);
            if (_hit.ShowFocus && _hit.FocusId == _dockOverlayId)
            {
                var ring = shape.Rect.Deflate(2f);
                g.DrawRoundedRect(ring, 6f, UiColor.White, 2f);
                g.DrawRoundedRect(ring, 6f, UiColor.Black, 1f);
            }
        }
        g.PopClip();
    }

    private static UiRotatedRect GetDockShape(DockCard card, float expansion, float maxWidth)
    {
        float remaining = 1f - expansion;
        float stretch = Math.Clamp((expansion - 0.24f) / 0.76f, 0f, 1f);
        var pivot = card.Center with { X = card.Center.X + DockSlide * expansion };
        float size = card.Size;
        return new UiRotatedRect(new UiRect(pivot.X - size * 0.5f, pivot.Y - size * 0.5f,
            size + (maxWidth - size) * stretch * stretch, size),
            pivot, DockTileAngle * remaining * remaining, DockCornerRadius);
    }

    private static void DrawDockCard(D2dTarget g, UiThemePalette t, DockCard card, UiRotatedRect shape,
        float expansion, float maxWidth)
    {
        var rect = shape.Rect;
        if (shape.Radians != 0f) g.PushTransform(shape.Transform);
        DrawDockPlate(g, t, rect, card.Selected, card.Primary);
        if (shape.Radians != 0f) g.PopTransform();

        float iconX = shape.Pivot.X + DockIconInset * (1f - expansion);
        float iconSide = MathF.Min(DockIconSide, card.Size - 9f);
        card.Icon.Render(g, t, new UiRect(iconX - iconSide * 0.5f, rect.CenterY - iconSide * 0.5f, iconSide, iconSide));

        if (rect.Width > DockTitleInset + DockTitlePadding)
        {
            float opacity = Math.Clamp((expansion - 0.43f) / 0.48f, 0f, 1f);
            var textClip = rect.Deflate(DockTitleInset, 1f, DockTitlePadding, 1f);
            g.PushClip(textClip);
            var textRect = textClip with { Width = maxWidth - DockTitleInset - DockTitlePadding };
            g.DrawText(card.Label, textRect, (card.Offline ? t.TextSecondary : t.TextPrimary).WithAlphaFraction(opacity),
                DockTitleFont, 600);
            g.PopClip();
        }
    }

    private static void DrawDockPlate(D2dTarget g, UiThemePalette t, UiRect rect, bool selected, bool primary, bool hot = false)
    {
        var surface = selected ? t.NavSelected : hot ? t.NavHover : UiColor.Blend(t.SurfaceBg, t.SurfaceAlt, 0.45);
        var edge = primary ? UiColor.Blend(t.Border, t.Accent, 0.50) : t.Border;
        g.FillRoundedRect(rect.Offset(0f, 1.25f), DockCornerRadius, UiColor.Black.WithAlpha(t.IsDark ? (byte)40 : (byte)15));
        g.FillRoundedRect(rect, DockCornerRadius, surface);
        g.DrawRoundedRect(rect, DockCornerRadius, edge, 1f);
    }

    internal bool UpdateDockHover(string? target)
    {
        string? next = !_dockToolsOpen && target?.StartsWith(DockItemPrefix, StringComparison.Ordinal) == true
            ? target : null;
        if (next == _dockHoverId) return false;
        bool wasOpening = _dockHoverId == _dockLiftedId;
        _dockHoverId = next;
        bool allowAnimation = LayeredWindow.ClientAreaAnimationsEnabled();
        if (!allowAnimation || _dockLiftedId == null)
        {
            LiftDockCard(next, animate: allowAnimation && next != _dockOverlayId);
            return true;
        }
        bool opening = next == _dockLiftedId;
        if (!opening && _dockExpansion == 0f)
        {
            LiftDockCard(next, animate: true);
            return true;
        }
        if (opening == wasOpening && NeedsAnimationTick) return false;
        _dockFrom = _dockExpansion;
        _dockAnimationStarted = Stopwatch.GetTimestamp();
        return false;
    }

    public void TickAnimation()
    {
        bool opening = _dockLiftedId == _dockHoverId;
        float duration = opening ? 155f : 105f;
        float t = Math.Clamp((float)Stopwatch.GetElapsedTime(_dockAnimationStarted).TotalMilliseconds / duration, 0f, 1f);
        float eased = 1f - (1f - t) * (1f - t) * (1f - t);
        _dockExpansion = _dockFrom + ((opening ? 1f : 0f) - _dockFrom) * eased;
        if (t < 1f) return;
        _dockAnimationStarted = 0;
        if (opening) return;
        LiftDockCard(_dockHoverId, animate: true);
        _actions.Invalidate();
    }

    private void LiftDockCard(string? id, bool animate)
    {
        _dockLiftedId = id;
        _dockOverlayId = null;
        _dockExpansion = _dockFrom = id == null || animate ? 0f : 1f;
        _dockAnimationStarted = id != null && animate ? Stopwatch.GetTimestamp() : 0;
        _dockLiftedCard = default;
        DockOverlayBounds = default;
        _hit?.ClearOverlay();
    }

    private bool ResetDockHover()
    {
        if (_dockLiftedId == null) return false;
        _dockHoverId = null;
        LiftDockCard(null, animate: false);
        return true;
    }

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

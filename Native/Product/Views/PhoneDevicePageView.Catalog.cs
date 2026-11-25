#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed partial class PhoneDevicePageView
{
    private const string CatalogRow = "phone.catalog.row:";
    private bool _showCatalog, _catalogLoading;
    private float _detailOffset;
    private UiRect _visibleDeviceRect;
    private AndroidApp[] _filteredCatalog = [];
    private readonly HashSet<string> _selectedCatalog = new(StringComparer.Ordinal);
    private string? _catalogAnchor;

    private void CloseCatalog()
    {
        if (_showCatalog) _scroll.Offset = _detailOffset;
        _showCatalog = _catalogLoading = false;
        _selectedCatalog.Clear();
        _catalogAnchor = null;
        _catalogApps = [];
        _filteredCatalog = [];
        CancelCts(ref _catalogCts);
    }

    private void SetCatalogSearch(string text)
    {
        if (_appSearch == text) return;
        _appSearch = text;
        _scroll.Offset = 0;
        FilterCatalog();
        _actions.Invalidate();
    }

    private void FilterCatalog()
    {
        string query = _appSearch.Trim();
        _filteredCatalog = _catalogApps.Where(app => query.Length == 0 ||
            app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            app.Package.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _selectedCatalog.IntersectWith(_filteredCatalog.Select(app => app.Package));
    }

    private void RenderCatalog(D2dTarget g, UiThemePalette theme, UiRect bounds)
    {
        var card = bounds.Deflate(2f, 0f);
        UiPrimitives.DrawCard(g, theme, card, 10f);
        var inner = card.Deflate(12f);
        var done = new UiRect(inner.Right - 70f, inner.Y, 70f, 32f);
        UiPrimitives.DrawSecondaryButton(g, theme, _hit, "phone.catalog.done", done, L10n.T("Phone.DoneButton"));
        g.DrawText(L10n.T("Phone.AppPickerTitle"), new UiRect(inner.X, inner.Y, inner.Width - 80f, 32f), theme.TextPrimary, 18f, 600);
        var search = new UiRect(inner.X, done.Bottom + 8f, inner.Width, 34f);
        UiPrimitives.DrawTextBox(g, theme, _hit, "phone.appsearch", search, _appSearch, L10n.T("Phone.SearchAppsHint"),
            focused: _actions.ActiveInlineEditTag == "phone.appsearch", search: true);
        string status = L10n.T(_catalogLoading ? "Phone.LoadingPhoneApps" : _catalogApps.Count == 0 ? "Phone.CatalogEmpty" : "Phone.CatalogSelectionHint");
        float statusH = MathF.Max(16f, D2dSharedContext.Instance.MeasureText(status, UiMetrics.FontFamily, 11f, 400, inner.Width, 84f, true).Height);
        g.DrawText(status, new UiRect(inner.X, search.Bottom + 8f, inner.Width, statusH), theme.TextSecondary, 11f, wrap: true);
        float top = search.Bottom + statusH + 16f;
        var view = new UiRect(inner.X, top, inner.Width, MathF.Max(1f, inner.Bottom - top));
        const float rowH = 48f;
        _scroll.ContentHeight = rowH * _filteredCatalog.Length;
        _scroll.ViewportHeight = view.Height;
        _scroll.Clamp();
        int first = (int)(_scroll.Offset / rowH);
        int end = Math.Min(_filteredCatalog.Length, (int)MathF.Ceiling((_scroll.Offset + view.Height) / rowH));
        var retained = _actions.Host.Phone.Store.Profile.RetainedApps;
        g.PushClip(view);
        _hit.PushClip(view);
        for (int i = first; i < end; i++)
        {
            var app = _filteredCatalog[i];
            var row = new UiRect(view.X, top + i * rowH - _scroll.Offset, view.Width - (_scroll.MaxOffset > 0.5f ? UiMetrics.ScrollBarWidth + 4f : 0f), rowH);
            string id = CatalogRow + app.Package;
            _hit.Add(id, row, UiCursorKind.Hand);
            if (_selectedCatalog.Contains(app.Package)) g.FillRoundedRect(row, 6f, theme.NavSelected);
            else if (_hit.IsHover(id)) g.FillRoundedRect(row, 6f, theme.NavHover);
            var icon = new UiRect(row.X + 6f, row.CenterY - 14f, 28f, 28f);
            if (TryGetAppIcon(app, out var pixels, out int width, out int height, out int version))
                g.DrawBgraBitmap("phonepage.icon:" + app.Package, pixels, width, height, icon, version: version);
            bool pinned = retained.Any(item => item.Package == app.Package);
            var pin = new UiRect(row.Right - 34f, row.CenterY - 14f, 28f, 28f);
            UiPrimitives.DrawGhostIconButton(g, theme, _hit, "phone.pin:" + app.Package, pin, UiIcon.Pin, 14f,
                active: pinned, tooltip: L10n.T(pinned ? "Phone.RemoveFromQuickPanel" : "Phone.AddToQuickPanel"));
            float tw = MathF.Max(1f, pin.X - icon.Right - 14f);
            g.DrawText(app.Name, new UiRect(icon.Right + 8f, row.Y + 6f, tw, 18f), theme.TextPrimary, 13f);
            g.DrawText(app.Package, new UiRect(icon.Right + 8f, row.Y + 24f, tw, 15f), theme.TextSecondary, 10f);
        }
        _hit.PopClip();
        g.PopClip();
        _scroll.Draw(g, theme, _hit, "phone.scroll", view);
    }

    private void SelectCatalogRow(string package, bool ctrl, bool shift)
    {
        int target = Array.FindIndex(_filteredCatalog, app => app.Package == package);
        if (target < 0) return;
        int anchor = shift ? Array.FindIndex(_filteredCatalog, app => app.Package == _catalogAnchor) : -1;
        if (!ctrl) _selectedCatalog.Clear();
        if (anchor >= 0)
        {
            for (int i = Math.Min(anchor, target); i <= Math.Max(anchor, target); i++) _selectedCatalog.Add(_filteredCatalog[i].Package);
        }
        else
        {
            if (!ctrl || !_selectedCatalog.Remove(package)) _selectedCatalog.Add(package);
            _catalogAnchor = package;
        }
        _actions.Invalidate();
    }

    private void ShowCatalogMenu(string package)
    {
        if (!_selectedCatalog.Contains(package)) SelectCatalogRow(package, false, false);
        var store = _actions.Host.Phone.Store;
        string profile = store.Profile.Id;
        var apps = _filteredCatalog.Where(app => _selectedCatalog.Contains(app.Package) &&
            !store.Profile.RetainedApps.Any(kept => kept.Package == app.Package)).ToArray();
        _actions.ShowMenuAtCursor([new UiMenuItem
        {
            Text = apps.Length == 0 ? L10n.T("Phone.AddSelectedApps") : string.Format(L10n.T("Phone.AddSelectedAppsCount"), apps.Length),
            Enabled = apps.Length > 0 && !_busy,
            Click = () =>
            {
                if (_active && _showCatalog && !_busy && store.Profile.Id == profile) store.RetainMany(apps);
            }
        }]);
    }
}
#endif

using System;
using System.Collections.Generic;
using System.Globalization;
using SonicRoute.Core;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed class ThemePageView : IViewPage
{
    private static readonly (string Key, string LabelKey, UiIcon Icon)[] ThemeModes =
    [
        ("system", "St.ThemeSystem", UiIcon.Monitor),
        ("light", "St.ThemeLight", UiIcon.Sun),
        ("dark", "St.ThemeDark", UiIcon.Moon),
    ];

    private static readonly string[] OsdGridCodes =
    [
        "TL", "TC", "TR",
        "ML", "C",  "MR",
        "BL", "BC", "BR",
    ];

    private readonly IViewHostActions _actions;
    private readonly UiScrollState _scroll = new();
    private readonly Dictionary<string, Action> _clicks = new(StringComparer.Ordinal);

    private UiRect _viewportRect;
    private UiRect _opacitySliderRect;
    private UiRect _osdWidthSliderRect;
    private UiRect _osdFontSliderRect;
    private UiRect _osdFadeInSliderRect;
    private UiRect _osdFadeOutSliderRect;
    private UiRect _panelHeightSliderRect;

    private UiRect _hexBoxRect;
    private UiRect _osdOffsetXRect;
    private UiRect _osdOffsetYRect;
    private UiRect _panelCustomXRect;
    private UiRect _panelCustomYRect;

    private string? _draggingSlider;
    private double _initialSliderValue;
    private bool _osdMoreExpanded = true;
    private ColorPickerWindow? _pickerWindow;

    internal ThemePageView(IViewHostActions actions)
    {
        _actions = actions;
    }

    public void OnActivated() { }

    public void OnDeactivated()
    {
        FinishSliderDrag(preview: false);
        if (_pickerWindow is { IsVisible: true }) _pickerWindow.CloseWindow();
        _pickerWindow = null;
    }

    public void OnCaptureLost()
    {
        FinishSliderDrag(preview: false);
        _scroll.EndDrag();
        _actions.Invalidate();
    }

    public void RefreshData() => _actions.Invalidate();

    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _clicks.Clear();
        _viewportRect = contentRect;
        var cfg = _actions.Config;

        string pageHint = L10n.T("Th.PageHint");
        float hintH = D2dSharedContext.Instance.MeasureText(pageHint, UiMetrics.FontFamily, 11.5f, 400, contentRect.Width, 60f, wrap: true).Height;

        float card1H = 308f;
        float card2H = 148f;
        float card3H = 412f + (_osdMoreExpanded ? 164f : 0f);
        float card4H = 360f;
        float totalH = 33f + hintH + 20f + card1H + 14f + card2H + 14f + card3H + 14f + card4H + 16f;

        _scroll.ViewportHeight = contentRect.Height;
        _scroll.ContentHeight = totalH;
        _scroll.Clamp();

        float w = _scroll.MaxOffset > 0.5f ? contentRect.Width - UiMetrics.ScrollBarWidth - 4f : contentRect.Width;

        g.PushClip(contentRect);
        hit.PushClip(contentRect);
        float y = contentRect.Y - _scroll.Offset;

        g.DrawText(L10n.T("Nav.Theme"), new UiRect(contentRect.X, y, w, 28f), t.TextPrimary, 22f, 600);
        y += 33f;
        g.DrawText(pageHint, new UiRect(contentRect.X, y, w, hintH + 2f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: false);
        y += hintH + 20f;

        var card1 = new UiRect(contentRect.X, y, w, card1H);
        UiPrimitives.DrawCard(g, t, card1, UiMetrics.CardCornerRadius);
        RenderModeAndAccentCard(g, t, hit, card1.Deflate(18f), cfg);
        y += card1H + 14f;

        var card2 = new UiRect(contentRect.X, y, w, card2H);
        UiPrimitives.DrawCard(g, t, card2, UiMetrics.CardCornerRadius);
        RenderOpacityCard(g, t, hit, card2.Deflate(18f), cfg);
        y += card2H + 14f;

        var card3 = new UiRect(contentRect.X, y, w, card3H);
        UiPrimitives.DrawCard(g, t, card3, 16f);
        RenderOsdCard(g, t, hit, card3.Deflate(20f), cfg);
        y += card3H + 14f;

        var card4 = new UiRect(contentRect.X, y, w, card4H);
        UiPrimitives.DrawCard(g, t, card4, 16f);
        RenderPanelCard(g, t, hit, card4.Deflate(20f), cfg);

        hit.PopClip();
        g.PopClip();
        _scroll.Draw(g, t, hit, "th.scroll", contentRect);
    }

    private void RenderModeAndAccentCard(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect inner, AppConfig cfg)
    {
        float y = inner.Y;
        DrawSectionBadgeHeader(g, t, new UiRect(inner.X, y, inner.Width, 38f), UiIcon.Palette, L10n.T("St.ThemeMode"), L10n.T("Th.ModeHint"));
        y += 54f;

        const float modeGap = 8f;
        float modeW = MathF.Min(136f, (inner.Width - modeGap * 2f) / 3f);
        for (int i = 0; i < ThemeModes.Length; i++)
        {
            var (key, labelKey, icon) = ThemeModes[i];
            var mr = new UiRect(inner.X + i * (modeW + modeGap), y, modeW, 46f);
            bool selected = string.Equals(cfg.ThemeMode, key, StringComparison.OrdinalIgnoreCase)
                || (key == "system" && string.IsNullOrEmpty(cfg.ThemeMode));
            string id = "th.mode." + key;
            hit.Add(id, mr, UiCursorKind.Hand);
            _clicks[id] = () =>
            {
                cfg.ThemeMode = key;
                SaveAndNotifyTheme();
            };

            UiColor bg = selected ? t.NavSelected : hit.IsHover(id) ? t.NavHover : t.SurfaceBg;
            g.FillRoundedRect(mr, 9f, bg);
            g.DrawRoundedRect(mr, 9f, selected ? t.Accent : t.Border, 1f);
            g.DrawIcon(icon, new UiRect(mr.X + 10f, mr.CenterY - 8f, 16f, 16f), selected ? t.Accent : t.TextSecondary, 1.8f);
            g.DrawText(L10n.T(labelKey), new UiRect(mr.X + 33f, mr.Y, mr.Width - 42f, mr.Height),
                t.TextPrimary, 12.5f, selected ? 600 : 400);

            if (selected)
            {
                float cx = mr.Right - 4f;
                float cy = mr.Y + 4f;
                g.FillEllipse(cx, cy, 6.5f, 6.5f, t.Accent);
                g.DrawIcon(UiIcon.Check, new UiRect(cx - 4.5f, cy - 4.5f, 9f, 9f), t.OnAccent, 2f);
            }
        }
        y += 56f;

        var box = new UiRect(inner.X, y, inner.Width, inner.Bottom - y);
        g.DrawRoundedRect(box, 11f, t.Border, 1f);
        var boxIn = box.Deflate(14f, 12f);

        float previewW = MathF.Max(130f, (boxIn.Width - 14f) / 2.4f);
        float leftW = boxIn.Width - previewW - 14f;

        g.DrawText(L10n.T("Th.CustomTitle"), new UiRect(boxIn.X, boxIn.Y, leftW, 20f), t.TextPrimary, 13.5f, 600);
        g.DrawText(L10n.T("Th.CustomHint"), new UiRect(boxIn.X, boxIn.Y + 22f, leftW, 18f), t.TextSecondary, 11.5f, 400);

        float rowY = boxIn.Y + 46f;
        var swatch = new UiRect(boxIn.X, rowY, 54f, 54f);
        g.FillRoundedRect(swatch, 8f, t.Accent);


        _hexBoxRect = new UiRect(swatch.Right + 10f, rowY + 10f, 90f, 34f);
        UiPrimitives.DrawTextBox(g, t, hit, "th.hex", _hexBoxRect, t.Accent.ToHexRgb(), "#2F80ED", _actions.ActiveInlineEditTag == "th.hex", mono: true, fontSize: 12f);
        _clicks["th.hex"] = () =>
        {
            _actions.StartInlineEdit("th.hex", _hexBoxRect, t.Accent.ToHexRgb(), null, (val, _) => CommitHex(val), mono: true);
        };

        string pickLabel = L10n.T("Th.ChooseColor");
        float pickW = MathF.Max(104f, D2dSharedContext.Instance.MeasureText(pickLabel, UiMetrics.FontFamily, 12f, 400, 160f, 34f, false).Width + 38f);
        var pickBtn = new UiRect(_hexBoxRect.Right + 8f, rowY + 10f, MathF.Min(pickW, MathF.Max(80f, leftW - (_hexBoxRect.Right - boxIn.X) - 8f)), 34f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "th.chooseColor", pickBtn, pickLabel, fontSize: 12f, icon: UiIcon.Theme);
        _clicks["th.chooseColor"] = OpenColorPicker;

        var prevRect = new UiRect(boxIn.Right - previewW, boxIn.Y, previewW, MathF.Min(123f, boxIn.Height));
        RenderThemePreview(g, t, prevRect);
    }

    private static void RenderThemePreview(D2dTarget g, UiThemePalette t, UiRect rect)
    {
        g.FillRoundedRect(rect, 11f, t.SurfaceAlt);
        var inner = rect.Deflate(10f);

        g.DrawText(L10n.T("Th.Preview"), new UiRect(inner.X, inner.Y, inner.Width - 36f, 14f), t.TextPrimary, 10f, 600);
        for (int i = 0; i < 3; i++)
            g.FillEllipse(inner.Right - 24f + i * 9f, inner.Y + 7f, 3f, 3f, t.Border);

        var body = new UiRect(inner.X, inner.Y + 20f, inner.Width, MathF.Max(48f, inner.Height - 20f));
        g.FillRoundedRect(body, 6f, t.SurfaceBg);
        var bIn = body.Deflate(7f);

        float navW = MathF.Min(54f, bIn.Width * 0.36f);
        g.FillRoundedRect(new UiRect(bIn.X + 4f, bIn.Y + 6f, navW - 12f, 5f), 2.5f, t.SliderBg);
        var activePill = new UiRect(bIn.X, bIn.Y + 18f, navW - 4f, 16f);
        g.FillRoundedRect(activePill, 4f, t.NavSelected);
        g.FillRoundedRect(activePill.Deflate(5f, 5.5f), 2.5f, t.Accent);
        g.FillRoundedRect(new UiRect(bIn.X + 4f, bIn.Y + 42f, navW - 16f, 5f), 2.5f, t.SliderBg);

        float rx = bIn.X + navW + 4f;
        float rw = MathF.Max(30f, bIn.Right - rx);
        g.FillRoundedRect(new UiRect(rx, bIn.Y + 4f, MathF.Min(55f, rw * 0.65f), 5f), 2.5f, t.SliderBg);
        g.FillEllipse(bIn.Right - 6f, bIn.Y + 6.5f, 5.5f, 5.5f, t.Accent);

        var miniCard = new UiRect(rx, bIn.Y + 18f, rw, MathF.Max(28f, bIn.Height - 20f));
        g.DrawRoundedRect(miniCard, 5f, t.Border, 1f);
        var mcIn = miniCard.Deflate(6f);
        g.FillRoundedRect(new UiRect(mcIn.X, mcIn.Y, 22f, 22f), 4f, t.NavSelected);
        g.FillRoundedRect(new UiRect(mcIn.X + 28f, mcIn.Y + 4f, MathF.Max(10f, mcIn.Width - 30f), 5f), 2.5f, t.SliderBg);
        g.FillRoundedRect(new UiRect(mcIn.X + 28f, mcIn.Y + 13f, MathF.Min(35f, MathF.Max(8f, mcIn.Width - 30f)), 4f), 2f, t.Border);
    }

    private void RenderOpacityCard(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect inner, AppConfig cfg)
    {
        DrawSectionBadgeHeader(g, t, new UiRect(inner.X, inner.Y, inner.Width, 38f), UiIcon.Transparency, L10n.T("St.Opacity"), L10n.T("Th.OpacityHint"));

        float rowY = inner.Y + 50f;
        var badgeRect = new UiRect(inner.Right - 58f, rowY, 58f, 34f);
        g.FillRoundedRect(badgeRect, 12f, t.SurfaceAlt);
        g.DrawText($"{cfg.BackgroundOpacity}%", badgeRect, t.TextPrimary, 14f, 600, UiTextAlign.Center, UiVertAlign.Center);

        _opacitySliderRect = new UiRect(inner.X + 36f, rowY + 2f, MathF.Max(60f, badgeRect.X - inner.X - 50f), 30f);
        UiPrimitives.DrawVolumeSlider(g, t, hit, "th.opacity", _opacitySliderRect, cfg.BackgroundOpacity / 100f, 0f, false, false);

        g.DrawText(L10n.T("St.OpacityHint"), new UiRect(inner.X + 36f, rowY + 40f, inner.Width - 36f, 20f), t.TextSecondary, 11.5f, 400);
    }

    private void RenderOsdCard(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect inner, AppConfig cfg)
    {
        float y = inner.Y;

        string adjLabel = L10n.T("Exp.OsdAdjust");
        string prevLabel = L10n.T("Exp.OsdPreview");
        string rstLabel = L10n.T("Exp.OsdReset");
        float adjW = MathF.Max(92f, D2dSharedContext.Instance.MeasureText(adjLabel, UiMetrics.FontFamily, 12f, 400, 160f, 36f, false).Width + 32f);
        float prevW = MathF.Max(72f, D2dSharedContext.Instance.MeasureText(prevLabel, UiMetrics.FontFamily, 12f, 400, 120f, 36f, false).Width + 30f);
        float rstW = MathF.Max(108f, D2dSharedContext.Instance.MeasureText(rstLabel, UiMetrics.FontFamily, 12f, 400, 180f, 36f, false).Width + 32f);

        var rstBtn = new UiRect(inner.Right - rstW, y + 2f, rstW, 36f);
        var prevBtn = new UiRect(rstBtn.X - 8f - prevW, y + 2f, prevW, 36f);
        var adjBtn = new UiRect(prevBtn.X - 8f - adjW, y + 2f, adjW, 36f);

        UiPrimitives.DrawSecondaryButton(g, t, hit, "th.osd.adjust", adjBtn, adjLabel, fontSize: 12f, icon: UiIcon.Position, tooltip: L10n.T("Exp.OsdAdjustHint"));
        UiPrimitives.DrawSecondaryButton(g, t, hit, "th.osd.preview", prevBtn, prevLabel, fontSize: 12f, icon: UiIcon.Play);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "th.osd.reset", rstBtn, rstLabel, fontSize: 12f, icon: UiIcon.Reset, tooltip: L10n.T("Exp.OsdResetHint"));
        _clicks["th.osd.adjust"] = () => _actions.Host.BeginOsdAdjust();
        _clicks["th.osd.preview"] = () => _actions.Host.PreviewOsd();
        _clicks["th.osd.reset"] = ResetOsdSettings;

        DrawThemeCardHeader(g, t, new UiRect(inner.X, y, MathF.Max(100f, adjBtn.X - inner.X - 10f), 42f),
            UiIcon.Monitor, L10n.T("Theme.OsdAppearanceSettings"), L10n.T("Th.OsdHint"));
        y += 56f;

        g.DrawLine(inner.X, y, inner.Right, y, t.Border, 1f, roundCaps: false);
        y += 12f;

        DrawToggleRow(g, t, hit, "th.osd.volvisual", new UiRect(inner.X, y, inner.Width, 38f),
            L10n.T("Theme.OsdVolumeVisual"), L10n.T("Theme.OsdVolumeVisualHint"), cfg.OsdVolumeVisualEnabled,
            () => { cfg.OsdVolumeVisualEnabled = !cfg.OsdVolumeVisualEnabled; SaveAndNotifySettings(); });
        y += 44f;

        DrawToggleRow(g, t, hit, "th.osd.micpersist", new UiRect(inner.X, y, inner.Width, 28f),
            L10n.T("Theme.MicMuteOsdPersist"), null, cfg.MicMuteOsdPersistent,
            () => { cfg.MicMuteOsdPersistent = !cfg.MicMuteOsdPersistent; SaveAndNotifySettings(); });
        y += 34f;

        DrawToggleRow(g, t, hit, "th.osd.mictrack", new UiRect(inner.X, y, inner.Width, 28f),
            L10n.T("Theme.MicMuteTrackInput"), null, cfg.MicMuteOsdDefaultInputPersistent,
            () => { cfg.MicMuteOsdDefaultInputPersistent = !cfg.MicMuteOsdDefaultInputPersistent; SaveAndNotifySettings(); });
        y += 36f;

        g.DrawLine(inner.X, y, inner.Right, y, t.Border, 1f, roundCaps: false);
        y += 12f;

        g.DrawText(L10n.T("Exp.Osd"), new UiRect(inner.X, y, inner.Width, 20f), t.TextPrimary, 13.5f, 600);
        y += 26f;

        float gridCell = 26f;
        float gridGap = 4f;
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                string code = OsdGridCodes[row * 3 + col];
                var cell = new UiRect(inner.X + col * (gridCell + gridGap), y + row * (gridCell + gridGap), gridCell, gridCell);
                bool active = string.Equals(cfg.OsdPosition, code, StringComparison.OrdinalIgnoreCase);
                string gid = "th.osd.pos." + code;
                UiPrimitives.DrawSecondaryButton(g, t, hit, gid, cell, "", fontSize: 10f, radius: 5f, active: active, tooltip: code);
                g.FillEllipse(cell.CenterX, cell.CenterY, 3f, 3f, active ? t.Accent : t.TextSecondary);
                var codeCopy = code;
                _clicks[gid] = () =>
                {
                    cfg.OsdPosition = codeCopy;
                    if (!SaveAndNotifySettings()) return;
                    _actions.Host.PreviewOsd();
                };
            }
        }

        float offLeft = inner.X + 3 * (gridCell + gridGap) + 20f;
        g.DrawText(L10n.T("Exp.Osd.X") + " (px)", new UiRect(offLeft, y + 4f, 80f, 16f), t.TextSecondary, 11.5f, 400);
        _osdOffsetXRect = new UiRect(offLeft, y + 24f, 82f, 30f);
        UiPrimitives.DrawTextBox(g, t, hit, "th.osd.ox", _osdOffsetXRect, cfg.OsdOffsetX.ToString(CultureInfo.InvariantCulture), "0", _actions.ActiveInlineEditTag == "th.osd.ox", mono: true);
        _clicks["th.osd.ox"] = () =>
        {
            _actions.StartInlineEdit("th.osd.ox", _osdOffsetXRect, cfg.OsdOffsetX.ToString(CultureInfo.InvariantCulture), null, (val, _) =>
            {
                if (int.TryParse(val.Trim(), out int ox)) { cfg.OsdOffsetX = Math.Clamp(ox, -2000, 2000); SaveAndNotifySettings(); }
            }, mono: true);
        };

        g.DrawText(L10n.T("Exp.Osd.Y") + " (px)", new UiRect(offLeft + 96f, y + 4f, 80f, 16f), t.TextSecondary, 11.5f, 400);
        _osdOffsetYRect = new UiRect(offLeft + 96f, y + 24f, 82f, 30f);
        UiPrimitives.DrawTextBox(g, t, hit, "th.osd.oy", _osdOffsetYRect, cfg.OsdOffsetY.ToString(CultureInfo.InvariantCulture), "0", _actions.ActiveInlineEditTag == "th.osd.oy", mono: true);
        _clicks["th.osd.oy"] = () =>
        {
            _actions.StartInlineEdit("th.osd.oy", _osdOffsetYRect, cfg.OsdOffsetY.ToString(CultureInfo.InvariantCulture), null, (val, _) =>
            {
                if (int.TryParse(val.Trim(), out int oy)) { cfg.OsdOffsetY = Math.Clamp(oy, -2000, 2000); SaveAndNotifySettings(); }
            }, mono: true);
        };

        y += 3 * (gridCell + gridGap) + 12f;
        g.DrawLine(inner.X, y, inner.Right, y, t.Border, 1f, roundCaps: false);
        y += 12f;

        var moreBtn = new UiRect(inner.Right - 96f, y - 2f, 96f, 26f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "th.osd.more", moreBtn, L10n.T("Exp.MoreOptions"), fontSize: 11.5f, active: _osdMoreExpanded);
        _clicks["th.osd.more"] = () => { _osdMoreExpanded = !_osdMoreExpanded; _actions.Invalidate(); };
        g.DrawText(L10n.T("Theme.OsdStyle"), new UiRect(inner.X, y, inner.Width - 108f, 24f), t.TextPrimary, 14.5f, 600);
        y += 34f;

        if (_osdMoreExpanded)
        {
            _osdWidthSliderRect = DrawLabeledSlider(g, t, hit, "th.osd.w", new UiRect(inner.X, y, inner.Width, 38f),
                L10n.T("Exp.OsdWidthLabel"), $"{cfg.OsdWidth}px", (cfg.OsdWidth - 180f) / (600f - 180f));
            y += 40f;

            double fontScale = Math.Clamp(cfg.OsdFontScale, 0.7, 2.0);
            _osdFontSliderRect = DrawLabeledSlider(g, t, hit, "th.osd.font", new UiRect(inner.X, y, inner.Width, 38f),
                L10n.T("Exp.OsdFontLabel"), $"{(int)Math.Round(fontScale * 100)}%", (float)((fontScale - 0.7) / (2.0 - 0.7)));
            y += 40f;

            _osdFadeInSliderRect = DrawLabeledSlider(g, t, hit, "th.osd.fin", new UiRect(inner.X, y, inner.Width, 38f),
                L10n.T("Exp.OsdFadeInLabel"), $"{cfg.OsdFadeInMs}ms", cfg.OsdFadeInMs / 500f);
            y += 40f;

            _osdFadeOutSliderRect = DrawLabeledSlider(g, t, hit, "th.osd.fout", new UiRect(inner.X, y, inner.Width, 38f),
                L10n.T("Exp.OsdFadeOutLabel"), $"{cfg.OsdFadeOutMs}ms", cfg.OsdFadeOutMs / 1000f);
        }
    }

    private void RenderPanelCard(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect inner, AppConfig cfg)
    {
        float y = inner.Y;

        string adjLabel = L10n.T("Exp.PanelPosAdjust");
        string rstLabel = L10n.T("Exp.PanelPosReset");
        float adjW = MathF.Max(88f, D2dSharedContext.Instance.MeasureText(adjLabel, UiMetrics.FontFamily, 12f, 400, 150f, 36f, false).Width + 32f);
        float rstW = MathF.Max(92f, D2dSharedContext.Instance.MeasureText(rstLabel, UiMetrics.FontFamily, 12f, 400, 150f, 36f, false).Width + 32f);
        var rstBtn = new UiRect(inner.Right - rstW, y + 2f, rstW, 36f);
        var adjBtn = new UiRect(rstBtn.X - 8f - adjW, y + 2f, adjW, 36f);

        UiPrimitives.DrawSecondaryButton(g, t, hit, "th.panel.adjust", adjBtn, adjLabel, fontSize: 12f, icon: UiIcon.Position);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "th.panel.reset", rstBtn, rstLabel, fontSize: 12f, icon: UiIcon.Reset);
        _clicks["th.panel.adjust"] = () => _actions.Host.BeginQuickPanelAdjust();
        _clicks["th.panel.reset"] = () =>
        {
            cfg.QuickPanelPosMode = "default";
            cfg.QuickPanelCustomX = -1;
            cfg.QuickPanelCustomY = -1;
            if (!SaveAndNotifySettings()) return;
            _actions.ShowNotice(L10n.T("Exp.PanelPosResetDone"));
        };

        DrawThemeCardHeader(g, t, new UiRect(inner.X, y, MathF.Max(100f, adjBtn.X - inner.X - 10f), 42f),
            UiIcon.Application, L10n.T("Exp.PanelPosSection"), L10n.T("Th.PanelHint"));
        y += 56f;

        g.DrawLine(inner.X, y, inner.Right, y, t.Border, 1f, roundCaps: false);
        y += 12f;

        bool isCustomPos = string.Equals(cfg.QuickPanelPosMode, "custom", StringComparison.OrdinalIgnoreCase);
        var defBtn = new UiRect(inner.X, y, 108f, 30f);
        var cusBtn = new UiRect(defBtn.Right + 8f, y, 108f, 30f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "th.panel.posDef", defBtn, L10n.T("Dev.SystemDefault"), fontSize: 12f, active: !isCustomPos);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "th.panel.posCus", cusBtn, L10n.T("Exp.Osd.Custom"), fontSize: 12f, active: isCustomPos);
        _clicks["th.panel.posDef"] = () => { cfg.QuickPanelPosMode = "default"; SaveAndNotifySettings(); };
        _clicks["th.panel.posCus"] = () => { cfg.QuickPanelPosMode = "custom"; SaveAndNotifySettings(); };

        _panelCustomXRect = new UiRect(cusBtn.Right + 16f, y, 72f, 30f);
        _panelCustomYRect = new UiRect(_panelCustomXRect.Right + 8f, y, 72f, 30f);
        UiPrimitives.DrawTextBox(g, t, hit, "th.panel.cx", _panelCustomXRect, cfg.QuickPanelCustomX.ToString(CultureInfo.InvariantCulture), "X", _actions.ActiveInlineEditTag == "th.panel.cx", mono: true);
        UiPrimitives.DrawTextBox(g, t, hit, "th.panel.cy", _panelCustomYRect, cfg.QuickPanelCustomY.ToString(CultureInfo.InvariantCulture), "Y", _actions.ActiveInlineEditTag == "th.panel.cy", mono: true);
        _clicks["th.panel.cx"] = () =>
        {
            _actions.StartInlineEdit("th.panel.cx", _panelCustomXRect, cfg.QuickPanelCustomX.ToString(CultureInfo.InvariantCulture), null, (val, _) =>
            {
                if (int.TryParse(val.Trim(), out int px)) { cfg.QuickPanelPosMode = "custom"; cfg.QuickPanelCustomX = px; SaveAndNotifySettings(); }
            }, mono: true);
        };
        _clicks["th.panel.cy"] = () =>
        {
            _actions.StartInlineEdit("th.panel.cy", _panelCustomYRect, cfg.QuickPanelCustomY.ToString(CultureInfo.InvariantCulture), null, (val, _) =>
            {
                if (int.TryParse(val.Trim(), out int py)) { cfg.QuickPanelPosMode = "custom"; cfg.QuickPanelCustomY = py; SaveAndNotifySettings(); }
            }, mono: true);
        };
        y += 42f;

        int pHeight = Math.Clamp(cfg.QuickPanelHeight, 350, 850);
        _panelHeightSliderRect = DrawLabeledSlider(g, t, hit, "th.panel.h", new UiRect(inner.X, y, inner.Width, 38f),
            L10n.T("Exp.PanelHeight"), $"{pHeight} px", (pHeight - 350f) / (850f - 350f));
        y += 44f;

        DrawToggleRow(g, t, hit, "th.panel.peak", new UiRect(inner.X, y, inner.Width, 30f),
            L10n.T("Theme.AppPeakMeter"), null, cfg.ShowAppPeakMeter,
            () => { cfg.ShowAppPeakMeter = !cfg.ShowAppPeakMeter; SaveAndNotifySettings(); });
        y += 38f;

        DrawToggleRow(g, t, hit, "th.panel.legacy", new UiRect(inner.X, y, inner.Width, 38f),
            L10n.T("Theme.PanelLegacyRows"), L10n.T("Theme.PanelLegacyRowsHint"), cfg.QuickPanelUseLegacyAppRows,
            () => { cfg.QuickPanelUseLegacyAppRows = !cfg.QuickPanelUseLegacyAppRows; SaveAndNotifySettings(); });
        y += 46f;

        if (cfg.QuickPanelUseLegacyAppRows)
        {
            DrawToggleRow(g, t, hit, "th.panel.compactOut", new UiRect(inner.X + 20f, y, inner.Width - 20f, 36f),
                L10n.T("Theme.PanelCompactOutputName"), L10n.T("Theme.PanelCompactOutputNameHint"), cfg.QuickPanelCompactShowOutputName,
                () => { cfg.QuickPanelCompactShowOutputName = !cfg.QuickPanelCompactShowOutputName; SaveAndNotifySettings(); });
        }
        else
        {
            DrawToggleRow(g, t, hit, "th.panel.fullOut", new UiRect(inner.X, y, inner.Width, 36f),
                L10n.T("Theme.PanelOutputName"), L10n.T("Theme.PanelOutputNameHint"), cfg.QuickPanelShowOutputName,
                () => { cfg.QuickPanelShowOutputName = !cfg.QuickPanelShowOutputName; SaveAndNotifySettings(); });
        }
        y += 44f;

        DrawToggleRow(g, t, hit, "th.panel.allOutVol", new UiRect(inner.X, y, inner.Width, 30f),
            L10n.T("Qp.ExpandOutputVolumes"), null, cfg.QuickPanelShowAllOutputVolumes,
            () => { cfg.QuickPanelShowAllOutputVolumes = !cfg.QuickPanelShowAllOutputVolumes; SaveAndNotifySettings(); });
    }

    private static void DrawSectionBadgeHeader(D2dTarget g, UiThemePalette t, UiRect rect, UiIcon icon, string title, string hint)
    {
        var iconBg = new UiRect(rect.X, rect.Y + 2f, 32f, 32f);
        g.FillRoundedRect(iconBg, 16f, t.NavSelected);
        g.DrawIcon(icon, iconBg.Deflate(8f), t.Accent, 1.8f);
        g.DrawText(title, new UiRect(rect.X + 42f, rect.Y, rect.Width - 42f, 20f), t.TextPrimary, 14.5f, 600);
        g.DrawText(hint, new UiRect(rect.X + 42f, rect.Y + 21f, rect.Width - 42f, 18f), t.TextSecondary, 11.5f, 400);
    }

    private static void DrawThemeCardHeader(D2dTarget g, UiThemePalette t, UiRect rect, UiIcon icon, string title, string hint)
    {
        var iconBg = new UiRect(rect.X, rect.Y + 1f, 40f, 40f);
        g.FillRoundedRect(iconBg, 14f, t.NavSelected);
        g.DrawIcon(icon, new UiRect(iconBg.CenterX - 10.5f, iconBg.CenterY - 10.5f, 21f, 21f), t.Accent, 1.8f);
        g.DrawText(title, new UiRect(rect.X + 52f, rect.Y, rect.Width - 52f, 22f), t.TextPrimary, 16f, 600);
        g.DrawText(hint, new UiRect(rect.X + 52f, rect.Y + 24f, rect.Width - 52f, 18f), t.TextSecondary, 11.5f, 400);
    }

    private void DrawToggleRow(
        D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect,
        string label, string? hint, bool isChecked, Action onToggle)
    {
        hit.Add(id, rect, UiCursorKind.Hand);
        _clicks[id] = onToggle;
        float textW = MathF.Max(60f, rect.Width - 50f);
        if (string.IsNullOrEmpty(hint))
        {
            g.DrawText(label, new UiRect(rect.X, rect.Y, textW, rect.Height), t.TextPrimary, 13.5f, 400);
        }
        else
        {
            g.DrawText(label, new UiRect(rect.X, rect.Y, textW, 20f), t.TextPrimary, 13.5f, 400);
            g.DrawText(hint, new UiRect(rect.X, rect.Y + 20f, textW, 18f), t.TextSecondary, 11.5f, 400);
        }
        UiPrimitives.DrawSwitch(g, t, hit, id, new UiRect(rect.Right - 36f, rect.CenterY - 12f, 34f, 24f), isChecked);
    }

    private static UiRect DrawLabeledSlider(D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, string label, string valText, float norm01)
    {
        g.DrawText(label, new UiRect(rect.X, rect.Y, 110f, rect.Height), t.TextPrimary, 12.5f, 400);
        var valRect = new UiRect(rect.Right - 62f, rect.Y, 62f, rect.Height);
        g.DrawText(valText, valRect, t.TextSecondary, 12.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center);
        var sliderRect = new UiRect(rect.X + 118f, rect.Y, MathF.Max(60f, valRect.X - rect.X - 134f), rect.Height);
        UiPrimitives.DrawVolumeSlider(g, t, hit, id, sliderRect, Math.Clamp(norm01, 0f, 1f), 0f, false, false);
        return sliderRect;
    }

    public bool OnPointerMove(float x, float y, bool leftDown)
    {
        if (leftDown && _draggingSlider != null)
        {
            ApplySliderDrag(_draggingSlider, x);
            return true;
        }
        return leftDown && _scroll.DragMove(y);
    }

    public bool OnPointerDown(UiPointerEventArgs e, string? targetId)
    {
        if (e.Button != UiMouseButton.Left) return false;
        if (targetId == "th.scroll" && _scroll.BeginDrag(e.Y)) return true;

        _draggingSlider = targetId switch
        {
            "th.opacity" => "opacity",
            "th.osd.w" => "osdW",
            "th.osd.font" => "osdFont",
            "th.osd.fin" => "osdFin",
            "th.osd.fout" => "osdFout",
            "th.panel.h" => "panelH",
            _ => null,
        };
        if (_draggingSlider == null) return false;
        _initialSliderValue = SliderValue(_draggingSlider);
        ApplySliderDrag(_draggingSlider, e.X);
        return true;
    }

    public bool OnPointerUp(UiPointerEventArgs e, string? clickId)
    {
        if (e.Button != UiMouseButton.Left) return false;
        if (_draggingSlider != null)
        {
            FinishSliderDrag(preview: true);
            return true;
        }
        if (_scroll.Dragging)
        {
            _scroll.EndDrag();
            return true;
        }
        if (clickId == null || !_clicks.TryGetValue(clickId, out var click)) return false;
        click();
        return true;
    }

    public bool OnMouseWheel(float x, float y, float delta, bool horizontal)
    {
        if (_viewportRect.Contains(x, y))
        {
            _scroll.OnWheel(delta);
            _actions.CloseInlineEdit(commit: true);
            return true;
        }
        return false;
    }

    public bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt) => false;

    public bool OnFocusedKey(string id, int vk)
    {
        var cfg = _actions.Config;
        (double current, double min, double max, double small, double large) = id switch
        {
            "th.opacity" => (cfg.BackgroundOpacity, 0, 100, 1, 10),
            "th.osd.w" => (cfg.OsdWidth, 180, 600, 1, 10),
            "th.osd.font" => (cfg.OsdFontScale, 0.7, 2.0, 0.05, 0.1),
            "th.osd.fin" => (cfg.OsdFadeInMs, 0, 500, 10, 50),
            "th.osd.fout" => (cfg.OsdFadeOutMs, 0, 1000, 50, 100),
            "th.panel.h" => (cfg.QuickPanelHeight, 350, 850, 10, 10),
            _ => (0, 0, 0, 0, 0)
        };
        if (max == 0 || !ViewSliderKeys.TryChange(vk, current, min, max, small, large, out double value)) return false;
        if (value == current) return true;
        void SetValue(double v)
        {
            switch (id)
            {
                case "th.opacity": cfg.BackgroundOpacity = (int)v; break;
                case "th.osd.w": cfg.OsdWidth = (int)v; break;
                case "th.osd.font": cfg.OsdFontScale = Math.Round(v, 2); break;
                case "th.osd.fin": cfg.OsdFadeInMs = (int)v; break;
                case "th.osd.fout": cfg.OsdFadeOutMs = (int)v; break;
                case "th.panel.h": cfg.QuickPanelHeight = (int)v; break;
            }
        }
        SetValue(value);
        if (!_actions.CommitSettings()) return true;
        if (id == "th.opacity") _actions.NotifyThemeChanged();
        if (id == "th.panel.h") _actions.NotifySettingsChanged();
        if (id is "th.osd.w" or "th.osd.font") _actions.Host.PreviewOsd();
        _actions.Invalidate();
        return true;
    }

    private double SliderValue(string id) => id switch
    {
        "opacity" => _actions.Config.BackgroundOpacity,
        "osdW" => _actions.Config.OsdWidth,
        "osdFont" => _actions.Config.OsdFontScale,
        "osdFin" => _actions.Config.OsdFadeInMs,
        "osdFout" => _actions.Config.OsdFadeOutMs,
        _ => _actions.Config.QuickPanelHeight
    };

    private void FinishSliderDrag(bool preview)
    {
        if (_draggingSlider is not string id) return;
        _draggingSlider = null;
        if (SliderValue(id) == _initialSliderValue) return;
        if (!_actions.CommitSettings())
        {
            if (id == "opacity") _actions.NotifyThemeChanged();
            return;
        }
        if (preview && id is ("osdW" or "osdFont")) _actions.Host.PreviewOsd();
        if (id == "panelH") _actions.NotifySettingsChanged();
    }

    private void ApplySliderDrag(string name, float x)
    {
        var cfg = _actions.Config;
        switch (name)
        {
            case "opacity":
            {
                int v = UiPrimitives.SliderValueFromX(_opacitySliderRect, x);
                if (cfg.BackgroundOpacity != v)
                {
                    cfg.BackgroundOpacity = v;
                    _actions.NotifyThemeChanged();
                }
                break;
            }
            case "osdW":
            {
                float t = UiPrimitives.SliderValueFromX(_osdWidthSliderRect, x) / 100f;
                int v = Math.Clamp((int)MathF.Round(180f + t * (600f - 180f)), 180, 600);
                if (cfg.OsdWidth != v)
                {
                    cfg.OsdWidth = v;
                    _actions.Invalidate();
                }
                break;
            }
            case "osdFont":
            {
                float t = UiPrimitives.SliderValueFromX(_osdFontSliderRect, x) / 100f;
                double v = Math.Round(0.7 + t * (2.0 - 0.7), 2);
                if (Math.Abs(cfg.OsdFontScale - v) > 0.005)
                {
                    cfg.OsdFontScale = v;
                    _actions.Invalidate();
                }
                break;
            }
            case "osdFin":
            {
                float t = UiPrimitives.SliderValueFromX(_osdFadeInSliderRect, x) / 100f;
                int v = Math.Clamp((int)MathF.Round(t * 50f) * 10, 0, 500);
                if (cfg.OsdFadeInMs != v)
                {
                    cfg.OsdFadeInMs = v;
                    _actions.Invalidate();
                }
                break;
            }
            case "osdFout":
            {
                float t = UiPrimitives.SliderValueFromX(_osdFadeOutSliderRect, x) / 100f;
                int v = Math.Clamp((int)MathF.Round(t * 20f) * 50, 0, 1000);
                if (cfg.OsdFadeOutMs != v)
                {
                    cfg.OsdFadeOutMs = v;
                    _actions.Invalidate();
                }
                break;
            }
            case "panelH":
            {
                float t = UiPrimitives.SliderValueFromX(_panelHeightSliderRect, x) / 100f;
                int v = Math.Clamp(350 + (int)MathF.Round(t * 50f) * 10, 350, 850);
                if (cfg.QuickPanelHeight != v)
                {
                    cfg.QuickPanelHeight = v;
                    _actions.Invalidate();
                }
                break;
            }
        }
    }

    private void CommitHex(string text)
    {
        if (UiColor.TryParseHex(text, out var col))
        {
            if (col.WithAlpha(255) != _actions.Theme.Accent) ApplyAccent(col.ToHexRgb());
        }
        else
        {
            _actions.ShowNotice(L10n.T("Th.InvalidHex"));
        }
    }

    private void OpenColorPicker()
    {
        if (_pickerWindow is { IsVisible: true })
        {
            _pickerWindow.RequestFocus();
            return;
        }

        _pickerWindow = ColorPickerWindow.Open(
            _actions.OwnerWindow,
            _actions.Theme,
            _actions.Theme.Accent,
            chosen => ApplyAccent(chosen.ToHexRgb()));
    }

    private void ResetOsdSettings()
    {
        var cfg = _actions.Config;
        cfg.OsdPosition = "TR";
        cfg.OsdOffsetX = 0;
        cfg.OsdOffsetY = 0;
        cfg.OsdCustomX = -1;
        cfg.OsdCustomY = -1;
        cfg.OsdCustomPhysicalX = null;
        cfg.OsdCustomPhysicalY = null;
        cfg.OsdWidth = 240;
        cfg.OsdFontScale = 1.0;
        cfg.OsdFadeInMs = 100;
        cfg.OsdFadeOutMs = 200;
        if (!SaveAndNotifySettings()) return;
        _actions.Host.PreviewOsd();
        _actions.ShowNotice(L10n.T("Exp.OsdResetDone"));
    }

    private void ApplyAccent(string value)
    {
        var cfg = _actions.Config;
        string previous = cfg.Accent;
        if (string.Equals(previous, value, StringComparison.OrdinalIgnoreCase)) return;
        cfg.Accent = value;
        if (_actions.CommitSettings()) _actions.NotifyThemeChanged();
    }

    private void SaveAndNotifyTheme()
    {
        if (_actions.CommitSettings()) _actions.NotifyThemeChanged();
    }

    private bool SaveAndNotifySettings()
    {
        if (!_actions.CommitSettings()) return false;
        _actions.NotifySettingsChanged();
        return true;
    }

    public void Dispose()
    {
        if (_pickerWindow is { IsVisible: true }) _pickerWindow.CloseWindow();
        _pickerWindow = null;
    }
}

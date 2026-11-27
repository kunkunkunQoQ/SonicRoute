#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed partial class PhoneDevicePageView
{
    private static float PhoneTextHeight(string value, float width, float size = 12f) =>
        MathF.Max(size * 1.35f, D2dSharedContext.Instance.MeasureText(value, UiMetrics.FontFamily, size, 400,
            MathF.Max(1f, width), 1024f, wrap: true).Height);

    private static float PhoneTextWidth(string value, float size = 12f, int weight = 400) =>
        MathF.Ceiling(D2dSharedContext.Instance.MeasureText(value, UiMetrics.FontFamily, size, weight, 1600f, 100f, false).Width);

    private void DrawOverlayAction(D2dTarget g, UiThemePalette theme, string id, UiRect rect, string label, bool enabled)
    {
        if (enabled) _hit.Add(id, rect, UiCursorKind.Hand);
        bool focused = enabled && _hit.ShowFocus && _hit.FocusId == id;
        float opacity = enabled ? 1f : 0.5f;
        var fill = enabled && _hit.IsHover(id) ? theme.NavHover : theme.Overlay;
        g.FillRoundedRect(rect, 8f, fill.WithAlphaFraction(opacity));
        g.DrawRoundedRect(rect, 8f, (focused ? theme.Accent : theme.Border).WithAlphaFraction(opacity), 1f);
        g.DrawText(label, rect.Deflate(10f, 0f), theme.TextSecondary.WithAlphaFraction(opacity),
            12f, 400, UiTextAlign.Center, UiVertAlign.Center);
    }

    private float RenderConnectionView(D2dTarget g, UiThemePalette t, float x, float y, float w)
    {
        float androidWidth = PhoneTextWidth("Android", 13f) + 42f;
        float airPlayWidth = PhoneTextWidth("AirPlay", 13f) + 42f;
        float appWidth = PhoneTextWidth("SonicRoute App", 13f) + 42f;
        UiPrimitives.DrawRadioButton(g, t, _hit, "phone.connchoice.android", new UiRect(x, y, androidWidth, 28f),
            !_airPlayConnectionChoice && !_appConnectionChoice, "Android", 13f);
        bool wrapMethods = w < androidWidth + airPlayWidth + appWidth;
        UiPrimitives.DrawRadioButton(g, t, _hit, "phone.connchoice.app",
            new UiRect(wrapMethods ? x : x + androidWidth + airPlayWidth, wrapMethods ? y + 28f : y, appWidth, 28f),
            _appConnectionChoice, "SonicRoute App", 13f);
        UiPrimitives.DrawRadioButton(g, t, _hit, "phone.connchoice.airplay", new UiRect(x + androidWidth, y, airPlayWidth, 28f), _airPlayConnectionChoice, "AirPlay", 13f);
        if (wrapMethods) y += 28f;
        y += 36f;

        if (_appConnectionChoice) return RenderAppConnection(g, t, x, y, w);

        if (_airPlayConnectionChoice)
        {
            var bounds = new UiRect(x, y, w, _airPlay.MeasureHeight(w - 26f, showHeaderStatus: true) + 26f);
            UiPrimitives.DrawCard(g, t, bounds, 12f);
            _airPlay.Render(g, t, _hit, bounds.Deflate(13f), showHeaderStatus: true);
            return bounds.Bottom + 10f;
        }

        const float pad = 13f, qrSide = 166f;
        float inner = MathF.Max(1f, w - 2 * pad);
        bool stacked = inner < 350f;
        float left = stacked ? inner : MathF.Max(182f, inner * 0.9f / 1.9f) - 16f;
        float right = stacked ? inner : inner - left - 16f;
        string qrGuide = L10n.T("Phone.QrPairSteps"), codeGuide = L10n.T("Phone.CodePairSteps");
        string qrHint = L10n.T(_qrRetry ? "Phone.QrRetryHint" : "Phone.QrRefreshHint");
        float qGuideH = PhoneTextHeight(qrGuide, left), codeGuideH = PhoneTextHeight(codeGuide, right);
        float qHintH = PhoneTextHeight(qrHint, left);
        float qrH = 20f + 4f + qGuideH + 8f + qrSide + 8f + qHintH;
        string addrLabel = L10n.T("Phone.PairingAddressLabel"), codeLabel = L10n.T("Phone.PairingCodeLabel");
        float addrH = PhoneTextHeight(addrLabel, right), codeH = PhoneTextHeight(codeLabel, right);
        float codeColumnH = 20f + 4f + codeGuideH + 8f + addrH + 6f + 32f + 8f + codeH + 6f + 38f + 8f + 28f;
        float cardH = (stacked ? qrH + 14f + codeColumnH : MathF.Max(qrH, codeColumnH)) + 2 * pad;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card, 12f);
        float qx = x + pad, qy = y + pad;
        g.DrawText(L10n.T("Phone.QrPairTitle"), new UiRect(qx, qy, left, 20f), t.TextPrimary, 14f, 600);
        g.DrawText(qrGuide, new UiRect(qx, qy + 24f, left, qGuideH), t.TextSecondary, 12f, wrap: true);
        var qr = new UiRect(qx + (left - qrSide) / 2f, qy + 32f + qGuideH, qrSide, qrSide);
        if (CanShowQr) _hit.Add("phone.qrrefresh", qr, UiCursorKind.Hand, L10n.T("Phone.QrRefreshHint"));
        g.FillRoundedRect(qr, 7f, UiColor.White);
        g.DrawRoundedRect(qr, 7f, _hit.IsHover("phone.qrrefresh") ? t.Accent : t.Border, 1f);
        if (_qrBgra != null && _qrSizePx > 0)
            g.DrawBgraBitmap("phone.qr", _qrBgra, _qrSizePx, _qrSizePx, qr.Deflate(1f), version: _qrVersion, nearestNeighbor: true);
        else
            g.DrawText(L10n.T("Phone.ProcessingStatus"), qr, UiColor.FromRgb(0x68, 0x70, 0x80), 12f, 400, UiTextAlign.Center, UiVertAlign.Center);
        g.DrawText(qrHint, new UiRect(qx, qr.Bottom + 8f, left, qHintH), t.TextSecondary, 12f, 400, UiTextAlign.Center, wrap: true);

        float cx = stacked ? qx : qx + left + 16f;
        float cy = stacked ? qy + qrH + 14f : qy;
        g.DrawText(L10n.T("Phone.CodePairTitle"), new UiRect(cx, cy, right, 20f), t.TextPrimary, 14f, 600);
        g.DrawText(codeGuide, new UiRect(cx, cy + 24f, right, codeGuideH), t.TextSecondary, 12f, wrap: true);
        cy += 32f + codeGuideH;
        g.DrawText(addrLabel, new UiRect(cx, cy, right, addrH), t.TextPrimary, 12f, wrap: true);
        var address = new UiRect(cx, cy + addrH + 6f, right, 32f);
        UiPrimitives.DrawTextBox(g, t, _hit, "phone.pairaddr", address, _pairAddress, focused: _actions.ActiveInlineEditTag == "phone.pairaddr", enabled: !_busy);
        cy = address.Bottom + 8f;
        g.DrawText(codeLabel, new UiRect(cx, cy, right, codeH), t.TextPrimary, 12f, wrap: true);
        var code = new UiRect(cx, cy + codeH + 6f, right, 38f);
        UiPrimitives.DrawTextBox(g, t, _hit, "phone.paircode", code, _pairCode, focused: _actions.ActiveInlineEditTag == "phone.paircode", mono: true, fontSize: 18f, enabled: !_busy);
        DrawOverlayAction(g, t, "phone.pairbtn", new UiRect(cx, code.Bottom + 8f, right, 28f),
            L10n.T("Phone.PairAndConnect"), enabled: !_busy);

        string directGuide = L10n.T("Phone.DirectConnectDesc"), searchText = L10n.T("Phone.SearchSavedDevices"), connectText = L10n.T("Phone.ConnectShort");
        float directHintH = PhoneTextHeight(directGuide, inner);
        string? searchStatus = _savedSearchStatus == null ? null : L10n.T(_savedSearchStatus);
        float searchH = searchStatus == null ? 0f : 6f + PhoneTextHeight(searchStatus, inner);
        float searchW = MathF.Min(inner * 0.42f, PhoneTextWidth(searchText, 11f) + 18f);
        float connectW = MathF.Min(inner * 0.28f, PhoneTextWidth(connectText, 11f) + 18f);
        var direct = new UiRect(x, card.Bottom + 10f, w, 2 * pad + 18f + 4f + directHintH + 8f + 32f + searchH + 40f * _restorableUsb.Length);
        UiPrimitives.DrawCard(g, t, direct, 12f);
        float dy = direct.Y + pad;
        g.DrawText(L10n.T("Phone.DirectConnectTitle"), new UiRect(qx, dy, inner, 18f), t.TextPrimary, 13f, 600);
        g.DrawText(directGuide, new UiRect(qx, dy + 22f, inner, directHintH), t.TextSecondary, 12f, wrap: true);
        var manual = new UiRect(qx, dy + 30f + directHintH, MathF.Max(1f, inner - searchW - connectW - 16f), 32f);
        if (_savedChoice == null)
            UiPrimitives.DrawTextBox(g, t, _hit, "phone.connaddr", manual, _connectAddress, focused: _actions.ActiveInlineEditTag == "phone.connaddr", enabled: !_busy);
        else
            UiPrimitives.DrawComboBox(g, t, _hit, "phone.saved", manual, SavedLabel(_savedChoice), enabled: !_busy);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.searchsaved", new UiRect(manual.Right + 8f, manual.Y, searchW, 32f),
            searchText, fontSize: 11f, enabled: !_busy && !_manualSearch);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.connbtn", new UiRect(direct.Right - pad - connectW, manual.Y, connectW, 32f),
            connectText, fontSize: 11f, enabled: !_busy && (!_manualSearch || _savedChoice == null));
        dy = manual.Bottom;
        if (searchStatus != null)
            g.DrawText(searchStatus, new UiRect(qx, dy + 6f, inner, searchH - 6f), t.TextSecondary, 12f, wrap: true);
        dy += searchH;
        foreach (var device in _restorableUsb)
        {
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.restoreusb:" + AndroidDeviceIdentity.Key(device),
                new UiRect(qx, dy + 8f, inner, 32f), string.Format(L10n.T("Phone.RestoreUsbDevice"), device.Name), enabled: !_busy, fontSize: 11f);
            dy += 40f;
        }
        return direct.Bottom + 10f;
    }
}
#endif

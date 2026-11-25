using System;
using System.Globalization;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed class ColorPickerWindow : LayeredWindow
{
    private const int SvRes = 128;

    private static readonly UiColor[] Presets =
    [
        UiColor.FromRgb(0x2F, 0x80, 0xED),
        UiColor.FromRgb(0x22, 0xC5, 0x5E),
        UiColor.FromRgb(0xEC, 0x48, 0x99),
        UiColor.FromRgb(0x8B, 0x5C, 0xF6),
        UiColor.FromRgb(0xF5, 0x9E, 0x0B),
        UiColor.FromRgb(0xEF, 0x44, 0x44),
        UiColor.FromRgb(0x06, 0xB6, 0xD4),
        UiColor.FromRgb(0x10, 0xB9, 0x81),
        UiColor.FromRgb(0x63, 0x66, 0xF1),
        UiColor.FromRgb(0xF9, 0x73, 0x16),
    ];

    private readonly UiThemePalette _theme;
    private readonly UiColor _initialColor;
    private readonly Action<UiColor> _onAccepted;
    private readonly Action<UiColor>? _onLiveChanged;
    private readonly Action? _onCancelled;
    private readonly UiHitMap _hit = new();
    private readonly InlineEditHost _inlineEdit = new();

    private double _hue;       // 0..360
    private double _sat;       // 0..1
    private double _val;       // 0..1
    private UiColor _selected;

    private readonly byte[] _svPixels = new byte[SvRes * SvRes * 4];
    private double _svRenderedHue = -1;
    private int _svVersion;

    private UiRect _svRect;
    private UiRect _hueRect;
    private bool _draggingSv;
    private bool _draggingHue;

    private UiRect _hexRect;
    private UiRect _rRect;
    private UiRect _gRect;
    private UiRect _bRect;

    private bool _hexError;
    private bool _accepted;
    private bool _reverted;

    internal ColorPickerWindow(
        LayeredWindow owner,
        UiThemePalette theme,
        UiColor initialColor,
        Action<UiColor> onAccepted,
        Action<UiColor>? onLiveChanged = null,
        Action? onCancelled = null)
        : base(340f, 360f, 340f, 360f)
    {
        _theme = theme;
        _initialColor = initialColor.WithAlpha(255);
        _onAccepted = onAccepted;
        _onLiveChanged = onLiveChanged;
        _onCancelled = onCancelled;
        TopMost = true;
        ToolWindow = true;
        DestroyOnClose = true;
        OwnerHandle = owner.Handle;

        SetColorFromRgb(_initialColor, updateHsv: true, notifyLive: false);

        _inlineEdit.Committed += OnInlineCommitted;
    }

    public static ColorPickerWindow Open(
        LayeredWindow owner,
        UiThemePalette theme,
        UiColor initialColor,
        Action<UiColor> onAccepted,
        Action<UiColor>? onLiveChanged = null,
        Action? onCancelled = null)
    {
        var win = new ColorPickerWindow(owner, theme, initialColor, onAccepted, onLiveChanged, onCancelled);
        win.EnsureCreated(L10n.T("Th.ChooseColor"));

        GetMonitorWorkAreaForWindow(owner.Handle, out int ml, out int mt, out int mr, out int mb, out uint monDpi);
        float scale = monDpi / 96f;
        int wPx = (int)MathF.Round(340f * scale);
        int hPx = (int)MathF.Round(360f * scale);
        int cx = owner.LeftPx + (owner.WidthPx - wPx) / 2;
        int cy = owner.TopPx + (owner.HeightPx - hPx) / 2;
        cx = Math.Clamp(cx, ml + 8, Math.Max(ml + 8, mr - wPx - 8));
        cy = Math.Clamp(cy, mt + 8, Math.Max(mt + 8, mb - hPx - 8));
        win.MovePx(cx, cy);
        win.Show(activate: true);
        return win;
    }

    protected override string GetWindowTitle() => L10n.T("Th.ChooseColor");

    protected override void OnHidden()
    {
        _inlineEdit.Close(commit: false, viaEnter: false);
        RevertIfUnaccepted();
        ReleaseRenderResources();
    }

    protected override void OnClosed()
    {
        RevertIfUnaccepted();
        _inlineEdit.Committed -= OnInlineCommitted;
        _inlineEdit.Dispose();
        base.OnClosed();
    }

    protected override void OnCaptureLost()
    {
        _draggingSv = false;
        _draggingHue = false;
        _hit.ActiveId = null;
        Invalidate();
    }

    protected override void OnMoved(int leftPx, int topPx) => RepositionEdit();

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        _hit.Clear();
        var outer = new UiRect(0.5f, 0.5f, widthDip - 1f, heightDip - 1f);
        g.FillRoundedRect(outer, 12f, _theme.SurfaceBg);
        g.DrawRoundedRect(outer, 12f, _theme.Border, 1f);

        float pad = 16f;
        float contentW = widthDip - pad * 2f;

        g.DrawText(L10n.T("Th.ChooseColor"), new UiRect(pad, 12f, contentW - 28f, 22f), _theme.TextPrimary, 14f, 600);
        UiPrimitives.DrawGhostIconButton(g, _theme, _hit, "cp.close", new UiRect(widthDip - pad - 24f, 11f, 24f, 24f), UiIcon.Close, 13f);

        _svRect = new UiRect(pad, 42f, contentW, 136f);
        _hit.Add("cp.sv", _svRect, UiCursorKind.Hand);
        EnsureSvBitmap();
        g.PushClip(_svRect);
        g.DrawBgraBitmap("cp.sv", _svPixels, SvRes, SvRes, _svRect, 1f, _svVersion);
        g.PopClip();
        g.DrawRoundedRect(_svRect, 4f, _theme.Border, 1f);

        float mx = _svRect.X + (float)_sat * _svRect.Width;
        float my = _svRect.Y + (float)(1.0 - _val) * _svRect.Height;
        g.DrawEllipse(mx, my, 6f, 6f, UiColor.Black, 2.2f);
        g.DrawEllipse(mx, my, 6f, 6f, UiColor.White, 1.4f);

        _hueRect = new UiRect(pad, _svRect.Bottom + 10f, contentW, 16f);
        _hit.Add("cp.hue", _hueRect, UiCursorKind.Hand);
        g.FillHueSpectrumRect(_hueRect, 4f);
        g.DrawRoundedRect(_hueRect, 4f, _theme.Border, 1f);
        float hx = _hueRect.X + (float)(_hue / 360.0) * _hueRect.Width;
        g.FillRoundedRect(new UiRect(hx - 3f, _hueRect.Y - 2f, 6f, _hueRect.Height + 4f), 3f, UiColor.White);
        g.DrawRoundedRect(new UiRect(hx - 3f, _hueRect.Y - 2f, 6f, _hueRect.Height + 4f), 3f, UiColor.Black, 1f);

        float rowY = _hueRect.Bottom + 12f;
        var swatchRect = new UiRect(pad, rowY, 44f, 44f);
        g.FillRoundedRect(swatchRect, 8f, _selected);
        g.DrawRoundedRect(swatchRect, 8f, _theme.Border, 1f);

        float inputLeft = swatchRect.Right + 10f;
        float rightW = widthDip - pad - inputLeft;
        float hexW = 86f;
        float chGap = 6f;
        float chW = (rightW - hexW - chGap * 3f) / 3f;

        g.DrawText("Hex", new UiRect(inputLeft, rowY - 1f, hexW, 14f), _hexError ? _theme.Fail : _theme.TextSecondary, 10.5f, 600);
        _hexRect = new UiRect(inputLeft, rowY + 15f, hexW, 28f);
        UiPrimitives.DrawTextBox(g, _theme, _hit, "cp.hex", _hexRect, _selected.ToHexRgb(), "", _inlineEdit.ActiveTag == "cp.hex", mono: true, fontSize: 12f);
        if (_hexError) g.DrawRoundedRect(_hexRect, 6f, _theme.Fail, 1.2f);

        float rx = _hexRect.Right + chGap;
        g.DrawText(L10n.T("Color.Red"), new UiRect(rx, rowY - 1f, chW, 14f), _theme.TextSecondary, 10.5f, 600);
        _rRect = new UiRect(rx, rowY + 15f, chW, 28f);
        UiPrimitives.DrawTextBox(g, _theme, _hit, "cp.r", _rRect, _selected.R.ToString(CultureInfo.InvariantCulture), "", _inlineEdit.ActiveTag == "cp.r", mono: true, fontSize: 12f);

        float gx = _rRect.Right + chGap;
        g.DrawText(L10n.T("Color.Green"), new UiRect(gx, rowY - 1f, chW, 14f), _theme.TextSecondary, 10.5f, 600);
        _gRect = new UiRect(gx, rowY + 15f, chW, 28f);
        UiPrimitives.DrawTextBox(g, _theme, _hit, "cp.g", _gRect, _selected.G.ToString(CultureInfo.InvariantCulture), "", _inlineEdit.ActiveTag == "cp.g", mono: true, fontSize: 12f);

        float bx = _gRect.Right + chGap;
        g.DrawText(L10n.T("Color.Blue"), new UiRect(bx, rowY - 1f, chW, 14f), _theme.TextSecondary, 10.5f, 600);
        _bRect = new UiRect(bx, rowY + 15f, chW, 28f);
        UiPrimitives.DrawTextBox(g, _theme, _hit, "cp.b", _bRect, _selected.B.ToString(CultureInfo.InvariantCulture), "", _inlineEdit.ActiveTag == "cp.b", mono: true, fontSize: 12f);

        float presetY = rowY + 56f;
        float swGap = 6f;
        float swSize = (contentW - swGap * (Presets.Length - 1)) / Presets.Length;
        for (int i = 0; i < Presets.Length; i++)
        {
            var p = Presets[i];
            var pr = new UiRect(pad + i * (swSize + swGap), presetY, swSize, swSize);
            string id = "cp.preset." + i;
            _hit.Add(id, pr, UiCursorKind.Hand, p.ToHexRgb());
            g.FillRoundedRect(pr, 5f, p);
            bool sel = p.R == _selected.R && p.G == _selected.G && p.B == _selected.B;
            g.DrawRoundedRect(pr, 5f, sel ? _theme.TextPrimary : _theme.Border, sel ? 1.8f : 1f);
        }

        float btnY = heightDip - pad - 32f;
        float btnW = 92f;
        var cancelRect = new UiRect(widthDip - pad - btnW, btnY, btnW, 32f);
        var okRect = new UiRect(cancelRect.X - 10f - btnW, btnY, btnW, 32f);
        UiPrimitives.DrawAccentButton(g, _theme, _hit, "cp.ok", okRect, L10n.T("Color.Confirm"));
        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "cp.cancel", cancelRect, L10n.T("Auto.Cancel"));
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        if (leftDown && _draggingSv)
        {
            PickSv(xDip, yDip);
            Invalidate();
            return;
        }
        if (leftDown && _draggingHue)
        {
            PickHue(xDip);
            Invalidate();
            return;
        }

        bool changed = _hit.UpdateHover(xDip, yDip);
        if (_hit.Hovered is { } hit) SetCursorKind(hit.Cursor);
        else SetCursorKind(UiCursorKind.Arrow);
        if (changed) Invalidate();
    }

    protected override void OnPointerDown(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return;
        if (_inlineEdit.IsOpen) _inlineEdit.Close(commit: true, viaEnter: false);

        var hit = _hit.HitTest(e.X, e.Y);
        _hit.ActiveId = hit?.Id;
        if (hit == null) return;

        if (hit.Value.Id == "cp.sv")
        {
            _draggingSv = true;
            PickSv(e.X, e.Y);
            Invalidate();
        }
        else if (hit.Value.Id == "cp.hue")
        {
            _draggingHue = true;
            PickHue(e.X);
            Invalidate();
        }
    }

    protected override void OnPointerUp(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return;
        string? activeId = _hit.ActiveId;
        _hit.ActiveId = null;

        if (_draggingSv || _draggingHue)
        {
            _draggingSv = false;
            _draggingHue = false;
            Invalidate();
            return;
        }

        var hit = _hit.HitTest(e.X, e.Y);
        if (hit == null || hit.Value.Id != activeId) return;

        switch (hit.Value.Id)
        {
            case "cp.close":
            case "cp.cancel":
                CancelAndClose();
                return;
            case "cp.ok":
                AcceptAndClose();
                return;
            case "cp.hex":
                _hexError = false;
                BeginEdit("cp.hex", _hexRect, _selected.ToHexRgb());
                return;
            case "cp.r":
                BeginEdit("cp.r", _rRect, _selected.R.ToString(CultureInfo.InvariantCulture));
                return;
            case "cp.g":
                BeginEdit("cp.g", _gRect, _selected.G.ToString(CultureInfo.InvariantCulture));
                return;
            case "cp.b":
                BeginEdit("cp.b", _bRect, _selected.B.ToString(CultureInfo.InvariantCulture));
                return;
            default:
                if (hit.Value.Id.StartsWith("cp.preset.", StringComparison.Ordinal) &&
                    int.TryParse(hit.Value.Id.AsSpan("cp.preset.".Length), out int idx) &&
                    idx >= 0 && idx < Presets.Length)
                {
                    _hexError = false;
                    SetColorFromRgb(Presets[idx], updateHsv: true, notifyLive: true);
                    Invalidate();
                }
                return;
        }
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (vk == 0x1B /*VK_ESCAPE*/)
        {
            if (_inlineEdit.IsOpen) _inlineEdit.Close(commit: false, viaEnter: false);
            else CancelAndClose();
            return true;
        }
        if (vk == 0x0D /*VK_RETURN*/)
        {
            AcceptAndClose();
            return true;
        }
        return false;
    }

    private void AcceptAndClose()
    {
        if (_inlineEdit.IsOpen) _inlineEdit.Close(commit: true, viaEnter: true);
        if (_accepted) return;
        _accepted = true;
        var chosen = _selected;
        CloseWindow();
        _onAccepted(chosen);
    }

    private void CancelAndClose()
    {
        if (_inlineEdit.IsOpen) _inlineEdit.Close(commit: false, viaEnter: false);
        RevertIfUnaccepted();
        CloseWindow();
    }

    private void RevertIfUnaccepted()
    {
        if (_accepted || _reverted) return;
        _reverted = true;
        _onLiveChanged?.Invoke(_initialColor);
        _onCancelled?.Invoke();
    }

    private void BeginEdit(string tag, UiRect rect, string initial)
    {
        if (Handle == 0) return;
        var screen = DipToScreenPx(rect.X, rect.Y);
        _inlineEdit.Open(
            Handle,
            tag,
            initial,
            screen.X,
            screen.Y,
            Math.Max(24, (int)MathF.Round(rect.Width * DpiScale)),
            Math.Max(20, (int)MathF.Round(rect.Height * DpiScale)),
            12f,
            Dpi,
            _theme.SurfaceBg,
            _theme.TextPrimary,
            mono: true,
            password: false,
            selectAll: true);
        Invalidate();
    }

    private void RepositionEdit()
    {
        if (!_inlineEdit.IsOpen || Handle == 0) return;
        UiRect r = _inlineEdit.ActiveTag switch
        {
            "cp.hex" => _hexRect,
            "cp.r" => _rRect,
            "cp.g" => _gRect,
            "cp.b" => _bRect,
            _ => default,
        };
        if (r.IsEmpty) return;
        var pt = DipToScreenPx(r.X, r.Y);
        _inlineEdit.Reposition(pt.X, pt.Y, Math.Max(24, (int)MathF.Round(r.Width * DpiScale)), Math.Max(20, (int)MathF.Round(r.Height * DpiScale)), 12f, Dpi);
    }

    private void OnInlineCommitted(string tag, string text, bool viaEnter)
    {
        string trimmed = (text ?? "").Trim();
        if (tag == "cp.hex")
        {
            if (UiColor.TryParseHex(trimmed, out var col))
            {
                _hexError = false;
                SetColorFromRgb(col.WithAlpha(255), updateHsv: true, notifyLive: true);
            }
            else if (!string.IsNullOrEmpty(trimmed))
            {
                _hexError = true;
            }
        }
        else if (byte.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out byte ch))
        {
            UiColor next = tag switch
            {
                "cp.r" => UiColor.FromRgb(ch, _selected.G, _selected.B),
                "cp.g" => UiColor.FromRgb(_selected.R, ch, _selected.B),
                "cp.b" => UiColor.FromRgb(_selected.R, _selected.G, ch),
                _ => _selected,
            };
            _hexError = false;
            SetColorFromRgb(next, updateHsv: true, notifyLive: true);
        }
        Invalidate();
    }

    private void PickSv(float xDip, float yDip)
    {
        _hexError = false;
        _sat = Math.Clamp((xDip - _svRect.X) / MathF.Max(1f, _svRect.Width), 0f, 1f);
        _val = 1.0 - Math.Clamp((yDip - _svRect.Y) / MathF.Max(1f, _svRect.Height), 0f, 1f);
        _selected = HsvToRgb(_hue, _sat, _val);
        _onLiveChanged?.Invoke(_selected);
    }

    private void PickHue(float xDip)
    {
        _hexError = false;
        _hue = Math.Clamp((xDip - _hueRect.X) / MathF.Max(1f, _hueRect.Width), 0f, 1f) * 360.0;
        _selected = HsvToRgb(_hue, _sat, _val);
        _onLiveChanged?.Invoke(_selected);
    }

    private void SetColorFromRgb(UiColor rgb, bool updateHsv, bool notifyLive)
    {
        _selected = rgb.WithAlpha(255);
        if (updateHsv) RgbToHsv(_selected, ref _hue, out _sat, out _val);
        if (notifyLive) _onLiveChanged?.Invoke(_selected);
    }

    private void EnsureSvBitmap()
    {
        if (Math.Abs(_svRenderedHue - _hue) < 0.1 && _svVersion > 0) return;
        _svRenderedHue = _hue;
        _svVersion++;

        for (int py = 0; py < SvRes; py++)
        {
            double v = 1.0 - (double)py / (SvRes - 1);
            for (int px = 0; px < SvRes; px++)
            {
                double s = (double)px / (SvRes - 1);
                var c = HsvToRgb(_hue, s, v);
                int idx = (py * SvRes + px) * 4;
                _svPixels[idx + 0] = c.B;
                _svPixels[idx + 1] = c.G;
                _svPixels[idx + 2] = c.R;
                _svPixels[idx + 3] = 255;
            }
        }
    }

    private static UiColor HsvToRgb(double hue, double sat, double val)
    {
        double h = ((hue % 360.0) + 360.0) % 360.0 / 60.0;
        double c = val * sat;
        double x = c * (1.0 - Math.Abs(h % 2.0 - 1.0));
        double m = val - c;
        double r, g, b;
        if (h < 1.0) { r = c; g = x; b = 0; }
        else if (h < 2.0) { r = x; g = c; b = 0; }
        else if (h < 3.0) { r = 0; g = c; b = x; }
        else if (h < 4.0) { r = 0; g = x; b = c; }
        else if (h < 5.0) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return UiColor.FromRgb(
            (byte)Math.Clamp((int)Math.Round((r + m) * 255.0), 0, 255),
            (byte)Math.Clamp((int)Math.Round((g + m) * 255.0), 0, 255),
            (byte)Math.Clamp((int)Math.Round((b + m) * 255.0), 0, 255));
    }

    private static void RgbToHsv(UiColor c, ref double hue, out double sat, out double val)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        val = max;
        sat = max == 0 ? 0 : d / max;
        if (d == 0) return;
        hue = 60.0 * (max == r ? (g - b) / d : max == g ? (b - r) / d + 2.0 : (r - g) / d + 4.0);
        if (hue < 0) hue += 360.0;
    }
}

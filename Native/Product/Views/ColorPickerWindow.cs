using System;
using System.Globalization;
using System.Runtime.InteropServices;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed class ColorPickerWindow : LayeredWindow
{
    private const float PickerWidth = 720f;
    private const float PickerHeight = 494f;
    private static readonly UiColor[] Presets = CreatePresets();
    private static readonly string[] ChannelIds = ["cp.r", "cp.g", "cp.b", "cp.h", "cp.s", "cp.l", "cp.hex"];
    private static readonly string[] LabelKeys = ["Color.Red", "Color.Green", "Color.Blue", "Color.Hue", "Color.Saturation", "Color.Lightness"];
    private UiThemePalette _theme;
    private readonly LayeredWindow _owner;
    private readonly Action<UiColor> _onAccepted;
    private readonly UiHitMap _hit = new();
    private readonly InlineEditHost _inlineEdit = new();
    private readonly UiRect[] _channels = new UiRect[7];
    private readonly string[] _values = new string[7];
    private UiRect _spectrum, _lightBar;
    private bool _disabledOwner, _finished;
    private double _hue, _saturation, _lightness;
    private UiColor _selected;
    private string? _drag, _arrowTarget;

    private ColorPickerWindow(LayeredWindow owner, UiThemePalette theme, UiColor initial, Action<UiColor> accepted)
        : base(PickerWidth, PickerHeight, 220, 220)
    {
        _owner = owner;
        _theme = theme;
        _onAccepted = accepted;
        ToolWindow = DestroyOnClose = true;
        OwnerHandle = owner.Handle;
        SelectRgb(initial);
        _inlineEdit.Committed += CommitField;
    }

    public static ColorPickerWindow Open(LayeredWindow owner, UiThemePalette theme, UiColor initialColor, Action<UiColor> onAccepted)
    {
        var win = new ColorPickerWindow(owner, theme, initialColor, onAccepted);
        GetMonitorWorkAreaForWindow(owner.Handle, out int l, out int t, out int r, out int b, out uint dpi);
        float scale = dpi / 96f;
        int w = (int)MathF.Round(PickerWidth * scale), h = (int)MathF.Round(PickerHeight * scale);
        int x = Math.Clamp(owner.LeftPx + (owner.WidthPx - w) / 2, l, Math.Max(l, r - w));
        int y = Math.Clamp(owner.TopPx + (owner.HeightPx - h) / 2, t, Math.Max(t, b - h));
        win.MovePx(x, y);
        win.EnsureCreated(L10n.T("Th.ChooseColor"));
        try
        {
            win._disabledOwner = IsWindowEnabled(owner.Handle);
            if (win._disabledOwner) EnableWindow(owner.Handle, false);
            win.Show(activate: true);
        }
        catch { win.Dispose(); throw; }
        return win;
    }

    internal override void ApplyTheme(UiThemePalette theme)
    {
        _theme = theme;
        if (_inlineEdit.IsOpen) _inlineEdit.SetTheme(theme.SurfaceBg, theme.TextPrimary);
        Invalidate();
    }

    protected override UiHitMap InputMap => _hit;
    protected override string GetWindowTitle() => L10n.T("Th.ChooseColor");
    protected override int HitTestNonClient(float xDip, float yDip) =>
        yDip < 34f && xDip < WidthDip - 52f ? 2 /* HTCAPTION */ : 1;

    protected override void OnClosed()
    {
        _inlineEdit.Committed -= CommitField;
        _inlineEdit.Dispose();
        _hit.Clear();
        _spectrumPixels = [];
        if (_disabledOwner && _owner.Handle != 0)
        {
            EnableWindow(_owner.Handle, true);
            if (_owner.IsVisible) _owner.RequestFocus();
        }
        base.OnClosed();
    }

    protected override void OnCaptureLost()
    {
        _drag = null;
        _hit.ActiveId = null;
        Invalidate();
    }

    protected override void OnDpiChanged(uint newDpi) => Invalidate();

    protected override void OnRender(D2dTarget g, float w, float h)
    {
        _hit.Clear();
        var surface = new UiRect(0.5f, 0.5f, w - 1, h - 1);
        g.FillRoundedRect(surface, 8f, _theme.SurfaceBg);
        g.DrawRoundedRect(surface, 8f, _theme.Border, 1f);
        g.DrawText(GetWindowTitle(), new UiRect(16, 0, w - 64, 34), _theme.TextPrimary, 14, 600);
        UiPrimitives.DrawGhostIconButton(g, _theme, _hit, "cp.close", new UiRect(w - 46, 1, 45, 32), UiIcon.Close, 13);
        const float left = 27, right = left + 336 + 24, firstY = 79;
        g.DrawText(L10n.T("Color.Basic"), new UiRect(left + 2, 48, 334, 22), _theme.TextPrimary, 13, 600);
        for (int i = 0; i < Presets.Length; i++)
        {
            var swatch = Presets[i];
            var rect = new UiRect(left + 2 + i % 12 * 28, firstY + 2 + i / 12 * 28, 24, 24);
            string id = SwatchIds[i];
            _hit.Add(id, rect, UiCursorKind.Hand, SwatchLabels[i]);
            g.FillRoundedRect(rect, 4f, _hit.IsHover(id) ? swatch.WithAlphaFraction(0.8f) : swatch);
            bool selected = _selected == swatch;
            g.DrawRoundedRect(rect, 4f, _theme.Border, selected ? 2f : 1f);
            if (selected)
            {
                var ink = swatch.R * .299 + swatch.G * .587 + swatch.B * .114 > 150 ? UiColor.Black : UiColor.White;
                g.DrawText("✓", rect, ink, 12, 600, UiTextAlign.Center, UiVertAlign.Center);
            }
        }
        var preview = new UiRect(left, firstY + 238, 60, 34);
        g.FillRoundedRect(preview, 8, _selected);
        _channels[6] = new UiRect(left + 70, preview.Y, 266, 34);
        DrawField(g, 6);
        DrawChannels(g, 0, left, preview.Bottom + 12, 336);
        g.DrawText(L10n.T("Color.Spectrum"), new UiRect(right, 48, w - right - left, 22), _theme.TextPrimary, 13, 600);
        _spectrum = new UiRect(right, firstY, w - left - right - 44, 220);
        _hit.Add("cp.spectrum", _spectrum, UiCursorKind.Hand, L10n.T("Color.Spectrum"));
        g.DrawBgraBitmap("cp.spectrum", _spectrumPixels, SpectrumWidth, SpectrumHeight, _spectrum);
        float mx = _spectrum.X + (float)(_hue / 360) * _spectrum.Width;
        float my = _spectrum.Bottom - (float)_saturation * _spectrum.Height;
        g.PushClip(_spectrum);
        g.DrawEllipse(mx, my, 6, 6, UiColor.Black, 3);
        g.DrawEllipse(mx, my, 6, 6, UiColor.White, 1.5f);
        g.PopClip();
        _lightBar = new UiRect(_spectrum.Right + 12, firstY, 32, 220);
        _hit.Add("cp.lightness", _lightBar, UiCursorKind.Hand, L10n.T("Color.Lightness"));
        var mid = FromHsl(_hue, _saturation, 0.5);
        var track = _lightBar.Deflate(0, 6, 12, 6);
        var upper = new UiRect(track.X, track.Y, track.Width, track.Height / 2);
        g.FillLinearGradientRect(upper, 0, UiColor.White, mid, false);
        g.FillLinearGradientRect(upper.Offset(0, upper.Height), 0, mid, UiColor.Black, false);
        float thumbY = track.Bottom - (float)_lightness * track.Height;
        g.DrawText("◀", new UiRect(_lightBar.Right - 11, thumbY - 8, 12, 16), _theme.TextPrimary, 12, 400, UiTextAlign.Center);
        DrawChannels(g, 3, right, _spectrum.Bottom + 14, w - left - right);
        var cancel = new UiRect(w - left - 100, h - 20 - 36, 100, 36);
        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "cp.ok", cancel.Offset(-110, 0), L10n.T("Color.Confirm"), fontSize: 13, radius: 8);
        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "cp.cancel", cancel, L10n.T("Auto.Cancel"), fontSize: 13, radius: 8);
    }

    private void DrawChannels(D2dTarget g, int first, float x, float y, float totalWidth)
    {
        float cell = totalWidth / 3;
        for (int i = 0; i < 3; i++)
        {
            var label = new UiRect(x + i * cell + 2, y, cell - 8, 17);
            g.DrawText(L10n.T(LabelKeys[first + i]), label, _theme.TextPrimary, 13, 600);
            _channels[first + i] = new UiRect(label.X, label.Bottom + 4, label.Width, 34);
            DrawField(g, first + i);
        }
    }

    protected override InlineEditHost ActiveEditor => _inlineEdit;

    private void DrawField(D2dTarget g, int i) =>
        UiPrimitives.DrawTextBox(g, _theme, _hit, ChannelIds[i], _channels[i], _values[i], "", _inlineEdit.ActiveTag == ChannelIds[i], mono: i == 6, fontSize: 13);

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        if (_drag != null && leftDown) { Pick(_drag, xDip, yDip); return; }
        if (_hit.UpdateHover(xDip, yDip)) { SetCursorKind(_hit.Hovered?.Cursor ?? UiCursorKind.Arrow); Invalidate(); }
    }

    protected override void OnPointerDown(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return;
        if (_inlineEdit.IsOpen) _inlineEdit.Close(commit: true, viaEnter: false);
        string? id = _hit.HitTest(e.X, e.Y)?.Id;
        _hit.ActiveId = id;
        _arrowTarget = id;
        if (id is "cp.spectrum" or "cp.lightness")
        {
            _drag = id;
            Pick(id, e.X, e.Y);
        }
    }

    protected override void OnPointerUp(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return;
        string? pressed = _hit.ActiveId;
        _hit.ActiveId = null;
        if (_drag is { } drag)
        {
            _drag = null;
            Pick(drag, e.X, e.Y);
            return;
        }
        if (_hit.HitTest(e.X, e.Y) is { } item && item.Id == pressed) Activate(item.Id);
    }

    protected override bool ActivateFocused(UiHitItem item, UiPointerEventArgs e) => Activate(item.Id);

    private bool Activate(string id)
    {
        _arrowTarget = id;
        if (id == "cp.ok") { Accept(); return true; }
        if (id is "cp.cancel" or "cp.close") { CloseWindow(); return true; }
        int i = Array.IndexOf(ChannelIds, id);
        if (i >= 0)
        {
            _inlineEdit.Open(this, id, _values[i], _channels[i], 13f, _theme.SurfaceBg, _theme.TextPrimary,
                mono: i == 6, selectAll: true);
            Invalidate();
            return true;
        }
        if (id.StartsWith("cp.preset.", StringComparison.Ordinal) && int.TryParse(id.AsSpan(10), out int index)
            && (uint)index < (uint)Presets.Length) { SelectRgb(Presets[index]); return true; }
        return id is "cp.spectrum" or "cp.lightness";
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (vk == 0x1B) { CloseWindow(); return true; }
        if (vk == 0x0D && !_hit.ShowFocus) { Accept(); return true; }
        string? target = _hit.TryGetFocus(out var focused, out _) ? focused.Id : _arrowTarget;
        if (target == "cp.spectrum" && vk is >= 0x25 and <= 0x28)
        {
            _hue = Math.Clamp(_hue + (vk == 0x25 ? -1 : vk == 0x27 ? 1 : 0), 0, 360);
            _saturation = Math.Clamp(_saturation + (vk == 0x26 ? .01 : vk == 0x28 ? -.01 : 0), 0, 1);
        }
        else if (target == "cp.lightness" && vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x21 or 0x22)
            _lightness = Math.Clamp(_lightness + (vk is 0x26 or 0x27 ? .01 : vk == 0x21 ? .1 : vk == 0x22 ? -.1 : -.01), 0, 1);
        else return false;
        SelectHsl();
        return true;
    }

    private void Accept()
    {
        if (_finished) return;
        if (_inlineEdit.IsOpen) _inlineEdit.Close(commit: true, viaEnter: true);
        _finished = true;
        var chosen = _selected;
        CloseWindow();
        _onAccepted(chosen);
    }



    private void CommitField(string id, string text, bool viaEnter)
    {
        int i = Array.IndexOf(ChannelIds, id);
        text = text.Trim();
        if (i == 6)
        {
            var hex = text.AsSpan();
            if (hex.StartsWith("#")) hex = hex[1..];
            if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
                SelectRgb(UiColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }
        else if (i is >= 0 and < 3 && byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out byte value))
            SelectRgb(UiColor.FromRgb(i == 0 ? value : _selected.R, i == 1 ? value : _selected.G, i == 2 ? value : _selected.B));
        else if (i is >= 3 and < 6 && double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double v)
            && v >= 0 && v <= (i == 3 ? 360 : 100))
        {
            if (i == 3) _hue = v;
            else if (i == 4) _saturation = v / 100;
            else _lightness = v / 100;
            SelectHsl();
        }
        Invalidate();
    }

    private void Pick(string target, float x, float y)
    {
        if (target == "cp.lightness")
        {
            var track = _lightBar.Deflate(0, 6, 12, 6);
            _lightness = Math.Clamp((track.Bottom - y) / track.Height, 0, 1);
        }
        else
        {
            _hue = 360 * Math.Clamp((x - _spectrum.X) / _spectrum.Width, 0, 1);
            _saturation = Math.Clamp((_spectrum.Bottom - y) / _spectrum.Height, 0, 1);
        }
        SelectHsl();
    }

    private void SelectHsl() { _selected = FromHsl(_hue, _saturation, _lightness); UpdateValues(); }

    private void SelectRgb(UiColor rgb)
    {
        _selected = rgb.WithAlpha(255);
        RgbToHsv(rgb, ref _hue, out double sat, out double max);
        double min = max * (1 - sat);
        _lightness = (min + max) / 2;
        _saturation = _lightness is <= 0 or >= 1 ? 0 : (max - min) / (1 - Math.Abs(2 * _lightness - 1));
        UpdateValues();
    }

    private void UpdateValues()
    {
        _values[0] = _selected.R.ToString(CultureInfo.InvariantCulture);
        _values[1] = _selected.G.ToString(CultureInfo.InvariantCulture);
        _values[2] = _selected.B.ToString(CultureInfo.InvariantCulture);
        _values[3] = _hue.ToString("0.##", CultureInfo.InvariantCulture);
        _values[4] = (_saturation * 100).ToString("0.##", CultureInfo.InvariantCulture);
        _values[5] = (_lightness * 100).ToString("0.##", CultureInfo.InvariantCulture);
        _values[6] = _selected.ToHexRgb();
        Invalidate();
    }

    private static UiColor FromHsl(double h, double s, double l)
    {
        double v = l + s * Math.Min(l, 1 - l);
        return HsvToRgb(h, v == 0 ? 0 : 2 * (1 - l / v), v);
    }

    private static UiColor[] CreatePresets()
    {
        var colors = new UiColor[96];
        for (int i = 0; i < 12; i++)
        {
            byte gray = (byte)Math.Round(i * 255 / 11.0);
            colors[i] = UiColor.FromRgb(gray, gray, gray);
        }
        double[] sat = [.2, .4, .65, 1, 1, 1, 1], value = [1, 1, 1, 1, .8, .6, .35];
        for (int i = 12; i < colors.Length; i++)
            colors[i] = HsvToRgb(i % 12 * 30, sat[i / 12 - 1], value[i / 12 - 1]);
        return colors;
    }

    private static readonly string[] SwatchIds = Array.ConvertAll(Enumerable.Range(0, 96).ToArray(), i => "cp.preset." + i);
    private static readonly string[] SwatchLabels = Array.ConvertAll(Presets, c => c.ToHexRgb());
    private const int SpectrumWidth = 193, SpectrumHeight = 101;
    private byte[] _spectrumPixels = MakeSpectrum();

    private static byte[] MakeSpectrum()
    {
        var data = new byte[SpectrumWidth * SpectrumHeight * 4];
        for (int x = 0; x < SpectrumWidth; x++)
        {
            var hue = FromHsl(360d * x / (SpectrumWidth - 1), 1, .5);
            for (int y = 0; y < SpectrumHeight; y++)
            {
                // WPF overlays transparent white to opaque #808080 on the rainbow.
                double alpha = (double)y / (SpectrumHeight - 1);
                byte grey = (byte)Math.Round(255 - 127 * alpha);
                var c = UiColor.Blend(hue, UiColor.FromRgb(grey, grey, grey), alpha);
                int offset = (y * SpectrumWidth + x) * 4;
                data[offset] = c.B;
                data[offset + 1] = c.G;
                data[offset + 2] = c.R;
                data[offset + 3] = 255;
            }
        }
        return data;
    }

    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint hwnd, bool enable);

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

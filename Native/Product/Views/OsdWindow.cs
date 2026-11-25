using System;
using SonicRoute.Core;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

/// <summary>
/// Direct2D layered OSD window matching WPF OsdService geometry, scale, volume bar, and placement.
/// Non-activating, click-through, topmost, zero idle timers.
/// </summary>
internal sealed class OsdWindow : LayeredWindow
{
    private readonly AppConfig _config;
    private UiThemePalette _theme;
    private string _title = "";
    private string _text = "";
    private int _parsedPercent = -1;
    private bool _muted;
    private byte _alpha = 255;

    internal OsdWindow(AppConfig config)
        : base(Math.Clamp(config.OsdWidth, 180, 600), 70f, 180f, 40f)
    {
        _config = config;
        _theme = UiThemePalette.Resolve(config);
        NoActivate = true;
        ClickThrough = true;
        TopMost = true;
        ToolWindow = true;
    }

    internal bool ShowOsd(nint owner, string title, string text, byte alpha = 255, bool? muted = null)
    {
        OwnerHandle = owner;
        _theme = UiThemePalette.Resolve(_config);
        _title = title ?? "";
        _text = text ?? "";
        _alpha = alpha;

        (_parsedPercent, bool parsedMuted) = ParseVolumeState(_text);
        _muted = muted ?? parsedMuted;

        float width = Math.Clamp(_config.OsdWidth, 180, 600);
        float s = Math.Clamp((float)_config.OsdFontScale, 0.7f, 1.6f);
        bool hasVol = _config.OsdVolumeVisualEnabled && _parsedPercent >= 0;
        float height = 68f * s + 2f + (hasVol ? 12f * s : 0f);

        ResizeDip(width, height);
        EnsureCreated("SonicRoute OSD");
        PositionOnMonitor(width, height);

        if (!IsVisible)
        {
            Show(activate: false);
        }
        else
        {
            RenderNow();
        }
        return Handle != 0;
    }

    internal void SetOsdAlpha(byte alpha)
    {
        _alpha = alpha;
        if (IsVisible)
        {
            RenderNow();
        }
    }

    internal void HideAndRelease()
    {
        Hide();
        ReleaseRenderResources();
    }

    protected override string GetWindowTitle() => "SonicRoute OSD";

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        float alphaFrac = _alpha / 255f;
        if (alphaFrac <= 0.005f) return;

        float s = Math.Clamp((float)_config.OsdFontScale, 0.7f, 1.6f);
        float radius = 10f * s;
        float padX = 16f * s;
        float padY = 10f * s;

        var surface = new UiRect(0f, 0f, widthDip, heightDip);
        g.FillRoundedRect(surface, radius, _theme.SurfaceBgAlpha.WithAlphaFraction(alphaFrac));
        g.DrawRoundedRect(surface, radius, _theme.Border.WithAlphaFraction(alphaFrac), 1f);

        float contentW = MathF.Max(10f, widthDip - padX * 2f);
        var titleRect = new UiRect(padX, padY, contentW, 18f * s);
        g.DrawText(_title, titleRect, _theme.OsdTitleText.WithAlphaFraction(alphaFrac),
            12f * s, 400, UiTextAlign.Leading, UiVertAlign.Center, wrap: false, ellipsis: true);

        bool hasVol = _config.OsdVolumeVisualEnabled && _parsedPercent >= 0;
        if (!hasVol)
        {
            var textRect = new UiRect(padX, titleRect.Bottom + 3f * s, contentW, 27f * s);
            g.DrawText(_text, textRect, _theme.Accent.WithAlphaFraction(alphaFrac),
                18f * s, 600, UiTextAlign.Leading, UiVertAlign.Center, wrap: false, ellipsis: true);
            return;
        }

        float volTop = titleRect.Bottom + 3f * s;
        float rowH = 27f * s;
        float iconW = MathF.Min(28f * s, contentW * 0.26f);
        float iconSize = MathF.Min(18f * s, iconW / 1.3f);
        var iconBox = new UiRect(padX, volTop + (rowH - iconSize) * 0.5f, iconSize, iconSize);
        UiColor accentInk = _theme.Accent.WithAlphaFraction(alphaFrac);
        g.DrawIcon(_muted ? UiIcon.VolumeMutedSlash : UiIcon.Speaker, iconBox, accentInk, 1.8f);

        float numSize = MathF.Min(18f * s, MathF.Max(10f, (contentW - iconW) / 3.5f));
        float numColW = 3.1f * numSize;
        var pctRect = new UiRect(padX + iconW, volTop, numColW, rowH);
        g.DrawText($"{_parsedPercent}%", pctRect, accentInk,
            numSize, 600, UiTextAlign.Leading, UiVertAlign.Center, wrap: false, ellipsis: false);

        if (_muted)
        {
            float capX = pctRect.Right + 4f * s;
            float capW = MathF.Max(0f, padX + contentW - capX);
            if (capW > 8f)
            {
                g.DrawText(L10n.T("Ov.Muted"), new UiRect(capX, volTop, capW, rowH), accentInk,
                    12f * s, 400, UiTextAlign.Leading, UiVertAlign.Center, wrap: false, ellipsis: true);
            }
        }

        float trackY = volTop + rowH + 8f * s;
        float trackH = MathF.Max(2f, 4f * s);
        float trackR = trackH * 0.5f;
        var trackRect = new UiRect(padX, trackY, contentW, trackH);
        g.FillRoundedRect(trackRect, trackR, _theme.SliderBg.WithAlphaFraction(alphaFrac));

        float fillW = contentW * Math.Clamp(_parsedPercent / 100f, 0f, 1f);
        if (fillW > 0.5f)
        {
            UiColor barColor = (_muted ? _theme.OsdTitle : _theme.OsdAccent).WithAlphaFraction(alphaFrac);
            g.FillRoundedRect(new UiRect(padX, trackY, fillW, trackH), trackR, barColor);
        }
    }

    private void PositionOnMonitor(float widthDip, float heightDip)
    {
        string pos = (_config.OsdPosition ?? "TR").Trim();
        if (string.Equals(pos, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            if (_config.OsdCustomPhysicalX is int px && _config.OsdCustomPhysicalY is int py)
            {
                GetMonitorWorkAreaForPoint(px, py, out int ml, out int mt, out int mr, out int mb, out uint monDpi);
                float scale = monDpi / 96f;
                int wPx = Math.Max(1, (int)MathF.Round(widthDip * scale));
                int hPx = Math.Max(1, (int)MathF.Round(heightDip * scale));
                int cx = Math.Clamp(px, ml, Math.Max(ml, mr - wPx));
                int cy = Math.Clamp(py, mt, Math.Max(mt, mb - hPx));
                SetBoundsPx(cx, cy, widthDip, heightDip);
                return;
            }
            if (_config.OsdCustomX >= 0 || _config.OsdCustomY >= 0)
            {
                GetMonitorWorkAreaForPoint(0, 0, out int ml, out int mt, out int mr, out int mb, out uint monDpi);
                float scale = monDpi / 96f;
                int wPx = Math.Max(1, (int)MathF.Round(widthDip * scale));
                int hPx = Math.Max(1, (int)MathF.Round(heightDip * scale));
                int cx = Math.Clamp((int)MathF.Round(_config.OsdCustomX * scale), ml, Math.Max(ml, mr - wPx));
                int cy = Math.Clamp((int)MathF.Round(_config.OsdCustomY * scale), mt, Math.Max(mt, mb - hPx));
                SetBoundsPx(cx, cy, widthDip, heightDip);
                return;
            }
        }

        // Primary monitor work area (matching OsdPlacement.Apply)
        GetMonitorWorkAreaForPoint(0, 0, out int workL, out int workT, out int workR, out int workB, out uint dpi);
        float s = dpi / 96f;
        int widthPx = Math.Max(1, (int)MathF.Round(widthDip * s));
        int heightPx = Math.Max(1, (int)MathF.Round(heightDip * s));
        int workW = Math.Max(1, workR - workL);
        int workH = Math.Max(1, workB - workT);

        string upper = pos.ToUpperInvariant();
        if (upper == "C") upper = "MC";

        int gutterX = (int)MathF.Round(16f * s);
        int gutterY = (int)MathF.Round(14f * s);

        int x = upper.EndsWith('L') ? workL + gutterX
            : upper.EndsWith('C') ? workL + (workW - widthPx) / 2
            : workR - widthPx - gutterX;

        int y = upper.StartsWith('B') ? workB - heightPx - gutterY
            : upper.StartsWith('M') ? workT + (workH - heightPx) / 2
            : workT + gutterY;

        x += (int)MathF.Round(_config.OsdOffsetX * s);
        y += (int)MathF.Round(_config.OsdOffsetY * s);

        x = Math.Clamp(x, workL, Math.Max(workL, workR - widthPx));
        y = Math.Clamp(y, workT, Math.Max(workT, workB - heightPx));
        SetBoundsPx(x, y, widthDip, heightDip);
    }

    private static (int Percent, bool Muted) ParseVolumeState(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (-1, false);
        bool muted = text.Contains('\uD83D') // emoji prefix like 🔇
            || text.Contains("🔇", StringComparison.Ordinal)
            || string.Equals(text, L10n.T("Ov.Muted"), StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, L10n.T("Ov.AppMuted"), StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, L10n.T("Ov.MicMuted"), StringComparison.OrdinalIgnoreCase);

        int pctIdx = text.IndexOf('%');
        if (pctIdx > 0)
        {
            int start = pctIdx - 1;
            while (start >= 0 && char.IsDigit(text[start])) start--;
            start++;
            if (start < pctIdx && int.TryParse(text.AsSpan(start, pctIdx - start), out int val))
            {
                return (Math.Clamp(val, 0, 100), muted);
            }
        }
        return (-1, muted);
    }
}

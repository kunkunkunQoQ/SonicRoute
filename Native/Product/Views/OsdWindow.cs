using System;
using SonicRoute.Core;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

/// <summary>
/// Direct2D layered OSD window matching WPF OsdService geometry, scale, 90ms eased volume bar,
/// custom multi-monitor drag-adjust mode, and placement.
/// Non-activating and click-through during normal OSD display; zero idle timers.
/// </summary>
internal sealed class OsdWindow : LayeredWindow
{
    private const float BarDurationMs = 90f;

    private readonly AppConfig _config;
    private UiThemePalette _theme;
    private string _title = "";
    private string _text = "";
    private int _parsedPercent = -1;
    private bool _muted;
    private byte _alpha = 255;

    private string? _barOwner;
    private float _barProgress;
    private float _barFrom;
    private float _barTarget;
    private long _barStartTicks;
    private bool _barAnimating;

    private bool _adjusting;
    private bool _dragging;
    private int _dragCursorStartX;
    private int _dragCursorStartY;
    private int _dragWinStartX;
    private int _dragWinStartY;

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

    internal byte CurrentAlpha => _alpha;
    internal bool IsAdjusting => _adjusting;

    internal bool ShowOsd(nint owner, string title, string text, byte alpha = 255, bool? muted = null, int? volume = null)
    {
        if (_adjusting)
        {
            EndAdjustFlags();
        }

        OwnerHandle = owner;
        _theme = UiThemePalette.Resolve(_config);
        string nextTitle = title ?? "";
        _text = text ?? "";
        _alpha = alpha;

        if (volume.HasValue)
        {
            _parsedPercent = Math.Clamp(volume.Value, 0, 100);
            _muted = muted ?? false;
        }
        else
        {
            (int textPercent, bool parsedMuted) = ParseVolumeState(_text);
            _parsedPercent = textPercent;
            _muted = muted ?? parsedMuted;
        }

        float width = Math.Clamp(_config.OsdWidth, 180, 600);
        float s = Math.Clamp((float)_config.OsdFontScale, 0.7f, 2f);
        bool hasVol = _config.OsdVolumeVisualEnabled && _parsedPercent >= 0;
        float height = 68f * s + 2f + (hasVol ? 12f * s : 0f);

        bool wasVisible = IsVisible;
        bool sameTarget = wasVisible && hasVol && string.Equals(_barOwner, nextTitle, StringComparison.Ordinal);
        _title = nextTitle;

        UpdateBarTransition(hasVol, sameTarget);

        ResizeDip(width, height);
        EnsureCreated("SonicRoute OSD");
        PositionOnMonitor(width, height);

        if (!wasVisible)
        {
            Show(activate: false);
        }
        else
        {
            RenderNow();
        }
        return Handle != 0;
    }

    internal void BeginAdjust(nint owner = 0)
    {
        if (owner != 0) OwnerHandle = owner;
        _theme = UiThemePalette.Resolve(_config);
        _adjusting = true;
        _dragging = false;
        _title = L10n.T("Exp.OsdDragTitle");
        _text = L10n.T("Exp.OsdDragHint");
        _parsedPercent = -1;
        _muted = false;
        _alpha = 255;
        StopBarAnimation();

        float width = Math.Clamp(_config.OsdWidth, 180, 600);
        float s = Math.Clamp((float)_config.OsdFontScale, 0.7f, 2f);
        float height = 68f * s + 2f;

        ResizeDip(width, height);
        EnsureCreated("SonicRoute OSD");
        SetClickThrough(false);
        SetNoActivate(false);
        SetCursorKind(UiCursorKind.Hand);
        PositionOnMonitor(width, height);

        if (!IsVisible) Show(activate: true);
        else { RequestFocus(); RenderNow(); }
    }

    internal void CancelAdjust()
    {
        if (!_adjusting) return;
        EndAdjustFlags();
        Hide();
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

    internal void ReleaseIdleResources()
    {
        if (!IsVisible) ReleaseRenderResources();
    }

    internal override void ApplyTheme(UiThemePalette theme)
    {
        _theme = theme;
        Invalidate();
    }

    protected override string GetWindowTitle() => "SonicRoute OSD";

    protected override void OnHidden()
    {
        StopBarAnimation();
        if (_adjusting) EndAdjustFlags();
    }

    protected override void OnTickAnimation()
    {
        if (!_barAnimating || !IsVisible)
        {
            _barAnimating = false;
            StopAnimationTimer();
            return;
        }

        float t = Math.Clamp((Environment.TickCount64 - _barStartTicks) / BarDurationMs, 0f, 1f);
        float eased = 1f - (1f - t) * (1f - t);
        _barProgress = _barFrom + (_barTarget - _barFrom) * eased;

        if (t >= 1f)
        {
            _barProgress = _barTarget;
            _barAnimating = false;
            StopAnimationTimer();
        }

        Invalidate();
    }

    protected override void OnPointerDown(UiPointerEventArgs e)
    {
        if (!_adjusting) return;
        if (e.Button == UiMouseButton.Right)
        {
            CancelAdjust();
            return;
        }
        if (e.Button == UiMouseButton.Left && ViewNative.GetCursorPos(out var cur) != 0)
        {
            _dragCursorStartX = cur.X;
            _dragCursorStartY = cur.Y;
            _dragWinStartX = LeftPx;
            _dragWinStartY = TopPx;
            _dragging = true;
            if (Handle != 0) ViewNative.SetCapture(Handle);
        }
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        if (!_adjusting || !_dragging) return;
        if (!leftDown)
        {
            CommitDragAdjust();
            return;
        }
        if (ViewNative.GetCursorPos(out var cur) == 0) return;

        int nx = _dragWinStartX + (cur.X - _dragCursorStartX);
        int ny = _dragWinStartY + (cur.Y - _dragCursorStartY);
        GetMonitorWorkAreaForPoint(cur.X, cur.Y, out int ml, out int mt, out int mr, out int mb, out _);
        nx = Math.Clamp(nx, ml, Math.Max(ml, mr - WidthPx));
        ny = Math.Clamp(ny, mt, Math.Max(mt, mb - HeightPx));
        MovePx(nx, ny);
    }

    protected override void OnPointerUp(UiPointerEventArgs e)
    {
        if (!_adjusting) return;
        if (e.Button == UiMouseButton.Left && _dragging)
        {
            CommitDragAdjust();
        }
    }

    protected override void OnCaptureLost()
    {
        _dragging = false;
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (_adjusting && vk == 0x1B /*VK_ESCAPE*/)
        {
            CancelAdjust();
            return true;
        }
        return false;
    }

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        float alphaFrac = _alpha / 255f;
        if (alphaFrac <= 0.005f) return;

        float s = Math.Clamp((float)_config.OsdFontScale, 0.7f, 2f);
        float radius = 10f * s;
        float padX = 16f * s + 1f;
        float padY = 10f * s + 1f;

        var surface = new UiRect(0f, 0f, widthDip, heightDip);
        g.FillRoundedRect(surface, radius, _theme.SurfaceBgAlpha.WithAlphaFraction(alphaFrac));
        UiColor borderColor = (_adjusting ? _theme.Accent : _theme.Border).WithAlphaFraction(alphaFrac);
        g.DrawRoundedRect(surface, radius, borderColor, _adjusting ? 1.5f : 1f);

        float contentW = MathF.Max(10f, widthDip - padX * 2f);
        var titleRect = new UiRect(padX, padY, contentW, 18f * s);
        g.DrawText(_title, titleRect, _theme.OsdTitleText.WithAlphaFraction(alphaFrac),
            12f * s, 400, UiTextAlign.Leading, UiVertAlign.Center, wrap: false, ellipsis: true);

        bool hasVol = !_adjusting && _config.OsdVolumeVisualEnabled && _parsedPercent >= 0;
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

        float fillW = contentW * Math.Clamp(_barProgress, 0f, 1f);
        if (fillW > 0.5f)
        {
            UiColor barColor = (_muted ? _theme.OsdTitle : _theme.OsdAccent).WithAlphaFraction(alphaFrac);
            g.FillRoundedRect(new UiRect(padX, trackY, fillW, trackH), trackR, barColor);
        }
    }

    private void UpdateBarTransition(bool hasVol, bool sameTarget)
    {
        if (!hasVol)
        {
            StopBarAnimation();
            return;
        }

        float nextTarget = Math.Clamp(_parsedPercent / 100f, 0f, 1f);
        if (!sameTarget)
        {
            _barAnimating = false;
            StopAnimationTimer();
            _barOwner = _title;
            _barFrom = _barTarget = _barProgress = nextTarget;
            return;
        }

        if (_barAnimating)
        {
            float t = Math.Clamp((Environment.TickCount64 - _barStartTicks) / BarDurationMs, 0f, 1f);
            float eased = 1f - (1f - t) * (1f - t);
            _barProgress = _barFrom + (_barTarget - _barFrom) * eased;
        }

        if (MathF.Abs(nextTarget - _barProgress) <= 0.0005f)
        {
            _barAnimating = false;
            StopAnimationTimer();
            _barFrom = _barTarget = _barProgress = nextTarget;
            return;
        }

        _barFrom = _barProgress;
        _barTarget = nextTarget;
        _barStartTicks = Environment.TickCount64;
        _barAnimating = true;
        StartAnimationTimer(16);
    }

    private void StopBarAnimation()
    {
        _barAnimating = false;
        _barOwner = null;
        StopAnimationTimer();
    }

    private void CommitDragAdjust()
    {
        _dragging = false;
        ReleasePointerCapture();

        float scale = MathF.Max(1f, DpiScale);
        _config.OsdPosition = "Custom";
        _config.OsdCustomPhysicalX = LeftPx;
        _config.OsdCustomPhysicalY = TopPx;
        _config.OsdCustomX = (int)MathF.Round(LeftPx / scale);
        _config.OsdCustomY = (int)MathF.Round(TopPx / scale);
        ConfigService.Save(_config);

        EndAdjustFlags();
        Hide();
    }

    private void EndAdjustFlags()
    {
        _adjusting = false;
        _dragging = false;
        ReleasePointerCapture();
        SetClickThrough(true);
        SetNoActivate(true);
        SetCursorKind(UiCursorKind.Arrow);
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
        bool muted = text.Contains("🔇", StringComparison.Ordinal)
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

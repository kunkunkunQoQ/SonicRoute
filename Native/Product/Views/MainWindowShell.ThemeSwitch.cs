using System.Diagnostics;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed partial class MainWindowShell
{
    private static readonly (string Key, string Id, string Label, UiIcon Icon)[] Modes =
    [
        ("system", "shell.mode.system", "St.ThemeSystem", UiIcon.Monitor),
        ("light", "shell.mode.light", "St.ThemeLight", UiIcon.Sun),
        ("dark", "shell.mode.dark", "St.ThemeDark", UiIcon.Moon)
    ];

    private bool _modeAnimating;
    private long _modeStart;
    private float _modeFrom, _modePosition;

    private int SelectedMode => string.Equals(_config.ThemeMode, "dark", StringComparison.OrdinalIgnoreCase) ? 2
        : string.Equals(_config.ThemeMode, "light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private void SelectThemeMode(string id)
    {
        int next = Array.FindIndex(Modes, m => m.Id == id);
        int old = SelectedMode;
        if (next < 0 || next == old) return;
        _config.ThemeMode = Modes[next].Key;
        if (!this.CommitSettings()) return;
        _modeFrom = _modePosition = _modeAnimating ? _modePosition : old;
        _modeStart = Stopwatch.GetTimestamp();
        _modeAnimating = ClientAreaAnimationsEnabled();
        NotifyThemeChanged();
        if (_modeAnimating) StartAnimationTimer(16);
        Invalidate();
    }

    private bool TickThemeSwitch()
    {
        if (!_modeAnimating) return false;
        const double durationMs = 150;
        float remaining = (float)Math.Clamp(1 - Stopwatch.GetElapsedTime(_modeStart).TotalMilliseconds / durationMs, 0, 1);
        _modePosition = SelectedMode + (_modeFrom - SelectedMode) * remaining * remaining * remaining;
        _modeAnimating = remaining > 0;
        return _modeAnimating;
    }

    private float DrawThemeSwitch(D2dTarget g, UiRect nav)
    {
        bool rail = nav.Width < 118f;
        float width = rail ? 40f : nav.Width - 16f;
        float height = rail ? 82f : 32f;
        var track = new UiRect(nav.CenterX - width / 2f, nav.Bottom - 12f - height, width, height);
        g.FillRoundedRect(track, 7f, _theme.SurfaceAlt);
        g.DrawRoundedRect(track, 7f, _theme.Border, 1f);

        var inner = track.Deflate(2f);
        Span<UiRect> cells = stackalloc UiRect[3];
        float x = inner.X;
        for (int i = 0; i < cells.Length; i++)
        {
            float w = inner.Width * (i == 0 ? .42f : .29f);
            cells[i] = rail ? new UiRect(inner.X, inner.Y + i * inner.Height / 3f, inner.Width, inner.Height / 3f)
                : new UiRect(x, inner.Y, w, inner.Height);
            x += w;
            string id = Modes[i].Id;
            if (_hit.IsHover(id) || _hit.IsActive(id)) g.FillRoundedRect(cells[i], 5f, _theme.NavHover);
        }
        int selected = SelectedMode;
        float position = _modeAnimating ? _modePosition : selected;
        int left = (int)MathF.Floor(position);
        var first = cells[left];
        var last = cells[Math.Min(left + 1, 2)];
        float blend = position - left;
        var indicator = new UiRect(first.X + (last.X - first.X) * blend, first.Y + (last.Y - first.Y) * blend,
            first.Width + (last.Width - first.Width) * blend, first.Height);
        g.FillRoundedRect(indicator, 5f, _theme.SurfaceBg);
        g.DrawRoundedRect(indicator, 5f, _theme.Border, 1f);

        for (int i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            var mode = Modes[i];
            string label = L10n.T(mode.Label);
            _hit.Add(mode.Id, cell, UiCursorKind.Hand, label);
            var color = i == selected ? _theme.Accent : _theme.TextPrimary;
            bool showLabel = !rail && nav.Width >= 154f;
            float iconX = showLabel ? cell.X + 4f : cell.CenterX - 6.5f;
            g.DrawIcon(mode.Icon, new UiRect(iconX, cell.CenterY - 6.5f, 13f, 13f), color, 1.6f);
            if (showLabel)
                g.DrawText(label, cell.Deflate(20f, 0f, 2f, 0f), color, 11f, i == selected ? 600 : 400,
                    UiTextAlign.Center, UiVertAlign.Center);
        }
        return track.Y;
    }
}

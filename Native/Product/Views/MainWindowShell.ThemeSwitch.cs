using System.Diagnostics;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed partial class MainWindowShell
{
    private static readonly string[] Modes = ["system", "light", "dark"];
    private static readonly UiSwitchSegment[] ThemeSegments =
    [
        new("shell.mode.system", "St.ThemeSystem", UiIcon.Monitor, "Th.SystemShort"),
        new("shell.mode.light", "St.ThemeLight", UiIcon.Sun),
        new("shell.mode.dark", "St.ThemeDark", UiIcon.Moon)
    ];

    private bool _modeAnimating;
    private long _modeStart;
    private float _modeFrom, _modePosition;

    private int SelectedMode => string.Equals(_config.ThemeMode, "dark", StringComparison.OrdinalIgnoreCase) ? 2
        : string.Equals(_config.ThemeMode, "light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private void SelectThemeMode(string id)
    {
        int next = Array.FindIndex(ThemeSegments, segment => segment.Id == id);
        int old = SelectedMode;
        if (next < 0 || next == old) return;
        _config.ThemeMode = Modes[next];
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

    private void DrawThemeSwitch(D2dTarget g, UiRect nav)
    {
        bool rail = nav.Width < 118f;
        float width = rail ? 40f : nav.Width - 16f;
        float height = rail ? 82f : 32f;
        var track = new UiRect(nav.CenterX - width / 2f, nav.Bottom - 12f - height, width, height);
        int selected = SelectedMode;
        UiSegmentedSwitch.Draw(g, _theme, _hit, track, ThemeSegments, [.42f, .29f, .29f],
            selected, _modeAnimating ? _modePosition : selected, vertical: rail, showLabels: nav.Width >= 154f, tooltips: true);
    }
}

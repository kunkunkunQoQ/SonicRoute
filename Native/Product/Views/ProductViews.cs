using SonicRoute.Core;

namespace SonicRoute.Native.Product;

internal sealed partial class ProductViews
{
    private sealed record Row(string Title, string Detail, Action? Click, Action<int>? Slider, int Value, bool Selected, bool Section, float? Peak = null, object? IconKey = null, Func<byte[]?>? Icon = null);
    private sealed record Hit(ViewRect Area, Row Row, int Index);
    private static readonly string[] Navigation = ["Nav.Apps", "Nav.Hotkeys", "Nav.Theme", "Nav.Settings", "Nav.Automation", "Nav.Experimental"];
    private readonly ApplicationHost host;
    private readonly AppConfig config;
    private readonly ProductViewWindow window;
    private readonly bool panel;
    private readonly List<Row> rows = [];
    private readonly List<Hit> hits = [];
    private int page, active = -1, hover = -1, visibleFirst, visibleCount, scroll, modifiers;
    private float rowHeight = 48;
    private Hit? sizing;
    private int dragValue;
    private bool visible, modal, automationObserved;
    private string status = "";
    private IDisposable? refreshTimer, statusTimer;
    private long generation;
    private nint Owner => window.Handle;
    private static string T(string key) => L10n.T(key);
    private static string Label(string zh, string en) => L10n.CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;

    internal ProductViews(ApplicationHost host, AppConfig config, ProductViewWindow window, bool panel)
        => (this.host, this.config, this.window, this.panel) = (host, config, window, panel);

    internal bool HasModal => modal;
    internal void Opened()
    {
        visible = true;
        status = "";
        ObserveAutomation(page == 4 && !panel);
#if SONICROUTE_PHONE
        if (page == 0 && phonePage) OpenPhoneView();
#endif
        Refresh();
        ScheduleRefresh(TimeSpan.Zero);
    }

    internal void Closed()
    {
        visible = false;
        ++generation;
        refreshTimer?.Dispose(); refreshTimer = null;
        statusTimer?.Dispose(); statusTimer = null;
        sizing = null;
        rows.Clear();
        hits.Clear();
        ClearAudio();
        DetachAccessibility();
        ObserveAutomation(false);
#if SONICROUTE_PHONE
        ReleasePhoneView();
        ReleaseAirPlayView();
#endif
    }

    internal void VisibilityChanged(bool value)
    {
        if (visible == value) return;
        visible = value;
        if (visible) ScheduleRefresh(TimeSpan.Zero);
        else
        {
            ++generation;
            refreshTimer?.Dispose();
            refreshTimer = null;
        }
    }

    private void ObserveAutomation(bool enabled)
    {
        if (automationObserved == enabled) return;
        automationObserved = enabled;
        if (enabled) AutoRuleService.ExecutionStateChanged += AutomationChanged;
        else AutoRuleService.ExecutionStateChanged -= AutomationChanged;
    }

    private void AutomationChanged() => host.TryPost(() => { if (automationObserved) Refresh(); });

    private void ScheduleRefresh(TimeSpan delay)
    {
        if (!visible || Owner == 0 || !(page == 0 || page == 3 || panel)) return;
        refreshTimer?.Dispose();
        refreshTimer = host.ScheduleOnce(delay, () =>
        {
            refreshTimer = null;
            RequestAudio();
        });
    }

    private void Add(string title, string detail = "", Action? click = null, Action<int>? slider = null, int value = 0, bool selected = false)
        => rows.Add(new(title, detail, click, slider, value, selected, false));

    private void RowIcon(object key, Func<byte[]?> load)
    {
        if (rows.Count != 0) rows[^1] = rows[^1] with { IconKey = key, Icon = load };
    }

    private void Header(string title, string detail = "") => rows.Add(new(title, detail, null, null, 0, false, true));

    private void Refresh()
    {
        if (Owner == 0) return;
        if (sizing == null)
        {
            rows.Clear();
            if (panel) BuildAudio();
            else switch (page)
            {
                case 0: BuildAudio(); break;
                case 1: BuildHotkeys(); break;
                case 2: BuildTheme(); break;
                case 3: BuildSettings(); break;
                case 4: BuildAutomation(); break;
                case 5: BuildExperimental(); break;
            }
            scroll = Math.Clamp(scroll, 0, Math.Max(0, rows.Count - Math.Max(1, visibleCount) / 2));
            window.SetOpacity();
        }
        window.Invalidate();
    }

    private void ChangePage(int index)
    {
        if (!panel && page == index) return;
        page = index;
        ObserveAutomation(index == 4);
#if SONICROUTE_PHONE
        if (index != 0) { ReleasePhoneView(); ReleaseAirPlayView(); }
        else if (phonePage) OpenPhoneView();
#endif
        scroll = 0;
        active = hover = -1;
        ++generation;
        Refresh();
        ScheduleRefresh(TimeSpan.Zero);
    }

    internal void Paint(ViewCanvas canvas, float width, float height)
    {
        var theme = ViewPalette.From(config);
        float left = panel ? 0 : config.MainSidebarCollapsed ? 58 : 174;
        float pad = width < 470 ? 12 : 22;
        float header = 51;
        float footer = status.Length == 0 ? 9 : 34;
        canvas.Clear(theme.Background);
        hits.Clear();

        if (!panel)
        {
            canvas.Fill(new(0, 0, left, height), theme.Sidebar);
            var collapse = new ViewRect(10, 9, left - 20, 34);
            var collapseRow = new Row(config.MainSidebarCollapsed ? "☰" : "☰  SonicRoute", "", () =>
            {
                config.MainSidebarCollapsed = !config.MainSidebarCollapsed;
                Persist();
            }, null, 0, false, false);
            DrawNav(canvas, theme, collapseRow, collapse, -10);
            for (int i = 0; i < Navigation.Length; i++)
            {
                int index = i;
                string name = T(Navigation[i]);
                if (config.MainSidebarCollapsed) name = new[] { "♫", "⌨", "◐", "⚙", "↻", "⚗" }[i];
                var nav = new Row(name, "", () => ChangePage(index), null, 0, i == page, false);
                DrawNav(canvas, theme, nav, new(10, 54 + 41 * i, left - 20, 35), -11 - i);
            }
        }

        float content = width - left - pad * 2;
        if (content < 45) return;
        canvas.Text(panel ? T("Tray.OpenPanel") : T(Navigation[page]), new(left + pad, 13, content - (panel ? 83 : 0), 32), theme.Text);
        if (panel)
            DrawNav(canvas, theme, new(T("Nav.Settings"), "", () => host.OpenProductMain(), null, 0, false, false),
                new(width - pad - 78, 8, 78, 34), -18);

#if SONICROUTE_PHONE
        if (page == 0 && phonePage && qrMatrix != null)
        {
            PaintPhoneQr(canvas, left + pad, header, Math.Min(content, height - header - 65));
            header += Math.Min(204, Math.Max(0, height - header - 65));
        }
#endif
        float bottom = Math.Max(header, height - footer);
        rowHeight = panel && !config.QuickPanelUseLegacyAppRows ? 37 : 49;
        visibleCount = Math.Max(1, (int)Math.Ceiling((bottom - header) / rowHeight));
        scroll = Math.Clamp(scroll, 0, Math.Max(0, rows.Count - Math.Max(1, visibleCount - 1)));
        visibleFirst = scroll;
        canvas.Clip(new(left, header, width - left, bottom - header));
        for (int i = scroll; i < rows.Count && i < scroll + visibleCount; i++)
        {
            var row = rows[i];
            var bounds = new ViewRect(left + pad, header + (i - scroll) * rowHeight, content, rowHeight - 5);
            DrawRow(canvas, theme, row, bounds, i);
        }
        canvas.Unclip();
        if (rows.Count > visibleCount)
        {
            float area = bottom - header;
            float thumb = Math.Max(18, area * visibleCount / rows.Count);
            float y = header + (area - thumb) * scroll / Math.Max(1, rows.Count - visibleCount + 1);
            canvas.Fill(new(width - 7, y, 3, thumb), theme.Secondary, 1.5f, .6f);
        }
        if (status.Length != 0)
            canvas.Text(status, new(left + pad, height - 28, content, 24), theme.Secondary);
        UpdateAccessibleRows();
    }

    private void DrawNav(ViewCanvas c, ViewPalette theme, Row row, ViewRect area, int index)
    {
        hits.Add(new(area, row, index));
        if (row.Selected || index == hover || index == active)
            c.Fill(area, theme.Hover, 7);
        if (row.Selected) c.Fill(new(area.X, area.Y + 8, 3, area.Height - 16), theme.Accent, 1.5f);
        c.Text(row.Title, new(area.X + 10, area.Y + 6, area.Width - 14, 25), theme.Text);
    }

    private void DrawRow(ViewCanvas c, ViewPalette theme, Row row, ViewRect area, int index)
    {
        bool interactive = row.Click != null || row.Slider != null;
        if (interactive) hits.Add(new(area, row, index));
        if (!row.Section)
            c.Fill(area, row.Selected || index == hover || index == active ? theme.Hover : theme.Card, 7);
        if (row.Selected) c.Fill(new(area.X, area.Y + 8, 3, area.Height - 16), theme.Accent, 2);
        float titleWidth = area.Width - 22;
        if (row.Slider != null)
        {
            var slider = SliderArea(area);
            titleWidth = slider.X - area.X - 28;
            int value = sizing?.Index == index ? dragValue : row.Value;
            c.Fill(slider, theme.Sidebar, 3);
            float fraction = Math.Clamp(value / 100f, 0, 1);
            if (row.Peak is float peak && peak > 0)
                c.Fill(slider with { Width = slider.Width * peak, Height = 3 }, theme.Accent, 1, .55f);
            if (fraction > 0) c.Fill(slider with { Width = slider.Width * fraction }, theme.Accent, 3);
            c.Fill(new(slider.X + slider.Width * fraction - 4, slider.Y - 3, 8, 12), theme.Text, 4);
            c.Text(value.ToString(), new(slider.X + slider.Width + 9, area.Y + (area.Height - 22) / 2, 34, 23), theme.Text);
        }
        float textX = area.X + 11;
        if (row.IconKey is { } iconKey && row.Icon is { } icon)
        {
            var iconBox = new ViewRect(textX, area.Y + (area.Height - 23) / 2, 23, 23);
            if (c.Icon(iconKey, icon, iconBox)) { textX += 30; titleWidth -= 30; }
        }
        float y = area.Y + (area.Height - 23) / 2;
        if (row.Detail.Length != 0 && area.Height > 36)
        {
            y = area.Y + 1;
            c.Text(row.Detail, new(textX, y + 20, titleWidth, 22), theme.Secondary);
        }
        c.Text(row.Title, new(textX, y, titleWidth, 23), row.Section ? theme.Accent : theme.Text);
    }

    private static ViewRect SliderArea(ViewRect row)
    {
        float width = Math.Clamp(row.Width * .32f, 38, 218);
        return new(row.X + row.Width - width - 46, row.Y + row.Height / 2 - 3, width, 6);
    }

    internal void PointerMove(float x, float y)
    {
        if (sizing != null)
        {
            var area = SliderArea(sizing.Area);
            int next = Math.Clamp((int)Math.Round((x - area.X) * 100 / area.Width), 0, 100);
            if (next == dragValue) return;
            dragValue = next;
            window.Invalidate();
            return;
        }
        int nextHover = hits.FirstOrDefault(hit => hit.Area.Contains(x, y))?.Index ?? -1;
        if (nextHover == hover) return;
        hover = nextHover;
        window.Invalidate();
    }

    internal void PointerDown(float x, float y, int keys)
    {
        var hit = hits.FirstOrDefault(hit => hit.Area.Contains(x, y));
        if (hit == null) return;
        active = hit.Index;
        modifiers = keys;
        if (hit.Row.Slider != null && x >= SliderArea(hit.Area).X - 9)
        {
            sizing = hit;
            dragValue = hit.Row.Value;
            ViewNative.SetCapture(Owner);
            PointerMove(x, y);
        }
        else Invoke(hit.Row.Click);
    }

    internal void PointerUp()
    {
        if (sizing is not { } hit) return;
        sizing = null;
        ViewNative.ReleaseCapture();
        Invoke(() => hit.Row.Slider?.Invoke(dragValue));
        window.Invalidate();
    }

    internal void PointerMenu(float x, float y)
    {
        var hit = hits.FirstOrDefault(hit => hit.Area.Contains(x, y));
        if (hit?.Row.Slider is not { } set) return;
        uint command = Menu([.. new[] { 25, 50, 75, 100 }.Select<int, (string, Action?)>(v => ($"{v}%", () => set(v)))]);
        if (command != 0) window.Invalidate();
    }

    private uint Menu(IReadOnlyList<(string Label, Action? Action)> menu)
    {
        ViewNative.GetCursorPos(out var position);
        bool wasModal = modal;
        modal = true;
        uint chosen;
        try { chosen = ViewNative.Menu(Owner, position, menu); }
        finally { modal = wasModal; }
        if (chosen > 0 && chosen <= menu.Count) Invoke(menu[(int)chosen - 1].Action);
        return chosen;
    }

    internal void Scroll(int lines)
    {
        if (lines == 0 || sizing != null) return;
        scroll = Math.Clamp(scroll + lines, 0, Math.Max(0, rows.Count - Math.Max(1, visibleCount - 1)));
        window.Invalidate();
    }

    internal bool Key(uint key)
    {
        if (key == 27) { window.Close(); return true; }
        if (key is 33 or 34) { Scroll(key == 33 ? -visibleCount : visibleCount); return true; }
        if (key is 9 or 38 or 40)
        {
            int next = hits.FindIndex(item => item.Index == active);
            int direction = key == 38 || key == 9 && IsKeyDown(0x10) ? -1 : 1;
            if (hits.Count > 0) active = hits[(next + direction + hits.Count) % hits.Count].Index;
            window.Invalidate(); return true;
        }
        var entry = hits.FirstOrDefault(hit => hit.Index == active);
        if (entry == null) return false;
        if (key is 13 or 32)
        {
            modifiers = (IsKeyDown(0x11) ? 8 : 0) | (IsKeyDown(0x10) ? 4 : 0);
            Invoke(entry.Row.Click);
            return true;
        }
        if (key is 37 or 39 && entry.Row.Slider is { } slider)
        {
            Invoke(() => slider(Math.Clamp(entry.Row.Value + (key == 37 ? -config.VolumeStep : config.VolumeStep), 0, 100)));
            return true;
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32")] private static extern short GetKeyState(int key);
    private static bool IsKeyDown(int key) => GetKeyState(key) < 0;

    private void Invoke(Action? action)
    {
        if (action == null) return;
        bool wasModal = modal;
        modal = true;
        try { action(); }
        catch (Exception error) { Notice(error.Message); }
        finally { modal = wasModal; }
    }

    private void Run(Func<Task> operation)
    {
        _ = Complete();
        async Task Complete()
        {
            try
            {
                await operation().ConfigureAwait(false);
                host.TryPost(() => { if (Owner != 0) { Refresh(); ScheduleRefresh(TimeSpan.Zero); } });
            }
            catch (Exception error) { host.TryPost(() => Notice(error.Message)); }
        }
    }

    private void Notice(string text)
    {
        if (Owner == 0) return;
        status = text;
        statusTimer?.Dispose();
        statusTimer = host.ScheduleOnce(TimeSpan.FromSeconds(6), () => { status = ""; window.Invalidate(); });
        window.Invalidate();
    }

    private void Persist()
    {
        if (!ConfigService.TrySave(config)) Notice(T("Tray.SaveFailed"));
        host.ViewSettingsChanged();
        Refresh();
    }

    internal void ExternalRefresh() { if (Owner != 0) { Refresh(); ScheduleRefresh(TimeSpan.Zero); } }

    internal void FilesDropped(IReadOnlyList<string> paths)
    {
#if SONICROUTE_PHONE
        if (phonePage && TrySendPhoneFiles(paths)) return;
#endif
        if (!panel && page is 3 or 5 && paths[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            Invoke(() => ImportConfiguration(paths[0]));
    }
}

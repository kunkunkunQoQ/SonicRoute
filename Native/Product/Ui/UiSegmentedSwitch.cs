namespace SonicRoute.Native.Ui;

internal readonly record struct UiSwitchSegment(string Id, string LabelKey, UiIcon Icon, string? ShortLabelKey = null);

internal static class UiSegmentedSwitch
{
    internal const float Padding = 2f;

    internal static void Draw(
        D2dTarget g, UiThemePalette theme, UiHitMap hit, UiRect track,
        ReadOnlySpan<UiSwitchSegment> segments, ReadOnlySpan<float> widthFractions,
        int selected, float indicatorPosition, bool vertical = false, bool showLabels = true, bool tooltips = false)
    {
        g.FillRoundedRect(track, 7f, theme.SurfaceAlt);
        g.DrawRoundedRect(track, 7f, theme.Border, 1f);

        var inner = track.Deflate(Padding);
        Span<UiRect> cells = stackalloc UiRect[segments.Length];
        float x = inner.X;
        for (int i = 0; i < cells.Length; i++)
        {
            float width = inner.Width * widthFractions[i];
            cells[i] = vertical
                ? new UiRect(inner.X, inner.Y + i * inner.Height / cells.Length, inner.Width, inner.Height / cells.Length)
                : new UiRect(x, inner.Y, width, inner.Height);
            x += width;
            if (hit.IsHover(segments[i].Id) || hit.IsActive(segments[i].Id))
                g.FillRoundedRect(cells[i], 5f, theme.NavHover);
        }

        int left = (int)MathF.Floor(indicatorPosition);
        var first = cells[left];
        var next = cells[Math.Min(left + 1, cells.Length - 1)];
        float blend = indicatorPosition - left;
        var indicator = new UiRect(first.X + (next.X - first.X) * blend, first.Y + (next.Y - first.Y) * blend,
            first.Width + (next.Width - first.Width) * blend, first.Height);
        g.FillRoundedRect(indicator, 5f, theme.SurfaceBg);
        g.DrawRoundedRect(indicator, 5f, theme.Border, 1f);

        for (int i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            var segment = segments[i];
            string label = L10n.T(segment.LabelKey);
            hit.Add(segment.Id, cell, UiCursorKind.Hand, tooltips ? label : null);
            hit.Describe(segment.Id, label, UiAccessibleRole.PageTab, i == selected);
            var color = i == selected ? theme.Accent : theme.TextPrimary;
            float iconX = showLabels ? cell.X + 4f : cell.CenterX - 6.5f;
            g.DrawIcon(segment.Icon, new UiRect(iconX, cell.CenterY - 6.5f, 13f, 13f), color, 1.6f);
            if (showLabels)
                g.DrawText(segment.ShortLabelKey is { } key ? L10n.T(key) : label, cell.Deflate(20f, 0f, 2f, 0f),
                    color, 11f, i == selected ? 600 : 400, UiTextAlign.Center, UiVertAlign.Center);
        }
    }
}

namespace SonicRoute.Native.Ui;

// Rows use content coordinates so dragging can update the insertion line on
// the same tick as edge scrolling, before the next frame is rendered.
internal sealed class UiVerticalReorder<T> where T : notnull
{
    internal readonly record struct Drop(T Source, T Anchor, bool After);
    private readonly List<(T Key, float Center, float Bottom)> _rows = [];
    private T _source = default!;
    private Drop? _target;
    private float _startY, _pointerY;

    internal bool Pressed { get; private set; }
    internal bool Dragging { get; private set; }
    internal void ClearRows() => _rows.Clear();
    internal void AddRow(T key, UiRect rect, float offset) =>
        _rows.Add((key, rect.CenterY + offset, rect.Bottom + offset));

    internal void Begin(T key, float pointerY)
    {
        _source = key;
        _startY = _pointerY = pointerY;
        Pressed = true;
        Dragging = false;
        _target = null;
    }

    internal bool Move(float pointerY, UiScrollState scroll, UiRect viewport)
    {
        if (!Pressed) return false;
        _pointerY = pointerY;
        if (!Dragging && MathF.Abs(pointerY - _startY) < 6f) return false;
        bool started = !Dragging;
        Dragging = true;
        return Retarget(scroll, viewport) || started;
    }

    internal bool Retarget(UiScrollState scroll, UiRect viewport)
    {
        if (!Dragging) return false;
        var before = _target;
        _target = null;
        if (_rows.Count != 0 && !viewport.IsEmpty)
        {
            float y = Math.Clamp(_pointerY, viewport.Y, viewport.Bottom) + scroll.Offset;
            int lo = 0, hi = _rows.Count - 1;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (y < _rows[mid].Bottom) hi = mid;
                else lo = mid + 1;
            }
            var row = _rows[lo];
            if (!EqualityComparer<T>.Default.Equals(_source, row.Key))
                _target = new(_source, row.Key, y >= row.Center);
        }
        return before != _target;
    }

    private int ScrollDirection(UiScrollState scroll, UiRect viewport)
    {
        if (!Dragging || viewport.IsEmpty) return 0;
        float edge = MathF.Min(28f, viewport.Height / 2f);
        if (_pointerY < viewport.Y + edge && scroll.Offset > 0f) return -1;
        if (_pointerY > viewport.Bottom - edge && scroll.Offset < scroll.MaxOffset) return 1;
        return 0;
    }

    internal bool NeedsScrollTick(UiScrollState scroll, UiRect viewport) => ScrollDirection(scroll, viewport) != 0;

    internal void TickScroll(UiScrollState scroll, UiRect viewport)
    {
        scroll.Offset += ScrollDirection(scroll, viewport) * 10f;
        scroll.Clamp();
        Retarget(scroll, viewport);
    }

    internal bool IsSource(T key) => Dragging && EqualityComparer<T>.Default.Equals(_source, key);

    internal void DrawInsertionLine(D2dTarget g, UiThemePalette theme, T key, UiRect rect, float gap)
    {
        if (_target is not { } target || !EqualityComparer<T>.Default.Equals(target.Anchor, key)) return;
        float y = target.After ? rect.Bottom + gap : rect.Y - gap;
        g.DrawLine(rect.X, y, rect.Right, y, theme.Accent, 2.5f);
    }

    internal Drop? End(bool commit)
    {
        var result = commit && Dragging ? _target : null;
        Pressed = Dragging = false;
        _source = default!;
        _target = null;
        return result;
    }
}

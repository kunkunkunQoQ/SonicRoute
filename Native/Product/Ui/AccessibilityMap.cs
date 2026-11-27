namespace SonicRoute.Native.Ui;

// These values are the Windows UI Automation control type identifiers.
internal enum UiAccessibleRole
{
    Button = 50000, CheckBox = 50002, ComboBox = 50003, TextBox = 50004, ListItem = 50007,
    MenuItem = 50011, RadioButton = 50013, Slider = 50015, PageTab = 50019, Text = 50020, Window = 50032
}

internal readonly record struct UiAccessibleItem(
    string Id, string Name, UiAccessibleRole Role, UiRect Bounds, bool? Selected, bool Enabled, bool IsPassword,
    UiAccessibleRange? Range = null);

internal readonly record struct UiAccessibleRange(double Value, double Maximum, double SmallChange, double LargeChange,
    bool ReadOnly, string? Subject);

internal sealed partial class UiHitMap
{
    // Allocate this only when assistive technology first asks the HWND for a provider.
    private Dictionary<string, UiAccessibleItem>? _accessibleItems;
    private Dictionary<string, Action<string, double>>? _rangeSetters;
    internal bool AccessibilityEnabled => _accessibleItems != null;
    internal void EnableAccessibility() => _accessibleItems ??= new(StringComparer.Ordinal);
    internal void DisableAccessibility() { _accessibleItems = null; _rangeSetters = null; }

    internal void DescribeRange(string id, UiRect bounds, string name, bool enabled, double? value,
        double maximum, double smallChange, double largeChange, string? subject, Action<string, double> setValue)
    {
        if (_accessibleItems == null) return;
        var visible = VisiblePart(bounds);
        if (visible.IsEmpty) return;
        UiAccessibleRange? range = value.HasValue ? new(Math.Clamp(value.Value, 0, maximum), maximum,
            smallChange, largeChange, !enabled, subject) : null;
        _accessibleItems[id] = new(id, name, UiAccessibleRole.Slider, visible, null, enabled, false, range);
        if (enabled && range != null) (_rangeSetters ??= new(StringComparer.Ordinal))[id] = setValue;
    }

    internal void DescribeVolume(string id, UiRect bounds, string targetName, bool enabled, int? percent,
        string? subject, Action<string, double> setValue) =>
        DescribeRange(id, bounds, L10n.T("Apps.Volume") + " · " + targetName, enabled, percent, 100, 1, 10, subject, setValue);

    internal void SetAccessibleRange(string id, double value)
    {
        if (_accessibleItems == null || !_accessibleItems.TryGetValue(id, out var item) || !item.Enabled ||
            item.Range is not { ReadOnly: false } range || !double.IsFinite(value) || value < 0 || value > range.Maximum ||
            _rangeSetters == null || !_rangeSetters.TryGetValue(id, out var setter)) return;
        setter(id, value);
    }

    internal void Describe(string id, string name, UiAccessibleRole role = UiAccessibleRole.Button,
        bool? selected = null, bool enabled = true)
    {
        if (_accessibleItems == null || string.IsNullOrWhiteSpace(name)) return;
        for (int i = _items.Count - 1; i >= 0; --i)
        {
            if (_items[i].Item.Id != id) continue;
            RecordAccessible(id, _items[i].Visible, name, role, selected, enabled);
            return;
        }
    }

    internal void DescribeDisabled(string id, UiRect bounds, string name, UiAccessibleRole role = UiAccessibleRole.Button,
        bool? selected = null) => DescribeControl(id, bounds, name, role, selected, false);

    internal void DescribeText(string id, UiRect bounds, string text) =>
        DescribeControl(id, bounds, text, UiAccessibleRole.Text, null, true);

    internal void DescribeControl(string id, UiRect bounds, string? name, UiAccessibleRole role, bool? selected,
        bool enabled, bool isPassword = false)
    {
        if (_accessibleItems != null && !string.IsNullOrWhiteSpace(name))
            RecordAccessible(id, VisiblePart(bounds), name, role, selected, enabled, isPassword);
    }

    private void RecordAccessible(string id, UiRect visible, string name, UiAccessibleRole role, bool? selected,
        bool enabled, bool isPassword = false)
    {
        if (!visible.IsEmpty)
            _accessibleItems![id] = new(id, name, role, visible, selected, enabled, isPassword);
    }

    internal void CopyAccessibleItems(List<UiAccessibleItem> destination)
    {
        destination.Clear();
        if (_accessibleItems == null) return;
        foreach (var item in _accessibleItems.Values)
        {
            if (!item.Enabled || item.Role == UiAccessibleRole.Text || HitTest(item.Bounds.CenterX, item.Bounds.CenterY)?.Id == item.Id)
                destination.Add(item);
        }
    }

    internal bool SetAccessibleFocus(string id, out UiHitItem item, out UiRect visible)
    {
        for (int i = 0; i < _items.Count; ++i)
        {
            if (_items[i].Item.Id != id || !IsTabStop(i)) continue;
            (item, visible, _) = _items[i];
            FocusId = id;
            ShowFocus = true;
            _keyboardScroll = null;
            return true;
        }
        item = default;
        visible = default;
        return false;
    }
}

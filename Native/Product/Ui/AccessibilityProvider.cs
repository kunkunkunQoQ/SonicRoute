using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SonicRoute.Native.Ui;

internal sealed record AccessibleEntry(UiAccessibleItem Item, AccessibleControl Control);
internal sealed record AccessibleFrame(AccessibleEntry[] Children, string? Focus, bool WindowFocused,
    UiRect Extent, float Scale, float ContentOffset)
{
    internal static readonly AccessibleFrame Empty = new([], null, false, default, 1f, 0f);
}

internal abstract unsafe class AccessibleElement : IAccessibleSimple, IAccessibleFragment
{
    internal abstract AccessibleWindow Root { get; }
    internal virtual bool Available => Root.Available;
    public int ProviderOptions(int* result) { *result = 2; return 0; } // ServerSideProvider
    public virtual int GetPatternProvider(int pattern, nint* result) { *result = 0; return 0; }
    public abstract int GetPropertyValue(int property, ComVariant* result);
    public virtual int HostProvider(nint* result) { *result = 0; return 0; }
    public abstract int Navigate(int direction, nint* result);
    public abstract int GetRuntimeId(nint* result);
    public abstract int BoundingRectangle(AccessibleRect* result);
    public int EmbeddedRoots(nint* result) { *result = 0; return 0; }
    public abstract int SetFocus();
    public int FragmentRoot(nint* result)
    {
        *result = Available ? AccessibilityApi.Pointer<IAccessibleRoot>(Root) : 0;
        return Available ? 0 : AccessibilityApi.Unavailable;
    }
}

// Providers can be called concurrently from the UI Automation worker. Reads use the last
// published render frame. Input is posted back to the window and rechecked against its live hit map.
[GeneratedComClass]
internal sealed unsafe partial class AccessibleWindow(nint hwnd) : AccessibleElement, IAccessibleRoot
{
    private readonly List<UiAccessibleItem> _scratch = [];
    private readonly ConcurrentQueue<(AccessibleControl? Control, bool Activate, double? RangeValue)> _pending = new();
    private static int _nextControlId;
    private volatile AccessibleFrame _frame = AccessibleFrame.Empty;
    internal AccessibleFrame Frame => _frame;
    internal override AccessibleWindow Root => this;
    internal override bool Available => !Frame.Extent.IsEmpty;
    internal bool WindowEnabled => Available && AccessibilityApi.IsWindowEnabled(hwnd) != 0;

    internal void Publish(UiHitMap map, UiRect extent, float scale, float offset, string? focusId, bool windowFocused)
    {
        map.CopyAccessibleItems(_scratch);
        var old = Frame;
        bool sameItems = _scratch.Count == old.Children.Length;
        for (int i = 0; sameItems && i < _scratch.Count; i++)
            sameItems = _scratch[i] == old.Children[i].Item;
        if (sameItems && old.Focus == focusId && windowFocused == old.WindowFocused &&
            extent == old.Extent && scale == old.Scale && offset == old.ContentOffset) return;

        bool structure = false;
        var entries = old.Children;
        if (!sameItems)
        {
            entries = new AccessibleEntry[_scratch.Count];
            structure = entries.Length != old.Children.Length;
            for (int i = 0; i < entries.Length; i++)
            {
                var item = _scratch[i];
                var previous = i < old.Children.Length ? old.Children[i] : null;
                if (previous?.Item.Id != item.Id || previous.Item.Role != item.Role || previous.Item.Range?.Subject != item.Range?.Subject)
                {
                    structure = true;
                    previous = Array.Find(old.Children, e => e.Item.Id == item.Id && e.Item.Role == item.Role && e.Item.Range?.Subject == item.Range?.Subject);
                }
                entries[i] = previous?.Item == item ? previous : new(item,
                    previous?.Control ?? new AccessibleControl(this, Interlocked.Increment(ref _nextControlId)));
            }
        }
        var frame = new AccessibleFrame(entries, focusId, windowFocused, extent, scale, offset);
        _frame = frame;
        if (old == AccessibleFrame.Empty || AccessibilityApi.UiaClientsAreListening() == 0) return;
        if (structure) AccessibilityApi.Announce(this, true);
        else if (!sameItems)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                var before = old.Children[i].Item;
                var after = entries[i].Item;
                if (before.Name != after.Name)
                    AccessibilityApi.Changed(entries[i].Control, AccessibilityApi.Name, ComVariant.Create(before.Name), ComVariant.Create(after.Name));
                if (before.Enabled != after.Enabled)
                    AccessibilityApi.Changed(entries[i].Control, AccessibilityApi.IsEnabled, ComVariant.Create(before.Enabled), ComVariant.Create(after.Enabled));
                if (before.Range is { } prior && after.Range is { } current && prior.Value != current.Value)
                    AccessibilityApi.Changed(entries[i].Control, AccessibilityApi.RangeValue, ComVariant.Create(prior.Value), ComVariant.Create(current.Value));
                if (before.Selected is bool was && after.Selected is bool now && was != now)
                {
                    bool toggle = AccessibleControl.IsToggle(after);
                    AccessibilityApi.Changed(entries[i].Control, toggle ? AccessibilityApi.ToggleToggleState : AccessibilityApi.SelectionItemIsSelected,
                        toggle ? ComVariant.Create(was ? 1 : 0) : ComVariant.Create(was),
                        toggle ? ComVariant.Create(now ? 1 : 0) : ComVariant.Create(now));
                }
            }
        }
        if (frame.WindowFocused && (old.Focus != frame.Focus || !old.WindowFocused))
            AccessibilityApi.Announce((IAccessibleSimple?)Focused(frame) ?? this, false);
    }

    internal void Close()
    {
        _frame = AccessibleFrame.Empty;
        while (_pending.TryDequeue(out _)) { }
        AccessibilityApi.UiaReturnRawElementProvider(hwnd, 0, 0, 0);
    }

    internal AccessibleRect Rectangle(UiRect rect, AccessibleFrame frame, bool child)
    {
        var origin = new ScreenPoint();
        if (!ClientToScreen(hwnd, ref origin)) return default;
        return new(origin.X + rect.X * frame.Scale,
            origin.Y + (rect.Y + (child ? frame.ContentOffset : 0f)) * frame.Scale,
            rect.Width * frame.Scale, rect.Height * frame.Scale);
    }

    internal int Queue(AccessibleControl? item, bool activate, double? rangeValue = null)
    {
        if (!Available) return AccessibilityApi.Unavailable;
        if (!WindowEnabled) return AccessibilityApi.Disabled;
        if (item != null)
        {
            var entry = item.Entry;
            if (entry == null) return AccessibilityApi.Unavailable;
            if (!entry.Item.Enabled) return AccessibilityApi.Disabled;
            if (entry.Item.Role == UiAccessibleRole.Text) return AccessibilityApi.InvalidOperation;
            if (rangeValue is double value)
            {
                if (entry.Item.Range is not { ReadOnly: false } range) return AccessibilityApi.InvalidOperation;
                if (!double.IsFinite(value) || value < 0 || value > range.Maximum) return unchecked((int)0x80070057);
            }
        }
        _pending.Enqueue((item, activate, rangeValue));
        return PostMessageW(hwnd, LayeredWindow.AccessibleActionMessage, 0, 0) ? 0 : AccessibilityApi.Unavailable;
    }

    internal void Dispatch(LayeredWindow window)
    {
        while (_pending.TryDequeue(out var request))
        {
            if (!WindowEnabled || !window.IsVisible || window.IsMinimized) continue;
            window.RefreshAccessibleInput();
            var entry = request.Control?.Entry;
            if (request.Control != null && (entry == null || !entry.Item.Enabled)) continue;
            if (request.RangeValue is double value && entry != null) window.PerformAccessibleRange(entry.Item.Id, value);
            else window.PerformAccessibleAction(entry?.Item.Id, request.Activate);
        }
    }

    private static AccessibleControl? Focused(AccessibleFrame frame) => !frame.WindowFocused ? null :
        Array.Find(frame.Children, e => e.Item.Id == frame.Focus)?.Control;

    public override int HostProvider(nint* result)
    {
        *result = 0;
        return Available ? AccessibilityApi.UiaHostProviderFromHwnd(hwnd, result) : AccessibilityApi.Unavailable;
    }

    public override int GetPropertyValue(int property, ComVariant* result)
    {
        *result = default;
        if (!Available) return AccessibilityApi.Unavailable;
        if (property == AccessibilityApi.FrameworkId) *result = ComVariant.Create("Win32");
        return 0; // Window title, role, etc. come from the real HWND host provider.
    }

    public override int Navigate(int direction, nint* result)
    {
        var frame = Frame;
        AccessibleControl? item = direction switch { 3 => frame.Children.FirstOrDefault()?.Control,
            4 => frame.Children.LastOrDefault()?.Control, _ => null };
        *result = AccessibilityApi.Pointer<IAccessibleFragment>(item);
        return frame.Extent.IsEmpty ? AccessibilityApi.Unavailable : 0;
    }

    public override int BoundingRectangle(AccessibleRect* result)
    {
        var frame = Frame;
        *result = Rectangle(frame.Extent, frame, false);
        return frame.Extent.IsEmpty ? AccessibilityApi.Unavailable : 0;
    }

    public override int GetRuntimeId(nint* result) { *result = 0; return 0; } // HWND runtime ID
    public override int SetFocus() => Queue(null, false);
    public int GetFocus(nint* result)
    {
        *result = AccessibilityApi.Pointer<IAccessibleFragment>(Focused(Frame));
        return Available ? 0 : AccessibilityApi.Unavailable;
    }
    public int FromPoint(double x, double y, nint* result)
    {
        *result = 0;
        var frame = Frame;
        if (frame.Extent.IsEmpty) return AccessibilityApi.Unavailable;
        if (!Rectangle(frame.Extent, frame, false).Contains(x, y)) return 0;
        for (int i = frame.Children.Length - 1; i >= 0; --i)
        {
            var entry = frame.Children[i];
            if (!Rectangle(entry.Item.Bounds, frame, true).Contains(x, y)) continue;
            *result = AccessibilityApi.Pointer<IAccessibleFragment>(entry.Control);
            return 0;
        }
        *result = AccessibilityApi.Pointer<IAccessibleFragment>(this);
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
    [DllImport("user32")] private static extern bool ClientToScreen(nint hwnd, ref ScreenPoint point);
    [DllImport("user32")] private static extern bool PostMessageW(nint hwnd, uint message, nuint wp, nint lp);
}

[GeneratedComClass]
internal sealed unsafe partial class AccessibleControl(AccessibleWindow owner, int serial) : AccessibleElement,
    IAccessibleInvoke, IAccessibleToggle, IAccessibleSelectionItem, IAccessibleRangeValue
{
    internal override AccessibleWindow Root => owner;
    internal AccessibleEntry? Entry => Array.Find(owner.Frame.Children, e => ReferenceEquals(e.Control, this));
    internal override bool Available => Entry != null;
    internal static bool IsToggle(UiAccessibleItem item) => item.Role == UiAccessibleRole.CheckBox ||
        item.Role == UiAccessibleRole.MenuItem && item.Selected.HasValue;
    private static bool IsChoice(UiAccessibleItem item) => item.Selected.HasValue &&
        item.Role is UiAccessibleRole.PageTab or UiAccessibleRole.RadioButton or UiAccessibleRole.ListItem;

    public override int GetPropertyValue(int property, ComVariant* result)
    {
        *result = default;
        if (Entry is not { Item: var item }) return AccessibilityApi.Unavailable;
        bool enabled = item.Enabled && owner.WindowEnabled;
        *result = property switch
        {
            AccessibilityApi.Name => ComVariant.Create(item.Name),
            // The internal hit key can contain a private path, endpoint or fingerprint.
            AccessibilityApi.AutomationId => ComVariant.Create("SonicRouteControl" + serial.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            AccessibilityApi.ControlType => ComVariant.Create((int)item.Role),
            AccessibilityApi.IsKeyboardFocusable => ComVariant.Create(enabled && item.Role != UiAccessibleRole.Text),
            AccessibilityApi.HasKeyboardFocus => ComVariant.Create(owner.Frame.WindowFocused && owner.Frame.Focus == item.Id),
            AccessibilityApi.IsEnabled => ComVariant.Create(enabled),
            AccessibilityApi.IsPassword => ComVariant.Create(item.IsPassword),
            AccessibilityApi.RangeValue when item.Range is { } r => ComVariant.Create(r.Value),
            AccessibilityApi.RangeReadOnly when item.Range is { } r => ComVariant.Create(r.ReadOnly || !enabled),
            AccessibilityApi.RangeMaximum when item.Range is { } r => ComVariant.Create(r.Maximum),
            AccessibilityApi.RangeMinimum when item.Range.HasValue => ComVariant.Create(0d),
            AccessibilityApi.RangeLargeChange when item.Range is { } r => ComVariant.Create(r.LargeChange),
            AccessibilityApi.RangeSmallChange when item.Range is { } r => ComVariant.Create(r.SmallChange),
            AccessibilityApi.IsContentElement or AccessibilityApi.IsControlElement => ComVariant.Create(true),
            AccessibilityApi.IsOffscreen => ComVariant.Create(false),
            AccessibilityApi.FrameworkId => ComVariant.Create("Win32"),
            AccessibilityApi.ToggleToggleState when IsToggle(item) => ComVariant.Create(item.Selected == true ? 1 : 0),
            AccessibilityApi.SelectionItemIsSelected when IsChoice(item) => ComVariant.Create(item.Selected == true),
            _ => default
        };
        return 0;
    }

    public override int GetPatternProvider(int pattern, nint* result)
    {
        *result = 0;
        if (Entry is not { Item: var item }) return AccessibilityApi.Unavailable;
        *result = pattern switch
        {
            AccessibilityApi.TogglePattern when IsToggle(item) => AccessibilityApi.Pointer<IAccessibleToggle>(this),
            AccessibilityApi.RangeValuePattern when item.Range.HasValue => AccessibilityApi.Pointer<IAccessibleRangeValue>(this),
            AccessibilityApi.SelectionItemPattern when IsChoice(item) => AccessibilityApi.Pointer<IAccessibleSelectionItem>(this),
            AccessibilityApi.InvokePattern when item.Role is UiAccessibleRole.Button or UiAccessibleRole.ListItem or
                UiAccessibleRole.ComboBox or UiAccessibleRole.MenuItem or UiAccessibleRole.TextBox => AccessibilityApi.Pointer<IAccessibleInvoke>(this),
            _ => 0
        };
        return 0;
    }

    public override int Navigate(int direction, nint* result)
    {
        *result = 0;
        var entries = owner.Frame.Children;
        int index = Array.FindIndex(entries, e => ReferenceEquals(e.Control, this));
        if (index < 0) return AccessibilityApi.Unavailable;
        IAccessibleFragment? next = direction switch { 0 => owner, 1 when index + 1 < entries.Length => entries[index + 1].Control,
            2 when index > 0 => entries[index - 1].Control, _ => null };
        *result = AccessibilityApi.Pointer(next);
        return 0;
    }

    public override int GetRuntimeId(nint* result) => AccessibilityApi.RuntimeId(serial, result);
    public override int BoundingRectangle(AccessibleRect* result)
    {
        *result = default;
        var frame = owner.Frame;
        var entry = Array.Find(frame.Children, e => ReferenceEquals(e.Control, this));
        if (entry == null) return AccessibilityApi.Unavailable;
        *result = owner.Rectangle(entry.Item.Bounds, frame, true);
        return 0;
    }
    public override int SetFocus() => owner.Queue(this, false);
    public int Invoke() => owner.Queue(this, true);
    public int Toggle() => owner.Queue(this, true);
    public int Select() => Entry is { Item: { Selected: true, Enabled: true } } && owner.WindowEnabled ? 0 : owner.Queue(this, true);
    public int AddToSelection() => Select();
    public int RemoveFromSelection() => AccessibilityApi.InvalidOperation;
    public int IsSelected(int* result)
    {
        var entry = Entry;
        *result = entry?.Item.Selected == true ? 1 : 0;
        return entry == null ? AccessibilityApi.Unavailable : 0;
    }
    public int ToggleState(int* result) => IsSelected(result);
    public int SelectionContainer(nint* result) { *result = 0; return Available ? 0 : AccessibilityApi.Unavailable; }

    public int SetValue(double value) => owner.Queue(this, false, value);
    public int IsReadOnly(int* value)
    {
        var item = Entry?.Item;
        *value = item?.Range is { ReadOnly: false } && item.Value.Enabled && owner.WindowEnabled ? 0 : 1;
        return item?.Range == null ? AccessibilityApi.Unavailable : 0;
    }
    private int GetRangeNumber(int property, double* value)
    {
        *value = 0;
        if (Entry?.Item.Range is not { } range) return AccessibilityApi.Unavailable;
        *value = property switch { AccessibilityApi.RangeMaximum => range.Maximum, AccessibilityApi.RangeMinimum => 0,
            AccessibilityApi.RangeLargeChange => range.LargeChange, AccessibilityApi.RangeSmallChange => range.SmallChange, _ => range.Value };
        return 0;
    }
    public int RangeValue(double* value) => GetRangeNumber(AccessibilityApi.RangeValue, value);
    public int Maximum(double* value) => GetRangeNumber(AccessibilityApi.RangeMaximum, value);
    public int Minimum(double* value) => GetRangeNumber(AccessibilityApi.RangeMinimum, value);
    public int LargeChange(double* value) => GetRangeNumber(AccessibilityApi.RangeLargeChange, value);
    public int SmallChange(double* value) => GetRangeNumber(AccessibilityApi.RangeSmallChange, value);
}

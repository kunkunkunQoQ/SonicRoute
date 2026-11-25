using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Win = SonicRoute.Native.Product.ViewNative;

namespace SonicRoute.Native.Product;

[GeneratedComInterface, Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
internal partial interface IViewUia
{
    [PreserveSig] int ProviderOptions(out int value);
    [PreserveSig] int GetPattern(int id, out nint value);
    [PreserveSig] int GetProperty(int id, out UiaVariant value);
    [PreserveSig] int GetHostProvider(out nint value);
}

[GeneratedComInterface, Guid("f7063da8-8359-439c-9297-bbc5299a7d87")]
internal partial interface IViewFragment
{
    [PreserveSig] int Navigate(int direction, out nint value);
    [PreserveSig] int GetRuntimeId(out nint value);
    [PreserveSig] int GetBounds(out UiaBounds value);
    [PreserveSig] int GetEmbeddedRoots(out nint value);
    [PreserveSig] int SetFocus();
    [PreserveSig] int GetRoot(out nint value);
}

[GeneratedComInterface, Guid("620ce2a5-ab8f-40a9-86cb-de3c75599b58")]
internal partial interface IViewFragmentRoot
{
    [PreserveSig] int FromPoint(double x, double y, out nint value);
    [PreserveSig] int GetFocus(out nint value);
}

[GeneratedComInterface, Guid("54fcb24b-e18e-47a2-b4d3-eccbe77599a2")]
internal partial interface IViewInvoke
{
    [PreserveSig] int Invoke();
}

[GeneratedComInterface, Guid("36dc7aef-33e6-4691-afe1-2be7274b3d33")]
internal partial interface IViewRangeValue
{
    [PreserveSig] int SetValue(double value);
    [PreserveSig] int GetValue(out double value);
    [PreserveSig] int IsReadOnly(out int value);
    [PreserveSig] int GetMaximum(out double value);
    [PreserveSig] int GetMinimum(out double value);
    [PreserveSig] int GetLargeChange(out double value);
    [PreserveSig] int GetSmallChange(out double value);
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct UiaVariant
{
    [FieldOffset(0)] internal ushort Kind;
    [FieldOffset(8)] internal nint Pointer;
    [FieldOffset(8)] internal int Integer;
    [FieldOffset(8)] internal short Flag;
    internal static UiaVariant Text(string text) => new() { Kind = 8, Pointer = Marshal.StringToBSTR(text) };
    internal static UiaVariant Number(int value) => new() { Kind = 3, Integer = value };
    internal static UiaVariant Boolean(bool value) => new() { Kind = 11, Flag = (short)(value ? -1 : 0) };
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct UiaBounds(double X, double Y, double Width, double Height)
{
    internal bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

internal sealed record UiaRow(int Id, string Label, string Detail, UiaBounds Bounds, Action? Click, Action<int>? Slider,
    int Level, bool Selected);

[GeneratedComClass]
internal sealed partial class ViewUiaProvider : IViewUia, IViewFragment, IViewFragmentRoot, IViewInvoke, IViewRangeValue
{
    private const int Unavailable = unchecked((int)0x80040201);
    private readonly ProductViews view;
    private readonly nint window;
    private readonly ViewUiaProvider root;
    private readonly int? id;
    private readonly string title;
    private readonly UiaRow? initial;
    private UiaRow[] snapshot = [];
    private UiaRow? Current => id is int key ? root.Rows.FirstOrDefault(row => row.Id == key && row.Label == initial?.Label) : null;
    private UiaRow[] Rows => Volatile.Read(ref snapshot);

    internal ViewUiaProvider(ProductViews view, nint window, string title)
    {
        this.view = view;
        this.window = window;
        this.title = title;
        root = this;
    }

    private ViewUiaProvider(ViewUiaProvider root, UiaRow row)
        : this(root.view, root.window, root.title) => (this.root, id, initial) = (root, row.Id, row);

    internal void Update(UiaRow[] items) => Volatile.Write(ref snapshot, items);

    internal unsafe nint GetObject(nuint w, nint l)
    {
        void* pointer = ComInterfaceMarshaller<IViewUia>.ConvertToUnmanaged(this);
        try { return UiaReturnRawElementProvider(window, w, l, (nint)pointer); }
        finally { ComInterfaceMarshaller<IViewUia>.Free(pointer); }
    }

    public int ProviderOptions(out int value) { value = 2; return 0; }

    public unsafe int GetPattern(int pattern, out nint value)
    {
        var item = Current;
        value = pattern switch
        {
            10000 when item?.Click != null => (nint)ComInterfaceMarshaller<IViewInvoke>.ConvertToUnmanaged(this),
            10003 when item?.Slider != null => (nint)ComInterfaceMarshaller<IViewRangeValue>.ConvertToUnmanaged(this),
            _ => 0
        };
        return 0;
    }

    public int GetProperty(int property, out UiaVariant value)
    {
        var item = Current;
        value = property switch
        {
            30020 when id == null => UiaVariant.Number(unchecked((int)window)),
            30003 => UiaVariant.Number(id == null ? 50032 : item?.Slider != null ? 50015 : 50000),
            30005 => UiaVariant.Text(item?.Label ?? title),
            30008 => UiaVariant.Boolean(id != null && view.AccessibleFocus == id),
            30009 or 30010 or 30016 or 30017 => UiaVariant.Boolean(true),
            30011 when id != null => UiaVariant.Text(id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            30013 when item != null => UiaVariant.Text(item.Detail),
            30022 => UiaVariant.Boolean(id != null && item == null),
            30079 when item != null => UiaVariant.Boolean(item.Selected),
            _ => default
        };
        return 0;
    }

    public int GetHostProvider(out nint value)
    {
        value = 0;
        return id == null ? UiaHostProviderFromHwnd(window, out value) : 0;
    }

    private static unsafe nint Fragment(ViewUiaProvider? provider) =>
        provider == null ? 0 : (nint)ComInterfaceMarshaller<IViewFragment>.ConvertToUnmanaged(provider);

    private nint Fragment(UiaRow? row) => row == null ? 0 : Fragment(new ViewUiaProvider(root, row));

    public int Navigate(int direction, out nint value)
    {
        var entries = root.Rows;
        int index = id.HasValue ? Array.FindIndex(entries, item => item.Id == id && item.Label == initial?.Label) : -1;
        value = direction switch
        {
            0 when id.HasValue => Fragment(root),
            1 when index >= 0 && index + 1 < entries.Length => Fragment(entries[index + 1]),
            2 when index > 0 => Fragment(entries[index - 1]),
            3 when id == null && entries.Length > 0 => Fragment(entries[0]),
            4 when id == null && entries.Length > 0 => Fragment(entries[^1]),
            _ => 0
        };
        return 0;
    }

    public int GetRuntimeId(out nint value)
    {
        value = id.HasValue ? SafeArrayCreateVector(3, 0, 2) : 0;
        if (value != 0)
        {
            int append = 3, key = id!.Value;
            SafeArrayPutElement(value, 0, in append);
            SafeArrayPutElement(value, 1, in key);
        }
        return 0;
    }

    public int GetBounds(out UiaBounds value)
    {
        value = Current?.Bounds ?? default;
        if (id == null && Win.GetClientRect(window, out var area) != 0)
        {
            Win.Point point = default;
            Win.ClientToScreen(window, ref point);
            value = new(point.X, point.Y, area.Width, area.Height);
        }
        return 0;
    }

    public int GetEmbeddedRoots(out nint value) { value = 0; return 0; }

    public int SetFocus() => view.PostAccessible(window, Current, null, setFocus: true) ? 0 : Unavailable;

    public unsafe int GetRoot(out nint value)
    {
        value = (nint)ComInterfaceMarshaller<IViewFragmentRoot>.ConvertToUnmanaged(root);
        return 0;
    }

    public int FromPoint(double x, double y, out nint value)
    {
        var item = root.Rows.FirstOrDefault(row => row.Bounds.Contains(x, y));
        value = item == null ? Fragment(root) : Fragment(item);
        return 0;
    }
    public int GetFocus(out nint value) { value = Fragment(root.Rows.FirstOrDefault(row => row.Id == view.AccessibleFocus)); return 0; }

    public int Invoke() => Current is { Click: not null } item && view.PostAccessible(window, item, null, setFocus: false) ? 0 : Unavailable;

    public int SetValue(double value) => double.IsFinite(value) && value is >= 0 and <= 100 &&
        Current is { Slider: not null } item && view.PostAccessible(window, item, (int)Math.Round(value), setFocus: false) ? 0 : Unavailable;

    public int GetValue(out double value) { value = Current?.Level ?? 0; return 0; }
    public int IsReadOnly(out int value) { value = Current?.Slider == null ? 1 : 0; return 0; }
    public int GetMaximum(out double value) { value = 100; return 0; }
    public int GetMinimum(out double value) { value = 0; return 0; }
    public int GetLargeChange(out double value) { value = 10; return 0; }
    public int GetSmallChange(out double value) { value = 1; return 0; }

    [DllImport("UIAutomationCore.dll")] private static extern nint UiaReturnRawElementProvider(nint window, nuint w, nint l, nint provider);
    [DllImport("UIAutomationCore.dll")] private static extern int UiaHostProviderFromHwnd(nint window, out nint provider);
    [DllImport("oleaut32.dll")] private static extern nint SafeArrayCreateVector(ushort type, int lowerBound, uint count);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayPutElement(nint array, in int index, in int value);
}

internal sealed partial class ProductViews
{
    private ViewUiaProvider? accessibility;
    internal int AccessibleFocus => active;

    internal nint GetAccessibleObject(nuint w, nint l)
    {
        accessibility ??= new(this, Owner, panel ? T("Tray.OpenPanel") : BuildIdentity.DisplayName);
        UpdateAccessibleRows();
        return accessibility.GetObject(w, l);
    }

    private void UpdateAccessibleRows()
    {
        if (accessibility == null) return;
        Win.Point origin = default;
        Win.ClientToScreen(Owner, ref origin);
        float scale = Math.Max(96, Win.GetDpiForWindow(Owner)) / 96f;
        accessibility.Update(hits.Select(hit => new UiaRow(hit.Index, hit.Row.Title, hit.Row.Detail,
            new UiaBounds(origin.X + hit.Area.X * scale, origin.Y + hit.Area.Y * scale, hit.Area.Width * scale, hit.Area.Height * scale),
            hit.Row.Click, hit.Row.Slider, hit.Row.Value, hit.Row.Selected)).ToArray());
    }

    private void DetachAccessibility()
    {
        accessibility?.Update([]);
        accessibility = null;
    }

    internal bool PostAccessible(nint expectedWindow, UiaRow? item, int? value, bool setFocus) => host.TryPost(() =>
    {
        if (Owner == 0 || Owner != expectedWindow) return;
        if (setFocus)
        {
            if (item != null) active = item.Id;
            Win.SetForegroundWindow(Owner);
            window.Invalidate();
        }
        else if (item != null && hits.Any(hit => hit.Index == item.Id && hit.Row.Title == item.Label))
        {
            if (value.HasValue) Invoke(() => item.Slider?.Invoke(value.Value));
            else Invoke(item.Click);
        }
    });
}

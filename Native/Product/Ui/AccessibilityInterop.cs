using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.CompilerServices;

namespace SonicRoute.Native.Ui;

// The Windows SDK's UIAutomationCore interfaces are separate IUnknown interfaces.
// Source-generated callable wrappers keep these same interfaces available in Native AOT.
[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper), Guid("D6DD68D1-86FD-4332-8666-9ABEDEA2D24C")]
internal unsafe partial interface IAccessibleSimple
{
    [PreserveSig] int ProviderOptions(int* result);
    [PreserveSig] int GetPatternProvider(int pattern, nint* result);
    [PreserveSig] int GetPropertyValue(int property, ComVariant* result);
    [PreserveSig] int HostProvider(nint* result);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper), Guid("F7063DA8-8359-439C-9297-BBC5299A7D87")]
internal unsafe partial interface IAccessibleFragment
{
    [PreserveSig] int Navigate(int direction, nint* result);
    [PreserveSig] int GetRuntimeId(nint* result);
    [PreserveSig] int BoundingRectangle(AccessibleRect* result);
    [PreserveSig] int EmbeddedRoots(nint* result);
    [PreserveSig] int SetFocus();
    [PreserveSig] int FragmentRoot(nint* result);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper), Guid("620CE2A5-AB8F-40A9-86CB-DE3C75599B58")]
internal unsafe partial interface IAccessibleRoot
{
    [PreserveSig] int FromPoint(double x, double y, nint* result);
    [PreserveSig] int GetFocus(nint* result);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper), Guid("54FCB24B-E18E-47A2-B4D3-ECCBE77599A2")]
internal partial interface IAccessibleInvoke
{
    [PreserveSig] int Invoke();
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper), Guid("56D00BD0-C4F4-433C-A836-1A52A57E0892")]
internal unsafe partial interface IAccessibleToggle
{
    [PreserveSig] int Toggle();
    [PreserveSig] int ToggleState(int* result);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper), Guid("2ACAD808-B2D4-452D-A407-91FF1AD167B2")]
internal unsafe partial interface IAccessibleSelectionItem
{
    [PreserveSig] int Select();
    [PreserveSig] int AddToSelection();
    [PreserveSig] int RemoveFromSelection();
    [PreserveSig] int IsSelected(int* result);
    [PreserveSig] int SelectionContainer(nint* result);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper), Guid("36DC7AEF-33E6-4691-AFE1-2BE7274B3D33")]
internal unsafe partial interface IAccessibleRangeValue
{
    [PreserveSig] int SetValue(double value);
    [PreserveSig] int RangeValue(double* value);
    [PreserveSig] int IsReadOnly(int* value);
    [PreserveSig] int Maximum(double* value);
    [PreserveSig] int Minimum(double* value);
    [PreserveSig] int LargeChange(double* value);
    [PreserveSig] int SmallChange(double* value);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct AccessibleRect(double Left, double Top, double Width, double Height)
{
    internal bool Contains(double x, double y) =>
        x >= Left && x < Left + Width && y >= Top && y < Top + Height;
}

internal static unsafe partial class AccessibilityApi
{
    internal const int Unavailable = unchecked((int)0x80040201);
    internal const int Disabled = unchecked((int)0x80040200);
    internal const int InvalidOperation = unchecked((int)0x80131509);
    internal const int InvokePattern = 10000, RangeValuePattern = 10003, SelectionItemPattern = 10010, TogglePattern = 10015;
    internal const int RangeValue = 30047, RangeReadOnly = 30048, RangeMinimum = 30049, RangeMaximum = 30050,
        RangeLargeChange = 30051, RangeSmallChange = 30052;
    internal const int ControlType = 30003, Name = 30005, HasKeyboardFocus = 30008, IsKeyboardFocusable = 30009,
        IsEnabled = 30010, AutomationId = 30011, IsControlElement = 30016, IsContentElement = 30017, IsPassword = 30019,
        IsOffscreen = 30022, FrameworkId = 30024, SelectionItemIsSelected = 30079, ToggleToggleState = 30086;

    internal static nint Pointer<T>(T? provider) where T : class =>
        (nint)ComInterfaceMarshaller<T>.ConvertToUnmanaged(provider);

    internal static nint Return(nint hwnd, nuint wParam, nint lParam, AccessibleWindow provider)
    {
        nint pointer = Pointer<IAccessibleSimple>(provider);
        try { return UiaReturnRawElementProvider(hwnd, wParam, lParam, pointer); }
        finally { ComInterfaceMarshaller<IAccessibleSimple>.Free((void*)pointer); }
    }

    internal static int RuntimeId(int id, nint* result)
    {
        // The HWND supplies the first portion of each fragment's runtime ID.
        *result = 0;
        nint array = SafeArrayCreateVector(3 /* VT_I4 */, 0, 2);
        if (array == 0) return unchecked((int)0x8007000E);
        int* data;
        int hr = SafeArrayAccessData(array, (void**)&data);
        if (hr < 0) { SafeArrayDestroy(array); return hr; }
        data[0] = 3; // UiaAppendRuntimeId
        data[1] = id;
        SafeArrayUnaccessData(array);
        *result = array;
        return 0;
    }

    internal static void Announce(IAccessibleSimple provider, bool structure)
    {
        nint pointer = Pointer(provider);
        try
        {
            if (structure) UiaRaiseStructureChangedEvent(pointer, 2 /* ChildrenInvalidated */, null, 0);
            else UiaRaiseAutomationEvent(pointer, 20005 /* AutomationFocusChanged */);
        }
        finally { ComInterfaceMarshaller<IAccessibleSimple>.Free((void*)pointer); }
    }

    internal static void Changed(IAccessibleSimple provider, int property, ComVariant previous, ComVariant current)
    {
        nint pointer = Pointer(provider);
        try
        {
            UiaRaiseAutomationPropertyChangedEvent(pointer, property,
                Unsafe.As<ComVariant, NativeVariant>(ref previous), Unsafe.As<ComVariant, NativeVariant>(ref current));
        }
        finally
        {
            previous.Dispose();
            current.Dispose();
            ComInterfaceMarshaller<IAccessibleSimple>.Free((void*)pointer);
        }
    }

    [LibraryImport("UIAutomationCore")] internal static partial nint UiaReturnRawElementProvider(nint hwnd, nuint wParam, nint lParam, nint provider);
    [LibraryImport("UIAutomationCore")] internal static partial int UiaHostProviderFromHwnd(nint hwnd, nint* result);
    [LibraryImport("UIAutomationCore")] internal static partial int UiaClientsAreListening();
    [LibraryImport("UIAutomationCore")] private static partial int UiaRaiseStructureChangedEvent(nint provider, int change, int* id, int length);
    [LibraryImport("UIAutomationCore")] private static partial int UiaRaiseAutomationEvent(nint provider, int eventId);
    // VARIANT passed by value: the native union accommodates a record and its IRecordInfo pointer.
    [StructLayout(LayoutKind.Sequential)] private struct NativeVariant { internal long Header; internal nint Value, RecordInfo; }
    [LibraryImport("UIAutomationCore")] private static partial int UiaRaiseAutomationPropertyChangedEvent(nint provider, int property, NativeVariant before, NativeVariant after);
    [LibraryImport("oleaut32")] private static partial nint SafeArrayCreateVector(ushort type, int start, uint length);
    [LibraryImport("oleaut32")] private static partial int SafeArrayAccessData(nint array, void** data);
    [LibraryImport("oleaut32")] private static partial int SafeArrayUnaccessData(nint array);
    [LibraryImport("oleaut32")] private static partial int SafeArrayDestroy(nint array);
    [LibraryImport("user32")] internal static partial int IsWindowEnabled(nint hwnd);
    [LibraryImport("user32")] internal static partial nint GetFocus();
}

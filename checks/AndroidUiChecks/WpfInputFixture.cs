using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

internal static partial class Program
{
    // Feed pointer reports into this WPF dispatcher. No physical cursor movement or system input injection.
    private static void ReportWpfMouse(FrameworkElement element, Point at, string actions)
    {
        var source = (HwndSource)PresentationSource.FromVisual(element)!;
        var client = source.CompositionTarget!.TransformToDevice.Transform(element.TranslatePoint(at, (UIElement)source.RootVisual));
        var assembly = typeof(Mouse).Assembly;
        Type InputType(string name) => assembly.GetType("System.Windows.Input." + name, true)!;
        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        object report = Activator.CreateInstance(InputType("RawMouseInputReport"), instance, null,
            [Enum.Parse(InputType("InputMode"), "Foreground"), Environment.TickCount, source,
             Enum.Parse(InputType("RawMouseActions"), actions), (int)Math.Round(client.X), (int)Math.Round(client.Y), 0, IntPtr.Zero], null)!;
        var args = (InputEventArgs)Activator.CreateInstance(InputType("InputReportEventArgs"), instance, null, [null, report], null)!;
        args.RoutedEvent = (RoutedEvent)typeof(InputManager).GetField("PreviewInputReportEvent", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        InputManager.Current.ProcessInput(args);
    }

    [DllImport("user32.dll")] private static extern bool GetKeyboardState([Out] byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    // Drive the drop target registered with OLE. No cursor movements, injected input or clipboard writes.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OleEnter(nint target, nint data, int keys, long position, ref int effects);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OleOver(nint target, int keys, long position, ref int effects);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OleLeave(nint target);

    private static DragDropEffects OleDragOver(Window window, FrameworkElement zone, DataObject data, double fraction,
        bool drop = false, Action? over = null)
    {
        nint target = GetProp(new WindowInteropHelper(window).Handle, "OleDropTargetInterface");
        if (target == 0) throw new InvalidOperationException("The window has no OLE drop target.");
        nint unknown = Marshal.GetIUnknownForObject(data);
        nint native = 0;
        try
        {
            Guid iid = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out native));
            var point = zone.PointToScreen(new Point(zone.ActualWidth * .5, zone.ActualHeight * fraction));
            long coordinates = ((long)(int)point.Y << 32) | (uint)(int)point.X;
            nint vtable = Marshal.ReadIntPtr(target);
            T Entry<T>(int slot) where T : Delegate =>
                Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
            int effects = (int)(DragDropEffects.Copy | DragDropEffects.Move);
            Marshal.ThrowExceptionForHR(Entry<OleEnter>(3)(target, native, 1, coordinates, ref effects));
            bool dropped = false;
            try
            {
                effects = (int)(DragDropEffects.Copy | DragDropEffects.Move);
                Marshal.ThrowExceptionForHR(Entry<OleOver>(4)(target, 1, coordinates, ref effects));
                over?.Invoke();
                if (drop)
                {
                    effects = (int)(DragDropEffects.Copy | DragDropEffects.Move);
                    dropped = true;
                    Marshal.ThrowExceptionForHR(Entry<OleEnter>(6)(target, native, 0, coordinates, ref effects));
                }
                return (DragDropEffects)effects;
            }
            finally { if (!dropped) Entry<OleLeave>(5)(target); }
        }
        finally
        {
            if (native != 0) Marshal.Release(native);
            Marshal.Release(unknown);
        }
    }
}

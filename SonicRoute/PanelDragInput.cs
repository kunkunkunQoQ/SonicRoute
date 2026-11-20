#if SONICROUTE_PHONE
using System.Windows;
using Point = System.Windows.Point;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using IDataObject = System.Windows.IDataObject;

namespace SonicRoute;

/// <summary>Panel routing uses the same payload and positions for OLE mouse drops and captured touch drops.</summary>
internal readonly struct PanelDragInput
{
    private readonly DragEventArgs? _native;
    private readonly Point _screen;
    internal IDataObject Data { get; }

    internal PanelDragInput(DragEventArgs native) { _native = native; Data = native.Data; _screen = default; }
    internal PanelDragInput(IDataObject data, Point screen) { _native = null; Data = data; _screen = screen; }
    internal Point GetPosition(FrameworkElement target) => _native != null ? _native.GetPosition(target) : target.PointFromScreen(_screen);
    internal DragDropEffects Effects { set { if (_native != null) _native.Effects = value; } }
    internal bool Handled { set { if (_native != null) _native.Handled = value; } }
}
#endif

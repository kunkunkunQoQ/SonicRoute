namespace SonicRoute.Native.Ui;

internal abstract unsafe partial class LayeredWindow
{
    internal const uint AccessibleActionMessage = 0x8000 + 0x391;
    private AccessibleWindow? _accessibility;
    protected virtual string? AccessibleFocusId => InputMap?.FocusId;
    protected virtual bool AccessibleWindowFocused => AccessibilityApi.GetFocus() == Handle;

    private nint ReturnAccessibleProvider(nuint wParam, nint lParam, UiHitMap map)
    {
        if (_accessibility == null)
        {
            _accessibility = new AccessibleWindow(Handle);
            map.EnableAccessibility();
            RenderNow();
        }
        return AccessibilityApi.Return(Handle, wParam, lParam, _accessibility);
    }

    private void ReleaseAccessibility()
    {
        if (_accessibility == null) return;
        var provider = _accessibility;
        _accessibility = null;
        InputMap?.DisableAccessibility();
        provider.Close();
    }

    internal void RefreshAccessibleInput()
    {
        if (_paintPending) RenderNow();
    }

    internal void PerformAccessibleRange(string id, double value)
    {
        if (_leftClickCount == 0) InputMap?.SetAccessibleRange(id, value);
    }

    internal void PerformAccessibleAction(string? id, bool activate)
    {
        if (id == null) { RequestFocus(); return; }
        PerformAccessibleItem(id, activate);
    }

    protected virtual void PerformAccessibleItem(string id, bool activate)
    {
        if (InputMap is not { } hit || !hit.SetAccessibleFocus(id, out var item, out var bounds)) return;
        if (!activate) RequestFocus();
        else ActivateFocused(item, new UiPointerEventArgs
        {
            Button = UiMouseButton.Left, X = bounds.CenterX, Y = bounds.CenterY + InputOffsetY
        });
        Invalidate();
    }
}

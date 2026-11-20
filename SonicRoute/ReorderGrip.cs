using System;
using System.Windows;
using MouseButtonState = System.Windows.Input.MouseButtonState;
using Point = System.Windows.Point;

namespace SonicRoute;

internal static class ReorderGrip
{
    private static FrameworkElement? _touchGrip;

    internal static void Attach(FrameworkElement grip, UIElement relativeTo,
        Action<Point> start, Action<Point> move, Action<Point, bool> finish)
    {
        bool mouseDown = false;
        grip.MouseLeftButtonDown += (_, e) =>
        {
            if (TouchHoldDrag.IsTouchMouse(e) || _touchGrip != null) return;
            if (!grip.CaptureMouse()) return;
            mouseDown = true;
            start(e.GetPosition(relativeTo));
            e.Handled = true;
        };
        grip.MouseMove += (_, e) =>
        {
            if (!mouseDown || TouchHoldDrag.IsTouchMouse(e)) return;
            if (e.LeftButton != MouseButtonState.Pressed) FinishMouse(default, false);
            else move(e.GetPosition(relativeTo));
            e.Handled = true;
        };
        grip.MouseLeftButtonUp += (_, e) =>
        {
            if (!mouseDown || TouchHoldDrag.IsTouchMouse(e)) return;
            FinishMouse(e.GetPosition(relativeTo), true);
            e.Handled = true;
        };
        grip.LostMouseCapture += (_, _) => FinishMouse(default, false);

        void FinishMouse(Point point, bool commit)
        {
            if (!mouseDown) return;
            mouseDown = false;
            try { finish(point, commit); }
            finally { if (grip.IsMouseCaptured) grip.ReleaseMouseCapture(); }
        }

        TouchHoldDrag.Attach(grip, _ => () =>
        {
            var contact = TouchHoldDrag.Current!;
            start(relativeTo.PointFromScreen(contact.StartScreen));
            _touchGrip = grip;
            bool commit = false;
            Point last = default;
            try
            {
                commit = contact.Track(screen => move(relativeTo.PointFromScreen(screen)), out var release);
                if (commit && PresentationSource.FromVisual(relativeTo) != null) last = relativeTo.PointFromScreen(release);
                else commit = false;
            }
            finally
            {
                _touchGrip = null;
                finish(last, commit);
            }
        });
    }

    internal static void Release(FrameworkElement? grip)
    {
        if (grip == null) return;
        if (grip == _touchGrip) TouchHoldDrag.Current?.Cancel();
        if (grip.IsMouseCaptured) grip.ReleaseMouseCapture();
    }
}

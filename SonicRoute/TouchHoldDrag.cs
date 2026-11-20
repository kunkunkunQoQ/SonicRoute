using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SonicRoute;

/// <summary>Lets short swipes pan normally. Only a held contact that starts moving is captured.</summary>
internal sealed class TouchHoldDrag
{
    private const int HoldMilliseconds = 500;
    private const double Slop = 8;
    private static TouchHoldDrag? _pending;
    internal static TouchHoldDrag? Current => _pending is { _started: true } active ? active : null;

    private readonly Window _window;
    private readonly FrameworkElement _surface;
    private readonly TouchDevice _device;
    private readonly Point _start;
    private readonly int _downTime;
    private readonly Action _begin;
    private readonly Func<bool>? _holdReleased;
    private Point _screen;
    private DispatcherFrame? _frame;
    private Action<Point>? _move;
    private bool _started, _ended, _released;
    internal Point StartScreen { get; }

    internal static bool IsTouchMouse(MouseEventArgs e) => e.StylusDevice?.TabletDevice.Type == TabletDeviceType.Touch;

    internal static void Attach(FrameworkElement element, Func<DependencyObject, Action?> choose, Func<DependencyObject, bool>? holdReleased = null)
    {
        // Windows normally translates a steady hold into a right click before the finger moves.
        // Draggable surfaces resolve that hold on lift so the menu cannot steal the contact mid-drag.
        Stylus.SetIsPressAndHoldEnabled(element, false);
        element.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>((_, e) =>
        {
            if (_pending != null || e.OriginalSource is not DependencyObject hit || Window.GetWindow(element) is not { } window
                || window.TouchesOver.Skip(1).Any())
                return;
            if (choose(hit) is { } begin) _pending = new TouchHoldDrag(window, element, e, begin,
                holdReleased == null ? null : () => holdReleased(hit));
        }), true);
    }

    private TouchHoldDrag(Window window, FrameworkElement surface, TouchEventArgs e, Action begin, Func<bool>? holdReleased)
    {
        _window = window;
        _surface = surface;
        _device = e.TouchDevice;
        _start = e.GetTouchPoint(window).Position;
        _screen = StartScreen = window.PointToScreen(_start);
        _downTime = e.Timestamp;
        _begin = begin;
        _holdReleased = holdReleased;
        window.AddHandler(UIElement.PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>(OnMove), true);
        window.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(OnUp), true);
        window.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OtherContact), true);
        window.AddHandler(UIElement.LostTouchCaptureEvent, new EventHandler<TouchEventArgs>(LostCapture), true);
        _device.Deactivated += DeviceEnded;
        window.PreviewKeyDown += KeyDown;
        window.Deactivated += WindowLost;
        window.IsVisibleChanged += VisibilityChanged;
        window.Closed += WindowLost;
    }

    private void OnMove(object? sender, TouchEventArgs e)
    {
        if (e.TouchDevice != _device || _ended) return;
        var point = e.GetTouchPoint(_window).Position;
        _screen = _window.PointToScreen(point);
        if (_started)
        {
            e.Handled = true;
            _move?.Invoke(_screen);
            return;
        }
        if (Math.Abs(point.X - _start.X) < Slop && Math.Abs(point.Y - _start.Y) < Slop) return;
        if (unchecked(e.Timestamp - _downTime) < HoldMilliseconds || !_surface.IsLoaded || !TakeCapture())
        {
            Cancel();
            return;
        }
        _started = true;
        e.Handled = true;
        // A button may already hold the promoted mouse press. Once dragging wins, releasing its
        // capture prevents the eventual finger lift from also clicking the original icon.
        if (Mouse.Captured is System.Windows.Media.Visual captured
            && (ReferenceEquals(captured, _surface) || _surface.IsAncestorOf(captured))) Mouse.Capture(null);
        try { _begin(); }
        finally { Cancel(); }
    }

    private bool TakeCapture()
    {
        if (_device.Captured == _window) return true;
        // A ScrollViewer removes its manipulators on completion. Let it finish releasing its
        // original capture before taking a new capture for the same contact.
        if (_device.Captured is UIElement owner && Manipulation.IsManipulationActive(owner))
            Manipulation.CompleteManipulation(owner);
        if (_device.Captured != null) _device.Capture(null);
        return !_ended && _device.Capture(_window) && _device.Captured == _window;
    }

    /// <summary>Tracks this contact across HWND boundaries until lift or cancellation, with the UI dispatcher running.</summary>
    internal bool Track(Action<Point> move, out Point releasedAt)
    {
        try
        {
            _move = move;
            move(_screen);
            if (!_ended)
            {
                _frame = new DispatcherFrame();
                Dispatcher.PushFrame(_frame);
            }
            releasedAt = _screen;
            return _released;
        }
        finally { _move = null; _frame = null; }
    }

    private void OnUp(object? sender, TouchEventArgs e)
    {
        if (e.TouchDevice != _device) return;
        if (_started)
        {
            _screen = _window.PointToScreen(e.GetTouchPoint(_window).Position);
            e.Handled = _released = true;
        }
        bool heldStill = !_started && unchecked(e.Timestamp - _downTime) >= HoldMilliseconds;
        Cancel();
        if (heldStill && _holdReleased?.Invoke() == true) e.Handled = true;
    }

    internal void Cancel()
    {
        if (_ended) return;
        _ended = true;
        _window.RemoveHandler(UIElement.PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>(OnMove));
        _window.RemoveHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(OnUp));
        _window.RemoveHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OtherContact));
        _window.RemoveHandler(UIElement.LostTouchCaptureEvent, new EventHandler<TouchEventArgs>(LostCapture));
        _device.Deactivated -= DeviceEnded;
        _window.PreviewKeyDown -= KeyDown;
        _window.Deactivated -= WindowLost;
        _window.IsVisibleChanged -= VisibilityChanged;
        _window.Closed -= WindowLost;
        if (ReferenceEquals(_pending, this)) _pending = null;
        if (_started && _device.Captured == _window) _device.Capture(null);
        if (_frame != null) _frame.Continue = false;
    }

    private void OtherContact(object? sender, TouchEventArgs e) { if (e.TouchDevice != _device) Cancel(); }
    private void LostCapture(object? sender, TouchEventArgs e)
    {
        if (e.TouchDevice == _device && _started && _device.Captured != _window) Cancel();
    }
    private void KeyDown(object sender, KeyEventArgs e) { if (_started && e.Key == Key.Escape) { e.Handled = true; Cancel(); } }
    private void WindowLost(object? sender, EventArgs e) => Cancel();
    private void DeviceEnded(object? sender, EventArgs e) => Cancel();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!_window.IsVisible) Cancel(); }
}

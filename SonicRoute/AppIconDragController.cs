using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Application = System.Windows.Application;
using Point = System.Windows.Point;
using DataObject = System.Windows.DataObject;
using Image = System.Windows.Controls.Image;
using Brushes = System.Windows.Media.Brushes;
using DragDropEffects = System.Windows.DragDropEffects;
using QueryContinueDragEventHandler = System.Windows.QueryContinueDragEventHandler;
using GiveFeedbackEventHandler = System.Windows.GiveFeedbackEventHandler;

namespace SonicRoute;

/// <summary>Shared app-icon gesture and cross-window preview. Runs only during an active drag.</summary>
internal static class AppIconDragController
{
    internal const string DesktopFormat = "SonicRoute.PcApp.v1";
    internal static bool IsDragging { get; private set; }
    internal static bool IsDesktopDragging { get; private set; }
    internal static event Action? DesktopDragStateChanged;
    internal static void SetDesktopDraggingForTest(bool dragging)
    {
        if (IsDesktopDragging == dragging) return;
        IsDesktopDragging = dragging;
        DesktopDragStateChanged?.Invoke();
    }
    internal static void Attach(FrameworkElement element, Action drag)
    {
        Point? origin = null;
        element.PreviewMouseLeftButtonDown += (_, e) => origin = e.GetPosition(element);
        element.PreviewMouseLeftButtonUp += (_, _) => origin = null;
        element.PreviewMouseMove += (_, e) =>
        {
            if (origin is not { } start || e.LeftButton != MouseButtonState.Pressed || IsDragging) return;
            var point = e.GetPosition(element);
            if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            origin = null; Mouse.Capture(null); e.Handled = true; drag();
        };
    }
    internal static void AttachDesktop(FrameworkElement element, AppItem app) => Attach(element, () =>
    {
        Run(element, new DataObject(DesktopFormat, app.Info), app.Icon, app.Label, dragging =>
        {
            if (IsDesktopDragging == dragging) return;
            IsDesktopDragging = dragging;
            DesktopDragStateChanged?.Invoke();
        });
        ((App)Application.Current).QuickPanelInstance?.CloseAfterFocusLeaves();
    });
    internal static DragResult Run(FrameworkElement source, System.Windows.IDataObject data, ImageSource? icon, string label, Action<bool>? changed = null)
    {
        bool released = false, cancelled = false; ScreenPoint drop = default;
        var preview = new IconPreview(icon, label);
        Window[] panels = Array.Empty<Window>();
        QueryContinueDragEventHandler continuing = (_, e) =>
        {
            if (e.EscapePressed || (e.KeyStates & DragDropKeyStates.RightMouseButton) != 0) cancelled = true;
            if ((e.KeyStates & DragDropKeyStates.LeftMouseButton) == 0) released = GetCursorPos(out drop);
        };
        GiveFeedbackEventHandler feedback = (_, e) =>
        {
            bool located = GetCursorPos(out var point);
            if (located) preview.Move(point.X, point.Y);
            bool outside = e.Effects == DragDropEffects.None && located && !InsidePanel(point, panels);
            e.UseDefaultCursors = !outside;
            if (outside) Mouse.SetCursor(System.Windows.Input.Cursors.Arrow);
            e.Handled = true;
        };
        source.AddHandler(System.Windows.DragDrop.QueryContinueDragEvent, continuing, true);
        source.AddHandler(System.Windows.DragDrop.GiveFeedbackEvent, feedback, true);
        IsDragging = true;
        DragDropEffects result;
        try
        {
            changed?.Invoke(true); Mouse.Capture(null);
            panels = CapturePanelWindows();
            preview.Show(); if (GetCursorPos(out var point)) preview.Move(point.X, point.Y);
            result = System.Windows.DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            preview.Close();
            source.RemoveHandler(System.Windows.DragDrop.QueryContinueDragEvent, continuing);
            source.RemoveHandler(System.Windows.DragDrop.GiveFeedbackEvent, feedback);
            Mouse.SetCursor(null); IsDragging = false; changed?.Invoke(false);
        }
        return new DragResult(result == DragDropEffects.None && released && !cancelled && !InsidePanel(drop), drop.X, drop.Y);
    }
    internal readonly struct DragResult
    {
        internal bool Outside { get; }
        internal int X { get; }
        internal int Y { get; }
        internal DragResult(bool outside, int x, int y) { Outside = outside; X = x; Y = y; }
    }
    private static bool InsidePanel(ScreenPoint point)
        => InsidePanel(point, CapturePanelWindows());
    private static Window[] CapturePanelWindows()
    {
        // Application.Windows clones its collection. Keep this snapshot only for this gesture;
        // visibility and window bounds are still checked live, and the final drop takes a fresh snapshot.
        var windows = Application.Current.Windows;
        var panels = new List<Window>(2);
        for (int i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            bool panel = window is QuickPanelModernWindow;
#if SONICROUTE_PHONE
            panel |= window is PhoneAudioWindow;
#endif
            if (panel) panels.Add(window);
        }
        return panels.ToArray();
    }
    private static bool InsidePanel(ScreenPoint point, Window[] panels)
    {
        for (int i = 0; i < panels.Length; i++)
        {
            var window = panels[i];
            if (!window.IsVisible) continue;
            var hwnd = (PresentationSource.FromVisual(window) as HwndSource)?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var area)
                && point.X >= area.Left && point.X < area.Right && point.Y >= area.Top && point.Y < area.Bottom) return true;
        }
        return false;
    }
    internal sealed class IconPreview : Window
    {
        private IntPtr _handle;
        private int _x = int.MinValue, _y = int.MinValue;
        internal IconPreview(ImageSource? icon, string label)
        {
            Left = Top = -10000;
            Width = Height = 36; AllowsTransparency = true; Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowActivated = false; ShowInTaskbar = false;
            Topmost = true; IsHitTestVisible = false; Focusable = false;
            var body = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(3), Opacity = .92 };
            body.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceBg");
            if (icon != null) body.Child = new Image { Source = icon, Stretch = Stretch.Uniform };
            else
            {
                var text = new TextBlock { Text = string.IsNullOrWhiteSpace(label) ? "♪" : label.Substring(0, 1),
                    FontSize = 22, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                text.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextPrimary"); body.Child = text;
            }
            Content = body;
            Closed += (_, _) => ((App)Application.Current).ReleaseClosedUiWindow(this);
            SourceInitialized += (_, _) =>
            {
                _handle = new WindowInteropHelper(this).Handle;
                var style = GetWindowLongPtr(_handle, -20).ToInt64();
                SetWindowLongPtr(_handle, -20, new IntPtr(style | 0x20 | 0x08000000 | 0x80));
            };
        }
        internal void Move(int x, int y)
        {
            if (_handle == IntPtr.Zero || (_x == x && _y == y)) return;
            _x = x; _y = y;
            SetWindowPos(_handle, new IntPtr(-1), x + 14, y + 16, 0, 0, 0x0011);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct ScreenRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out ScreenRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}

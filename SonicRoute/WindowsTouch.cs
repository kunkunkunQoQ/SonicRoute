using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace SonicRoute;

internal static class WindowsTouch
{
    private static bool _initialized;

    // Called on the UI dispatcher before creating controls.
    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(ScrollViewerLoaded));
        EventManager.RegisterClassHandler(typeof(ScrollViewer), ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(ScrollRangeChanged));
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(SliderTouchDown));
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.ManipulationStartingEvent,
            new EventHandler<ManipulationStartingEventArgs>(SliderManipulationStarting));
    }

    private static void ScrollViewerLoaded(object sender, RoutedEventArgs e) => SetPanning((ScrollViewer)sender);

    private static void ScrollRangeChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0
            || e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0))
            SetPanning((ScrollViewer)sender);
    }

    private static void SetPanning(ScrollViewer viewer)
    {
        if (viewer.TemplatedParent is TextBoxBase or PasswordBox) return;
        var source = DependencyPropertyHelper.GetValueSource(viewer, ScrollViewer.PanningModeProperty);
        if (source.BaseValueSource != BaseValueSource.Default) return;

        // A nested list that fits should leave a swipe to the containing page.
        bool vertical = viewer.ScrollableHeight > 0;
        bool horizontal = viewer.ScrollableWidth > 0;
        var direction = vertical ? horizontal ? PanningMode.Both : PanningMode.VerticalOnly
            : horizontal ? PanningMode.HorizontalOnly : PanningMode.None;
        if (viewer.PanningMode != direction)
            viewer.SetCurrentValue(ScrollViewer.PanningModeProperty, direction);
    }

    // Give a slider its usual thumb capture and snapping, even inside a panning list.
    private static void SliderTouchDown(object? sender, TouchEventArgs e)
    {
        if (sender is Slider slider) slider.SetCurrentValue(UIElement.IsManipulationEnabledProperty, true);
    }

    private static void SliderManipulationStarting(object? sender, ManipulationStartingEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        e.Cancel();
        e.Handled = true;
    }
}

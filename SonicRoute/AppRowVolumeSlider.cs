using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System;
using System.Windows.Input;

namespace SonicRoute
{
    /// <summary>名称与轨道共用原 Slider/Thumb；点击定位后由原生 Thumb 接续捕获和拖动。</summary>
    internal sealed class AppRowVolumeSlider : Slider
    {
        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);
            if (!e.Handled || !IsEnabled || !IsMoveToPointEnabled || e.ChangedButton != MouseButton.Left) return;
            if (Template?.FindName("PART_Track", this) is not Track track || track.Thumb == null || track.Thumb.IsDragging) return;
            UpdateLayout();
            track.Thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
            {
                RoutedEvent = MouseLeftButtonDownEvent
            });
        }
    }

    internal static class VolumeTrackLayout
    {
        internal static bool Update(Slider slider, Border? background, Border? fill)
        {
            if (background == null || fill == null || background.ActualWidth <= 0) return false;
            double range = slider.Maximum - slider.Minimum;
            double fraction = range <= 0 ? 0 : (slider.Value - slider.Minimum) / range;
            double width = Math.Max(0, background.ActualWidth * fraction);
            if (fill.Width != width) fill.Width = width;
            return true;
        }
    }
}

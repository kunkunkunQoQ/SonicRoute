using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
}

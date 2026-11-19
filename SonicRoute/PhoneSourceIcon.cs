#if SONICROUTE_PHONE
using System.Windows.Media;

namespace SonicRoute;

/// <summary>Shared frozen vector images for device audio sources.</summary>
internal static class PhoneSourceIcon
{
    internal static ImageSource Image { get; } = Create(AutomationIcons.Phone);
    internal static ImageSource AirPlayImage { get; } = Create(AutomationIcons.AirPlay);
    internal static ImageSource MonitorImage { get; } = Create(AutomationIcons.Monitor);
    internal static ImageSource WindowImage { get; } = Create(AutomationIcons.Application);
    internal static ImageSource SpeakerImage { get; } = Create(AutomationIcons.Speaker);

    private static ImageSource Create(Geometry geometry)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128));
        brush.Freeze();
        var pen = new System.Windows.Media.Pen(brush, 1.5);
        pen.Freeze();
        var drawing = new GeometryDrawing(null, pen, geometry);
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
#endif

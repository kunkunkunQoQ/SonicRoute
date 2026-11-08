#if SONICROUTE_PHONE
using System.Windows.Media;

namespace SonicRoute;

/// <summary>Shared frozen vector image for the whole-phone audio source.</summary>
internal static class PhoneSourceIcon
{
    internal static ImageSource Image { get; } = Create();

    private static ImageSource Create()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128));
        brush.Freeze();
        var pen = new System.Windows.Media.Pen(brush, 1.5);
        pen.Freeze();
        var drawing = new GeometryDrawing(null, pen, AutomationIcons.Phone);
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
#endif

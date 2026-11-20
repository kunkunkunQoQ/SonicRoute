#if SONICROUTE_PHONE
using System.Windows.Media;

namespace SonicRoute;

/// <summary>Shared vector images for device audio sources.</summary>
internal static class PhoneSourceIcon
{
    internal static ImageSource Image { get; } = Create(AutomationIcons.Phone);
    internal static ImageSource AirPlayImage { get; } = Create(AutomationIcons.AirPlay);
    internal static ImageSource MonitorImage { get; } = Create(AutomationIcons.Monitor);
    internal static ImageSource WindowImage { get; } = Create(AutomationIcons.Application);
    private static DrawingImage? _systemAudio;
    internal static ImageSource SystemAudioImage => _systemAudio ??= CreateSystemAudio();
    internal static ImageSource SpeakerImage { get; } = Create(AutomationIcons.Speaker);

    // All consumers use the same drawing, including a drag preview that is already visible.
    private static DrawingImage CreateSystemAudio()
    {
        var tile = new GeometryDrawing { Geometry = new RectangleGeometry(new System.Windows.Rect(0, 0, 32, 32), 7, 7) };
        var pen = new System.Windows.Media.Pen { Thickness = 1.9, LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var speaker = new DrawingGroup { Transform = new MatrixTransform(0.84, 0, 0, 0.84, 5, 6) };
        speaker.Children.Add(new GeometryDrawing(null, pen, AutomationIcons.Speaker));
        var drawing = new DrawingGroup();
        drawing.Children.Add(tile);
        drawing.Children.Add(speaker);
        void ApplyColors()
        {
            var resources = System.Windows.Application.Current.Resources;
            tile.Brush = (System.Windows.Media.Brush)resources[ThemeService.Dark ? "Theme.DeviceIconBackground" : "Theme.Accent"];
            pen.Brush = (System.Windows.Media.Brush)resources[ThemeService.Dark ? "Theme.DeviceIconForeground" : "Theme.AccentIconInk"];
        }
        ApplyColors();
        ThemeService.PaletteChanged += ApplyColors;
        return new DrawingImage(drawing);
    }

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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

namespace SonicRoute
{
    /// <summary>自动化页共用矢量图标，几何只解析一次并冻结，Lite/Legacy不依赖图标字体。</summary>
    public static class AutomationIcons
    {
        private static Geometry Frozen(string data)
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        }

        public static Geometry Search { get; } = Frozen("M10,3 A7,7 0 1 0 10,17 A7,7 0 1 0 10,3 M15,15 L21,21");
        public static Geometry Play { get; } = Frozen("M6,3 L21,12 L6,21 Z");
        public static Geometry Waiting { get; } = Frozen("M5,3 L19,3 M5,21 L19,21 M7,3 L7,7 L17,17 L17,21 M17,3 L17,7 L7,17 L7,21");
        public static Geometry Edit { get; } = Frozen("M4,16 L16,4 Q18,2 20,4 Q22,6 20,8 L8,20 L3,21 Z M14,6 L18,10");
        public static Geometry Copy { get; } = Frozen("M9,8 L19,8 Q21,8 21,10 L21,20 Q21,22 19,22 L9,22 Q7,22 7,20 L7,10 Q7,8 9,8 Z M16,4 L16,2 L3,2 L3,16 L4,16");
        public static Geometry Delete { get; } = Frozen("M4,6 L20,6 M9,6 L9,3 L15,3 L15,6 M6,6 L7,21 L17,21 L18,6 M10,10 L10,17 M14,10 L14,17");
        public static Geometry More { get; } = Frozen("M4,12 L4.1,12 M12,12 L12.1,12 M20,12 L20.1,12");
        public static Geometry Grip { get; } = Frozen("M7,4 L7.1,4 M16,4 L16.1,4 M7,10 L7.1,10 M16,10 L16.1,10 M7,16 L7.1,16 M16,16 L16.1,16 M7,22 L7.1,22 M16,22 L16.1,22");
        public static Geometry Options { get; } = Frozen("M3,6 L21,6 M3,12 L21,12 M3,18 L21,18 M8,3 L8,9 M16,9 L16,15 M10,15 L10,21");
        public static Geometry Clock { get; } = Frozen("M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M12,6 L12,12 L17,14");
        public static Geometry Keyboard { get; } = Frozen("M2,5 L22,5 L22,19 L2,19 Z M6,9 L6.2,9 M10,9 L10.2,9 M14,9 L14.2,9 M18,9 L18.2,9 M6,12 L6.2,12 M10,12 L10.2,12 M14,12 L14.2,12 M18,12 L18.2,12 M7,16 L17,16");
        public static Geometry Application { get; } = Frozen("M3,3 L21,3 L21,21 L3,21 Z M3,8 L21,8 M7,5.5 L7.2,5.5 M11,5.5 L11.2,5.5");
        public static Geometry Switch { get; } = Frozen("M3,3 L21,3 L21,21 L3,21 Z M6,15 L15,6 M10,6 L15,6 L15,11");
        public static Geometry Exit { get; } = Frozen("M10,3 L3,3 L3,21 L10,21 M9,12 L22,12 M17,7 L22,12 L17,17");
        public static Geometry Speaker { get; } = Frozen("M3,9 L7,9 L13,4 L13,20 L7,15 L3,15 Z M17,8 Q21,12 17,16 M20,5 Q26,12 20,19");
        public static Geometry Microphone { get; } = Frozen("M8,5 A4,4 0 0 1 16,5 L16,12 A4,4 0 0 1 8,12 Z M5,10 L5,12 A7,7 0 0 0 19,12 L19,10 M12,19 L12,23 M8,23 L16,23");
        public static Geometry Message { get; } = Frozen("M3,3 L21,3 L21,17 L10,17 L5,22 L5,17 L3,17 Z M7,8 L17,8 M7,12 L14,12");
        public static Geometry Link { get; } = Frozen("M10,6 L13,3 Q17,-1 21,3 Q25,7 21,11 L18,14 M14,18 L11,21 Q7,25 3,21 Q-1,17 3,13 L6,10 M8,16 L16,8");
        public static Geometry Terminal { get; } = Frozen("M2,4 L22,4 L22,20 L2,20 Z M6,8 L10,12 L6,16 M13,16 L18,16");

        internal static Viewbox Create(Geometry geometry, double size, string brush = "Theme.TextSecondary")
        {
            var path = new Path
            {
                Data = geometry, Width = 24, Height = 24, StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.None
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush);
            return new Viewbox { Width = size, Height = size, Child = path, IsHitTestVisible = false };
        }
    }
}

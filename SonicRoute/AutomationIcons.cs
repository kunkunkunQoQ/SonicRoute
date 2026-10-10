using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

namespace SonicRoute
{
    /// <summary>导航与自动化共用矢量图标，几何只解析一次并冻结，Lite/Legacy不依赖图标字体。</summary>
    public static class AutomationIcons
    {
        private static Geometry Frozen(string data)
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        }

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
        public static Geometry Home { get; } = Frozen("M3,10 L12,2 L21,10 L21,21 L15,21 L15,14 L9,14 L9,21 L3,21 Z");
        public static Geometry Apps { get; } = Frozen("M3,3 L9,3 L9,9 L3,9 Z M15,3 L21,3 L21,9 L15,9 Z M3,15 L9,15 L9,21 L3,21 Z M15,15 L21,15 L21,21 L15,21 Z");
        public static Geometry Theme { get; } = Frozen("M9,15 L17,3 Q19,0 22,3 L13,17 Z M9,15 Q4,14 4,19 Q4,22 1,22 Q10,24 11,18 Z");
        public static Geometry Settings { get; } = Frozen("M9,3 L15,3 L16,6 L19,7 L22,10 L20,13 L20,17 L16,19 L14,22 L10,22 L8,19 L4,17 L4,13 L2,10 L5,7 L8,6 Z M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8");
        public static Geometry Automation { get; } = Frozen("M13,1 L3,14 L10,14 L9,23 L21,9 L14,9 Z");
        public static Geometry Experiment { get; } = Frozen("M8,2 L16,2 M9,2 L9,9 L3,20 Q2,22 5,22 L19,22 Q22,22 21,20 L15,9 L15,2 M6,16 L18,16");
        public static Geometry Info { get; } = Frozen("M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M12,7 L12.1,7 M12,11 L12,17");
        public static Geometry Monitor { get; } = Frozen("M2,3 L22,3 L22,17 L2,17 Z M12,17 L12,22 M7,22 L17,22");
        public static Geometry File { get; } = Frozen("M5,2 L14,2 L20,8 L20,22 L5,22 Z M14,2 L14,8 L20,8");
        public static Geometry Sun { get; } = Frozen("M12,6 A6,6 0 1 0 12,18 A6,6 0 1 0 12,6 M12,0 L12,3 M12,21 L12,24 M0,12 L3,12 M21,12 L24,12 M3,3 L5,5 M19,19 L21,21 M3,21 L5,19 M19,5 L21,3");
        public static Geometry Moon { get; } = Frozen("M17,2 A10,10 0 1 0 22,17 A10,10 0 0 1 17,2 Z");
        public static Geometry Palette { get; } = Frozen("M12,2 A10,10 0 1 0 12,22 Q16,22 14,18 Q12,15 17,15 Q23,15 22,10 Q21,2 12,2 Z M7,7 L7.1,7 M12,5 L12.1,5 M17,8 L17.1,8 M6,12 L6.1,12");
        public static Geometry Transparency { get; } = Frozen("M2,2 L10,2 L10,10 L2,10 Z M14,2 L22,2 L22,10 L14,10 Z M2,14 L10,14 L10,22 L2,22 Z M14,14 L22,14 L22,22 L14,22 Z M7,12 L11,16 L18,8");
        public static Geometry ChevronDown { get; } = Frozen("M5,9 L12,16 L19,9");
        public static Geometry Headphones { get; } = Frozen("M3,13 L3,10 A9,9 0 0 1 21,10 L21,13 M3,12 L6,12 L6,21 L3,21 Z M18,12 L21,12 L21,21 L18,21 Z");
        public static Geometry Label { get; } = Frozen("M3,3 L12,3 L22,13 L13,22 L3,12 Z M8,7 L8.1,7");
        public static Geometry Power { get; } = Frozen("M12,1 L12,12 M6,5 A10,10 0 1 0 18,5");
        public static Geometry History { get; } = Frozen("M3,10 A9,9 0 1 1 5,19 M3,3 L3,10 L10,10 M12,6 L12,12 L16,14");
        public static Geometry Position { get; } = Frozen("M12,1 L12,5 M12,19 L12,23 M1,12 L5,12 M19,12 L23,12 M12,4 A8,8 0 1 0 12,20 A8,8 0 1 0 12,4 M12,9 A3,3 0 1 0 12,15 A3,3 0 1 0 12,9");
        public static Geometry Reset { get; } = Frozen("M3,10 A9,9 0 1 1 5,19 M3,3 L3,10 L10,10");

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

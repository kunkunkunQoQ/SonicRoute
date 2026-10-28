using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void Layout(FrameworkElement element, double width, double height)
    { element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout(); }
    private static void DrainUi() => Finish(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task);
    private static T? Descendant<T>(DependencyObject element) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        { var child = VisualTreeHelper.GetChild(element, i); if (child is T match) return match; var nested = Descendant<T>(child); if (nested != null) return nested; }
        return null;
    }
    private static ScrollViewer SampleViewer() => new()
    {
        Width = 270, Height = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = new Border { Width = 800, Height = 1800, Background = Brushes.Transparent }
    };

    private static void CheckAppScrollLayout(MethodInfo theme, List<Window> windows, string directory, bool includeLegacy = false)
    {
        Directory.CreateDirectory(directory);
        var config = ConfigService.Load();
        bool savedLayout = config.QuickPanelUseLegacyAppRows;
        bool savedCompactNames = config.QuickPanelCompactShowOutputName;
        foreach (int layout in includeLegacy ? new[] { 0, 1, 2 } : new[] { savedLayout ? (savedCompactNames ? 2 : 1) : 0 })
        foreach (string mode in new[] { "light", "dark" })
        {
            bool legacy = layout != 0;
            config.QuickPanelUseLegacyAppRows = legacy;
            config.QuickPanelCompactShowOutputName = layout == 2;
            theme.Invoke(null, new object[] { mode, "#00D5DD" });
            var panel = new QuickPanelModernWindow(); windows.Add(panel);
            var apps = Enumerable.Range(0, 3).Select(i => new AudioAppInfo
            {
                ProcessId = (uint)(int.MaxValue - 50 - i), ProcessName = "ScrollLayoutMissing" + i, DisplayName = "App " + i
            }).ToList();
            Finish((Task)panel.GetType().GetMethod("BuildAppRowsAsync", Instance)!.Invoke(panel, new object[] { apps, ConfigService.Load() })!);
            var surface = (FrameworkElement)panel.Content;
            var viewer = (ScrollViewer)panel.FindName("AppListScroll");
            var rows = Field<System.Collections.IDictionary>(panel, "_rows");
            var row = rows.Values.Cast<object>().First();
            var root = Field<FrameworkElement>(row, "RootElement");
            var slider = Field<Slider>(row, "Slider");
            var expand = Field<ToggleButton>(row, "ExpandButton");
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
            void Arrange() { Layout(surface, 372, 450); DrainUi(); surface.UpdateLayout(); }
            void Render(string state)
            {
                var bitmap = new RenderTargetBitmap(372, 450, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(directory, mode + (legacy ? (layout == 2 ? "-compact-names" : "-legacy") : "") + "-" + state + ".png")); encoder.Save(stream);
            }
            Arrange();
            Check(viewer.ComputedVerticalScrollBarVisibility == Visibility.Collapsed, mode + " fitting rows start without a vertical scrollbar");
            double viewport = viewer.ViewportWidth;
            var sliderBefore = Bounds(slider); var expandBefore = Bounds(expand);
            Render("collapsed");
            root.Height = 400; // Content growth reproduces an expanded application without executing device actions.
            Arrange();
            Check(viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible, mode + " expanded rows require a vertical scrollbar");
            var sliderAfter = Bounds(slider); var expandAfter = Bounds(expand);
            Console.WriteLine($"LAYOUT {mode} viewport={viewport}->{viewer.ViewportWidth} slider={sliderBefore}->{sliderAfter} expand={expandBefore}->{expandAfter}");
            Render("expanded");
            Check(Math.Abs(viewport - viewer.ViewportWidth) < .01 && Math.Abs(sliderBefore.X - sliderAfter.X) < .01
                && Math.Abs(sliderBefore.Width - sliderAfter.Width) < .01 && Math.Abs(expandBefore.X - expandAfter.X) < .01,
                mode + " scrollbar appearance preserves viewport, slider width and arrow position");
            var bar = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
            Check(ReferenceEquals(bar.Style, Application.Current.FindResource(typeof(ScrollBar)))
                && Bounds(bar).Left >= expandAfter.Right && Bounds(bar).Right <= surface.ActualWidth,
                mode + " native shared scrollbar occupies the right gutter without covering application controls");
            viewer.ScrollToEnd(); Arrange();
            Check(viewer.VerticalOffset > 0 && Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) < .01,
                mode + " existing native scrolling reaches the expanded content end");
            root.Height = double.NaN; viewer.ScrollToTop(); Arrange();
            Check(viewer.ComputedVerticalScrollBarVisibility == Visibility.Collapsed && Math.Abs(Bounds(expand).X - expandBefore.X) < .01
                && Math.Abs(viewer.ViewportWidth - viewport) < .01, mode + " collapsing content removes scrollbar without horizontal motion");
            Check(Field<System.Collections.IDictionary>(panel, "_pendingVolume").Count == 0
                && Field<System.Collections.IDictionary>(panel, "_pendingOutputVolume").Count == 0,
                mode + " layout and scrolling queue no audio writes");
            panel.Close();
        }
        config.QuickPanelUseLegacyAppRows = savedLayout;
        config.QuickPanelCompactShowOutputName = savedCompactNames;
    }
    private static void CheckScrollControls(string mode)
    {
        var app = Application.Current;
        Check(((Transform)app.FindResource("ScrollThumbWideVertical")).IsFrozen && ((Transform)app.FindResource("ScrollThumbWideHorizontal")).IsFrozen,
            mode + " hover and drag transforms are frozen shared resources");
        var viewer = SampleViewer(); Layout(viewer, 270, 170); DrainUi(); viewer.UpdateLayout();
        var vertical = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
        var horizontal = (ScrollBar)viewer.Template.FindName("PART_HorizontalScrollBar", viewer);
        foreach (var bar in new[] { vertical, horizontal })
        {
            bar.ApplyTemplate();
            var track = bar.Template.FindName("PART_Track", bar) as Track ?? throw new Exception("Missing native PART_Track"); track.Thumb.ApplyTemplate();
            var surface = (Border)track.Thumb.Template.FindName("ThumbSurface", track.Thumb);
            Check(ReferenceEquals(bar.Style, app.FindResource(typeof(ScrollBar))),
                mode + " native viewer picks shared scrollbar template: " + bar.Orientation);
            Check((bar.Orientation == Orientation.Vertical ? bar.ActualWidth : bar.ActualHeight) == 14
                && track.Thumb.ActualWidth > 0 && track.Thumb.ActualHeight > 0, mode + " fixed 14-DIP gutter and visible native thumb: " + bar.Orientation
                + $" bar={bar.ActualWidth}x{bar.ActualHeight} width={bar.Width} height={bar.Height} track={track.ActualWidth}x{track.ActualHeight} thumb={track.Thumb.ActualWidth}x{track.Thumb.ActualHeight} visible={track.Visibility}");
            Check(track.IsDirectionReversed == (bar.Orientation == Orientation.Vertical)
                && ReferenceEquals(track.IncreaseRepeatButton.Command, bar.Orientation == Orientation.Vertical ? ScrollBar.PageDownCommand : ScrollBar.PageRightCommand),
                mode + " correct native track direction and page command: " + bar.Orientation);
            Check((bar.Orientation == Orientation.Vertical ? track.Thumb.ActualHeight : track.Thumb.ActualWidth) >= 28,
                mode + " native viewport sizing respects minimum thumb length: " + bar.Orientation);
            var initialSize = bar.RenderSize;
            typeof(Thumb).GetProperty("IsDragging")!.GetSetMethod(true)!.Invoke(track.Thumb, new object[] { true });
            DrainUi();
            var scale = (ScaleTransform)surface.RenderTransform;
            Check(ReferenceEquals(surface.Background, app.FindResource("Theme.Accent"))
                && Math.Abs((bar.Orientation == Orientation.Vertical ? scale.ScaleX : scale.ScaleY) - 4.0 / 3) < 0.001
                && bar.RenderSize == initialSize, mode + " drag color and widening preserve layout: " + bar.Orientation);
            track.Thumb.RaiseEvent(new DragDeltaEventArgs(bar.Orientation == Orientation.Horizontal ? 24 : 0,
                bar.Orientation == Orientation.Vertical ? 24 : 0) { RoutedEvent = Thumb.DragDeltaEvent });
            typeof(Thumb).GetProperty("IsDragging")!.GetSetMethod(true)!.Invoke(track.Thumb, new object[] { false });
            DrainUi(); viewer.UpdateLayout();
            Check((bar.Orientation == Orientation.Vertical ? viewer.VerticalOffset : viewer.HorizontalOffset) > 0,
                mode + " routed native thumb delta scrolls content: " + bar.Orientation);
            // Exercise the contrast branch without changing the user's Windows settings.
            surface.Tag = true; DrainUi();
            Check(ReferenceEquals(surface.Background, SystemColors.ControlTextBrush) && surface.Opacity == 1,
                mode + " contrast branch uses system brush: " + bar.Orientation);
            surface.ClearValue(FrameworkElement.TagProperty);
        }
        viewer.ScrollToHome(); DrainUi(); viewer.UpdateLayout();
        double before = viewer.VerticalOffset;
        ScrollBar.PageDownCommand.Execute(null, vertical); DrainUi(); viewer.UpdateLayout();
        Check(viewer.VerticalOffset > before, mode + " native vertical page command scrolls viewer");
        viewer.ScrollToEnd(); DrainUi(); viewer.UpdateLayout();
        Check(viewer.VerticalOffset == viewer.ScrollableHeight, mode + " scroll-to-end reaches native range boundary");
        viewer.ScrollToHome(); DrainUi(); viewer.UpdateLayout();
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent };
        viewer.RaiseEvent(wheel); DrainUi(); viewer.UpdateLayout();
        Check(viewer.VerticalOffset > 0, mode + " routed wheel event retains native scrolling");
        viewer.Content = new Border { Width = 40, Height = 40 }; Layout(viewer, 270, 170); DrainUi(); viewer.UpdateLayout();
        Check(viewer.ComputedVerticalScrollBarVisibility == Visibility.Collapsed && viewer.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed,
            mode + " native auto visibility hides bars when content fits");
        var text = new TextBox { Width = 240, Height = 110, Text = string.Join("\n", Enumerable.Repeat("History line", 60)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Style = (Style)app.FindResource("ModernTextBox") };
        Layout(text, 240, 110); DrainUi(); text.UpdateLayout();
        var textBar = Descendant<ScrollBar>(text);
        Check(textBar != null && ReferenceEquals(textBar.Style, app.FindResource(typeof(ScrollBar))), mode + " textbox embedded scrollbar uses same style");
        var combo = new ComboBox { Width = 220, MaxDropDownHeight = 130, Style = (Style)app.FindResource("RoundedComboBox") };
        for (int i = 0; i < 30; i++) combo.Items.Add("Choice " + i);
        Layout(combo, 220, 34);
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        Layout((FrameworkElement)popup.Child, 220, 130); DrainUi();
        var popupBar = Descendant<ScrollBar>(popup.Child);
        Check(!popup.IsOpen && popupBar != null && ReferenceEquals(popupBar.Style, app.FindResource(typeof(ScrollBar))),
            mode + " closed dropdown template inherits shared scrollbar without opening native popup");
        var menu = new ContextMenu { Style = (Style)app.FindResource("ConvenienceMenu"), MaxHeight = 160 };
        for (int i = 0; i < 25; i++) menu.Items.Add(new MenuItem { Header = "Menu " + i, Style = (Style)app.FindResource("ConvenienceMenuItem") });
        Layout(menu, 240, 160); DrainUi();
        var menuBar = Descendant<ScrollBar>(menu);
        Check(!menu.IsOpen && menuBar != null && ReferenceEquals(menuBar.Style, app.FindResource(typeof(ScrollBar))),
            mode + " unopened menu template inherits shared scrollbar");
        menu.Items.Clear(); combo.Items.Clear();
        viewer.Content = null; text.Clear();
    }

    private static void RenderScrollbars(MethodInfo theme, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (string mode in new[] { "light", "dark" })
        {
            theme.Invoke(null, new object[] { mode, "#00D5DD" });
            var app = Application.Current;
            var root = new StackPanel { Width = 680, Background = (Brush)app.FindResource("Theme.WindowBg"), Margin = new Thickness(0) };
            root.Children.Add(new TextBlock { Text = "SonicRoute · " + mode, FontSize = 23, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)app.FindResource("Theme.TextPrimary"), Margin = new Thickness(24, 20, 20, 16) });
            var window = new MainWindow(); Call(window, "EnsureSettingsPage");
            var config = ConfigService.Load();
            var card = new WrapPanel { Margin = new Thickness(24, 0, 24, 18) };
            for (int i = 0; i < 3; i++)
            {
                var device = new AudioDeviceInfo { Id = "render-device-" + i, DisplayName = "SteelSeries Sonar - " + i + " (SteelSeries Sonar Virtual Audio Device)", Flow = EDataFlow.eRender };
                if (i < 2) config.DeviceNames[device.Id] = i == 0 ? "聊天" : "游戏 (自定义)";
                var check = (CheckBox)typeof(MainWindow).GetMethod("MakeFilterCheckBox", Instance)!.Invoke(window,
                    new object[] { device, new List<string>(), new RoutedEventHandler((_, _) => { }) })!;
                card.Children.Add(check);
            }
            root.Children.Add(card);
            var viewer = new ScrollViewer { Height = 290, Margin = new Thickness(24, 0, 24, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
            var content = new StackPanel();
            for (int i = 0; i < 16; i++) content.Children.Add(new Border { Height = 48, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(8),
                Background = (Brush)app.FindResource("Theme.SurfaceBg"), Child = new TextBlock { Text = "设备选项 / Device " + (i + 1),
                    Foreground = (Brush)app.FindResource("Theme.TextPrimary"), Margin = new Thickness(16, 12, 0, 0) } });
            viewer.Content = content; root.Children.Add(viewer);
            var horizontal = new ScrollViewer { Height = 65, Margin = new Thickness(24, 12, 24, 0), HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = new Border { Width = 1500, Height = 28,
                    Background = (Brush)app.FindResource("Theme.SurfaceAlt"), Child = new TextBlock { Text = "横向滚动 / Horizontal scrolling", Foreground = (Brush)app.FindResource("Theme.TextPrimary") } } };
            root.Children.Add(horizontal);
            Layout(root, 680, 560); DrainUi(); root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(680, 560, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(directory, mode + ".png"))) encoder.Save(output);
            viewer.Content = horizontal.Content = null; window.Close();
        }
        Console.WriteLine("RENDER actual WPF templates, offscreen 96 DPI, no windows shown");
    }
}

using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SonicRoute;

internal static partial class Program
{
    // Uses WPF's public touch provider contract. All touches stay inside the fixture process.
    // Screen positions allow a captured contact to travel between two independent test HWNDs.
    private sealed class FixtureContact(FrameworkElement origin, Point down) : TouchDevice(17), IDisposable
    {
        private readonly PresentationSource _source = PresentationSource.FromVisual(origin)!;
        private Point _screen = origin.PointToScreen(down);
        private TouchAction _action = TouchAction.Down;

        public bool Press()
        {
            SetActiveSource(_source);
            Activate();
            return ReportDown();
        }

        public void MoveTo(FrameworkElement element, Point point)
        {
            _action = TouchAction.Move;
            _screen = element.PointToScreen(point);
            ReportMove();
        }

        public bool Lift()
        {
            _action = TouchAction.Up;
            try { return ReportUp(); }
            finally { Deactivate(); }
        }

        public void Dispose() { if (IsActive) Lift(); }

        public override TouchPoint GetTouchPoint(IInputElement? relativeTo)
        {
            var relative = relativeTo as Visual ?? _source.RootVisual;
            var point = relative.PointFromScreen(_screen);
            return new TouchPoint(this, point, new Rect(new Point(point.X - 2, point.Y - 2), new Size(4, 4)), _action);
        }

        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement? relativeTo)
            => [GetTouchPoint(relativeTo)];
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int metric);
    // Schedule the rest of the gesture before the first drag move enters its nested dispatcher.
    private static void DragHeldContact(FrameworkElement source, Point down, Action<FixtureContact> whileDragging)
    {
        using var finger = new FixtureContact(source, down);
        finger.Press();
        Pump(Task.Delay(700));
        var finish = Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            try { whileDragging(finger); }
            finally { finger.Lift(); }
        }, DispatcherPriority.Background);
        finger.MoveTo(source, new Point(down.X + 18, down.Y));
        Pump(finish.Task.WaitAsync(TimeSpan.FromSeconds(4)));
    }

    private static void CheckTouchPeerRow(PhoneAudioWindow quick, QuickPanelModernWindow mixer, Type hub)
    {
        var list = (ListBox)quick.FindName("PhoneApps");
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
        var center = new Point(row.ActualWidth / 2, row.ActualHeight / 2);
        using (var hold = new FixtureContact(row, center))
        {
            hold.Press();
            Pump(Task.Delay(700));
            hold.Lift();
            DrainUi();
            Check(row.ContextMenu?.IsOpen == true, "holding a peer app row without moving opens its playback menu");
            row.ContextMenu!.IsOpen = false;
            DrainUi();
        }

        var shared = (Border)mixer.FindName("AndroidSharedDropZone");
        var dragSource = hub.GetProperty("DragSource", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool payloadPublished = false, hoverReached = false;
        DragHeldContact(row, center, finger =>
        {
            mixer.UpdateLayout();
            payloadPublished = dragSource.GetValue(null) != null && shared.IsVisible;
            finger.MoveTo(shared, new Point(shared.ActualWidth / 2, shared.ActualHeight / 2));
            hoverReached = ReferenceEquals(shared.Background, mixer.FindResource("Theme.Overlay"));
        });
        Check(payloadPublished && hoverReached && dragSource.GetValue(null) == null && row.ContextMenu?.IsOpen != true,
            "a held peer app drags across independent panel windows, highlights shared playback, and releases cleanly");
    }

    private static int WindowsTouchChecks()
    {
        Console.WriteLine($"Windows digitizer flags {GetSystemMetrics(94)}; maximum contacts {GetSystemMetrics(95)}. Source: in-process WPF touch reports.");
        var layout = new DockPanel();
        var choices = new ListBox { Height = 80 };
        ScrollViewer.SetHorizontalScrollBarVisibility(choices, ScrollBarVisibility.Disabled);
        choices.Items.Add("First");
        choices.Items.Add("Second");
        DockPanel.SetDock(choices, Dock.Top);
        layout.Children.Add(choices);
        var rows = new StackPanel();
        var grip = new Border { Height = 50, Background = Brushes.LightGray };
        rows.Children.Add(grip);
        var dragRow = new Border { Height = 70, Background = Brushes.CadetBlue };
        rows.Children.Add(dragRow);
        rows.Children.Add(new Border { Height = 720, Background = Brushes.LightSteelBlue });
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        layout.Children.Add(scroll);
        var window = new Window { Content = layout, Width = 260, Height = 390, Top = 35, Left = 20,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        var receiver = new Window { Content = new Border { Background = Brushes.White }, Width = 220,
            Height = 320, Top = 35, Left = 300, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false, ShowActivated = false };

        try
        {
            window.Show();
            receiver.Show();
            window.Activate();
            DrainUi();
            Check(scroll.PanningMode == PanningMode.VerticalOnly && scroll.IsManipulationEnabled && scroll.ScrollableHeight > 0,
                "the application activates native one-finger vertical panning on a loaded scrolling region");

            var choice = (ListBoxItem)choices.ItemContainerGenerator.ContainerFromIndex(0);
            var classicTap = new Point(choice.ActualWidth / 2, choice.ActualHeight / 2);
            int taps = 0;
            choice.PreviewTouchDown += (_, _) => taps++;
            using (var tap = new FixtureContact(choice, classicTap))
            {
                tap.Press();
                tap.Lift();
                Check(taps == 1, "a WPF touch contact reaches the intended selection row once");
            }
            // The custom TouchDevice reports only touch; emulate the primary contact's promoted
            // click through this HWND's WPF input provider to check normal control activation.
            byte[] savedKeys = new byte[256];
            if (!GetKeyboardState(savedKeys)) throw new InvalidOperationException("Unable to read the fixture thread's input state.");
            try
            {
                var keys = (byte[])savedKeys.Clone();
                ReportWpfMouse(choice, classicTap, "Activate, AbsoluteMove");
                ReportWpfMouse(choice, classicTap, "AbsoluteMove");
                keys[1] = 0x80;
                SetKeyboardState(keys);
                ReportWpfMouse(choice, classicTap, "Button1Press");
                keys[1] = 0;
                SetKeyboardState(keys);
                ReportWpfMouse(choice, classicTap, "Button1Release");
            }
            finally { SetKeyboardState(savedKeys); }
            Check(choices.SelectedIndex == 0, "the primary click delivered to the tapped row selects it");

            var appIcons = typeof(App).Assembly.GetType("SonicRoute.AppIconDragController", true)!;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            int started = 0;
            object? drop = null;
            Action launch = () =>
            {
                started++;
                drop = appIcons.GetMethod("Run", statics)!.Invoke(null,
                    [dragRow, new DataObject("SonicRoute.Fixture", "test"), null, "Fixture", null]);
            };
            appIcons.GetMethod("Attach", statics)!.Invoke(null, [dragRow, launch, null]);
            var origin = new Point(30, 35);
            using (var fast = new FixtureContact(dragRow, origin))
            {
                fast.Press();
                Point at = dragRow.TranslatePoint(origin, window);
                for (int i = 1; i <= 5; i++)
                {
                    fast.MoveTo(window, new Point(at.X, at.Y - i * 15));
                    Pump(Task.Delay(35));
                }
                fast.Lift();
            }
            Check(started == 0 && scroll.VerticalOffset > 20, "a quick swipe on a draggable row scrolls the viewport without starting a drag");
            Pump(Task.Delay(900));
            scroll.ScrollToTop();
            DrainUi();

            Point releaseAt = new(60, 75);
            Point expectedScreen = receiver.PointToScreen(releaseAt);
            DragHeldContact(dragRow, origin, finger => finger.MoveTo(receiver, releaseAt));
            Check(started == 1 && drop != null && (bool)drop.GetType().GetProperty("Outside", Hidden)!.GetValue(drop)!,
                "holding then moving starts one drag and a release beyond the mixer is recognized");
            int x = (int)drop!.GetType().GetProperty("X", Hidden)!.GetValue(drop)!;
            int y = (int)drop.GetType().GetProperty("Y", Hidden)!.GetValue(drop)!;
            Check(Math.Abs(x - expectedScreen.X) <= 1 && Math.Abs(y - expectedScreen.Y) <= 1,
                "drag-out placement uses the final finger coordinates in the second window");

            CheckTouchReorderGrip(grip, scroll, window);
            return 0;
        }
        finally { receiver.Close(); window.Close(); }
    }

    private static void CheckTouchReorderGrip(Border grip, ScrollViewer scroll, Window host)
    {
        int starts = 0, moves = 0, commits = 0;
        var type = typeof(App).Assembly.GetType("SonicRoute.ReorderGrip", true)!;
        Action<Point> start = _ => starts++;
        Action<Point> move = _ => moves++;
        Action<Point, bool> end = (_, commit) => { if (commit) commits++; };
        type.GetMethod("Attach", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [grip, host, start, move, end]);
        scroll.ScrollToTop();
        DrainUi();
        DragHeldContact(grip, new Point(18, 22), finger => finger.MoveTo(host, new Point(32, 188)));
        Check(starts == 1 && moves > 0 && commits == 1, "a held reorder handle starts, tracks and commits one sort");
    }
}

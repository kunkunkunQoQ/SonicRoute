using System.Reflection;
using System.Runtime.InteropServices;
using SonicRoute;

internal static partial class Program
{
    // A temporary icon owned by this test; never inspect or alter the running app's icon.
    private static void CheckNativeTrayHit()
    {
        var type = typeof(App).Assembly.GetType("SonicRoute.TrayWheelService", true)!;
        // Use the product's full assembly identity, including Framework's GAC binding.
        var notifyType = type.GetConstructors(Instance).Single().GetParameters()[0].ParameterType;
        using var notify = (IDisposable)Activator.CreateInstance(notifyType)!;
        var iconType = notifyType.GetProperty("Icon")!.PropertyType;
        var systemIcons = iconType.Assembly.GetType("System.Drawing.SystemIcons", true)!;
        notifyType.GetProperty("Icon")!.SetValue(notify, systemIcons.GetProperty("Application")!.GetValue(null));
        notifyType.GetProperty("Text")!.SetValue(notify, "SonicRoute tray hit check");
        using var wheel = (IDisposable)Activator.CreateInstance(type, Instance, null, new object[] { notify }, null)!;
        var identity = type.GetMethod("TryGetTrayIconIdentity", Instance)!;
        var hit = type.GetMethod("IsOverOurIcon", Instance)!;
        var pointType = type.GetNestedType("POINT", BindingFlags.NonPublic)!;
        var identifierType = type.GetNestedType("NOTIFYICONIDENTIFIER", BindingFlags.NonPublic)!;
        object Point(int x, int y)
        {
            object point = Activator.CreateInstance(pointType)!;
            pointType.GetField("x")!.SetValue(point, x); pointType.GetField("y")!.SetValue(point, y);
            return point;
        }
        bool Hit(int x, int y) => (bool)hit.Invoke(wheel, new[] { Point(x, y) })!;
        int Rect(object rect, string edge) => (int)rect.GetType().GetField(edge)!.GetValue(rect)!;
        Check(type.GetMethod("Shell_NotifyIconGetRect", BindingFlags.NonPublic | BindingFlags.Static)!.ReturnType == typeof(int),
            "actual product imports Shell_NotifyIconGetRect as HRESULT");
        Check(type.GetMethod("IsOverOurIconByToolbar", BindingFlags.NonPublic | BindingFlags.Static) == null,
            "actual product has no synchronous Explorer toolbar fallback");
        notifyType.GetProperty("Visible")!.SetValue(notify, true);
        object[] arguments = { IntPtr.Zero, 0 };
        Check((bool)identity.Invoke(wheel, arguments)! && (IntPtr)arguments[0] != IntPtr.Zero,
            "actual WinForms private fields and NativeWindow.Handle resolve the live icon identity");
        object identifier = Activator.CreateInstance(identifierType)!;
        identifierType.GetField("cbSize")!.SetValue(identifier, Marshal.SizeOf(identifierType));
        identifierType.GetField("hWnd")!.SetValue(identifier, arguments[0]);
        identifierType.GetField("uID")!.SetValue(identifier, arguments[1]);
        object?[] query = { identifier, null };
        var getRect = type.GetMethod("Shell_NotifyIconGetRect", BindingFlags.NonPublic | BindingFlags.Static)!;
        int hr = -1;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            hr = (int)getRect.Invoke(null, query)!;
            if (hr >= 0 && Rect(query[1]!, "Right") > Rect(query[1]!, "Left")) break;
            Finish(Task.Delay(100));
        }
        Check(hr == 0 && Rect(query[1]!, "Right") > Rect(query[1]!, "Left"),
            "real Shell API reports S_OK and a visible or overflow icon rectangle");
        int x = (Rect(query[1]!, "Left") + Rect(query[1]!, "Right")) / 2;
        int y = (Rect(query[1]!, "Top") + Rect(query[1]!, "Bottom")) / 2;
        Check(Hit(x, y) && !Hit(Rect(query[1]!, "Right") + 5, y),
            "actual product hit test accepts the real icon and rejects a point beyond its margin");
        notifyType.GetProperty("Visible")!.SetValue(notify, false);
        hr = (int)getRect.Invoke(null, query)!;
        Check(hr < 0 && Hit(x, y), "real icon removal exercises last-good recovery on a failing Shell query");
        type.GetField("_cachedIconRectAt", Instance)!.SetValue(wheel,
            System.Diagnostics.Stopwatch.GetTimestamp() - 30L * System.Diagnostics.Stopwatch.Frequency);
        Check(!Hit(x, y), "actual expired cache rejects the removed icon");
        notifyType.GetProperty("Visible")!.SetValue(notify, true);
        Check(Hit(x, y), "real icon restoration recovers without restarting the service");
    }
}

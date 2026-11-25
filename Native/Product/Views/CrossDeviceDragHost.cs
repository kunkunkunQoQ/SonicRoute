#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using SonicRoute.Core.Models;

namespace SonicRoute.Native.Product.Views;

/// <summary>
/// Screen-space cross-device drag coordinator between QuickPanelWindow (PC apps) and PhoneAudioWindowView drop zones.
/// Pure managed state with zero idle timers, reflection, or unsafe code.
/// </summary>
internal static class CrossDeviceDragHost
{
    private sealed record TargetEntry(Func<int, int, string?> HitTest, Action<AudioAppInfo, string> OnDrop);

    private sealed class Registration(Func<int, int, string?> hitTest) : IDisposable
    {
        public void Dispose() => UnregisterTarget(hitTest);
    }

    private static readonly object Gate = new();
    private static readonly List<TargetEntry> Targets = new();

    internal static bool IsDragging { get; private set; }
    internal static AudioAppInfo? DraggedApp { get; private set; }
    internal static string? ActiveZone { get; private set; }
    internal static int ScreenX { get; private set; }
    internal static int ScreenY { get; private set; }

    internal static event Action? StateChanged;

    internal static IDisposable RegisterTarget(Func<int, int, string?> hitTestDropZone, Action<AudioAppInfo, string> onDrop)
    {
        ArgumentNullException.ThrowIfNull(hitTestDropZone);
        ArgumentNullException.ThrowIfNull(onDrop);
        lock (Gate)
        {
            Targets.RemoveAll(t => ReferenceEquals(t.HitTest, hitTestDropZone));
            Targets.Add(new TargetEntry(hitTestDropZone, onDrop));
        }
        return new Registration(hitTestDropZone);
    }

    internal static void UnregisterTarget(Func<int, int, string?> hitTestDropZone)
    {
        lock (Gate)
        {
            Targets.RemoveAll(t => ReferenceEquals(t.HitTest, hitTestDropZone));
        }
    }

    internal static void BeginDrag(AudioAppInfo app, float screenX, float screenY)
    {
        ArgumentNullException.ThrowIfNull(app);
        int sx = (int)MathF.Round(screenX);
        int sy = (int)MathF.Round(screenY);
        string? zone;
        lock (Gate)
        {
            IsDragging = true;
            DraggedApp = app;
            ScreenX = sx;
            ScreenY = sy;
            zone = HitTestLocked(sx, sy);
            ActiveZone = zone;
        }
        StateChanged?.Invoke();
    }

    internal static string? UpdateDrag(int screenX, int screenY)
    {
        bool changed = false;
        string? zone;
        lock (Gate)
        {
            if (!IsDragging) return null;
            ScreenX = screenX;
            ScreenY = screenY;
            zone = HitTestLocked(screenX, screenY);
            if (!string.Equals(ActiveZone, zone, StringComparison.Ordinal))
            {
                ActiveZone = zone;
                changed = true;
            }
        }
        if (changed) StateChanged?.Invoke();
        return zone;
    }

    internal static bool EndDrag(int screenX, int screenY)
    {
        AudioAppInfo? app;
        TargetEntry? matchedTarget = null;
        string? matchedZone = null;

        lock (Gate)
        {
            if (!IsDragging || DraggedApp == null)
            {
                ClearLocked();
                return false;
            }

            app = DraggedApp;
            for (int i = Targets.Count - 1; i >= 0; i--)
            {
                string? zone = Targets[i].HitTest(screenX, screenY);
                if (!string.IsNullOrEmpty(zone))
                {
                    matchedTarget = Targets[i];
                    matchedZone = zone;
                    break;
                }
            }
            ClearLocked();
        }

        StateChanged?.Invoke();
        if (matchedTarget != null && matchedZone != null)
        {
            matchedTarget.OnDrop(app, matchedZone);
            return true;
        }
        return false;
    }

    internal static void CancelDrag()
    {
        bool wasDragging;
        lock (Gate)
        {
            wasDragging = IsDragging;
            if (!wasDragging) return;
            ClearLocked();
        }
        StateChanged?.Invoke();
    }

    private static string? HitTestLocked(int screenX, int screenY)
    {
        for (int i = Targets.Count - 1; i >= 0; i--)
        {
            string? zone = Targets[i].HitTest(screenX, screenY);
            if (!string.IsNullOrEmpty(zone)) return zone;
        }
        return null;
    }

    private static void ClearLocked()
    {
        IsDragging = false;
        DraggedApp = null;
        ActiveZone = null;
    }
}
#endif

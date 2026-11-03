#if SONICROUTE_PHONE
using System;
using System.Windows;
using Application = System.Windows.Application;
using DataObject = System.Windows.DataObject;

namespace SonicRoute;

internal static class AndroidAppDragController
{
    internal static void Attach(FrameworkElement element, Func<AndroidAppDrag> payload)
        => AppIconDragController.Attach(element, () => Run(element, payload()));
    internal static void Run(FrameworkElement source, AndroidAppDrag payload)
    {
        var result = AppIconDragController.Run(source, new DataObject(AndroidAppDrag.Format, payload),
            new PhoneAppItem(payload.App).Icon, payload.App.Name, active => AndroidAudioHost.IsDragging = active);
        var app = (App)Application.Current;
        app.QuickPanelInstance?.ClearAndroidDropHighlight();
        if (result.Outside) _ = app.OpenAndroidDisplayAsync(payload, result.X, result.Y);
        app.QuickPanelInstance?.CloseAfterFocusLeaves();
    }
}
#endif

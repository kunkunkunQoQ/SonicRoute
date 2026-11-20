#if SONICROUTE_PHONE
using System;
using System.Windows;
using System.Windows.Media;
using Application = System.Windows.Application;
using DataObject = System.Windows.DataObject;

namespace SonicRoute;

internal static class WindowsLinkAppDragController
{
    internal static void Attach(FrameworkElement element, Func<WindowsLinkAppDrag> payload, Func<ImageSource?>? icon = null)
        => AppIconDragController.Attach(element, () => Run(element, payload(), icon?.Invoke()));

    internal static void Run(FrameworkElement source, WindowsLinkAppDrag payload, ImageSource? icon = null)
    {
        ImageSource? previewIcon = icon ?? (payload.App.IsSystemAudio
            ? PhoneSourceIcon.SystemAudioImage
            : AppIconService.DecodePng(payload.App.IconPng));
        var result = AppIconDragController.Run(
            source,
            new DataObject(WindowsLinkAppDrag.Format, payload),
            previewIcon,
            payload.App.Name,
            active => WindowsLinkHub.DragSource = active ? payload : null);
        var app = (App)Application.Current;
        app.QuickPanelInstance?.ClearAndroidDropHighlight();
        if (result.Outside && payload.Window) _ = app.OpenWindowsLinkDisplayAsync(payload, result.X, result.Y);
        app.QuickPanelInstance?.CloseAfterFocusLeaves();
    }
}
#endif

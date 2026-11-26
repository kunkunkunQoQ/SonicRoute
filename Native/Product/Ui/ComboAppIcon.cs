using SonicRoute.Core;
using SonicRoute.Core.Models;

namespace SonicRoute.Native.Ui;

internal readonly record struct ComboAppIcon(UiIcon Fallback, AppConfig? Config = null, int ProcessId = 0,
    string? IconPath = null, string? ProcessName = null, byte[]? Png = null)
{
    internal static ComboAppIcon ForApp(AudioAppInfo app, AppConfig config)
    {
        UiIcon fallback = app.ProcessName == "android:sonicroute:device-audio" ? UiIcon.Phone : UiIcon.Application;
#if SONICROUTE_PHONE
        if (app.ProcessName == "airplay:receiver") fallback = UiIcon.AirPlay;
#endif
        return new(fallback, config, (int)app.ProcessId, app.IconPath, app.ProcessName, app.ExternalIconPng);
    }

    internal static ComboAppIcon ForExecutable(string path) => new(UiIcon.Application, IconPath: path);

    internal void Draw(D2dTarget g, UiRect rect, UiColor ink, Action? onLoaded, float opacity = 1f)
    {
        var bitmap = Png is { Length: > 0 } png
            ? NativeAppIconService.GetPngIcon(png, onLoaded)
            : NativeAppIconService.GetAppIcon(Config, ProcessId, IconPath, ProcessName, onLoaded);
        if (bitmap is { } icon)
            g.DrawBgraBitmap(icon.BgraPixels, icon.BgraPixels, icon.Width, icon.Height, rect, opacity);
        else
            g.DrawIcon(Fallback, rect, ink);
    }
}

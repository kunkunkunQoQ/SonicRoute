using System;
using System.Globalization;
using System.Linq;
using SonicRoute.Core;

namespace SonicRoute;

internal enum StartupView { None, Main, QuickPanel }

internal static class HostStartup
{
    internal static StartupView InitialView(AppConfig settings, string[] args, bool phoneLab = false)
    {
        if (phoneLab || args.Contains("--panel", StringComparer.OrdinalIgnoreCase)) return StartupView.QuickPanel;
        if (args.Contains("--main", StringComparer.OrdinalIgnoreCase)) return StartupView.Main;
        if (settings.StartPanelOnStart) return StartupView.QuickPanel;
        return settings.StartMinimized ? StartupView.None : StartupView.Main;
    }

    internal static string DetectSystemLanguage()
    {
        try
        {
            var culture = CultureInfo.InstalledUICulture;
            var name = culture.Name.ToLowerInvariant();
            if (name.StartsWith("zh-tw") || name.StartsWith("zh-hk") || name.StartsWith("zh-mo") || name.StartsWith("zh-hant"))
                return "zh-TW";
            return culture.TwoLetterISOLanguageName.ToLowerInvariant() switch
            {
                "zh" => "zh-CN", "ja" => "ja-JP", "ko" => "ko-KR", "fr" => "fr-FR",
                "de" => "de-DE", "es" => "es-ES", "ru" => "ru-RU", _ => "en-US"
            };
        }
        catch { return "en-US"; }
    }
}

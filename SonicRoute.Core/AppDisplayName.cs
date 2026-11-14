using SonicRoute.Core.Models;

namespace SonicRoute.Core
{
    /// <summary>应用显示名称：优先返回用户自定义名称（按进程名），否则用默认 Label。
    /// 通知/OSD、应用列表、面板/概览统一走这里，保证改名后处处生效。</summary>
    public static class AppDisplayName
    {
        private const string AndroidPrefix = "android:";

        /// <summary>可选外部应用标识解析器（例如 Android 包名 / 设备整机音频标识）。</summary>
        public static Func<string, string?>? ExternalNameResolver { get; set; }

        public static string Get(AudioAppInfo app)
        {
            if (app == null) return "";
            var cfg = ConfigService.Load();
            var pn = app.ProcessName;
            if (!string.IsNullOrWhiteSpace(pn) &&
                cfg.AppNames.TryGetValue(pn!, out var n) && !string.IsNullOrWhiteSpace(n))
                return n;
            if (IsAndroidIdentifier(pn) && string.IsNullOrWhiteSpace(app.DisplayName))
            {
                var resolved = ResolveExternalName(pn!);
                if (!string.IsNullOrWhiteSpace(resolved)) return resolved!;
            }
            return app.Label;
        }

        /// <summary>按进程名返回自定义名；无则返回 fallback（外部 android: 标识优先解析为应用/手机名）。</summary>
        public static string Get(string? processName, string fallback)
        {
            var cfg = ConfigService.Load();
            if (!string.IsNullOrWhiteSpace(processName) &&
                cfg.AppNames.TryGetValue(processName!, out var n) && !string.IsNullOrWhiteSpace(n))
                return n;
            if (IsAndroidIdentifier(processName))
            {
                var resolved = ResolveExternalName(processName!);
                if (!string.IsNullOrWhiteSpace(resolved)) return resolved!;
                if (!string.IsNullOrWhiteSpace(fallback) && !IsAndroidIdentifier(fallback))
                    return fallback;
                return StripAndroidFallback(processName!);
            }
            return fallback;
        }

        /// <summary>将规则或历史步骤中的应用标识格式化为可展示名称；非外部标识原样保留。</summary>
        public static string FormatIdentifier(string? identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier)) return "";
            if (!IsAndroidIdentifier(identifier)) return identifier!;
            return Get(identifier, "");
        }

        private static bool IsAndroidIdentifier(string? value) =>
            !string.IsNullOrWhiteSpace(value) && value!.StartsWith(AndroidPrefix, StringComparison.OrdinalIgnoreCase);

        private static string? ResolveExternalName(string identifier)
        {
            try { return ExternalNameResolver?.Invoke(identifier); }
            catch { return null; }
        }

        private static string StripAndroidFallback(string identifier)
        {
            string suffix = identifier.Substring(AndroidPrefix.Length).Trim();
            if (suffix.Length == 0 || string.Equals(suffix, "sonicroute:device-audio", StringComparison.OrdinalIgnoreCase))
                return "Phone";
            return suffix;
        }
    }
}

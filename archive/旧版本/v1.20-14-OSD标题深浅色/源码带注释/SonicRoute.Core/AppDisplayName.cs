using SonicRoute.Core.Models;

namespace SonicRoute.Core
{
    /// <summary>应用显示名称：优先返回用户自定义名称（按进程名），否则用默认 Label。
    /// 通知/OSD、应用列表、面板/概览统一走这里，保证改名后处处生效。</summary>
    public static class AppDisplayName
    {
        public static string Get(AudioAppInfo app)
        {
            if (app == null) return "";
            var cfg = ConfigService.Load();
            var pn = app.ProcessName;
            if (!string.IsNullOrWhiteSpace(pn) &&
                cfg.AppNames.TryGetValue(pn, out var n) && !string.IsNullOrWhiteSpace(n))
                return n;
            return app.Label;
        }

        /// <summary>按进程名返回自定义名；无则返回 fallback。</summary>
        public static string Get(string? processName, string fallback)
        {
            var cfg = ConfigService.Load();
            if (!string.IsNullOrWhiteSpace(processName) &&
                cfg.AppNames.TryGetValue(processName!, out var n) && !string.IsNullOrWhiteSpace(n))
                return n;
            return fallback;
        }
    }
}

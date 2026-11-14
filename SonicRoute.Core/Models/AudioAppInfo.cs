namespace SonicRoute.Core.Models
{
    /// <summary>当前有音频会话的应用（按 PID 去重）</summary>
    public sealed class AudioAppInfo
    {
        public required uint ProcessId { get; init; }
        /// <summary>External sessions have a source identity and never impersonate a Windows PID.</summary>
        public string? ExternalSourceId { get; init; }
        public string? SourceLabel { get; set; }
        public byte[]? ExternalIconPng { get; init; }
        public bool IsExternal => ExternalSourceId != null;
        public string Identity => ExternalSourceId ?? "windows:" + ProcessId;

        /// <summary>会话显示名（可能为空）</summary>
        public string? DisplayName { get; set; }

        /// <summary>进程名（不含扩展名）</summary>
        public string? ProcessName { get; init; }

        /// <summary>音频会话提供的图标文件路径；不要求调用方能够打开目标进程。</summary>
        public string? IconPath { get; set; }

        /// <summary>是否存在正在播放（Active）的会话，用于排序优先展示正在出声的应用</summary>
        public bool HasActiveSession { get; set; }

        public string Label =>
            !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! :
            !string.IsNullOrWhiteSpace(ProcessName) ? FormatProcessLabel(ProcessName!) :
            $"PID {ProcessId}";

        private static string FormatProcessLabel(string processName)
        {
            const string prefix = "android:";
            if (!processName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return processName;
            string suffix = processName.Substring(prefix.Length).Trim();
            if (suffix.Length == 0 || string.Equals(suffix, "sonicroute:device-audio", StringComparison.OrdinalIgnoreCase))
                return "Phone";
            return suffix;
        }

        public override string ToString() => Label;
    }
}

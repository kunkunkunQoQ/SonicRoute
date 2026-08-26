namespace SonicRoute.Core.Models
{
    /// <summary>当前有音频会话的应用（按 PID 去重）</summary>
    public sealed class AudioAppInfo
    {
        public required uint ProcessId { get; init; }

        /// <summary>会话显示名（可能为空）</summary>
        public string? DisplayName { get; set; }

        /// <summary>进程名（不含扩展名）</summary>
        public string? ProcessName { get; init; }

        /// <summary>是否存在正在播放（Active）的会话，用于排序优先展示正在出声的应用</summary>
        public bool HasActiveSession { get; set; }

        public string Label =>
            !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! :
            !string.IsNullOrWhiteSpace(ProcessName) ? ProcessName! :
            $"PID {ProcessId}";

        public override string ToString() => Label;
    }
}

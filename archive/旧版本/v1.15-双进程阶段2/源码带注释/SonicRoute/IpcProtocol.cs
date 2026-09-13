using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.Core;

namespace SonicRoute
{
    /// <summary>
    /// Named Pipe 消息信封：长度前缀 JSON。
    /// 请求/响应：UI → Backend（请求管道，串行复用）；事件：Backend → UI（事件管道，单向推送）。
    /// </summary>
    public sealed class IpcMessage
    {
        /// <summary>消息类型（请求类型 / 事件类型 / "Resp"）。</summary>
        public string T { get; set; } = "";

        /// <summary>请求 Id（响应回填，用于匹配；事件为 null）。</summary>
        public int? Id { get; set; }

        /// <summary>负载（DTO JSON 元素；无负载为 null）。</summary>
        public JsonElement? P { get; set; }
    }

    // ================= 请求类型（UI → Backend） =================

    public static class IpcReq
    {
        public const string Hello = "Hello";
        public const string QueryState = "QueryState";
        public const string SaveConfig = "SaveConfig";
        public const string ResetConfig = "ResetConfig";
        public const string ExportConfig = "ExportConfig";
        public const string ImportConfig = "ImportConfig";
        public const string ShowOsd = "ShowOsd";
        public const string ShowMicMuteOsd = "ShowMicMuteOsd";
        public const string NotifyMicMuteOsdSettingChanged = "NotifyMicMuteOsdSettingChanged";
        public const string BeginOsdAdjust = "BeginOsdAdjust";
        public const string CancelOsdAdjust = "CancelOsdAdjust";
        public const string PreviewOsd = "PreviewOsd";
        public const string SetOsdSize = "SetOsdSize";
        public const string ApplyOsdSize = "ApplyOsdSize";
        public const string RebuildTrayMenu = "RebuildTrayMenu";
        public const string ReloadHotkeys = "ReloadHotkeys";
        public const string QueryHotkeyRegistration = "QueryHotkeyRegistration";
    }

    // ================= 事件类型（Backend → UI） =================

    public static class IpcEvt
    {
        /// <summary>请求打开完整界面（托盘双击 / 二次启动激活 / 菜单设置）。</summary>
        public const string OpenMainWindow = "OpenMainWindow";
        /// <summary>请求切换快速面板（托盘单击 / 快捷键面板键）。</summary>
        public const string ToggleQuickPanel = "ToggleQuickPanel";
        /// <summary>激活已有窗口（重复启动 UI 时，旧 UI 收到后置顶）。</summary>
        public const string ActivateWindow = "ActivateWindow";
        /// <summary>当前应用变化 / 快捷键操作后刷新（负载 CurrentChangedDto，App 可为 null）。</summary>
        public const string CurrentChanged = "CurrentChanged";
        /// <summary>全局麦克风静音状态变化（负载 MicMuteChangedDto）。</summary>
        public const string MicMuteStateChanged = "MicMuteStateChanged";
        /// <summary>OSD 拖拽保存后通知（设置页复位按钮 / 同步输入框）。</summary>
        public const string OsdAdjustFinished = "OsdAdjustFinished";
        /// <summary>配置已由 Backend 权威写入（负载 ConfigDto）。</summary>
        public const string ConfigChanged = "ConfigChanged";
        /// <summary>Backend 正在退出（托盘退出），UI 应退出（不重连）。</summary>
        public const string Quit = "Quit";
    }

    // ================= 请求/响应 DTO =================

    public sealed class HelloDto
    {
        public string UiId { get; set; } = "";
        /// <summary>替换模式（--restart 重启应用）：通知旧 UI 退出并由新 UI 接管；false = 重复启动，激活旧 UI 后新 UI 退出。</summary>
        public bool Replace { get; set; }
    }
    public sealed class HelloRespDto { public bool ExistsUi { get; set; } }
    public sealed class ShowOsdDto { public string App { get; set; } = ""; public string Text { get; set; } = ""; }
    public sealed class MicMuteDto { public string App { get; set; } = ""; public bool Muted { get; set; } }
    public sealed class BoolDto { public bool Value { get; set; } }
    public sealed class SizeDto { public double W { get; set; } public double Fs { get; set; } }
    public sealed class ConfigDto { public AppConfig Config { get; set; } = new(); }
    public sealed class PathDto { public string Path { get; set; } = ""; }
    public sealed class OkDto { public bool Ok { get; set; } }

    /// <summary>应用信息（跨进程传递当前应用，AudioAppInfo 的轻量 DTO）。</summary>
    public sealed class AppInfoDto
    {
        public int Pid { get; set; }
        public string? ProcessName { get; set; }
        public string? DisplayName { get; set; }
    }

    public sealed class CurrentChangedDto { public AppInfoDto? App { get; set; } }
    public sealed class MicMuteChangedDto { public bool Muted { get; set; } }
    public sealed class HotkeyMapDto { public System.Collections.Generic.Dictionary<string, string> Map { get; set; } = new(); }

    /// <summary>QueryState 响应：UI 重连 / 启动时一次取回完整状态。</summary>
    public sealed class StateRespDto
    {
        public AppConfig Config { get; set; } = new();
        public AppInfoDto? CurrentApp { get; set; }
        public bool MicMuted { get; set; }
    }

    // ================= 传输协议（长度前缀 JSON） =================

    /// <summary>Named Pipe 编码/解码：每条消息 = [4 字节小端长度][UTF-8 JSON]。管道为字节模式。</summary>
    public static class IpcProtocol
    {
        public const string ReqPipe = "SonicRoute.Ipc.Req";
        public const string EvtPipe = "SonicRoute.Ipc.Evt";
        public const string EvtHello = "EvtHello";
        public const string Resp = "Resp";

        public static JsonElement? ToElement(object? payload) =>
            payload == null ? null : JsonSerializer.SerializeToElement(payload, payload.GetType());

        public static T? Get<T>(JsonElement? el) =>
            el == null ? default : el.Value.Deserialize<T>();

        public static IpcMessage RespMsg(int? id, object? payload) =>
            new() { T = Resp, Id = id, P = ToElement(payload) };

        public static IpcMessage EventMsg(string type, object? payload) =>
            new() { T = type, P = ToElement(payload) };

        public static async Task<IpcMessage?> ReadAsync(Stream s, CancellationToken ct)
        {
            var head = new byte[4];
            if (await ReadFullAsync(s, head, 4, ct).ConfigureAwait(false) < 4) return null;
            int len = BitConverter.ToInt32(head, 0);
            if (len <= 0 || len > 16 * 1024 * 1024) return null;
            var body = new byte[len];
            if (await ReadFullAsync(s, body, len, ct).ConfigureAwait(false) < len) return null;
            try { return JsonSerializer.Deserialize<IpcMessage>(body); }
            catch { return null; }
        }

        public static async Task WriteAsync(Stream s, IpcMessage m)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(m);
            var head = BitConverter.GetBytes(body.Length);
            await s.WriteAsync(head, 0, 4, CancellationToken.None).ConfigureAwait(false);
            await s.WriteAsync(body, 0, body.Length, CancellationToken.None).ConfigureAwait(false);
        }

        private static async Task<int> ReadFullAsync(Stream s, byte[] buf, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await s.ReadAsync(buf, total, count - total, ct).ConfigureAwait(false);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }
}

using SonicRoute.Core.Interop;

namespace SonicRoute.Core.Models
{
    /// <summary>音频设备（播放/录音）</summary>
    public sealed class AudioDeviceInfo
    {
        /// <summary>IMMDevice.GetId() 返回的完整设备接口路径</summary>
        public required string Id { get; init; }

        /// <summary>友好名称（PKEY_Device_FriendlyName）</summary>
        public string? DisplayName { get; init; }

        public EDataFlow Flow { get; init; }

        /// <summary>是否为系统默认设备（仅用于显示）</summary>
        public bool IsDefault { get; set; }

        public string DisplayLabel
        {
            get
            {
                var icon = Flow == EDataFlow.eRender ? "🎧 " : "🎤 ";
                var def = IsDefault ? "（默认）" : "";
                return icon + (string.IsNullOrWhiteSpace(DisplayName) ? "(未知设备)" : DisplayName) + def;
            }
        }

        public override string ToString() => DisplayLabel;
    }
}

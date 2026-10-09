using System;
using System.Runtime.InteropServices;

namespace SonicRoute.Core.Compat
{
    /// <summary>
    /// Windows 版本检测（取**真实**内核 Build 号），供 net8 与 net48 共用。
    ///
    /// 为什么不能直接用 Environment.OSVersion.Version.Build：
    ///   .NET Framework 4.8 的 Environment.OSVersion 走 GetVersionEx，**受 app.manifest 的
    ///   &lt;supportedOS&gt; 声明限制**——若未声明 Windows 10，在 Win10/Win11 上会返回兼容性版本
    ///   （6.2 / Build 9200），导致按应用音频路由的 Win11 分支判断失败（静默失效）。
    ///   .NET 8 内部已改用 RtlGetVersion，不受 manifest 影响。
    ///   这里统一显式调用 RtlGetVersion，使两个 TFM 的判断结果完全一致，且不再依赖 manifest。
    ///
    /// 注意：本类只提供"真实 Build 号"，不改变任何上层判断阈值
    /// （例如 AudioPolicyConfig 的 Win11 判定阈值 22000 保持不变）。
    /// </summary>
    public static class WindowsVersion
    {
        // RTL_OSVERSIONINFOEXW（winternl.h）：5 个 ULONG + WCHAR[128]
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RTL_OSVERSIONINFOEXW
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEXW lpVersionInformation);

        private static readonly int _build = DetectBuild();

        private static int DetectBuild()
        {
            try
            {
                var vi = new RTL_OSVERSIONINFOEXW
                {
                    dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(RTL_OSVERSIONINFOEXW))
                };
                // STATUS_SUCCESS == 0
                if (RtlGetVersion(ref vi) == 0 && vi.dwBuildNumber > 0)
                    return (int)vi.dwBuildNumber;
            }
            catch
            {
                // ntdll 不可用等异常情况：退回 BCL
            }
            try { return Environment.OSVersion.Version.Build; }
            catch { return 0; }
        }

        /// <summary>真实 Windows Build 号（Win10 19045 / Win11 22621 等）；取不到返回 0。</summary>
        public static int Build => _build;

        /// <summary>是否为 Windows 11（Build ≥ 22000）。</summary>
        public static bool IsWindows11 => _build >= 22000;
    }
}

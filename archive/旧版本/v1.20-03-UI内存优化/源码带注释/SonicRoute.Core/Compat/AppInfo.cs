using System;
using System.Diagnostics;

namespace SonicRoute.Core.Compat
{
    /// <summary>
    /// 进程 / 路径信息的兼容封装，供 net8 与 net48 共用。
    ///
    /// 原因：Environment.ProcessPath（.NET 6+）与 Environment.ProcessId（.NET 5+）
    /// 在 .NET Framework 4.8 上不存在。
    ///
    /// 语义保持一致：
    ///   - net8  分支直接转发 BCL（与改造前逐值一致）；
    ///   - net48 分支用 Process.GetCurrentProcess()（.NET Framework 原生 API）。
    /// 两者在启动时各取一次并缓存——可执行文件路径与进程 ID 在进程生命周期内不会变化，
    /// 缓存不改变任何行为，同时避免在高频路径上反复创建 Process 对象。
    /// </summary>
    public static class AppInfo
    {
        private static readonly int _currentProcessId = DetectCurrentProcessId();
        private static readonly string? _executablePath = DetectExecutablePath();

        private static int DetectCurrentProcessId()
        {
#if NET48
            try
            {
                using var p = Process.GetCurrentProcess();
                return p.Id;
            }
            catch
            {
                return 0;
            }
#else
            return Environment.ProcessId;
#endif
        }

        private static string? DetectExecutablePath()
        {
#if NET48
            try
            {
                using var p = Process.GetCurrentProcess();
                return p.MainModule?.FileName;
            }
            catch
            {
                // 极端情况下（权限/沙箱）读不到主模块：返回 null，调用方按"取不到"处理
                return null;
            }
#else
            return Environment.ProcessPath;
#endif
        }

        /// <summary>当前进程 ID（取不到返回 0）。</summary>
        public static int CurrentProcessId => _currentProcessId;

        /// <summary>当前可执行文件完整路径（取不到返回 null）。</summary>
        public static string? ExecutablePath => _executablePath;
    }
}

using System;
using System.Diagnostics;

namespace SonicRoute.Core.Compat
{
    /// <summary>
    /// 环境 API 的最小兼容垫片。
    ///
    /// 原因：Environment.TickCount64 是 .NET Core 3.0+ 引入的 API，
    /// .NET Framework 4.8 只有 32 位的 Environment.TickCount（约 49.7 天回绕），
    /// 而 Core 的 AudioService 设备缓存 TTL 需要一个不会回绕的 64 位毫秒计数。
    ///
    /// 语义：单调递增的毫秒计数。起点无意义——调用方只做「当前值 − 之前值」的差值比较，
    /// 因此两种实现（自开机起毫秒 / 自进程启动起毫秒）在调用点行为完全等价。
    ///
    /// 注意：本垫片只保证"与原 Environment.TickCount64 相同的单位（毫秒）与单调性"，
    /// 不改变任何 TTL 判定逻辑与数值。
    /// </summary>
    internal static class CompatEnv
    {
#if NET48
        // Stopwatch.Frequency 在 Windows 上为 10,000,000（100ns 精度）→ 除以 1000 得每毫秒的 tick 数。
        private static readonly long _ticksPerMillisecond = Stopwatch.Frequency / 1000L;

        /// <summary>单调递增的毫秒计数（net48 实现：Stopwatch 高精度计数器换算）。</summary>
        public static long TickCount64 => _ticksPerMillisecond > 0
            ? Stopwatch.GetTimestamp() / _ticksPerMillisecond
            : Environment.TickCount; // 极端兜底：频率异常低时退回 32 位计数（Windows 上不会走到）
#else
        /// <summary>单调递增的毫秒计数（net8 实现：直接转发 BCL，行为与改造前完全一致）。</summary>
        public static long TickCount64 => Environment.TickCount64;
#endif
    }
}
